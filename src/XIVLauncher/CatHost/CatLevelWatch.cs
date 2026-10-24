using System.Globalization;
using Serilog;
using XIVLauncher.InGame;

namespace XIVLauncher.CatHost;

/// <summary>
///     向游戏内模块读一次的结果
/// </summary>
internal enum CatModuleRead
{
    /// <summary>模块回应了（读到值、没读成、读数不合理都算）; 这一轮可以接着发别的命令</summary>
    Answered,

    /// <summary>模块是旧版, 不认识这条命令; 之后不再读</summary>
    Unsupported,

    /// <summary>
    ///     超时没有回应: 发命令的一方超时即断开管道（下一条命令重新连）, 模块多半卡着, 这一轮不要再发别的命令
    /// </summary>
    TimedOut
}

/// <summary>
///     向游戏内模块读一次全部职业等级（<c>LEVELS</c>）并连同同一轮 WHOAMI 读到的当前角色报告; 报不报 game.levels 由 <see cref="CatJobChannel" />
///     按角色或数组有没有变化决定。游戏里注着的是不认识这条命令的旧模块（0.8.0 之前）时记一次日志, 之后不再读。
///     每次读都有上限（<see cref="CatAutoEnterTimings.ReadTimeout" />）, 超时跳过这一轮, 不一直占着模块闸
/// </summary>
internal sealed class CatLevelReader(ICatLaunchReporter reporter, int processId, TimeSpan timeout)
{
    /// <summary>模块命令</summary>
    public const string COMMAND = "LEVELS";

    private bool unsupported;
    private bool warnedInconsistent;

    /// <summary>模块不认识 <c>LEVELS</c>, 不再读</summary>
    public bool Unsupported => unsupported;

    /// <summary>
    ///     读一次。who = 同一轮读到的当前角色（必须已载入）。通话失败照常抛异常, 由调用方决定跳过这一次; 调用方事后要放开管道
    /// </summary>
    public async Task<CatModuleRead> ReadAsync(IMiniModuleChannel module, CatModuleReplies.WhoAmI who, CancellationToken cancellationToken)
    {
        if (unsupported)
            return CatModuleRead.Unsupported;

        if (await SendWithTimeoutAsync(module, COMMAND, timeout, cancellationToken).ConfigureAwait(false) is not { } response)
        {
            Log.Debug("[CatHost] 读职业等级 {Seconds} 秒没有回应, 跳过这一次", timeout.TotalSeconds);
            return CatModuleRead.TimedOut;
        }

        if (response.Contains("unknown-command", StringComparison.Ordinal))
        {
            unsupported = true;
            Log.Information("[CatHost] 游戏 {Pid} 里的游戏内模块是旧版, 读不了职业等级（下次启动游戏才会换上新模块）", processId);
            return CatModuleRead.Unsupported;
        }

        if (CatModuleReplies.ParseLevels(response) is not { } reading)
        {
            if (!CatModuleReplies.IsOk(response))
                Log.Debug("[CatHost] 这一次没读成职业等级: {Response}", CatModuleReplies.FirstLine(response));

            return CatModuleRead.Answered;
        }

        // 当前职业的那一格应等于当前等级（等级同步中不比）: 对不上多半是游戏更新后偏移漂移, 读出的是形似等级的错位数据, 不发
        if (!reading.Consistent)
        {
            if (!warnedInconsistent)
            {
                warnedInconsistent = true;
                Log.Warning
                (
                    "[CatHost] 游戏 {Pid} 的职业等级读数对不上当前职业（ClassJob {Job} 当前 {Level} 级）, 不发; 若一直如此要按新版客户端核对偏移",
                    processId,
                    reading.ClassJob,
                    reading.Level
                );
            }

            return CatModuleRead.Answered;
        }

        reporter.Levels(processId, new CatLevelsCharacter(who.Name, who.ContentId, who.HomeWorldId), reading.Levels);
        return CatModuleRead.Answered;
    }

