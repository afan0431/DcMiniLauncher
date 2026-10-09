using System.Collections.Concurrent;

namespace XIVLauncher.InGame;

/// <summary>
///     一次游戏内换大区的进行状态。游戏内 UI（Afan/Minion 的 Lua 窗口）靠轮询这个来显示进度 ——
///     整个流程要几十秒到几分钟（排队 + 冷却 + 重试），HTTP 不可能一直挂着等。
/// </summary>
public sealed class InGameTravelStatus
{
    public int    Pid       { get; init; }
    public bool   Running   { get; set; }
    public bool   Ok        { get; set; }
    public string Phase     { get; set; } = "";
    public string Target    { get; set; } = "";
    public string Message   { get; set; } = "";
    public string StartedAt { get; set; } = "";
    public string EndedAt   { get; set; } = "";
}

/// <summary>按 PID 存每个客户端最近一次换大区的状态。多开时各是各的。</summary>
public static class InGameTravelJobs
{
    private static readonly ConcurrentDictionary<int, InGameTravelStatus> JOBS = new();

    /// <summary>开始一次换大区与交接停止互斥: 两边都在这把锁里查、改</summary>
    private static readonly object Gate = new();

    /// <summary>守护进程正在交接停止的客户端: 不再接新的换大区（进程马上退出, 做到一半会被拦腰截断）</summary>
    private static readonly HashSet<int> HELD = [];

    /// <summary>
    ///     交接停止: 这个客户端不再接新的换大区。已经有一次在进行时不挡, 返回 false（交接要等它结束）
    /// </summary>
    public static bool TryHold(int pid)
    {
        lock (Gate)
        {
            if (IsRunning(pid))
                return false;

            HELD.Add(pid);
            return true;
        }
    }

    /// <summary>交接停止没成: 这个客户端照常接换大区</summary>
    public static void Release(int pid)
    {
        lock (Gate)
            HELD.Remove(pid);
    }

    /// <summary>
    ///     开始一次换大区; 已有一次在进行、或守护进程正在交接停止时返回 null
    /// </summary>
    public static InGameTravelStatus? TryBegin(int pid, string target)
    {
        lock (Gate)
            return HELD.Contains(pid) || IsRunning(pid) ? null : Begin(pid, target);
    }

    public static InGameTravelStatus Begin(int pid, string target)
    {
        var status = new InGameTravelStatus
        {
            Pid       = pid,
            Running   = true,
            Target    = target,
            Phase     = "准备中",
            Message   = "准备中",
            StartedAt = DateTimeOffset.Now.ToString("HH:mm:ss")
        };

        JOBS[pid] = status;
        return status;
    }

    public static void Report(int pid, string message)
    {
        if (!JOBS.TryGetValue(pid, out var status))
            return;

        status.Phase   = message;
        status.Message = message;
    }

    public static void End(int pid, bool ok, string message)
    {
        if (!JOBS.TryGetValue(pid, out var status))
            return;

        status.Running = false;
        status.Ok      = ok;
        status.Phase   = ok ? "已完成" : "已失败";
        status.Message = message;
        status.EndedAt = DateTimeOffset.Now.ToString("HH:mm:ss");
    }

    public static bool IsRunning(int pid) => JOBS.TryGetValue(pid, out var status) && status.Running;

    public static InGameTravelStatus? Get(int pid) => JOBS.GetValueOrDefault(pid);

    public static InGameTravelStatus[] All() => JOBS.Values.ToArray();
}
