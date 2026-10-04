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
    public const int UDP_PORT = 13451;

    /// <summary>GameStatus.GSRUNNING</summary>
    public const byte STATUS_RUNNING = 5;

    /// <summary>
    ///     GameStatus.GSNONE —— 它的状态机对这个状态直接 return（既不排队也不重启），
    ///     正是「这一行没在跑」该有的样子。
    ///     ⚠ 不要用 GSSTOPPING_ACCOUNT(-6)：那条分支会去 KillProcess + 杀 MinionLauncher_64 与 sdologin。
    /// </summary>
    public const byte STATUS_NONE = 0;

    /// <summary>MinionReceiveFilter 固定的包体长度</summary>
    public const int PACKET_LENGTH = 40;

    private const int UID_LENGTH = 16;

    /// <summary>补报「运行中」前等多久（UDP 不保证送达）</summary>
    private static readonly TimeSpan ResendDelay = TimeSpan.FromSeconds(20);

    /// <summary>同一进程的「停机」在这段时间内只报一次</summary>
    private static readonly TimeSpan StopDedupWindow = TimeSpan.FromMinutes(1);

    /// <summary>
    ///     本次启动我们替哪个账号报过状态 —— 游戏退出时要按同一个账号报「停机」
    /// </summary>
    private static readonly ConcurrentDictionary<int, MinionAccount> ReportedAccounts = [];

    /// <summary>最近报过「停机」的进程号与时间</summary>
    private static readonly ConcurrentDictionary<int, DateTime> RecentlyStopped = [];

    /// <summary>测试用: 代替「MINIONAPP 是否在运行」的判断</summary>
    internal static Func<bool>? IsMinionAppRunningOverride { get; set; }

    /// <summary>测试用: 代替 MINIONAPP 的 UDP 端口</summary>
    internal static int? PortOverride { get; set; }

    public static bool IsMinionAppRunning()
    {
        if (IsMinionAppRunningOverride is { } overrideCheck)
            return overrideCheck();

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
    ///     报一次「运行中」, 并在后台隔一会儿补一次（UDP 不保证送达; 种时间戳是幂等的, 多发无害）。不等补发, 立即返回。
    /// </summary>
    public static void SeedRunningStatus(MinionAccount account, Process gameProcess)
    {
        if (BuildPacket(account.Uid, account.Keycode, (uint)gameProcess.Id, STATUS_RUNNING) is not { } packet)
        {
            Log.Warning("[Minion] 账号 UID 不是 32 位十六进制({Uid}), 无法给 MINIONAPP 报状态", account.Uid);
            return;
        }

        var pid = gameProcess.Id;
        ReportedAccounts[pid] = account;
        RecentlyStopped.TryRemove(pid, out _);
        Send(packet, account.Label, pid, "运行中");

        _ = Task.Run
        (async () =>
            {
                await Task.Delay(ResendDelay).ConfigureAwait(false);

                try
                {
                    if (gameProcess.HasExited)
                        return;
                }
                catch (InvalidOperationException)
                {
                    return;
                }

                Send(packet, account.Label, pid, "运行中");
            }
        );
    }

    /// <summary>
    ///     游戏退出后报「停机」—— 不报的话 MINIONAPP 会把这一行转成「排队开始」并过一分钟自己拉新实例。
    ///     只对本启动器挂过的进程做（本进程报过「运行中」的, 或占用记录里带 Minion 行 UID 的）, 不碰 MINIONAPP 自己管的会话。
    /// </summary>
    /// <param name="gamePid">已退出的游戏进程号</param>
    /// <param name="minionUid">占用记录里的 Minion 行 UID（本进程没报过时用）</param>
    public static void ReportStopped(int gamePid, string? minionUid = null)
    {
        string? uid;
        string? keycode = null;
        string  label;

        if (ReportedAccounts.TryRemove(gamePid, out var account))
        {
            uid     = account.Uid;
            keycode = account.Keycode;
            label   = account.Label;
        }
        else
        {
            uid   = minionUid ?? MinionOccupancy.Read(gamePid)?.MinionUid;
            label = uid is { Length: >= 8 } ? uid[..8] : "?";
        }

        if (string.IsNullOrWhiteSpace(uid))
            return;

        var now = DateTime.UtcNow;

        if (RecentlyStopped.TryGetValue(gamePid, out var last) && now - last < StopDedupWindow)
            return;

        if (!IsMinionAppRunning())
            return;

        // PID 报 0 = 这一行没有对应进程
        if (BuildPacket(uid, keycode, 0, STATUS_NONE) is not { } packet)
            return;

        RecentlyStopped[gamePid] = now;
        Send(packet, label, gamePid, "停机");
    }

    private static void Send(byte[] packet, string label, int gamePid, string what)
    {
        var port = PortOverride ?? UDP_PORT;

        try
        {
            using var client = new UdpClient();
            client.Send(packet, packet.Length, "127.0.0.1", port);

            Log.Information
            (
                "[Minion] 已按 MINIONAPP 协议报「{What}」: 账号={Account}, PID={GamePid} → 127.0.0.1:{Port}",
                what,
                label,
                gamePid,
                port
            );
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Minion] 给 MINIONAPP 报「{What}」失败", what);
        }
    }

    /// <summary>
    ///     包体 40 字节: UuId(16) + KeyMd5(16) + PId(uint32 小端) + Status(1) + 保留(3)
    ///     —— 见 MINIONAPP.Core.Networking 的 MinionPackage.GetStatusMessage / MinionReceiveFilter。
    ///     KeyMd5 那 16 字节 MINIONAPP 收下但从不校验, 有 Keycode 时仍按 bot 的样子填它的 MD5, 没有就全 0。
    ///     UID 不是 32 位十六进制时返回 null。
    /// </summary>
    public static byte[]? BuildPacket(string? minionUid, string? keycode, uint gamePid, byte status)
    {
        if (ParseUid(minionUid) is not { } uid)
            return null;

        var packet = new byte[PACKET_LENGTH];
        uid.CopyTo(packet, 0);

        if (!string.IsNullOrWhiteSpace(keycode))
            MD5.HashData(Encoding.ASCII.GetBytes(keycode)).CopyTo(packet, UID_LENGTH);

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
