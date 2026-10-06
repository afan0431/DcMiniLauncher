using System.Globalization;
using System.Text.Json;
using Serilog;
using XIVLauncher.Common.Constant;

namespace XIVLauncher.Common.Game.International;

/// <summary>国际服运行时配置是从哪来的</summary>
public enum InternationalClientConfigSource
{
    /// <summary>刚从 goatcorp 的配置接口取到</summary>
    Remote,

    /// <summary>接口取不到, 用的是本机上次成功取到后存下的</summary>
    Cache,

    /// <summary>接口取不到、本机也没有存过, 用的是写在程序里的值（可能已过期）</summary>
    Builtin
}

/// <summary>
///     国际服运行时配置
/// </summary>
/// <param name="FrontierUrl">登录页地址模板（Referer 用）, {0} = 语言代码, {1} = 时间</param>
/// <param name="CutOffBootVersion">
///     杀开关: SE 改了登录流程时 goatcorp 会填上最后一个确认可用的 boot 版本, 本地 boot 版本比它新就不该再登录; 空 = 没有限制
/// </param>
/// <param name="Source">来源</param>
public sealed record InternationalClientConfig(string FrontierUrl, string? CutOffBootVersion, InternationalClientConfigSource Source)
{
    /// <summary>
    ///     本地 boot 版本是否已被杀开关拦下（比 <see cref="CutOffBootVersion" /> 新）。
    ///     判定照 goatcorp MainWindowViewModel.cs:211-222（bootver &gt; cutoff 时拒绝启动）; 版本号解析不了时不拦。
    /// </summary>
    public bool IsBootVersionCutOff(string localBootVersion)
    {
        if (string.IsNullOrWhiteSpace(CutOffBootVersion))
            return false;

        try
        {
            return SeVersion.Parse(localBootVersion.Trim()) > SeVersion.Parse(CutOffBootVersion.Trim());
        }
        catch (Exception ex) when (ex is FormatException or IndexOutOfRangeException or OverflowException)
        {
            Log.Warning("[International] 杀开关版本号无法比较: 本地 {Local}, 截止 {CutOff}", localBootVersion, CutOffBootVersion);
            return false;
        }
    }
}

/// <summary>
///     取国际服运行时配置（登录页地址模板不在 goatcorp 源码里, 由它的服务器下发）。
///     兜底顺序: 接口 → 本机上次成功取到后存下的文件 → 写在程序里的值。一次取不到不至于上不了号。
/// </summary>
/// <param name="cacheFile">存上次成功结果的文件; null = 不存不读</param>
/// <param name="httpHandler">HTTP 处理器; 测试传假的</param>
/// <param name="url">配置接口地址</param>
/// <param name="timeout">取配置的超时</param>
public sealed class InternationalClientConfigProvider
(
    FileInfo?           cacheFile,
    HttpMessageHandler? httpHandler = null,
    string              url         = Links.GOATCORP_LAUNCHER_CLIENT_CONFIG_URL,
    TimeSpan?           timeout     = null
)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    private sealed class ConfigDto
    {
        public string? FrontierUrl   { get; set; }
        public string? CutOffBootver { get; set; }
    }

    /// <summary>
    ///     取配置; 不抛网络异常（取消除外）
    /// </summary>
    public async Task<InternationalClientConfig> GetAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var remote = await FetchAsync(cancellationToken).ConfigureAwait(false);

            if (remote != null)
            {
                SaveCache(remote);
                return new InternationalClientConfig(remote.FrontierUrl!, NullIfBlank(remote.CutOffBootver), InternationalClientConfigSource.Remote);
            }

            Log.Warning("[International] 配置接口返回的登录页地址模板不合格, 改用兜底值");
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            Log.Warning(ex, "[International] 取登录页地址模板失败, 改用兜底值");
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (LoadCache() is { } cached)
            return new InternationalClientConfig(cached.FrontierUrl!, NullIfBlank(cached.CutOffBootver), InternationalClientConfigSource.Cache);

        return new InternationalClientConfig(Links.SE_FRONTIER_URL_TEMPLATE_BUILTIN, null, InternationalClientConfigSource.Builtin);
    }

    /// <summary>
    ///     模板是否可用: http(s) 绝对地址, 含 {0} 和 {1}, 能正常格式化
    /// </summary>
    public static bool IsValidFrontierUrlTemplate(string? template)
    {
        if (string.IsNullOrWhiteSpace(template) || !template.Contains("{0}", StringComparison.Ordinal) || !template.Contains("{1}", StringComparison.Ordinal))
            return false;

        try
        {
            var formatted = string.Format(CultureInfo.InvariantCulture, template, "en_gb", "2026-01-01-00-00");

            return Uri.TryCreate(formatted, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http";
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private async Task<ConfigDto?> FetchAsync(CancellationToken cancellationToken)
    {
        using var client = httpHandler == null ? new HttpClient() : new HttpClient(httpHandler, false);
        client.Timeout = timeout ?? TimeSpan.FromSeconds(10);

        using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var dto  = JsonSerializer.Deserialize<ConfigDto>(json, JsonOptions);

        return IsValidFrontierUrlTemplate(dto?.FrontierUrl) ? dto : null;
    }

    private ConfigDto? LoadCache()
    {
        if (cacheFile == null)
            return null;

        try
        {
            if (!File.Exists(cacheFile.FullName))
                return null;

            var dto = JsonSerializer.Deserialize<ConfigDto>(File.ReadAllText(cacheFile.FullName), JsonOptions);
            return IsValidFrontierUrlTemplate(dto?.FrontierUrl) ? dto : null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[International] 读取上次存下的登录页地址模板失败");
            return null;
        }
    }

    private void SaveCache(ConfigDto dto)
    {
        if (cacheFile == null)
            return;

        try
        {
            Directory.CreateDirectory(cacheFile.DirectoryName!);

            var tempPath = cacheFile.FullName + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(dto, JsonOptions));
            File.Move(tempPath, cacheFile.FullName, true);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[International] 保存登录页地址模板失败");
        }
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
