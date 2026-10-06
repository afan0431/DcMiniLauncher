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
public sealed class MiniModuleClient(int gameProcessId) : IMiniModuleChannel, IDisposable
{
    private NamedPipeClientStream? stream;

    /// <summary>自动进入角色（LOBBYSTATE / CHARAS / WHOAMI / SELECTCHARA / ENTERCHARA / DIALOG）要求的最低模块版本</summary>
    public static readonly Version AutoEnterMinimumVersion = new(0, 6, 0);

    public static string PipeName(int gameProcessId) => $"minilauncher-{gameProcessId}";

    /// <summary>
    ///     从 <c>VERSION</c> 的回应（<c>OK version=0.6.0 pid=… log=…</c>）里取模块版本; 版本号后面的后缀（如 <c>-focus</c>）不看。
    ///     回应不是 OK 或没有版本号时返回 false。
    /// </summary>
    public static bool TryParseVersion(string? response, out Version version)
    {
        version = new Version(0, 0, 0);

        if (response == null || !response.StartsWith("OK", StringComparison.Ordinal))
            return false;

        const string KEY = "version=";
        var start = response.IndexOf(KEY, StringComparison.Ordinal);

        if (start < 0)
            return false;

        start += KEY.Length;
        var end = start;

        while (end < response.Length && (char.IsAsciiDigit(response[end]) || response[end] == '.'))
            end++;

        var parts = response[start..end].Split('.', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length is < 2 or > 4 || !parts.All(x => int.TryParse(x, out _)))
            return false;

        version = new Version(int.Parse(parts[0]), int.Parse(parts[1]), parts.Length > 2 ? int.Parse(parts[2]) : 0);
        return true;
    }

    /// <summary>
    ///     注入后的版本握手: 问模块版本, 看够不够 <paramref name="minimum" />。返回 (够不够, 模块版本, 原始回应)。
    /// </summary>
    public static async Task<(bool Ok, Version? Version, string Response)> HandshakeAsync(IMiniModuleChannel module, Version minimum, CancellationToken cancellationToken)
    {
        var response = await module.SendAsync("VERSION", cancellationToken).ConfigureAwait(false);

        return TryParseVersion(response, out var version)
                   ? (version >= minimum, version, response)
                   : (false, null, response);
    }

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

        // ⚠ SETSID 带着登录票据, 日志里只留命令名
        Log.Debug("[MiniModule] {Command} → {Response}", command.StartsWith("SETSID ", StringComparison.Ordinal) ? "SETSID ***" : command, response);

        return response;
    }

    public void Dispose()
    {
        this.stream?.Dispose();
        this.stream = null;
    }
}
