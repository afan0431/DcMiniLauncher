using System.IO;
using System.IO.Pipes;
using System.Text;
using Serilog;

namespace XIVLauncher.InGame;

/// <summary>
///     和游戏进程里的 <c>MiniLauncherModule.dll</c> 通话。
///     管道名按 PID 分开（<c>\\.\pipe\minilauncher-&lt;pid&gt;</c>）—— 多开时每个客户端各一条, 命令不会串。
///     方向是「启动器命令模块」: 在线那半（下单 / 轮询 / RefreshSID）留在启动器这边, native 只被命令。
/// </summary>
public sealed class MiniModuleClient(int gameProcessId) : IDisposable
{
    private NamedPipeClientStream? stream;

    public static string PipeName(int gameProcessId) => $"minilauncher-{gameProcessId}";

    public async Task ConnectAsync(int timeoutMs, CancellationToken cancellationToken)
    {
        var pipe = new NamedPipeClientStream(".", PipeName(gameProcessId), PipeDirection.InOut, PipeOptions.Asynchronous);

        await pipe.ConnectAsync(timeoutMs, cancellationToken).ConfigureAwait(false);

        // 服务端是消息模式, 客户端也要按消息读, 否则一次 Read 可能只拿到半条
        pipe.ReadMode = PipeTransmissionMode.Message;

        this.stream = pipe;
    }

    /// <summary>发一条命令并等回应。回应形如 <c>OK …</c> / <c>FAIL …</c>, 原样返回给调用方判读。</summary>
    public async Task<string> SendAsync(string command, CancellationToken cancellationToken)
    {
        if (this.stream is not { IsConnected: true })
            throw new InvalidOperationException("还没连上模块");

        var payload = Encoding.UTF8.GetBytes(command);
        await this.stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await this.stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        // 管道是消息模式: 缓冲区比整条回应小会直接读失败, 所以留够（PROBE/DUMP 的回应上千字节）
        var buffer = new byte[8192];
        var read   = await this.stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

        if (read <= 0)
            throw new EndOfStreamException("模块断开了管道");

        var response = Encoding.UTF8.GetString(buffer, 0, read);
        Log.Debug("[MiniModule] {Command} → {Response}", command, response);

        return response;
    }

    public void Dispose()
    {
        this.stream?.Dispose();
        this.stream = null;
    }
}
