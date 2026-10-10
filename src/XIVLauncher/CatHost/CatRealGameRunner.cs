using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using Serilog;
using XIVLauncher.Account;
using XIVLauncher.Account.Cred;
using XIVLauncher.Account.DeviceProfiles;
using XIVLauncher.Common;
using XIVLauncher.Common.Game;
using XIVLauncher.Common.Game.Exceptions;
using XIVLauncher.Common.Util;
using XIVLauncher.CompanionApp;
using XIVLauncher.Dalamud;
using XIVLauncher.GamePatchV3.Update;
using XIVLauncher.GamePatchV3.Update.Models;
using XIVLauncher.InGame;
using XIVLauncher.Login.Channels;
using XIVLauncher.Login.Client;
using XIVLauncher.Login.Exceptions;
using XIVLauncher.Login.Models;
using XIVLauncher.Minion;
using XIVLauncher.Support;
using XIVLauncher.Windows.ViewModel.Main.Services;

namespace XIVLauncher.CatHost;

/// <summary>
///     启动失败, 带协议失败码
/// </summary>
public sealed class CatLaunchException(string code, string message) : Exception(message)
{
    /// <summary>协议失败码</summary>
    public string Code { get; } = code;
}

/// <summary>在账号库里找 WeGame 号的结果</summary>
public enum CatWeGameAccountMatch
{
    /// <summary>没有对得上的行</summary>
    None,

    /// <summary>账号名（WeGame 给的用户号）完全相同</summary>
    ByName,

    /// <summary>恰好一行的备注里写着请求的号</summary>
    ByNote,

    /// <summary>不止一行的备注里写着请求的号, 不知道该用哪一行</summary>
    AmbiguousNote
}

/// <summary>
///     无界面启动核心:与界面路径（GameLaunchFlow.StartGameAndCompanionApp）同样的步骤, 但不读界面状态、不弹任何框,
///     所有原本弹框的地方改为发事件 / 返回失败码。本进程只服务一个账号, 跨区会话也只属于这一个号。
/// </summary>
public sealed class CatRealGameRunner(CatLogRedactor redactor, Func<Task> ensureInitialized) : ICatGameRunner
{
    /// <summary>等 Dalamud.dll 出现在游戏进程里的上限</summary>
    private static readonly TimeSpan DalamudInjectTimeout = TimeSpan.FromMinutes(1);

    private readonly Launcher            launcher            = new();
    private readonly CompanionAppService companionAppService = new();
    private readonly LoginClient         loginClient         = new();
    private readonly SemaphoreSlim       quickKeyLock        = new(1, 1);
    private readonly object              cleanupLock         = new();
    private readonly HashSet<int>        cleanedPids         = [];
    private readonly object              closeLock           = new();

    private readonly CancellationTokenSource closeCts        = new();

    private CatLaunchRequest       request        = null!;
    private AccountManager         accountManager = null!;
    private XIVAccount             account        = null!;
    private DirectoryInfo          gamePath       = null!;
    private DeviceProfileSnapshot  device         = null!;
    private GameLaunchContext      context        = null!;
    private DCTravelRuntimeService? dcTravel;
    private string?                quickKey;
    private string?                weGameToken;
    private CatWeGameLoginCapture? weGameLogin;
    private CatWeGameRow?          weGameRow;
    private FFXIVProcess?          currentProcess;
    private int                    lastPid;
    private int?                   lastExitCode;
    private bool                   closeRequested;
    private TimeSpan               closeTimeout = TimeSpan.FromSeconds(CatProtocol.DEFAULT_CLOSE_TIMEOUT_SECONDS);
    private string?                exitReason;
    private string?                exitFailureCode;
    private string?                exitFailureMessage;

    /// <summary>各游戏进程的自动进入角色: 取消源（进程收尾时取消）与后台任务</summary>
    private readonly Dictionary<int, CancellationTokenSource> autoEnterCancellations = [];
    private readonly List<Task>                               autoEnterTasks         = [];
    private volatile CatAutoEnter?                            autoEnter;

    /// <summary>等自动进入角色的后台任务收尾的上限（它们在游戏退出时已被取消, 这里只是不让事件落在 game.exited 后面）</summary>
    private static readonly TimeSpan AutoEnterShutdownTimeout = TimeSpan.FromSeconds(5);

    /// <summary>本进程守着的游戏的守护记录与守护锁</summary>
    private readonly GameRecordWriter records = new();

    /// <summary>
    ///     正在交接停止或已交接: 进程退出前不再碰游戏、记录、端口文件, 也不再重开（游戏留给下一个守护进程）。
    ///     没交接成时撤回, 之前被它挡下的收尾由 <see cref="HandOffDecidedAsync" /> 的等待方补做
    /// </summary>
    private volatile bool handingOff;

    /// <summary>交接停止的结论: true = 已交接（本进程退出）, false = 没交接成（照常守护）; 没在交接时为 null</summary>
    private TaskCompletionSource<bool>? handOffDecision;

    private bool IsCloseRequested
    {
        get
        {
            lock (closeLock)
                return closeRequested;
        }
    }

    /// <inheritdoc />
    public async Task<int> RunAsync(CatLaunchRequest launchRequest, ICatLaunchReporter reporter, CancellationToken cancellationToken)
    {
        request = launchRequest;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, closeCts.Token);

