using System.Diagnostics;
using Serilog;
using XIVLauncher.InGame;

namespace XIVLauncher.CatHost;

/// <summary>请游戏登出后退出的结果</summary>
public enum CatGameExitOutcome
{
    /// <summary>请不动（模块不可用、旧版本、主线程无响应、出错）, 改发关闭消息</summary>
    Unavailable,

    /// <summary>角色在世界里, 游戏已开始登出并会自己退出</summary>
    Exiting,

    /// <summary>不在世界里（标题、选角、片头）, 没有要登出的角色, 可以直接结束进程</summary>
    NotInWorld
}

/// <summary>
///     下号时请游戏登出后退出, 代替会弹「确定要结束游戏吗？」的 WM_CLOSE
/// </summary>
public interface ICatGameExit
{
    /// <summary>
    ///     发起登出退出, 不等游戏退出。<paramref name="cancellationToken" /> 取消时尽快返回。
    /// </summary>
    /// <param name="game">游戏进程</param>
    /// <param name="cancellationToken">取消</param>
    Task<CatGameExitOutcome> RequestAsync(Process game, CancellationToken cancellationToken);
}

/// <summary>
///     经游戏内模块的 <c>EXIT</c> 命令退出（国服客户端）: 模块不在就先注入, 占着模块闸发命令。
/// </summary>
public sealed class CatModuleGameExit : ICatGameExit
{
    /// <summary>共用实例</summary>
    public static readonly CatModuleGameExit Instance = new();

    private static readonly TimeSpan InjectTimeout   = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan GateTimeout     = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan GatePoll        = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(6);

    private const int PIPE_CONNECT_TIMEOUT_MS = 2_000;

    private CatModuleGameExit()
    {
    }

    /// <inheritdoc />
    public async Task<CatGameExitOutcome> RequestAsync(Process game, CancellationToken cancellationToken)
    {
        if (!MiniModuleInjector.ModulePath.Exists)
            return CatGameExitOutcome.Unavailable;

        if (!MiniModuleInjector.IsInjected(game))
        {
            string? error;

            try
            {
                error = await Task.Run(() => MiniModuleInjector.Inject(game), CancellationToken.None)
                                  .WaitAsync(InjectTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                error = $"{InjectTimeout.TotalSeconds:F0} 秒内没有注入完成";
            }

            if (error != null)
            {
                Log.Warning("[CatHost] 下号时注入游戏内模块失败 PID={Pid}: {Error}", game.Id, error);
                return CatGameExitOutcome.Unavailable;
            }
        }

        using var gate = await EnterGateAsync(game.Id, cancellationToken).ConfigureAwait(false);

        if (gate == null)
        {
            Log.Warning("[CatHost] 游戏内模块正被别的操作占用 PID={Pid}, 下号改发关闭消息", game.Id);
            return CatGameExitOutcome.Unavailable;
        }

        using var module = new MiniModuleClient(game.Id);
        await module.ConnectAsync(PIPE_CONNECT_TIMEOUT_MS, cancellationToken).ConfigureAwait(false);

        using var response = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        response.CancelAfter(ResponseTimeout);

        var reply = await module.SendAsync("EXIT", response.Token).ConfigureAwait(false);
        var outcome = Parse(reply);

        if (outcome == CatGameExitOutcome.Unavailable)
            Log.Warning("[CatHost] 游戏内模块没有接下退出 PID={Pid}: {Reply}", game.Id, reply);

        return outcome;
    }

    /// <summary>模块对 <c>EXIT</c> 的回应; 旧模块回 <c>FAIL unknown-command</c></summary>
    public static CatGameExitOutcome Parse(string? reply) =>
        reply?.Trim() switch
        {
            "OK exiting"      => CatGameExitOutcome.Exiting,
            "OK not-in-world" => CatGameExitOutcome.NotInWorld,
            _                 => CatGameExitOutcome.Unavailable
        };

    /// <summary>关闭时自动选角色已被取消, 它占着的闸很快会放; 等不到就返回 null</summary>
    private static async Task<IDisposable?> EnterGateAsync(int pid, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();

        while (true)
        {
            if (!InGameTravelJobs.IsRunning(pid) && InGameTravelService.TryEnterGate(pid) is { } gate)
                return gate;

            if (clock.Elapsed >= GateTimeout)
                return null;

            await Task.Delay(GatePoll, cancellationToken).ConfigureAwait(false);
        }
    }
}
