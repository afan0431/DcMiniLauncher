using System.Diagnostics;
using System.IO;
using XIVLauncher.Minion;

namespace XIVLauncher.CatHost;

/// <summary>
///     模拟模式: 不登录、不启动真游戏, 用一个占位子进程代替游戏, 按真实时序发事件。accountName 的前缀决定要模拟的情况:
///     <list type="bullet">
///         <item><c>fail:&lt;失败码&gt;</c>: 发对应的 launch.failed（如 <c>fail:authorizationRequired</c>）</item>
///         <item><c>netfail</c>: 发 launch.failed{networkError}（快速登录遇到网络错误, 不换密码）</item>
///         <item><c>agentfail:dalamud</c> / <c>agentfail:minion</c>: 游戏照常起来, 该代理注入失败</item>
///         <item><c>crash:</c> 或 <c>crash:restart</c>: 运行一会儿后崩溃并重启一次（game.restarted, 进程号变）</item>
///         <item><c>crash:dialog</c>: 运行一会儿后崩溃, 崩溃对话框没人选 → game.crashed, 等待超时后 game.exited{reason:"crashDialogTimeout"}</item>
///         <item>minion.cardFingerprint 为 <c>0000000000000000</c>: 发 launch.failed{minionCardNotFound}</item>
///         <item>占位进程被结束时发 game.exited; close 时结束占位进程并发 game.exited{reason:"closed"}; 随后本进程退出</item>
///     </list>
/// </summary>
public sealed class CatSimulatedGameRunner : ICatGameRunner
{
    /// <summary>模拟中代表"找不到卡"的指纹</summary>
    public const string MISSING_CARD_FINGERPRINT = "0000000000000000";

    /// <summary>模拟启动失败的前缀</summary>
    public const string FAIL_PREFIX = "fail:";

    /// <summary>模拟网络错误的前缀</summary>
    public const string NETWORK_FAIL_PREFIX = "netfail";

    /// <summary>模拟代理注入失败的前缀</summary>
    public const string AGENT_FAIL_PREFIX = "agentfail:";

    /// <summary>模拟崩溃的前缀</summary>
    public const string CRASH_PREFIX = "crash:";

    /// <summary>崩溃对话框没人选的模拟</summary>
    public const string CRASH_DIALOG = "dialog";

    private readonly CancellationTokenSource closeCts = new();
    private readonly object                  stateLock = new();

    private Process? placeholder;
    private bool     closeRequested;

