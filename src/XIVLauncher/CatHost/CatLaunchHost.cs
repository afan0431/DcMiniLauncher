using System.Text.Json;
using System.Threading.Channels;
using Serilog;
using XIVLauncher.Common.Game;
using XIVLauncher.Minion;

namespace XIVLauncher.CatHost;

/// <summary>
///     一次 launch 的参数（已校验）; MinionCard = 要挂的 Minion 卡, 不挂为 null; WeGameLogin = WeGame 号在本机没有可用的登录信息时拉起 WeGame 等员工登录, 为 false 时直接报 authorizationRequired;
///     AutoEnter = 游戏起来后自动经标题、选角进入游戏; CharacterName / CharacterHomeWorld = 要登录的角色（可为空）
/// </summary>
public sealed record CatLaunchRequest
(
    string         OperationId,
    string         AccountName,
    bool           Dalamud,
    CatMinionLaunch? MinionCard,
    int            CrashDialogTimeoutSeconds = CatProtocol.DEFAULT_CRASH_DIALOG_TIMEOUT_SECONDS,
    string?        AreaName                  = null,
    XIVAccountType Platform                  = XIVAccountType.Sdo,
    bool           IsInternational           = false,
    CatSecret?     Password                  = null,
    bool           WeGameLogin               = false,
    CatWeGameScan? WeGameScan                = null,
    bool           AutoEnter                 = false,
    string?        CharacterName             = null,
    string?        CharacterHomeWorld        = null
)
{
    /// <summary>是否为 WeGame 版国服的号</summary>
    public bool IsWeGame => Platform == XIVAccountType.WeGame;

    /// <summary>
    ///     渠道（含国际服）。<see cref="Platform" /> 只对国服有意义（账号库里的行类型）, 国际服时它停在缺省值, 不要拿去用
    /// </summary>
    public CatPlatform Channel =>
        IsInternational ? CatPlatform.International : IsWeGame ? CatPlatform.WeGame : CatPlatform.Shengqu;

    /// <summary>是否要挂 Minion</summary>
    public bool Minion => MinionCard != null;

    /// <summary>要挂的卡的指纹, 不挂为 null</summary>
    public string? CardFingerprint => MinionCard?.Fingerprint;

    /// <summary>要挂的卡的 variant, 不挂为 null</summary>
    public string? Variant => MinionCard?.Variant;

    /// <summary>游戏已退出而崩溃处理器还开着时最多等多久</summary>
    public TimeSpan CrashDialogTimeout => TimeSpan.FromSeconds(CrashDialogTimeoutSeconds);
}

/// <summary>
///     一次 adopt 的参数（已校验）: 要接管的游戏、它的守护记录, 以及崩溃重开后重新挂 Minion 用的卡（没给为 null）
/// </summary>
public sealed record CatAdoptRequest
(
    string                        OperationId,
    int                           Pid,
    DateTimeOffset                ProcessStartedAt,
    InGame.GameRecord             Record,
    int                           CrashDialogTimeoutSeconds,
    CatMinionLaunch?              MinionCard,
    System.Diagnostics.Process    Game,
    IDisposable                   GuardClaim
);

/// <summary>
///     启动过程向外壳报告进度
/// </summary>
public interface ICatLaunchReporter
{
    /// <summary>已接管一个在跑的游戏（adopt）, 之后与 launch 起来的游戏一样守护</summary>
    void Adopted(int pid, DateTimeOffset processStartedAt)
    {
    }

    /// <summary>进入某个阶段</summary>
    void Stage(string stage);

    /// <summary>进入排队阶段或排队名次变了; queuePosition = 排在第几位, 读不到时为 null</summary>
    void Queueing(int? queuePosition) => Stage(CatStages.QUEUEING);

    /// <summary>选角界面读到的角色列表; needsChoice = 定不了登录哪个, 在等人选（之后用 selectCharacter 回答）</summary>
    void Characters(bool needsChoice, IReadOnlyList<CatCharacterInfo> characters)
    {
    }

    /// <summary>已进入游戏的角色（之后人手动换了角色会再报一次）</summary>
    void Character(CatCharacterInfo character)
    {
    }

    /// <summary>自动进入角色没有做完, 游戏停在当前界面交给人; code 见 <see cref="CatAutoEnterStopCodes" /></summary>
    void AutoEnterStopped(string code, string message)
    {
    }

    /// <summary>游戏进程已创建</summary>
    void Started(int pid, DateTimeOffset processStartedAt);

    /// <summary>崩溃重启, 进程号变了</summary>
    void Restarted(int oldPid, int pid, DateTimeOffset processStartedAt);

    /// <summary>游戏已退出但 Dalamud 崩溃处理器还开着（崩溃对话框在等人选择）</summary>
    void Crashed(int pid);

    /// <summary>Dalamud / Minion 注入结果</summary>
    void Agent(string kind, bool ok, string? code = null, string? message = null);

    /// <summary>游戏已退出（不再重启）; reason 见 <see cref="CatExitReasons" />, 重启失败时带 code / message</summary>
    void Exited(int pid, int? exitCode, string? reason = null, string? code = null, string? message = null);

    /// <summary>启动失败</summary>
    void Failed(string code, string message);

    /// <summary>脱敏后的日志</summary>
    void Log(string level, string message);

    /// <summary>等 WeGame 登录期间出现了要客户配合的验证（二维码或设备验证短信）</summary>
    void WeGameChallenge(CatWeGameChallenge challenge);

    /// <summary>那项验证已经不在了（客户已完成、员工自己处理了、或等登录结束）</summary>
    void WeGameChallengeCleared(string challengeId);

    /// <summary>自动切到扫码页没有成功, 要员工在 WeGame 窗口里手动切; scan 见 <see cref="CatWeGameScans" /></summary>
    void WeGameScanSwitchFailed(string scan);

    /// <summary>点了设备验证的「确定」之后的结果（目前只报没通过; 通过时那项验证直接消失）</summary>
    void WeGameSmsResult(string challengeId, bool passed);
}

/// <summary>
///     真正干活的启动器: 真实启动或模拟启动
/// </summary>
public interface ICatGameRunner
{
    /// <summary>
    ///     执行整个生命周期直到游戏退出或启动失败, 返回进程退出码
    /// </summary>
    Task<int> RunAsync(CatLaunchRequest request, ICatLaunchReporter reporter, CancellationToken cancellationToken);

