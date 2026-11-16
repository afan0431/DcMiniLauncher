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