    /// <summary>每个阶段之间的停顿</summary>
    public TimeSpan StepDelay { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>崩溃前运行多久</summary>
    public TimeSpan CrashAfter { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>覆盖 launch 里的崩溃对话框等待时长（测试用）</summary>
    public TimeSpan? CrashDialogTimeout { get; init; }

    /// <summary>占位进程的创建方式, 默认 <c>ping -t 127.0.0.1</c></summary>
    public Func<Process> PlaceholderFactory { get; init; } = StartDefaultPlaceholder;

    /// <inheritdoc />
    public async Task<int> RunAsync(CatLaunchRequest request, ICatLaunchReporter reporter, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, closeCts.Token);
        var token = linked.Token;

        try
        {
            return await RunCoreAsync(request, reporter, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (closeCts.IsCancellationRequested && placeholder == null)
        {
            reporter.Failed(CatCodes.CANCELLED, "启动途中收到关闭请求, 没有起游戏（模拟）");
            return CatLaunchHost.EXIT_LAUNCH_FAILED;
        }
    }

    /// <inheritdoc />
    public async Task InjectAsync(bool dalamud, bool minion, bool force, ICatLaunchReporter reporter, CancellationToken cancellationToken)
    {
        if (placeholder is not { HasExited: false })
        {
            if (dalamud)
                reporter.Agent(CatAgentKinds.DALAMUD, false, CatCodes.NOT_RUNNING, "占位进程已退出");
            if (minion)
                reporter.Agent(CatAgentKinds.MINION, false, CatCodes.NOT_RUNNING, "占位进程已退出");
            return;
        }

        await Task.Delay(StepDelay, cancellationToken).ConfigureAwait(false);

        if (dalamud)
            reporter.Agent(CatAgentKinds.DALAMUD, true);
        if (minion)
            reporter.Agent(CatAgentKinds.MINION, true);

        reporter.Stage(CatStages.RUNNING);
    }

    /// <inheritdoc />
    public Task CloseAsync(TimeSpan gracefulTimeout)
    {
        Process? current;

        lock (stateLock)
        {
            closeRequested = true;
            current        = placeholder;
        }

        closeCts.Cancel();
        TryKill(current);
        return Task.CompletedTask;
    }

    private async Task<int> RunCoreAsync(CatLaunchRequest request, ICatLaunchReporter reporter, CancellationToken token)
    {
        var account = request.AccountName;

        reporter.Log("information", "模拟模式: 不登录、不启动真游戏");
        reporter.Stage(CatStages.PREPARING);
        await Task.Delay(StepDelay, token).ConfigureAwait(false);

        if (account.StartsWith(FAIL_PREFIX, StringComparison.Ordinal))
        {
            var code = account[FAIL_PREFIX.Length..];
            reporter.Failed(string.IsNullOrWhiteSpace(code) ? CatCodes.LAUNCH_FAILED : code, "模拟的启动失败");
            return CatLaunchHost.EXIT_LAUNCH_FAILED;
        }

        if (account.StartsWith(NETWORK_FAIL_PREFIX, StringComparison.Ordinal))
        {
            reporter.Failed(CatCodes.NETWORK_ERROR, "连不上盛趣登录服务器, 稍后重试即可（模拟）");
            return CatLaunchHost.EXIT_LAUNCH_FAILED;
        }

        if (request.Minion && request.CardFingerprint == MISSING_CARD_FINGERPRINT)
        {
            reporter.Failed(CatCodes.MINION_CARD_NOT_FOUND, "本机 Minion Accounts.json 里找不到这张卡对应的行（模拟）");
            return CatLaunchHost.EXIT_LAUNCH_FAILED;
        }

        var failAgent = account.StartsWith(AGENT_FAIL_PREFIX, StringComparison.Ordinal) ? account[AGENT_FAIL_PREFIX.Length..] : null;
        var crashMode = account.StartsWith(CRASH_PREFIX, StringComparison.Ordinal) ? account[CRASH_PREFIX.Length..] : null;

        var process = await StartGameAsync(request, null, failAgent, reporter, token).ConfigureAwait(false);

        if (crashMode != null && await WaitOrExitAsync(process, CrashAfter).ConfigureAwait(false))
        {
            var crashedPid = process.Id;
            TryKill(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);

            if (crashMode == CRASH_DIALOG)
            {
                reporter.Crashed(crashedPid);

                var timeout = CrashDialogTimeout ?? request.CrashDialogTimeout;

                try
                {
                    await Task.Delay(timeout, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (closeCts.IsCancellationRequested)
                {
                    reporter.Exited(crashedPid, process.ExitCode, CatExitReasons.CLOSED);
                    process.Dispose();
                    return CatHostRuntime.EXIT_OK;
                }

                reporter.Exited(crashedPid, process.ExitCode, CatExitReasons.CRASH_DIALOG_TIMEOUT);
                process.Dispose();
                return CatHostRuntime.EXIT_OK;
            }

            process.Dispose();

            try
            {
                process = await StartGameAsync(request, crashedPid, failAgent, reporter, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (closeCts.IsCancellationRequested)
            {
                reporter.Exited(crashedPid, null, CatExitReasons.CLOSED);
                return CatHostRuntime.EXIT_OK;
            }
        }

        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);

        int? exitCode;

        try
        {
            exitCode = process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            exitCode = null;
        }

        bool closed;

        lock (stateLock)
            closed = closeRequested;

        reporter.Exited(process.Id, exitCode, closed ? CatExitReasons.CLOSED : null);
        process.Dispose();
        return CatHostRuntime.EXIT_OK;
    }

    private async Task<Process> StartGameAsync(CatLaunchRequest request, int? restartedFromPid, string? failAgent, ICatLaunchReporter reporter, CancellationToken token)
    {
        if (request.Dalamud)
        {
            reporter.Stage(CatStages.UPDATING_DALAMUD);
            await Task.Delay(StepDelay, token).ConfigureAwait(false);
        }

        reporter.Stage(CatStages.STARTING);
        await Task.Delay(StepDelay, token).ConfigureAwait(false);

        var process = PlaceholderFactory();
        bool closeNow;

        lock (stateLock)
        {
            placeholder = process;
            closeNow    = closeRequested;
        }

        var startedAt = MinionOccupancy.GetProcessStartedAt(process);

        if (restartedFromPid is { } oldPid)
            reporter.Restarted(oldPid, process.Id, startedAt);
        else
            reporter.Started(process.Id, startedAt);

        if (closeNow)
        {
            TryKill(process);
            return process;
        }

        try
        {
            if (request.Dalamud)
            {
                reporter.Stage(CatStages.INJECTING);
                await Task.Delay(StepDelay, token).ConfigureAwait(false);

                if (failAgent == CatAgentKinds.DALAMUD)
                    reporter.Agent(CatAgentKinds.DALAMUD, false, CatCodes.DALAMUD_UNAVAILABLE, "游戏进程里没有等到 Dalamud 加载（模拟）");
                else
                    reporter.Agent(CatAgentKinds.DALAMUD, true);
            }

            if (request.Minion)
            {
                reporter.Stage(CatStages.ATTACHING_MINION);
                await Task.Delay(StepDelay, token).ConfigureAwait(false);

                if (failAgent == CatAgentKinds.MINION)
                    reporter.Agent(CatAgentKinds.MINION, false, CatCodes.ATTACH_FAILED, "MinionLauncher 报错（模拟）");
                else
                    reporter.Agent(CatAgentKinds.MINION, true);
            }
        }
        catch (OperationCanceledException) when (closeCts.IsCancellationRequested)
        {
            return process;
        }

        reporter.Stage(CatStages.RUNNING);
        return process;
    }

    /// <summary>
    ///     等一段时间; 期间进程退出了返回 false
    /// </summary>
    private static async Task<bool> WaitOrExitAsync(Process process, TimeSpan delay)
    {
        using var timeout = new CancellationTokenSource(delay);

        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return false;
        }
        catch (OperationCanceledException)
        {
            return true;
        }
    }

    private static void TryKill(Process? process)
    {
        try
        {
            if (process is { HasExited: false })
                process.Kill();
        }
        catch (Exception)
        {
            // 已退出
        }
    }

    private static Process StartDefaultPlaceholder()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName               = Path.Combine(Environment.SystemDirectory, "PING.EXE"),
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            RedirectStandardInput  = true
        };
        startInfo.ArgumentList.Add("-t");
        startInfo.ArgumentList.Add("127.0.0.1");

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动占位进程");

        // 占位进程的输出不能进外壳读的标准输出, 这里读掉丢弃
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived  += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }
}
