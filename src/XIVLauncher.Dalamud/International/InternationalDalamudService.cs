using Newtonsoft.Json;
using XIVLauncher.Common;

namespace XIVLauncher.Dalamud.International;

/// <summary>
///     国际服 Dalamud（goatcorp 原版）: 自己的更新器 + 与国服共用的注入器和会话外壳（按原版的取值传参）。
///     与国服的 <see cref="DalamudService" /> 是两个互不相干的实例, 目录由 <paramref name="hostPaths" /> 给定, 必须与国服的完全分开。
/// </summary>
public sealed class InternationalDalamudService
(
    DalamudHostPaths                    hostPaths,
    IDalamudGameVersionProvider         gameVersionProvider,
    InternationalDalamudUpdaterOptions? updaterOptions = null
)
{
    /// <summary>目录</summary>
    public DalamudHostPaths HostPaths { get; } = hostPaths;

    /// <summary>更新器</summary>
    public InternationalDalamudUpdater Updater { get; } =
        new(hostPaths.AddonDirectory, hostPaths.RuntimeDirectory, hostPaths.AssetDirectory, updaterOptions);

    /// <summary>
    ///     国际服整套 Dalamud 目录: 都在 <paramref name="internationalRoot" /> 之下（本体 addon、运行时 runtime、资源 dalamudAssets,
    ///     配置与日志就在根上）, 不与国服共用任何一个目录
    /// </summary>
    public static DalamudHostPaths CreateHostPaths(DirectoryInfo internationalRoot, DirectoryInfo launcherDirectory) =>
        new
        (
            new DirectoryInfo(Path.Combine(internationalRoot.FullName, "addon")),
            new DirectoryInfo(Path.Combine(internationalRoot.FullName, "runtime")),
            new DirectoryInfo(Path.Combine(internationalRoot.FullName, "dalamudAssets")),
            internationalRoot,
            internationalRoot,
            launcherDirectory
        );

    /// <summary>
    ///     建一次启动用的会话: 语言照客户端语言传, 不传国服专有的两个参数, 更新器就绪后核对支持的游戏版本
    /// </summary>
    public DalamudSession CreateSession(DirectoryInfo gamePath, DalamudLaunchOptions launchOptions, ClientLanguage language, string troubleshootingJson)
    {
        Directory.CreateDirectory(HostPaths.ConfigDirectory.FullName);
        Directory.CreateDirectory(HostPaths.LogDirectory.FullName);

        return new DalamudSession
        (
            new DalamudInjector(),
            Updater,
            launchOptions.LoadMethod,
            gamePath,
            HostPaths.ConfigDirectory,
            HostPaths.LogDirectory,
            launchOptions.DelayInitializeMs,
            launchOptions.FakeLogin,
            launchOptions.NoPlugins,
            launchOptions.NoThirdPlugins,
            troubleshootingJson,
            gameVersionProvider,
            DalamudSessionFlavor.Upstream((int)language, Updater.ReCheckVersion)
        );
    }

    /// <summary>
    ///     给原版 Dalamud 的排障信息, 字段照 goatcorp（XIVLauncher/Support/Troubleshooting.cs:154-202）。
    ///     没做的项如实填: 不是官方构建, 没有做索引完整性检查（3 = ReferenceNotFound）。
    /// </summary>
    public static string BuildTroubleshootingJson
    (
        DirectoryInfo     gamePath,
        DalamudLoadMethod loadMethod,
        int               injectionDelayMs,
        bool              encryptArguments,
        int               dpiAwareness,
        string            launcherVersion,
        string            launcherHash
    )
    {
        string Ver(Repository repo, bool isBck = false) => repo.GetVer(gamePath, isBck);

        Repository[] repositories = [Repository.Ffxiv, Repository.Ex1, Repository.Ex2, Repository.Ex3, Repository.Ex4, Repository.Ex5];

        return JsonConvert.SerializeObject
        (
            new
            {
                When                  = DateTime.Now,
                IsAutoLogin           = true,
                IsUidCache            = false,
                DalamudEnabled        = true,
                DalamudLoadMethod     = (int)loadMethod,
                DalamudInjectionDelay = (decimal)injectionDelayMs,
                SteamIntegration      = false,
                EncryptArguments      = encryptArguments,
                LauncherVersion       = launcherVersion,
                LauncherHash          = launcherHash,
                Official              = false,
                DpiAwareness          = dpiAwareness,
                Platform              = 0,
                ObservedGameVersion   = Ver(Repository.Ffxiv),
                ObservedEx1Version    = Ver(Repository.Ex1),
                ObservedEx2Version    = Ver(Repository.Ex2),
                ObservedEx3Version    = Ver(Repository.Ex3),
                ObservedEx4Version    = Ver(Repository.Ex4),
                ObservedEx5Version    = Ver(Repository.Ex5),
                BckMatch              = repositories.All(x => Ver(x) == Ver(x, true)),
                IndexIntegrity        = 3
            }
        );
    }
}
