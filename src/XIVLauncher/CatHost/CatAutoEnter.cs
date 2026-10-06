using System.Globalization;
using System.Text.RegularExpressions;
using Serilog;
using XIVLauncher.InGame;

namespace XIVLauncher.CatHost;

/// <summary>服务器表里的一个服务器: 游戏内部代号、中文名、所属大区</summary>
public sealed record CatAutoEnterWorld(string Code, string Name, string AreaName);

/// <summary>
///     要登录的角色: 都可以为空。ContentId 只在崩溃重启后接着上一次进的那个角色时有
/// </summary>
public sealed record CatAutoEnterTarget(string? Name, string? HomeWorld, string? ContentId = null);

/// <summary>自动进入角色的结局</summary>
public enum CatAutoEnterOutcome
{
    /// <summary>角色已进入游戏</summary>
    InWorld,

    /// <summary>停在当前界面交给人（已发 game.autoEnterStopped）</summary>
    Stopped,

    /// <summary>被取消（关闭请求）或游戏已退出</summary>
    Cancelled
}

/// <summary>
///     自动进入角色的编排要用到的游戏一侧: 模块通道、换大厅、服务器表、时钟。真实实现是 <see cref="CatAutoEnterRealGame" />, 测试里换成假的。
///     <see cref="IMiniModuleChannel.SendAsync" /> 在这里的约定: 需要时自己占闸、连管道; 管道被别的操作占着或连不上就抛异常。
/// </summary>
public interface ICatAutoEnterGame : IMiniModuleChannel
{
    /// <summary>等游戏窗口出现并注入模块; 返回 null 表示成功, 否则是失败原因</summary>
    Task<string?> AttachAsync(CancellationToken cancellationToken);

    /// <summary>放开管道和闸（长时间等待期间不占着, 别的操作才用得了模块）; 下次发命令时再连</summary>
    void Release();

    /// <summary>模块的管道是否正被别的操作占着（换区任务等）</summary>
    bool ModuleBusy { get; }

    /// <summary>游戏进程是否已退出</summary>
    bool HasExited { get; }

    /// <summary>游戏现在连着的大区名</summary>
    string CurrentAreaName { get; }

    /// <summary>全部大区名, 按列表顺序</summary>
    IReadOnlyList<string> AreaNames { get; }

    /// <summary>服务器表（代号、中文名、所属大区）; 拿不到时返回空表</summary>
    Task<IReadOnlyList<CatAutoEnterWorld>> LoadWorldsAsync(CancellationToken cancellationToken);

    /// <summary>
    ///     只换大厅（现签票据, 不重新登录账号, 不下超域订单）, 发出「开始游戏」后返回; 返回 null 表示成功, 否则是失败原因。
    ///     成功后 <see cref="CurrentAreaName" /> 变为新大区。
    /// </summary>
    Task<string?> SwitchAreaAsync(string areaName, CancellationToken cancellationToken);

    /// <summary>角色已在这个大区进入游戏: 记下来, 下次启动和崩溃重启都从这个大区进</summary>
    void AreaEntered(string areaName);

    /// <summary>等一段时间</summary>
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);

    /// <summary>单调时钟</summary>
    TimeSpan Elapsed { get; }
}

