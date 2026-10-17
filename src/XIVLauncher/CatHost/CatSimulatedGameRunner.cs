using System.Diagnostics;
using System.IO;
using XIVLauncher.DCTravel;
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
///         <item>
///             上号自动开工（外壳经 setJob 交任务单）: 占位进程起来后开一个本机监听（随机端口, 提供 <c>GET /cat/job?pid=</c>、<c>POST /cat/progress</c>）,
///             写 <c>dctravel-&lt;占位进程号&gt;.port</c>, 并发一条 launcher.log 写明地址。进世界（带 autoEnter 为 inWorld, 否则 running）后
///             由 <see cref="CatSimulatedAfan" /> 每 <see cref="AfanStep" /> 推进一步: 没有任务单报 waiting; 有任务单先报一次 waiting（核对角色）,
///             desired 为 run 时, 有 startShot 为 true 且要拍的项就在之后的第一条回报里带开工图（每个占位进程只拍这一次）:
///             要开工时这条是只带开工图、不推进的 running, 没有可自动做的项时是 idle, 都做完了时是 done;
///             练级项（含 routine 为 null 的）按职业分组, 战斗职业（含 classJobId 为 null 的）共用一张、生产采集职业（ClassJob 8–18）共用一张,
///             金碟币（mgp）与糖豆人声誉（fallguys-rep）共用一张, 勾选类等不拍, 每项各带一条 kind=start、同一张图的 file 相同, 可自动做的项只拍勾上的;
///             再按 order 把 routine 不为 null 且勾上的项（selected 为 true, 任务单没有这个字段按 true）一项项做完（running, current 每步加 (target − 起点)/5, 勾选类三步完成）, 一项做完且 shots.completion
///             为 true 时在截图目录写 <c>&lt;pid&gt;-&lt;seq&gt;.png</c> 并带在回报的 shots 里（progressAt 不为空时开工后先带一张 progress 图）,
///             勾上的项全部做完报 done（没勾的项不看, 去勾的项不再推进）; desired 为 pause / stop 报 paused / stopped; desired 为 finish 时走完正在做的这一把再报 stopped（不在一把里立即报）;
///             没有勾上的、也没有做完的可自动做的项报 idle; 没有变化时每 <see cref="AfanHeartbeat" /> 心跳一次。
///             职业等级按任务单里练级项的当前值（ClassJob → ExpArrayIndex 用国服表）连同当前角色每步报一次, 有变化才发 game.levels;
///             不自动进入角色时与真实启动一样, 进世界先报一次 game.character（模拟角色, 名字取 launch 的 character.name）。
///             是否在副本中每步报一次, 有变化才发 game.duty（进世界后第一步必发）: 例程进行中每走 <see cref="CatSimulatedAfan.DUTY_ROUND_STEPS" /> 步算一把、
///             在副本中, 两把之间隔一步不在副本中; 其它状态（没有任务单、waiting、paused、stopped、blocked、done 等）不在副本中。
///             账号名前缀 <c>blocked:</c> = 第一项走两步后报 blocked（noProgress）, 直到任务单 revision 变了才接着做;
///             <c>manual:</c> = 不产生回报（由测试工具往 /cat/progress 发, pid 用占位进程号）, 只报等级;
///             <c>mismatch:</c> = waiting 之后一直报 mismatch（reason.code=error, text 写进的是哪个角色）;
///             <c>unsupported:</c> = 第一项报一次 blocked（reason.code=unsupported, 例程认不出）, 跳过它接着做后面的项;
///             <c>induty:</c> = 是否在副本中一直报 true（任务单照常推进）;
///             <c>dutylost:</c> = 进世界后第 <see cref="CatSimulatedAfan.DUTY_LOST_AFTER_STEPS" /> 步之后模拟掉线回标题
///             <see cref="CatSimulatedAfan.DUTY_LOST_STEPS" /> 步: 这几步读不到等级与副本状态, 接连两步读不到发一次 <c>game.duty { inDuty: null }</c>,
///             之后回到世界照常发（任务单照常推进）。这两个可放在上面四个前缀前面连用, 顺序不限（如 <c>induty:blocked:</c>、<c>dutylost:induty:</c>）。
///             seq 与真 Afan 一样以 Unix 毫秒为起点（13 位）
///         </item>
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

    /// <summary>上号自动开工: 模拟回报中途停下（blocked）的前缀</summary>
    public const string JOB_BLOCKED_PREFIX = "blocked:";

    /// <summary>上号自动开工: 不产生模拟回报（由测试工具往 /cat/progress 发）的前缀</summary>
    public const string JOB_MANUAL_PREFIX = "manual:";

    /// <summary>上号自动开工: 核对角色不符、一直报 mismatch 的前缀</summary>
    public const string JOB_MISMATCH_PREFIX = "mismatch:";

    /// <summary>上号自动开工: 第一项例程认不出（报一次 blocked + unsupported, 跳过它接着做）的前缀</summary>
    public const string JOB_UNSUPPORTED_PREFIX = "unsupported:";

    /// <summary>是否在副本中一直报 true 的前缀（可放在上面几个上号自动开工的前缀前面连用）</summary>
    public const string IN_DUTY_PREFIX = "induty:";

    /// <summary>模拟掉线回标题、副本状态变成未知再恢复的前缀（可放在上面几个上号自动开工的前缀前面连用）</summary>
    public const string DUTY_LOST_PREFIX = "dutylost:";

    private readonly CancellationTokenSource closeCts = new();
    private readonly object                  stateLock = new();
    private readonly List<Task>              afanTasks = [];

    private CatLocalListener? localListener;

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

    /// <summary>上号自动开工: 模拟 Afan 每一步的间隔</summary>
    public TimeSpan AfanStep { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>上号自动开工: 模拟 Afan 没有变化时的心跳间隔</summary>
    public TimeSpan AfanHeartbeat { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>模拟截图写到哪里; 不给用 <see cref="CatShots.Directory" /></summary>
    public string? ShotsDirectory { get; init; }

    /// <summary>端口文件写到哪里; 不给用真实位置（<c>%ProgramData%\DcMiniLauncher</c>）</summary>
    public string? PortFileDirectory { get; init; }

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
        finally
        {
            localListener?.Dispose();
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
            await autoEnterTask.ConfigureAwait(false);
            await ReleaseGameAsync(crashedPid).ConfigureAwait(false);

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
        await autoEnterTask.ConfigureAwait(false);
        await ReleaseGameAsync(process.Id).ConfigureAwait(false);

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

        if (reporter.Jobs is { } jobs)
            await OpenLocalApiAsync(jobs, process, reporter).ConfigureAwait(false);

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

        // 不自动进入角色时没有进世界的信号, running 就当进了世界
        if (request.AutoEnter)
            autoEnterTask = RunAutoEnterAsync(request, process, reporter, token);
        else
            StartSimulatedAfan(request, process, reporter);

        return process;
    }

    /// <summary>
    ///     上号自动开工: 开本机监听（一个进程一个, 崩溃重开后沿用）并写这个占位进程的端口文件, 地址经 launcher.log 告诉外壳
    /// </summary>
    private async Task OpenLocalApiAsync(CatJobChannel jobs, Process process, ICatLaunchReporter reporter)
    {
        if (localListener == null)
        {
            for (var attempt = 1; attempt <= 3 && localListener == null; attempt++)
            {
                var listener = new CatLocalListener(Common.Util.APIHelper.GetAvailablePort(), jobs);

                if (await listener.StartAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false))
                    localListener = listener;
                else
                    listener.Dispose();
            }

            if (localListener == null)
            {
                reporter.Log("warning", "模拟模式: 本机接口没能打开, /cat/job 不可用");
                return;
            }
        }

        var path = PortFilePath(process.Id);

        try
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, localListener.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "[CatHost] 模拟模式写端口文件失败");
        }

        reporter.Log("information", $"模拟模式: 本机接口 http://127.0.0.1:{localListener.Port}/cat/job?pid={process.Id}（回报 POST /cat/progress）, 端口文件 {path}");
    }

    private string PortFilePath(int pid) =>
        Path.Combine(PortFileDirectory ?? Path.GetDirectoryName(InGame.RunningGameRegistry.PortFilePath(pid))!, $"dctravel-{pid}.port");

    /// <summary>
    ///     上号自动开工: 进世界后开始模拟 Afan, 占位进程退出即停（外壳没有任务单通道时不做）。
    ///     entered = 自动进入角色进的角色; 不自动进入角色时为 null, 这里按真实启动的做法（模块读到当前角色）先报一次 game.character
    /// </summary>
    private void StartSimulatedAfan(CatLaunchRequest request, Process process, ICatLaunchReporter reporter, CatCharacterInfo? entered = null)
    {
        if (reporter.Jobs is not { } jobs || process.HasExited)
            return;

        if (entered == null)
        {
            entered = SimulatedCharacter(1, request.CharacterName ?? SIMULATED_CHARACTER_NAME, request.CharacterHomeWorld ?? "LaNuoXiYa");
            reporter.Character(entered);
        }

        var account      = request.AccountName;
        var alwaysInDuty = false;
        var dutyLost     = false;

        // 副本状态的两个前缀顺序不限, 都在上号自动开工的前缀前面
        while (true)
        {
            if (!alwaysInDuty && account.StartsWith(IN_DUTY_PREFIX, StringComparison.Ordinal))
            {
                alwaysInDuty = true;
                account      = account[IN_DUTY_PREFIX.Length..];
            }
            else if (!dutyLost && account.StartsWith(DUTY_LOST_PREFIX, StringComparison.Ordinal))
            {
                dutyLost = true;
                account  = account[DUTY_LOST_PREFIX.Length..];
            }
            else
            {
                break;
            }
        }

        var behavior = account.StartsWith(JOB_BLOCKED_PREFIX, StringComparison.Ordinal) ? CatSimulatedAfan.Behavior.Blocked
                       : account.StartsWith(JOB_MANUAL_PREFIX, StringComparison.Ordinal) ? CatSimulatedAfan.Behavior.Manual
                       : account.StartsWith(JOB_MISMATCH_PREFIX, StringComparison.Ordinal) ? CatSimulatedAfan.Behavior.Mismatch
                       : account.StartsWith(JOB_UNSUPPORTED_PREFIX, StringComparison.Ordinal) ? CatSimulatedAfan.Behavior.Unsupported
                       : CatSimulatedAfan.Behavior.Normal;

        // 模拟截图写到别处（测试）时, 回报里的截图路径校验也认那个目录
        if (ShotsDirectory != null)
            jobs.ShotsDirectory = ShotsDirectory;

        var afan = new CatSimulatedAfan(jobs, process.Id, behavior, entered)
        {
            Step           = AfanStep,
            Heartbeat      = AfanHeartbeat,
            ShotsDirectory = ShotsDirectory ?? CatShots.Directory,
            AlwaysInDuty   = alwaysInDuty,
            DutyLost       = dutyLost
        };

        var task = Task.Run
        (async () =>
            {
                using var stop = new CancellationTokenSource();
                var run = afan.RunAsync(stop.Token);

                try
                {
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // 占位进程已不可用
                }

                await stop.CancelAsync().ConfigureAwait(false);

                try
                {
                    await run.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // 停下
                }
            }
        );

        lock (stateLock)
            afanTasks.Add(task);
    }

    /// <summary>
    ///     占位进程退出后: 等模拟 Afan 停下（它的回报都排在 game.exited 之前）, 删掉端口文件
    /// </summary>
    private async Task ReleaseGameAsync(int pid)
    {
        Task[] tasks;

        lock (stateLock)
        {
            tasks = afanTasks.ToArray();
            afanTasks.Clear();
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);

        if (localListener == null)
            return;

        try
        {
            File.Delete(PortFilePath(pid));
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "[CatHost] 模拟模式删端口文件失败");
        }
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
                StartSimulatedAfan(request, process, reporter, entered);
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
