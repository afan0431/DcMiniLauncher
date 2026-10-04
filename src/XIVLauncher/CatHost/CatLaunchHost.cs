using System.Text.Json;
using Serilog;
using XIVLauncher.Minion;

namespace XIVLauncher.CatHost;

/// <summary>
///     一次 launch 的参数（已校验）
/// </summary>
public sealed record CatLaunchRequest(string OperationId, string AccountName, bool Dalamud, string? CardFingerprint, string? Variant)
{
    /// <summary>是否要挂 Minion</summary>
    public bool Minion => CardFingerprint != null;
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

    /// <summary>Dalamud / Minion 注入结果</summary>
    void Agent(string kind, bool ok, string? code = null, string? message = null);

    /// <summary>游戏已退出（不再重启）</summary>
    void Exited(int pid, int? exitCode);

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
    Task InjectAsync(bool dalamud, bool minion, ICatLaunchReporter reporter, CancellationToken cancellationToken);
}

/// <summary>
///     dml-cat/1 的方法分发与状态: 一个进程只接受一次 launch, 状态随事件更新
/// </summary>
public sealed class CatLaunchHost : ICatRpcHandler, ICatLaunchReporter
{
    /// <summary>launch 失败后的进程退出码</summary>
    public const int EXIT_LAUNCH_FAILED = 3;

    private readonly ICatGameRunner             runner;
    private readonly Func<string, object, Task> publish;
    private readonly CatLogRedactor             redactor;
    private readonly object                     stateLock = new();
    private readonly TaskCompletionSource<int>  completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private CatLaunchRequest? request;
    private string            stage = CatStages.IDLE;
    private int?              pid;
    private DateTimeOffset?   processStartedAt;
    private bool?             dalamudOk;
    private bool?             minionOk;
    private int               injectBusy;

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

    /// <inheritdoc />
    public Task<object?> HandleAsync(string method, JsonElement parameters, CancellationToken cancellationToken) =>
        method switch
        {
            "launch" => Task.FromResult<object?>(Launch(Deserialize<CatLaunchParams>(parameters))),
            "inject" => Task.FromResult<object?>(Inject(Deserialize<CatInjectParams>(parameters))),
            "status" => Task.FromResult<object?>(GetStatus()),
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

        CatLaunchRequest accepted;

        lock (stateLock)
        {
            if (request != null)
                return CatAcceptResult.Rejected(CatCodes.ALREADY_LAUNCHED, "本进程已经接受过一次 launch");

            accepted = new CatLaunchRequest
            (
                parameters.OperationId.Trim(),
                parameters.AccountName.Trim(),
                parameters.Dalamud,
                parameters.Minion?.CardFingerprint,
                parameters.Minion?.Variant
            );
            request = accepted;
        }

        Serilog.Log.Information
        (
            "[CatHost] 接受 launch: 操作={OperationId}, 账号={Account}, Dalamud={Dalamud}, Minion={Minion}",
            accepted.OperationId,
            accepted.AccountName,
            accepted.Dalamud,
            accepted.Minion ? $"{accepted.CardFingerprint}/{accepted.Variant}" : "否"
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

        if (!wantDalamud && !wantMinion)
            return CatAcceptResult.Rejected(CatCodes.INVALID_PARAMS, "dalamud 与 minion 至少要有一个为 true");

        lock (stateLock)
        {
            if (pid == null || stage != CatStages.RUNNING)
                return CatAcceptResult.Rejected(CatCodes.NOT_RUNNING, "当前没有运行中的游戏");
        }

        if (Interlocked.Exchange(ref injectBusy, 1) == 1)
            return CatAcceptResult.Rejected(CatCodes.LAUNCH_FAILED, "上一次补注入还没结束");

        _ = Task.Run
        (async () =>
            {
                try
                {
                    await runner.InjectAsync(wantDalamud, wantMinion, this, CancellationToken.None).ConfigureAwait(false);
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
    ///     当前状态
    /// </summary>
    public CatStatusResult GetStatus()
    {
        lock (stateLock)
            return new CatStatusResult(stage, pid, processStartedAt is { } at ? CatProtocol.FormatTimestamp(at) : null, dalamudOk, minionOk);
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
    public void Exited(int exitedPid, int? exitCode)
    {
        lock (stateLock)
            stage = CatStages.EXITED;

        Publish("game.exited", new { operationId = OperationId, pid = exitedPid, exitCode });
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

            if (HasStarted)
                Log("error", $"启动流程出现未处理异常: {ex.GetType().Name}: {ex.Message}");
            else
                Failed(CatCodes.LAUNCH_FAILED, $"启动流程出现未处理异常: {ex.GetType().Name}: {ex.Message}");

            exitCode = EXIT_LAUNCH_FAILED;
        }

        completion.TrySetResult(exitCode);
    }

    private void Publish(string method, object parameters)
    {
        try
        {
            publish(method, parameters).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "[CatHost] 发送事件 {Method} 失败", method);
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
