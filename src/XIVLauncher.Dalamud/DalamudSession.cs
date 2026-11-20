using System.Diagnostics;
using Serilog;
using XIVLauncher.Common.Game;

namespace XIVLauncher.Dalamud;

public class DalamudSession
(
    IDalamudRunner              injector,
    IDalamudUpdater             updater,
    DalamudLoadMethod           loadMethod,
    DirectoryInfo               gamePath,
    DirectoryInfo               configDirectory,
    DirectoryInfo               logPath,
    int                         injectionDelay,
    bool                        fakeLogin,
    bool                        noPlugin,
    bool                        noThirdPlugin,
    string                      troubleshootingData,
    IDalamudGameVersionProvider gameVersionProvider,
    DalamudSessionFlavor?       flavor = null
)
{
    private readonly DalamudSessionFlavor flavor = flavor ?? DalamudSessionFlavor.Default;

    public DalamudInstallState EnsureReady(DirectoryInfo gamePathDir)
    {
        Log.Information("[HOOKS] DalamudSession::EnsureReady(gp:{0})", gamePathDir.FullName);

        if (updater.State != DalamudUpdater.DownloadState.Done)
            updater.ShowLoading();

        updater.WaitForCompletion();

        if (updater.State == DalamudUpdater.DownloadState.NoIntegrity)
        {
            updater.HideLoading();
            throw new DalamudRunnerException("Dalamud 完整性检测或更新反复失败, 请检查你的本地网络环境", updater.EnsurementException);
        }

        if (updater.State != DalamudUpdater.DownloadState.Done)
            throw new DalamudRunnerException("Dalamud 更新器未能进入就绪状态");

        if (updater.Runner == null || !updater.Runner.Exists)
            throw new DalamudRunnerException("Dalamud 本地注入文件不存在, 请重新启动 XIVLauncher 以开始完整性检测与下载流程");

        if (updater is DalamudUpdater cnUpdater)
            CheckConfirmedGameVersion(cnUpdater, gamePathDir);

        // 国服不核对（flavor 缺省时 IsGameVersionSupported 为 null, 这一段不执行）
        if (flavor.IsGameVersionSupported is { } isSupported)
        {
            var applicable = isSupported(gamePathDir) ?? throw new DalamudRunnerException("无法核对 Dalamud 支持的游戏版本");

            if (!applicable)
            {
                Log.Error("[HOOKS] Dalamud 支持的游戏版本与本地游戏版本不一致");
                return DalamudInstallState.OutOfDate;
            }
        }

        return DalamudInstallState.Ok;
    }

    /// <summary>
    ///     国服: 没连上发行源、用的是本地确认过的版本时, 游戏必须还是确认时那一版, 否则本地 Dalamud 不一定适用;
    ///     连上了发行源就记下这次的游戏版本, 供以后连不上时核对
    /// </summary>
    private void CheckConfirmedGameVersion(DalamudUpdater cnUpdater, DirectoryInfo gamePathDir)
    {
        string? gameVersion;

        try
        {
            gameVersion = gameVersionProvider.GetVersion(gamePathDir);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[HOOKS] 读不出游戏版本");
            gameVersion = null;
        }

        if (!cnUpdater.UsingConfirmedLocal)
        {
            if (!string.IsNullOrWhiteSpace(gameVersion))
                cnUpdater.ConfirmGameVersion(gameVersion);
            return;
        }

        if (string.IsNullOrWhiteSpace(gameVersion) || gameVersion != cnUpdater.ConfirmedGameVersion)
        {
            updater.HideLoading();
            throw new DalamudRunnerException
            (
                $"连不上 Dalamud 发行源, 本地 Dalamud {DalamudUpdater.Version} 是在游戏 {cnUpdater.ConfirmedGameVersion} 时确认的, 现在游戏是 {gameVersion ?? "未知版本"}, 不一定适用",
                cnUpdater.EnsurementException
            );
        }

        Log.Warning("[HOOKS] 连不上 Dalamud 发行源, 游戏仍是 {GameVersion}, 用本地确认过的 Dalamud {Version}", gameVersion, DalamudUpdater.Version);
    }

    public void InjectGame(int gamePid, bool safeMode = false)
    {
        Log.Information("[HOOKS] DalamudSession::InjectGame(gp:{0})", gamePath.FullName);

        var startInfo = CreateStartInfo();

        var environment = new Dictionary<string, string>
        {
            ["DALAMUD_RUNTIME"]          = updater.Runtime.FullName,
            ["DOTNET_ROOT"]              = updater.Runtime.FullName,
            ["DOTNET_MULTILEVEL_LOOKUP"] = "0"
        };

        DalamudInjector.Inject(updater.Runner!, gamePid, environment, startInfo, safeMode, noThirdPlugin, true);
    }

    public Process LaunchGame(FileInfo gameExe, string gameArgs, IDictionary<string, string> environment)
    {
        Log.Information("[Dalamud Session] 开始运行, 游戏路径: {0}", gamePath.FullName);

        var startInfo = CreateStartInfo();

        if (loadMethod != DalamudLoadMethod.ACLonly)
            Log.Information("[HOOKS] DelayInitializeMs: {0}", startInfo.DelayInitializeMs);

        switch (loadMethod)
        {
            case DalamudLoadMethod.EntryPoint:
                Log.Verbose("[HOOKS] Now running OEP rewrite");
                break;

            case DalamudLoadMethod.ACLonly:
                Log.Verbose("[HOOKS] Now running ACL-only fix without injection");
                break;
        }

        // 解除多开限制与注入器开游戏同锁串行: 几个号同时上号时, 别的启动器会去读本机正被注入器挂起改写的游戏进程, 启动器崩溃、注入失败 (2026-11-16 PC2)
        var process = GameInstanceLimit.Launch(() => injector.Run(updater.Runner!, fakeLogin, noPlugin, noThirdPlugin, gameExe, gameArgs, environment, loadMethod, startInfo));

        updater.HideLoading();

        if (loadMethod != DalamudLoadMethod.ACLonly)
            Log.Information("[HOOKS] Started dalamud!");

        return process ?? throw new DalamudRunnerException("无法启动游戏进程");
    }

    private DalamudStartInfo CreateStartInfo()
    {
        var ingamePluginPath = Path.Combine(configDirectory.FullName, "installedPlugins");
        Directory.CreateDirectory(ingamePluginPath);

        if (updater.AssetDirectory == null || updater.Runner == null)
            throw new DalamudRunnerException("Dalamud 资源尚未准备完成");

        return new DalamudStartInfo
        {
            PluginDirectory         = ingamePluginPath,
            ConfigurationPath       = Path.Combine(configDirectory.FullName, "dalamudConfig.json"),
            LoggingPath             = logPath.FullName,
            AssetDirectory          = updater.AssetDirectory.FullName,
            GameVersion             = gameVersionProvider.GetVersion(gamePath),
            WorkingDirectory        = updater.Runner.Directory?.FullName ?? updater.Runner.DirectoryName ?? Environment.CurrentDirectory,
            DelayInitializeMs       = injectionDelay,
            TroubleshootingPackData = troubleshootingData,
            LauncherDirectory       = Environment.CurrentDirectory,
            ClientLanguage          = flavor.ClientLanguage,
            PassLauncherDirectory   = flavor.PassLauncherDirectory,
            ManagedRestart          = flavor.ManagedRestart
        };
    }

    public enum DalamudInstallState
    {
        Ok,
        OutOfDate
    }
}
