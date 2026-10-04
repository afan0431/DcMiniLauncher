using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace XIVLauncher.Minion;

/// <summary>
///     Minion 卡（Accounts.json 的 Keycode）的不可逆指纹与按指纹选行。
///     同一张卡常有两行, 分别给国服和国际服客户端用（注入设置不同）, 两行靠各自的「游戏执行程序」路径（<c>PathToExe</c>）区分。
/// </summary>
public static class MinionCards
{
    /// <summary>执行程序路径位于本启动器配置的国服游戏目录之下（或为空）的那一行</summary>
    public const string VARIANT_CN = "cn";

    /// <summary>执行程序路径不在国服游戏目录之下的那一行</summary>
    public const string VARIANT_GLOBAL = "global";

    private const string FINGERPRINT_PREFIX = "cat-minion-card:";

    private const int FINGERPRINT_LENGTH = 16;

    /// <summary>
    ///     卡指纹: SHA-256("cat-minion-card:" + Keycode) 的前 16 个小写十六进制字符
    /// </summary>
    public static string Fingerprint(string keycode)
    {
        ArgumentNullException.ThrowIfNull(keycode);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(FINGERPRINT_PREFIX + keycode));
        return Convert.ToHexStringLower(hash)[..FINGERPRINT_LENGTH];
    }

    /// <summary>
    ///     指纹格式是否合法（16 个小写十六进制字符）
    /// </summary>
    public static bool IsValidFingerprint(string? fingerprint) =>
        fingerprint is { Length: FINGERPRINT_LENGTH } && fingerprint.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>
    ///     variant 是否为已知取值
    /// </summary>
    public static bool IsValidVariant(string? variant) =>
        variant is VARIANT_CN or VARIANT_GLOBAL;

    /// <summary>
    ///     本启动器设置里的国服游戏目录（盛趣与 WeGame）, 未配置的项为 null
    /// </summary>
    public static IReadOnlyList<string?> ConfiguredCnGameRoots()
    {
        var settings = App.Settings;

        if (settings == null)
            return [];

        return [settings.GamePath?.FullName, settings.WeGamePath?.FullName];
    }

    /// <summary>
    ///     按本启动器当前设置的国服游戏目录判断行对应的 variant
    /// </summary>
    public static string VariantOf(MinionAccount account) =>
        VariantOf(account, ConfiguredCnGameRoots());

    /// <summary>
    ///     行对应的 variant: 执行程序路径为空或位于任一国服游戏目录之下 → <see cref="VARIANT_CN" />, 否则 → <see cref="VARIANT_GLOBAL" />
    /// </summary>
    /// <param name="account">Accounts.json 的一行</param>
    /// <param name="cnGameRoots">国服游戏目录, 空项忽略</param>
    public static string VariantOf(MinionAccount account, IEnumerable<string?> cnGameRoots)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(cnGameRoots);

        if (string.IsNullOrWhiteSpace(account.PathToExe))
            return VARIANT_CN;

        return cnGameRoots.Any(root => IsUnderDirectory(account.PathToExe, root)) ? VARIANT_CN : VARIANT_GLOBAL;
    }

    /// <summary>
    ///     按卡指纹与 variant 找唯一一行（variant 按本启动器当前设置的国服游戏目录判断）
    /// </summary>
    public static MinionAccount? FindByCard(IEnumerable<MinionAccount> accounts, string fingerprint, string variant) =>
        FindByCard(accounts, fingerprint, variant, ConfiguredCnGameRoots());

    /// <summary>
    ///     按卡指纹与 variant 找唯一一行; 找不到返回 null, 不退回其它行。
    ///     同一指纹同一 variant 有多行时取 Accounts.json 里的第一行。
    /// </summary>
    /// <param name="accounts">Accounts.json 的全部行</param>
    /// <param name="fingerprint">卡指纹</param>
    /// <param name="variant"><see cref="VARIANT_CN" /> 或 <see cref="VARIANT_GLOBAL" /></param>
    /// <param name="cnGameRoots">国服游戏目录, 空项忽略</param>
    public static MinionAccount? FindByCard(IEnumerable<MinionAccount> accounts, string fingerprint, string variant, IEnumerable<string?> cnGameRoots) =>
        SelectRow(accounts, fingerprint, variant, cnGameRoots, new HashSet<string>()).Row;

    /// <summary>
    ///     按卡指纹与 variant 选一行, 跳过已挂在别的活着的游戏上的行（按占用记录里的 Minion 行 UID）;
    ///     符合的行都被占用时仍取第一行并在说明里写明。说明逐行写出判定依据（UID 前 8 位、执行程序路径、判成的 variant、是否占用）, 不含 Keycode。
    /// </summary>
    /// <param name="accounts">Accounts.json 的全部行</param>
    /// <param name="fingerprint">卡指纹</param>
    /// <param name="variant"><see cref="VARIANT_CN" /> 或 <see cref="VARIANT_GLOBAL" /></param>
    /// <param name="cnGameRoots">国服游戏目录, 空项忽略</param>
    /// <param name="occupiedUids">已被占用的 Minion 行 UID（大小写不敏感比较）</param>
    public static MinionCardSelection SelectRow
    (
        IEnumerable<MinionAccount> accounts,
        string                     fingerprint,
        string                     variant,
        IEnumerable<string?>       cnGameRoots,
        IReadOnlySet<string>       occupiedUids
    )
    {
        if (!IsValidFingerprint(fingerprint) || !IsValidVariant(variant))
            return new MinionCardSelection(null, ["卡指纹或 variant 无效"], false);

        var roots      = cnGameRoots.ToList();
        var rootsText  = string.Join("; ", roots.Where(x => !string.IsNullOrWhiteSpace(x)));
        var notes      = new List<string> { $"国服游戏目录: {(rootsText.Length == 0 ? "(未配置)" : rootsText)}" };
        var candidates = new List<MinionAccount>();

        foreach (var account in accounts)
        {
            if (string.IsNullOrWhiteSpace(account.Keycode) || !string.Equals(Fingerprint(account.Keycode), fingerprint, StringComparison.Ordinal))
                continue;

            var rowVariant = VariantOf(account, roots);
            var occupied   = IsOccupied(account, occupiedUids);
            var uid        = string.IsNullOrWhiteSpace(account.Uid) ? "(无 UID)" : account.Uid.Trim()[..Math.Min(8, account.Uid.Trim().Length)];

            notes.Add
            (
                $"行 UID {uid}: 执行程序 {(string.IsNullOrWhiteSpace(account.PathToExe) ? "(空, 按国服)" : account.PathToExe)} → {rowVariant}" +
                (occupied ? ", 已挂在别的游戏上" : string.Empty)
            );

            if (string.Equals(rowVariant, variant, StringComparison.Ordinal))
                candidates.Add(account);
        }

        if (candidates.Count == 0)
            return new MinionCardSelection(null, notes, false);

        var free = candidates.FirstOrDefault(x => !IsOccupied(x, occupiedUids));

        if (free != null)
            return new MinionCardSelection(free, notes, false);

        notes.Add("符合的行都已挂在别的游戏上, 仍选第一行");
        return new MinionCardSelection(candidates[0], notes, true);
    }

    private static bool IsOccupied(MinionAccount account, IReadOnlySet<string> occupiedUids) =>
        !string.IsNullOrWhiteSpace(account.Uid) && occupiedUids.Contains(account.Uid.Trim());

    /// <summary>
    ///     规范化完整路径后按目录前缀比较（大小写不敏感）, 判断 <paramref name="path" /> 是否位于 <paramref name="directory" /> 之下
    /// </summary>
    private static bool IsUnderDirectory(string path, string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return false;

        string fullPath;
        string fullDirectory;

        try
        {
            fullPath      = Path.GetFullPath(path.Trim());
            fullDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory.Trim()));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        // 盘符根目录（如 G:\）去不掉末尾分隔符, 不再重复追加
        var prefix = Path.EndsInDirectorySeparator(fullDirectory) ? fullDirectory : fullDirectory + Path.DirectorySeparatorChar;

        return fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
///     按卡选行的结果
/// </summary>
/// <param name="Row">选中的行; 没有符合的行为 null</param>
/// <param name="Notes">判定依据, 逐行写给日志</param>
/// <param name="AllOccupied">符合的行都已被占用（仍选了第一行）</param>
public sealed record MinionCardSelection(MinionAccount? Row, IReadOnlyList<string> Notes, bool AllOccupied);
