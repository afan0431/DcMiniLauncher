using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Serilog;

namespace XIVLauncher.CatHost;

/// <summary>
///     处理握手之后的 JSON-RPC 请求
/// </summary>
public interface ICatRpcHandler
{
    /// <summary>
    ///     处理一个请求, 返回值作为 result; 抛 <see cref="CatRpcException" /> 作为 error 回给调用方
    /// </summary>
    Task<object?> HandleAsync(string method, JsonElement parameters, CancellationToken cancellationToken);
}

/// <summary>
///     dml-cat/1 命名管道服务端: 一行一帧的 JSON-RPC 2.0, 第一帧必须是带正确令牌的 hello;
///     同一时刻只有一个连接, 断开后可用同一令牌重连; 没有连接时事件先缓存, 重连握手后补发。
///     缓存满时先丢最旧的 launcher.log, 没有日志可丢才丢最旧的其它事件。
/// </summary>
public sealed class CatRpcServer : IDisposable
{
    /// <summary>断线期间最多缓存的事件数</summary>
    public const int MAX_BUFFERED_EVENTS = 512;

    private const string LOG_EVENT_METHOD = "launcher.log";

    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(15);

    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private readonly string         pipeName;
    private readonly byte[]         tokenHash;
    private readonly ICatRpcHandler handler;
    private readonly string         launcherVersion;

    private readonly SemaphoreSlim  writeLock      = new(1, 1);
    private readonly LinkedList<(string Method, byte[] Frame)> bufferedEvents = new();
    private readonly object         stateLock      = new();

    private Stream?                 activeStream;
    private int                     pendingResponses;
    private TaskCompletionSource    clientReady = NewSignal();
    private NamedPipeServerStream?  pipe;

    /// <summary>
    ///     创建服务端（还不监听）
    /// </summary>
    public CatRpcServer(string pipeName, string token, ICatRpcHandler handler, string launcherVersion)
    {
        if (!CatProtocol.IsValidPipeName(pipeName) && !CatProtocol.IsValidUiGuardPipeName(pipeName))
            throw new ArgumentException("管道名无效", nameof(pipeName));

        if (!CatProtocol.IsValidToken(token))
            throw new ArgumentException("令牌长度无效", nameof(token));

        this.pipeName        = pipeName;
        tokenHash            = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        this.handler         = handler;
        this.launcherVersion = launcherVersion;
    }

    /// <summary>当前是否有已握手的连接</summary>
    public bool HasClient
    {
        get
        {
            lock (stateLock)
                return activeStream != null;
        }
    }

    /// <summary>尚未送出的缓存事件数</summary>
    public int BufferedEventCount
    {
        get
        {
            lock (stateLock)
                return bufferedEvents.Count;
        }
    }

    /// <summary>
    ///     创建只允许当前用户访问、且必须是第一个实例的管道
    /// </summary>
    public static NamedPipeServerStream CreatePipe(string name)
    {
        var user = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("当前用户不可用");
        var acl  = new PipeSecurity();
        acl.SetOwner(user);
        acl.SetAccessRuleProtection(true, false);
        acl.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create
        (
            name,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough | PipeOptions.FirstPipeInstance,
            4096,
            4096,
            acl
        );
    }

    /// <summary>
    ///     创建管道实例。与 <see cref="RunAsync" /> 分开, 好让调用方在管道就绪后再继续（管道名被占用时这里就抛异常）。
    /// </summary>
    public void Listen() =>
        pipe ??= CreatePipe(pipeName);

