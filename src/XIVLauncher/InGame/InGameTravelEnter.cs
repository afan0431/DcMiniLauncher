using Serilog;
using XIVLauncher.CatHost;

namespace XIVLauncher.InGame;

/// <summary>
///     请求带 enter 时跨区落定后的那一步: 游戏一侧与要登录的角色。游戏一侧在跨区跑完后才建（要用落点大区）
/// </summary>
/// <param name="CreateGameAsync">建游戏一侧; 实现了 <see cref="IDisposable" /> 的用完释放</param>
/// <param name="Target">要登录的角色</param>
/// <param name="Timings">自动进入角色的等待上限, 不给用默认值</param>
public sealed record InGameTravelEnterPlan
(
    Func<Task<ICatAutoEnterGame>> CreateGameAsync,
    CatAutoEnterTarget            Target,
    CatAutoEnterTimings?          Timings = null
);

/// <summary>
///     一张游戏内跨区单从跑各段到落定的收尾, 也是这一单唯一结束（<see cref="InGameTravelJobs.End" />）的地方:
///     跨区没成就报失败; 成了且带 enter 时, 这一单保持进行中, 对同一游戏跑一次 <see cref="CatAutoEnter" />（不等人选）, 角色进了世界才报成功,
///     进不去按它的停手原因报失败。跨区各段在返回前已经放开管道闸, 自动进入角色再自己去占。
/// </summary>
public static class InGameTravelEnter
{
    /// <summary>自动进入角色途中游戏退出了（只出现在 <see cref="InGameTravelStatus.StopCode" />）</summary>
    public const string GAME_EXITED = "gameExited";

    /// <summary>
    ///     跑完一张单
    /// </summary>
    /// <param name="pid">游戏进程号（这一单已经 <see cref="InGameTravelJobs.TryBegin" />）</param>
    /// <param name="travelAsync">跑跨区各段, 自己不结束这一单</param>
    /// <param name="enter">带 enter 时进世界那一步; null = 停在选角界面</param>
    public static async Task<InGameTravelResult> RunAsync(int pid, Func<Task<InGameTravelResult>> travelAsync, InGameTravelEnterPlan? enter)
    {
        InGameTravelResult result;

        try
        {
            result = await travelAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[InGameTravel] 换大区任务异常");
            result = InGameTravelResult.Failed(ex.Message);
        }

        if (!result.Ok || enter == null)
        {
            InGameTravelJobs.End(pid, result.Ok, result.Message);
            return result;
        }

        InGameTravelJobs.EnterStep(pid, $"{result.Message}, 正在进入游戏…");

        var (ok, message, stopCode) = await EnterAsync(pid, enter, result.Message).ConfigureAwait(false);

        InGameTravelJobs.End(pid, ok, message, stopCode);
        return new InGameTravelResult(ok, message);
    }

    private static async Task<(bool Ok, string Message, string StopCode)> EnterAsync(int pid, InGameTravelEnterPlan plan, string travelled)
    {
        ICatAutoEnterGame? game = null;

        try
        {
            game = await plan.CreateGameAsync().ConfigureAwait(false);

            var reporter = new StatusReporter(pid);
            var flow     = new CatAutoEnter(game, reporter, plan.Target, plan.Timings) { StopWhenChoiceNeeded = true };
            var outcome  = await flow.RunAsync(CancellationToken.None).ConfigureAwait(false);

            switch (outcome)
            {
                case CatAutoEnterOutcome.InWorld:
                    Log.Information("[InGameTravel] 跨区后已进入游戏: {Name} (PID={Pid})", reporter.EnteredName, pid);
                    return (true, $"{travelled}, {reporter.EnteredName ?? plan.Target.Name} 已进入游戏", string.Empty);

                case CatAutoEnterOutcome.Stopped:
                    Log.Warning("[InGameTravel] 跨区后自动进入角色停手: {Code} {Message}", reporter.StopCode, reporter.StopMessage);
                    return (false, reporter.StopMessage ?? "自动进入角色没有做完", reporter.StopCode ?? CatAutoEnterStopCodes.MODULE_UNAVAILABLE);

                default:
                    return reporter.StopCode != null
                               ? (false, reporter.StopMessage ?? "自动进入角色没有做完", reporter.StopCode)
                               : (false, "自动进入角色途中游戏退出了", GAME_EXITED);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[InGameTravel] 跨区后自动进入角色出错");
            return (false, $"自动进入角色时出错: {ex.Message}", CatAutoEnterStopCodes.MODULE_UNAVAILABLE);
        }
        finally
        {
            (game as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    ///     自动进入角色的报告只落进这一单的状态（阶段 → Phase, 停手原因 → StopCode / Message）, 不发给 Cat 工作台
    /// </summary>
    private sealed class StatusReporter(int pid) : ICatLaunchReporter
    {
        public string? StopCode { get; private set; }

        public string? StopMessage { get; private set; }

        public string? EnteredName { get; private set; }

        public void Stage(string stage)
        {
            var text = stage switch
            {
                CatStages.ENTERING_LOBBY => "正在进入角色选择界面…",
                CatStages.SWITCHING_AREA => "正在换到角色所在大区的大厅…",
                CatStages.ENTERING_WORLD => "正在登录角色…",
                CatStages.QUEUEING       => "登录排队中…",
                CatStages.IN_WORLD       => "已进入游戏",
                _                        => null
            };

            if (text != null)
                InGameTravelJobs.Report(pid, text);
        }

        public void Queueing(int? queuePosition) =>
            InGameTravelJobs.Report(pid, queuePosition is { } position ? $"登录排队中（第 {position} 位）…" : "登录排队中…");

        public void Character(CatCharacterInfo character) => EnteredName = character.Name;

        public void AutoEnterStopped(string code, string message)
        {
            StopCode    = code;
            StopMessage = message;
        }

        public void Started(int pid, DateTimeOffset processStartedAt)
        {
        }

        public void Restarted(int oldPid, int pid, DateTimeOffset processStartedAt)
        {
        }

        public void Crashed(int pid)
        {
        }

        public void Agent(string kind, bool ok, string? code = null, string? message = null)
        {
        }

        public void Exited(int pid, int? exitCode, string? reason = null, string? code = null, string? message = null)
        {
        }

        public void Failed(string code, string message)
        {
        }

        public void Log(string level, string message)
        {
        }

        public void WeGameChallenge(CatWeGameChallenge challenge)
        {
        }

        public void WeGameChallengeCleared(string challengeId)
        {
        }

        public void WeGameScanSwitchFailed(string scan)
        {
        }

        public void WeGameSmsResult(string challengeId, bool passed)
        {
        }
    }
}