/// <summary>
///     各步的等待上限与轮询间隔。模块行为实机验证后按实测调整; 排队和等人选角色没有上限。
/// </summary>
public sealed class CatAutoEnterTimings
{
    /// <summary>读界面状态的间隔</summary>
    public TimeSpan Poll { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>排队期间读界面状态的间隔（期间不占管道）</summary>
    public TimeSpan QueuePoll { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>等人选角色期间读界面状态的间隔（期间不占管道）</summary>
    public TimeSpan ChoicePoll { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>编排结束后看当前角色的间隔</summary>
    public TimeSpan ObserveInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>从游戏起来到出现标题界面最多等多久（含启动画面、片头）</summary>
    public TimeSpan TitleTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>点了「开始游戏」到进入选角界面最多等多久（大厅排队不算）</summary>
    public TimeSpan CharaSelectTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>进入选角界面后, 角色列表为空时再等多久才认定这个大区没有角色</summary>
    public TimeSpan CharaListSettle { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>游戏过了启动阶段之后, 界面状态连续读不出来最多容忍多久</summary>
    public TimeSpan LobbyUnreadableTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>切服务器、选中角色这类命令, 列表还没载入时最多重试多久</summary>
    public TimeSpan CommandSettle { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>客户端提示「稍后再登录」（暂时锁定）时最多等多久</summary>
    public TimeSpan LockedTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>点了角色到出现登录确认框最多等多久</summary>
    public TimeSpan ConfirmTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>确认登录到进入游戏最多等多久（排队不算）</summary>
    public TimeSpan EnterTimeout { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>进入游戏后到读得到当前角色最多等多久（读盘）</summary>
    public TimeSpan WorldLoadTimeout { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>和模块通话失败后等多久再看游戏进程还在不在（游戏崩溃时管道比进程先断）</summary>
    public TimeSpan ExitGrace { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>
///     自动进入角色: 游戏起来后, 经标题、选角登录到指定角色并进入游戏。唯一的编排者 —— 游戏内模块一次只做一步,
///     等待、判断、换大区都在这里; 对工作台只报阶段、角色列表、实际进入的角色和停手原因。
///     任何一步出问题都停在当前界面交给人: 不关游戏、不重新登录账号。
///     工作台靠这个类型在不在程序集里判断登录器支不支持自动进入, 改名要同步改工作台外壳的探测。
/// </summary>
public sealed class CatAutoEnter
{
    /// <summary>排队中想取消时的确认框文字里有这一句: 这种确认框不替人点</summary>
    public const string CANCEL_LOGIN_PROMPT = "取消登录";

    /// <summary>排队提示框（SelectOk）的文字里有这个词</summary>
    public const string QUEUE_PROMPT = "排队";

    private const byte LOGIN_FLAGS_BLOCKING = 1 | 2 | 4;

    private readonly ICatAutoEnterGame   game;
    private readonly ICatLaunchReporter  reporter;
    private readonly CatAutoEnterTarget  target;
    private readonly CatAutoEnterTimings timings;
    private readonly object              stateLock = new();

    private readonly Dictionary<int, string> worldCodes = [];

    private IReadOnlyList<CatAutoEnterWorld> worlds = [];
    private IReadOnlyList<CharaSelectReader.Entry> knownEntries = [];
    private TaskCompletionSource<string>? pendingChoice;
    private HashSet<string>               choosable = [];
    private string?                       currentStage;
    private int?                          reportedQueue;
    private string?                       enteredContentId;
    private TimeSpan?                     lobbyUnreadableSince;

    /// <summary>
    ///     创建一次编排
    /// </summary>
    /// <param name="game">游戏一侧</param>
    /// <param name="reporter">向工作台报告</param>
    /// <param name="target">要登录的角色</param>
    /// <param name="timings">等待上限与轮询间隔, 不给用默认值</param>
    public CatAutoEnter(ICatAutoEnterGame game, ICatLaunchReporter reporter, CatAutoEnterTarget target, CatAutoEnterTimings? timings = null)
    {
        this.game     = game;
        this.reporter = reporter;
        this.target   = target;
        this.timings  = timings ?? new CatAutoEnterTimings();
    }

    /// <summary>编排最后报的阶段; 还没开始时为 null。补注入、挂 Minion 改过阶段后用它恢复</summary>
    public string? CurrentStage
    {
        get
        {
            lock (stateLock)
                return currentStage;
        }
    }

    /// <summary>最近一次报告的已进入游戏的角色</summary>
    public string? EnteredContentId
    {
        get
        {
            lock (stateLock)
                return enteredContentId;
        }
    }

    /// <summary>
    ///     员工选了角色: 只在等人选角色时接受, 且必须是 game.characters 里列出的角色
    /// </summary>
    public CatAcceptResult SelectCharacter(string contentId)
    {
        lock (stateLock)
        {
            if (pendingChoice == null)
                return CatAcceptResult.Rejected(CatCodes.NOT_RUNNING, "当前没有在等人选角色");

            if (!choosable.Contains(contentId))
                return CatAcceptResult.Rejected(CatCodes.INVALID_PARAMS, "角色列表里没有这个角色");

            pendingChoice.TrySetResult(contentId);
            return CatAcceptResult.Ok();
        }
    }

    /// <summary>
    ///     跑一次编排, 直到角色进入游戏、停手或被取消
    /// </summary>
    public async Task<CatAutoEnterOutcome> RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RunCoreAsync(cancellationToken).ConfigureAwait(false);
            return CatAutoEnterOutcome.InWorld;
        }
        catch (StopException stop)
        {
            return Stop(stop.Code, stop.Message);
        }
        catch (OperationCanceledException)
        {
            if (!game.HasExited)
                reporter.AutoEnterStopped(CatAutoEnterStopCodes.CANCELLED, "收到关闭请求, 自动进入角色没有做完");

            return CatAutoEnterOutcome.Cancelled;
        }
        catch (Exception ex)
        {
            if (game.HasExited)
                return CatAutoEnterOutcome.Cancelled;

            Log.Error(ex, "[CatAutoEnter] 编排出现未处理异常");
            return Stop(CatAutoEnterStopCodes.MODULE_UNAVAILABLE, $"自动进入角色时出错: {ex.Message}");
        }
        finally
        {
            lock (stateLock)
                pendingChoice = null;

            game.Release();
        }
    }

    /// <summary>
    ///     编排结束或停手之后: 隔一段时间看一次游戏里当前是谁, 角色变了（人手动进了游戏、换了角色）就再报一次。
    ///     管道被别的操作占着时跳过这一次; 游戏退出或取消即结束。
    /// </summary>
    public async Task ObserveAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && !game.HasExited)
        {
            await game.DelayAsync(timings.ObserveInterval, cancellationToken).ConfigureAwait(false);

            if (game.HasExited)
                return;

            if (game.ModuleBusy)
                continue;

            try
            {
                var who = CatModuleReplies.ParseWhoAmI(await game.SendAsync("WHOAMI", cancellationToken).ConfigureAwait(false));

                if (who is { Loaded: true } && who.ContentId != EnteredContentId)
                    ReportEntered(who);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[CatAutoEnter] 这一次没看成当前角色");
            }
            finally
            {
                game.Release();
            }
        }
    }

    /// <summary>
    ///     按规则定要登录的角色:
    ///     ① 指定了 contentId 且在列表里; ② 名字唯一匹配（同名多个时按原始服务器再筛）; ③ 没给名字或匹配不到, 列表里只有一个角色;
    ///     其余（多个角色定不了）要人选。列表为空时两个返回值都是空。
    ///     ③ 是猜的, 所以只在列表完整（<paramref name="listComplete" />）、且不是在找上一次进的那个角色（target 带 contentId）时才用:
    ///     否则宁可让人选, 也不登录一个没人指定过的角色。
    /// </summary>
    internal static (CharaSelectReader.Entry? Entry, bool NeedsChoice) Resolve
    (
        IReadOnlyList<CharaSelectReader.Entry> entries,
        CatAutoEnterTarget                     target,
        IReadOnlyList<CatAutoEnterWorld>       worlds,
        bool                                   listComplete = true
    )
    {
        if (entries.Count == 0)
            return (null, false);

        var resuming = !string.IsNullOrEmpty(target.ContentId);

        if (resuming && entries.FirstOrDefault(x => x.ContentId == target.ContentId) is { } byId)
            return (byId, false);

        if (!string.IsNullOrWhiteSpace(target.Name))
        {
            var name    = target.Name.Trim();
            var matches = entries.Where(x => string.Equals(x.Name, name, StringComparison.Ordinal)).ToArray();

            if (matches.Length > 1 && !string.IsNullOrWhiteSpace(target.HomeWorld))
                matches = matches.Where(x => WorldMatches(target.HomeWorld.Trim(), x.HomeWorldCode, WorldName(worlds, x.HomeWorldCode))).ToArray();

            switch (matches.Length)
            {
                case 1:
                    return (matches[0], false);

                case > 1:
                    return (null, true);
            }
        }

        return entries.Count == 1 && listComplete && !resuming ? (entries[0], false) : (null, true);
    }

    /// <summary>
    ///     请求里的服务器（Cat 的枚举名或中文名）与游戏内部代号是不是同一个: 忽略大小写; 代号末尾的数字（如 HongChaChuan2）不比
    /// </summary>
    internal static bool WorldMatches(string requested, string code, string? chineseName)
    {
        if (string.Equals(requested, code, StringComparison.OrdinalIgnoreCase))
            return true;

        if (chineseName != null && string.Equals(requested, chineseName, StringComparison.Ordinal))
            return true;

        return string.Equals(requested.TrimEnd("0123456789".ToCharArray()), code.TrimEnd("0123456789".ToCharArray()), StringComparison.OrdinalIgnoreCase);
    }

    #region 编排

    private async Task RunCoreAsync(CancellationToken cancellationToken)
    {
        SetStage(CatStages.ENTERING_LOBBY);

        // 1. 注入、握手
        if (await game.AttachAsync(cancellationToken).ConfigureAwait(false) is { } attachError)
            throw new StopException(CatAutoEnterStopCodes.MODULE_UNAVAILABLE, $"游戏内模块没有注入成功: {attachError}");

        var (versionOk, version, versionResponse) = await MiniModuleClient.HandshakeAsync(new Channel(this, "问模块版本"), MiniModuleClient.AutoEnterMinimumVersion, cancellationToken)
                                                                            .ConfigureAwait(false);

        if (!versionOk)
        {
            throw version == null
                      ? new StopException(CatAutoEnterStopCodes.MODULE_UNAVAILABLE, $"游戏内模块没有报出版本: {CatModuleReplies.FirstLine(versionResponse)}")
                      : new StopException
                      (
                          CatAutoEnterStopCodes.MODULE_OUTDATED,
                          $"游戏里注着的模块是 {version} 版, 自动进入角色要 {MiniModuleClient.AutoEnterMinimumVersion} 以上; 更新 DcMiniLauncher 后重启游戏"
                      );
        }

        worlds = await game.LoadWorldsAsync(cancellationToken).ConfigureAwait(false);

        // 2. 标题 → 开始游戏 → 选角界面
        if (!await WaitForCharaSelectAsync(true, cancellationToken).ConfigureAwait(false))
        {
            await WaitInWorldAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var visitedAreas   = new HashSet<string>(StringComparer.Ordinal) { game.CurrentAreaName };
        var followedTarget = false;
        var wanted         = target;

        while (true)
        {
            // 3. 读整个大区的角色
            var (entries, listComplete) = await ReadCharactersAsync(cancellationToken).ConfigureAwait(false);

            if (entries.Count == 0)
            {
                if (followedTarget)
                    throw new StopException(CatAutoEnterStopCodes.SWITCH_AREA_FAILED, $"换到 {game.CurrentAreaName} 后选角界面没有角色");

                // 角色全在别的大区（或新号）: 挨个大区找一遍
                var next = game.AreaNames.FirstOrDefault(x => !visitedAreas.Contains(x))
                           ?? throw new StopException(CatAutoEnterStopCodes.NO_CHARACTER, "这个号在各个大区的选角界面里都没有角色");

                visitedAreas.Add(next);

                if (!await SwitchAreaAsync(next, cancellationToken).ConfigureAwait(false))
                    break;

                continue;
            }

            // 4. 定目标
            var (entry, needsChoice) = Resolve(entries, wanted, worlds, listComplete);

            if (followedTarget && (entry == null || entry.ContentId != wanted.ContentId))
                throw new StopException(CatAutoEnterStopCodes.SWITCH_AREA_FAILED, $"换到 {game.CurrentAreaName} 后选角列表里没有要登录的角色");

            if (!needsChoice)
                reporter.Characters(false, entries.Select(ToInfo).ToArray());
            else
            {
                entry = await WaitForChoiceAsync(entries, cancellationToken).ConfigureAwait(false);

                // 等的时候人自己在游戏里点角色进去了
                if (entry == null)
                    break;
            }

            wanted = new CatAutoEnterTarget(entry!.Name, entry.HomeWorldCode, entry.ContentId);

            // 角色超域在别的大区: 换到它所在大区的大厅再登录, 只换一次
            if (AreaOf(entry) is { } awayArea)
            {
                if (followedTarget)
                    throw new StopException(CatAutoEnterStopCodes.SWITCH_AREA_FAILED, $"已经换过一次大区, 角色 {entry.Name} 仍显示在别的大区（{awayArea}）");

                followedTarget = true;
                visitedAreas.Add(awayArea);

                if (!await SwitchAreaAsync(awayArea, cancellationToken).ConfigureAwait(false))
                    break;

                continue;
            }

            if ((entry.LoginFlags & LOGIN_FLAGS_BLOCKING) != 0)
                throw new StopException(CatAutoEnterStopCodes.CHARACTER_LOCKED, $"角色 {entry.Name} 现在不能登录（被锁定、要改名或缺资料片, 标记 {entry.LoginFlags}）");

            // 5–6. 切服务器 → 选中 → 点进入 → 确认 → 排队
            await EnterAsync(entry, cancellationToken).ConfigureAwait(false);
            break;
        }

        // 7. 进入游戏
        await WaitInWorldAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     等到选角界面。<paramref name="clickStart" /> 为真时自己在标题界面点「开始游戏」（换大厅那套步骤已经点过就传假）。
    ///     返回 false 表示游戏已经在世界里了（有人手动进去了）。
    /// </summary>
    private async Task<bool> WaitForCharaSelectAsync(bool clickStart, CancellationToken cancellationToken)
    {
        var stage    = CurrentStage ?? CatStages.ENTERING_LOBBY;
        var clicked  = !clickStart;
        var deadline = game.Elapsed + (clicked ? timings.CharaSelectTimeout : timings.TitleTimeout);
        var queueing = false;

        while (true)
        {
            // 排队期间管道是放开的, 别的操作（读选角列表等）正用着时等下一轮, 不把「管道被占」当成模块失效
            if (queueing && game.ModuleBusy)
            {
                await DelayAsync(timings.QueuePoll, cancellationToken).ConfigureAwait(false);
                continue;
            }

            // 游戏刚起来时界面还没建好, 读不出状态是正常的, 由「到标题界面」的时限兜着
            var lobby = await ReadLobbyAsync("等选角界面", cancellationToken, !clicked).ConfigureAwait(false);

            if (lobby != null)
            {
                if (lobby.Dialogue)
                    throw new StopException(CatAutoEnterStopCodes.LOBBY_ERROR, $"进入选角界面时大厅提示: {Describe(lobby.DialogueText)}");

                switch (lobby.Where)
                {
                    case CatModuleReplies.WHERE_IN_GAME:
                        return false;

                    case CatModuleReplies.WHERE_CHARA_SELECT:
                        if (queueing)
                            SetStage(stage);

                        return true;
                }

                if (lobby.Ok)
                {
                    switch (KindOfOk(lobby))
                    {
                        case OkKind.Notice:
                            throw new StopException(CatAutoEnterStopCodes.LOBBY_ERROR, $"进入选角界面时游戏提示: {Describe(lobby.OkText)}");

                        // 大厅排队: 不点、不限时
                        case OkKind.Queue:
                            queueing = true;
                            ReportQueue(lobby.Queue);
                            game.Release();
                            await DelayAsync(timings.QueuePoll, cancellationToken).ConfigureAwait(false);
                            continue;
                    }

                    // 确定框被别的框盖着、读不到它的文字: 不点, 等它露出来
                    await DelayAsync(timings.Poll, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (queueing)
                {
                    queueing = false;
                    deadline = game.Elapsed + timings.CharaSelectTimeout;
                    SetStage(stage);
                }

                if (!clicked && lobby.Where == CatModuleReplies.WHERE_TITLE)
                {
                    var ready = await SendAsync("TITLEREADY", "看标题界面是否就绪", cancellationToken).ConfigureAwait(false);

                    if (ready.Contains("ready=1", StringComparison.Ordinal) &&
                        CatModuleReplies.IsOk(await SendAsync("LOGIN", "点「开始游戏」", cancellationToken).ConfigureAwait(false)))
                    {
                        clicked  = true;
                        deadline = game.Elapsed + timings.CharaSelectTimeout;
                    }
                }
                else if (!clicked && lobby.Where == CatModuleReplies.WHERE_BUSY)
                {
                    // 启动画面或片头动画: 让模块跳过; 点过「开始游戏」之后不再跳, 免得把正在连大厅的过程按掉
                    await SendAsync("SKIPMOVIE", "跳过片头", cancellationToken).ConfigureAwait(false);
                }
            }

            if (game.Elapsed > deadline)
            {
                throw new StopException
                (
                    CatAutoEnterStopCodes.TIMEOUT,
                    clicked
                        ? $"点了「开始游戏」后 {timings.CharaSelectTimeout.TotalSeconds:F0} 秒没有进入选角界面"
                        : $"等了 {timings.TitleTimeout.TotalSeconds:F0} 秒游戏没有到标题界面"
                );
            }

            await DelayAsync(timings.Poll, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     读整个大区的角色。Complete 为假表示列表里有模块读不准的条目（不是空位）, 这份列表不能当作「这个号的全部角色」
    /// </summary>
    private async Task<(IReadOnlyList<CharaSelectReader.Entry> Entries, bool Complete)> ReadCharactersAsync(CancellationToken cancellationToken)
    {
        var deadline = game.Elapsed + timings.CharaListSettle;

        while (true)
        {
            var response = await SendAsync("CHARAS", "读选角列表", cancellationToken).ConfigureAwait(false);
            var snapshot = CharaSelectReader.Parse(response);

            if (snapshot is { Where: CatModuleReplies.WHERE_CHARA_SELECT, Entries.Count: > 0 })
            {
                Remember(snapshot.Entries);
                Log.Information
                (
                    "[CatAutoEnter] {Area} 的角色: {Characters}",
                    game.CurrentAreaName,
                    string.Join(" | ", snapshot.Entries.Select(x => $"{x.Name}@{x.HomeWorldCode} cid={x.ContentId} 现在={x.CurrentWorldCode} 标记={x.LoginFlags}"))
                );

                if (snapshot.Invalid > 0)
                    Log.Warning("[CatAutoEnter] 选角列表里有 {Invalid} 个条目读不准, 不按「只有一个角色」自动选", snapshot.Invalid);

                return (snapshot.Entries, snapshot.Invalid == 0);
            }

            if (game.Elapsed > deadline)
            {
                if (snapshot == null)
                    throw new StopException(CatAutoEnterStopCodes.MODULE_UNAVAILABLE, $"读不到选角列表: {CatModuleReplies.FirstLine(response)}");

                if (snapshot.Where != CatModuleReplies.WHERE_CHARA_SELECT)
                    throw new StopException(CatAutoEnterStopCodes.TIMEOUT, $"读选角列表时游戏已经不在选角界面（{snapshot.Where}）");

                // 有条目但一个都读不准: 不是「这个大区没有角色」, 别据此去别的大区找
                if (snapshot.Invalid > 0)
                    throw new StopException(CatAutoEnterStopCodes.MODULE_UNAVAILABLE, $"选角列表里的 {snapshot.Invalid} 个角色都读不准: {CatModuleReplies.FirstLine(response)}");

                Log.Information("[CatAutoEnter] {Area} 的选角界面没有角色", game.CurrentAreaName);
                return ([], true);
            }

            await DelayAsync(timings.Poll, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     只换大厅并等到选角界面。返回 false 表示游戏已经在世界里了。
    /// </summary>
    private async Task<bool> SwitchAreaAsync(string areaName, CancellationToken cancellationToken)
    {
        // 这期间有人自己进了游戏: 不换（换大厅要先回标题, 那会把他登出去）
        if (await ReadLobbyAsync("换大区前看界面", cancellationToken).ConfigureAwait(false) is { Where: CatModuleReplies.WHERE_IN_GAME })
            return false;

        SetStage(CatStages.SWITCHING_AREA);
        Log.Information("[CatAutoEnter] 换到 {Area} 的大厅", areaName);

        string? error;

        try
        {
            error = await game.SwitchAreaAsync(areaName, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (!game.HasExited)
        {
            error = ex.Message;
        }

        if (error != null)
            throw new StopException(CatAutoEnterStopCodes.SWITCH_AREA_FAILED, $"换到 {areaName} 没有成功: {error}");

        return await WaitForCharaSelectAsync(false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     等人选角色。期间不占管道; 人直接在游戏里点角色进去了返回 null。没有时限。
    /// </summary>
    private async Task<CharaSelectReader.Entry?> WaitForChoiceAsync(IReadOnlyList<CharaSelectReader.Entry> entries, CancellationToken cancellationToken)
    {
        var choice = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (stateLock)
        {
            choosable     = entries.Select(x => x.ContentId).ToHashSet(StringComparer.Ordinal);
            pendingChoice = choice;
        }

        // 先进阶段再发列表: 工作台一收到列表就可能回 selectCharacter, 那时阶段必须已经是等人选
        SetStage(CatStages.AWAITING_CHARACTER_CHOICE);
        reporter.Characters(true, entries.Select(ToInfo).ToArray());
        game.Release();

        try
        {
            while (true)
            {
                using (var wake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    var delay = game.DelayAsync(timings.ChoicePoll, wake.Token);

                    await Task.WhenAny(choice.Task, delay).ConfigureAwait(false);
                    await wake.CancelAsync().ConfigureAwait(false);

                    try
                    {
                        await delay.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // 选了角色或被取消, 下面分别处理
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();

                if (game.HasExited)
                    throw new OperationCanceledException();

                if (choice.Task.IsCompletedSuccessfully)
                {
                    var contentId = await choice.Task.ConfigureAwait(false);
                    var chosen    = entries.First(x => x.ContentId == contentId);

                    Log.Information("[CatAutoEnter] 选了角色 {Name} cid={ContentId}", chosen.Name, chosen.ContentId);
                    return chosen;
                }

                if (game.ModuleBusy)
                    continue;

                CatModuleReplies.LobbyState? lobby;

                try
                {
                    lobby = await ReadLobbyAsync("等人选角色", cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    game.Release();
                }

                if (lobby is { Where: CatModuleReplies.WHERE_IN_GAME })
                    return null;

                if (lobby is { Dialogue: true })
                    throw new StopException(CatAutoEnterStopCodes.LOBBY_ERROR, $"等人选角色时大厅提示: {Describe(lobby.DialogueText)}");
            }
        }
        finally
        {
            lock (stateLock)
                pendingChoice = null;
        }
    }

    private async Task EnterAsync(CharaSelectReader.Entry entry, CancellationToken cancellationToken)
    {
        SetStage(CatStages.ENTERING_WORLD);
        Log.Information("[CatAutoEnter] 登录角色 {Name} cid={ContentId}", entry.Name, entry.ContentId);

        await SettleCommandAsync($"FOCUSCHARA {entry.ContentId}", "切到角色所在的服务器", cancellationToken).ConfigureAwait(false);
        await SettleCommandAsync($"SELECTCHARA {entry.ContentId}", "选中角色", cancellationToken).ConfigureAwait(false);

        var lockedDeadline = game.Elapsed + timings.LockedTimeout;
        var settleDeadline = game.Elapsed + timings.CommandSettle;

        while (true)
        {
            var response = await SendAsync($"ENTERCHARA {entry.ContentId}", "点角色进入游戏", cancellationToken).ConfigureAwait(false);

            if (CatModuleReplies.IsOk(response))
                break;

            if (response.Contains("flags=", StringComparison.Ordinal))
                throw new StopException(CatAutoEnterStopCodes.CHARACTER_LOCKED, $"角色 {entry.Name} 带着不能直接登录的标记（被锁定、要改名、缺资料片等）: {CatModuleReplies.FirstLine(response)}");

            if (response.Contains("special-prompt", StringComparison.Ordinal))
                throw new StopException(CatAutoEnterStopCodes.CHARACTER_LOCKED, $"点角色 {entry.Name} 会先弹出别的提示而不是登录确认框, 没有替人处理: {CatModuleReplies.FirstLine(response)}");

            if (response.Contains("locked", StringComparison.Ordinal))
            {
                if (game.Elapsed > lockedDeadline)
                    throw new StopException(CatAutoEnterStopCodes.CHARACTER_LOCKED, $"客户端一直提示稍后再登录, 等了 {timings.LockedTimeout.TotalSeconds:F0} 秒没有解除");
            }
            else if (!CatModuleReplies.IsTransient(response) || game.Elapsed > settleDeadline)
                throw new StopException(CatAutoEnterStopCodes.MODULE_UNAVAILABLE, $"点角色进入游戏没有成功: {CatModuleReplies.FirstLine(response)}");

            await DelayAsync(timings.Poll, cancellationToken).ConfigureAwait(false);
        }

        var confirmDeadline = game.Elapsed + timings.ConfirmTimeout;
        var enterDeadline   = game.Elapsed + timings.EnterTimeout;
        var confirmed       = false;
        var clickedYes      = false;
        var confirmClosed   = false;
        var queueing        = false;
        string? refusal     = null;

        while (true)
        {
            // 排队期间管道是放开的, 别的操作正用着时等下一轮, 不把「管道被占」当成模块失效
            if (queueing && game.ModuleBusy)
            {
                await DelayAsync(timings.QueuePoll, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var lobby = await ReadLobbyAsync("等进入游戏", cancellationToken).ConfigureAwait(false);

            // 这一次没读出来: 不拿它去判超时（读不出来另有时限）
            if (lobby == null)
            {
                await DelayAsync(timings.Poll, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (lobby.Where == CatModuleReplies.WHERE_IN_GAME)
                return;

            if (lobby.Dialogue)
                throw new StopException(CatAutoEnterStopCodes.LOBBY_ERROR, $"登录角色时大厅提示: {Describe(lobby.DialogueText)}");

            if (lobby.Ok)
            {
                switch (KindOfOk(lobby))
                {
                    case OkKind.Notice:
                        throw new StopException(CatAutoEnterStopCodes.LOBBY_ERROR, $"登录角色时游戏提示: {Describe(lobby.OkText)}");

                    // 排队: 确定框不点（点了就是取消排队）, 不限时; 这时出现的是/否框是人在取消排队, 也不替他点
                    case OkKind.Queue:
                        queueing  = true;
                        confirmed = true;
                        ReportQueue(lobby.Queue);
                        game.Release();
                        await DelayAsync(timings.QueuePoll, cancellationToken).ConfigureAwait(false);
                        continue;
                }

                // 确定框被是/否框盖着、读不到它的文字: 两个都不点, 等人处理
                await DelayAsync(timings.Poll, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (queueing)
            {
                queueing      = false;
                enterDeadline = game.Elapsed + timings.EnterTimeout;
                SetStage(CatStages.ENTERING_WORLD);
            }

            // 「是」只替人点一次, 而且只点这一次点击带出来的登录确认框:
            // 「取消登录」那句不点; 文字读不到的不点; 点过之后那个框关了又出来别的是/否框, 不知道在问什么, 停手交给人。
            // 是不是登录确认框、对应的是不是这个角色, 由模块在点的那一刻自己再核对一遍（见模块的 DIALOG YES）。
            if (!lobby.YesNo)
                confirmClosed = clickedYes;
            else if (!lobby.YesNoText.Contains(CANCEL_LOGIN_PROMPT, StringComparison.Ordinal))
            {
                if (clickedYes)
                {
                    if (confirmClosed)
                        throw new StopException(CatAutoEnterStopCodes.LOBBY_ERROR, $"确认登录 {entry.Name} 后游戏又弹出了确认框, 没有替人回答: {Describe(lobby.YesNoText)}");
                }
                else if (lobby.YesNoText.Length == 0)
                    refusal = "读不到确认框的文字";
                else
                {
                    var reply = await SendAsync($"DIALOG YES {entry.ContentId}", "点登录确认框的「是」", cancellationToken).ConfigureAwait(false);

                    if (CatModuleReplies.IsOk(reply))
                    {
                        clickedYes    = true;
                        confirmed     = true;
                        enterDeadline = game.Elapsed + timings.EnterTimeout;
                    }
                    else if (reply.Contains("other-character", StringComparison.Ordinal))
                        throw new StopException(CatAutoEnterStopCodes.LOBBY_ERROR, $"游戏里选中的已经不是 {entry.Name}（有人点了别的角色）, 确认框没有替人回答: {Describe(lobby.YesNoText)}");
                    else
                        refusal = CatModuleReplies.FirstLine(reply);
                }
            }

            if (!confirmed && game.Elapsed > confirmDeadline)
            {
                throw new StopException
                (
                    CatAutoEnterStopCodes.TIMEOUT,
                    refusal == null
                        ? $"点了角色 {entry.Name} 后 {timings.ConfirmTimeout.TotalSeconds:F0} 秒没有出现登录确认框"
                        : $"点了角色 {entry.Name} 后出现了确认框, 但 {timings.ConfirmTimeout.TotalSeconds:F0} 秒内没能确认它是登录确认框（{refusal}）, 没有替人回答"
                );
            }

            if (confirmed && game.Elapsed > enterDeadline)
                throw new StopException(CatAutoEnterStopCodes.TIMEOUT, $"确认登录 {entry.Name} 后 {timings.EnterTimeout.TotalSeconds:F0} 秒没有进入游戏");

            await DelayAsync(timings.Poll, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task WaitInWorldAsync(CancellationToken cancellationToken)
    {
        var deadline = game.Elapsed + timings.WorldLoadTimeout;

        while (true)
        {
            var who = CatModuleReplies.ParseWhoAmI(await SendAsync("WHOAMI", "读游戏里的当前角色", cancellationToken).ConfigureAwait(false));

            if (who is { Loaded: true })
            {
                ReportEntered(who);
                game.AreaEntered(game.CurrentAreaName);
                return;
            }

            if (game.Elapsed > deadline)
                throw new StopException(CatAutoEnterStopCodes.TIMEOUT, $"游戏已经进入, 但 {timings.WorldLoadTimeout.TotalSeconds:F0} 秒内读不到当前角色");

            await DelayAsync(timings.Poll, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     发一条「列表还没载入时会暂时失败」的命令, 在 <see cref="CatAutoEnterTimings.CommandSettle" /> 内重试到成功
    /// </summary>
    private async Task SettleCommandAsync(string command, string step, CancellationToken cancellationToken)
    {
        var deadline = game.Elapsed + timings.CommandSettle;

        while (true)
        {
            var response = await SendAsync(command, step, cancellationToken).ConfigureAwait(false);

            if (CatModuleReplies.IsOk(response))
                return;

            if (!CatModuleReplies.IsTransient(response) || game.Elapsed > deadline)
                throw new StopException(CatAutoEnterStopCodes.MODULE_UNAVAILABLE, $"{step}没有成功: {CatModuleReplies.FirstLine(response)}");

            await DelayAsync(timings.Poll, cancellationToken).ConfigureAwait(false);
        }
    }

    #endregion

    #region 辅助

    private CatAutoEnterOutcome Stop(string code, string message)
    {
        reporter.AutoEnterStopped(code, message);

        // 停手后游戏照常开着, 阶段回到「运行中」
        if (!game.HasExited)
            SetStage(CatStages.RUNNING);

        return CatAutoEnterOutcome.Stopped;
    }

    private void SetStage(string stage)
    {
        lock (stateLock)
        {
            if (currentStage == stage)
                return;

            currentStage  = stage;
            reportedQueue = null;
        }

        reporter.Stage(stage);
    }

    private void ReportQueue(int queue)
    {
        int? position = queue > 0 ? queue : null;

        lock (stateLock)
        {
            if (currentStage == CatStages.QUEUEING && reportedQueue == position)
                return;

            currentStage  = CatStages.QUEUEING;
            reportedQueue = position;
        }

        reporter.Queueing(position);
    }

    private void ReportEntered(CatModuleReplies.WhoAmI who)
    {
        var known       = knownEntries.FirstOrDefault(x => x.ContentId == who.ContentId);
        var currentCode = FirstNonEmpty(who.WorldCode, worldCodes.GetValueOrDefault(who.WorldId), known?.CurrentWorldCode);
        var homeCode    = FirstNonEmpty(who.HomeWorldCode, worldCodes.GetValueOrDefault(who.HomeWorldId), known?.HomeWorldCode);

        lock (stateLock)
            enteredContentId = who.ContentId;

        Log.Information("[CatAutoEnter] 已进入游戏: {Name} cid={ContentId} 原始={Home} 现在={Current}", who.Name, who.ContentId, homeCode, currentCode);

        reporter.Character
        (
            new CatCharacterInfo
            (
                who.ContentId,
                who.Name,
                homeCode,
                currentCode,
                WorldName(worlds, homeCode),
                WorldName(worlds, currentCode),
                false,
                true
            )
        );

        SetStage(CatStages.IN_WORLD);
    }

    private CatCharacterInfo ToInfo(CharaSelectReader.Entry entry) =>
        new
        (
            entry.ContentId,
            entry.Name,
            entry.HomeWorldCode,
            entry.CurrentWorldCode,
            WorldName(worlds, entry.HomeWorldCode),
            WorldName(worlds, entry.CurrentWorldCode),
            entry.DcTraveling,
            (entry.LoginFlags & LOGIN_FLAGS_BLOCKING) == 0
        );

    /// <summary>
    ///     角色超域在别的大区时返回那个大区名; 就在当前大区（或没在超域）返回 null。以选角列表为准, 不查超域订单
    /// </summary>
    private string? AreaOf(CharaSelectReader.Entry entry)
    {
        if (!entry.DcTraveling)
            return null;

        var world = worlds.FirstOrDefault(x => string.Equals(x.Code, entry.CurrentWorldCode, StringComparison.OrdinalIgnoreCase))
                    ?? throw new StopException
                    (
                        CatAutoEnterStopCodes.SWITCH_AREA_FAILED,
                        $"角色 {entry.Name} 正在超域, 但查不到它所在的服务器 {entry.CurrentWorldCode} 属于哪个大区"
                    );

        return string.Equals(world.AreaName, game.CurrentAreaName, StringComparison.Ordinal) ? null : world.AreaName;
    }

    private void Remember(IReadOnlyList<CharaSelectReader.Entry> entries)
    {
        knownEntries = entries;

        foreach (var entry in entries)
        {
            if (entry.CurrentWorldId > 0 && !string.IsNullOrEmpty(entry.CurrentWorldCode))
                worldCodes[entry.CurrentWorldId] = entry.CurrentWorldCode;

            if (entry.HomeWorldId > 0 && !string.IsNullOrEmpty(entry.HomeWorldCode))
                worldCodes[entry.HomeWorldId] = entry.HomeWorldCode;
        }
    }

    private static string? WorldName(IReadOnlyList<CatAutoEnterWorld> worlds, string? code) =>
        string.IsNullOrEmpty(code) ? null : worlds.FirstOrDefault(x => string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase))?.Name;

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(x => !string.IsNullOrEmpty(x)) ?? string.Empty;

    private enum OkKind
    {
        /// <summary>排队提示</summary>
        Queue,

        /// <summary>别的提示（角色在别处登录、维护之类）</summary>
        Notice,

        /// <summary>读不到它的文字（被是/否框盖着）, 分不清</summary>
        Unknown
    }

    /// <summary>
    ///     确定框是哪一种: 有排队名次或文字里有「排队」算排队; 读到了别的文字算提示; 文字读不到（上面还叠着是/否框）算分不清
    /// </summary>
    private static OkKind KindOfOk(CatModuleReplies.LobbyState lobby)
    {
        if (lobby.Queue > 0 || lobby.OkText.Contains(QUEUE_PROMPT, StringComparison.Ordinal))
            return OkKind.Queue;

        return lobby.OkText.Length == 0 && lobby.YesNo ? OkKind.Unknown : OkKind.Notice;
    }

    private static string Describe(string text) =>
        string.IsNullOrWhiteSpace(text) ? "（没有读到提示文字）" : text;

    /// <summary>
    ///     读一次界面状态; 这一次读不出来（主线程忙、界面还没建好）返回 null, 由调用方下一轮再读。
    ///     <paramref name="booting" /> 为假时, 连续读不出来超过 <see cref="CatAutoEnterTimings.LobbyUnreadableTimeout" /> 就停手。
    /// </summary>
    private async Task<CatModuleReplies.LobbyState?> ReadLobbyAsync(string step, CancellationToken cancellationToken, bool booting = false)
    {
        var response = await SendAsync("LOBBYSTATE", step, cancellationToken).ConfigureAwait(false);
        var lobby    = CatModuleReplies.ParseLobbyState(response);

        if (lobby != null)
        {
            lobbyUnreadableSince = null;
            return lobby;
        }

        lobbyUnreadableSince ??= game.Elapsed;

        if (!booting && game.Elapsed - lobbyUnreadableSince.Value > timings.LobbyUnreadableTimeout)
        {
            throw new StopException
            (
                CatAutoEnterStopCodes.MODULE_UNAVAILABLE,
                $"连续 {timings.LobbyUnreadableTimeout.TotalSeconds:F0} 秒读不到游戏界面状态（{step}）: {CatModuleReplies.FirstLine(response)}"
            );
        }

        return null;
    }

    /// <summary>
    ///     发一条模块命令。连不上、断了按停手处理; 特征码失效、不认识命令这两种回应也直接停手。
    /// </summary>
    private async Task<string> SendAsync(string command, string step, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string response;

        try
        {
            response = await game.SendAsync(command, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            game.Release();

            // 游戏崩溃或被关掉时管道比进程先断: 等一下再看进程还在不在, 别把「游戏没了」报成模块失效（那样还会接着去挂 Minion）
            if (!game.HasExited)
                await game.DelayAsync(timings.ExitGrace, cancellationToken).ConfigureAwait(false);

            if (game.HasExited)
                throw new OperationCanceledException("游戏进程已经退出");

            throw new StopException(CatAutoEnterStopCodes.MODULE_UNAVAILABLE, $"和游戏内模块通话失败（{step}）: {ex.Message}");
        }

        if (response.Contains("sigscan-failed", StringComparison.Ordinal))
            throw new StopException(CatAutoEnterStopCodes.MODULE_UNAVAILABLE, $"游戏内模块在这个版本的游戏上失效了（{step}）, 要等 DcMiniLauncher 更新");

        if (response.Contains("unknown-command", StringComparison.Ordinal))
            throw new StopException(CatAutoEnterStopCodes.MODULE_OUTDATED, $"游戏里注着的模块不认识命令 {command.Split(' ')[0]}; 更新 DcMiniLauncher 后重启游戏");

        return response;
    }

    private async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        await game.DelayAsync(delay, cancellationToken).ConfigureAwait(false);

        if (game.HasExited)
            throw new OperationCanceledException("游戏进程已经退出");
    }

    /// <summary>给版本握手用的通道: 走本类的发送（带停手判断）</summary>
    private sealed class Channel(CatAutoEnter owner, string step) : IMiniModuleChannel
    {
        public Task<string> SendAsync(string command, CancellationToken cancellationToken) =>
            owner.SendAsync(command, step, cancellationToken);
    }

    private sealed class StopException(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }

    #endregion
}

/// <summary>
///     游戏内模块几条新命令的回应解析（格式见任务调研 research-chara-select-primitive.md 7.1）
/// </summary>
internal static partial class CatModuleReplies
{
    /// <summary>where: 在游戏世界里</summary>
    public const string WHERE_IN_GAME = "ingame";

    /// <summary>where: 选角界面</summary>
    public const string WHERE_CHARA_SELECT = "charaselect";

    /// <summary>where: 标题界面</summary>
    public const string WHERE_TITLE = "title";

    /// <summary>where: 启动画面、片头、读盘</summary>
    public const string WHERE_BUSY = "busy";

    /// <summary>
    ///     LOBBYSTATE 的回应。YesNoText / OkText / DialogueText 是是/否框、确定框、错误框各自的提示文字, 没有那个框或读不到时为空
    /// </summary>
    public sealed record LobbyState
    (
        string Where,
        bool   Locked,
        int    Queue,
        bool   YesNo,
        bool   Ok,
        bool   Dialogue,
        bool   Loading,
        string YesNoText,
        string OkText,
        string DialogueText
    );

    /// <summary>WHOAMI 的回应; WorldCode / HomeWorldCode 是模块给的服务器代号, 可能为空</summary>
    public sealed record WhoAmI
    (
        bool   Loaded,
        string Name,
        string ContentId,
        int    WorldId,
        int    HomeWorldId,
        string WorldCode,
        string HomeWorldCode
    );

    public static bool IsOk(string response) =>
        response.StartsWith("OK", StringComparison.Ordinal);

    /// <summary>
    ///     模块暂时答不上、稍后重试可能就好的失败: 主线程忙、指针还没就绪、列表还没载入。这些情况模块都还没有动游戏。
    ///     「发了回调但没生效」（not-applied）不在此列: 客户端处理回调是当场完成的, 没生效说明回调编号或结构对不上这个版本的游戏,
    ///     再发只会把一条含义不明的回调重复发几十遍。
    /// </summary>
    public static bool IsTransient(string response) =>
        response.Contains("mainthread-timeout", StringComparison.Ordinal) ||
        response.Contains("pointers-unavailable", StringComparison.Ordinal) ||
        response.Contains("not-in-list", StringComparison.Ordinal) ||
        response.Contains("no-world-list", StringComparison.Ordinal) ||
        response.Contains("bad-list", StringComparison.Ordinal);

    public static string FirstLine(string response)
    {
        var newline = response.IndexOf('\n');
        return (newline < 0 ? response : response[..newline]).TrimEnd('\r');
    }

    public static LobbyState? ParseLobbyState(string response)
    {
        if (!IsOk(response))
            return null;

        var fields = ParseFields(FirstLine(response));

        if (!fields.TryGetValue("where", out var where))
            return null;

        // 后面每个在场的对话框一行: D \t <SelectYesno|SelectOk|Dialogue> \t <addon id> \t <ready> \t <visible> \t <提示文字>;
        // 最后的 T 行是其中最该看的那个（是/否框优先, 其次错误框, 最后确定框）。没有 D 行时按这个优先级把 T 行归给对应的框
        var texts   = new Dictionary<string, string>(StringComparer.Ordinal);
        var primary = string.Empty;
        var lines   = response.Split('\n');

        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');

            if (line.StartsWith("D\t", StringComparison.Ordinal))
            {
                var parts = line.Split('\t', 6);

                if (parts.Length == 6)
                    texts[parts[1]] = parts[5].Trim();
            }
            else if (line.StartsWith("T ", StringComparison.Ordinal) || line.StartsWith("T\t", StringComparison.Ordinal))
            {
                primary = string.Join(' ', new[] { line[2..] }.Concat(lines.Skip(i + 1).Select(x => x.TrimEnd('\r')))).Trim();
                break;
            }
        }

        var yesNo    = Flag(fields, "yesno");
        var ok       = Flag(fields, "ok");
        var dialogue = Flag(fields, "dialogue");

        return new LobbyState
        (
            where,
            Flag(fields, "locked"),
            Int(fields, "queue"),
            yesNo,
            ok,
            dialogue,
            Flag(fields, "loading"),
            texts.GetValueOrDefault("SelectYesno") ?? (yesNo ? primary : string.Empty),
            texts.GetValueOrDefault("SelectOk") ?? (ok && !yesNo && !dialogue ? primary : string.Empty),
            texts.GetValueOrDefault("Dialogue") ?? (dialogue && !yesNo ? primary : string.Empty)
        );
    }

    public static WhoAmI? ParseWhoAmI(string response)
    {
        if (!IsOk(response))
            return null;

        var fields = ParseFields(FirstLine(response));

        if (!Flag(fields, "loaded"))
            return new WhoAmI(false, string.Empty, string.Empty, 0, 0, string.Empty, string.Empty);

        var contentId = fields.GetValueOrDefault("cid", string.Empty);
        var name      = fields.GetValueOrDefault("name", string.Empty);

        // 读盘中途名字、ContentId 可能还是空的, 当作还没载入
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(contentId) || contentId == "0")
            return new WhoAmI(false, string.Empty, string.Empty, 0, 0, string.Empty, string.Empty);

        return new WhoAmI
        (
            true,
            name,
            contentId,
            Int(fields, "world"),
            Int(fields, "home"),
            fields.GetValueOrDefault("worldName", string.Empty),
            fields.GetValueOrDefault("homeName", string.Empty)
        );
    }

    /// <summary>
    ///     拆 <c>key=value</c>。字段之间是空格或制表符; 值里可以有空格（角色名）, 一直取到下一个 <c>key=</c> 之前
    /// </summary>
    public static Dictionary<string, string> ParseFields(string line)
    {
        var fields  = new Dictionary<string, string>(StringComparer.Ordinal);
        var matches = FieldKey().Matches(line);

        for (var i = 0; i < matches.Count; i++)
        {
            var key   = matches[i].Groups[1];
            var start = matches[i].Index + matches[i].Length;
            var end   = i + 1 < matches.Count ? matches[i + 1].Index : line.Length;

            fields[key.Value] = line[start..end].Trim(' ', '\t');
        }

        return fields;
    }

    private static bool Flag(Dictionary<string, string> fields, string key) =>
        fields.TryGetValue(key, out var value) && value == "1";

    private static int Int(Dictionary<string, string> fields, string key) =>
        fields.TryGetValue(key, out var value) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;

    [GeneratedRegex(@"(?:^|[ \t])([A-Za-z][A-Za-z0-9]*)=")]
    private static partial Regex FieldKey();
}
