using System.Collections.Frozen;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using Serilog;
using XIVLauncher.Common.Http;

namespace XIVLauncher.DCTravel;

public sealed class DCTravelListener : IDisposable, IAsyncDisposable
{
    public DCTravelClient DCTravelClient { get; }

    /// <summary>
    ///     处理 <c>/dctravel/ingame-travel</c> 的钩子（F4）。由启动器装上 ——
    ///     本项目不认识注入模块那一套, 所以行为由上层注入, 监听器只负责收发。
    /// </summary>
    public Func<InGameTravelRequest, CancellationToken, Task<InGameTravelResponse>>? InGameTravelHandler { get; set; }

    /// <summary>
    ///     <c>GET /dctravel/ingame-travel/status</c> 的钩子 —— 游戏内 UI 靠它显示「正在排队/正在登录…」。
    ///     参数是 pid（可空: 不给就返回全部客户端的状态）, 返回值直接序列化成 JSON。
    /// </summary>
    public Func<int?, object>? InGameTravelStatusProvider { get; set; }

    /// <summary>
    ///     <c>GET /dctravel/ingame-travel/areas</c> 的钩子 —— 游戏内 UI 用它问「我这个角色现在什么处境」:
    ///     所在地、原始大区, 以及能去的大区/服务器和拥挤度（queueTime: 0=通畅, &lt;0=繁忙, &gt;0=排队分钟数）。
    ///     超域中（away）/ 跨界传送中（visiting）也照给目的地, 由调用方按这两个标记决定先走哪一步。
    ///     角色是谁、人在哪见 <see cref="InGameTravelIdentity" />: 游戏内由 Lua 报,
    ///     标题/选角界面报不了时启动器改问注入的原生模块。
    /// </summary>
    public Func<InGameTravelIdentity, CancellationToken, Task<object>>? InGameTravelAreasProvider { get; set; }

    /// <summary>
    ///     <c>POST /dctravel/ingame-travel/switch-area</c> 的钩子 —— 换登录大区（标题界面用）。
    ///     和超域旅行是两回事: 不动角色、不下单、无冷却。
    /// </summary>
    public Func<SwitchAreaRequest, CancellationToken, Task<InGameTravelResponse>>? InGameSwitchAreaHandler { get; set; }

    /// <summary>
    ///     <c>GET /dctravel/ingame-travel/login-areas</c> 的钩子 —— 可切换的登录大区列表。
    /// </summary>
    public Func<CancellationToken, Task<object>>? InGameLoginAreasProvider { get; set; }

    /// <summary>
    ///     <c>GET/POST /dctravel/ingame-travel/settings</c> 的钩子 —— 游戏内 UI 抄的是 DcTraveler
    ///     的设置页（自动重试 / 繁忙时自动换服务器 / 最大重试次数 / 重试间隔）。
    ///     入参为 null 表示只读取, 非 null 表示写入后返回最新值。
    /// </summary>
    public Func<string?, object>? InGameTravelSettingsHandler { get; set; }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly CancellationTokenSource              listenerCts = new();
    private readonly FrozenDictionary<string, MethodInfo> rpcMethodCache;
    private readonly byte[]?                              key;
    private readonly byte[]?                              iv;
    private readonly bool                                 useEncrypt;

    private WebServer? webServer;
    private int        stopState;
    private int        disposeState;

    public DCTravelListener(DCTravelClient dcTravelClient, int port, bool useEncrypt = true)
    {
        ArgumentNullException.ThrowIfNull(dcTravelClient);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(port);
        if (port > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(port), "port 必须在 1-65535 范围内");

        DCTravelClient  = dcTravelClient;
        this.useEncrypt = useEncrypt;
        if (useEncrypt)
            (key, iv) = GenerateAesKeyIv();
        rpcMethodCache = BuildRpcMethodCache();

        var urlPrefix = new UriBuilder(Uri.UriSchemeHttp, "127.0.0.1", port).Uri.ToString().TrimEnd('/');

        webServer = new WebServer
            (o => o
                  .WithUrlPrefix(urlPrefix)
                  .WithMode(HttpListenerMode.EmbedIO)
            )
            .WithWebApi("/dctravel", m => m.WithController(() => new RpcController(this)));
    }

