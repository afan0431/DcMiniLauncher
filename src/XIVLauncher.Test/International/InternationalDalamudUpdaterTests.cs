using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using XIVLauncher.Common;
using XIVLauncher.Common.Constant;
using XIVLauncher.Dalamud;
using XIVLauncher.Dalamud.International;
using Xunit;
using Xunit.Abstractions;

namespace XIVLauncher.Test.International;

public sealed class InternationalDalamudUpdaterTests : IDisposable
{
    private const string DALAMUD_URL = "https://example.invalid/dalamud/latest.zip";
    private const string ASSET_URL   = "https://example.invalid/assets/package.zip";

    private readonly DirectoryInfo root = Directory.CreateTempSubdirectory("dml-intl-dalamud-");

    private string dalamudVersion   = "13.0.0.4";
    private string supportedGameVer = FakeGameDirectory.GAME_VERSION;
    private string runtimeVersion   = "9.0.2";
    private int    assetVersion     = 7;
    private bool   failVersionInfo;

    private DirectoryInfo InternationalRoot => new(Path.Combine(root.FullName, "XIVLauncherCN", "international"));

    private DalamudHostPaths Paths => InternationalDalamudService.CreateHostPaths(InternationalRoot, new DirectoryInfo(root.FullName));

    public void Dispose()
    {
        try
        {
            root.Delete(true);
        }
        catch
        {
            // ignored
        }
    }

    #region 假服务器

