using System.Diagnostics;
using System.Runtime.InteropServices;
using Serilog;
using XIVLauncher.Minion;

namespace XIVLauncher.CatHost;

/// <summary>
///     下号时关游戏: 先给游戏窗口发 WM_CLOSE 请它自己退出, 超时再结束进程
/// </summary>
public static class CatGameCloser
{
    private const uint WM_CLOSE = 0x0010;

    /// <summary>
    ///     进程号对应的进程是否还活着且是同一个进程（创建时间相差不到 2 秒; 不知道创建时间时只看进程号）
    /// </summary>
    public static bool IsAlive(int pid, DateTimeOffset? processStartedAt) =>
        MinionOccupancy.IsSameProcessAlive(pid, processStartedAt);

    /// <summary>
    ///     按进程号关游戏; 进程已不在或进程号已被复用时直接返回 true
    /// </summary>
    public static async Task<bool> CloseAsync(int pid, DateTimeOffset? processStartedAt, TimeSpan gracefulTimeout)
    {
        Process process;

        try
        {
            process = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            return true;
        }

        using (process)
        {
            if (processStartedAt is { } expected && !IsAlive(pid, expected))
                return true;

            return await CloseAsync(process, gracefulTimeout).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     请游戏自己关闭, 等 <paramref name="gracefulTimeout" />, 还没退出就结束进程。返回进程是否已退出。
    /// </summary>
    public static async Task<bool> CloseAsync(Process process, TimeSpan gracefulTimeout)
    {
        if (HasExited(process))
            return true;

        var pid     = process.Id;
        var windows = FindWindows(process);

        foreach (var window in windows)
        {
            if (!PostMessageW(window, WM_CLOSE, IntPtr.Zero, IntPtr.Zero))
                Log.Warning("[CatHost] 给游戏窗口发关闭消息失败 PID={Pid}, 错误码 {Error}", pid, Marshal.GetLastWin32Error());
        }

        Log.Information("[CatHost] 已请游戏自己关闭 PID={Pid}（窗口 {Count} 个）, 最多等 {Seconds}s", pid, windows.Count, gracefulTimeout.TotalSeconds);

        if (windows.Count > 0 && gracefulTimeout > TimeSpan.Zero)
        {
            using var timeout = new CancellationTokenSource(gracefulTimeout);

            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                Log.Information("[CatHost] 游戏已自己退出 PID={Pid}", pid);
                return true;
            }
            catch (OperationCanceledException)
            {
            }
        }

        try
        {
            if (!HasExited(process))
            {
                process.Kill();
                Log.Information("[CatHost] 游戏没有在时限内退出, 已结束进程 PID={Pid}", pid);
            }

            using var killWait = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(killWait.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[CatHost] 结束游戏进程失败 PID={Pid}", pid);
            return HasExited(process);
        }
    }

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    ///     主窗口; 没有可见主窗口（加载中、最小化到托盘）时取这个进程的全部顶层窗口
    /// </summary>
    private static List<IntPtr> FindWindows(Process process)
    {
        try
        {
            process.Refresh();

            if (process.MainWindowHandle != IntPtr.Zero)
                return [process.MainWindowHandle];
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[CatHost] 读取游戏主窗口失败");
        }

        var pid    = (uint)process.Id;
        var result = new List<IntPtr>();

        EnumWindows
        (
            (window, _) =>
            {
                if (GetWindowThreadProcessId(window, out var owner) != 0 && owner == pid)
                    result.Add(window);

                return true;
            },
            IntPtr.Zero
        );

        return result;
    }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