        try
        {
            await PrepareAsync(reporter, linked.Token).ConfigureAwait(false);

            // 只登录: 登录成功即结束, 不取票据、不起游戏
            if (request.AuthOnly)
            {
                reporter.Authorized(account.UserName, weGameLogin?.Captured == true);
                return CatHostRuntime.EXIT_OK;
            }

            using var final = await RunGameAsync(RestartMonitor.RestartOptions.Normal, null, reporter, linked.Token, cancellationToken).ConfigureAwait(false);

            if (await HandOffDecidedAsync().ConfigureAwait(false))
                return CatHostRuntime.EXIT_HANDED_OFF;

            // 交接途中游戏退出、交接又没成: 当时被挡下的收尾补上（已做过的不会重做）
            CleanupProcess(final, null);
            await WaitAutoEnterEndAsync().ConfigureAwait(false);

            if (IsCloseRequested)
                reporter.Exited(lastPid, lastExitCode, CatExitReasons.CLOSED);
            else
                reporter.Exited(lastPid, lastExitCode, exitReason, exitFailureCode, exitFailureMessage);

            return CatHostRuntime.EXIT_OK;
        }
        catch (CatLaunchException ex) when (currentProcess == null)
        {
            reporter.Failed(ex.Code, ex.Message);
            return CatLaunchHost.EXIT_LAUNCH_FAILED;
        }
        catch (OperationCanceledException) when (currentProcess == null && IsCloseRequested)
        {
            reporter.Failed(CatCodes.CANCELLED, "启动途中收到关闭请求, 没有起游戏");
            return CatLaunchHost.EXIT_LAUNCH_FAILED;
        }
        catch (Exception ex) when (currentProcess != null)
        {
            return await GuardAfterErrorAsync(ex, reporter).ConfigureAwait(false);
        }
        finally
        {
            dcTravel?.Dispose();
        }
    }

    /// <inheritdoc />
    public async Task<int> AdoptAsync(CatAdoptRequest adoptRequest, ICatLaunchReporter reporter, CancellationToken cancellationToken)
    {
        var record = adoptRequest.Record;

        request = new CatLaunchRequest
        (
            adoptRequest.OperationId,
            record.AccountName,
            record.DalamudRequested,
            adoptRequest.MinionCard,
            adoptRequest.CrashDialogTimeoutSeconds,
            record.AreaName,
            record.Channel == GameRecordChannels.WE_GAME ? XIVAccountType.WeGame : XIVAccountType.Sdo,
            false,
            null,
            false,
            null,
            record.AutoEnter,
            record.CharacterName,
            record.CharacterHomeWorld
        );

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, closeCts.Token);

        try
        {
            var launched = await PrepareAdoptAsync(adoptRequest, reporter, linked.Token).ConfigureAwait(false);
            var options  = new RestartMonitor.RestartOptions(record.RestartNoDalamud, record.RestartNoThirdPlugins, record.RestartNoPlugins);

            using var final = await GuardAsync(launched, record.Dalamud, options, null, TimeSpan.Zero, reporter, linked.Token, cancellationToken).ConfigureAwait(false);

            if (await HandOffDecidedAsync().ConfigureAwait(false))
                return CatHostRuntime.EXIT_HANDED_OFF;

            // 交接途中游戏退出、交接又没成: 当时被挡下的收尾补上（已做过的不会重做）
            CleanupProcess(final, null);
            await WaitAutoEnterEndAsync().ConfigureAwait(false);

            if (IsCloseRequested)
                reporter.Exited(lastPid, lastExitCode, CatExitReasons.CLOSED);
            else
                reporter.Exited(lastPid, lastExitCode, exitReason, exitFailureCode, exitFailureMessage);

            return CatHostRuntime.EXIT_OK;
        }
        catch (CatLaunchException ex) when (currentProcess == null)
        {
            reporter.Failed(ex.Code, ex.Message);
            return CatLaunchHost.EXIT_LAUNCH_FAILED;
        }
        catch (OperationCanceledException) when (currentProcess == null && IsCloseRequested)
        {
            // 接管准备途中收到 close: 外壳要的是下号, 游戏照样关掉
            Log.Information("[CatHost] 接管途中收到关闭请求, 直接关游戏 {Pid}", adoptRequest.Pid);
            await CatGameCloser.CloseAsync(adoptRequest.Game, closeTimeout).ConfigureAwait(false);

            int? exitCode = null;

            try
            {
                exitCode = adoptRequest.Game.HasExited ? adoptRequest.Game.ExitCode : null;
            }
            catch (InvalidOperationException)
            {
                // 读不到退出码
            }

            RunningGameRegistry.Unregister(adoptRequest.Pid);
            GameRecords.Delete(adoptRequest.Pid, adoptRequest.ProcessStartedAt);
            reporter.Exited(adoptRequest.Pid, exitCode, CatExitReasons.CLOSED);
            return CatHostRuntime.EXIT_OK;
        }
        catch (Exception ex) when (currentProcess != null)
        {
            return await GuardAfterErrorAsync(ex, reporter).ConfigureAwait(false);
        }
        finally
        {
            dcTravel?.Dispose();
        }
    }

    /// <summary>
    ///     接管前的准备: 按守护记录找回账号、设备、大区, 用记录里的 TGT/guid 重建登录刷新（不重新登录）,
    ///     跨区服务绑回游戏命令行里的端口, 把守护者改成本进程。返回游戏进程。
    /// </summary>
    private async Task<FFXIVProcess> PrepareAdoptAsync(CatAdoptRequest adoptRequest, ICatLaunchReporter reporter, CancellationToken cancellationToken)
    {
        var record = adoptRequest.Record;

        reporter.Stage(CatStages.PREPARING);

        try
        {
            await ensureInitialized().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[CatHost] 初始化设置与账号库失败");
            throw new CatLaunchException(CatCodes.LAUNCH_FAILED, $"DcMiniLauncher 初始化失败: {ex.Message}");
        }

        accountManager = App.AccountManager;

        // 被强杀的守护进程留下的记录、临时文件、锁文件
        GameRecords.PruneStale();

        if (accountManager.CurrentCredType == CredType.WindowsHello)
            throw new CatLaunchException(CatCodes.AUTHORIZATION_REQUIRED, "账号库使用 Windows Hello 加密, 无人值守时无法解密已保存的凭证");

        account = accountManager.FindAccount(record.AccountUserName, request.Platform)
                  ?? throw new CatLaunchException(CatCodes.AUTHORIZATION_REQUIRED, $"账号库里找不到守护记录里的号 {record.AccountUserName}");

        accountManager.RefreshFromDatabase(account);

        gamePath = App.Settings.GetGamePath(request.Platform) is { Exists: true } path
                       ? path
                       : throw new CatLaunchException(CatCodes.INVALID_GAME_PATH, "DcMiniLauncher 设置里的游戏目录无效");

        device = CatDeviceProfiles.Resolve(accountManager, account, out _)
                 ?? throw new CatLaunchException(CatCodes.AUTHORIZATION_REQUIRED, "这个号开了独立设备, 但账号库里找不到它的设备信息");

        var areas = await LoadAreasAsync(cancellationToken).ConfigureAwait(false);

        // 账号库里的大区会随游戏内换大区更新, 比启动时写进记录的新
        var area = ResolveArea(areas, account.AreaName, null, x => x.AreaName, out _)
                   ?? ResolveArea(areas, record.AreaName, null, x => x.AreaName, out _);

        if (area == null)
        {
            area = areas[0];
            reporter.Log("warning", $"账号库和守护记录里都没有能用的大区, 崩溃重开时按列表第一个「{area.AreaName}」启动");
        }

        // 崩溃重开换票据用的凭证: 先用记录里的 TGT/guid（不算登录）, 过期了再用快速登录凭证 / WeGame 令牌
        var tgt   = await DecryptOrNullAsync(record.Tgt).ConfigureAwait(false);
        var guid  = await DecryptOrNullAsync(record.Guid).ConfigureAwait(false);
        var oauth = new OAuthLoginResult { SndaID = record.SndaId ?? string.Empty, TGT = tgt, Guid = guid, DeviceProfile = device };

        if (request.IsWeGame)
            weGameToken = await DecryptOrNullAsync(accountManager.HasUnavailableSecrets(account) ? null : account.WeGameQuickLoginSecret).ConfigureAwait(false);
        else
            quickKey = await DecryptOrNullAsync(accountManager.HasUnavailableSecrets(account) ? null : account.SdoQuickLoginSecret).ConfigureAwait(false);

        context = new GameLaunchContext(new LoginResult { State = LoginState.Ok, OAuthLogin = oauth }, area, areas, request.Platform);

        dcTravel = new DCTravelRuntimeService(SyncAreaFromDcTravel);

        if (!string.IsNullOrEmpty(tgt) && !string.IsNullOrEmpty(guid))
            new LoginChannelContext(device).BindLoginSessionRefresh(dcTravel, tgt, guid);

        if (CanRefreshByLogin)
            dcTravel.ConfigureQuickLoginRefresh(RefreshSessionIdByQuickLoginAsync);

        // 交接停止过来的: 旧守护没登出, 它的网页会话接着用（失效了 GetValidCookie 照常换票据）
        if (await DecryptOrNullAsync(record.DcTravelSession).ConfigureAwait(false) is { } handedSession)
            dcTravel.Client.SeedNSessionId(handedSession);

        if (record.DcTravelPort > 0)
        {
            context.DcTravelPort = await dcTravel.StartAsync(record.DcTravelPort).ConfigureAwait(false);

            if (context.DcTravelPort == 0)
                reporter.Log("error", $"跨区端口 {record.DcTravelPort} 没能绑回, 这个游戏的游戏内跨区不可用（崩溃重开照常）");
        }

        // 受理 adopt 时已打开并核对过的进程（一直拿着句柄, 进程号不会被复用）
        var process = adoptRequest.Game;

        if (process.HasExited)
            throw new CatLaunchException(CatCodes.NOT_RUNNING, $"游戏 {adoptRequest.Pid} 已经退出");

        var launched = new FFXIVProcess(process);
        bool closeNow;

        lock (closeLock)
        {
            currentProcess = launched;
            closeNow       = closeRequested;
        }

        await records.WriteAsync
        (
            () => Task.FromResult<GameRecord?>
            (
                record with
                {
                    OperationId = adoptRequest.OperationId,
                    GuardPid = Environment.ProcessId,
                    GuardStartedAt = GameRecordWriter.SelfStartedAt,
                    DcTravelSession = null,
                    UpdatedAt = DateTimeOffset.UtcNow
                }
            )
        ).ConfigureAwait(false);

        records.MarkRecorded(process.Id, record.ProcessStartedAt);

        // 受理 adopt 时认领的守护锁: 这个游戏一退出（含崩溃重开换了新进程）就放掉, 不等整个接管流程结束
        records.Hold(process.Id, adoptRequest.GuardClaim);

        context.InGameAgents = (record.Dalamud ? InGameAgents.Dalamud : InGameAgents.None) | (record.MinionFingerprint != null ? InGameAgents.Minion : InGameAgents.None);
        RunningGameRegistry.Register(process, context.InGameAgents, context.DcTravelPort);

        Log.Information
        (
            "[CatHost] 已接管游戏 {Pid}（原守护进程 {GuardPid}）, 跨区端口 {Port}, 盯崩溃处理器={Dalamud}, 有 TGT={HasTgt}, 有快速登录凭证={CanRefresh}",
            process.Id,
            record.GuardPid,
            context.DcTravelPort,
            record.Dalamud,
            !string.IsNullOrEmpty(tgt) && !string.IsNullOrEmpty(guid),
            CanRefreshByLogin
        );

        // 接管来的游戏已经注好的东西照实报一次, status 才不会是空的
        if (record.Dalamud)
            reporter.Agent(CatAgentKinds.DALAMUD, IsDalamudLoaded(process));

        if (record.MinionFingerprint != null)
            reporter.Agent(CatAgentKinds.MINION, CatMinionReservations.IsAttached(process));

        // running 先于 game.adopted: 外壳把 adopted 之前的阶段当作接管进程自己的准备过程, 不转述（游戏早就进了游戏）
        if (!closeNow)
            reporter.Stage(CatStages.RUNNING);

        reporter.Adopted(process.Id, record.ProcessStartedAt);

        if (closeNow)
        {
            // 准备途中收到了 close: 接管下来再关, 之后照常发 game.exited{closed}
            _ = CatGameCloser.CloseAsync(process, closeTimeout);
            return launched;
        }

        // 自动进入角色的游戏: 接着每隔一段时间看一次当前角色（编排早就做完了, 不再重做）
        if (record.AutoEnter && !process.HasExited)
            StartObserve(launched, reporter, cancellationToken);

        return launched;
    }

    /// <summary>
    ///     只看当前角色、不做编排（接管来的游戏已经进过游戏了）
    /// </summary>
    private void StartObserve(FFXIVProcess launched, ICatLaunchReporter reporter, CancellationToken token)
    {
        var process      = launched.UnderlyingProcess;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var game         = new CatAutoEnterRealGame(process, dcTravel!.Client, context.Areas, context.Area.AreaName, RememberEnteredArea);
        var flow         = new CatAutoEnter(game, reporter, new CatAutoEnterTarget(request.CharacterName, request.CharacterHomeWorld, null));
        var observeToken = cancellation.Token;

        autoEnter = flow;

        lock (cleanupLock)
        {
            autoEnterCancellations[launched.ProcessID] = cancellation;
            autoEnterTasks.Add
            (
                Task.Run
                (async () =>
                    {
                        try
                        {
                            await flow.ObserveAsync(observeToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            // 游戏退出或收到关闭请求
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "[CatHost] 接管后看当前角色的后台任务出错（游戏不受影响）");
                        }
                        finally
                        {
                            game.Dispose();
                        }
                    }
                )
            );
        }
    }

    /// <inheritdoc />
    public async Task CloseAsync(TimeSpan gracefulTimeout)
    {
        FFXIVProcess? process;

        lock (closeLock)
        {
            if (closeRequested)
                return;

            closeRequested = true;
            closeTimeout   = gracefulTimeout;
            process        = currentProcess;
        }

        Log.Information("[CatHost] 收到关闭请求, 不再崩溃重启");

        // 取消启动途中的步骤、不再等崩溃处理器的重启决定
        await closeCts.CancelAsync().ConfigureAwait(false);

        // 游戏还没起来时由启动流程自己收尾; 正在创建进程时由创建方看到关闭请求后关
        if (process != null)
            await CatGameCloser.CloseAsync(process.UnderlyingProcess, gracefulTimeout).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public CatAcceptResult ConfirmWeGameSms(string challengeId) =>
        weGameLogin?.ConfirmSms(challengeId) ?? CatAcceptResult.Rejected(CatCodes.NOT_RUNNING, "当前没有在等 WeGame 的设备验证");

    /// <inheritdoc />
    public CatAcceptResult SelectCharacter(string contentId) =>
        autoEnter?.SelectCharacter(contentId) ?? CatAcceptResult.Rejected(CatCodes.NOT_RUNNING, "当前没有在等人选角色");

    /// <inheritdoc />
    public async Task<CatAcceptResult?> PrepareHandOffAsync(CancellationToken cancellationToken)
    {
        if (IsCloseRequested)
            return CatAcceptResult.Rejected(CatCodes.CLOSING, "已收到 close, 本进程即将退出");

        if (currentProcess is not { } launched || launched.UnderlyingProcess.HasExited)
            return CatAcceptResult.Rejected(CatCodes.BUSY, "游戏刚退出（可能开着崩溃对话框）, 稍后再试");

        var gamePid = launched.ProcessID;

        // 账号库不加密时记录里不存 TGT 和网页会话, 接管方续不上跨区
        if (accountManager.CurrentCredType == CredType.NoEncryption && dcTravel?.Listener != null)
            return CatAcceptResult.Rejected(CatCodes.UNSUPPORTED, "账号库没有加密, 守护记录里不存登录凭证, 交接后游戏内跨区会不可用");

        if (!records.TryGetRecorded(gamePid, out var startedAt))
            return CatAcceptResult.Rejected(CatCodes.GAME_NOT_FOUND, $"游戏 {gamePid} 没有守护记录, 交接后没有进程接得了");

        // 不再接新的换大区（做到一半会被进程退出截断）; 已有一次在进行就等它结束
        if (!InGameTravelJobs.TryHold(gamePid))
            return CatAcceptResult.Rejected(CatCodes.BUSY, "游戏内跨区正在进行, 等它结束再交接");

        // 从这里起不再碰游戏: 之后游戏若退出或崩溃, 等交接的结论再说
        var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        handOffDecision = decision;
        handingOff      = true;

        var session = dcTravel?.Client.TryGetNSessionId();
        var written = await records.WriteAsync
                      (async () =>
                          {
                              if (GameRecords.ReadMatching(gamePid, startedAt) is not { } record)
                                  return null;

                              var oauth = context.LoginResult.OAuthLogin;

                              return record with
                              {
                                  OperationId = request.OperationId,
                                  Tgt = await EncryptOrNullAsync(oauth?.TGT).ConfigureAwait(false) ?? record.Tgt,
                                  Guid = await EncryptOrNullAsync(oauth?.Guid).ConfigureAwait(false) ?? record.Guid,
                                  DcTravelSession = await EncryptOrNullAsync(session).ConfigureAwait(false),
                                  UpdatedAt = DateTimeOffset.UtcNow
                              };
                          }
                      ).ConfigureAwait(false);

        CatAcceptResult? refused = null;

        if (!written)
            refused = CatAcceptResult.Rejected(CatCodes.GAME_NOT_FOUND, $"游戏 {gamePid} 的守护记录读不到或写不进去, 交接后没有进程接得了");
        else if (launched.UnderlyingProcess.HasExited)
            // 写记录期间游戏退出了（多半是崩了）: 接管方接不了已经退出的游戏, 由本进程照常处理崩溃重开或收尾
            refused = CatAcceptResult.Rejected(CatCodes.BUSY, "游戏刚退出（可能开着崩溃对话框）, 稍后再试");

        if (refused != null)
        {
            InGameTravelJobs.Release(gamePid);
            handingOff = false;
            decision.TrySetResult(false);
            return refused;
        }

        decision.TrySetResult(true);
        Log.Information("[CatHost] 交接停止: 游戏 {Pid} 的记录已更新（带跨区会话={HasSession}）, 不登出、不删记录", gamePid, session != null);
        return null;
    }

    /// <summary>
    ///     交接停止是否已定: 没在交接返回 false; 正在交接就等结论（写一份记录的工夫）
    /// </summary>
    private Task<bool> HandOffDecidedAsync() =>
        handingOff && handOffDecision is { } decision ? decision.Task : Task.FromResult(false);

    /// <summary>
    ///     游戏起来后出了意外异常: 不再守护（崩溃重启、跨区刷新）, 但照样等游戏结束再发 game.exited。期间 close 仍可用。
    /// </summary>
    private async Task<int> GuardAfterErrorAsync(Exception exception, ICatLaunchReporter reporter)
    {
        var detail  = $"{exception.GetType().Name}: {exception.Message}";
        var process = currentProcess!;

        Log.Error(exception, "[CatHost] 守护游戏时出错");
        reporter.Log("error", $"守护游戏时出错, 不再负责崩溃重启和跨区, 游戏结束后才会发 game.exited: {detail}");

        try
        {
            await process.UnderlyingProcess.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[CatHost] 等游戏进程退出失败");
        }

        CleanupProcess(process, null);
        await WaitAutoEnterEndAsync().ConfigureAwait(false);

        var closed = IsCloseRequested;
        reporter.Exited(process.ProcessID, TryGetExitCode(process), closed ? CatExitReasons.CLOSED : CatExitReasons.GUARD_ERROR, null, closed ? null : detail);
        return CatLaunchHost.EXIT_GUARD_ERROR;
    }

    /// <inheritdoc />
    public async Task InjectAsync(bool dalamud, bool minion, bool force, ICatLaunchReporter reporter, CancellationToken cancellationToken)
    {
        var process = currentProcess;

        if (process == null || process.UnderlyingProcess.HasExited)
        {
            if (dalamud)
                reporter.Agent(CatAgentKinds.DALAMUD, false, CatCodes.NOT_RUNNING, "游戏进程已退出");
            if (minion)
                reporter.Agent(CatAgentKinds.MINION, false, CatCodes.NOT_RUNNING, "游戏进程已退出");
            return;
        }

        try
        {
            if (dalamud)
                await InjectDalamudIntoRunningAsync(process, reporter, cancellationToken).ConfigureAwait(false);

            if (minion)
            {
                if (!force && CatMinionReservations.IsAttached(process.UnderlyingProcess))
                    reporter.Agent(CatAgentKinds.MINION, true, CatCodes.ALREADY_ATTACHED, "这个游戏已经挂着 Minion, 没有重复挂; 要重新挂请带 force");
                else
                    await AttachMinionAsync(process, IsDalamudLoaded(process.UnderlyingProcess), reporter, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            // 自动进入角色报过的阶段（如已进入游戏、等人选角色）不能被补注入冲掉
            if (!process.UnderlyingProcess.HasExited)
                reporter.Stage(autoEnter?.CurrentStage ?? CatStages.RUNNING);
        }
    }

    #region 准备

    private async Task PrepareAsync(ICatLaunchReporter reporter, CancellationToken cancellationToken)
    {
        reporter.Stage(CatStages.PREPARING);

        try
        {
            await ensureInitialized().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[CatHost] 初始化设置与账号库失败");
            throw new CatLaunchException(CatCodes.LAUNCH_FAILED, $"DcMiniLauncher 初始化失败: {ex.Message}");
        }

        accountManager = App.AccountManager;

        // 被强杀的守护进程留下的记录、临时文件、锁文件
        GameRecords.PruneStale();

        if (accountManager.CurrentCredType == CredType.WindowsHello)
        {
            throw new CatLaunchException
            (
                CatCodes.AUTHORIZATION_REQUIRED,
                request.IsWeGame
                    ? "账号库使用 Windows Hello 加密, 无人值守时读不出已保存的登录信息"
                    : "账号库使用 Windows Hello 加密, 无人值守时无法解密已保存的凭证"
            );
        }

        // 盛趣号与 WeGame 号在账号库里是不同的行（账号名可以相同）, 按请求的渠道找
        account = request.IsWeGame
                      ? await FindWeGameAccountAsync(reporter, cancellationToken).ConfigureAwait(false)
                      : accountManager.FindAccount(request.AccountName, XIVAccountType.Sdo)
                        ?? throw new CatLaunchException(CatCodes.AUTHORIZATION_REQUIRED, "DcMiniLauncher 账号库里没有这个号, 需要先授权");

        // 其它进程可能刚更新过这个号的凭证和设备, 启动前从数据库刷新这一行
        accountManager.RefreshFromDatabase(account);

        // 只登录不起游戏: 不看游戏目录和补丁, 也不挂 Minion（拉起 WeGame 用的 sdologin 目录在就地登录时另外检查）
        if (!request.AuthOnly)
        {
            gamePath = App.Settings.GetGamePath(request.Platform) is { Exists: true } path
                           ? path
                           : throw new CatLaunchException
                             (
                                 CatCodes.INVALID_GAME_PATH,
                                 request.IsWeGame
                                     ? "DcMiniLauncher 设置里的 WeGame 版游戏目录无效, 请在界面版「设置」里重新选择"
                                     : "DcMiniLauncher 设置里的国服游戏目录无效"
                             );

            await CheckGameUpdateAsync(cancellationToken).ConfigureAwait(false);

            if (request.Minion)
            {
                // 起游戏前先确认本机能挂 Minion; 占用检查与预占在挂载前加锁再做
                if (CatMinionReservations.Check(request.MinionCard) is { } minionError)
                    throw new CatLaunchException(minionError.Code, minionError.Message);
            }
        }

        device = CatDeviceProfiles.Resolve(accountManager, account, out var isPerAccount)
                 ?? throw new CatLaunchException
                 (
                     CatCodes.AUTHORIZATION_REQUIRED,
                     request.IsWeGame
                         ? "这个号开了独立设备, 但账号库里找不到它的设备信息, 请在 DcMiniLauncher 界面版里重新设置这个号的设备"
                         : "这个号开了独立设备, 但账号库里找不到它的设备信息, 需要重新授权"
                 );

        if (!isPerAccount)
            reporter.Log("warning", "这个号在账号库里用的是共享设备, 不是独立设备");

        if (account.IsDeviceProfileRotation)
            reporter.Log("warning", "这个号开着「定期自动更换设备」: 无界面启动不会换设备, 但界面版到期会换, 换了就要客户重新验证; 请在 DcMiniLauncher 账号设备设置里关掉");

        // 只登录: 登录成功即可, 不定大区（不回写账号库）、不开跨区服务
        if (request.AuthOnly)
        {
            await LoginWithSavedCredentialAsync(reporter, cancellationToken).ConfigureAwait(false);
            return;
        }

        var areas = await LoadAreasAsync(cancellationToken).ConfigureAwait(false);
        var area  = ResolveArea(areas, account.AreaName, request.AreaName, x => x.AreaName, out var fromRequest);

        if (area == null && fromRequest)
            throw new CatLaunchException(CatCodes.LAUNCH_FAILED, $"资料里的大区「{request.AreaName}」不在盛趣的大区列表里, 请核对任务资料");

        if (area == null)
        {
            area = areas[0];
            reporter.Log("warning", $"账号库和上号请求都没有这个号的大区, 按列表第一个「{area.AreaName}」启动");
        }
        else if (fromRequest && !string.Equals(account.AreaName, area.AreaName, StringComparison.Ordinal))
        {
            // 账号库记的和资料不一样（或没记）: 按资料启动并记下来, 界面版和跨区同步都以它为起点
            if (!string.IsNullOrEmpty(account.AreaName))
                Log.Information("[CatHost] 账号库记的大区是 {Saved}, 按资料里的 {Area} 启动", account.AreaName, area.AreaName);

            account.AreaName = area.AreaName;
            accountManager.Save(account);
        }

        dcTravel = new DCTravelRuntimeService(SyncAreaFromDcTravel);

        var loginResult = await LoginWithSavedCredentialAsync(reporter, cancellationToken).ConfigureAwait(false);

        context = new GameLaunchContext(loginResult, area, areas, request.Platform);

        if (CanRefreshByLogin)
            dcTravel.ConfigureQuickLoginRefresh(RefreshSessionIdByQuickLoginAsync);

        context.DcTravelPort = await dcTravel.StartAsync().ConfigureAwait(false);
        Log.Information("[CatHost] 跨区会话已建立, 端口 {Port}, 大区 {Area}", context.DcTravelPort, area.AreaName);
    }

    /// <summary>
    ///     找 WeGame 号那一行（按账号名, 找不到再按备注, 见 <see cref="CatWeGameLoginCapture.FindRowAsync" />）;
    ///     账号库里没有这个号且 launch 带了 weGameLogin 时, 会先在本机拉起 WeGame 等员工登录。之后登录、设备、令牌都用找到的这一行。
    /// </summary>
    private async Task<XIVAccount> FindWeGameAccountAsync(ICatLaunchReporter reporter, CancellationToken cancellationToken)
    {
        var store = new CatWeGameAccountStore(accountManager);

        weGameLogin = new CatWeGameLoginCapture(new CatWeGameLoginRealEnvironment(() => App.Settings.WeGamePath?.FullName), store, redactor)
        {
            Screen = new CatWeGameRealScreen(new CatZxingQrCodec())
        };
        weGameRow   = await weGameLogin.FindRowAsync(request, reporter, cancellationToken).ConfigureAwait(false);

        return store.GetAccount(weGameRow);
    }

    private async Task CheckGameUpdateAsync(CancellationToken cancellationToken)
    {
        GameUpdateCheckResult checkResult;

        try
        {
            checkResult = await GameUpdater.Check(gamePath, false, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            Log.Error(ex, "[CatHost] 启动前补丁检查失败");
            throw new CatLaunchException(CatLoginFailures.ToCode(CatLoginFailures.Classify(ex), CatCodes.LAUNCH_FAILED), $"检查游戏更新失败: {ex.Message}");
        }

        if (checkResult.NeedsUpdate)
            throw new CatLaunchException(CatCodes.GAME_UPDATE_REQUIRED, "游戏有待安装的补丁, 请先在 DcMiniLauncher 界面里更新游戏");
    }

    private static async Task<LoginArea[]> LoadAreasAsync(CancellationToken cancellationToken)
    {
        LoginArea[] areas;

        try
        {
            areas = await LoginArea.Get().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[CatHost] 获取大区列表失败");
            throw new CatLaunchException(CatLoginFailures.ToCode(CatLoginFailures.Classify(ex), CatCodes.LAUNCH_FAILED), $"获取大区列表失败: {ex.Message}");
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (areas.Length == 0)
            throw new CatLaunchException(CatCodes.LAUNCH_FAILED, "获取大区列表失败: 列表为空");

        return areas;
    }

    #endregion

    #region 登录与票据

    /// <summary>
    ///     用账号库里保存的凭证登录, 尽量少登录盛趣: 快速登录凭证优先; 网络错误不换密码（报 networkError 可重试）;
    ///     要客户验证时报 riskControl; 只有凭证被拒且有密码才用密码登录一次; 其余报 authorizationRequired。
    ///     WeGame 号另走 <see cref="LoginWithWeGameTokenAsync" />。
    /// </summary>
    private async Task<LoginResult> LoginWithSavedCredentialAsync(ICatLaunchReporter reporter, CancellationToken cancellationToken)
    {
        if (request.IsWeGame)
            return await LoginWithWeGameTokenAsync(reporter, cancellationToken).ConfigureAwait(false);

        var secretsAvailable = !accountManager.HasUnavailableSecrets(account);
        Exception? lastError = null;

        var savedKey = secretsAvailable && !string.IsNullOrEmpty(account.SdoQuickLoginSecret)
                           ? await accountManager.Decrypt(account.SdoQuickLoginSecret).ConfigureAwait(false)
                           : null;

        if (!string.IsNullOrEmpty(savedKey))
        {
            redactor.Register(savedKey);

            try
            {
                var result = await loginClient.LoginAsync
                             (
                                 LoginType.QuickLogin,
                                 new LoginRequest
                                 {
                                     Account                 = account.UserName,
                                     Secret                  = savedKey,
                                     QuickLoginEnabled       = true,
                                     DeviceProfile           = device,
                                     LoginSessionRefreshSink = dcTravel,
                                     ShowLoginMessage        = message => Log.Information("[CatHost] 登录: {Message}", message)
                                 },
                                 cancellationToken
                             ).ConfigureAwait(false);

                quickKey = savedKey;
                await PersistQuickKeyAsync(result.OAuthLogin?.QuickLoginSecret).ConfigureAwait(false);
                return EnsureLoginOk(result);
            }
            catch (CatLaunchException)
            {
                throw;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                var kind   = CatLoginFailures.Classify(ex);
                var detail = CatLoginFailures.Describe(ex);
                Log.Warning(ex, "[CatHost] 用已保存的快速登录凭证登录失败 ({Kind}): {Detail}", kind, detail);

                switch (kind)
                {
                    case CatLoginFailureKind.Network:
                        throw new CatLaunchException(CatCodes.NETWORK_ERROR, $"连不上盛趣登录服务器, 稍后重试即可: {detail}");
                    case CatLoginFailureKind.RiskControl:
                        throw new CatLaunchException(CatCodes.RISK_CONTROL, $"盛趣要求客户验证: {detail}");
                    case CatLoginFailureKind.Unknown:
                        throw new CatLaunchException(CatCodes.LAUNCH_FAILED, $"快速登录出错, 没有改用密码: {ex.GetType().Name}: {detail}");
                }

                reporter.Log("warning", $"快速登录凭证被拒: {detail}");
                lastError = ex;
            }
        }

        var savedPassword = secretsAvailable && !string.IsNullOrEmpty(account.SdoPassword)
                                ? await accountManager.Decrypt(account.SdoPassword).ConfigureAwait(false)
                                : null;

        if (!string.IsNullOrEmpty(savedPassword))
        {
            redactor.Register(savedPassword);

            try
            {
                var result = await loginClient.LoginAsync
                             (
                                 LoginType.Static,
                                 new LoginRequest
                                 {
                                     Account                     = account.UserName,
                                     Secret                      = savedPassword,
                                     QuickLoginEnabled           = false,
                                     DeviceProfile               = device,
                                     LoginSessionRefreshSink     = dcTravel,
                                     ShowLoginMessage            = message => Log.Information("[CatHost] 登录: {Message}", message),
                                     StopOnSafePhoneVerification = true,

                                     // 无人值守: 需要短信 / 图形验证码时不弹框, 直接按需要授权处理
                                     PromptTextInput    = (_, _, _) => null,
                                     PromptCaptchaInput = _ => null
                                 },
                                 cancellationToken
                             ).ConfigureAwait(false);

                return EnsureLoginOk(result);
            }
            catch (CatLaunchException)
            {
                throw;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                var kind   = CatLoginFailures.Classify(ex);
                var detail = CatLoginFailures.Describe(ex);
                Log.Warning(ex, "[CatHost] 用已保存的密码登录失败 ({Kind}): {Detail}", kind, detail);

                // 密码登录被盛趣拒绝（含不认识的返回码, 如密码错）时仍报 authorizationRequired; 不是盛趣的回答才按启动失败报
                switch (kind)
                {
                    case CatLoginFailureKind.Network:
                        throw new CatLaunchException(CatCodes.NETWORK_ERROR, $"连不上盛趣登录服务器, 稍后重试即可: {detail}");
                    case CatLoginFailureKind.RiskControl:
                        throw new CatLaunchException(CatCodes.RISK_CONTROL, $"盛趣要求客户验证: {detail}");
                    case CatLoginFailureKind.Unknown when ex is not LoginException:
                        throw new CatLaunchException(CatCodes.LAUNCH_FAILED, $"密码登录出错: {ex.GetType().Name}: {detail}");
                }

                lastError = ex;
            }
        }

        throw new CatLaunchException
        (
            CatCodes.AUTHORIZATION_REQUIRED,
            lastError == null
                ? "这个号在账号库里没有可用的快速登录凭证或密码, 需要先授权"
                : $"已保存的凭证登录失败, 需要重新授权: {CatLoginFailures.Describe(lastError)}"
        );
    }

    /// <summary>
    ///     WeGame 号: 用存下的 WeGame 令牌登录（与界面版走同一个接口）, 没有密码兜底。没有令牌或盛趣不认这枚令牌时报 authorizationRequired,
    ///     launch 带了 weGameLogin 则改为在本机拉起 WeGame 等员工登录后再登一次; 只有盛趣明确说令牌不行（第三方验证失败）才把已存的令牌清掉。
    ///     这些判断都在 <see cref="CatWeGameLoginCapture.LoginAsync{TLogin}" /> 里。
    ///     登录成功不回写: 令牌本身不变, 界面版也不给 WeGame 号存盛趣的快速登录凭证。
    /// </summary>
    private Task<LoginResult> LoginWithWeGameTokenAsync(ICatLaunchReporter reporter, CancellationToken cancellationToken) =>
        weGameLogin!.LoginAsync
        (
            request,
            weGameRow!,
            reporter,
            async (token, loginToken) =>
            {
                redactor.Register(token);

                var result = await loginClient.LoginAsync(LoginType.WeGame, CreateWeGameLoginRequest(token, true), loginToken).ConfigureAwait(false);

                // 登录时顺带签发的盛趣快速登录凭证界面版不存, 这里也不存, 只防它进日志
                if (!string.IsNullOrEmpty(result.OAuthLogin?.QuickLoginSecret))
                    redactor.Register(result.OAuthLogin.QuickLoginSecret);

                weGameToken = token;
                return EnsureLoginOk(result);
            },
            cancellationToken
        );

    /// <summary>
    ///     WeGame 令牌登录的请求, 与界面版一致: 启动时那次登录带快速登录标记, 之后刷新票据时不带
    /// </summary>
    private LoginRequest CreateWeGameLoginRequest(string token, bool quickLoginEnabled) =>
        new()
        {
            Account                 = account.UserName,
            Secret                  = token,
            QuickLoginEnabled       = quickLoginEnabled,
            DeviceProfile           = device,
            LoginSessionRefreshSink = dcTravel,
            ShowLoginMessage        = message => Log.Information("[CatHost] 登录: {Message}", message)
        };

    /// <summary>手里有没有能重新登录换票据的凭证: 盛趣号是快速登录凭证, WeGame 号是 WeGame 令牌</summary>
    private bool CanRefreshByLogin => !string.IsNullOrEmpty(request.IsWeGame ? weGameToken : quickKey);

    private static LoginResult EnsureLoginOk(LoginResult result) =>
        result.State switch
        {
            LoginState.Ok when result.OAuthLogin != null => result,
            LoginState.NeedsPatchGame                    => throw new CatLaunchException(CatCodes.GAME_UPDATE_REQUIRED, "游戏需要更新"),
            LoginState.NeedsPatchBoot                    => throw new CatLaunchException(CatCodes.LAUNCH_FAILED, "部分游戏文件损坏, 需要重新安装游戏"),
            LoginState.NoService                         => throw new CatLaunchException(CatCodes.LAUNCH_FAILED, "该账号无游戏游玩权限"),
            LoginState.NoTerms                           => throw new CatLaunchException(CatCodes.LAUNCH_FAILED, "该账号尚未接受游玩使用条款"),
            _                                            => throw new CatLaunchException(CatCodes.AUTHORIZATION_REQUIRED, $"登录未成功 ({result.State}), 需要重新授权")
        };

    private async Task PersistQuickKeyAsync(string? newKey)
    {
        if (string.IsNullOrEmpty(newKey) || string.Equals(newKey, quickKey, StringComparison.Ordinal))
            return;

        quickKey = newKey;
        redactor.Register(newKey);

        try
        {
            var encrypted = await accountManager.Encrypt(newKey).ConfigureAwait(false);

            if (string.IsNullOrEmpty(encrypted))
                return;

            // 只改这个号自己那一行的快速登录凭证, 不动当前账号选择和设备设置
            account.SdoQuickLoginSecret = encrypted;
            accountManager.Save(account);
            Log.Information("[CatHost] 已更新这个号的快速登录凭证");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[CatHost] 保存新的快速登录凭证失败");
        }
    }

    private async Task<string> RefreshSessionIdByQuickLoginAsync()
    {
        await quickKeyLock.WaitAsync().ConfigureAwait(false);

        try
        {
            // WeGame 号没有盛趣快速登录凭证: 照界面版, 拿同一枚 WeGame 令牌再登录一次, 令牌不变所以没有要回写的
            var result = request.IsWeGame
                             ? await loginClient.LoginAsync(LoginType.WeGame, CreateWeGameLoginRequest(weGameToken!, false)).ConfigureAwait(false)
                             : await loginClient.LoginBySessionKey(account.UserName, quickKey!, dcTravel, device).ConfigureAwait(false);

            if (!request.IsWeGame)
                await PersistQuickKeyAsync(result.OAuthLogin?.QuickLoginSecret).ConfigureAwait(false);

            var oauth = result.OAuthLogin;

            if (oauth == null)
                return string.Empty;

            // 新 TGT 写回, 下次崩溃重启先用它换票据, 少登录一次; 守护记录也跟着换, 接管的进程拿到的是能用的那个
            if (!string.IsNullOrEmpty(oauth.TGT) && !string.IsNullOrEmpty(oauth.Guid) && context?.LoginResult.OAuthLogin is { } current)
            {
                redactor.Register(oauth.TGT);
                current.TGT  = oauth.TGT;
                current.Guid = oauth.Guid;
                await RewriteGameRecordCredentialsAsync().ConfigureAwait(false);
            }

            if (!string.IsNullOrEmpty(oauth.SessionID))
                return oauth.SessionID;

            if (string.IsNullOrEmpty(oauth.TGT) || string.IsNullOrEmpty(oauth.Guid))
                return string.Empty;

            var sessionId = await new LoginChannelContext(device).GetSessionIdAsync(oauth.TGT, oauth.Guid).ConfigureAwait(false);
            redactor.Register(sessionId);
            return sessionId;
        }
        finally
        {
            quickKeyLock.Release();
        }
    }

    /// <summary>
    ///     起游戏前现取一次性票据: 先用 TGT 换（不算登录）; 崩溃重启时 TGT 可能已过期, 换不到再用快速登录凭证（WeGame 号用 WeGame 令牌）刷新一次
    /// </summary>
    private async Task EnsureFreshSessionIdAsync(bool isRestart)
    {
        var oauthLogin = context.LoginResult.OAuthLogin!;
        Exception? error = null;

        if (!string.IsNullOrEmpty(oauthLogin.TGT) && !string.IsNullOrEmpty(oauthLogin.Guid))
        {
            redactor.Register(oauthLogin.TGT);

            // 用过的票据不能再用, 换不到新的就不能留着旧的
            oauthLogin.SessionID = null!;

            try
            {
                var loginContext = new LoginChannelContext(oauthLogin.DeviceProfile ?? device);
                oauthLogin.SessionID = await loginContext.GetSessionIdAsync(oauthLogin.TGT, oauthLogin.Guid).ConfigureAwait(false);
                redactor.Register(oauthLogin.SessionID);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[CatHost] 通过 TGT 获取 session ticket 失败");
                error = ex;
            }
        }

        // 崩溃重开: TGT 换票据失败（网络错误除外）, 或手里压根没有 TGT（接管来的游戏记录里没存或解不开）, 都用快速登录凭证刷新
        var noTgt = string.IsNullOrEmpty(oauthLogin.TGT) || string.IsNullOrEmpty(oauthLogin.Guid);

        if (isRestart && CanRefreshByLogin && (error != null ? CatLoginFailures.Classify(error) != CatLoginFailureKind.Network : noTgt))
        {
            try
            {
                Log.Information("[CatHost] 崩溃重启: TGT 已不可用, 用{Credential}刷新票据", request.IsWeGame ? " WeGame 令牌" : "快速登录凭证");
                var sessionId = await RefreshSessionIdByQuickLoginAsync().ConfigureAwait(false);

                if (!string.IsNullOrEmpty(sessionId))
                {
                    oauthLogin.SessionID = sessionId;
                    redactor.Register(sessionId);
                    error = null;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[CatHost] 用{Credential}刷新票据失败", request.IsWeGame ? " WeGame 令牌" : "快速登录凭证");
                error = ex;
            }
        }

        if (error != null)
        {
            throw new CatLaunchException
            (
                CatLoginFailures.ToCode(CatLoginFailures.Classify(error), CatCodes.AUTHORIZATION_REQUIRED),
                $"登录会话已过期或无法刷新: {error.Message}"
            );
        }

        if (string.IsNullOrEmpty(oauthLogin.SessionID))
            throw new CatLaunchException(CatCodes.AUTHORIZATION_REQUIRED, "登录异常: 没有拿到 session ticket");

        if (string.IsNullOrEmpty(oauthLogin.SndaID))
            throw new CatLaunchException(CatCodes.AUTHORIZATION_REQUIRED, "登录异常: SNDAID 为空");
    }

    /// <summary>
    ///     在账号库的 WeGame 行里找上号请求指的那一行: 先按账号名精确找; 找不到时再按备注找: 请求的号是纯数字时, 找备注里写着这个号的行
    ///     （备注按非数字字符切成几段数字, 某一段与请求的号完全相等才算, 免得 12345 对上 123456）; 不是纯数字时, 备注整串与它相同才算。
    ///     备注对上多行时不猜, 返回 null 并标 <see cref="CatWeGameAccountMatch.AmbiguousNote" />。没有账号名的残行不参与。
    /// </summary>
    internal static T? ResolveWeGameAccount<T>
    (
        IReadOnlyList<T>          rows,
        string?                   requestedName,
        Func<T, string?>          nameOf,
        Func<T, string?>          noteOf,
        out CatWeGameAccountMatch match
    )
        where T : class
    {
        match = CatWeGameAccountMatch.None;

        var requested = requestedName?.Trim();

        if (string.IsNullOrEmpty(requested))
            return null;

        if (rows.FirstOrDefault(x => string.Equals(nameOf(x), requested, StringComparison.Ordinal)) is { } byName)
        {
            match = CatWeGameAccountMatch.ByName;
            return byName;
        }

        var numeric = requested.All(char.IsAsciiDigit);
        var byNote = rows.Where
        (
            x => !string.IsNullOrWhiteSpace(nameOf(x))
                 && (numeric ? NoteMentionsNumber(noteOf(x), requested) : string.Equals(noteOf(x)?.Trim(), requested, StringComparison.Ordinal))
        ).ToArray();

        switch (byNote.Length)
        {
            case 0:
                return null;

            case 1:
                match = CatWeGameAccountMatch.ByNote;
                return byNote[0];

            default:
                match = CatWeGameAccountMatch.AmbiguousNote;
                return null;
        }
    }

    /// <summary>
    ///     备注里有没有一段连续数字正好等于 <paramref name="number" />
    /// </summary>
    private static bool NoteMentionsNumber(string? note, string number)
    {
        if (string.IsNullOrEmpty(note))
            return false;

        var start = -1;

        for (var i = 0; i <= note.Length; i++)
        {
            if (i < note.Length && char.IsAsciiDigit(note[i]))
            {
                if (start < 0)
                    start = i;

                continue;
            }

            if (start >= 0 && note.AsSpan(start, i - start).SequenceEqual(number))
                return true;

            start = -1;
        }

        return false;
    }

    /// <summary>
    ///     选启动大区: 上号请求带的（任务资料）优先; 请求没带时用账号库记的; 请求带了却对不上, 或都没有时返回 null。
    ///     账号库记的是游戏里最后一次换到的大区（标题画面选大区、超域都会改它）, 不代表角色所在的大区;
    ///     角色超域在别的大区时由自动进入按选角列表的「超域中」标记跟过去
    /// </summary>
    internal static T? ResolveArea<T>(IReadOnlyList<T> areas, string? savedAreaName, string? requestedAreaName, Func<T, string?> nameOf, out bool fromRequest)
        where T : class
    {
        fromRequest = !string.IsNullOrEmpty(requestedAreaName);

        if (fromRequest)
            return areas.FirstOrDefault(x => string.Equals(nameOf(x), requestedAreaName, StringComparison.Ordinal));

        return string.IsNullOrEmpty(savedAreaName)
                   ? null
                   : areas.FirstOrDefault(x => string.Equals(nameOf(x), savedAreaName, StringComparison.Ordinal));
    }

    private void SyncAreaFromDcTravel(string areaName)
    {
        var matched = context?.Areas.FirstOrDefault(x => string.Equals(x.AreaName, areaName, StringComparison.Ordinal));

        if (matched == null)
        {
            Log.Warning("[CatHost] 当前登录上下文中不存在大区 {AreaName}, 忽略同步请求", areaName);
            return;
        }

        account.AreaName = areaName;
        accountManager.Save(account);
        context!.Area = matched;
        Log.Information("[CatHost] 已同步启动大区为 {AreaName} (ID={AreaID})", areaName, matched.AreaID);
    }

    #endregion

    #region 启动与生命周期

    /// <summary>
    ///     起一次游戏并守到它退出; Dalamud 崩溃处理器要求重启时递归起新进程（进程号会变）。返回最后一个游戏进程。
    /// </summary>
    /// <param name="options">本次启动的 Dalamud 选项</param>
    /// <param name="restartedFromPid">崩溃重启时为旧进程号</param>
    /// <param name="reporter">报告</param>
    /// <param name="startToken">启动途中可被 close 取消</param>
    /// <param name="cancellationToken">外部取消（等游戏退出用）</param>
    private async Task<FFXIVProcess> RunGameAsync
    (
        RestartMonitor.RestartOptions options,
        int?                          restartedFromPid,
        ICatLaunchReporter            reporter,
        CancellationToken             startToken,
        CancellationToken             cancellationToken
    )
    {
        var (launched, dalamudOk, companionAppManager) = await StartOnceAsync(options, restartedFromPid, reporter, startToken).ConfigureAwait(false);

        return await GuardAsync(launched, dalamudOk, options, companionAppManager, null, reporter, startToken, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     守护一个已经在跑的游戏到它退出（正常启动与接管共用）: 注入了 Dalamud 的盯崩溃处理器, 要重开时递归 <see cref="RunGameAsync" />。
    ///     返回最后一个游戏进程（重开过则是新进程）。
    /// </summary>
    /// <param name="launched">游戏进程</param>
    /// <param name="watchCrashHandler">游戏里有 Dalamud（有崩溃处理器可盯）</param>
    /// <param name="options">本次的模式, 崩溃处理器回「按原模式重开」时用</param>
    /// <param name="companionAppManager">伴随程序（接管来的游戏为 null）</param>
    /// <param name="crashHandlerDiscoveryTimeout">找崩溃处理器的期限; null = 默认, 接管时给 0</param>
    /// <param name="reporter">报告</param>
    /// <param name="startToken">重开途中可被 close 取消</param>
    /// <param name="cancellationToken">外部取消</param>
    private async Task<FFXIVProcess> GuardAsync
    (
        FFXIVProcess                  launched,
        bool                          watchCrashHandler,
        RestartMonitor.RestartOptions options,
        CompanionAppManager?          companionAppManager,
        TimeSpan?                     crashHandlerDiscoveryTimeout,
        ICatLaunchReporter            reporter,
        CancellationToken             startToken,
        CancellationToken             cancellationToken
    )
    {
        FFXIVProcess result = launched;

        try
        {
            if (watchCrashHandler)
            {
                await launcher.RestartMonitor
                              .MonitorAsync
                              (
                                  launched,
                                  options,
                                  async restartOptions =>
                                  {
                                      CleanupProcess(launched, companionAppManager);

                                      // 交接停止已定: 不在本进程里重开（本进程马上退出, 重开出来的游戏没人守）;
                                      // 还在交接中就等结论, 没交接成照常重开
                                      if (IsCloseRequested || await HandOffDecidedAsync().ConfigureAwait(false))
                                          return null;

                                      // 交接没成: 上面那次收尾被挡下了, 补上（做过的不会重做）
                                      CleanupProcess(launched, companionAppManager);

                                      try
                                      {
                                          exitReason = null;
                                          result     = await RunGameAsync(restartOptions, launched.ProcessID, reporter, startToken, cancellationToken).ConfigureAwait(false);
                                          return result;
                                      }
                                      catch (CatLaunchException ex)
                                      {
                                          exitReason         = CatExitReasons.RESTART_FAILED;
                                          exitFailureCode    = ex.Code;
                                          exitFailureMessage = ex.Message;
                                          reporter.Log("error", $"崩溃后重启游戏失败: {ex.Message}");
                                          return null;
                                      }
                                      catch (OperationCanceledException) when (IsCloseRequested)
                                      {
                                          return null;
                                      }
                                  },
                                  cancellationToken,
                                  new RestartMonitor.MonitorOptions
                                  {
                                      CrashHandlerExitTimeout      = request.CrashDialogTimeout,
                                      CrashHandlerOutlivedGame     = reporter.Crashed,
                                      CrashHandlerTimedOut         = () => exitReason = CatExitReasons.CRASH_DIALOG_TIMEOUT,
                                      StopToken                    = closeCts.Token,
                                      CrashHandlerDiscoveryTimeout = crashHandlerDiscoveryTimeout
                                  }
                              )
                              .ConfigureAwait(false);
            }
            else
                await launched.UnderlyingProcess.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CleanupProcess(launched, companionAppManager);
        }

        return result;
    }

    private async Task<(FFXIVProcess Process, bool DalamudOk, CompanionAppManager? Companion)> StartOnceAsync
    (
        RestartMonitor.RestartOptions options,
        int?                          restartedFromPid,
        ICatLaunchReporter            reporter,
        CancellationToken             startToken
    )
    {
        var dalamudOk = false;
        DalamudSession? dalamudSession = null;

        if (request.Dalamud && !options.ForceNoDalamud)
        {
            reporter.Stage(CatStages.UPDATING_DALAMUD);

            var (session, error) = await Task.Run(() => PrepareDalamud(options.NoPlugins, options.NoThirdPlugins), startToken).ConfigureAwait(false);

            if (session == null)
            {
                // 首次启动时 Dalamud 不可用就不起游戏; 崩溃重启时游戏必须照常起来, 只报告 Dalamud 失败
                if (restartedFromPid == null)
                    throw new CatLaunchException(CatCodes.DALAMUD_UNAVAILABLE, error!);

                reporter.Agent(CatAgentKinds.DALAMUD, false, CatCodes.DALAMUD_UNAVAILABLE, error);
            }
            else
            {
                dalamudSession = session;
                dalamudOk      = true;
            }
        }

        // 接管时没能绑回原端口（或原来就没开成）: 新起的游戏换一个端口重新开跨区, 新进程的命令行会带上它
        if (restartedFromPid != null && context.DcTravelPort == 0 && dcTravel != null)
        {
            context.DcTravelPort = await dcTravel.StartAsync().ConfigureAwait(false);
            Log.Information("[CatHost] 崩溃重开前重新打开跨区服务, 端口 {Port}", context.DcTravelPort);
        }

        // 票据单次有效且有时效, Dalamud 准备完再现取
        await EnsureFreshSessionIdAsync(restartedFromPid != null).ConfigureAwait(false);

        context.InGameAgents = (dalamudOk ? InGameAgents.Dalamud : InGameAgents.None) | (request.Minion ? InGameAgents.Minion : InGameAgents.None);
        Log.Information("[CatHost] 本次启动的游戏内代理: {InGameAgents}", context.InGameAgents);

        startToken.ThrowIfCancellationRequested();
        reporter.Stage(CatStages.STARTING);

        // 进程创建本身不可取消; 创建期间收到 close 时, 创建完由这里负责关掉
        var launched = await Task.Run(() => LaunchProcess(dalamudOk, dalamudSession), CancellationToken.None).ConfigureAwait(false);
        var process  = launched.UnderlyingProcess;
        bool closeNow;

        // 先记下进程再做别的: 之后任何异常都按「游戏已起来」处理, 不会把活着的游戏报成启动失败
        lock (closeLock)
        {
            currentProcess = launched;
            closeNow       = closeRequested;
        }

        var startedAt = SafeProcessStartedAt(process);

        // 先写守护记录再报 started: 外壳一拿到进程号, 这个游戏就必须是接管得了的
        await WriteGameRecordAsync(process, startedAt, options, dalamudOk).ConfigureAwait(false);

        if (restartedFromPid is { } oldPid)
            reporter.Restarted(oldPid, launched.ProcessID, startedAt);
        else
            reporter.Started(launched.ProcessID, startedAt);

        RunningGameRegistry.Register(process, context.InGameAgents, context.DcTravelPort);

        CompanionAppManager? companionAppManager = null;

        try
        {
            companionAppManager = companionAppService.StartCompanionApps(launched.ProcessID);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[CatHost] 启动伴随程序失败");
            reporter.Log("warning", $"启动伴随程序失败: {ex.Message}");
        }

        if (closeNow)
        {
            _ = CatGameCloser.CloseAsync(process, closeTimeout);
            return (launched, dalamudOk, companionAppManager);
        }

        try
        {
            if (dalamudOk)
            {
                reporter.Stage(CatStages.INJECTING);
                var loaded = await WaitForDalamudAsync(process, startToken).ConfigureAwait(false);
                reporter.Agent(CatAgentKinds.DALAMUD, loaded, loaded ? null : CatCodes.DALAMUD_UNAVAILABLE, loaded ? null : "游戏进程里没有等到 Dalamud 加载");
            }

            // Minion 在自动进入角色的编排开始之前挂好, 带不带 autoEnter 时机都一样
            if (request.Minion)
            {
                await AttachMinionAsync(launched, dalamudOk, reporter, startToken).ConfigureAwait(false);

                if (MiniModuleInjector.ModulePath.Exists && !IsCloseRequested)
                {
                    try
                    {
                        await MiniModuleGate.RunAsync(process, startToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "[CatHost] 游戏内模块自检时发生异常（游戏不受影响）");
                    }
                }
            }
        }
        catch (OperationCanceledException) when (IsCloseRequested)
        {
            Log.Information("[CatHost] 收到关闭请求, 不再等注入");
        }

        if (!process.HasExited && !IsCloseRequested)
        {
            reporter.Stage(CatStages.RUNNING);

            if (request.AutoEnter)
                StartAutoEnter(launched, reporter, startToken);
        }

        return (launched, dalamudOk, companionAppManager);
    }

    /// <summary>
    ///     在后台自动进入角色（不挡崩溃守护）: 编排 → 之后隔一段时间看一次当前角色。带 Minion 的在调用之前已经挂好。
    ///     等人选角色、排队都可能很久, 所以不在启动流程里等; 游戏进程收尾时取消。
    ///     崩溃重启后的新进程接着登录上一次实际进的那个角色。
    /// </summary>
    private void StartAutoEnter(FFXIVProcess launched, ICatLaunchReporter reporter, CancellationToken startToken)
    {
        var process      = launched.UnderlyingProcess;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(startToken);
        var game         = new CatAutoEnterRealGame(process, dcTravel!.Client, context.Areas, context.Area.AreaName, RememberEnteredArea);
        var flow         = new CatAutoEnter(game, reporter, new CatAutoEnterTarget(request.CharacterName, request.CharacterHomeWorld, autoEnter?.EnteredContentId));
        var token        = cancellation.Token;

        autoEnter = flow;

        lock (cleanupLock)
        {
            autoEnterCancellations[launched.ProcessID] = cancellation;
            autoEnterTasks.Add
            (
                Task.Run
                (async () =>
                    {
                        try
                        {
                            await flow.RunAsync(token).ConfigureAwait(false);
                            await flow.ObserveAsync(token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            // 游戏退出或收到关闭请求
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "[CatHost] 自动进入角色的后台任务出错（游戏不受影响）");
                        }
                        finally
                        {
                            game.Dispose();
                        }
                    }
                )
            );
        }
    }

    /// <summary>
    ///     角色换了大区才进的游戏: 把账号库和本次启动的大区改成它（换大厅本身不回写）, 下次启动、崩溃重启都从那里进
    /// </summary>
    private void RememberEnteredArea(string areaName)
    {
        if (!string.Equals(context.Area.AreaName, areaName, StringComparison.Ordinal))
            SyncAreaFromDcTravel(areaName);
    }

    /// <summary>
    ///     等自动进入角色的后台任务结束, 保证它们的事件都排在 game.exited 之前
    /// </summary>
    private async Task WaitAutoEnterEndAsync()
    {
        Task[] tasks;

        lock (cleanupLock)
        {
            foreach (var cancellation in autoEnterCancellations.Values)
                cancellation.Cancel();

            tasks = autoEnterTasks.ToArray();
        }

        if (tasks.Length == 0)
            return;

        try
        {
            await Task.WhenAll(tasks).WaitAsync(AutoEnterShutdownTimeout).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[CatHost] 等自动进入角色的后台任务结束超时");
        }
    }

    private FFXIVProcess LaunchProcess(bool dalamudOk, DalamudSession? dalamudSession)
    {
        var settings          = App.Settings;
        var oauthLogin        = context.LoginResult.OAuthLogin!;
        var dotnetRuntimePath = App.Dalamud.Updater.Runtime;

        Process? StartGame(GameStartRequest startRequest)
        {
            if (dalamudOk && dalamudSession != null)
            {
                var compat = "RunAsInvoker ";
                compat += startRequest.DpiAwareness switch
                {
                    DPIAwareness.Aware   => "HighDPIAware",
                    DPIAwareness.Unaware => "DPIUnaware",
                    _                    => throw new ArgumentOutOfRangeException()
                };
                startRequest.Environment.Add("__COMPAT_LAYER", compat);

                if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DALAMUD_RUNTIME")))
                    startRequest.Environment.Add("DALAMUD_RUNTIME", dotnetRuntimePath.FullName);

                if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOTNET_ROOT")))
                    startRequest.Environment.Add("DOTNET_ROOT", dotnetRuntimePath.FullName);

                if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOTNET_MULTILEVEL_LOOKUP")))
                    startRequest.Environment.Add("DOTNET_MULTILEVEL_LOOKUP", "0");

                return dalamudSession.LaunchGame(new FileInfo(startRequest.ExePath), startRequest.Arguments, startRequest.Environment);
            }

            return NativeAclFix.LaunchGame
            (
                startRequest.WorkingDirectory,
                startRequest.ExePath,
                startRequest.Arguments,
                startRequest.Environment,
                startRequest.DpiAwareness,
                process => new GameArgumentInterop.Fixer(process).Fix()
            );
        }

        FFXIVProcess? launched;

        try
        {
            launched = launcher.LaunchGame
            (
                StartGame,
                oauthLogin.SessionID,
                oauthLogin.SndaID,
                context.DcTravelPort,
                context.Area.AreaID,
                context.Area.AreaLobby,
                context.Area.AreaGM,
                context.Area.AreaConfigUpload,
                Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(context.Areas))),
                settings.AdditionalLaunchArgs,
                gamePath,
                settings.EncryptArgumentsV2,
                settings.DPIAwareness
            );
        }
        catch (BinaryNotPresentException ex)
        {
            Log.Error(ex, "[CatHost] 找不到游戏可执行文件");
            throw new CatLaunchException(CatCodes.INVALID_GAME_PATH, "找不到游戏可执行文件, 可能需要重新安装游戏");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[CatHost] 启动游戏进程失败");
            throw new CatLaunchException(CatCodes.LAUNCH_FAILED, $"启动游戏进程失败: {ex.GetType().Name}: {ex.Message}");
        }

        Troubleshooting.LogTroubleshooting(gamePath);

        return launched ?? throw new CatLaunchException(CatCodes.LAUNCH_FAILED, "游戏进程为空");
    }

    private void CleanupProcess(FFXIVProcess launched, CompanionAppManager? companionAppManager)
    {
        // 交接停止: 游戏、记录、端口文件、伴随程序都留给接管的进程, 也不报 Minion 停机
        if (handingOff)
            return;

        lock (cleanupLock)
        {
            if (!cleanedPids.Add(launched.ProcessID))
                return;

            if (autoEnterCancellations.TryGetValue(launched.ProcessID, out var autoEnterCancellation))
                autoEnterCancellation.Cancel();
        }

        try
        {
            companionAppService.StopCompanionApps(launched.ProcessID, companionAppManager);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[CatHost] 停止伴随程序失败");
        }

        RunningGameRegistry.Unregister(launched.ProcessID);
        DeleteGameRecord(launched.ProcessID);

        lastPid      = launched.ProcessID;
        lastExitCode = TryGetExitCode(launched);

        Log.Information("[CatHost] 游戏进程已退出 (PID={ProcessID}, ExitCode=0x{ExitCode:X8})", launched.ProcessID, (uint)(lastExitCode ?? 0));

        try
        {
            // 告诉 MINIONAPP 这一行停机了, 否则它会过一分钟自己拉个新客户端
            MinionAppStatusReporter.ReportStopped(launched.ProcessID);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[CatHost] 补报 Minion 停机失败");
        }
    }

    private static int? TryGetExitCode(FFXIVProcess process)
    {
        try
        {
            return process.UnderlyingProcess.HasExited ? process.ExitCode : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    #endregion

    #region 守护记录

    /// <summary>
    ///     写这个游戏的守护记录, 本进程死了之后新进程凭它接管（adopt）。写失败只记日志, 不影响游戏
    /// </summary>
    /// <param name="process">游戏进程</param>
    /// <param name="startedAt">游戏进程创建时间</param>
    /// <param name="options">本次启动的模式, 也是崩溃处理器回「按原模式重开」时用的模式</param>
    /// <param name="dalamudOk">本次是否注入了 Dalamud（决定接管后要不要盯崩溃处理器）</param>
    private async Task WriteGameRecordAsync(Process process, DateTimeOffset startedAt, RestartMonitor.RestartOptions options, bool dalamudOk)
    {
        // 先认领守护权: 拿着它, 别的进程就不能来接管这个游戏（本进程死了系统自动放掉）
        if (!records.Claim(process.Id, startedAt))
            Log.Warning("[CatHost] 游戏 {Pid} 的守护锁拿不到（不该发生）, 照常守护但别的进程可能也来接管", process.Id);

        await records.WriteNewAsync(process.Id, startedAt, () => BuildRecordAsync(process, startedAt, options, dalamudOk)).ConfigureAwait(false);
    }

    private async Task<GameRecord?> BuildRecordAsync(Process process, DateTimeOffset startedAt, RestartMonitor.RestartOptions options, bool dalamudOk)
    {
        var oauth = context.LoginResult.OAuthLogin;

        return new GameRecord
            {
                Pid                       = process.Id,
                ProcessStartedAt          = startedAt,
                OperationId               = request.OperationId,
                Channel                   = request.IsWeGame ? GameRecordChannels.WE_GAME : GameRecordChannels.SDO,
                AccountName               = request.AccountName,
                AccountUserName           = account.UserName,
                AreaName                  = context.Area.AreaName,
                Dalamud                   = dalamudOk,
                DalamudRequested          = request.Dalamud,
                RestartNoDalamud          = options.ForceNoDalamud,
                RestartNoThirdPlugins     = options.NoThirdPlugins,
                RestartNoPlugins          = options.NoPlugins,
                DcTravelPort              = context.DcTravelPort,
                SndaId                    = oauth?.SndaID,
                Tgt                       = await EncryptOrNullAsync(oauth?.TGT).ConfigureAwait(false),
                Guid                      = await EncryptOrNullAsync(oauth?.Guid).ConfigureAwait(false),
                MinionFingerprint         = request.CardFingerprint,
                MinionVariant             = request.Variant,
                AutoEnter                 = request.AutoEnter,
                CharacterName             = request.CharacterName,
                CharacterHomeWorld        = request.CharacterHomeWorld,
                CrashDialogTimeoutSeconds = request.CrashDialogTimeoutSeconds,
                GuardPid                  = Environment.ProcessId,
                GuardStartedAt            = GameRecordWriter.SelfStartedAt,
                UpdatedAt                 = DateTimeOffset.UtcNow
            };
    }

    /// <summary>
    ///     换到新 TGT 后更新当前游戏守护记录里的凭证; 只改本进程守着、而且还没删的那份（核对创建时间, 防进程号复用）
    /// </summary>
    private Task RewriteGameRecordCredentialsAsync() =>
        records.WriteAsync
        (async () =>
            {
                if (currentProcess is not { } process)
                    return null;

                if (!records.TryGetRecorded(process.ProcessID, out var startedAt))
                    return null;

                if (GameRecords.ReadMatching(process.ProcessID, startedAt) is not { } record)
                    return null;

                var oauth = context.LoginResult.OAuthLogin;

                return record with
                {
                    Tgt = await EncryptOrNullAsync(oauth?.TGT).ConfigureAwait(false),
                    Guid = await EncryptOrNullAsync(oauth?.Guid).ConfigureAwait(false),
                    UpdatedAt = DateTimeOffset.UtcNow
                };
            }
        );

    /// <summary>
    ///     游戏退出（或崩溃重开换了新进程）时删掉它的守护记录并放掉守护锁
    /// </summary>
    private void DeleteGameRecord(int processId) =>
        records.Delete(processId);

    /// <summary>
    ///     加密凭证写进记录; 账号库选了「不加密」时不存（记录目录本机所有用户可读）
    /// </summary>
    private Task<string?> EncryptOrNullAsync(string? text) =>
        GameRecordWriter.EncryptOrNullAsync(accountManager, text);

    private async Task<string?> DecryptOrNullAsync(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return null;

        try
        {
            var plain = await accountManager.Decrypt(text).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(plain))
                redactor.Register(plain);

            return plain;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[CatHost] 解密守护记录里的凭证失败");
            return null;
        }
    }

    #endregion

    #region Dalamud 与 Minion

    private (DalamudSession? Session, string? Error) PrepareDalamud(bool noPlugins, bool noThird)
    {
        try
        {
            new DalamudCompatibilityCheck().EnsureCompatibility();
        }
        catch (IDalamudCompatibilityCheck.NoRedistsException ex)
        {
            Log.Error(ex, "[CatHost] 未找到 Dalamud 所需的 Redists");
            return (null, "Dalamud 需要安装 Microsoft Visual C++ 2015-2019 Redistributable");
        }
        catch (IDalamudCompatibilityCheck.ArchitectureNotSupportedException ex)
        {
            Log.Error(ex, "[CatHost] 不受支持的本地环境架构");
            return (null, "Dalamud 仅支持 64 位 Windows");
        }

        try
        {
            var settings = App.Settings;
            var session = App.Dalamud.CreateLauncher
            (
                gamePath,
                new DalamudLaunchOptions(settings.DalamudLoadMethod, (int)settings.DalamudInjectionDelayMS, false, noPlugins, noThird)
            );

            App.Dalamud.RunUpdater();

            return session.EnsureReady(gamePath) == DalamudSession.DalamudInstallState.Ok
                       ? (session, null)
                       : (null, "Dalamud 尚未准备完成（下载或校验未通过）");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[CatHost] 准备 Dalamud 时发生错误");
            return (null, $"下载 Dalamud 相关文件异常: {ex.Message}");
        }
    }

    private async Task InjectDalamudIntoRunningAsync(FFXIVProcess process, ICatLaunchReporter reporter, CancellationToken cancellationToken)
    {
        if (IsDalamudLoaded(process.UnderlyingProcess))
        {
            reporter.Agent(CatAgentKinds.DALAMUD, true, null, "Dalamud 已在游戏里");
            return;
        }

        reporter.Stage(CatStages.UPDATING_DALAMUD);

        var (session, error) = await Task.Run(() => PrepareDalamud(false, false), cancellationToken).ConfigureAwait(false);

        if (session == null)
        {
            reporter.Agent(CatAgentKinds.DALAMUD, false, CatCodes.DALAMUD_UNAVAILABLE, error);
            return;
        }

        reporter.Stage(CatStages.INJECTING);

        try
        {
            await Task.Run(() => session.InjectGame(process.ProcessID), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[CatHost] 补注入 Dalamud 失败");
            reporter.Agent(CatAgentKinds.DALAMUD, false, CatCodes.DALAMUD_UNAVAILABLE, $"补注入 Dalamud 失败: {ex.Message}");
            return;
        }

        var loaded = await WaitForDalamudAsync(process.UnderlyingProcess, cancellationToken).ConfigureAwait(false);
        reporter.Agent(CatAgentKinds.DALAMUD, loaded, loaded ? null : CatCodes.DALAMUD_UNAVAILABLE, loaded ? null : "游戏进程里没有等到 Dalamud 加载");
    }

    private async Task AttachMinionAsync(FFXIVProcess launched, bool dalamudInjected, ICatLaunchReporter reporter, CancellationToken cancellationToken)
    {
        reporter.Stage(CatStages.ATTACHING_MINION);

        var (error, reserved) = await CatMinionReservations.ReserveAsync(request.MinionCard, launched.UnderlyingProcess, request.AccountName, SafeProcessStartedAt)
                                                           .ConfigureAwait(false);

        if (error is { } failure)
        {
            reporter.Agent(CatAgentKinds.MINION, false, failure.Code, failure.Message);
            return;
        }

        var ok = false;

        try
        {
            // 占用记录里写上号请求带的账号名（外壳认得的那个）: 按备注找到的 WeGame 号, 它与账号库里的账号名不同
            var result = await MinionAttacher.AttachAsync(request.MinionCard!, launched.UnderlyingProcess, gamePath, dalamudInjected, request.AccountName, cancellationToken)
                                             .ConfigureAwait(false);
            ok = result.Ok;

            if (IsCloseRequested)
                Log.Information("[CatHost] 收到关闭请求, Minion 挂载结果不再上报: {Ok}", result.Ok);
            else if (result.Ok)
                reporter.Agent(CatAgentKinds.MINION, true);
            else
            {
                Log.Error("[CatHost] Minion 挂载失败: {Error}", result.Error);
                reporter.Agent(CatAgentKinds.MINION, false, CatCodes.ATTACH_FAILED, result.Error);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[CatHost] 挂载 Minion 时发生未处理异常");
            reporter.Agent(CatAgentKinds.MINION, false, CatCodes.ATTACH_FAILED, $"挂载 Minion 时出错: {ex.Message}");
        }
        finally
        {
            // 没挂上就撤掉预占, 别让这张卡一直显示被占用
            if (reserved && !ok)
                MinionOccupancy.Delete(launched.ProcessID);
        }
    }

    /// <summary>
    ///     进程创建时间; 读不到时用当前时间近似并记日志
    /// </summary>
    private static DateTimeOffset SafeProcessStartedAt(Process process)
    {
        try
        {
            return MinionOccupancy.GetProcessStartedAt(process);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[CatHost] 读取游戏进程创建时间失败, 用当前时间代替");
            return DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }
    }

    private static async Task<bool> WaitForDalamudAsync(Process process, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        while (stopwatch.Elapsed < DalamudInjectTimeout)
        {
            if (process.HasExited)
                return false;

            if (IsDalamudLoaded(process))
                return true;

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
        }

        return IsDalamudLoaded(process);
    }

    private static bool IsDalamudLoaded(Process process)
    {
        try
        {
            return !process.HasExited && FFXIVProcess.IsDalamudInjected(process);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[CatHost] 检查 Dalamud 是否注入失败");
            return false;
        }
    }

    #endregion
}