    private static byte[] Zip(params (string Name, string Content)[] entries)
    {
        using var buffer = new MemoryStream();

        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, true))
        {
            foreach (var (name, content) in entries)
            {
                using var stream = archive.CreateEntry(name).Open();
                stream.Write(Encoding.UTF8.GetBytes(content));
            }
        }

        return buffer.ToArray();
    }

    private static string Md5(string content) => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(content)));

    private static string Sha1(string content) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(content)));

    private byte[] DalamudZip()
    {
        var injector = "injector " + dalamudVersion;
        var hashes   = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["Dalamud.Injector.exe"] = Md5(injector),
            ["Dalamud.dll"]          = Md5("dalamud"),
            ["ImGuiScene.dll"]       = Md5("imgui")
        });

        return Zip(("Dalamud.Injector.exe", injector), ("Dalamud.dll", "dalamud"), ("ImGuiScene.dll", "imgui"), ("hashes.json", hashes));
    }

    private FakeHttpHandler NewServer() =>
        new
        (request =>
            {
                var url = request.Url;

                if (url.StartsWith("https://kamori.goats.dev/Dalamud/Release/VersionInfo", StringComparison.Ordinal))
                {
                    if (failVersionInfo)
                        return FakeHttpHandler.Text("down", HttpStatusCode.ServiceUnavailable);

                    return FakeHttpHandler.Text
                    (
                        JsonSerializer.Serialize
                        (
                            new
                            {
                                assemblyVersion  = dalamudVersion,
                                supportedGameVer,
                                runtimeVersion,
                                runtimeRequired = true,
                                track           = "release",
                                displayName     = "Release",
                                downloadUrl     = DALAMUD_URL
                            }
                        )
                    );
                }

                if (url == DALAMUD_URL)
                    return Bytes(DalamudZip());

                if (url == $"https://kamori.goats.dev/Dalamud/Release/Runtime/Hashes/{runtimeVersion}")
                {
                    return FakeHttpHandler.Text
                    (
                        JsonSerializer.Serialize
                        (
                            new Dictionary<string, string>
                            {
                                [$"host\\fxr\\{runtimeVersion}\\hostfxr.dll"]                       = Md5("fxr"),
                                [$"shared\\Microsoft.NETCore.App\\{runtimeVersion}\\core.dll"]      = Md5("core"),
                                [$"shared\\Microsoft.WindowsDesktop.App\\{runtimeVersion}\\wd.dll"] = Md5("desktop")
                            }
                        )
                    );
                }

                if (url == $"https://kamori.goats.dev/Dalamud/Release/Runtime/DotNet/{runtimeVersion}")
                    return Bytes(Zip(($"host/fxr/{runtimeVersion}/hostfxr.dll", "fxr"), ($"shared/Microsoft.NETCore.App/{runtimeVersion}/core.dll", "core")));

                if (url == $"https://kamori.goats.dev/Dalamud/Release/Runtime/WindowsDesktop/{runtimeVersion}")
                    return Bytes(Zip(($"shared/Microsoft.WindowsDesktop.App/{runtimeVersion}/wd.dll", "desktop")));

                if (url == "https://kamori.goats.dev/Dalamud/Asset/Meta")
                {
                    return FakeHttpHandler.Text
                    (
                        JsonSerializer.Serialize
                        (
                            new
                            {
                                version    = assetVersion,
                                assets     = new[] { new { url = "u", fileName = "UIRes/logo.png", hash = Sha1("logo" + assetVersion) }, new { url = "u", fileName = "font.ttf", hash = "" } },
                                packageUrl = ASSET_URL
                            }
                        )
                    );
                }

                if (url == ASSET_URL)
                    return Bytes(Zip(("UIRes/logo.png", "logo" + assetVersion), ("font.ttf", "font")));

                return FakeHttpHandler.Text("not found: " + url, HttpStatusCode.NotFound);
            }
        );

    private static HttpResponseMessage Bytes(byte[] bytes) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    private InternationalDalamudUpdater NewUpdater(FakeHttpHandler server, int maxTries = 2) =>
        new
        (
            Paths.AddonDirectory,
            Paths.RuntimeDirectory,
            Paths.AssetDirectory,
            new InternationalDalamudUpdaterOptions
            {
                HttpHandler       = server,
                RolloutBucket     = "Control",
                MaxTries          = maxTries,
                RetryDelay        = TimeSpan.Zero,
                DeleteSettleDelay = TimeSpan.Zero,
                IsGameOpen        = () => false
            }
        );

    private static void RunToEnd(InternationalDalamudUpdater updater)
    {
        updater.Run();
        updater.WaitForCompletion();
    }

    private static int Count(FakeHttpHandler server, string url) =>
        server.Requests.Count(x => x.Url == url);

    #endregion

    [Fact]
    public void FirstRun_DownloadsEverything_IntoInternationalRootOnly()
    {
        // 旁边放一套「国服」目录, 更新后必须原封不动
        var domesticHooks = Directory.CreateDirectory(Path.Combine(root.FullName, "XIVLauncherCN", "addon", "Hooks", "cn-1.2.3"));
        File.WriteAllText(Path.Combine(domesticHooks.FullName, "Dalamud.dll"), "cn");
        Directory.CreateDirectory(Path.Combine(root.FullName, "XIVLauncherCN", "runtime"));
        File.WriteAllText(Path.Combine(root.FullName, "XIVLauncherCN", "runtime", "version"), "cn-runtime");
        Directory.CreateDirectory(Path.Combine(root.FullName, "XIVLauncherCN", "dalamudAssets", "99"));
        var before = Directory.GetFileSystemEntries(Path.Combine(root.FullName, "XIVLauncherCN"), "*", SearchOption.AllDirectories)
                              .Where(x => !x.StartsWith(InternationalRoot.FullName, StringComparison.OrdinalIgnoreCase))
                              .Order()
                              .ToArray();

        var server = NewServer();
        using var updater = NewUpdater(server);
        RunToEnd(updater);

        Assert.Equal(DalamudUpdater.DownloadState.Done, updater.State);
        Assert.Null(updater.EnsurementException);

        var hooks = Path.Combine(InternationalRoot.FullName, "addon", "Hooks", "13.0.0.4");
        Assert.Equal(Path.Combine(hooks, "Dalamud.Injector.exe"), updater.Runner!.FullName);
        Assert.True(updater.Runner.Exists);
        Assert.True(File.Exists(Path.Combine(hooks, "version.json")));
        Assert.True(File.Exists(Path.Combine(InternationalRoot.FullName, "addon", "Hooks", "dev", "Dalamud.dll")));

        Assert.Equal(Path.Combine(InternationalRoot.FullName, "runtime"), updater.Runtime.FullName);
        Assert.Equal("9.0.2", File.ReadAllText(Path.Combine(updater.Runtime.FullName, "version")));
        Assert.True(File.Exists(Path.Combine(updater.Runtime.FullName, "shared", "Microsoft.WindowsDesktop.App", "9.0.2", "wd.dll")));

        Assert.Equal(Path.Combine(InternationalRoot.FullName, "dalamudAssets", "7"), updater.AssetDirectory!.FullName);
        Assert.Equal("7", File.ReadAllText(Path.Combine(InternationalRoot.FullName, "dalamudAssets", "asset.ver")));
        Assert.True(File.Exists(Path.Combine(updater.AssetDirectory.FullName, "UIRes", "logo.png")));

        Assert.Equal("release", updater.ResolvedBranch!.Track);
        Assert.Equal("https://kamori.goats.dev/Dalamud/Release/VersionInfo?track=release&bucket=Control", server.Requests[0].Url);
        Assert.Equal("no-cache", server.Requests[0].Headers["Cache-Control"]);

        // 国服那套目录: 一个文件都没多、没少、没改
        var after = Directory.GetFileSystemEntries(Path.Combine(root.FullName, "XIVLauncherCN"), "*", SearchOption.AllDirectories)
                             .Where(x => !x.StartsWith(InternationalRoot.FullName, StringComparison.OrdinalIgnoreCase))
                             .Order()
                             .ToArray();
        Assert.Equal(before, after);
        Assert.Equal("cn", File.ReadAllText(Path.Combine(domesticHooks.FullName, "Dalamud.dll")));
        Assert.Equal("cn-runtime", File.ReadAllText(Path.Combine(root.FullName, "XIVLauncherCN", "runtime", "version")));
    }

    [Fact]
    public void HostPaths_AreAllUnderInternationalRoot_AndDifferFromDomestic()
    {
        var roaming  = new DirectoryInfo(Path.Combine(root.FullName, "XIVLauncherCN"));
        var paths    = InternationalDalamudService.CreateHostPaths(new DirectoryInfo(Path.Combine(roaming.FullName, "international")), roaming);
        var prefix   = Path.Combine(roaming.FullName, "international");
        var domestic = new[] { Path.Combine(roaming.FullName, "addon"), Path.Combine(roaming.FullName, "runtime"), Path.Combine(roaming.FullName, "dalamudAssets"), roaming.FullName };

        foreach (var directory in new[] { paths.AddonDirectory, paths.RuntimeDirectory, paths.AssetDirectory, paths.ConfigDirectory, paths.LogDirectory })
        {
            Assert.StartsWith(prefix, directory.FullName, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(directory.FullName, domestic, StringComparer.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void SecondRun_UpToDate_DownloadsNothingAgain()
    {
        using (var first = NewUpdater(NewServer()))
            RunToEnd(first);

        var server = NewServer();
        using var second = NewUpdater(server);
        RunToEnd(second);

        Assert.Equal(DalamudUpdater.DownloadState.Done, second.State);
        Assert.Equal(0, Count(server, DALAMUD_URL));
        Assert.Equal(0, Count(server, ASSET_URL));
        Assert.DoesNotContain(server.Requests, x => x.Url.Contains("/Runtime/DotNet/", StringComparison.Ordinal));
        Assert.NotNull(second.Runner);
        Assert.NotNull(second.AssetDirectory);
    }

    [Fact]
    public void CorruptedFile_IsRedownloaded()
    {
        using (var first = NewUpdater(NewServer()))
            RunToEnd(first);

        File.WriteAllText(Path.Combine(InternationalRoot.FullName, "addon", "Hooks", "13.0.0.4", "Dalamud.dll"), "tampered");

        var server = NewServer();
        using var second = NewUpdater(server);
        RunToEnd(second);

        Assert.Equal(DalamudUpdater.DownloadState.Done, second.State);
        Assert.Equal(1, Count(server, DALAMUD_URL));
        Assert.Equal("dalamud", File.ReadAllText(Path.Combine(InternationalRoot.FullName, "addon", "Hooks", "13.0.0.4", "Dalamud.dll")));
    }

    [Fact]
    public void NewDalamudVersion_ReplacesOldVersionDirectory()
    {
        using (var first = NewUpdater(NewServer()))
            RunToEnd(first);

        dalamudVersion = "13.0.0.5";
        using var second = NewUpdater(NewServer());
        RunToEnd(second);

        var hooks = Path.Combine(InternationalRoot.FullName, "addon", "Hooks");
        Assert.Equal(["13.0.0.5", "dev"], Directory.GetDirectories(hooks).Select(Path.GetFileName).Order());
        Assert.Contains("13.0.0.5", second.Runner!.FullName);
    }

    [Fact]
    public void RuntimeVersionChange_RedownloadsRuntime()
    {
        using (var first = NewUpdater(NewServer()))
            RunToEnd(first);

        runtimeVersion = "10.0.1";
        var server = NewServer();
        using var second = NewUpdater(server);
        RunToEnd(second);

        Assert.Equal(DalamudUpdater.DownloadState.Done, second.State);
        Assert.Equal(1, Count(server, "https://kamori.goats.dev/Dalamud/Release/Runtime/DotNet/10.0.1"));
        Assert.Equal(1, Count(server, "https://kamori.goats.dev/Dalamud/Release/Runtime/WindowsDesktop/10.0.1"));
        Assert.Equal("10.0.1", File.ReadAllText(Path.Combine(second.Runtime.FullName, "version")));
        Assert.False(Directory.Exists(Path.Combine(second.Runtime.FullName, "host", "fxr", "9.0.2")));
        Assert.True(File.Exists(Path.Combine(second.Runtime.FullName, "host", "fxr", "10.0.1", "hostfxr.dll")));
    }

    [Fact]
    public void AssetVersionChange_DownloadsNewPackage_AndRemovesOld()
    {
        using (var first = NewUpdater(NewServer()))
            RunToEnd(first);

        assetVersion = 8;
        var server = NewServer();
        using var second = NewUpdater(server);
        RunToEnd(second);

        var assets = Path.Combine(InternationalRoot.FullName, "dalamudAssets");
        Assert.Equal(1, Count(server, ASSET_URL));
        Assert.Equal(Path.Combine(assets, "8"), second.AssetDirectory!.FullName);
        Assert.Equal(["8", "dev"], Directory.GetDirectories(assets).Select(Path.GetFileName).Order());
        Assert.Equal("8", File.ReadAllText(Path.Combine(assets, "asset.ver")));
    }

    [Fact]
    public void AssetFileHashMismatch_RefreshesAssets()
    {
        using (var first = NewUpdater(NewServer()))
            RunToEnd(first);

        File.WriteAllText(Path.Combine(InternationalRoot.FullName, "dalamudAssets", "7", "UIRes", "logo.png"), "broken");

        var server = NewServer();
        using var second = NewUpdater(server);
        RunToEnd(second);

        Assert.Equal(1, Count(server, ASSET_URL));
        Assert.Equal("logo7", File.ReadAllText(Path.Combine(InternationalRoot.FullName, "dalamudAssets", "7", "UIRes", "logo.png")));
    }

    [Fact]
    public void GameOpen_KeepsOldVersionDirectories()
    {
        using (var first = NewUpdater(NewServer()))
            RunToEnd(first);

        dalamudVersion = "13.0.0.6";
        using var second = new InternationalDalamudUpdater
        (
            Paths.AddonDirectory,
            Paths.RuntimeDirectory,
            Paths.AssetDirectory,
            new InternationalDalamudUpdaterOptions { HttpHandler = NewServer(), RolloutBucket = "Control", RetryDelay = TimeSpan.Zero, DeleteSettleDelay = TimeSpan.Zero, IsGameOpen = () => true }
        );
        RunToEnd(second);

        Assert.Equal(["13.0.0.4", "13.0.0.6", "dev"], Directory.GetDirectories(Path.Combine(InternationalRoot.FullName, "addon", "Hooks")).Select(Path.GetFileName).Order());
    }

    [Fact]
    public void ServerDown_EndsInNoIntegrity_AfterMaxTries_AndSessionThrows()
    {
        failVersionInfo = true;
        var server = NewServer();
        using var game = new FakeGameDirectory();
        var service = NewService(server, 3);

        service.Updater.Run();
        var session = service.CreateSession(game.Root, new DalamudLaunchOptions(DalamudLoadMethod.EntryPoint, 0, false, false, false), ClientLanguage.English, "{}");

        Assert.Throws<DalamudRunnerException>(() => session.EnsureReady(game.Root));
        Assert.Equal(DalamudUpdater.DownloadState.NoIntegrity, service.Updater.State);
        Assert.NotNull(service.Updater.EnsurementException);
        Assert.Equal(3, server.Requests.Count);
        Assert.Null(service.Updater.Runner);
    }

    [Fact]
    public void Session_EnsureReady_Ok_WhenSupportedGameVersionMatches()
    {
        using var game = new FakeGameDirectory();
        var service = NewService(NewServer());

        service.Updater.Run();
        var session = service.CreateSession(game.Root, new DalamudLaunchOptions(DalamudLoadMethod.EntryPoint, 0, false, false, false), ClientLanguage.French, "{}");

        Assert.Equal(DalamudSession.DalamudInstallState.Ok, session.EnsureReady(game.Root));
        Assert.True(service.Updater.ReCheckVersion(game.Root));
    }

    [Fact]
    public void Session_EnsureReady_OutOfDate_WhenDalamudDoesNotSupportLocalGameVersion()
    {
        supportedGameVer = "2026.08.01.0000.0000";
        using var game = new FakeGameDirectory();
        var service = NewService(NewServer());

        service.Updater.Run();
        var session = service.CreateSession(game.Root, new DalamudLaunchOptions(DalamudLoadMethod.EntryPoint, 0, false, false, false), ClientLanguage.English, "{}");

        Assert.Equal(DalamudSession.DalamudInstallState.OutOfDate, session.EnsureReady(game.Root));
        Assert.False(service.Updater.ReCheckVersion(game.Root));
        Assert.Equal("2026.08.01.0000.0000", service.Updater.ResolvedBranch!.SupportedGameVer);
    }

    [Fact]
    public void ReCheckVersion_BeforeDone_IsNull()
    {
        using var game = new FakeGameDirectory();
        using var updater = NewUpdater(NewServer());

        Assert.Null(updater.ReCheckVersion(game.Root));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("dev")]
    [InlineData("")]
    public void VersionInfo_WithUnsafeAssemblyVersion_IsRejected(string version)
    {
        dalamudVersion = version;
        using var updater = NewUpdater(NewServer(), 1);
        RunToEnd(updater);

        Assert.Equal(DalamudUpdater.DownloadState.NoIntegrity, updater.State);
        Assert.False(Directory.Exists(Path.Combine(InternationalRoot.FullName, "addon", "Hooks")));
    }

    [Fact]
    public void RolloutBucket_IsPersistedAndReused()
    {
        var server = NewServer();
        using (var updater = new InternationalDalamudUpdater
               (
                   Paths.AddonDirectory,
                   Paths.RuntimeDirectory,
                   Paths.AssetDirectory,
                   new InternationalDalamudUpdaterOptions { HttpHandler = server, RetryDelay = TimeSpan.Zero, DeleteSettleDelay = TimeSpan.Zero, IsGameOpen = () => false }
               ))
            RunToEnd(updater);

        var bucket = File.ReadAllText(Path.Combine(InternationalRoot.FullName, "addon", "rolloutBucket"));
        Assert.Contains(bucket, new[] { "Canary", "Control" });
        Assert.EndsWith("&bucket=" + bucket, server.Requests[0].Url);
    }

    [Fact]
    public void TroubleshootingJson_HasGoatcorpFields()
    {
        using var game = new FakeGameDirectory();

        var json = InternationalDalamudService.BuildTroubleshootingJson(game.Root, DalamudLoadMethod.EntryPoint, 500, true, 1, "1.2.3", "abcdef");
        using var document = JsonDocument.Parse(json);
        var rootElement = document.RootElement;

        string[] expected =
        [
            "When", "IsAutoLogin", "IsUidCache", "DalamudEnabled", "DalamudLoadMethod", "DalamudInjectionDelay", "SteamIntegration", "EncryptArguments",
            "LauncherVersion", "LauncherHash", "Official", "DpiAwareness", "Platform", "ObservedGameVersion", "ObservedEx1Version", "ObservedEx2Version",
            "ObservedEx3Version", "ObservedEx4Version", "ObservedEx5Version", "BckMatch", "IndexIntegrity"
        ];

        Assert.Equal(expected, rootElement.EnumerateObject().Select(x => x.Name));
        Assert.Equal(FakeGameDirectory.GAME_VERSION, rootElement.GetProperty("ObservedGameVersion").GetString());
        Assert.False(rootElement.GetProperty("Official").GetBoolean());
    }

    private InternationalDalamudService NewService(FakeHttpHandler server, int maxTries = 2) =>
        new
        (
            Paths,
            new FakeGameVersionProvider(),
            new InternationalDalamudUpdaterOptions
            {
                HttpHandler       = server,
                RolloutBucket     = "Control",
                MaxTries          = maxTries,
                RetryDelay        = TimeSpan.Zero,
                DeleteSettleDelay = TimeSpan.Zero,
                IsGameOpen        = () => false
            }
        );

    private sealed class FakeGameVersionProvider : IDalamudGameVersionProvider
    {
        public string GetVersion(DirectoryInfo gamePath, bool isBck = false) => Repository.Ffxiv.GetVer(gamePath, isBck);
    }
}

/// <summary>
///     联网核对 goatcorp 的 Dalamud 发行信息接口还在、字段名没变。缺省跳过（DML_NETWORK_SMOKE=1 才跑）, 只取元数据, 不下载任何包。
/// </summary>
[Trait("Category", "InternationalNetwork")]
public sealed class InternationalDalamudNetworkSmokeTests(ITestOutputHelper output)
{
    [NetworkSmokeFact]
    public async Task VersionInfo_And_AssetMeta_HaveExpectedFields()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("XIVLauncherCN");

        var versionJson = await client.GetStringAsync(string.Format(Links.GOATCORP_DALAMUD_VERSION_INFO_URL_FORMAT, "Control"));
        var info        = Newtonsoft.Json.JsonConvert.DeserializeObject<InternationalDalamudVersionInfo>(versionJson)!;

        output.WriteLine($"AssemblyVersion={info.AssemblyVersion} SupportedGameVer={info.SupportedGameVer} RuntimeVersion={info.RuntimeVersion} RuntimeRequired={info.RuntimeRequired} Track={info.Track}");
        output.WriteLine($"DownloadUrl={info.DownloadUrl}");

        Assert.False(string.IsNullOrWhiteSpace(info.AssemblyVersion));
        Assert.False(string.IsNullOrWhiteSpace(info.SupportedGameVer));
        Assert.False(string.IsNullOrWhiteSpace(info.RuntimeVersion));
        Assert.StartsWith("http", info.DownloadUrl);

        using var meta = JsonDocument.Parse(await client.GetStringAsync(Links.GOATCORP_DALAMUD_ASSET_META_URL));
        output.WriteLine($"Asset version={meta.RootElement.GetProperty("version").GetInt32()} assets={meta.RootElement.GetProperty("assets").GetArrayLength()}");

        Assert.True(meta.RootElement.GetProperty("version").GetInt32() > 0);
        Assert.StartsWith("http", meta.RootElement.GetProperty("packageUrl").GetString());
        Assert.True(meta.RootElement.GetProperty("assets")[0].TryGetProperty("fileName", out _));

        using var hashes = JsonDocument.Parse(await client.GetStringAsync(string.Format(Links.GOATCORP_DALAMUD_RUNTIME_HASHES_URL_FORMAT, info.RuntimeVersion)));
        Assert.True(hashes.RootElement.EnumerateObject().Any());
    }
}
