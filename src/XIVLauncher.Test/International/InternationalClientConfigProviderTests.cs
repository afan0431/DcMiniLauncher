using System.IO;
using System.Net;
using System.Net.Http;
using XIVLauncher.Common.Constant;
using XIVLauncher.Common.Game.International;
using Xunit;
using Xunit.Abstractions;

namespace XIVLauncher.Test.International;

public sealed class InternationalClientConfigProviderTests : IDisposable
{
    private const string REMOTE_JSON = "{\"frontierUrl\":\"https://launcher.finalfantasyxiv.com/v999/index.html?rc_lang={0}&time={1}\",\"cutOffBootver\":\"\",\"flags\":0}";

    private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("dml-intl-config-");

    private FileInfo CacheFile => new(Path.Combine(directory.FullName, "sub", "launcherClientConfig.json"));

    public void Dispose()
    {
        try
        {
            directory.Delete(true);
        }
        catch
        {
            // ignored
        }
    }

    [Fact]
    public async Task Remote_Ok_IsUsed_AndSavedForLater()
    {
        var handler  = new FakeHttpHandler(_ => FakeHttpHandler.Text(REMOTE_JSON));
        var provider = new InternationalClientConfigProvider(CacheFile, handler);

        var config = await provider.GetAsync();

        Assert.Equal(InternationalClientConfigSource.Remote, config.Source);
        Assert.Equal("https://launcher.finalfantasyxiv.com/v999/index.html?rc_lang={0}&time={1}", config.FrontierUrl);
        Assert.Null(config.CutOffBootVersion);
        Assert.Equal(Links.GOATCORP_LAUNCHER_CLIENT_CONFIG_URL, Assert.Single(handler.Requests).Url);
        Assert.True(File.Exists(CacheFile.FullName));
    }

    [Fact]
    public async Task Remote_Fails_FallsBackToCache_ThenToBuiltin()
    {
        var failing = new FakeHttpHandler(_ => throw new HttpRequestException("offline"));

        // 没存过: 用内置值
        var builtin = await new InternationalClientConfigProvider(CacheFile, failing).GetAsync();
        Assert.Equal(InternationalClientConfigSource.Builtin, builtin.Source);
        Assert.Equal(Links.SE_FRONTIER_URL_TEMPLATE_BUILTIN, builtin.FrontierUrl);
        Assert.True(InternationalClientConfigProvider.IsValidFrontierUrlTemplate(builtin.FrontierUrl));

        // 成功一次后再失败: 用存下的
        await new InternationalClientConfigProvider(CacheFile, new FakeHttpHandler(_ => FakeHttpHandler.Text(REMOTE_JSON.Replace("\"cutOffBootver\":\"\"", "\"cutOffBootver\":\"2026.05.01.0000.0001\"")))).GetAsync();
        var cached = await new InternationalClientConfigProvider(CacheFile, failing).GetAsync();

        Assert.Equal(InternationalClientConfigSource.Cache, cached.Source);
        Assert.Contains("/v999/", cached.FrontierUrl);
        Assert.Equal("2026.05.01.0000.0001", cached.CutOffBootVersion);
    }

    [Theory]
    [InlineData("<html>502 Bad Gateway</html>", HttpStatusCode.BadGateway)]
    [InlineData("not json", HttpStatusCode.OK)]
    [InlineData("{}", HttpStatusCode.OK)]
    [InlineData("{\"frontierUrl\":\"\"}", HttpStatusCode.OK)]
    [InlineData("{\"frontierUrl\":\"https://launcher.finalfantasyxiv.com/v1/index.html\"}", HttpStatusCode.OK)]      // 没有占位符
    [InlineData("{\"frontierUrl\":\"javascript:alert({0}{1})\"}", HttpStatusCode.OK)]                                 // 不是 http(s)
    [InlineData("{\"frontierUrl\":\"https://x/{0}/{1}/{2}\"}", HttpStatusCode.OK)]                                    // 多余占位符, 格式化会失败
    public async Task Remote_BadAnswer_IsNotUsedAndNotSaved(string body, HttpStatusCode status)
    {
        var provider = new InternationalClientConfigProvider(CacheFile, new FakeHttpHandler(_ => FakeHttpHandler.Text(body, status)));

        var config = await provider.GetAsync();

        Assert.Equal(InternationalClientConfigSource.Builtin, config.Source);
        Assert.False(File.Exists(CacheFile.FullName));
    }

