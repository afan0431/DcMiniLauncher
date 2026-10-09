namespace XIVLauncher.Minion;

/// <summary>
///     launch 里 Minion 参数的格式校验。卡指纹是 SHA-256("cat-minion-card:" + 卡号) 的前 16 个小写十六进制字符, 由服务器计算。
///     一张卡在国服和国际服各有一个 Minion 编号（<c>-uid</c>）, 由工作台按这次上号的 variant 下发。
/// </summary>
public static class MinionCards
{
    /// <summary>国服客户端</summary>
    public const string VARIANT_CN = "cn";

    /// <summary>国际服客户端</summary>
    public const string VARIANT_GLOBAL = "global";

    private const int FINGERPRINT_LENGTH = 16;

    private const int UID_LENGTH = 32;

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
    ///     Minion 的 <c>-uid</c> 格式是否合法（32 个十六进制字符, 不分大小写）。Minion 按它保存每个号的登录大区、技能配置等
    /// </summary>
    public static bool IsValidUid(string? uid) =>
        uid is { Length: UID_LENGTH } && uid.All(char.IsAsciiHexDigit);
}
