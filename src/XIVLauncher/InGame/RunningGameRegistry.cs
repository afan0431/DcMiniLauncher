using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
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

    /// <summary>
    ///     游戏内 UI（Afan/Minion 的 Lua）要知道往哪个端口发请求。插件是从游戏参数
    ///     <c>XL.DcTraveler</c> 读的, 但 Minion 的 Lua 读不到游戏参数 —— 所以按 PID 落一个小文件,
    ///     Lua 用 <c>io.open</c> 就能读到。多开时各是各的。
    ///     <para>
    ///         为什么放 ProgramData 而不是 <c>%TEMP%</c>: Minion 的 Lua 沙箱没有 <c>os.getenv</c>,
    ///         读不到任何环境变量, 所以两边只能约定一个不依赖环境变量的固定路径。
    ///     </para>
    /// </summary>
    public static string PortFilePath(int processId) =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "DcMiniLauncher",
            $"dctravel-{processId}.port");

    public static void Register(Process process, InGameAgents agents, int dcTravelPort = 0)
    {
        ENTRIES[process.Id] = new Entry(process, agents);
        Log.Debug("[RunningGame] 登记 PID={Pid} agents={Agents}", process.Id, agents);

        if (dcTravelPort <= 0)
            return;

        try
        {
            var portFile = PortFilePath(process.Id);
            Directory.CreateDirectory(Path.GetDirectoryName(portFile)!);
            File.WriteAllText(portFile, dcTravelPort.ToString());
            Log.Debug("[RunningGame] 端口文件已写: {Path} = {Port}", portFile, dcTravelPort);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[RunningGame] 写端口文件失败, 游戏内 UI 将找不到启动器");
        }
    }

    public static void Unregister(int processId)
    {
        if (ENTRIES.TryRemove(processId, out _))
            Log.Debug("[RunningGame] 注销 PID={Pid}", processId);

        try
        {
            var path = PortFilePath(processId);

            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[RunningGame] 删端口文件失败 PID={Pid}", processId);
        }
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
            if (!ENTRIES.ContainsKey(pid))
                AdoptOrphans();

            if (ENTRIES.TryGetValue(pid, out var wanted))
            {
                error = null;
                return wanted;
            }

            error = $"PID {pid} 不是本启动器起的游戏客户端（或它已经退出了）";
            return null;
        }

        AdoptOrphans();

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

    /// <summary>
    ///     认领「本启动器上一次运行起的、现在还活着的」客户端。
    ///     登记表在内存里, 启动器一重启就空了 —— 而游戏还开着、模块还注在里面, 那种时候
    ///     游戏内换大区本该照样能用（无人值守场景下启动器被重启是常态）。
    ///     判据是模块的命名管道还在: <c>\\.\pipe\minilauncher-&lt;pid&gt;</c> 存在 = 那个进程里有我们的模块。
    /// </summary>
    private static void AdoptOrphans()
    {
        string[] pipes;

        try
        {
            pipes = Directory.GetFiles(@"\\.\pipe\", "minilauncher-*");
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[RunningGame] 枚举命名管道失败");
            return;
        }

        foreach (var process in Process.GetProcessesByName("ffxiv_dx11"))
        {
            if (ENTRIES.ContainsKey(process.Id))
                continue;

            if (!pipes.Any(x => x.EndsWith($"minilauncher-{process.Id}", StringComparison.Ordinal)))
                continue;

            // 认领来的客户端不知道当初以什么模式起的; 但管道在就说明模块在, 换服要的能力齐了
            ENTRIES[process.Id] = new Entry(process, InGameAgents.Minion);
            Log.Information("[RunningGame] 认领了上次留下的客户端 PID={Pid}（模块管道仍在）", process.Id);
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