    [Theory]
    [InlineData("{\"frontierUrl\":\"https://launcher.finalfantasyxiv.com/v1/index.html?rc_lang={0}&time={1}\",\"cutOffBootver\":null,\"flags\":0}", null)]
    [InlineData("{\"frontierUrl\":\"https://launcher.finalfantasyxiv.com/v1/index.html?rc_lang={0}&time={1}\"}", null)]
    [InlineData("{\"FrontierUrl\":\"https://launcher.finalfantasyxiv.com/v1/index.html?rc_lang={0}&time={1}\",\"CutOffBootver\":\" 2026.05.01.0000.0001 \",\"flags\":\"x\"}", "2026.05.01.0000.0001")]
    [InlineData("{\"frontierUrl\":\"https://launcher.finalfantasyxiv.com/v1/index.html?rc_lang={0}&time={1}\",\"cutOffBootver\":20260501,\"flags\":{\"a\":[1]}}", null)]
    public async Task Remote_OddlyShapedOptionalFields_DoNotDiscardTheTemplate(string body, string? expectedCutOff)
    {
        var config = await new InternationalClientConfigProvider(CacheFile, new FakeHttpHandler(_ => FakeHttpHandler.Text(body))).GetAsync();

        Assert.Equal(InternationalClientConfigSource.Remote, config.Source);
        Assert.Contains("/v1/", config.FrontierUrl);
        Assert.Equal(expectedCutOff, config.CutOffBootVersion);
    }

    [Theory]
    [InlineData("{\"frontierUrl\":740}")]
    [InlineData("{\"frontierUrl\":null}")]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    public async Task Remote_TemplateNotAString_FallsBack(string body)
    {
        var config = await new InternationalClientConfigProvider(CacheFile, new FakeHttpHandler(_ => FakeHttpHandler.Text(body))).GetAsync();

        Assert.Equal(InternationalClientConfigSource.Builtin, config.Source);
    }

    [Fact]
    public async Task Cancellation_IsNotSwallowed()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var provider = new InternationalClientConfigProvider(CacheFile, new FakeHttpHandler(_ => FakeHttpHandler.Text(REMOTE_JSON)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GetAsync(cts.Token));
    }

    [Theory]
    [InlineData(null, "2026.05.01.0000.0001", false)]
    [InlineData("", "2026.05.01.0000.0001", false)]
    [InlineData("2026.05.01.0000.0001", "2026.05.01.0000.0001", false)] // 相等不拦（goatcorp: bootver > cutoff 才拦）
    [InlineData("2026.05.01.0000.0000", "2026.05.01.0000.0001", true)]
    [InlineData("2026.05.01.0000.0002", "2026.05.01.0000.0001", false)]
    [InlineData("garbage", "2026.05.01.0000.0001", false)]
    public void KillSwitch_BlocksOnlyWhenLocalBootIsNewerThanCutOff(string? cutOff, string localBoot, bool expected)
    {
        var config = new InternationalClientConfig("https://x/{0}/{1}", cutOff, InternationalClientConfigSource.Remote);

        Assert.Equal(expected, config.IsBootVersionCutOff(localBoot));
    }
}

/// <summary>
///     联网核对 goatcorp 的公开接口还在、字段名没变。缺省跳过（DML_NETWORK_SMOKE=1 才跑）, 不进默认测试集。
/// </summary>
[Trait("Category", "InternationalNetwork")]
public sealed class InternationalNetworkSmokeTests(ITestOutputHelper output)
{
    [NetworkSmokeFact]
    public async Task LauncherClientConfig_IsReachable_AndHasFrontierUrl()
    {
        var config = await new InternationalClientConfigProvider(null).GetAsync();

        output.WriteLine($"FrontierUrl: {config.FrontierUrl}");
        output.WriteLine($"CutOffBootVersion: {config.CutOffBootVersion ?? "(空)"}");
        output.WriteLine($"内置兜底值与线上是否一致: {config.FrontierUrl == Links.SE_FRONTIER_URL_TEMPLATE_BUILTIN}");

        Assert.Equal(InternationalClientConfigSource.Remote, config.Source);
        Assert.StartsWith("https://launcher.finalfantasyxiv.com/", config.FrontierUrl);
    }
}
