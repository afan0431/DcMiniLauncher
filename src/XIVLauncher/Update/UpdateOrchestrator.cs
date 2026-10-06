using System.Diagnostics;
using System.IO;
using System.Windows;
using Serilog;
using Velopack;
using Velopack.Sources;
using XIVLauncher.CatHost;
using XIVLauncher.Common.Constant;
using XIVLauncher.Common.Http;
using XIVLauncher.Common.Network;
using XIVLauncher.Settings;
using XIVLauncher.Support;
using XIVLauncher.Windows;

namespace XIVLauncher.Update;

internal class UpdateOrchestrator
(
    LauncherSettingsV3          settings,
    INetworkEnvironmentService? networkEnvironmentService = null
)
{
    /// <summary>有游戏在由 Cat 运行时给用户看的提示</summary>
    internal const string CAT_RUNNING_MESSAGE = "有游戏正在由 Cat 运行, 本次先不更新启动器, 稍后再更新";

    private static readonly TimeSpan CatRunningMessageDelay = TimeSpan.FromSeconds(2);

    /// <summary>
    ///     有游戏在由 Cat（无界面启动）运行时不应用更新: Velopack 应用更新会结束安装目录下的所有进程, 包括守护游戏的无界面进程
    /// </summary>
    internal static Func<bool> IsCatHostRunning { get; set; } = CatHostPresence.IsAnyRunning;

    /// <summary>界面版启动器正守着游戏时给用户看的提示</summary>
    internal const string GAME_GUARDED_MESSAGE = "有启动器正守着运行中的游戏, 本次先不更新启动器, 稍后再更新";

    /// <summary>
    ///     有界面版启动器正守着游戏时同样不应用更新: 守护进程被结束后, 游戏退出时没人给 MINIONAPP 报「停机」
    /// </summary>
    internal static Func<bool> IsGameGuarded { get; set; } = GameGuardPresence.IsAnyRunning;

    /// <summary>
    ///     现在应用更新会不会结束正守着游戏的启动器进程（无界面的或界面版的）
    /// </summary>
    internal static bool WouldKillGameGuard() => IsCatHostRunning() || IsGameGuarded();

    public async Task<bool> Run
    (
        bool             downloadPrerelease,
        LoadingDialog?   loadingDialog,
        ChangelogWindow? changelogWindow,
        Action?          beforeShowChangelog = null
    )
    {
        _ = downloadPrerelease;
        _ = settings; // 只在上游「检查失败是否继续」那段用到, 本 fork 检查失败一律继续

        if (await SkipBecauseCatIsRunningAsync(loadingDialog, "检查").ConfigureAwait(false))
            return true;

        try
        {
            var networkEnvironmentTask =
                (networkEnvironmentService ?? NetworkEnvironmentService.Shared).GetCurrentAsync();
            var updateOptions = new UpdateOptions
            {
                ExplicitChannel       = "win",
                AllowVersionDowngrade = false
            };

            var downloader         = new XLHttpClientFileDownloader();
            var networkEnvironment = await networkEnvironmentTask;
            var updateBaseURL      = Links.LAUNCHER_DISTRIBUTE_BASE_URL;

            Log.Information
            (
                "网络区域 {Region}, 使用 XIVLauncher 发行源 {UpdateBaseURL}",
                networkEnvironment.Region,
                updateBaseURL
            );

            var updateSource = new SimpleWebSource
            (
                updateBaseURL,
                downloader
            );

            var updateManager = new UpdateManager(updateSource, updateOptions);
            loadingDialog?.SetMessage("正在检查启动器更新...");
            var newRelease = await updateManager.CheckForUpdatesAsync();

            if (newRelease == null)
                return true;

            var changelog = newRelease.TargetFullRelease.NotesMarkdown;
            loadingDialog?.SetMessage("正在下载启动器更新...");
            loadingDialog?.ReportProgress(0);

            await updateManager.DownloadUpdatesAsync
            (
                newRelease,
                progress =>
                {
                    loadingDialog?.SetMessage("正在下载启动器更新...");
                    loadingDialog?.ReportProgress(progress);
                }
            );

            // 下载期间可能有游戏刚由 Cat 起来; 已下载的更新留到下次启动再装
            if (await SkipBecauseCatIsRunningAsync(loadingDialog, "安装").ConfigureAwait(false))
                return true;

            loadingDialog?.SetMessage("正在安装启动器更新...");
            loadingDialog?.ReportProgress(100);

            if (changelogWindow == null)
            {
                Log.Error("更新日志窗口为空，直接进入更新安装流程。");
                updateManager.ApplyUpdatesAndRestart(newRelease);
                return false;
            }

            try
            {
                await changelogWindow.Dispatcher.InvokeAsync
                (() =>
                    {
                        beforeShowChangelog?.Invoke();
                        changelogWindow.UpdateVersion(newRelease.TargetFullRelease.Version.ToString());
                        changelogWindow.ChangeLogText.Markdown = changelog;
                        changelogWindow.ShowDialog();
                    }
                );

                updateManager.ApplyUpdatesAndRestart(newRelease);
                return false;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "无法显示更新日志窗口，直接进入更新安装流程。");
                updateManager.ApplyUpdatesAndRestart(newRelease);
                return false;
            }
        }
        catch (Exception ex)
        {
            // DcMiniLauncher: 更新源是 GitHub, 偶尔连不上; 不是 Velopack 安装的目录（直接解压 / 本地编译）也会走到这里。
            // 上游在这里弹错并退出, 对我们来说「检查不了更新」不该挡住进游戏 —— 记日志, 照常启动。
            Log.Warning(ex, "启动器更新检查失败, 继续使用当前版本: {Error}", GetUpdateFailureMessage(ex));
            return true;
        }
    }

    /// <summary>无界面更新的结果: 有启动器正守着游戏, 没有更新</summary>
    internal const string SILENT_GUARDED = "guarded";

    /// <summary>无界面更新的结果: 安装目录下还有别的启动器开着, 没有更新</summary>
    internal const string SILENT_IN_USE = "inUse";

    /// <summary>无界面更新的结果: 本进程不在 Velopack 安装目录里, 无从更新</summary>
    internal const string SILENT_NOT_INSTALLED = "notInstalled";

    /// <summary>无界面更新的结果: 已是最新</summary>
    internal const string SILENT_UP_TO_DATE = "upToDate";

    /// <summary>无界面更新的结果: 新版本已下载, 本进程退出后安装</summary>
    internal const string SILENT_APPLYING = "applying";

    /// <summary>
    ///     安装目录下除本进程外是否还有启动器在运行。测试时可替换
    /// </summary>
    internal static Func<bool> IsInstallInUse { get; set; } = IsInstallInUseByOthers;

    /// <summary>
    ///     不显示任何窗口地检查、下载并安装启动器更新, 装完不重启启动器。
    ///     安装会结束安装目录下的所有启动器进程, 所以有启动器正守着游戏、或安装目录下还有别的启动器开着时都不装
    ///     （已下载的留到下次）; 从安装目录之外的副本运行的无界面进程不受影响。返回结果代码。
    /// </summary>
    internal static async Task<string> RunSilentAsync()
    {
        if (WouldKillGameGuard())
            return SILENT_GUARDED;

        if (IsInstallInUse())
            return SILENT_IN_USE;

        var updateManager = new UpdateManager
        (
            new SimpleWebSource(Links.LAUNCHER_DISTRIBUTE_BASE_URL, new XLHttpClientFileDownloader()),
            new UpdateOptions
            {
                ExplicitChannel       = "win",
                AllowVersionDowngrade = false
            }
        );

        if (!updateManager.IsInstalled)
            return SILENT_NOT_INSTALLED;

        var newRelease = await updateManager.CheckForUpdatesAsync().ConfigureAwait(false);

        if (newRelease == null)
            return SILENT_UP_TO_DATE;

        Log.Information("[CatUpdate] 发现新版本 {Version}, 开始下载", newRelease.TargetFullRelease.Version);
        await updateManager.DownloadUpdatesAsync(newRelease).ConfigureAwait(false);

        // 下载期间可能有游戏刚起来、或有人打开了启动器
        if (WouldKillGameGuard())
            return SILENT_GUARDED;

        if (IsInstallInUse())
            return SILENT_IN_USE;

        updateManager.WaitExitThenApplyUpdates(newRelease, true, false);
        return SILENT_APPLYING;
    }

    /// <summary>
    ///     安装目录（本进程所在目录的上一级）下是否有除本进程外的启动器进程; 读不到某个进程的路径时按有算
    /// </summary>
    private static bool IsInstallInUseByOthers()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..")).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var name = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "XIVLauncherCN.exe");

        foreach (var process in Process.GetProcessesByName(name))
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId)
                    continue;

                try
                {
                    if (process.MainModule?.FileName is not { } image || image.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "[CatUpdate] 读不到启动器进程 {Pid} 的路径, 按安装目录在用处理", process.Id);
                    return true;
                }
            }
        }

        return false;
    }

    private static async Task<bool> SkipBecauseCatIsRunningAsync(LoadingDialog? loadingDialog, string step)
    {
        if (IsCatHostRunning())
        {
            Log.Information("有游戏正在由 Cat 运行, 跳过启动器更新{Step}", step);
            loadingDialog?.SetMessage(CAT_RUNNING_MESSAGE);
        }
        else if (IsGameGuarded())
        {
            Log.Information("有启动器正守着运行中的游戏, 跳过启动器更新{Step}", step);
            loadingDialog?.SetMessage(GAME_GUARDED_MESSAGE);
        }
        else
            return false;

        await Task.Delay(CatRunningMessageDelay).ConfigureAwait(false);
        return true;
    }

    internal static string GetUpdateFailureMessage(Exception exception) =>
        exception switch
        {
            TimeoutException timeoutException => timeoutException.Message,
            not null when exception.FindHttpRequestException() is { StatusCode: not null } httpRequestException => (int)httpRequestException.StatusCode switch
            {
                403 or 444 or 522 => $"更新源返回错误状态码 {(int)httpRequestException.StatusCode}{Environment.NewLine}{httpRequestException.Message}",
                _                 => $"更新请求失败, 状态码 {(int)httpRequestException.StatusCode}{Environment.NewLine}{httpRequestException.Message}"
            },
            OperationCanceledException => "更新请求已被取消。",
            _                          => exception.Message
        };
}
