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
///         <item>WeGame 号带了 weGameLogin: 先进 waitingWeGameLogin 阶段停一会儿（当作员工在 WeGame 里登录）, 回到 preparing 后照常继续; 期间 close 则发 launch.failed{cancelled}</item>
///         <item>
///             再带了 weGameScan: 停的这段时间里发一条假的二维码验证（weGame.challenge kind=qrcode）, 结束前发 weGame.challengeCleared。
///             账号名以 <c>scanfail:</c> 开头: 不发二维码, 改发 weGame.scanSwitchFailed。
///             账号名以 <c>sms:</c> 开头: 二维码之后再发一条设备验证短信（kind=sms）, 一直等到收到 weGame.confirmSms 才发 weGame.challengeCleared 并继续
///         </item>
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

    /// <summary>模拟自动切扫码页失败的前缀</summary>
    public const string SCAN_FAIL_PREFIX = "scanfail:";

    /// <summary>模拟扫码后还要设备验证短信的前缀</summary>
    public const string SMS_PREFIX = "sms:";

    /// <summary>模拟的二维码内容（占位链接, 扫了没有任何效果）</summary>
    public const string SIMULATED_QR_LINK = "https://example.invalid/wegame-simulated-qrcode";

    /// <summary>模拟的二维码验证编号</summary>
    public const string SIMULATED_QR_CHALLENGE_ID = "q-1";

    /// <summary>模拟的设备验证编号</summary>
    public const string SIMULATED_SMS_CHALLENGE_ID = "s-1";

    private readonly CancellationTokenSource closeCts = new();
    private readonly object                  stateLock = new();

    private Process? placeholder;
    private bool     closeRequested;

    private TaskCompletionSource? smsConfirmed;

    /// <summary>每个阶段之间的停顿</summary>
    public TimeSpan StepDelay { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>带了 weGameLogin 时在 waitingWeGameLogin 阶段停多久</summary>
    public TimeSpan WeGameLoginDelay { get; init; } = TimeSpan.FromSeconds(8);

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
    public CatAcceptResult ConfirmWeGameSms(string challengeId)
    {
        TaskCompletionSource? pending;

        lock (stateLock)
            pending = smsConfirmed;

        if (pending == null || challengeId != SIMULATED_SMS_CHALLENGE_ID)
            return CatAcceptResult.Rejected(CatCodes.NOT_RUNNING, "当前没有在等 WeGame 的设备验证（模拟）");

        pending.TrySetResult();
        return CatAcceptResult.Ok();
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

        // 模拟模式不查账号库、不登录, 渠道只体现在这条日志里; 国际服带来的密码不看也不输出
        reporter.Log("information", $"模拟模式: 不登录、不启动真游戏（渠道: {CatPlatforms.DisplayName(request.Channel)}）");
        reporter.Stage(CatStages.PREPARING);
        await Task.Delay(StepDelay, token).ConfigureAwait(false);

        if (request is { IsWeGame: true, WeGameLogin: true })
        {
            reporter.Stage(CatStages.WAITING_WE_GAME_LOGIN);
            await WaitForWeGameLoginAsync(request, reporter, token).ConfigureAwait(false);
            reporter.Stage(CatStages.PREPARING);
        }

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

    /// <summary>
    ///     模拟等员工或客户在 WeGame 里登录的那段时间; 带了 weGameScan 时按账号名前缀发假的验证通知
    /// </summary>
    private async Task WaitForWeGameLoginAsync(CatLaunchRequest request, ICatLaunchReporter reporter, CancellationToken token)
    {
        if (request.WeGameScan is not { } scan)
        {
            await Task.Delay(WeGameLoginDelay, token).ConfigureAwait(false);
            return;
        }

        if (request.AccountName.StartsWith(SCAN_FAIL_PREFIX, StringComparison.Ordinal))
        {
            reporter.WeGameScanSwitchFailed(CatWeGameScans.Name(scan));
            await Task.Delay(WeGameLoginDelay, token).ConfigureAwait(false);
            return;
        }

        var     quarter = WeGameLoginDelay / 4;
        string? open    = null;

        try
        {
            await Task.Delay(quarter, token).ConfigureAwait(false);

            open = SIMULATED_QR_CHALLENGE_ID;
            reporter.WeGameChallenge
            (
                new CatWeGameChallenge
                (
                    CatWeGameChallengeKinds.QRCODE,
                    SIMULATED_QR_CHALLENGE_ID,
                    Convert.ToBase64String(new CatZxingQrCodec().EncodePng(SIMULATED_QR_LINK)),
                    SIMULATED_QR_LINK,
                    120
                )
            );

            await Task.Delay(quarter * 2, token).ConfigureAwait(false);

            reporter.WeGameChallengeCleared(SIMULATED_QR_CHALLENGE_ID);
            open = null;

            if (request.AccountName.StartsWith(SMS_PREFIX, StringComparison.Ordinal))
            {
                var confirmed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

                lock (stateLock)
                    smsConfirmed = confirmed;

                open = SIMULATED_SMS_CHALLENGE_ID;
                reporter.WeGameChallenge
                (
                    new CatWeGameChallenge
                    (
                        CatWeGameChallengeKinds.SMS,
                        SIMULATED_SMS_CHALLENGE_ID,
                        Code: "SIMULATED01",
                        Phone: "1069070069",
                        Text: "设备环境发生变更，需进行验证。请编辑手机短信：SIMULATED01 发送到号码：1069070069（模拟）"
                    )
                );

                await confirmed.Task.WaitAsync(token).ConfigureAwait(false);

                reporter.WeGameChallengeCleared(SIMULATED_SMS_CHALLENGE_ID);
                open = null;
            }

            await Task.Delay(quarter, token).ConfigureAwait(false);
        }
        finally
        {
            lock (stateLock)
                smsConfirmed = null;

            // 等的途中被 close 取消: 没清的验证也报成已清除, 与真实启动一致
            if (open != null)
                reporter.WeGameChallengeCleared(open);
        }
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
