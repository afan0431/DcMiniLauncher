using System.Globalization;
using Serilog;
using XIVLauncher.InGame;

namespace XIVLauncher.CatHost;

/// <summary>
///     向游戏内模块读一次全部职业等级（<c>LEVELS</c>）并报告; 报不报 game.levels 由 <see cref="CatJobChannel" /> 按有没有变化决定。
///     游戏里注着的是不认识这条命令的旧模块（0.8.0 之前）时记一次日志, 之后不再读
/// </summary>
internal sealed class CatLevelReader(ICatLaunchReporter reporter, int processId)
{
    /// <summary>模块命令</summary>
    public const string COMMAND = "LEVELS";

    private bool unsupported;

    /// <summary>模块不认识 <c>LEVELS</c>, 不再读</summary>
    public bool Unsupported => unsupported;

    /// <summary>
    ///     读一次。模块不支持时返回 false（之后调用直接返回 false）; 通话失败照常抛异常, 由调用方决定跳过这一次
    /// </summary>
    public async Task<bool> ReadAsync(IMiniModuleChannel module, CancellationToken cancellationToken)
    {
        if (unsupported)
            return false;

        var response = await module.SendAsync(COMMAND, cancellationToken).ConfigureAwait(false);

        if (response.Contains("unknown-command", StringComparison.Ordinal))
        {
            unsupported = true;
            Log.Information("[CatHost] 游戏 {Pid} 里的游戏内模块是旧版, 读不了职业等级（下次启动游戏才会换上新模块）", processId);
            return false;
        }

        if (CatModuleReplies.ParseLevels(response) is { } levels)
            reporter.Levels(processId, levels);
        else if (!CatModuleReplies.IsOk(response))
            Log.Debug("[CatHost] 这一次没读成职业等级: {Response}", CatModuleReplies.FirstLine(response));

        return true;
    }
}

/// <summary>
///     不自动进入角色的盛趣 / WeGame 游戏（含接管来的）: 等游戏窗口、注入游戏内模块（已在就跳过）, 之后每隔
///     <see cref="CatAutoEnterTimings.ObserveInterval" /> 读一次全部职业等级。自动进入角色的游戏在 <see cref="CatAutoEnter.ObserveAsync" /> 里顺带读。
///     管道被别的操作（换大区等）占着时跳过这一次; 游戏退出或取消即结束
/// </summary>
internal sealed class CatLevelWatch(ICatAutoEnterGame game, ICatLaunchReporter reporter, CatAutoEnterTimings? timings = null)
{
    private readonly CatAutoEnterTimings timings = timings ?? new CatAutoEnterTimings();

    /// <summary>
    ///     注入并读到游戏退出; 注入失败只记日志（并告诉工作台一声）
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var error = await game.AttachAsync(cancellationToken).ConfigureAwait(false);

        if (error != null)
        {
            if (!game.HasExited)
            {
                Log.Warning("[CatHost] 读职业等级要用的游戏内模块没有注入: {Error}", error);
                reporter.Log("warning", $"游戏内模块没有注入, 读不到职业等级: {error}");
            }

            return;
        }

        var reader = new CatLevelReader(reporter, game.ProcessId);

        while (!cancellationToken.IsCancellationRequested && !game.HasExited)
        {
            await game.DelayAsync(timings.ObserveInterval, cancellationToken).ConfigureAwait(false);

            if (game.HasExited)
                return;

            if (game.ModuleBusy)
                continue;

            try
            {
                if (!await reader.ReadAsync(game, cancellationToken).ConfigureAwait(false))
                    return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[CatHost] 这一次没读成职业等级");
            }
            finally
            {
                game.Release();
            }
        }
    }
}

internal static partial class CatModuleReplies
{
    /// <summary>
    ///     LEVELS 的回应（<c>OK loaded=1 n=35 levels=100,80,…</c>）: 在世界里时返回各职业等级（下标为 ExpArrayIndex）;
    ///     不在世界里（loaded=0）、回应不是 OK、或格式对不上返回 null
    /// </summary>
    public static int[]? ParseLevels(string response)
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

        return levels;
    }
}