    /// <summary>
    ///     接管一个在跑的游戏并守护到它退出, 返回进程退出码。不支持的启动器报 launch.failed{unsupported}
    /// </summary>
    Task<int> AdoptAsync(CatAdoptRequest request, ICatLaunchReporter reporter, CancellationToken cancellationToken)
    {
        reporter.Failed(CatCodes.UNSUPPORTED, "这个启动器不支持接管在跑的游戏");
        return Task.FromResult(CatLaunchHost.EXIT_LAUNCH_FAILED);
    }

    /// <summary>
    ///     交接停止前的准备: 核对此刻能不能交接（游戏还在、没有正在进行的游戏内跨区、崩溃对话框没开着、守护记录在）,
    ///     能就把最新的跨区会话写进守护记录, 之后不再做任何会碰游戏的事。返回 null = 可以交接（本进程随后直接退出:
    ///     不关游戏、不登出、不删记录、不发 game.exited）; 否则返回拒绝原因, 一切照旧。
    /// </summary>
    Task<CatAcceptResult?> PrepareHandOffAsync(CancellationToken cancellationToken) =>
        Task.FromResult<CatAcceptResult?>(CatAcceptResult.Rejected(CatCodes.UNSUPPORTED, "这个启动器不支持交接停止"));

    /// <summary>
    ///     对运行中的游戏补注入 Dalamud / 重新挂 Minion, 结果经 <see cref="ICatLaunchReporter.Agent" /> 报告
    /// </summary>
    /// <param name="dalamud">补注入 Dalamud</param>
    /// <param name="minion">重新挂 Minion</param>
    /// <param name="force">Minion 已挂在这个游戏上时也重新挂</param>
    /// <param name="reporter">报告</param>
    /// <param name="cancellationToken">取消</param>
    Task InjectAsync(bool dalamud, bool minion, bool force, ICatLaunchReporter reporter, CancellationToken cancellationToken);

    /// <summary>
    ///     下号: 不再崩溃重启, 请游戏自己关闭, 超过 <paramref name="gracefulTimeout" /> 结束进程;
    ///     游戏还没起来时取消启动。之后 <see cref="RunAsync" /> 照常收尾（发 game.exited 或 launch.failed）。
    /// </summary>
    Task CloseAsync(TimeSpan gracefulTimeout);

    /// <summary>
    ///     下号时先经它请游戏自己登出退出（见 <see cref="CatGameCloser" />）; null = 只发关闭消息、超时结束进程
    /// </summary>
    ICatGameExit? GameExit => null;

    /// <summary>
    ///     客户说设备验证的短信已经发了: 点验证窗口的「确定」。立即返回是否点了, 结果经
    ///     <see cref="ICatLaunchReporter.WeGameSmsResult" /> 或 <see cref="ICatLaunchReporter.WeGameChallengeCleared" /> 报告。
    ///     不在等 WeGame 登录的启动器一律回 notRunning。
    /// </summary>
    CatAcceptResult ConfirmWeGameSms(string challengeId) =>
        CatAcceptResult.Rejected(CatCodes.NOT_RUNNING, "当前没有在等 WeGame 的设备验证");

    /// <summary>
    ///     员工在工作台选了要登录的角色（contentId 来自 game.characters）。立即返回是否接受, 之后的进度照常走事件。
    ///     不在等人选角色的启动器一律回 notRunning。
    /// </summary>
    CatAcceptResult SelectCharacter(string contentId) =>
        CatAcceptResult.Rejected(CatCodes.NOT_RUNNING, "当前没有在等人选角色");
}

/// <summary>
///     dml-cat/1 的方法分发与状态: 一个进程只接受一次 launch, 状态随事件更新。
///     事件先进队列, 由后台按序写出, 外壳不读管道时启动流程也不会被卡住。
/// </summary>
public sealed class CatLaunchHost : ICatRpcHandler, ICatLaunchReporter
{
    /// <summary>launch 失败后的进程退出码</summary>
    public const int EXIT_LAUNCH_FAILED = 3;

    /// <summary>launch 的 character.name 最长字符数</summary>
    public const int MAX_CHARACTER_NAME_LENGTH = 100;

    /// <summary>launch 的 character.homeWorld 最长字符数</summary>
    public const int MAX_HOME_WORLD_LENGTH = 64;

    /// <summary>游戏起来后本进程内部出错, 已等游戏结束并发 game.exited{reason:"guardError"}</summary>
    public const int EXIT_GUARD_ERROR = 5;

    private static readonly TimeSpan GuardErrorPollInterval = TimeSpan.FromSeconds(1);

    private readonly Func<CatLaunchRequest, ICatGameRunner> runnerFactory;
    private readonly Func<string, object, Task> publish;
    private readonly CatLogRedactor             redactor;
    private readonly object                     stateLock  = new();
    private readonly TaskCompletionSource<int>  completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<(string Method, object Parameters)> events =
        Channel.CreateUnbounded<(string Method, object Parameters)>(new UnboundedChannelOptions { SingleReader = true });

    private ICatGameRunner?   runner;
    private CatLaunchRequest? request;
    private string            stage = CatStages.IDLE;
    private int?              pid;
    private DateTimeOffset?   processStartedAt;
    private bool?             dalamudOk;
    private bool?             minionOk;
    private int               injectBusy;
    private bool              closeRequested;
    private bool              handOffRequested;
    private volatile bool     handOffCommitted;
    private bool              runnerFaulted;
    private int               pendingEvents;

    /// <summary>
    ///     创建分发器
    /// </summary>
    /// <param name="runner">真实或模拟启动器</param>
    /// <param name="publish">发事件（方法名, 参数）</param>
    /// <param name="redactor">日志脱敏</param>
    public CatLaunchHost(ICatGameRunner runner, Func<string, object, Task> publish, CatLogRedactor redactor)
        : this(_ => runner, publish, redactor)
    {
    }

