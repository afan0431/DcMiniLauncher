using System.Collections.Concurrent;
using System.Diagnostics;
using Serilog;
using XIVLauncher.Login.Models;

namespace XIVLauncher.InGame;

/// <summary>
///     记录本启动器起过、且还活着的游戏客户端（F4 用）。
///     游戏内换大区是从外部（bot 的 HTTP 请求 / 启动器界面）触发的, 那些地方拿不到启动流程里的
///     <see cref="Process" />, 所以在这里登记一份。按 PID 存, 多开时各是各的。
/// </summary>
public static class RunningGameRegistry
{
    public sealed record Entry(Process Process, InGameAgents Agents);

    private static readonly ConcurrentDictionary<int, Entry> ENTRIES = new();

    public static void Register(Process process, InGameAgents agents)
    {
        ENTRIES[process.Id] = new Entry(process, agents);
        Log.Debug("[RunningGame] 登记 PID={Pid} agents={Agents}", process.Id, agents);
    }

    public static void Unregister(int processId)
    {
        if (ENTRIES.TryRemove(processId, out _))
            Log.Debug("[RunningGame] 注销 PID={Pid}", processId);
    }

    /// <summary>
    ///     找出要操作的客户端。<paramref name="processId" /> 给了就用它, 没给就要求当前只有一个活着的客户端 ——
    ///     多开时不允许猜, 猜错就是把别人的角色换了大区。
    /// </summary>
    public static Entry? Resolve(int? processId, out string? error)
    {
        Prune();

        if (processId is { } pid)
        {
            if (ENTRIES.TryGetValue(pid, out var wanted))
            {
                error = null;
                return wanted;
            }

            error = $"PID {pid} 不是本启动器起的游戏客户端（或它已经退出了）";
            return null;
        }

        var alive = ENTRIES.Values.ToArray();

        switch (alive.Length)
        {
            case 0:
                error = "当前没有本启动器起着的游戏客户端";
                return null;

            case 1:
                error = null;
                return alive[0];

            default:
                error = $"当前开着 {alive.Length} 个客户端（PID: {string.Join(", ", alive.Select(x => x.Process.Id))}）, 请指定 pid";
                return null;
        }
    }

    private static void Prune()
    {
        foreach (var (pid, entry) in ENTRIES)
        {
            try
            {
                if (entry.Process.HasExited)
                    Unregister(pid);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[RunningGame] 检查 PID={Pid} 是否退出失败, 当作已退出", pid);
                Unregister(pid);
            }
        }
    }
}
