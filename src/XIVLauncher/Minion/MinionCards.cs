using System.Security.Cryptography;
using System.Text;

namespace XIVLauncher.Minion;

/// <summary>
///     Minion 卡（Accounts.json 的 Keycode）的不可逆指纹与按指纹选行。
///     同一张卡常有两行: 普通注入文件一行、<c>UseBetaFFXIVFiles</c> 勾选的 beta 注入文件一行。
/// </summary>
public static class MinionCards
{
    /// <summary>国服注入文件那一行（UseBetaFFXIVFiles = false, 用 FFXIVMinionCN_64.dat）</summary>
    public const string VARIANT_CN = "cn";

    /// <summary>国际服注入文件那一行（UseBetaFFXIVFiles = true, 用 MinionFiles\Beta\FFXIVMinionCN_64_BETA.dat）</summary>
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
    ///     行对应的 variant
    /// </summary>
    public static string VariantOf(MinionAccount account) =>
        account.UseBetaFiles ? VARIANT_GLOBAL : VARIANT_CN;

    /// <summary>
    ///     按卡指纹与 variant 找唯一一行; 找不到返回 null, 不退回其它行。
    ///     同一指纹同一 variant 有多行时取 Accounts.json 里的第一行（同卡同注入文件, 挂哪行效果相同）。
    /// </summary>
    public static MinionAccount? FindByCard(IEnumerable<MinionAccount> accounts, string fingerprint, string variant)
    {
        if (!IsValidFingerprint(fingerprint) || !IsValidVariant(variant))
            return null;

        return accounts.FirstOrDefault
        (account => !string.IsNullOrWhiteSpace(account.Keycode) &&
                    string.Equals(VariantOf(account), variant, StringComparison.Ordinal) &&
                    string.Equals(Fingerprint(account.Keycode), fingerprint, StringComparison.Ordinal)
        );
    }
}
