using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using XIVLauncher.Common.Network;
using XIVLauncher.Dalamud;
using Xunit;

namespace XIVLauncher.Test.Dalamud;

/// <summary>
///     连不上 Dalamud 发行源时: 发行源以前确认过、文件完好、游戏版本没变的本地 Dalamud 照样能起游戏;
///     其余情况（没有记录、文件被改、游戏换了版、还没用来起过游戏）照旧失败
/// </summary>
[Collection(nameof(DalamudConfirmedLocalTests))] // DalamudUpdater 的版本信息是静态的, 不与别的用例并行
public sealed class DalamudConfirmedLocalTests : IDisposable
{
    private const string VERSION = "26-11-13-01";
    private const string RUNTIME = "10.0.1";
    private const string GAME    = "2026.11.03.0000.0000";

    private readonly DirectoryInfo root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"dml-confirmed-{Guid.NewGuid():N}"));

    private DirectoryInfo Addon   => new(Path.Combine(root.FullName, "addon"));
    private DirectoryInfo Hooks   => new(Path.Combine(Addon.FullName, "Hooks"));
    private DirectoryInfo Runtime => new(Path.Combine(root.FullName, "runtime"));
    private DirectoryInfo Assets  => new(Path.Combine(root.FullName, "assets"));

    public void Dispose()
    {
        try
        {
            root.Delete(true);
        }
        catch
        {
            // 临时目录, 删不掉不影响结果
        }
    }

    /// <summary>铺一份完整的本地安装, 返回 hashes.json 的哈希</summary>
    private string Install()
    {
        var versionDir = Directory.CreateDirectory(Path.Combine(Hooks.FullName, VERSION));
        var hashes     = new Dictionary<string, string>();

        foreach (var name in new[] { "Dalamud.Injector.exe", "Dalamud.dll", "ImGuiScene.dll" })
        {
            var bytes = Encoding.UTF8.GetBytes($"{name} body");
            File.WriteAllBytes(Path.Combine(versionDir.FullName, name), bytes);
            hashes[name] = Convert.ToHexString(MD5.HashData(bytes));
        }

        var hashesJson = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(hashes));
        File.WriteAllBytes(Path.Combine(versionDir.FullName, "hashes.json"), hashesJson);

        Directory.CreateDirectory(Runtime.FullName);
        File.WriteAllText(Path.Combine(Runtime.FullName, "version"), RUNTIME);

        foreach (var part in new[] { Path.Combine("host", "fxr"), Path.Combine("shared", "Microsoft.NETCore.App"), Path.Combine("shared", "Microsoft.WindowsDesktop.App") })
        {
            var dir = Directory.CreateDirectory(Path.Combine(Runtime.FullName, part, RUNTIME));
            File.WriteAllText(Path.Combine(dir.FullName, "x.dll"), "x");
        }

        var assets = Directory.CreateDirectory(Path.Combine(Assets.FullName, "6"));
        File.WriteAllText(Path.Combine(assets.FullName, "font.ttf"), "font");
        File.WriteAllText(Path.Combine(Assets.FullName, "asset.ver"), "6");

        return Convert.ToHexString(MD5.HashData(hashesJson));
    }

    private DalamudUpdater OfflineUpdater() =>
        new(Addon, Runtime, Assets, new FixedNetwork(), new HttpClient(new RefusingHandler()));

    private static DalamudUpdater RunToEnd(DalamudUpdater updater)
    {
        updater.Run(true);
        updater.WaitForCompletion();
        return updater;
    }

    private DalamudSession Session(DalamudUpdater updater, string gameVersion) =>
        new
        (
            new DalamudInjector(),
            updater,
            DalamudLoadMethod.EntryPoint,
            root,
            root,
            root,
            0,
            false,
            false,
            false,
            "{}",
            new FixedGameVersion(gameVersion)
        );

    [Fact]
    public void Offline_WithConfirmedLocal_SameGame_IsReady()
    {
        var hash = Install();
        new DalamudConfirmedLocal(VERSION, hash, RUNTIME, GAME).Write(Hooks);

        var updater = RunToEnd(OfflineUpdater());

        Assert.Equal(DalamudUpdater.DownloadState.Done, updater.State);
        Assert.True(updater.UsingConfirmedLocal);
        Assert.Equal(Path.Combine(Hooks.FullName, VERSION, "Dalamud.Injector.exe"), updater.Runner!.FullName);
        Assert.Equal(Path.Combine(Assets.FullName, "6"), updater.AssetDirectory!.FullName);
        Assert.Equal(DalamudSession.DalamudInstallState.Ok, Session(updater, GAME).EnsureReady(root));
    }

    [Fact]
    public void Offline_GameVersionChanged_Refuses()
    {
        var hash = Install();
        new DalamudConfirmedLocal(VERSION, hash, RUNTIME, GAME).Write(Hooks);

        var updater = RunToEnd(OfflineUpdater());

        var ex = Assert.Throws<DalamudRunnerException>(() => Session(updater, "2026.12.01.0000.0000").EnsureReady(root));
        Assert.Contains("不一定适用", ex.Message);
    }

    [Fact]
    public void Offline_WithoutRecord_Fails()
    {
        Install();

        Assert.Equal(DalamudUpdater.DownloadState.NoIntegrity, RunToEnd(OfflineUpdater()).State);
    }

    [Fact]
    public void Offline_NeverLaunchedWithThisVersion_Fails()
    {
        var hash = Install();
        new DalamudConfirmedLocal(VERSION, hash, RUNTIME, null).Write(Hooks);

        Assert.Equal(DalamudUpdater.DownloadState.NoIntegrity, RunToEnd(OfflineUpdater()).State);
    }

    [Fact]
    public void Offline_TamperedFile_Fails()
    {
        var hash = Install();
        new DalamudConfirmedLocal(VERSION, hash, RUNTIME, GAME).Write(Hooks);
        File.WriteAllText(Path.Combine(Hooks.FullName, VERSION, "Dalamud.dll"), "changed");

        Assert.Equal(DalamudUpdater.DownloadState.NoIntegrity, RunToEnd(OfflineUpdater()).State);
    }

    [Fact]
    public void Offline_RuntimeMissing_Fails()
    {
        var hash = Install();
        new DalamudConfirmedLocal(VERSION, hash, RUNTIME, GAME).Write(Hooks);
        File.WriteAllText(Path.Combine(Runtime.FullName, "version"), "9.0.0");

        Assert.Equal(DalamudUpdater.DownloadState.NoIntegrity, RunToEnd(OfflineUpdater()).State);
    }

    [Fact]
    public void Confirm_SameVersion_KeepsGameVersion_NewVersion_ClearsIt()
    {
        new DalamudConfirmedLocal(VERSION, "H1", RUNTIME, GAME).Write(Hooks);

        DalamudConfirmedLocal.Confirm(Hooks, VERSION, "H1", RUNTIME);
        Assert.Equal(GAME, DalamudConfirmedLocal.Read(Hooks)!.GameVersion);

        DalamudConfirmedLocal.Confirm(Hooks, "26-12-01-01", "H2", RUNTIME);
        Assert.Null(DalamudConfirmedLocal.Read(Hooks)!.GameVersion);

        DalamudConfirmedLocal.ConfirmGameVersion(Hooks, "26-12-01-01", "2026.12.01.0000.0000");
        Assert.Equal("2026.12.01.0000.0000", DalamudConfirmedLocal.Read(Hooks)!.GameVersion);
    }

    private sealed class RefusingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("远程主机强迫关闭了一个现有的连接。");
    }

    private sealed class FixedNetwork : INetworkEnvironmentService
    {
        private static readonly NetworkEnvironmentInfo Info = new(NetworkRegion.ChineseMainland, "CN", DateTimeOffset.UtcNow);

        public Task<NetworkEnvironmentInfo> GetCurrentAsync(CancellationToken cancellationToken = default) => Task.FromResult(Info);

        public Task<NetworkEnvironmentInfo> RefreshAsync(CancellationToken cancellationToken = default) => Task.FromResult(Info);
    }

    private sealed class FixedGameVersion(string version) : IDalamudGameVersionProvider
    {
        public string GetVersion(DirectoryInfo gamePath, bool isBck = false) => version;
    }
}