    /// <summary>
    ///     创建分发器; 启动器要等收到 launch、知道渠道后才选（国服与国际服是两个互不相干的启动器）
    /// </summary>
    /// <param name="runnerFactory">按已校验的 launch 请求给出启动器; 只在接受 launch 时调一次</param>
    /// <param name="publish">发事件（方法名, 参数）</param>
    /// <param name="redactor">日志脱敏</param>
    public CatLaunchHost(Func<CatLaunchRequest, ICatGameRunner> runnerFactory, Func<string, object, Task> publish, CatLogRedactor redactor)
    {
        this.runnerFactory = runnerFactory;
        this.publish       = publish;
        this.redactor      = redactor;

        _ = Task.Run(PumpEventsAsync);
    }

    /// <summary>生命周期结束时完成, 值为进程退出码</summary>
    public Task<int> Completion => completion.Task;

    /// <summary>是否已接受 launch</summary>
    public bool HasLaunch
    {
        get
        {
            lock (stateLock)
                return request != null;
        }
    }

    /// <summary>游戏是否已创建过进程</summary>
    public bool HasStarted
    {
        get
        {
            lock (stateLock)
                return pid != null;
        }
    }

    /// <summary>当前 operationId</summary>
    public string? OperationId
    {
        get
        {
            lock (stateLock)
                return request?.OperationId;
        }
    }

    /// <summary>已入队但还没交给管道服务端的事件数</summary>
    public int PendingEventCount => Volatile.Read(ref pendingEvents);

    /// <inheritdoc />
    public Task<object?> HandleAsync(string method, JsonElement parameters, CancellationToken cancellationToken) =>
        method switch
        {
            "launch" => Task.FromResult<object?>(Launch(Deserialize<CatLaunchParams>(parameters))),
            "adopt"  => Task.FromResult<object?>(Adopt(Deserialize<CatAdoptParams>(parameters))),
            "inject" => Task.FromResult<object?>(Inject(Deserialize<CatInjectParams>(parameters))),
            "status" => Task.FromResult<object?>(GetStatus()),
            "close"  => Task.FromResult<object?>(Close(Deserialize<CatCloseParams>(parameters))),
            CatHandOff.METHOD => HandOffBoxedAsync(cancellationToken),
            "weGame.confirmSms" => Task.FromResult<object?>(ConfirmWeGameSms(Deserialize<CatWeGameConfirmSmsParams>(parameters))),
            "selectCharacter" => Task.FromResult<object?>(SelectCharacter(Deserialize<CatSelectCharacterParams>(parameters))),
            _        => throw new CatRpcException(CatRpcException.METHOD_NOT_FOUND, $"未知方法: {method}")
        };

    /// <summary>
    ///     校验 launch 的 minion: 指纹、variant、编号格式不对报 invalidParams; 卡号、论坛账号、论坛密码缺任一项报 minionNotConfigured
    ///     （缺值说明后台没有配置这张卡或论坛账号）。通过时返回 null 并给出要挂的卡。
    /// </summary>
    internal static CatAcceptResult? ValidateMinion(CatMinionParams minion, out CatMinionLaunch? card)
    {
        card = null;

        if (!MinionCards.IsValidFingerprint(minion.CardFingerprint))
            return CatAcceptResult.Rejected(CatCodes.INVALID_PARAMS, "minion.cardFingerprint 必须是 16 位小写十六进制");

        if (!MinionCards.IsValidVariant(minion.Variant))
            return CatAcceptResult.Rejected(CatCodes.INVALID_PARAMS, "minion.variant 只能是 cn 或 global");

        List<string> missing = [];

        if (string.IsNullOrWhiteSpace(minion.Keycode))
            missing.Add("卡号");
        if (string.IsNullOrWhiteSpace(minion.ForumId))
            missing.Add("论坛账号");
        if (string.IsNullOrEmpty(minion.ForumPassword))
            missing.Add("论坛密码");

        if (missing.Count > 0)
            return CatAcceptResult.Rejected(CatCodes.MINION_NOT_CONFIGURED, $"上号请求里没有 Minion 的{string.Join("、", missing)}, 请在 Cat 工作台「设置 → Minion」里补上");

        var uid = minion.Uid?.Trim();

        if (!MinionCards.IsValidUid(uid))
            return CatAcceptResult.Rejected(CatCodes.INVALID_PARAMS, "minion.uid 必须是 32 位十六进制");

        card = new CatMinionLaunch
        (
            minion.CardFingerprint!,
            minion.Variant!,
            new CatSecret(minion.Keycode!),
            uid!,
            minion.ForumId!.Trim(),
            new CatSecret(minion.ForumPassword!)
        );

        return null;
    }

