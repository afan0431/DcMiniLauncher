using System.Text.Json;
using System.Threading.Channels;
using Serilog;
using XIVLauncher.Minion;

namespace XIVLauncher.CatHost;

/// <summary>
///     一次 launch 的参数（已校验）
/// </summary>
public sealed record CatLaunchRequest
(
    string  OperationId,
    string  AccountName,
    bool    Dalamud,
    string? CardFingerprint,
    string? Variant,
    int     CrashDialogTimeoutSeconds = CatProtocol.DEFAULT_CRASH_DIALOG_TIMEOUT_SECONDS
)
{
    /// <summary>是否要挂 Minion</summary>
    public bool Minion => CardFingerprint != null;

    /// <summary>游戏已退出而崩溃处理器还开着时最多等多久</summary>
    public TimeSpan CrashDialogTimeout => TimeSpan.FromSeconds(CrashDialogTimeoutSeconds);
}

/// <summary>
///     启动过程向外壳报告进度
/// </summary>
public interface ICatLaunchReporter
{
    /// <summary>进入某个阶段</summary>
    void Stage(string stage);

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
    ///     游戏还没起来时取消启动。之后 <see cref="RunAsync" /> 照常收尾（补报 Minion 停机、发 game.exited 或 launch.failed）。
    /// </summary>
    Task CloseAsync(TimeSpan gracefulTimeout);
}

/// <summary>
///     dml-cat/1 的方法分发与状态: 一个进程只接受一次 launch, 状态随事件更新。
///     事件先进队列, 由后台按序写出, 外壳不读管道时启动流程也不会被卡住。
/// </summary>
public sealed class CatLaunchHost : ICatRpcHandler, ICatLaunchReporter
{
    /// <summary>launch 失败后的进程退出码</summary>
    public const int EXIT_LAUNCH_FAILED = 3;

    /// <summary>游戏起来后本进程内部出错, 已等游戏结束并发 game.exited{reason:"guardError"}</summary>
    public const int EXIT_GUARD_ERROR = 5;

    private static readonly TimeSpan GuardErrorPollInterval = TimeSpan.FromSeconds(1);

    private readonly ICatGameRunner             runner;
    private readonly Func<string, object, Task> publish;
    private readonly CatLogRedactor             redactor;
    private readonly object                     stateLock  = new();
    private readonly TaskCompletionSource<int>  completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<(string Method, object Parameters)> events =
        Channel.CreateUnbounded<(string Method, object Parameters)>(new UnboundedChannelOptions { SingleReader = true });

    private CatLaunchRequest? request;
    private string            stage = CatStages.IDLE;
    private int?              pid;
    private DateTimeOffset?   processStartedAt;
    private bool?             dalamudOk;
    private bool?             minionOk;
    private int               injectBusy;
    private bool              closeRequested;
    private bool              runnerFaulted;
    private int               pendingEvents;

    /// <summary>
    ///     创建分发器
    /// </summary>
    /// <param name="runner">真实或模拟启动器</param>
    /// <param name="publish">发事件（方法名, 参数）</param>
    /// <param name="redactor">日志脱敏</param>
    public CatLaunchHost(ICatGameRunner runner, Func<string, object, Task> publish, CatLogRedactor redactor)
    {
        this.runner   = runner;
        this.publish  = publish;
        this.redactor = redactor;

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
            "inject" => Task.FromResult<object?>(Inject(Deserialize<CatInjectParams>(parameters))),
            "status" => Task.FromResult<object?>(GetStatus()),
            "close"  => Task.FromResult<object?>(Close(Deserialize<CatCloseParams>(parameters))),
            _        => throw new CatRpcException(CatRpcException.METHOD_NOT_FOUND, $"未知方法: {method}")
        };

