using System.Windows;
using Serilog;
using Velopack;
using Velopack.Sources;
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