    /// <summary>
    ///     接受一次 launch 并在后台执行
    /// </summary>
    public CatAcceptResult Launch(CatLaunchParams? parameters)
    {
        if (parameters == null || string.IsNullOrWhiteSpace(parameters.OperationId) || string.IsNullOrWhiteSpace(parameters.AccountName))
            return CatAcceptResult.Rejected(CatCodes.INVALID_PARAMS, "operationId 与 accountName 不能为空");

        // 卡号和论坛密码先登记脱敏, 之后不管接受与否都不会出现在发给外壳的消息里
        redactor.RegisterSecret(parameters.Minion?.Keycode);
        redactor.RegisterSecret(parameters.Minion?.ForumPassword);

        CatMinionLaunch? minionCard = null;

        if (parameters.Minion != null && ValidateMinion(parameters.Minion, out minionCard) is { } minionRejected)
            return minionRejected;

        if (parameters.CrashDialogTimeoutSeconds is { } crashTimeout and (< 1 or > CatProtocol.MAX_CRASH_DIALOG_TIMEOUT_SECONDS))
            return CatAcceptResult.Rejected(CatCodes.INVALID_PARAMS, $"crashDialogTimeoutSeconds 必须在 1 到 {CatProtocol.MAX_CRASH_DIALOG_TIMEOUT_SECONDS} 之间");

        if (!CatPlatforms.TryParsePlatform(parameters.Platform, out var channel))
            return CatAcceptResult.Rejected(CatCodes.INVALID_PARAMS, $"platform 只能是 {CatPlatforms.SHENGQU}、{CatPlatforms.WE_GAME} 或 {CatPlatforms.INTERNATIONAL}");

        var isInternational = channel == CatPlatform.International;

        // 密码不管哪个渠道带来的都先登记脱敏（不受最短长度限制）; 只有国际服会留着用
        redactor.RegisterSecret(parameters.Password);

        if (isInternational && string.IsNullOrEmpty(parameters.Password))
            return CatAcceptResult.Rejected(CatCodes.INVALID_PARAMS, "国际服必须带 password");

        // 国际服的游戏只能按国际服挂（注入文件和编号都不同）
        if (isInternational && parameters.Minion != null && parameters.Minion.Variant != MinionCards.VARIANT_GLOBAL)
            return CatAcceptResult.Rejected(CatCodes.INVALID_PARAMS, $"国际服的 minion.variant 只能是 {MinionCards.VARIANT_GLOBAL}");

        var weGameLogin = parameters.WeGameLogin == true;

        if (weGameLogin && channel != CatPlatform.WeGame)
            return CatAcceptResult.Rejected(CatCodes.INVALID_PARAMS, $"weGameLogin 只能用于 platform 为 {CatPlatforms.WE_GAME} 的号");

        if (!CatWeGameScans.TryParse(parameters.WeGameScan, out var weGameScan))
            return CatAcceptResult.Rejected(CatCodes.INVALID_PARAMS, $"weGameScan 只能是 {CatWeGameScans.QQ} 或 {CatWeGameScans.WE_CHAT}");

        if (weGameScan != null && !weGameLogin)
            return CatAcceptResult.Rejected(CatCodes.INVALID_PARAMS, "weGameScan 只能和 weGameLogin 一起用");

        var characterName = string.IsNullOrWhiteSpace(parameters.Character?.Name) ? null : parameters.Character.Name.Trim();
        var homeWorld     = string.IsNullOrWhiteSpace(parameters.Character?.HomeWorld) ? null : parameters.Character.HomeWorld.Trim();

        if (characterName is { Length: > MAX_CHARACTER_NAME_LENGTH } || characterName?.Any(char.IsControl) == true)
            return CatAcceptResult.Rejected(CatCodes.INVALID_PARAMS, $"character.name 不能超过 {MAX_CHARACTER_NAME_LENGTH} 个字符, 也不能有控制字符");

        if (homeWorld is { Length: > MAX_HOME_WORLD_LENGTH } || homeWorld?.Any(char.IsControl) == true)
            return CatAcceptResult.Rejected(CatCodes.INVALID_PARAMS, $"character.homeWorld 不能超过 {MAX_HOME_WORLD_LENGTH} 个字符, 也不能有控制字符");

        // 游戏内模块是按国服客户端写的, 国际服带了 autoEnter 也不做
        var autoEnter = parameters.AutoEnter == true && !isInternational;

        CatLaunchRequest accepted;
        ICatGameRunner   selected;

        lock (stateLock)
        {
            if (closeRequested)
                return CatAcceptResult.Rejected(CatCodes.CLOSING, "已收到 close, 本进程即将退出");

            if (request != null)
                return CatAcceptResult.Rejected(CatCodes.ALREADY_LAUNCHED, "本进程已经接受过一次 launch");

            accepted = new CatLaunchRequest
            (
                parameters.OperationId.Trim(),
                parameters.AccountName.Trim(),
                parameters.Dalamud,
                minionCard,
                parameters.CrashDialogTimeoutSeconds ?? CatProtocol.DEFAULT_CRASH_DIALOG_TIMEOUT_SECONDS,
                string.IsNullOrWhiteSpace(parameters.AreaName) ? null : parameters.AreaName.Trim(),
                channel == CatPlatform.WeGame ? XIVAccountType.WeGame : XIVAccountType.Sdo,
                isInternational,
                isInternational ? new CatSecret(parameters.Password!) : null,
                weGameLogin,
                weGameScan,
                autoEnter,
                characterName,
                homeWorld
            );
            selected = runnerFactory(accepted);
            runner   = selected;
            request  = accepted;
        }

        Serilog.Log.Information
        (
            "[CatHost] 接受 launch: 操作={OperationId}, 渠道={Platform}, 账号={Account}, Dalamud={Dalamud}, Minion={Minion}, 崩溃对话框等待={CrashTimeout}s, 就地登录 WeGame={WeGameLogin}, 自动切扫码页={WeGameScan}, 自动进入角色={AutoEnter}, 角色={Character}, 原始服务器={HomeWorld}",
            accepted.OperationId,
            CatPlatforms.DisplayName(accepted.Channel),
            accepted.AccountName,
            accepted.Dalamud,
            accepted.Minion ? $"{accepted.CardFingerprint}/{accepted.Variant}" : "否",
            accepted.CrashDialogTimeoutSeconds,
            accepted.WeGameLogin,
            accepted.WeGameScan is { } scan ? CatWeGameScans.Name(scan) : "否",
            accepted.AutoEnter,
            accepted.CharacterName ?? "(未指定)",
            accepted.CharacterHomeWorld ?? "(未指定)"
        );

        _ = Task.Run(() => RunLifecycleAsync(selected, accepted));
        return CatAcceptResult.Ok();
    }

