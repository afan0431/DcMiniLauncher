using System.Diagnostics;
using Serilog;
using XIVLauncher.Common.Util;
using XIVLauncher.Minion;

namespace XIVLauncher.CatHost;

/// <summary>
///     能力标记: 这个版本按 launch 带来的卡号、编号和论坛账号直接挂 Minion, 不读本机 Minion 的账号文件。
///     工作台按程序集里有没有这个类型判断能不能给它发这些参数。
/// </summary>
public static class CatMinionDirect
{
}

/// <summary>
///     一次上号要挂的 Minion 卡（已校验）: Fingerprint = 卡指纹; Variant = cn / global; Keycode = 卡号;
///     Uid = 这张卡在这个 variant 下的 Minion 编号（-uid）; ForumId / ForumPassword = Minion 论坛账号与密码
/// </summary>
public sealed record CatMinionLaunch
(
    string    Fingerprint,
    string    Variant,
    CatSecret Keycode,
    string    Uid,
    string    ForumId,
    CatSecret ForumPassword
)
{
    /// <summary>
    ///     只打印指纹和 variant
    /// </summary>
    public override string ToString() =>
        $"CatMinionLaunch {{ Fingerprint = {Fingerprint}, Variant = {Variant} }}";
}

/// <summary>
///     国服与国际服挂 Minion 共用的检查、预占和占用判断
/// </summary>
public static class CatMinionReservations
{
    /// <summary>预占时持有, 防止同一张卡的号同时上号时都挂上去</summary>
    private const string MUTEX_NAME = @"Local\DcMiniLauncher-MinionSelect";

    private static readonly TimeSpan MutexTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     起游戏前检查本机能不能挂这张卡: launch 带了卡、本机找得到 MinionLauncher_64.exe。能挂返回 null
    /// </summary>
    public static (string Code, string Message)? Check(CatMinionLaunch? minion)
    {
        if (minion == null)
            return (CatCodes.MINION_NOT_CONFIGURED, "launch 时没有指定 Minion 卡");

        var installPath = MinionInstall.InstallPath;

        if (!MinionInstall.IsLauncherPresent(installPath))
            return (CatCodes.MINION_NOT_CONFIGURED, $"找不到 {MinionInstall.GetLauncherExePath(installPath)}, 请在 DcMiniLauncher「设置 → Minion」里指定安装目录");

        return null;
    }

    /// <summary>
    ///     加跨进程锁检查这张卡的这个编号没有挂在本机别的活着的游戏上, 并立即为这个游戏写占用记录作预占
    ///     （挂载成功后由挂载流程覆盖成正式记录）。拿不到锁时不加锁照常做。这个游戏已有自己的记录（force 重挂）时不再写预占。
    /// </summary>
    /// <returns>Error = 不能挂的原因; Reserved = 写了预占（没挂上时要撤掉）</returns>
    public static async Task<((string Code, string Message)? Error, bool Reserved)> ReserveAsync
    (
        CatMinionLaunch?              minion,
        Process                       process,
        string                        accountName,
        Func<Process, DateTimeOffset> startedAtOf
    )
    {
        if (minion == null)
            return ((CatCodes.MINION_NOT_CONFIGURED, "launch 时没有指定 Minion 卡"), false);

        using var gate = await CrossProcessMutex.TryAcquireAsync(MUTEX_NAME, MutexTimeout).ConfigureAwait(false);

        if (MinionOccupancy.IsUidAttachedElsewhere(minion.Uid, process.Id))
        {
            Log.Warning("[CatHost] 卡 {Fingerprint} 的 {Variant} 编号已挂在本机别的游戏上, 不挂", minion.Fingerprint, minion.Variant);
            return ((CatCodes.ALREADY_ATTACHED, $"这张卡（{minion.Fingerprint}）已挂在这台电脑的另一个游戏上, 没有挂, 免得把那边的 Minion 顶掉"), false);
        }

        var startedAt = startedAtOf(process);

        if (MinionOccupancy.Read(process.Id) is { } existing && existing.ProcessStartedAt == startedAt)
            return (null, false);

        var reserved = MinionOccupancy.Write
        (
            new MinionOccupancyRecord
            {
                Pid              = process.Id,
                ProcessStartedAt = startedAt,
                CardFingerprint  = minion.Fingerprint,
                Variant          = minion.Variant,
                AccountName      = accountName,
                MinionUid        = minion.Uid,
                KeycodeMd5       = MinionAppStatusReporter.KeycodeMd5Hex(minion.Keycode.Reveal()),
                AttachedAt       = DateTimeOffset.UtcNow
            }
        );

        return (null, reserved);
    }

    /// <summary>
    ///     占用记录里这个游戏进程已挂着 Minion（同一进程: 创建时间一致）
    /// </summary>
    public static bool IsAttached(Process process)
    {
        try
        {
            return MinionOccupancy.Read(process.Id) is { } record &&
                   record.ProcessStartedAt == MinionOccupancy.GetProcessStartedAt(process);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[CatHost] 读取 Minion 占用记录失败");
            return false;
        }
    }
}
