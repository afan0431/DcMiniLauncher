namespace XIVLauncher.CatHost;

/// <summary>
///     发给外壳的文字里把登记过的敏感值（握手令牌、票据、快速登录凭证、密码、卡密）替换成 ***
/// </summary>
public sealed class CatLogRedactor
{
    private const int MIN_SECRET_LENGTH = 6;

    private readonly object       secretsLock = new();
    private readonly List<string> secrets     = [];

    /// <summary>
    ///     登记一个敏感值; 太短的值不登记, 免得误伤正常文字
    /// </summary>
    public void Register(string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret) || secret.Length < MIN_SECRET_LENGTH)
            return;

        lock (secretsLock)
        {
            if (!secrets.Contains(secret))
                secrets.Add(secret);
        }
    }

    /// <summary>
    ///     把文字里出现的敏感值替换成 ***
    /// </summary>
    public string Redact(string text)
    {
        lock (secretsLock)
        {
            foreach (var secret in secrets.OrderByDescending(x => x.Length))
                text = text.Replace(secret, "***", StringComparison.Ordinal);
        }

        return text;
    }
}
