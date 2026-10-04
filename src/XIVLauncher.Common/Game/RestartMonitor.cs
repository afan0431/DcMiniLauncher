using System.Diagnostics;
using System.Management;
using Serilog;

namespace XIVLauncher.Common.Game;

public class RestartMonitor
{
    private const string DALAMUD_CRASH_HANDLER_PROCESS_NAME = "DalamudCrashHandler.exe";

    // 托管重启退出码协议, 须与 DalamudCrashHandler.cpp 保持一致
    private const uint MANAGED_EXIT_RESTART_DEFAULT    = 0x12345670; // 沿用启动器原始模式
    private const uint MANAGED_EXIT_RESTART_NORMAL     = 0x12345671; // 正常重启
    private const uint MANAGED_EXIT_RESTART_NO_3P      = 0x12345672; // 禁用第三方插件
    private const uint MANAGED_EXIT_RESTART_NO_PLUGINS = 0x12345673; // 禁用全部插件
    private const uint MANAGED_EXIT_RESTART_NO_DALAMUD = 0x12345674; // 禁用 Dalamud

    private static readonly TimeSpan CrashHandlerDiscoveryTimeout      = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CrashHandlerDiscoveryPollInterval = TimeSpan.FromMilliseconds(200);

    public readonly record struct RestartOptions
    (
        bool ForceNoDalamud,
        bool NoThirdPlugins,
        bool NoPlugins
    )
    {
        public static RestartOptions Normal => default;
    }

    /// <summary>
    ///     无人值守时的额外控制; 不传时行为与界面版一致（无限等崩溃处理器）
    /// </summary>
    public sealed record MonitorOptions
    {
        /// <summary>游戏退出后崩溃处理器最多再等多久（崩溃对话框没人选）; 超时结束崩溃处理器并按不重启处理。null = 一直等</summary>
        public TimeSpan? CrashHandlerExitTimeout { get; init; }

        /// <summary>游戏退出后崩溃处理器在这段时间内还没退出, 才算「崩溃对话框在等人」</summary>
        public TimeSpan CrashDialogGrace { get; init; } = TimeSpan.FromSeconds(3);

        /// <summary>游戏已退出而崩溃处理器还开着时回调, 参数为游戏进程号</summary>
        public Action<int>? CrashHandlerOutlivedGame { get; init; }

        /// <summary>崩溃处理器超时被结束时回调</summary>
        public Action? CrashHandlerTimedOut { get; init; }

        /// <summary>触发后不再重启: 游戏退出时不等崩溃处理器的决定, 还开着就结束它</summary>
        public CancellationToken StopToken { get; init; }
    }

    public async Task MonitorAsync
    (
        FFXIVProcess                              gameProcess,
        RestartOptions                            defaultRestartOptions,
        Func<RestartOptions, Task<FFXIVProcess?>> restartProcessAsync,
        CancellationToken                         cancellationToken = default,
        MonitorOptions?                           monitorOptions    = null
    )
    {
        // 必须在游戏存活时就抓住崩溃处理器句柄: 重启 / 杀死路径下它终止游戏后会立即退出,
        // 等游戏退出再去找会与其退出竞态
        using var crashHandler = await TryAcquireCrashHandlerAsync(gameProcess, cancellationToken).ConfigureAwait(false);

        await gameProcess.UnderlyingProcess.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        if (crashHandler == null)
        {
            Log.Information("游戏进程退出, 未检测到 Dalamud 崩溃处理器, 不接管重启");
            return;
        }

        if (monitorOptions == null)
        {
            // 崩溃处理器在终止游戏后才退出, 其退出码即重启决策
            // 崩溃对话框可能长时间停留等待用户选择, 故此处无限等待(可取消)
            await crashHandler.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        else if (!await WaitForCrashHandlerAsync(crashHandler, gameProcess.ProcessID, monitorOptions, cancellationToken).ConfigureAwait(false))
            return;

        if (monitorOptions?.StopToken.IsCancellationRequested == true)
        {
            Log.Information("已要求停止守护, 不处理崩溃处理器的重启决定");
            return;
        }

        var exitCode = (uint)crashHandler.ExitCode;

        if (MapRestartDecision(exitCode, defaultRestartOptions) is not { } restartOptions)
        {
            Log.Information("Dalamud 崩溃处理器未请求重启, 退出码: 0x{ExitCode:X}", exitCode);
            return;
        }

        Log.Information
        (
            "Dalamud 崩溃处理器请求重启, 退出码: 0x{ExitCode:X}, 模式: 禁用 Dalamud = {ForceNoDalamud}, 禁用第三方插件 = {NoThirdPlugins}, 禁用全部插件 = {NoPlugins}",
            exitCode,
            restartOptions.ForceNoDalamud,
            restartOptions.NoThirdPlugins,
            restartOptions.NoPlugins
        );

        using var restartedProcess = await restartProcessAsync(restartOptions).ConfigureAwait(false);

        if (restartedProcess == null)
            Log.Error("重启游戏失败");
    }

    /// <summary>
    ///     有限等待崩溃处理器退出。返回 true = 它已自己退出（可读退出码）; false = 超时或要求停止, 已结束它, 按不重启处理。
    /// </summary>
    private static async Task<bool> WaitForCrashHandlerAsync(Process crashHandler, int gamePid, MonitorOptions options, CancellationToken cancellationToken)
    {
        if (await WaitForExitAsync(crashHandler, options.CrashDialogGrace, options.StopToken, cancellationToken).ConfigureAwait(false))
            return true;

        if (!options.StopToken.IsCancellationRequested)
        {
            Log.Warning("游戏进程 {GamePid} 已退出, Dalamud 崩溃处理器仍开着（崩溃对话框在等人选择）", gamePid);
            options.CrashHandlerOutlivedGame?.Invoke(gamePid);

            if (await WaitForExitAsync(crashHandler, options.CrashHandlerExitTimeout, options.StopToken, cancellationToken).ConfigureAwait(false))
                return true;
        }

        if (options.StopToken.IsCancellationRequested)
            Log.Information("已要求停止守护, 结束仍开着的 Dalamud 崩溃处理器");
        else
        {
            Log.Warning("Dalamud 崩溃处理器 {Timeout} 内没有退出, 结束它并按不重启处理", options.CrashHandlerExitTimeout);
            options.CrashHandlerTimedOut?.Invoke();
        }

        try
        {
            if (!crashHandler.HasExited)
                crashHandler.Kill();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "结束 Dalamud 崩溃处理器失败");
        }

        return false;
    }

