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
///         <item>minion.cardFingerprint 为 <c>0000000000000000</c>: 发 launch.failed{minionNotConfigured}（模拟本机找不到 Minion 安装目录）</item>
///         <item>WeGame 号带了 weGameLogin: 先进 waitingWeGameLogin 阶段停一会儿（当作员工在 WeGame 里登录）, 回到 preparing 后照常继续; 期间 close 则发 launch.failed{cancelled}</item>
///         <item>
///             再带了 weGameScan: 停的这段时间里发一条假的二维码验证（weGame.challenge kind=qrcode）, 结束前发 weGame.challengeCleared。
///             账号名以 <c>scanfail:</c> 开头: 不发二维码, 改发 weGame.scanSwitchFailed。
///             账号名以 <c>sms:</c> 开头: 二维码之后再发一条设备验证短信（kind=sms）, 一直等到收到 weGame.confirmSms 才发 weGame.challengeCleared 并继续
///         </item>
///         <item>
///             launch 带了 authOnly（只登录）: 上面的等登录与失败前缀照旧, 之后发 launch.authorized 结束, 不起占位进程;
///             weGameAccountId 取 launch 下发的用户号, 没带时为 <see cref="SIMULATED_WE_GAME_ACCOUNT_ID" />; captured = launch 带了 weGameLogin（模拟里一律当作在 WeGame 里登录过）
///         </item>
///         <item>
///             launch 带了 autoEnter（国际服除外）: running 之后按真实顺序发自动进入角色的事件; 带 Minion 的照常在 running 之前挂好。账号名前缀决定走哪种:
///             其它任何账号名 = 单角色直进（enteringLobby → game.characters → enteringWorld → game.character → inWorld）;
///             <c>chars:</c> = 三个角色等人选（awaitingCharacterChoice → game.characters{needsChoice} → 收到 selectCharacter 后继续进入）;
///             <c>travel:</c> = 角色超域在别的大区（game.characters → switchingArea → game.characters → enteringWorld → game.character → inWorld）;
///             <c>queue:</c> = 进入时排队（queueing 带 queuePosition 3 → 1, 再回到 enteringWorld）;
///             <c>stop:&lt;停手码&gt;</c> = 停手（enteringLobby → game.autoEnterStopped → running; 不写停手码按 moduleUnavailable）
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

    /// <summary>自动进入角色: 多个角色等人选的前缀</summary>
    public const string CHARACTERS_PREFIX = "chars:";

    /// <summary>自动进入角色: 角色超域在别的大区、要换大区的前缀</summary>
    public const string TRAVEL_PREFIX = "travel:";

    /// <summary>自动进入角色: 进入时排队的前缀</summary>
    public const string QUEUE_PREFIX = "queue:";

    /// <summary>自动进入角色: 停手的前缀, 后面跟停手码</summary>
    public const string STOP_PREFIX = "stop:";

    /// <summary>自动进入角色: 没指定角色名时模拟角色的名字</summary>
    public const string SIMULATED_CHARACTER_NAME = "模拟角色";

    /// <summary>自动进入角色: 模拟角色的 contentId 前缀, 后面是序号 1–3</summary>
    public const string SIMULATED_CONTENT_ID_PREFIX = "400000000000000";

    /// <summary>模拟的二维码内容（占位链接, 扫了没有任何效果）</summary>
    public const string SIMULATED_QR_LINK = "https://example.invalid/wegame-simulated-qrcode";

    /// <summary>模拟的二维码验证编号</summary>
    public const string SIMULATED_QR_CHALLENGE_ID = "q-1";

    /// <summary>模拟的设备验证编号</summary>
    public const string SIMULATED_SMS_CHALLENGE_ID = "s-1";

    /// <summary>只登录时 launch 没带 weGameAccountId 用的 WeGame 用户号</summary>
    public const string SIMULATED_WE_GAME_ACCOUNT_ID = "10000000000000000";

    private readonly CancellationTokenSource closeCts = new();
    private readonly object                  stateLock = new();

    private Process? placeholder;
    private bool     closeRequested;

    private TaskCompletionSource? smsConfirmed;

    private TaskCompletionSource<string>?    characterChosen;
    private IReadOnlyList<CatCharacterInfo>  characterChoices = [];
    private Task                             autoEnterTask    = Task.CompletedTask;
    private string?                          autoEnterStage;

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

        reporter.Stage(LastAutoEnterStage ?? CatStages.RUNNING);
    }

    /// <inheritdoc />
    public CatAcceptResult SelectCharacter(string contentId)
    {
        lock (stateLock)
        {
            if (characterChosen == null)
                return CatAcceptResult.Rejected(CatCodes.NOT_RUNNING, "当前没有在等人选角色（模拟）");

            if (characterChoices.All(x => x.ContentId != contentId))
                return CatAcceptResult.Rejected(CatCodes.INVALID_PARAMS, "角色列表里没有这个角色（模拟）");

            characterChosen.TrySetResult(contentId);
            return CatAcceptResult.Ok();
        }
    }

    private string? LastAutoEnterStage
    {
        get
        {
            lock (stateLock)
                return autoEnterStage;
        }
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
            reporter.Failed(CatCodes.MINION_NOT_CONFIGURED, "本机找不到 Minion 安装目录（模拟）");
            return CatLaunchHost.EXIT_LAUNCH_FAILED;
        }

        if (request.AuthOnly)
        {
            reporter.Authorized(request.WeGameAccountId ?? SIMULATED_WE_GAME_ACCOUNT_ID, request.WeGameLogin);
            return CatHostRuntime.EXIT_OK;
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
                await autoEnterTask.ConfigureAwait(false);
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
        await autoEnterTask.ConfigureAwait(false);

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
        // 上一个占位进程的自动进入角色随进程结束而结束, 等它收完尾再起新的
        await autoEnterTask.ConfigureAwait(false);

        lock (stateLock)
            autoEnterStage = null;

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
                await AttachMinionAsync(failAgent, reporter, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (closeCts.IsCancellationRequested)
        {
            return process;
        }

        reporter.Stage(CatStages.RUNNING);

        if (request.AutoEnter)
            autoEnterTask = RunAutoEnterAsync(request, process, reporter, token);

        return process;
    }

    private async Task AttachMinionAsync(string? failAgent, ICatLaunchReporter reporter, CancellationToken token)
    {
        reporter.Stage(CatStages.ATTACHING_MINION);
        await Task.Delay(StepDelay, token).ConfigureAwait(false);

        if (failAgent == CatAgentKinds.MINION)
            reporter.Agent(CatAgentKinds.MINION, false, CatCodes.ATTACH_FAILED, "MinionLauncher 报错（模拟）");
        else
            reporter.Agent(CatAgentKinds.MINION, true);
    }

    /// <summary>
    ///     模拟自动进入角色: 按账号名前缀发与真实编排同样顺序的事件; 占位进程一结束就停, 不再发任何事件
    /// </summary>
    private async Task RunAutoEnterAsync(CatLaunchRequest request, Process process, ICatLaunchReporter reporter, CancellationToken token)
    {
        using var alive = CancellationTokenSource.CreateLinkedTokenSource(token);
        var exited = process.WaitForExitAsync(alive.Token);

        void Stage(string stage)
        {
            lock (stateLock)
                autoEnterStage = stage;

            reporter.Stage(stage);
        }

        async Task StepAsync()
        {
            if (await Task.WhenAny(exited, Task.Delay(StepDelay, alive.Token)).ConfigureAwait(false) == exited || process.HasExited)
                throw new OperationCanceledException();
        }

        try
        {
            var account = request.AccountName;

            Stage(CatStages.ENTERING_LOBBY);
            await StepAsync().ConfigureAwait(false);

            if (account.StartsWith(STOP_PREFIX, StringComparison.Ordinal))
            {
                var code = account[STOP_PREFIX.Length..];
                reporter.AutoEnterStopped(string.IsNullOrWhiteSpace(code) ? CatAutoEnterStopCodes.MODULE_UNAVAILABLE : code, "模拟的自动进入角色停手");
                Stage(CatStages.RUNNING);
            }
            else
            {
                CatCharacterInfo entered;

                if (account.StartsWith(CHARACTERS_PREFIX, StringComparison.Ordinal))
                {
                    var choices = new[]
                    {
                        SimulatedCharacter(1, SIMULATED_CHARACTER_NAME + "一", "LaNuoXiYa"),
                        SimulatedCharacter(2, SIMULATED_CHARACTER_NAME + "二", "HongYuHai"),
                        SimulatedCharacter(3, SIMULATED_CHARACTER_NAME + "三", "MengYaChi")
                    };
                    var chosen = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

                    lock (stateLock)
                    {
                        characterChoices = choices;
                        characterChosen  = chosen;
                    }

                    try
                    {
                        Stage(CatStages.AWAITING_CHARACTER_CHOICE);
                        reporter.Characters(true, choices);

                        if (await Task.WhenAny(exited, chosen.Task).ConfigureAwait(false) == exited)
                            throw new OperationCanceledException();

                        var contentId = await chosen.Task.ConfigureAwait(false);
                        entered = choices.First(x => x.ContentId == contentId);
                    }
                    finally
                    {
                        lock (stateLock)
                            characterChosen = null;
                    }
                }
                else if (account.StartsWith(TRAVEL_PREFIX, StringComparison.Ordinal))
                {
                    entered = SimulatedCharacter(1, request.CharacterName ?? SIMULATED_CHARACTER_NAME, request.CharacterHomeWorld ?? "LaNuoXiYa") with
                    {
                        CurrentWorld     = "BaiYinXiang",
                        CurrentWorldName = "白银乡",
                        Travelling       = true
                    };

                    reporter.Characters(false, [entered]);
                    Stage(CatStages.SWITCHING_AREA);
                    await StepAsync().ConfigureAwait(false);
                    reporter.Characters(false, [entered]);
                }
                else
                {
                    entered = SimulatedCharacter(1, request.CharacterName ?? SIMULATED_CHARACTER_NAME, request.CharacterHomeWorld ?? "LaNuoXiYa");
                    reporter.Characters(false, [entered]);
                }

                Stage(CatStages.ENTERING_WORLD);
                await StepAsync().ConfigureAwait(false);

                if (account.StartsWith(QUEUE_PREFIX, StringComparison.Ordinal))
                {
                    foreach (var position in new[] { 3, 1 })
                    {
                        lock (stateLock)
                            autoEnterStage = CatStages.QUEUEING;

                        reporter.Queueing(position);
                        await StepAsync().ConfigureAwait(false);
                    }

                    Stage(CatStages.ENTERING_WORLD);
                    await StepAsync().ConfigureAwait(false);
                }

                reporter.Character(entered);
                Stage(CatStages.IN_WORLD);
            }
        }
        catch (OperationCanceledException)
        {
            // 占位进程结束或收到 close
        }
        finally
        {
            await alive.CancelAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     模拟角色; 模拟模式没有服务器表, 只认得这里用到的三个服务器的中文名, 其它的不带中文名
    /// </summary>
    private static CatCharacterInfo SimulatedCharacter(int index, string name, string homeWorld)
    {
        var homeWorldName = homeWorld.ToUpperInvariant() switch
        {
            "LANUOXIYA" => "拉诺西亚",
            "HONGYUHAI" => "红玉海",
            "MENGYACHI" => "萌芽池",
            _           => null
        };

        return new CatCharacterInfo(SIMULATED_CONTENT_ID_PREFIX + index, name, homeWorld, homeWorld, homeWorldName, homeWorldName, false, true);
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
