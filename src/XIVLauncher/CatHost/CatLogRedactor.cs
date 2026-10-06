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
    ///     登记一个必须遮住的值（国际服密码、登录得到的会话值）: 不受最短长度限制, 并同时登记它在网址和表单里的编码形式
    ///     （登录表单是 URL 编码的, 异常信息里可能出现编码后的样子）。短值可能误伤正常文字, 宁可误伤。
    /// </summary>
    public void RegisterSecret(string? secret)
    {
        if (string.IsNullOrEmpty(secret))
            return;

        string[] forms = [secret, Uri.EscapeDataString(secret), System.Net.WebUtility.UrlEncode(secret)];

        lock (secretsLock)
        {
            foreach (var form in forms)
            {
                if (!string.IsNullOrEmpty(form) && !secrets.Contains(form))
                    secrets.Add(form);
            }
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
