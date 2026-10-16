using System.Text;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using Serilog;

namespace XIVLauncher.DCTravel;

/// <summary>
///     本机接口 <c>/cat/…</c> 一次处理的结果: 状态码与 JSON 体（null = 没有体, 如 204）
/// </summary>
public sealed record CatLocalResponse(int StatusCode, string? Json = null);

/// <summary>
///     本机接口 <c>/cat/job</c>、<c>/cat/progress</c> 的处理方（Cat 上号自动开工, 约定 cat-job/1）。
///     监听器只负责收发和拒绝跨域请求, 判断全在实现里（无界面启动器的任务单通道）
/// </summary>
public interface ICatLocalEndpoints
{
    /// <summary>
    ///     <c>GET /cat/job?pid=</c>: pid = 查询串里的游戏进程号, 不是整数时为 null
    /// </summary>
    CatLocalResponse GetJob(int? pid);

    /// <summary>
    ///     <c>POST /cat/progress</c>: body = 请求体原文（cat-progress/1）
    /// </summary>
    CatLocalResponse PostProgress(string body);
}

/// <summary>
///     <c>/cat</c> 下的两条路由。与 <c>/dctravel/ingame-travel</c> 同一信任级别: 只绑 127.0.0.1, 带 <c>Origin</c> 头的请求一律 403;
///     另外 <c>Host</c> 头只认 <c>127.0.0.1:&lt;端口&gt;</c>（挡住借 DNS 重绑定从网页打进来的请求）
/// </summary>
internal sealed class CatLocalController(Func<ICatLocalEndpoints?> endpoints, int port) : WebApiController
{
    /// <summary>回报请求体上限</summary>
    public const int MAX_BODY_BYTES = 64 * 1024;

    [Route(HttpVerbs.Get, "/job")]
    public async Task GetJob()
    {
        if (await RejectCrossOriginAsync().ConfigureAwait(false))
            return;

        var handler = endpoints();

        if (handler == null)
        {
            await WriteAsync(new CatLocalResponse(404, CatLocalJson.Error("本启动器没有启用任务单"))).ConfigureAwait(false);
            return;
        }

        int? pid = int.TryParse(Request.QueryString["pid"], out var parsed) ? parsed : null;
        await WriteAsync(Handle(() => handler.GetJob(pid))).ConfigureAwait(false);
    }

    [Route(HttpVerbs.Post, "/progress")]
    public async Task PostProgress()
    {
        if (await RejectCrossOriginAsync().ConfigureAwait(false))
            return;

        var handler = endpoints();

        if (handler == null)
        {
            await WriteAsync(new CatLocalResponse(404, CatLocalJson.Error("本启动器没有启用任务单"))).ConfigureAwait(false);
            return;
        }

        var body = await ReadBodyAsync().ConfigureAwait(false);

        if (body == null)
        {
            await WriteAsync(new CatLocalResponse(400, CatLocalJson.Error($"请求体超过 {MAX_BODY_BYTES} 字节"))).ConfigureAwait(false);
            return;
        }

        await WriteAsync(Handle(() => handler.PostProgress(body))).ConfigureAwait(false);
    }

    private static CatLocalResponse Handle(Func<CatLocalResponse> handle)
    {
        try
        {
            return handle();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[CatLocal] 处理本机接口请求出错");
            return new CatLocalResponse(500, CatLocalJson.Error($"处理出错: {ex.Message}"));
        }
    }

    private async Task<bool> RejectCrossOriginAsync()
    {
        var host = Request.Headers["Host"];

        if (string.IsNullOrEmpty(Request.Headers["Origin"]) && string.Equals(host, $"127.0.0.1:{port}", StringComparison.OrdinalIgnoreCase))
            return false;

        Response.StatusCode = 403;
        await Response.OutputStream.WriteAsync("Forbidden"u8.ToArray()).ConfigureAwait(false);
        return true;
    }

    /// <summary>读请求体; 超过上限返回 null</summary>
    private async Task<string?> ReadBodyAsync()
    {
        using var buffer = new MemoryStream();
        var       chunk  = new byte[8192];

        while (true)
        {
            var read = await Request.InputStream.ReadAsync(chunk).ConfigureAwait(false);

            if (read <= 0)
                break;

            if (buffer.Length + read > MAX_BODY_BYTES)
                return null;

            buffer.Write(chunk, 0, read);
        }

        return (Request.ContentEncoding ?? Encoding.UTF8).GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private async Task WriteAsync(CatLocalResponse response)
    {
        Response.StatusCode = response.StatusCode;

        if (response.Json == null)
            return;

        Response.ContentType = "application/json; charset=utf-8";
        await Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(response.Json)).ConfigureAwait(false);
    }
}

/// <summary>本机接口错误体</summary>
public static class CatLocalJson
{
    /// <summary><c>{"error":"…"}</c></summary>
    public static string Error(string message) =>
        System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string> { ["error"] = message });
}

/// <summary>
///     只提供 <c>/cat/…</c> 的独立本机监听（模拟模式用; 真实启动时这两条路由挂在 <see cref="DCTravelListener" /> 上）
/// </summary>
public sealed class CatLocalListener : IDisposable
{
    private readonly CancellationTokenSource    cts       = new();
    private readonly TaskCompletionSource<bool> listening = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private          WebServer?                 server;

    /// <summary>
    ///     创建; 还没开始监听, 调 <see cref="StartAsync" />
    /// </summary>
    /// <param name="port">要绑的端口（127.0.0.1）</param>
    /// <param name="endpoints">处理方</param>
    public CatLocalListener(int port, ICatLocalEndpoints endpoints)
    {
        Port = port;

        var urlPrefix = new UriBuilder(Uri.UriSchemeHttp, "127.0.0.1", port).Uri.ToString().TrimEnd('/');

        server = new WebServer(o => o.WithUrlPrefix(urlPrefix).WithMode(HttpListenerMode.EmbedIO))
            .WithWebApi("/cat", m => m.WithController(() => new CatLocalController(() => endpoints, port)));

        server.StateChanged += (_, e) =>
        {
            if (e.NewState == WebServerState.Listening)
                listening.TrySetResult(true);
        };
    }

    /// <summary>端口</summary>
    public int Port { get; }

    /// <summary>
    ///     开始监听, 等到真正在听或失败（端口被占）; 返回是否在听
    /// </summary>
    public async Task<bool> StartAsync(TimeSpan timeout)
    {
        var current = server ?? throw new ObjectDisposedException(nameof(CatLocalListener));

        _ = Task.Run
        (async () =>
            {
                try
                {
                    await current.RunAsync(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // 停止
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "[CatLocal] 本机监听出错");
                }
                finally
                {
                    listening.TrySetResult(false);
                }
            }
        );

        try
        {
            return await listening.Task.WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        cts.Cancel();
        Interlocked.Exchange(ref server, null)?.Dispose();
        cts.Dispose();
    }
}
