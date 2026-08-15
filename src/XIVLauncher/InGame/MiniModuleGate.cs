using System.Diagnostics;
using Serilog;

namespace XIVLauncher.InGame;

/// <summary>
///     F4 的生死闸自检：注进去 → 通上话 → 在游戏主线程上跑一次代码。
///     这三件事是整个 F4（游戏内跨大区）唯一还没被实测验证过的工程风险；换服逻辑本身
///     DcTraveler 早已证明可行, 但那是站在 Dalamud 肩上, 我们必须自己先把这三步走通。
///     ⚠ 判据纪律: 一律看模块自己写的日志和管道回包里的线程 id, 不认「没报错就算过」。
/// </summary>
public static class MiniModuleGate
{
    private const int PIPE_CONNECT_TIMEOUT_MS = 10_000;

    public static async Task RunAsync(Process gameProcess, CancellationToken cancellationToken)
    {
        var error = MiniModuleInjector.Inject(gameProcess);

        if (error != null)
        {
            Log.Error("[MiniModule] 注入失败: {Error}", error);
            return;
        }

        try
        {
            using var client = new MiniModuleClient(gameProcess.Id);
            await client.ConnectAsync(PIPE_CONNECT_TIMEOUT_MS, cancellationToken).ConfigureAwait(false);

            var version = await client.SendAsync("VERSION", cancellationToken).ConfigureAwait(false);
            Log.Information("[MiniModule] {Response}", version);

            var ping = await client.SendAsync("PING", cancellationToken).ConfigureAwait(false);

            if (!ping.StartsWith("OK", StringComparison.Ordinal))
            {
                Log.Error("[MiniModule] 生死闸不通过: PING 回了 {Response}", ping);
                return;
            }

            // 真正的闸: 让模块把一段代码派到游戏窗口线程上执行, 并回报它在哪个线程上跑的。
            // same=1 表示那就是进程主线程 —— 换服那几个函数正是必须在这个线程上调。
            var mainThread = await client.SendAsync("MAINTHREAD", cancellationToken).ConfigureAwait(false);

            if (mainThread.Contains("same=1", StringComparison.Ordinal))
                Log.Information("[MiniModule] 生死闸通过: 已在游戏主线程上执行 ({Response})", mainThread);
            else
                Log.Warning("[MiniModule] 能执行但不是主线程, 换服那步不能直接用这个通道 ({Response})", mainThread);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[MiniModule] 与模块通话失败（模块已注入, 但管道没走通）");
        }
    }
}
