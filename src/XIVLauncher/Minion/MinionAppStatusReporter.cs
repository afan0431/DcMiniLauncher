using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Serilog;

namespace XIVLauncher.Minion;

/// <summary>
///     本机开着 MINIONAPP 时, 按它自己的协议替我们挂的 bot 报状态, 免得它的看门狗把客户端当成卡死的杀掉、或在游戏退出后自己重开。
///     <para>
///         bot 在游戏里会往 <c>127.0.0.1:13451</c> 发 UDP 状态包（<c>MinionNetwork</c> 起的 SuperSocket UDP 服务），
///         MINIONAPP 按包里的 UID 找到对应行，记下 <c>PID</c> 与 <c>LastStatusUpdate</c>，
///         而 <c>LastRunningUpdate</c> 只在状态是「运行」(GSRUNNING=5) 时才更新。
///     </para>
///     <para>
///         它的看门狗（<c>LauncherViewModel</c>）判定：
///         <c>(Now - LastStatusUpdate) &gt; FrozenTime || (Now - LastRunningUpdate) &gt; FrozenTime</c> → <c>KillProcess(PID)</c>，
///         并在一分钟后重启该账号。MINIONAPP 自己拉起 MinionLauncher 时会同时把 <c>LastRunningUpdate</c> 设成当前时间；
///         由我们挂载时没人设它，它停在 <c>DateTime.MinValue</c>，客户端在挂载后十几秒被杀。
///     </para>
///     <para>
///         挂载成功后报一次「运行中」把计时器种上；游戏退出后报「停机」（GSNONE, PID 0），让它不再重开。
///         UID 取本次 launch 下发的编号, 包里的 KeyMd5 取下发卡号的 MD5。只在 MINIONAPP 进程在运行时发包, 发包失败只记日志。
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
    ///     GameStatus.GSNONE —— 它的状态机对这个状态直接 return（既不排队也不重启），即「这一行没在跑」。
    ///     不要用 GSSTOPPING_ACCOUNT(-6)：那条分支会去 KillProcess 并杀 MinionLauncher_64 与 sdologin。
    /// </summary>
    public const byte STATUS_NONE = 0;

    /// <summary>MinionReceiveFilter 固定的包体长度</summary>
    public const int PACKET_LENGTH = 40;

    private const int UID_LENGTH = 16;

    private const int KEY_MD5_LENGTH = 16;

    /// <summary>补报「运行中」前等多久（UDP 不保证送达）</summary>
    private static readonly TimeSpan ResendDelay = TimeSpan.FromSeconds(20);

    /// <summary>补报「停机」前等多久（UDP 不保证送达; 漏掉这一包 MINIONAPP 就会自己重开客户端）</summary>
    internal static TimeSpan StopResendDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>同一进程的「停机」在这段时间内只报一次</summary>
    private static readonly TimeSpan StopDedupWindow = TimeSpan.FromMinutes(1);

    /// <summary>本进程报过「运行中」的游戏进程号与对应的卡, 游戏退出时按同一张卡报「停机」</summary>
    private static readonly ConcurrentDictionary<int, ReportedCard> ReportedCards = [];

    /// <summary>最近报过「停机」的进程号与时间</summary>
    private static readonly ConcurrentDictionary<int, DateTime> RecentlyStopped = [];

    /// <summary>测试用: 代替「MINIONAPP 是否在运行」的判断</summary>
    internal static Func<bool>? IsMinionAppRunningOverride { get; set; }

    /// <summary>测试用: 代替 MINIONAPP 的 UDP 端口</summary>
    internal static int? PortOverride { get; set; }

    /// <summary>
    ///     卡号的 MD5（32 位小写十六进制）, 与 bot 发包时包里的 KeyMd5 相同算法（ASCII 编码后取 MD5）。卡号为空时返回 null
    /// </summary>
    public static string? KeycodeMd5Hex(string? keycode) =>
        string.IsNullOrWhiteSpace(keycode) ? null : Convert.ToHexString(MD5.HashData(Encoding.ASCII.GetBytes(keycode))).ToLowerInvariant();

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
    ///     MINIONAPP 在运行时报一次「运行中」, 并在后台隔一会儿补一次（UDP 不保证送达; 种时间戳是幂等的, 多发无害）。不等补发, 立即返回。
    ///     MINIONAPP 不在运行时只记下这张卡, 供游戏退出时判断。
    /// </summary>
    /// <param name="uid">这张卡在这次 variant 下的 Minion 编号（32 位十六进制）</param>
    /// <param name="keycodeMd5">卡号的 MD5 十六进制（<see cref="KeycodeMd5Hex" />）</param>
    /// <param name="label">日志里代表这张卡的文字（卡指纹）</param>
    /// <param name="gameProcess">挂上的游戏进程</param>
    public static void SeedRunningStatus(string uid, string? keycodeMd5, string label, Process gameProcess)
    {
        if (BuildPacket(uid, keycodeMd5, (uint)gameProcess.Id, STATUS_RUNNING) is not { } packet)
        {
            Log.Warning("[Minion] 卡 {Label} 的编号不是 32 位十六进制, 无法给 MINIONAPP 报状态", label);
            return;
        }

        var pid = gameProcess.Id;
        ReportedCards[pid] = new ReportedCard(uid, keycodeMd5, label);
        RecentlyStopped.TryRemove(pid, out _);

        if (!IsMinionAppRunning())
            return;

        Send(packet, label, pid, "运行中");

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

                Send(packet, label, pid, "运行中");
            }
        );
    }

    /// <summary>
    ///     游戏退出后报「停机」—— 不报的话 MINIONAPP 会把这一行转成「排队开始」并过一分钟自己拉新实例。
    ///     只对本启动器挂过的进程做（本进程报过「运行中」的, 或占用记录里带编号的）, 不碰 MINIONAPP 自己管的会话。
    /// </summary>
    /// <param name="gamePid">已退出的游戏进程号</param>
    /// <param name="minionUid">占用记录里的编号（本进程没报过时用）</param>
    /// <param name="keycodeMd5">占用记录里的卡号 MD5（本进程没报过时用; 旧记录没有时按全 0 发）</param>
    public static void ReportStopped(int gamePid, string? minionUid = null, string? keycodeMd5 = null)
    {
        string? uid;
        string? keyMd5;
        string  label;

        if (ReportedCards.TryRemove(gamePid, out var card))
        {
            uid    = card.Uid;
            keyMd5 = card.KeycodeMd5;
            label  = card.Label;
        }
        else
        {
            var record = minionUid == null ? MinionOccupancy.Read(gamePid) : null;
            uid    = minionUid ?? record?.MinionUid;
            keyMd5 = keycodeMd5 ?? record?.KeycodeMd5;
            label  = uid is { Length: >= 8 } ? uid[..8] : "?";
        }

        if (string.IsNullOrWhiteSpace(uid))
            return;

        // 同一编号还挂在别的活着的游戏上时, 报停机会让 MINIONAPP 把那边当卡死处理
        if (MinionOccupancy.IsUidAttachedElsewhere(uid, gamePid))
        {
            Log.Information("[Minion] 编号 {Uid} 还挂在别的游戏上, 不报「停机」(PID={GamePid})", label, gamePid);
            return;
        }

        var now = DateTime.UtcNow;

        if (RecentlyStopped.TryGetValue(gamePid, out var last) && now - last < StopDedupWindow)
            return;

        if (!IsMinionAppRunning())
            return;

        // PID 报 0 = 这一行没有对应进程
        if (BuildPacket(uid, keyMd5, 0, STATUS_NONE) is not { } packet)
            return;

        RecentlyStopped[gamePid] = now;

        var port       = PortOverride ?? UDP_PORT;
        var stoppedUid = uid;
        Send(packet, label, gamePid, "停机", port);

        _ = Task.Run
        (async () =>
            {
                await Task.Delay(StopResendDelay).ConfigureAwait(false);

                // 这个编号在这期间又被挂到别的游戏上了就不补: 晚到的「停机」会盖掉那边刚报的「运行中」
                if (IsUidInUse(stoppedUid, gamePid))
                    return;

                Send(packet, label, gamePid, "停机", port);
            }
        );
    }

    /// <summary>
    ///     这个编号是否正挂在除 <paramref name="exceptPid" /> 以外的游戏上（本进程报过「运行中」的, 或占用记录里活着的）
    /// </summary>
    private static bool IsUidInUse(string uid, int exceptPid)
    {
        try
        {
            return ReportedCards.Any(x => x.Key != exceptPid && string.Equals(x.Value.Uid.Trim(), uid.Trim(), StringComparison.OrdinalIgnoreCase)) ||
                   MinionOccupancy.IsUidAttachedElsewhere(uid, exceptPid);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[Minion] 检查编号占用失败, 不补报「停机」");
            return true;
        }
    }

    private static void Send(byte[] packet, string label, int gamePid, string what, int? targetPort = null)
    {
        var port = targetPort ?? PortOverride ?? UDP_PORT;

        try
        {
            using var client = new UdpClient();
            client.Send(packet, packet.Length, "127.0.0.1", port);

            Log.Information
            (
                "[Minion] 已按 MINIONAPP 协议报「{What}」: 卡={Label}, PID={GamePid} → 127.0.0.1:{Port}",
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
    ///     KeyMd5 那 16 字节 MINIONAPP 收下但不校验, 有卡号 MD5 时按 bot 的样子填, 没有或格式不对时全 0。
    ///     UID 不是 32 位十六进制时返回 null。
    /// </summary>
    public static byte[]? BuildPacket(string? minionUid, string? keycodeMd5, uint gamePid, byte status)
    {
        if (ParseHex(minionUid, UID_LENGTH) is not { } uid)
            return null;

        var packet = new byte[PACKET_LENGTH];
        uid.CopyTo(packet, 0);

        if (ParseHex(keycodeMd5, KEY_MD5_LENGTH) is { } keyMd5)
            keyMd5.CopyTo(packet, UID_LENGTH);

        BitConverter.GetBytes(gamePid).CopyTo(packet, UID_LENGTH + KEY_MD5_LENGTH);
        packet[36] = status;

        return packet;
    }

    /// <summary>
    ///     编号就是 16 字节的十六进制原样打印（MINIONAPP 侧是 <c>BitConverter.ToString(UuId).Replace("-", "").ToLower()</c> 后与 UID 比较）
    /// </summary>
    private static byte[]? ParseHex(string? hex, int byteLength)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return null;

        var trimmed = hex.Trim();

        if (trimmed.Length != byteLength * 2)
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

    /// <summary>本进程报过「运行中」的卡: 编号、卡号 MD5、日志标签</summary>
    private sealed record ReportedCard(string Uid, string? KeycodeMd5, string Label);
}
