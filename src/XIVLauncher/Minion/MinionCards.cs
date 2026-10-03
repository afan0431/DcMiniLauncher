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
    public static MinionAccount? FindByCard(IEnumerable<MinionAccount> accounts, string fingerprint, string variant, IEnumerable<string?> cnGameRoots)
    {
        if (!IsValidFingerprint(fingerprint) || !IsValidVariant(variant))
            return null;

        var roots = cnGameRoots.ToList();

        return accounts.FirstOrDefault
        (account => !string.IsNullOrWhiteSpace(account.Keycode) &&
                    string.Equals(VariantOf(account, roots), variant, StringComparison.Ordinal) &&
                    string.Equals(Fingerprint(account.Keycode), fingerprint, StringComparison.Ordinal)
        );
    }

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