    /// <summary>
    ///     发一条只读命令, 最多等 <paramref name="timeout" />; 超时返回 null, 外部取消照常抛 <see cref="OperationCanceledException" />
    /// </summary>
    public static async Task<string?> SendWithTimeoutAsync(IMiniModuleChannel module, string command, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var limited = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limited.CancelAfter(timeout);

        try
        {
            return await module.SendAsync(command, limited.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }
}

/// <summary>
///     一个游戏的"是否在副本中"读数怎样变成报告: 每读到一次值就报（发不发 game.duty 由 <see cref="CatJobChannel" /> 按有没有变化决定）;
///     读到过值之后连续 <see cref="MISSES_BEFORE_UNKNOWN" /> 轮读不到, 报一次 null（未知）, 之后再读到值照常报; 从未读到过值时读不到不报。
///     真实启动由 <see cref="CatDutyReader" /> 用, 模拟模式由 <see cref="CatSimulatedAfan" /> 用
/// </summary>
internal sealed class CatDutyTracker(Action<bool?, DateTimeOffset> report)
{
    /// <summary>读到过值之后连续这么多轮读不到, 报一次"未知"</summary>
    public const int MISSES_BEFORE_UNKNOWN = 2;

    private bool hasValue;
    private int  misses;

    /// <summary>这一轮读到了值</summary>
    public void Read(bool inDuty, DateTimeOffset at)
    {
        hasValue = true;
        misses   = 0;
        report(inDuty, at);
    }

    /// <summary>这一轮没读到值（没载入角色、读失败、超时、模块旧、读数不合理）</summary>
    public void Missed(DateTimeOffset at)
    {
        if (!hasValue)
            return;

        if (++misses == MISSES_BEFORE_UNKNOWN)
            report(null, at);
    }
}

/// <summary>
///     向游戏内模块读角色是否在副本中（<c>DUTY</c>, Conditions 的 BoundByDuty 类标志）并报告, 规则见 <see cref="CatDutyTracker" />:
///     读到过值之后连续两轮读不到（没载入角色、FAIL、超时、旧模块、读数不合理）报一次"未知"。
///     载入了角色却 112 个标志全为 0（正常时下标 1 NormalConditions 应置位）当作读不到, 连续 <see cref="ALL_CLEAR_ROUNDS_BEFORE_WARNING" /> 轮记一次警告;
///     模块回 sigscan-failed、bad-state 第一次出现时记警告, 之后不刷屏。游戏里注着的是不认识这条命令的旧模块（0.9.0 之前）时记一次日志, 之后不再读。
///     每次读都有上限（<see cref="CatAutoEnterTimings.ReadTimeout" />）。每一轮观察结束都要调 <see cref="EndRound" />（不论这一轮读没读 DUTY）
/// </summary>
internal sealed class CatDutyReader
{
    /// <summary>模块命令</summary>
    public const string COMMAND = "DUTY";

    /// <summary>载入了角色却全部标志为 0 连续这么多轮, 记一次警告</summary>
    public const int ALL_CLEAR_ROUNDS_BEFORE_WARNING = 3;

    private readonly int            processId;
    private readonly TimeSpan       timeout;
    private readonly CatDutyTracker tracker;

    private bool unsupported;
    private bool readThisRound;
    private int  allClearRounds;
    private bool warnedAllClear;
    private bool warnedSigscan;
    private bool warnedBadState;

    /// <summary>
    ///     创建
    /// </summary>
    public CatDutyReader(ICatLaunchReporter reporter, int processId, TimeSpan timeout)
    {
        this.processId = processId;
        this.timeout   = timeout;
        tracker        = new CatDutyTracker((inDuty, at) => reporter.Duty(processId, inDuty, at));
    }

    /// <summary>模块不认识 <c>DUTY</c>, 不再读</summary>
    public bool Unsupported => unsupported;

    /// <summary>已记的警告条数（找不到游戏数据、读数不合理、标志全为 0 各最多一条）</summary>
    public int Warnings { get; private set; }

    /// <summary>
    ///     读一次; 读到值就报。通话失败照常抛异常（这一轮算读不到）, 调用方事后要放开管道
    /// </summary>
    public async Task<CatModuleRead> ReadAsync(IMiniModuleChannel module, CancellationToken cancellationToken)
    {
        if (unsupported)
            return CatModuleRead.Unsupported;

        if (await CatLevelReader.SendWithTimeoutAsync(module, COMMAND, timeout, cancellationToken).ConfigureAwait(false) is not { } response)
        {
            Log.Debug("[CatHost] 读副本状态 {Seconds} 秒没有回应, 跳过这一次", timeout.TotalSeconds);
            return CatModuleRead.TimedOut;
        }

        if (response.Contains("unknown-command", StringComparison.Ordinal))
        {
            unsupported = true;
            Log.Information("[CatHost] 游戏 {Pid} 里的游戏内模块是旧版, 读不了是否在副本中（下次启动游戏才会换上新模块）", processId);
            return CatModuleRead.Unsupported;
        }

        if (CatModuleReplies.ParseDuty(response) is not { } reading)
        {
            allClearRounds = 0;
            LogFailure(response);
            return CatModuleRead.Answered;
        }

        if (reading.AllClear)
        {
            if (++allClearRounds >= ALL_CLEAR_ROUNDS_BEFORE_WARNING && !warnedAllClear)
            {
                warnedAllClear = true;
                Warnings++;
                Log.Warning
                (
                    "[CatHost] 游戏 {Pid} 载入了角色, 副本状态的标志却连续 {Rounds} 次全为 0, 不发; 若一直如此要按新版客户端核对 Conditions 地址",
                    processId,
                    allClearRounds
                );
            }

            return CatModuleRead.Answered;
        }

        allClearRounds = 0;
        readThisRound  = true;
        tracker.Read(reading.InDuty, DateTimeOffset.UtcNow);
        return CatModuleRead.Answered;
    }

    /// <summary>
    ///     一轮观察结束（不论这一轮读没读 DUTY、为什么没读）: 这一轮没读到值就记一次读不到。取消时不调
    /// </summary>
    public void EndRound()
    {
        if (!readThisRound)
            tracker.Missed(DateTimeOffset.UtcNow);

        readThisRound = false;
    }

    /// <summary>没读成: 找不到游戏数据（sigscan-failed）与读数不合理（bad-state）第一次出现时记警告, 其余只记调试日志</summary>
    private void LogFailure(string response)
    {
        var line = CatModuleReplies.FirstLine(response);

        if (line.StartsWith("FAIL sigscan-failed", StringComparison.Ordinal) && !warnedSigscan)
        {
            warnedSigscan = true;
            Warnings++;
            Log.Warning("[CatHost] 游戏 {Pid} 的游戏内模块找不到读副本状态要用的游戏数据（{Response}）, 读不了是否在副本中; 若一直如此要按新版客户端核对特征码", processId, line);
            return;
        }

        if (line.StartsWith("FAIL bad-state", StringComparison.Ordinal) && !warnedBadState)
        {
            warnedBadState = true;
            Warnings++;
            Log.Warning("[CatHost] 游戏 {Pid} 的副本状态读数不合理（{Response}）, 不发; 若一直如此要按新版客户端核对 Conditions 地址", processId, line);
            return;
        }

        if (!CatModuleReplies.IsOk(response))
            Log.Debug("[CatHost] 这一次没读成副本状态: {Response}", line);
    }
}

/// <summary>
///     不自动进入角色的盛趣 / WeGame 游戏（含接管来的）: 等游戏窗口、注入游戏内模块（已在就跳过; 失败隔
///     <see cref="CatAutoEnterTimings.AttachRetryInterval" /> 再试, 最多 <see cref="CatAutoEnterTimings.AttachAttempts" /> 次）, 之后每隔
///     <see cref="CatAutoEnterTimings.ObserveInterval" /> 先看当前角色（WHOAMI, 角色变了报 game.character）再读全部职业等级与是否在副本中。
///     自动进入角色的游戏在 <see cref="CatAutoEnter.ObserveAsync" /> 里做同样的事。管道被别的操作（换大区等）占着时跳过这一次; 游戏退出或取消即结束
/// </summary>
internal sealed class CatLevelWatch(ICatAutoEnterGame game, ICatLaunchReporter reporter, CatAutoEnterTimings? timings = null)
{
    private readonly CatAutoEnterTimings timings = timings ?? new CatAutoEnterTimings();

    private IReadOnlyList<CatAutoEnterWorld>? worlds;
    private string?                           reportedContentId;

    /// <summary>
    ///     注入并观察到游戏退出; 注入始终失败只记日志（并告诉工作台一声）
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (!await AttachAsync(cancellationToken).ConfigureAwait(false))
            return;

        // 服务器中文名表要联网查, 在占模块闸之前查好（查不到只是 game.character 不带中文名）
        worlds = await game.LoadWorldsAsync(cancellationToken).ConfigureAwait(false);

        var reader     = new CatLevelReader(reporter, game.ProcessId, timings.ReadTimeout);
        var dutyReader = new CatDutyReader(reporter, game.ProcessId, timings.ReadTimeout);

        while (!cancellationToken.IsCancellationRequested && !game.HasExited)
        {
            await game.DelayAsync(timings.ObserveInterval, cancellationToken).ConfigureAwait(false);

            if (game.HasExited)
                return;

            if (game.ModuleBusy)
                continue;

            try
            {
                var response = await CatLevelReader.SendWithTimeoutAsync(game, "WHOAMI", timings.ReadTimeout, cancellationToken).ConfigureAwait(false);

                // 比 0.6.0 还旧的模块没有 WHOAMI: 什么也读不了
                if (response != null && response.Contains("unknown-command", StringComparison.Ordinal))
                {
                    Log.Information("[CatHost] 游戏 {Pid} 里的游戏内模块太旧, 看不了当前角色", game.ProcessId);
                    return;
                }

                if (response == null || CatModuleReplies.ParseWhoAmI(response) is not { Loaded: true } who)
                    continue;

                if (who.ContentId != reportedContentId)
                    ReportCharacter(who);

                // 读等级超时了, 这一轮就不再读副本状态
                if (await reader.ReadAsync(game, who, cancellationToken).ConfigureAwait(false) != CatModuleRead.TimedOut)
                    await dutyReader.ReadAsync(game, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[CatHost] 这一次没看成当前角色和职业等级");
            }
            finally
            {
                game.Release();

                // 这一轮没读到副本状态（没载入角色、读失败、超时）记一次, 读到过值之后接连两轮读不到就报"未知"
                if (!cancellationToken.IsCancellationRequested)
                    dutyReader.EndRound();
            }
        }
    }

    /// <summary>注入模块, 失败隔一段时间再试; 返回是否注上了（游戏退出也算没注上）</summary>
    private async Task<bool> AttachAsync(CancellationToken cancellationToken)
    {
        string? error = null;

        for (var attempt = 1; attempt <= Math.Max(1, timings.AttachAttempts); attempt++)
        {
            if (attempt > 1)
                await game.DelayAsync(timings.AttachRetryInterval, cancellationToken).ConfigureAwait(false);

            if (game.HasExited)
                return false;

            error = await game.AttachAsync(cancellationToken).ConfigureAwait(false);

            if (error == null)
                return true;

            if (game.HasExited)
                return false;

            Log.Warning("[CatHost] 游戏内模块第 {Attempt} 次注入没成: {Error}", attempt, error);
        }

        reporter.Log("warning", $"游戏内模块没有注入, 读不到当前角色和职业等级: {error}");
        return false;
    }

    /// <summary>
    ///     当前角色变了（第一次读到也算）: 报 game.character。世界以模块读到的 Id 为准, 原始服务器的代号只在没有超域时能确定
    ///     （模块读到的 homeName 实际是当前所在的服务器, 见 <see cref="CatAutoEnter" /> 的同类处理）
    /// </summary>
    private void ReportCharacter(CatModuleReplies.WhoAmI who)
    {
        var current = who.WorldCode;
        var home    = who.HomeWorldId != 0 && who.HomeWorldId == who.WorldId ? current : string.Empty;

        reportedContentId = who.ContentId;
        Log.Information("[CatHost] 游戏 {Pid} 当前角色: {Name} cid={ContentId} 原始={Home} 现在={Current}", game.ProcessId, who.Name, who.ContentId, home, current);

        reporter.Character
        (
            new CatCharacterInfo
            (
                who.ContentId,
                who.Name,
                home,
                current,
                WorldName(home),
                WorldName(current),
                who.HomeWorldId != 0 && who.WorldId != 0 && who.HomeWorldId != who.WorldId,
                true
            )
        );
    }

    private string? WorldName(string code) =>
        string.IsNullOrEmpty(code) ? null : worlds?.FirstOrDefault(x => string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase))?.Name;
}

/// <summary>
///     职业表里登录器用得着的事实（国服客户端）
/// </summary>
public static class CatClassJobs
{
    /// <summary>
    ///     ClassJob 表行号 → ExpArrayIndex（国服客户端 ClassJob 表, 2026-10-16 用 Dalamud 自带的 Lumina 从本机 sqpack 读出）。
    ///     冒险者（0）不在表里; 驯兽师 ClassJob 43 → 32
    /// </summary>
    public static readonly IReadOnlyDictionary<int, int> ExpArrayIndex = new Dictionary<int, int>
    {
        [1] = 1, [2] = 0, [3] = 2, [4] = 4, [5] = 3, [6] = 6, [7] = 5,
        [8] = 7, [9] = 8, [10] = 9, [11] = 10, [12] = 11, [13] = 12, [14] = 13, [15] = 14,
        [16] = 15, [17] = 16, [18] = 17,
        [19] = 1, [20] = 0, [21] = 2, [22] = 4, [23] = 3, [24] = 6, [25] = 5,
        [26] = 18, [27] = 18, [28] = 18, [29] = 19, [30] = 19, [31] = 20, [32] = 21, [33] = 22,
        [34] = 23, [35] = 24, [36] = 25, [37] = 26, [38] = 27, [39] = 28, [40] = 29, [41] = 30, [42] = 31,
        [43] = 32
    };
}

internal static partial class CatModuleReplies
{
    /// <summary>
    ///     LEVELS 读到的内容: Levels = 各职业等级（下标为 ExpArrayIndex）; ClassJob / Level = 当前职业与等级（旧回应没有时为 null）; Synced = 等级同步中
    /// </summary>
    public sealed record LevelsReading(int[] Levels, int? ClassJob, int? Level, bool Synced)
    {
        /// <summary>
        ///     当前职业那一格是否等于当前等级。等级同步中、没带当前职业、或职业不在表里时不比, 算对得上
        /// </summary>
        public bool Consistent =>
            Synced ||
            ClassJob is not { } job ||
            Level is not { } level ||
            !CatClassJobs.ExpArrayIndex.TryGetValue(job, out var index) ||
            index >= Levels.Length ||
            Levels[index] == level;
    }