    /// <summary>
    ///     接受一次 adopt: 接管一个在跑、但原守护进程已不在的游戏, 在后台守护到它退出。与 launch 互斥（一个进程只服务一个游戏）。
    ///     游戏怎么起的全从它的守护记录里读; 记录对不上、原守护者还活着、游戏已退出时当场拒绝
    /// </summary>
    public CatAcceptResult Adopt(CatAdoptParams? parameters)
    {
        if (parameters == null || string.IsNullOrWhiteSpace(parameters.OperationId) || parameters.Pid is not > 0)
            return CatAcceptResult.Rejected(CatCodes.INVALID_PARAMS, "operationId 不能为空, pid 必须是正整数");

        if (!DateTimeOffset.TryParse(parameters.ProcessStartedAt, System.Globalization.CultureInfo.InvariantCulture,
                                     System.Globalization.DateTimeStyles.AssumeUniversal, out var startedAt))
            return CatAcceptResult.Rejected(CatCodes.INVALID_PARAMS, "processStartedAt 必须是 ISO 8601 时间");

        redactor.RegisterSecret(parameters.Minion?.Keycode);
        redactor.RegisterSecret(parameters.Minion?.ForumPassword);

        CatMinionLaunch? minionCard = null;

        if (parameters.Minion != null && ValidateMinion(parameters.Minion, out minionCard) is { } minionRejected)
            return minionRejected;

        if (parameters.CrashDialogTimeoutSeconds is { } crashTimeout and (< 1 or > CatProtocol.MAX_CRASH_DIALOG_TIMEOUT_SECONDS))
            return CatAcceptResult.Rejected(CatCodes.INVALID_PARAMS, $"crashDialogTimeoutSeconds 必须在 1 到 {CatProtocol.MAX_CRASH_DIALOG_TIMEOUT_SECONDS} 之间");

        var gamePid = parameters.Pid.Value;

        if (InGame.GameRecords.ReadMatching(gamePid, startedAt) is not { } record)
            return CatAcceptResult.Rejected(CatCodes.GAME_NOT_FOUND, $"没有游戏 {gamePid} 的守护记录, 或进程号与创建时间对不上");

        if (string.IsNullOrEmpty(record.SndaId) || string.IsNullOrEmpty(record.AccountUserName))
            return CatAcceptResult.Rejected(CatCodes.GAME_NOT_FOUND, $"游戏 {gamePid} 的守护记录不完整（缺账号信息）");

        // 换了卡就不能拿来在崩溃重开后挂: 记录里挂的是哪张卡, 重开后也只挂那张
        if (minionCard != null && !string.Equals(minionCard.Fingerprint, record.MinionFingerprint, StringComparison.Ordinal))
            return CatAcceptResult.Rejected(CatCodes.INVALID_PARAMS, "minion.cardFingerprint 与这个游戏挂着的卡不同");

        // 先打开游戏进程并一直拿着句柄: 拿着期间进程号不会被别的进程复用, 之后按进程号做的事都落在这个游戏上
        if (OpenSameProcess(gamePid, record.ProcessStartedAt) is not { } game)
            return CatAcceptResult.Rejected(CatCodes.NOT_RUNNING, $"游戏 {gamePid} 已经退出");

        // 认领守护权（检查与认领是同一个动作）: 拿不到 = 原守护进程还在, 或另一个进程刚接管了它
        if (InGame.GameRecords.TryClaimGuard(gamePid, record.ProcessStartedAt) is not { } claim)
        {
            game.Dispose();
            return CatAcceptResult.Rejected(CatCodes.ALREADY_GUARDED, $"游戏 {gamePid} 已经有守护进程（记录里是 {record.GuardPid}）, 不能重复接管");
        }

        var timeoutSeconds = parameters.CrashDialogTimeoutSeconds
                             ?? (record.CrashDialogTimeoutSeconds > 0 ? record.CrashDialogTimeoutSeconds : CatProtocol.DEFAULT_CRASH_DIALOG_TIMEOUT_SECONDS);

        var adopt = new CatAdoptRequest(parameters.OperationId.Trim(), gamePid, record.ProcessStartedAt, record, timeoutSeconds, minionCard, game, claim);
        var asLaunch = new CatLaunchRequest
        (
            adopt.OperationId,
            record.AccountName,
            record.DalamudRequested,
            minionCard,
            timeoutSeconds,
            record.AreaName,
            record.Channel == InGame.GameRecordChannels.WE_GAME ? XIVAccountType.WeGame : XIVAccountType.Sdo,
            false,
            null,
            false,
            null,
            record.AutoEnter,
            record.CharacterName,
            record.CharacterHomeWorld
        );

        ICatGameRunner selected;

        lock (stateLock)
        {
            var rejected = closeRequested
                               ? CatAcceptResult.Rejected(CatCodes.CLOSING, "已收到 close, 本进程即将退出")
                               : request != null
                                   ? CatAcceptResult.Rejected(CatCodes.ALREADY_LAUNCHED, "本进程已经接受过一次 launch 或 adopt")
                                   : null;

            if (rejected != null)
            {
                claim.Dispose();
                game.Dispose();
                return rejected;
            }

            selected = runnerFactory(asLaunch);
            runner   = selected;
            request  = asLaunch;
        }

        Serilog.Log.Information
        (
            "[CatHost] 接受 adopt: 操作={OperationId}, 游戏={Pid}, 账号={Account}, 原守护进程={GuardPid}, 端口={Port}, Dalamud={Dalamud}, 带卡={Minion}",
            adopt.OperationId,
            gamePid,
            record.AccountName,
            record.GuardPid,
            record.DcTravelPort,
            record.Dalamud,
            minionCard != null
        );

        _ = Task.Run(() => RunAdoptLifecycleAsync(selected, adopt));
        return CatAcceptResult.Ok();
    }

