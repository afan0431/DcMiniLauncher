using System.IO;
using Serilog;
using Velopack;
using XIVLauncher.Account;
using XIVLauncher.CatHost;
using XIVLauncher.Common.Constant;
using XIVLauncher.Dalamud;
using XIVLauncher.Settings;
using XIVLauncher.Startup;
using XIVLauncher.Support;
using XIVLauncher.Update;

namespace XIVLauncher;

/// <summary>
///     无界面启动入口（<c>--cat-launch</c>）: 不显示窗口、不检查自动更新、不弹任何对话框,
///     读 stdin 第一行握手后起命名管道服务, 一个进程只服务一个游戏, 游戏退出后进程退出。
/// </summary>
public partial class App
{
    private static readonly TimeSpan CatBootstrapTimeout = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan CatNoLaunchTimeout = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan CatFinalDeliveryTimeout = TimeSpan.FromSeconds(5);

    private async Task RunCatHostAsync()
    {
        int exitCode;

        try
        {
            exitCode = await RunCatHostCoreAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[CatHost] 无界面启动失败");
            exitCode = CatHostRuntime.EXIT_UNHANDLED;
        }

        if (CatHostRuntime.Host is { } host && !await host.DrainEventsAsync(CatFinalDeliveryTimeout).ConfigureAwait(false))
            Log.Warning("[CatHost] 退出前事件队列没有清空");

        if (CatHostRuntime.Server is { } server)
        {
            if (!await server.WaitForDeliveryAsync(CatFinalDeliveryTimeout).ConfigureAwait(false))
                Log.Warning("[CatHost] 退出前没能把事件送到外壳（外壳未连接）");

            server.Dispose();
        }

        CatHostPresence.Release();
        Log.Information("[CatHost] 进程退出, 退出码 {ExitCode}", exitCode);
        await Log.CloseAndFlushAsync().ConfigureAwait(false);
        Environment.Exit(exitCode);
    }

    /// <summary>
    ///     无界面更新入口（<c>--cat-self-update</c>）: 检查、下载并安装启动器更新后退出, 不显示窗口、不重启启动器。
    ///     由 Cat 工作台在后台调用 —— 自动上号的电脑上没人打开启动器窗口, 界面版启动时的更新检查轮不到。
    /// </summary>
    private static async Task RunCatSelfUpdateAsync()
    {
        var exitCode = 0;

        try
        {
            var args = CatHostMode.CommandLineArgs;

            if (CatHostMode.GetOption(args, "--roamingPath") is { Length: > 0 } roamingPath)
                Paths.OverrideRoamingPath(roamingPath);

            LogInit.Setup(Path.Combine(Paths.RoamingPath, "output.log"), [.. args]);
            Log.Information("========================================================");
            Log.Information("[CatUpdate] 无界面检查启动器更新 (v{Version} - {Hash})", AppUtil.GetAssemblyVersion(), AppUtil.GetGitHash());

            // 更新管理器要靠它认出安装目录; 已下载的更新不在这里自动应用（那会绕过下面的检查并重启出界面）
            VelopackApp.Build().SetAutoApplyOnStartup(false).Run();

            var outcome = await UpdateOrchestrator.RunSilentAsync().ConfigureAwait(false);
            Log.Information("[CatUpdate] 结果: {Outcome}", outcome);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[CatUpdate] 无界面更新失败");
            exitCode = 1;
        }

        await Log.CloseAndFlushAsync().ConfigureAwait(false);
        Environment.Exit(exitCode);
    }