    /// <summary>
    ///     等进程退出; timeout 为 null 时只受 stopToken / cancellationToken 限制。返回是否已退出。
    /// </summary>
    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan? timeout, CancellationToken stopToken, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stopToken, cancellationToken);

        if (timeout is { } limit)
            linked.CancelAfter(limit);

        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return process.HasExited;
        }
    }

    private static RestartOptions? MapRestartDecision(uint exitCode, RestartOptions defaultRestartOptions) =>
        exitCode switch
        {
            MANAGED_EXIT_RESTART_DEFAULT    => defaultRestartOptions,
            MANAGED_EXIT_RESTART_NORMAL     => RestartOptions.Normal,
            MANAGED_EXIT_RESTART_NO_3P      => new RestartOptions(false, true,  false),
            MANAGED_EXIT_RESTART_NO_PLUGINS => new RestartOptions(false, false, true),
            MANAGED_EXIT_RESTART_NO_DALAMUD => new RestartOptions(true,  false, false),
            _                               => null
        };

    private static async Task<Process?> TryAcquireCrashHandlerAsync(FFXIVProcess gameProcess, CancellationToken cancellationToken)
    {
        var gamePid  = gameProcess.ProcessID;
        var deadline = DateTime.Now + CrashHandlerDiscoveryTimeout;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (gameProcess.UnderlyingProcess.HasExited)
                return null;

            if (TryFindCrashHandlerProcess(gamePid) is { } crashHandler)
            {
                Log.Information("已捕获 Dalamud 崩溃处理器进程 {ProcessId}", crashHandler.Id);
                return crashHandler;
            }

            if (DateTime.Now >= deadline)
            {
                Log.Information("等待 Dalamud 崩溃处理器超时, 不接管重启");
                return null;
            }

            await Task.Delay(CrashHandlerDiscoveryPollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private static Process? TryFindCrashHandlerProcess(int gamePid)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher
            (
                "SELECT ProcessId FROM Win32_Process " + $"WHERE Name = '{DALAMUD_CRASH_HANDLER_PROCESS_NAME}' AND ParentProcessId = {gamePid}"
            );

            foreach (var o in searcher.Get())
            {
                using var managementObject = (ManagementObject)o;

                if (managementObject["ProcessId"] is not uint processId)
                    continue;

                try
                {
                    var crashHandler = Process.GetProcessById((int)processId);

                    // 立即打开并缓存进程句柄: 仅 WaitForExitAsync 打开的临时 SYNCHRONIZE 句柄会被释放,
                    // 进程退出后再读 ExitCode 会尝试重开句柄并抛异常; 趁存活时缓存句柄, 内核会为其保留退出码
                    _ = crashHandler.SafeHandle;
                    return crashHandler;
                }
                catch (ArgumentException)
                {
                    // 进程已退出, 继续尝试下一个
                }
                catch (InvalidOperationException)
                {
                    // 进程在打开句柄前退出, 继续尝试下一个
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "查询 Dalamud 崩溃处理器进程失败");
        }

        return null;
    }
}