    /// <summary>
    ///     对运行中的游戏补注入
    /// </summary>
    public CatAcceptResult Inject(CatInjectParams? parameters)
    {
        var wantDalamud = parameters?.Dalamud == true;
        var wantMinion  = parameters?.Minion  == true;
        var force       = parameters?.Force   == true;

        if (!wantDalamud && !wantMinion)
            return CatAcceptResult.Rejected(CatCodes.INVALID_PARAMS, "dalamud 与 minion 至少要有一个为 true");

        ICatGameRunner? current;

        lock (stateLock)
        {
            if (pid == null || !CatStages.IsGameRunning(stage) || closeRequested || runnerFaulted)
                return CatAcceptResult.Rejected(CatCodes.NOT_RUNNING, "当前没有运行中的游戏");

            if (handOffRequested)
                return CatAcceptResult.Rejected(CatCodes.BUSY, "正在交接停止, 本进程即将退出");

            current = runner;

            if (current == null)
                return CatAcceptResult.Rejected(CatCodes.NOT_RUNNING, "当前没有运行中的游戏");

            // 与交接停止在同一把锁里互斥: 交接看到 injectBusy == 0 后, 这里就不会再开始
            if (Interlocked.Exchange(ref injectBusy, 1) == 1)
                return CatAcceptResult.Rejected(CatCodes.BUSY, "上一次补注入还没结束");
        }

        _ = Task.Run
        (async () =>
            {
                try
                {
                    await current.InjectAsync(wantDalamud, wantMinion, force, this, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Serilog.Log.Error(ex, "[CatHost] 补注入时出错");
                    Log("error", $"补注入时出错: {ex.Message}");
                }
                finally
                {
                    Interlocked.Exchange(ref injectBusy, 0);
                }
            }
        );

        return CatAcceptResult.Ok();
    }

    /// <summary>
    ///     下号: 关掉崩溃重启 → 请游戏自己关闭（超时结束进程）→ 补报 Minion 停机 → game.exited → 本进程退出。
    ///     还没 launch 时直接退出; 启动途中则取消启动（launch.failed{cancelled}）。重复调用无副作用。
    /// </summary>
    public CatAcceptResult Close(CatCloseParams? parameters)
    {
        if (parameters?.TimeoutSeconds is { } seconds and (< 0 or > CatProtocol.MAX_CLOSE_TIMEOUT_SECONDS))
            return CatAcceptResult.Rejected(CatCodes.INVALID_PARAMS, $"timeoutSeconds 必须在 0 到 {CatProtocol.MAX_CLOSE_TIMEOUT_SECONDS} 之间");

        var timeout = TimeSpan.FromSeconds(parameters?.TimeoutSeconds ?? CatProtocol.DEFAULT_CLOSE_TIMEOUT_SECONDS);
        bool hasLaunch;
        bool faulted;
        int? gamePid;
        DateTimeOffset? startedAt;
        ICatGameRunner? current;

        lock (stateLock)
        {
            if (closeRequested)
                return CatAcceptResult.Ok();

            // 交接停止已在进行: 游戏要留给下一个守护进程, 这里不能再去关它（外壳要下号就对接管的那个进程发 close）
            if (handOffRequested)
                return CatAcceptResult.Rejected(CatCodes.BUSY, "正在交接停止, 本进程即将退出; 要下号请对接管的进程发 close");

            closeRequested = true;
            current        = runner;
            hasLaunch      = request != null;
            faulted        = runnerFaulted;
            gamePid        = pid;
            startedAt      = processStartedAt;

            if (stage is CatStages.EXITED or CatStages.FAILED)
                return CatAcceptResult.Ok();
        }

        Serilog.Log.Information("[CatHost] 收到 close, 等游戏自己退出最多 {Seconds}s", timeout.TotalSeconds);

        if (!hasLaunch || current == null)
        {
            completion.TrySetResult(CatHostRuntime.EXIT_OK);
            return CatAcceptResult.Ok();
        }

        _ = Task.Run
        (async () =>
            {
                try
                {
                    // 启动器已出错不再守护时, 直接按进程号关游戏
                    if (faulted && gamePid is { } target)
                        await CatGameCloser.CloseAsync(target, startedAt, timeout, current.GameExit).ConfigureAwait(false);
                    else
                        await current.CloseAsync(timeout).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Serilog.Log.Error(ex, "[CatHost] 关闭游戏时出错");
                    Log("error", $"关闭游戏时出错: {ex.Message}");
                }
            }
        );

        return CatAcceptResult.Ok();
    }

    /// <summary>
    ///     交接停止（更新启动器时用）: 本进程不再守这个游戏, 但不关游戏、不登出跨区、不删守护记录、不发 game.exited,
    ///     发 game.handedOff 后以 <see cref="CatHostRuntime.EXIT_HANDED_OFF" /> 退出; 守护锁随进程退出放掉, 下一个进程凭记录 adopt。
    ///     只在游戏稳定运行时接受（running / inWorld, 且没有补注入、游戏内跨区、崩溃对话框）, 否则回 busy, 稍后再试。
    /// </summary>
    public async Task<CatAcceptResult> HandOffAsync(CancellationToken cancellationToken = default)
    {
        ICatGameRunner current;
        int            gamePid;
        DateTimeOffset startedAt;

        lock (stateLock)
        {
            if (closeRequested)
                return CatAcceptResult.Rejected(CatCodes.CLOSING, "已收到 close, 本进程即将退出");

            if (handOffRequested)
                return CatAcceptResult.Rejected(CatCodes.BUSY, "交接停止已在进行");

            if (runner == null || pid is not { } p || processStartedAt is not { } at || stage is CatStages.EXITED or CatStages.FAILED)
                return CatAcceptResult.Rejected(CatCodes.NOT_RUNNING, "当前没有运行中的游戏");

            // 启动、崩溃重开、自动进入角色途中交接会把这些流程拦腰截断（接管方只会接着守, 不会接着做）
            if (stage is not (CatStages.RUNNING or CatStages.IN_WORLD))
                return CatAcceptResult.Rejected(CatCodes.BUSY, $"游戏正处于 {stage} 阶段, 现在交接会打断它, 稍后再试");

            if (Volatile.Read(ref injectBusy) == 1)
                return CatAcceptResult.Rejected(CatCodes.BUSY, "正在补注入, 稍后再试");

            handOffRequested = true;
            current          = runner;
            gamePid          = p;
            startedAt        = at;
        }

        CatAcceptResult? blocked;

        try
        {
            blocked = await current.PrepareHandOffAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[CatHost] 准备交接停止时出错, 照旧守护");
            blocked = CatAcceptResult.Rejected(CatCodes.LAUNCH_FAILED, $"准备交接停止时出错: {ex.Message}");
        }

        if (blocked != null)
        {
            lock (stateLock)
                handOffRequested = false;

            Serilog.Log.Information("[CatHost] 交接停止被拒: {Code} {Message}", blocked.Code, blocked.Message);
            return blocked;
        }

        Serilog.Log.Information("[CatHost] 交接停止: 游戏 {Pid} 留给下一个守护进程接管, 本进程退出", gamePid);
        Publish(CatHandOff.EVENT, new { operationId = OperationId, pid = gamePid, processStartedAt = CatProtocol.FormatTimestamp(startedAt) });

        // game.handedOff 是本进程关于这个游戏的最后一句话: 之后启动器里残留的动静（如游戏恰好崩了）一律不再发, 归接管的进程报
        handOffCommitted = true;
        completion.TrySetResult(CatHostRuntime.EXIT_HANDED_OFF);
        return CatAcceptResult.Ok();
    }

    private async Task<object?> HandOffBoxedAsync(CancellationToken cancellationToken) =>
        await HandOffAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    ///     客户说设备验证的短信已经发了: 交给启动器去点验证窗口的「确定」, 立即回是否点了
    /// </summary>
    public CatAcceptResult ConfirmWeGameSms(CatWeGameConfirmSmsParams? parameters)
    {
        if (string.IsNullOrWhiteSpace(parameters?.ChallengeId))
            return CatAcceptResult.Rejected(CatCodes.INVALID_PARAMS, "challengeId 不能为空");

        ICatGameRunner? current;

        lock (stateLock)
        {
            if (closeRequested || runnerFaulted || stage is CatStages.EXITED or CatStages.FAILED)
                return CatAcceptResult.Rejected(CatCodes.NOT_RUNNING, "当前没有在等 WeGame 的设备验证");

            current = runner;
        }

        return current == null
                   ? CatAcceptResult.Rejected(CatCodes.NOT_RUNNING, "当前没有在等 WeGame 的设备验证")
                   : current.ConfirmWeGameSms(parameters.ChallengeId.Trim());
    }

    /// <summary>
    ///     员工在工作台选了要登录的角色: 只在等人选角色（awaitingCharacterChoice）时交给启动器, 立即回是否接受
    /// </summary>
    public CatAcceptResult SelectCharacter(CatSelectCharacterParams? parameters)
    {
        var contentId = parameters?.ContentId?.Trim();

        if (string.IsNullOrEmpty(contentId) || contentId.Length > 32 || !contentId.All(char.IsAsciiLetterOrDigit))
            return CatAcceptResult.Rejected(CatCodes.INVALID_PARAMS, "contentId 不能为空, 且只能是字母和数字");

        ICatGameRunner? current;

        lock (stateLock)
        {
            if (closeRequested || runnerFaulted || stage != CatStages.AWAITING_CHARACTER_CHOICE)
                return CatAcceptResult.Rejected(CatCodes.NOT_RUNNING, "当前没有在等人选角色");

            current = runner;
        }

        return current == null
                   ? CatAcceptResult.Rejected(CatCodes.NOT_RUNNING, "当前没有在等人选角色")
                   : current.SelectCharacter(contentId);
    }

    /// <summary>
    ///     当前状态
    /// </summary>
    public CatStatusResult GetStatus()
    {
        lock (stateLock)
            return new CatStatusResult(stage, pid, processStartedAt is { } at ? CatProtocol.FormatTimestamp(at) : null, dalamudOk, minionOk);
    }

    /// <summary>
    ///     等队列里的事件都交给管道服务端, 或超时
    /// </summary>
    public async Task<bool> DrainEventsAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (PendingEventCount > 0)
        {
            if (DateTime.UtcNow >= deadline)
                return false;

            await Task.Delay(20).ConfigureAwait(false);
        }

        return true;
    }