    private async Task<int> RunCatHostCoreAsync()
    {
        var args = CatHostMode.CommandLineArgs;

        if (CatHostMode.GetOption(args, "--roamingPath") is { Length: > 0 } roamingPath)
            Paths.OverrideRoamingPath(roamingPath);

        LogInit.Setup(Path.Combine(Paths.RoamingPath, "output.log"), [.. args]);
        Log.Information("========================================================");
        Log.Information
        (
            "[CatHost] 无界面启动 (v{Version} - {Hash}){Simulate}",
            AppUtil.GetAssemblyVersion(),
            AppUtil.GetGitHash(),
            CatHostMode.IsSimulate ? " 模拟模式" : string.Empty
        );

        // 运行期间让界面版知道有游戏在由 Cat 运行, 不要应用启动器更新;
        // 从安装目录之外的副本运行时更新结束不到本进程, 不拦
        if (CatHostMode.IsDetached)
            Log.Information("[CatHost] 从安装目录之外的副本运行, 不拦启动器更新: {Directory}", AppContext.BaseDirectory);
        else
            CatHostPresence.Hold();

        var bootstrapLine = await ReadBootstrapLineAsync().ConfigureAwait(false);

        if (!CatHostRuntime.TryParseBootstrap(bootstrapLine, out var bootstrap, out var bootstrapError))
        {
            Log.Error("[CatHost] {Error}", bootstrapError);
            return CatHostRuntime.EXIT_BOOTSTRAP_FAILED;
        }

        var redactor = new CatLogRedactor();
        redactor.Register(bootstrap!.Token);

        var initialization = CatHostMode.IsSimulate ? Task.CompletedTask : Task.Run(InitializeCatHostContextAsync);

        ICatGameRunner runner = CatHostMode.IsSimulate
                                    ? new CatSimulatedGameRunner()
                                    : new CatRealGameRunner(redactor, () => initialization);

        // 渠道要等收到 launch 才知道: 国服（盛趣 / WeGame）仍用上面那个启动器, 国际服另用一个互不相干的类, 它的代码不会进国服路径
        ICatGameRunner SelectRunner(CatLaunchRequest launchRequest)
        {
            if (CatHostMode.IsSimulate || !launchRequest.IsInternational)
                return runner;

            return new CatInternationalGameRunner(redactor, new CatInternationalRealEnvironment(() => initialization));
        }

        CatRpcServer? server = null;
        var host = new CatLaunchHost(SelectRunner, (method, parameters) => server!.NotifyAsync(method, parameters), redactor);
        server = new CatRpcServer(bootstrap.PipeName, bootstrap.Token, host, AppUtil.GetAssemblyVersion());

        try
        {
            server.Listen();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[CatHost] 无法创建命名管道 {PipeName}", bootstrap.PipeName);
            server.Dispose();
            return CatHostRuntime.EXIT_BOOTSTRAP_FAILED;
        }

        CatHostRuntime.Server = server;
        CatHostRuntime.Host   = host;

        _ = Task.Run(() => server.RunAsync(CancellationToken.None));
        Log.Information("[CatHost] 管道已就绪: {PipeName}", bootstrap.PipeName);

        if (await Task.WhenAny(host.Completion, Task.Delay(CatNoLaunchTimeout)).ConfigureAwait(false) != host.Completion && !host.HasLaunch)
        {
            Log.Warning("[CatHost] {Minutes} 分钟内没有收到 launch, 退出", CatNoLaunchTimeout.TotalMinutes);
            return CatHostRuntime.EXIT_NO_LAUNCH;
        }

        return await host.Completion.ConfigureAwait(false);
    }

    private static async Task<string?> ReadBootstrapLineAsync()
    {
        // stdin 读完第一行后保持打开但不再读取; 外壳关闭 stdin 不代表要退出
        var read = Task.Run(() => Console.In.ReadLine());

        if (await Task.WhenAny(read, Task.Delay(CatBootstrapTimeout)).ConfigureAwait(false) != read)
        {
            Log.Error("[CatHost] {Seconds} 秒内 stdin 没有收到握手", CatBootstrapTimeout.TotalSeconds);
            return null;
        }

        return await read.ConfigureAwait(false);
    }

    private async Task InitializeCatHostContextAsync()
    {
        // 与界面启动用同一份设置和账号库, 但只读设置、不写回, 也不跑自动更新
        var settings       = LauncherSettingsV3.Load(Paths.GetConfigPath());
        var accountManager = new AccountManager(settings);
        var credResult     = await accountManager.InitializeCredProviderAsync(settings.CredType).ConfigureAwait(false);

        if (!credResult.Succeeded)
            throw new InvalidOperationException(credResult.UserMessage ?? "自动登录加密方式初始化失败");

        var context = new StartupContext
        {
            Dispatcher          = Dispatcher,
            Settings            = settings,
            AccountManager      = accountManager,
            CredTypeApplyResult = credResult
        };

        context.Dalamud = new DalamudService
        (
            new DalamudHostPaths
            (
                new DirectoryInfo(Path.Combine(Paths.RoamingPath, "addon")),
                new DirectoryInfo(Path.Combine(Paths.RoamingPath, "runtime")),
                new DirectoryInfo(Path.Combine(Paths.RoamingPath, "dalamudAssets")),
                new DirectoryInfo(Paths.RoamingPath),
                new DirectoryInfo(Paths.RoamingPath),
                new DirectoryInfo(AppContext.BaseDirectory)
            ),
            new CatDalamudProgressSink(),
            new AppDalamudGameVersionProvider(),
            new AppDalamudTroubleshootingProvider()
        );

        StartupContext = context;
        Log.Information("[CatHost] 设置与账号库已加载, 账号 {Count} 个", accountManager.Accounts.Count);
    }
}

/// <summary>
///     无界面启动时 Dalamud 更新进度只写日志, 不显示窗口
/// </summary>
internal sealed class CatDalamudProgressSink : IDalamudProgressSink
{
    public void ShowLoading() =>
        Log.Information("[CatHost] Dalamud 正在更新");

    public void HideLoading() =>
        Log.Information("[CatHost] Dalamud 更新等待结束");

    public void SetLoadingMessage(string message) =>
        Log.Information("[CatHost] Dalamud: {Message}", message);

    public void ReportLoadingProgress(long? size, long downloaded, double? progress)
    {
    }
}