    /// <summary>
    ///     接受一次 launch 并在后台执行
    /// </summary>
    public CatAcceptResult Launch(CatLaunchParams? parameters)
    {
        if (parameters == null || string.IsNullOrWhiteSpace(parameters.OperationId) || string.IsNullOrWhiteSpace(parameters.AccountName))
            return CatAcceptResult.Rejected(CatCodes.INVALID_PARAMS, "operationId 与 accountName 不能为空");

        if (parameters.Minion != null)
        {
            if (!MinionCards.IsValidFingerprint(parameters.Minion.CardFingerprint))
                return CatAcceptResult.Rejected(CatCodes.INVALID_PARAMS, "minion.cardFingerprint 必须是 16 位小写十六进制");

            if (!MinionCards.IsValidVariant(parameters.Minion.Variant))
                return CatAcceptResult.Rejected(CatCodes.INVALID_PARAMS, "minion.variant 只能是 cn 或 global");
        }

        if (parameters.CrashDialogTimeoutSeconds is { } crashTimeout and (< 1 or > CatProtocol.MAX_CRASH_DIALOG_TIMEOUT_SECONDS))
            return CatAcceptResult.Rejected(CatCodes.INVALID_PARAMS, $"crashDialogTimeoutSeconds 必须在 1 到 {CatProtocol.MAX_CRASH_DIALOG_TIMEOUT_SECONDS} 之间");

        CatLaunchRequest accepted;

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
                parameters.Minion?.CardFingerprint,
                parameters.Minion?.Variant,
                parameters.CrashDialogTimeoutSeconds ?? CatProtocol.DEFAULT_CRASH_DIALOG_TIMEOUT_SECONDS
            );
            request = accepted;
        }

        Serilog.Log.Information
        (
            "[CatHost] 接受 launch: 操作={OperationId}, 账号={Account}, Dalamud={Dalamud}, Minion={Minion}, 崩溃对话框等待={CrashTimeout}s",
            accepted.OperationId,
            accepted.AccountName,
            accepted.Dalamud,
            accepted.Minion ? $"{accepted.CardFingerprint}/{accepted.Variant}" : "否",
            accepted.CrashDialogTimeoutSeconds
        );

        _ = Task.Run(() => RunLifecycleAsync(accepted));
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

        lock (stateLock)
        {
            if (pid == null || stage != CatStages.RUNNING || closeRequested || runnerFaulted)
                return CatAcceptResult.Rejected(CatCodes.NOT_RUNNING, "当前没有运行中的游戏");
        }

        if (Interlocked.Exchange(ref injectBusy, 1) == 1)
            return CatAcceptResult.Rejected(CatCodes.BUSY, "上一次补注入还没结束");

        _ = Task.Run
        (async () =>
            {
                try
                {
                    await runner.InjectAsync(wantDalamud, wantMinion, force, this, CancellationToken.None).ConfigureAwait(false);
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

        lock (stateLock)
        {
            if (closeRequested)
                return CatAcceptResult.Ok();

            closeRequested = true;
            hasLaunch      = request != null;
            faulted        = runnerFaulted;
            gamePid        = pid;
            startedAt      = processStartedAt;

            if (stage is CatStages.EXITED or CatStages.FAILED)
                return CatAcceptResult.Ok();
        }

        Serilog.Log.Information("[CatHost] 收到 close, 等游戏自己退出最多 {Seconds}s", timeout.TotalSeconds);

        if (!hasLaunch)
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
                        await CatGameCloser.CloseAsync(target, startedAt, timeout).ConfigureAwait(false);
                    else
                        await runner.CloseAsync(timeout).ConfigureAwait(false);
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

    #endregion

    private async Task RunLifecycleAsync(CatLaunchRequest accepted)
    {
        int exitCode;

        try
        {
            exitCode = await runner.RunAsync(accepted, this, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[CatHost] 启动流程出现未处理异常");
            exitCode = await HandleRunnerFaultAsync(ex).ConfigureAwait(false);
        }

        completion.TrySetResult(exitCode);
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

        MinionAppStatusReporter.ReportStopped(target);

        bool closed;

        lock (stateLock)
            closed = closeRequested;

        Exited(target, null, closed ? CatExitReasons.CLOSED : CatExitReasons.GUARD_ERROR, null, closed ? null : detail);
        return EXIT_GUARD_ERROR;
    }

    private void Publish(string method, object parameters)
    {
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