    /// <summary>
    ///     LEVELS 的回应（<c>OK loaded=1 job=31 level=100 synced=0 n=35 levels=100,80,…</c>）: 在世界里时返回读到的内容;
    ///     不在世界里（loaded=0）、全为 0、回应不是 OK、或格式对不上返回 null
    /// </summary>
    public static LevelsReading? ParseLevels(string response)
    {
        if (!IsOk(response))
            return null;

        var fields = ParseFields(FirstLine(response));

        if (!Flag(fields, "loaded") || !fields.TryGetValue("levels", out var text) || text.Length == 0)
            return null;

        var parts  = text.Split(',');
        var levels = new int[parts.Length];

        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out levels[i]))
                return null;
        }

        if (fields.TryGetValue("n", out var count) && count != parts.Length.ToString(CultureInfo.InvariantCulture))
            return null;

        if (levels.All(x => x == 0))
            return null;

        return new LevelsReading(levels, OptionalInt(fields, "job"), OptionalInt(fields, "level"), Flag(fields, "synced"));
    }

    /// <summary>
    ///     DUTY 读到的内容: InDuty = 是否在副本中; AllClear = 全部 112 个标志都为 0（载入了角色时不该如此, 多半读歪了）
    /// </summary>
    public sealed record DutyReading(bool InDuty, bool AllClear);

    /// <summary>
    ///     DUTY 的回应（<c>OK loaded=1 inDuty=1 set=5 flags=34,56</c>, set = 112 个标志里置位的个数）: 在世界里时返回读到的内容;
    ///     没有载入角色（loaded=0）、回应不是 OK、或没带 inDuty（0 / 1 以外）返回 null = 不知道。没带 set 时不当作全为 0
    /// </summary>
    public static DutyReading? ParseDuty(string response)
    {
        if (!IsOk(response))
            return null;

        var fields = ParseFields(FirstLine(response));

        if (!Flag(fields, "loaded") || !fields.TryGetValue("inDuty", out var value))
            return null;

        bool? inDuty = value switch
        {
            "1" => true,
            "0" => false,
            _   => null
        };

        return inDuty is { } result ? new DutyReading(result, OptionalInt(fields, "set") == 0) : null;
    }

    private static int? OptionalInt(Dictionary<string, string> fields, string key) =>
        fields.TryGetValue(key, out var value) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
}