    #region Disposal

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposeState, 1) != 0)
            return;

        Stop();
        listenerCts.Dispose();
    }

    #endregion

    public void Stop()
    {
        if (Interlocked.Exchange(ref stopState, 1) != 0)
            return;

        listenerCts.Cancel();

        try
        {
            DCTravelClient.Logout().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[DCTravelListener] 登出失败");
        }

        DCTravelClient.EndSession();
        Interlocked.Exchange(ref webServer, null)?.Dispose();
    }

    public async Task StartAsync()
    {
        try
        {
            ThrowIfDisposed();
            var server = webServer ?? throw new ObjectDisposedException(nameof(DCTravelListener));
            await server.RunAsync(listenerCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Log.Information("[DCTravelListener] 已取消");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DCTravelListener] 发生异常");
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    internal string Encrypt(string plainText)
    {
        ArgumentNullException.ThrowIfNull(plainText);
        if (!useEncrypt)
            return plainText;

        using var aes = Aes.Create();
        aes.Key = key ?? throw new InvalidOperationException("加密密钥未初始化");
        aes.IV  = iv  ?? throw new InvalidOperationException("加密 IV 未初始化");

        using var encryptor      = aes.CreateEncryptor();
        var       plainBytes     = Encoding.UTF8.GetBytes(plainText);
        var       encryptedBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);
        return Convert.ToBase64String(encryptedBytes);
    }

    internal string Decrypt(string cipherText)
    {
        ArgumentNullException.ThrowIfNull(cipherText);
        if (!useEncrypt)
            return cipherText;

        var       buffer = Convert.FromBase64String(cipherText);
        using var aes    = Aes.Create();
        aes.Key = key ?? throw new InvalidOperationException("加密密钥未初始化");
        aes.IV  = iv  ?? throw new InvalidOperationException("加密 IV 未初始化");

        using var decryptor      = aes.CreateDecryptor();
        var       decryptedBytes = decryptor.TransformFinalBlock(buffer, 0, buffer.Length);
        return Encoding.UTF8.GetString(decryptedBytes);
    }

    private static (byte[] key, byte[] iv) GenerateAesKeyIv()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var iv  = RandomNumberGenerator.GetBytes(16);
        return (key, iv);
    }

    private static FrozenDictionary<string, MethodInfo> BuildRpcMethodCache()
    {
        var methods = typeof(DCTravelClient)
                      .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                      .Where(m => m.GetCustomAttribute<HttpRpcAttribute>() != null);

        var result = new Dictionary<string, MethodInfo>(StringComparer.Ordinal);

        foreach (var method in methods)
        {
            if (!result.TryAdd(method.Name, method))
                throw new InvalidOperationException($"重复的 RPC 方法名: {method.Name}");
        }

        return result.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private static object?[] BindParameters(ParameterInfo[] parameters, object?[] callArguments)
    {
        if (parameters.Length != callArguments.Length)
            throw new InvalidOperationException("参数数量不匹配");

        if (parameters.Length == 0)
            return [];

        var result = new object?[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
            result[i] = ConvertArgument(callArguments[i], parameters[i].ParameterType);
        return result;
    }

    private static object? ConvertArgument(object? value, Type targetType)
    {
        if (value is JsonElement jsonElement)
            return jsonElement.Deserialize(targetType, SerializerOptions);

        var nonNullableType = Nullable.GetUnderlyingType(targetType) ?? targetType;

        if (value is null)
        {
            if (nonNullableType.IsValueType && Nullable.GetUnderlyingType(targetType) is null)
                throw new InvalidOperationException($"参数类型 '{targetType}' 不接受 null");
            return null;
        }

        if (nonNullableType.IsInstanceOfType(value))
            return value;

        if (nonNullableType.IsEnum)
        {
            if (value is string stringValue)
                return Enum.Parse(nonNullableType, stringValue, true);
            var enumBaseType = Enum.GetUnderlyingType(nonNullableType);
            var enumRawValue = Convert.ChangeType(value, enumBaseType);
            return Enum.ToObject(nonNullableType, enumRawValue);
        }

        return Convert.ChangeType(value, nonNullableType);
    }

    private static Exception UnwrapException(Exception exception) =>
        exception is TargetInvocationException { InnerException: not null } tie
            ? tie.InnerException
            : exception;

    private async Task<object?> InvokeRpcMethodAsync(MethodInfo method, object?[] callParams)
    {
        object? invoked;

        try
        {
            invoked = method.Invoke(DCTravelClient, callParams);
        }
        catch (Exception ex)
        {
            throw UnwrapException(ex);
        }

        if (invoked is not Task task)
            return invoked;

        await task.ConfigureAwait(false);
        var returnType = method.ReturnType;
        if (!returnType.IsGenericType || returnType.GetGenericTypeDefinition() != typeof(Task<>))
            return null;

        var resultProperty = returnType.GetProperty("Result", BindingFlags.Public | BindingFlags.Instance)
                             ?? throw new InvalidOperationException($"无法读取方法 '{method.Name}' 的 Task 结果");
        return resultProperty.GetValue(task);
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref disposeState) != 0)
            throw new ObjectDisposedException(nameof(DCTravelListener));
    }

    public sealed class RpcRequest
    {
        public string    Method { get; set; } = string.Empty;
        public object?[] Params { get; set; } = [];
    }

    /// <summary>
    ///     游戏内换大区请求（F4）。给 bot（Afan/Minion 的 Lua 只会发普通 HTTP）用：
    ///     <c>POST http://127.0.0.1:&lt;XL.DcTraveler&gt;/dctravel/ingame-travel</c>
    ///     <code>{"area":"陆行鸟","group":"紫水栈桥","character":"某某","pid":1234}</code>
    ///     除 <see cref="Area" /> 外都可省：<see cref="Group" /> 省了就挑该大区第一个可用服务器，
    ///     <see cref="Character" /> 省了就要求源大区只有一个角色，<see cref="Pid" /> 省了就要求只开着一个客户端。
    /// </summary>
    public sealed class InGameTravelRequest
    {
        public string  Area      { get; set; } = string.Empty;
        public string? Group     { get; set; }
        public string? Character { get; set; }
        public int?    Pid       { get; set; }

        /// <summary>角色当前所在世界名（超域中就是做客地）。游戏内那侧给。</summary>
        public string? World { get; set; }

        /// <summary>角色原始世界名。SDO 的超域业务以它所在的服务器为源。</summary>
        public string? HomeWorld { get; set; }

        /// <summary>
        ///     角色的 ContentId（选角界面右键菜单给的）。<b>字符串</b>: 它有 17 位,
        ///     超出 Lua 数字（double）能精确表示的范围, 当数字传会被改掉末几位。
        /// </summary>
        public string? ContentId { get; set; }

        /// <summary>true = 返回原大区（不需要 area/group，目标记在当初那张跨区订单里）</summary>
        public bool Back { get; set; }

        /// <summary>true = 一直等到换完才回应；默认立刻返回, 进度用 /ingame-travel/status 查</summary>
        public bool Wait { get; set; }

        public InGameTravelIdentity Identity => new(Character, World, HomeWorld, Pid, ContentId);
    }

    /// <summary>
    ///     「这个请求说的是哪个角色、人在哪」。
    ///     <list type="bullet">
    ///       <item>游戏内: Lua 报 <see cref="Character" /> / <see cref="World" /> / <see cref="HomeWorld" />（中文世界名）。</item>
    ///       <item>标题/选角界面: Lua 拿不到 Player, 这三个为空 —— 启动器改问注入的原生模块（WHOLIST）,
    ///             用 <see cref="ContentId" />（右键菜单给的）或当前选中的角色定位。</item>
    ///     </list>
    ///     世界名可以是中文名, 也可以是游戏内部代号（= SDO 的 groupCode, 如 <c>HongChaChuan2</c>）。
    /// </summary>
    public sealed record InGameTravelIdentity
    (
        string? Character,
        string? World,
        string? HomeWorld,
        int?    Pid,
        string? ContentId
    );

    /// <summary>
    ///     换登录大区请求。
    ///     <c>POST http://127.0.0.1:&lt;XL.DcTraveler&gt;/dctravel/ingame-travel/switch-area</c>
    ///     <code>{"area":"陆行鸟","pid":1234}</code>
    /// </summary>
    public sealed class SwitchAreaRequest
    {
        public string Area { get; set; } = string.Empty;
        public int?   Pid  { get; set; }

        /// <summary>true = 一直等到换完才回应；默认立刻返回, 进度用 /ingame-travel/status 查</summary>
        public bool Wait { get; set; }
    }

    public sealed class InGameTravelResponse
    {
        public bool   Ok      { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    public sealed class RpcResponse
    {
        public object? Result { get; set; }
        public string? Error  { get; set; }
    }

    private sealed class RpcController
    (
        DCTravelListener listener
    ) : WebApiController
    {
        [Route(HttpVerbs.Post, "/")]
        public async Task ProcessRequest()
        {
            if (!string.IsNullOrEmpty(Request.Headers["Origin"]))
            {
                Response.StatusCode = 403;
                await Response.OutputStream.WriteAsync("CORS Forbidden"u8.ToArray()).ConfigureAwait(false);
                return;
            }

            try
            {
                // 维护期间直接返回明确错误, 游戏内插件可据此展示维护提示
                if (listener.DCTravelClient.MaintenanceState == DCTravelMaintenanceState.UnderMaintenance)
                    throw new DCTravelAPIException("超域旅行服务维护中, 请稍后再试", -10339180);

                var rpcRequest = await ReadRequestAsync().ConfigureAwait(false);

                if (!listener.rpcMethodCache.TryGetValue(rpcRequest.Method, out var method))
                    throw new InvalidOperationException("未知或未授权的方法");

                var callParams = BindParameters(method.GetParameters(), rpcRequest.Params);
                var result     = await listener.InvokeRpcMethodAsync(method, callParams).ConfigureAwait(false);
                await WriteRpcResponseAsync(new RpcResponse { Result = result }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await WriteRpcResponseAsync(new RpcResponse { Error = UnwrapException(ex).ToString() }).ConfigureAwait(false);
            }
        }

        /// <summary>
        ///     游戏内换大区（F4）。和上面的 RPC 不同, 这条**不是**代理 SDO 接口, 而是让启动器
        ///     去驱动注入到游戏里的 native 模块原地换服 —— 所以它不走 <c>rpcMethodCache</c>,
        ///     也不加密（本监听器整体就是明文绑 127.0.0.1, 见 DCTravelRuntimeService 的构造）。
        ///     整个流程要跑几分钟（排队), 这里会一直等到有结果为止。
        /// </summary>
        [Route(HttpVerbs.Post, "/ingame-travel")]
        public async Task ProcessInGameTravel()
        {
            if (!string.IsNullOrEmpty(Request.Headers["Origin"]))
            {
                Response.StatusCode = 403;
                await Response.OutputStream.WriteAsync("CORS Forbidden"u8.ToArray()).ConfigureAwait(false);
                return;
            }

            InGameTravelResponse response;

            try
            {
                var handler = listener.InGameTravelHandler
                              ?? throw new InvalidOperationException("本启动器未启用游戏内换大区（只在「只 Minion」模式下可用）");

                using var reader = new StreamReader(Request.InputStream, Request.ContentEncoding ?? Encoding.UTF8);
                var       body   = await reader.ReadToEndAsync().ConfigureAwait(false);

                var request = JsonSerializer.Deserialize<InGameTravelRequest>(body, SerializerOptions)
                              ?? throw new InvalidOperationException("无效的请求负载");

                if (string.IsNullOrWhiteSpace(request.Area))
                    throw new InvalidOperationException("缺少目标大区 area");

                Log.Information("[DCTravelListener] 收到游戏内换大区请求: area={Area} group={Group} pid={Pid}",
                                request.Area, request.Group, request.Pid);

                response = await handler(request, listener.listenerCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[DCTravelListener] 游戏内换大区失败");
                response = new InGameTravelResponse { Ok = false, Message = UnwrapException(ex).Message };
            }

            var json = JsonSerializer.Serialize(response, SerializerOptions);
            Response.ContentType = "application/json";
            Response.StatusCode  = response.Ok ? 200 : 500;
            await Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(json)).ConfigureAwait(false);
        }

        /// <summary>游戏内 UI 轮询进度用。<c>?pid=1234</c> 查单个客户端, 不给就返回全部。</summary>
        [Route(HttpVerbs.Get, "/ingame-travel/status")]
        public async Task GetInGameTravelStatus()
        {
            object payload;

            try
            {
                var provider = listener.InGameTravelStatusProvider
                               ?? throw new InvalidOperationException("本启动器未启用游戏内换大区");

                int? pid = int.TryParse(Request.QueryString["pid"], out var parsed) ? parsed : null;
                payload = provider(pid);
            }
            catch (Exception ex)
            {
                payload = new InGameTravelResponse { Ok = false, Message = UnwrapException(ex).Message };
            }

            await WriteJsonAsync(payload).ConfigureAwait(false);
        }

        /// <summary>
        ///     游戏内 UI 问「我现在什么处境」用:
        ///     <c>?character=名字&amp;world=晨曦王座&amp;homeWorld=红茶川&amp;pid=1234</c>（游戏内）,
        ///     或 <c>?pid=1234&amp;contentId=…</c>（选角界面, 角色由原生模块读）。
        ///     角色一旦超域, 登录大区、原始大区、实际所在大区三者互不相同,
        ///     只有游戏进程自己知道在玩谁、人在哪。
        /// </summary>
        [Route(HttpVerbs.Get, "/ingame-travel/areas")]
        public async Task GetInGameTravelAreas()
        {
            object payload;

            try
            {
                var provider = listener.InGameTravelAreasProvider
                               ?? throw new InvalidOperationException("本启动器未启用游戏内换大区");

                static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

                var identity = new InGameTravelIdentity
                (
                    Trim(Request.QueryString["character"]),
                    Trim(Request.QueryString["world"]),
                    Trim(Request.QueryString["homeWorld"]),
                    int.TryParse(Request.QueryString["pid"], out var pid) ? pid : null,
                    Trim(Request.QueryString["contentId"])
                );

                payload = await provider(identity, listener.listenerCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                payload = new InGameTravelResponse { Ok = false, Message = UnwrapException(ex).Message };
            }

            await WriteJsonAsync(payload).ConfigureAwait(false);
        }

        /// <summary>换登录大区 —— 不动角色、不下单、无冷却。</summary>
        [Route(HttpVerbs.Post, "/ingame-travel/switch-area")]
        public async Task PostInGameSwitchArea()
        {
            object payload;

            try
            {
                var handler = listener.InGameSwitchAreaHandler
                              ?? throw new InvalidOperationException("本启动器未启用游戏内换大区");

                using var reader = new StreamReader(Request.InputStream, Request.ContentEncoding ?? Encoding.UTF8);
                var       body   = await reader.ReadToEndAsync().ConfigureAwait(false);

                var request = JsonSerializer.Deserialize<SwitchAreaRequest>(body, SerializerOptions)
                              ?? throw new InvalidOperationException("请求体解析失败");

                payload = await handler(request, listener.listenerCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                payload = new InGameTravelResponse { Ok = false, Message = UnwrapException(ex).Message };
            }

            await WriteJsonAsync(payload).ConfigureAwait(false);
        }

        /// <summary>可切换的登录大区列表。</summary>
        [Route(HttpVerbs.Get, "/ingame-travel/login-areas")]
        public async Task GetInGameLoginAreas()
        {
            object payload;

            try
            {
                var provider = listener.InGameLoginAreasProvider
                               ?? throw new InvalidOperationException("本启动器未启用游戏内换大区");

                payload = await provider(listener.listenerCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                payload = new InGameTravelResponse { Ok = false, Message = UnwrapException(ex).Message };
            }

            await WriteJsonAsync(payload).ConfigureAwait(false);
        }

        /// <summary>读设置</summary>
        [Route(HttpVerbs.Get, "/ingame-travel/settings")]
        public async Task GetInGameTravelSettings() => await HandleSettingsAsync(null).ConfigureAwait(false);

        /// <summary>写设置（整份提交, 字段与读取时一致）</summary>
        [Route(HttpVerbs.Post, "/ingame-travel/settings")]
        public async Task PostInGameTravelSettings()
        {
            using var reader = new StreamReader(Request.InputStream, Request.ContentEncoding ?? Encoding.UTF8);
            var       body   = await reader.ReadToEndAsync().ConfigureAwait(false);

            await HandleSettingsAsync(body).ConfigureAwait(false);
        }

        private async Task HandleSettingsAsync(string? body)
        {
            object payload;

            try
            {
                var handler = listener.InGameTravelSettingsHandler
                              ?? throw new InvalidOperationException("本启动器未启用游戏内换大区");

                payload = handler(body);
            }
            catch (Exception ex)
            {
                payload = new InGameTravelResponse { Ok = false, Message = UnwrapException(ex).Message };
            }

            await WriteJsonAsync(payload).ConfigureAwait(false);
        }

        private async Task WriteJsonAsync(object payload)
        {
            var json = JsonSerializer.Serialize(payload, SerializerOptions);
            Response.ContentType = "application/json";
            Response.StatusCode  = 200;
            await Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(json)).ConfigureAwait(false);
        }

        private async Task<RpcRequest> ReadRequestAsync()
        {
            using var reader = new StreamReader(Request.InputStream, Request.ContentEncoding ?? Encoding.UTF8);
            var       body   = await reader.ReadToEndAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body))
                throw new InvalidOperationException("RPC 请求体为空");

            var plainBody = listener.Decrypt(body);
            var rpcRequest = JsonSerializer.Deserialize<RpcRequest>(plainBody, SerializerOptions)
                             ?? throw new InvalidOperationException("无效的 RPC 请求负载");
            if (string.IsNullOrWhiteSpace(rpcRequest.Method))
                throw new InvalidOperationException("缺少 RPC 方法名");
            rpcRequest.Params ??= [];
            return rpcRequest;
        }

        private async Task WriteRpcResponseAsync(RpcResponse response)
        {
            var responseJson = JsonSerializer.Serialize(response, SerializerOptions);
            if (listener.useEncrypt)
                responseJson = listener.Encrypt(responseJson);

            Response.ContentType = "application/json";
            Response.StatusCode  = 200;
            await Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(responseJson)).ConfigureAwait(false);
        }
    }
}