    #region ICatLaunchReporter

    /// <inheritdoc />
    public void Stage(string newStage)
    {
        lock (stateLock)
            stage = newStage;

        Publish("game.stage", new { operationId = OperationId, stage = newStage });
    }

    /// <inheritdoc />
    public void Queueing(int? queuePosition)
    {
        lock (stateLock)
            stage = CatStages.QUEUEING;

        Publish("game.stage", new { operationId = OperationId, stage = CatStages.QUEUEING, queuePosition });
    }

    /// <inheritdoc />
    public void Characters(bool needsChoice, IReadOnlyList<CatCharacterInfo> characters) =>
        Publish("game.characters", new { operationId = OperationId, needsChoice, characters });

    /// <inheritdoc />
    public void Character(CatCharacterInfo character) =>
        Publish
        (
            "game.character",
            new
            {
                operationId      = OperationId,
                contentId        = character.ContentId,
                name             = character.Name,
                homeWorld        = character.HomeWorld,
                currentWorld     = character.CurrentWorld,
                homeWorldName    = character.HomeWorldName,
                currentWorldName = character.CurrentWorldName
            }
        );

    /// <inheritdoc />
    public void AutoEnterStopped(string code, string message)
    {
        Serilog.Log.Warning("[CatHost] 自动进入角色停下: {Code} {Message}", code, message);
        Publish("game.autoEnterStopped", new { operationId = OperationId, code, message = Redact(message) });
    }

    /// <inheritdoc />
    public void Adopted(int newPid, DateTimeOffset startedAt)
    {
        lock (stateLock)
        {
            pid              = newPid;
            processStartedAt = startedAt;
        }

        Publish("game.adopted", new { operationId = OperationId, pid = newPid, processStartedAt = CatProtocol.FormatTimestamp(startedAt) });
    }

    /// <inheritdoc />
    public void Started(int newPid, DateTimeOffset startedAt)
    {
        lock (stateLock)
        {
            pid              = newPid;
            processStartedAt = startedAt;
        }

        Publish("game.started", new { operationId = OperationId, pid = newPid, processStartedAt = CatProtocol.FormatTimestamp(startedAt) });
    }

    /// <inheritdoc />
    public void Restarted(int oldPid, int newPid, DateTimeOffset startedAt)
    {
        lock (stateLock)
        {
            pid              = newPid;
            processStartedAt = startedAt;
            dalamudOk        = null;
            minionOk         = null;
        }

        Publish("game.restarted", new { operationId = OperationId, oldPid, pid = newPid, processStartedAt = CatProtocol.FormatTimestamp(startedAt) });
    }

    /// <inheritdoc />
    public void Crashed(int crashedPid) =>
        Publish("game.crashed", new { operationId = OperationId, pid = crashedPid });

    /// <inheritdoc />
    public void Agent(string kind, bool ok, string? code = null, string? message = null)
    {
        lock (stateLock)
        {
            if (kind == CatAgentKinds.DALAMUD)
                dalamudOk = ok;
            else if (kind == CatAgentKinds.MINION)
                minionOk = ok;
        }

        Publish("game.agent", new { operationId = OperationId, kind, ok, code, message = Redact(message) });
    }

    /// <inheritdoc />
    public void Exited(int exitedPid, int? exitCode, string? reason = null, string? code = null, string? message = null)
    {
        lock (stateLock)
            stage = CatStages.EXITED;

        Publish("game.exited", new { operationId = OperationId, pid = exitedPid, exitCode, reason, code, message = Redact(message) });
    }

