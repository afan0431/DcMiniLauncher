using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Serilog;

namespace XIVLauncher.Minion;

/// <summary>
///     按 MINIONAPP 自己的协议，替新挂上的 bot 先报一次「运行中」，好让它别把我们的客户端当成卡死的杀掉。
///     <para>
///         <b>为什么需要（2026-08-15 反编译 MINIONAPP 定案）</b>：
///         bot 在游戏里会往 <c>127.0.0.1:13451</c> 发 UDP 状态包（<c>MinionNetwork</c> 起的 SuperSocket UDP 服务），
///         MINIONAPP 按包里的账号 UID 找到对应行，记下 <c>PID</c> 与 <c>LastStatusUpdate</c>，
///         而 <c>LastRunningUpdate</c> <b>只在状态是「运行」(GSRUNNING=5) 时才更新</b>。
///     </para>
///     <para>
///         它的看门狗（<c>LauncherViewModel</c>）判定：
///         <c>(Now - LastStatusUpdate) &gt; FrozenTime || (Now - LastRunningUpdate) &gt; FrozenTime</c> → <c>KillProcess(PID)</c>，
///         并把 LastError 记成「游戏冻结」、一分钟后重启该账号。
///         MINIONAPP <b>自己</b>拉起 MinionLauncher 时，会在拿到 PID 的同时把 <c>LastRunningUpdate</c> 设成当前时间
///         （给 bot 留出初始化时间）；我们自己挂时没人设它，它就停在 <c>DateTime.MinValue</c> ——
///         于是不管 FrozenTime 设成多大（默认 180 分钟）都必然超时，客户端在 attach 后十几秒被杀。
///     </para>
///     <para>
///         所以这里做的事就是补上它自己那一行：用同一个协议报一次「运行中」，把计时器种上。
///         之后 bot 真的跑起来会自己持续汇报，我们不再插手。
///     </para>
/// </summary>
internal static class MinionAppStatusReporter
{
    public const string PROCESS_NAME = "MINIONAPP";

    /// <summary>MinionNetwork 里写死的监听端口</summary>
    private const int UDP_PORT = 13451;

    /// <summary>GameStatus.GSRUNNING</summary>
    private const byte STATUS_RUNNING = 5;

    /// <summary>
    ///     GameStatus.GSNONE —— 它的状态机对这个状态直接 return（既不排队也不重启），
    ///     正是「这一行没在跑」该有的样子。
    ///     ⚠ 不要用 GSSTOPPING_ACCOUNT(-6)：那条分支会去 KillProcess + 杀 MinionLauncher_64 与 sdologin。
    /// </summary>
    private const byte STATUS_NONE = 0;

    /// <summary>MinionReceiveFilter 固定的包体长度</summary>
    private const int PACKET_LENGTH = 40;

    private const int UID_LENGTH = 16;

    public static bool IsMinionAppRunning()
    {
        var processes = Process.GetProcessesByName(PROCESS_NAME);

        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }
    }

    /// <summary>
    ///     本次启动我们替哪个账号报过状态 —— 游戏退出时要按同一个账号报「停机」
    /// </summary>
    private static readonly ConcurrentDictionary<int, MinionAccount> ReportedAccounts = [];

    /// <summary>
    ///     报一次「运行中」。UDP 不保证送达, 所以隔一会儿补一次（种时间戳是幂等的, 多发无害）。
    /// </summary>
    public static async Task SeedRunningStatusAsync(MinionAccount account, Process gameProcess, CancellationToken cancellationToken)
    {
        if (BuildPacket(account, (uint)gameProcess.Id, STATUS_RUNNING) is not { } packet)
        {
            Log.Warning("[Minion] 账号 UID 不是 32 位十六进制({Uid}), 无法给 MINIONAPP 报状态", account.Uid);
            return;
        }

        ReportedAccounts[gameProcess.Id] = account;
        Send(packet, account, gameProcess.Id, "运行中");

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (gameProcess.HasExited)
            return;

        Send(packet, account, gameProcess.Id, "运行中");
    }

    /// <summary>
    ///     游戏退出后报「停机」—— 不报的话 MINIONAPP 会把这一行转成「排队开始」并过一分钟自己拉新实例。
    ///     只对本启动器报过状态的进程做, 不碰 MINIONAPP 自己管的会话。
    /// </summary>
    public static void ReportStopped(int gamePid)
    {
        if (!ReportedAccounts.TryRemove(gamePid, out var account))
            return;

        if (!IsMinionAppRunning())
            return;

        // PID 报 0 = 这一行没有对应进程
        if (BuildPacket(account, 0, STATUS_NONE) is not { } packet)
            return;

        Send(packet, account, gamePid, "停机");
    }

    private static void Send(byte[] packet, MinionAccount account, int gamePid, string what)
    {
        try
        {
            using var client = new UdpClient();
            client.Send(packet, packet.Length, "127.0.0.1", UDP_PORT);

            Log.Information
            (
                "[Minion] 已按 MINIONAPP 协议报「{What}」: 账号={Account}, PID={GamePid} → 127.0.0.1:{Port}",
                what,
                account.Label,
                gamePid,
                UDP_PORT
            );
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Minion] 给 MINIONAPP 报「{What}」失败", what);
        }
    }

    /// <summary>
    ///     包体 40 字节: UuId(16) + KeyMd5(16) + PId(uint32) + Status(1) + 保留(3)
    ///     —— 见 MINIONAPP.Core.Networking 的 MinionPackage.GetStatusMessage / MinionReceiveFilter。
    ///     KeyMd5 那 16 字节 MINIONAPP 收下但从不校验, 这里仍按 bot 的样子填 Keycode 的 MD5。
    /// </summary>
    private static byte[]? BuildPacket(MinionAccount account, uint gamePid, byte status)
    {
        if (ParseUid(account.Uid) is not { } uid)
            return null;

        var packet = new byte[PACKET_LENGTH];
        uid.CopyTo(packet, 0);

        if (!string.IsNullOrWhiteSpace(account.Keycode))
            MD5.HashData(Encoding.ASCII.GetBytes(account.Keycode)).CopyTo(packet, UID_LENGTH);

        BitConverter.GetBytes(gamePid).CopyTo(packet, 32);
        packet[36] = status;

        return packet;
    }

    /// <summary>
    ///     Accounts.json 里的 UID 就是这 16 字节的十六进制原样打印（MINIONAPP 侧是
    ///     <c>BitConverter.ToString(UuId).Replace("-", "").ToLower()</c> 后与 UID 比较）
    /// </summary>
    private static byte[]? ParseUid(string? uid)
    {
        if (string.IsNullOrWhiteSpace(uid))
            return null;

        var trimmed = uid.Trim();

        if (trimmed.Length != UID_LENGTH * 2)
            return null;

        try
        {
            return Convert.FromHexString(trimmed);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