    /// <summary>
    ///     接受连接直到取消; 每次断开后在同一个管道实例上等下一个连接
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        Listen();

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await pipe!.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException ex)
            {
                Log.Warning(ex, "[CatHost] 等待管道连接失败");
                ResetPipeConnection();
                continue;
            }

            try
            {
                await ServeConnectionAsync(pipe, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            {
                Log.Debug(ex, "[CatHost] 管道连接结束");
            }
            finally
            {
                ResetPipeConnection();
            }
        }
    }

    /// <summary>
    ///     在一个已连接的流上处理握手与请求, 直到对方断开
    /// </summary>
    public async Task ServeConnectionAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Utf8NoBom, false, 4096, true);

        string? firstLine;

        using (var handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            handshakeTimeout.CancelAfter(HandshakeTimeout);

            try
            {
                firstLine = await reader.ReadLineAsync(handshakeTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Log.Warning("[CatHost] 握手超时, 断开连接");
                return;
            }
        }

        if (firstLine == null || !TryAcceptHello(firstLine, out var helloId))
        {
            Log.Warning("[CatHost] 第一帧不是有效的 hello, 断开连接");
            return;
        }

        await WriteFrameAsync(stream, BuildResult(helloId, new CatHelloResult(CatProtocol.PROTOCOL_VERSION, launcherVersion)), cancellationToken)
            .ConfigureAwait(false);

        await FlushBufferedEventsAndActivateAsync(stream, cancellationToken).ConfigureAwait(false);

        Log.Information("[CatHost] 外壳已连接并完成握手");

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);

                if (line == null)
                    break;

                if (string.IsNullOrWhiteSpace(line))
                    continue;

                // 回复写出前计数, 退出前等它归零（如 close 触发退出时, close 的回复要先送到）
                Interlocked.Increment(ref pendingResponses);
                _ = Task.Run(() => DispatchCountedAsync(stream, line, cancellationToken), CancellationToken.None);
            }
        }
        finally
        {
            DeactivateSession(stream);
            Log.Information("[CatHost] 外壳连接已断开");
        }
    }

    /// <summary>
    ///     发一个事件; 没有连接时缓存, 握手后补发
    /// </summary>
    public async Task NotifyAsync(string method, object parameters)
    {
        var frame = JsonSerializer.SerializeToUtf8Bytes
        (
            new Dictionary<string, object> { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters },
            CatProtocol.JsonOptions
        );

        Stream? stream;

        lock (stateLock)
        {
            stream = activeStream;

            if (stream == null)
            {
                Buffer(method, frame);
                return;
            }
        }

        try
        {
            await WriteFrameAsync(stream, frame, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            lock (stateLock)
                Buffer(method, frame);

            DeactivateSession(stream);
        }
    }

    /// <summary>
    ///     等到有已握手的连接、缓存事件都已送出且已收到的请求都已回复, 或超时
    /// </summary>
    public async Task<bool> WaitForDeliveryAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (HasClient && BufferedEventCount == 0 && Volatile.Read(ref pendingResponses) == 0)
            {
                // 等正在写的帧写完
                await writeLock.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                writeLock.Release();
                return true;
            }

            Task ready;

            lock (stateLock)
                ready = clientReady.Task;

            await Task.WhenAny(ready, Task.Delay(100)).ConfigureAwait(false);
        }

        return HasClient && BufferedEventCount == 0 && Volatile.Read(ref pendingResponses) == 0;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            pipe?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[CatHost] 关闭管道失败");
        }
    }

    private bool TryAcceptHello(string line, out JsonElement id)
    {
        id = default;

        try
        {
            using var document = JsonDocument.Parse(line);
            var       root     = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object                                                  ||
                !root.TryGetProperty("jsonrpc", out var version) || version.GetString() != "2.0"         ||
                !root.TryGetProperty("method",  out var method)  || method.GetString() != "hello"        ||
                !root.TryGetProperty("id",      out var requestId)                                       ||
                requestId.ValueKind is not (JsonValueKind.String or JsonValueKind.Number)                ||
                !root.TryGetProperty("params", out var parameters))
                return false;

            var hello = parameters.Deserialize<CatHelloParams>(CatProtocol.JsonOptions);

            if (hello?.Token == null || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(hello.Token)), tokenHash))
                return false;

            id = requestId.Clone();
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    private async Task DispatchCountedAsync(Stream stream, string line, CancellationToken cancellationToken)
    {
        try
        {
            await DispatchAsync(stream, line, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref pendingResponses);
        }
    }

    private async Task DispatchAsync(Stream stream, string line, CancellationToken cancellationToken)
    {
        JsonElement id;
        string      method;
        JsonElement parameters;

        try
        {
            using var document = JsonDocument.Parse(line);
            var       root     = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("method", out var methodElement) || methodElement.ValueKind != JsonValueKind.String)
                return;

            // 没有 id 的是通知, 不需要回复
            if (!root.TryGetProperty("id", out var idElement) || idElement.ValueKind is not (JsonValueKind.String or JsonValueKind.Number))
                return;

            id         = idElement.Clone();
            method     = methodElement.GetString()!;
            parameters = root.TryGetProperty("params", out var p) ? p.Clone() : JsonDocument.Parse("{}").RootElement.Clone();
        }
        catch (JsonException)
        {
            await TryWriteAsync(stream, BuildError(null, -32700, "无法解析的 JSON"), cancellationToken).ConfigureAwait(false);
            return;
        }

        byte[] response;

        try
        {
            var result = method == "hello"
                             ? new CatHelloResult(CatProtocol.PROTOCOL_VERSION, launcherVersion)
                             : await handler.HandleAsync(method, parameters, cancellationToken).ConfigureAwait(false);
            response = BuildResult(id, result);
        }
        catch (CatRpcException ex)
        {
            response = BuildError(id, ex.Code, ex.Message);
        }
        catch (JsonException ex)
        {
            response = BuildError(id, CatRpcException.INVALID_PARAMS, $"参数无效: {ex.Message}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[CatHost] 处理 {Method} 时出错", method);
            response = BuildError(id, CatRpcException.INTERNAL_ERROR, ex.Message);
        }

        await TryWriteAsync(stream, response, cancellationToken).ConfigureAwait(false);
    }

    private async Task TryWriteAsync(Stream stream, byte[] frame, CancellationToken cancellationToken)
    {
        try
        {
            await WriteFrameAsync(stream, frame, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
            Log.Debug(ex, "[CatHost] 回复写入失败");
        }
    }

    private async Task WriteFrameAsync(Stream stream, byte[] frame, CancellationToken cancellationToken)
    {
        await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writeLock.Release();
        }
    }

    private async Task FlushBufferedEventsAndActivateAsync(Stream stream, CancellationToken cancellationToken)
    {
        // 补发缓存事件期间新事件继续进缓存, 缓存清空的同一把锁里才切成直写, 保证事件顺序
        while (true)
        {
            byte[] frame;

            lock (stateLock)
            {
                if (bufferedEvents.Count == 0)
                {
                    activeStream = stream;
                    clientReady.TrySetResult();
                    return;
                }

                frame = bufferedEvents.First!.Value.Frame;
            }

            await WriteFrameAsync(stream, frame, cancellationToken).ConfigureAwait(false);

            lock (stateLock)
            {
                if (bufferedEvents.First is { } first && ReferenceEquals(first.Value.Frame, frame))
                    bufferedEvents.RemoveFirst();
            }
        }
    }

    private void DeactivateSession(Stream stream)
    {
        lock (stateLock)
        {
            if (!ReferenceEquals(activeStream, stream))
                return;

            activeStream = null;
            clientReady  = NewSignal();
        }
    }

    private void Buffer(string method, byte[] frame)
    {
        bufferedEvents.AddLast((method, frame));

        while (bufferedEvents.Count > MAX_BUFFERED_EVENTS)
        {
            var victim = bufferedEvents.First;

            for (var node = bufferedEvents.First; node != null; node = node.Next)
            {
                if (node.Value.Method == LOG_EVENT_METHOD)
                {
                    victim = node;
                    break;
                }
            }

            bufferedEvents.Remove(victim!);
        }
    }

    private void ResetPipeConnection()
    {
        try
        {
            if (pipe is { IsConnected: true })
                pipe.Disconnect();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[CatHost] 断开管道失败");
        }
    }

    private static byte[] BuildResult(JsonElement id, object? result) =>
        JsonSerializer.SerializeToUtf8Bytes
        (
            new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result },
            CatProtocol.JsonOptions
        );

    private static byte[] BuildError(JsonElement? id, int code, string message) =>
        JsonSerializer.SerializeToUtf8Bytes
        (
            new Dictionary<string, object?>
            {
                ["jsonrpc"] = "2.0",
                ["id"]      = id,
                ["error"]   = new Dictionary<string, object> { ["code"] = code, ["message"] = message }
            },
            new JsonSerializerOptions(CatProtocol.JsonOptions) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never }
        );

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