    /// <inheritdoc />
    public void Failed(string code, string message)
    {
        lock (stateLock)
            stage = CatStages.FAILED;

        Serilog.Log.Warning("[CatHost] 启动失败: {Code} {Message}", code, message);
        Publish("launch.failed", new { operationId = OperationId, code, message = Redact(message) });
    }

    /// <inheritdoc />
    public void Log(string level, string message) =>
        Publish("launcher.log", new { level, message = Redact(message) });

    /// <inheritdoc />
    public void WeGameChallenge(CatWeGameChallenge challenge) =>
        Publish
        (
            "weGame.challenge",
            new
            {
                operationId      = OperationId,
                kind             = challenge.Kind,
                challengeId      = challenge.ChallengeId,
                image            = challenge.Image,
                link             = challenge.Link,
                expiresInSeconds = challenge.ExpiresInSeconds,
                code             = challenge.Code,
                phone            = challenge.Phone,
                text             = challenge.Text
            }
        );

    /// <inheritdoc />
    public void WeGameChallengeCleared(string challengeId) =>
        Publish("weGame.challengeCleared", new { operationId = OperationId, challengeId });

    /// <inheritdoc />
    public void WeGameScanSwitchFailed(string scan) =>
        Publish("weGame.scanSwitchFailed", new { operationId = OperationId, scan });

    /// <inheritdoc />
    public void WeGameSmsResult(string challengeId, bool passed) =>
        Publish("weGame.smsResult", new { operationId = OperationId, challengeId, passed });

    #endregion

    private async Task RunLifecycleAsync(ICatGameRunner selected, CatLaunchRequest accepted)
    {
        int exitCode;

        try
        {
            exitCode = await selected.RunAsync(accepted, this, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[CatHost] 启动流程出现未处理异常");
            exitCode = await HandleRunnerFaultAsync(ex).ConfigureAwait(false);
        }

        completion.TrySetResult(exitCode);
    }

    private async Task RunAdoptLifecycleAsync(ICatGameRunner selected, CatAdoptRequest adopt)
    {
        int exitCode;

        try
        {
            exitCode = await selected.AdoptAsync(adopt, this, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[CatHost] 接管流程出现未处理异常");
            exitCode = await HandleRunnerFaultAsync(ex).ConfigureAwait(false);
        }
        finally
        {
            // 接管来的那个游戏已经结束（或没接管成）, 放掉它的守护锁; 崩溃重开出来的新游戏由启动器自己另外认领
            adopt.GuardClaim.Dispose();
            adopt.Game.Dispose();
        }

        completion.TrySetResult(exitCode);
    }

    /// <summary>
    ///     打开进程号对应的进程, 并核对创建时间是同一个游戏; 已退出或对不上返回 null。返回的进程已打开句柄（拿着期间进程号不会被复用）
    /// </summary>
    internal static System.Diagnostics.Process? OpenSameProcess(int processId, DateTimeOffset expectedStartedAt)
    {
        System.Diagnostics.Process? process = null;

        try
        {
            process = System.Diagnostics.Process.GetProcessById(processId);
            _ = process.SafeHandle;

            if (!process.HasExited && Math.Abs((MinionOccupancy.GetProcessStartedAt(process) - expectedStartedAt).TotalSeconds) < 2)
                return process;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // 已退出或打不开
        }

        process?.Dispose();
        return null;
    }

    /// <summary>
    ///     启动器抛出意外异常: 游戏还没起来按启动失败处理; 已起来则继续等到游戏结束再发 game.exited, 期间 close 仍可用
    /// </summary>
    private async Task<int> HandleRunnerFaultAsync(Exception ex)
    {
        var detail = $"{ex.GetType().Name}: {ex.Message}";
        int? gamePid;
        DateTimeOffset? startedAt;
        string currentStage;

        lock (stateLock)
        {
            runnerFaulted = true;
            gamePid       = pid;
            startedAt     = processStartedAt;
            currentStage  = stage;
        }

        if (gamePid is not { } target)
        {
            Failed(CatCodes.LAUNCH_FAILED, $"启动流程出现未处理异常: {detail}");
            return EXIT_LAUNCH_FAILED;
        }

        if (currentStage == CatStages.EXITED)
            return CatHostRuntime.EXIT_OK;

        Log("error", $"守护游戏时出错, 不再负责崩溃重启和跨区, 游戏结束后才会发 game.exited: {detail}");

        while (CatGameCloser.IsAlive(target, startedAt))
            await Task.Delay(GuardErrorPollInterval).ConfigureAwait(false);

        try
        {
            MinionAppStatusReporter.ReportStopped(target);
        }
        catch (Exception reportError)
        {
            Serilog.Log.Warning(reportError, "[CatHost] 补报 Minion 停机失败");
        }

        bool closed;

        lock (stateLock)
            closed = closeRequested;

        Exited(target, null, closed ? CatExitReasons.CLOSED : CatExitReasons.GUARD_ERROR, null, closed ? null : detail);
        return EXIT_GUARD_ERROR;
    }

    private void Publish(string method, object parameters)
    {
        if (handOffCommitted)
        {
            Serilog.Log.Debug("[CatHost] 已交接停止, 不再发 {Method}", method);
            return;
        }

        Interlocked.Increment(ref pendingEvents);

        if (!events.Writer.TryWrite((method, parameters)))
            Interlocked.Decrement(ref pendingEvents);
    }

    private async Task PumpEventsAsync()
    {
        await foreach (var (method, parameters) in events.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                await publish(method, parameters).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Serilog.Log.Debug(ex, "[CatHost] 发送事件 {Method} 失败", method);
            }
            finally
            {
                Interlocked.Decrement(ref pendingEvents);
            }
        }
    }

    private string? Redact(string? message) =>
        message == null ? null : redactor.Redact(message);

    private static T? Deserialize<T>(JsonElement parameters) where T : class
    {
        if (parameters.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return null;

        if (parameters.ValueKind != JsonValueKind.Object)
            throw new CatRpcException(CatRpcException.INVALID_PARAMS, "params 必须是对象");

        return parameters.Deserialize<T>(CatProtocol.JsonOptions);
    }
}
