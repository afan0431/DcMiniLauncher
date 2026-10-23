using System.Diagnostics;
using System.IO;
using Serilog;
using XIVLauncher.Common.Util;

namespace XIVLauncher.Minion;

/// <summary>
///     一次挂载的附加选项。全部留空时与原来一样: 用 <see cref="MinionInstall.InstallPath" /> 挂, 不报排队。
/// </summary>
public sealed record MinionAttachOptions
{
    /// <summary>挂载用的 Minion 目录（主目录或 <see cref="MinionClientDir" /> 组装的客户端目录）; null = <see cref="MinionInstall.InstallPath" /></summary>
    public string? InstallPath { get; init; }

    /// <summary>挂载用的目录里 TensorCore 是否开着; null = 不知道（如直接挂主目录）, 按 <see cref="MinionAttachQueueTimings.SharedInstallGrace" /> 放锁</summary>
    public bool? TensorCoreEnabled { get; init; }

    /// <summary>本机另有游戏占着挂载锁、开始排队时调用一次</summary>
    public Action? Queued { get; init; }

    /// <summary>排过队并拿到挂载锁、即将拉起 MinionLauncher 时调用一次（没排队就不调用）</summary>
    public Action? Dequeued { get; init; }
}

/// <summary>
///     挂载排队的各项时限。默认值见 design §2.4, 测试里换成短的
/// </summary>
public sealed record MinionAttachQueueTimings
{
    /// <summary>等挂载锁的上限; 超过就不加锁挂载并记日志</summary>
    public TimeSpan LockWait { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>单个游戏占锁的上限（从拿到锁算起）; 超过就放锁并记「挂载排队超时放锁」</summary>
    public TimeSpan HoldCap { get; init; } = TimeSpan.FromSeconds(240);

    /// <summary>目录里没开 TensorCore 时, MinionLauncher 退出后再占锁多久</summary>
    public TimeSpan NoTensorCoreGrace { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>认不出 TensorCore 第二段注入（挂主目录、或不知道 TensorCore 开没开）时, MinionLauncher 退出后再占锁多久</summary>
    public TimeSpan SharedInstallGrace { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>等 TensorCore 拉起第二个 MinionLauncher 的上限</summary>
    public TimeSpan SecondLauncherAppear { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>第二个 MinionLauncher 出现后等它退出的上限</summary>
    public TimeSpan SecondLauncherExit { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>查进程的间隔</summary>
    public TimeSpan Poll { get; init; } = TimeSpan.FromMilliseconds(500);
}

/// <summary>
///     本机挂 Minion 排队: 同一台电脑一次只放一个游戏走「拉 MinionLauncher → TensorCore 自己的第二次注入做完」这一段。
///     MinionLauncher 挂载时会给每个游戏进程的句柄取名, 两个挂载同时进行会打断另一个游戏的注入致其闪退（PC2 2026-10-18）。
///     锁是跨进程的命名互斥量, 无界面的 Cat 上号进程与界面版手动挂载共用; 持有进程被强杀时系统放弃它, 下一个照常拿到。
/// </summary>
public static class MinionAttachQueue
{
    /// <summary>挂载锁的名字（当前登录会话内）</summary>
    public const string MUTEX_NAME = @"Local\DcMiniLauncher-MinionAttach";

    /// <summary>MinionLauncher 的进程名</summary>
    internal const string LAUNCHER_PROCESS_NAME = "MinionLauncher_64";

    /// <summary>默认时限</summary>
    public static MinionAttachQueueTimings DefaultTimings { get; } = new();

    /// <summary>
    ///     排队拿挂载锁。锁空着就直接拿到; 被别的游戏占着时先调用 <paramref name="queued" />, 再等。
    ///     等待期间游戏退出或 <paramref name="cancellationToken" /> 取消时返回 null（不抛异常）。
    ///     锁打不开或等超时就返回一个不持锁的租约, 照常挂载。
    /// </summary>
    /// <param name="gameProcess">要挂载的游戏进程; 它退出时租约立即放锁</param>
    /// <param name="queued">开始排队时调用一次</param>
    /// <param name="cancellationToken">取消</param>
    public static Task<MinionAttachLease?> EnterAsync(Process gameProcess, Action? queued, CancellationToken cancellationToken) =>
        EnterAsync(gameProcess, queued, MUTEX_NAME, DefaultTimings, cancellationToken);

    /// <summary>
    ///     同 <see cref="EnterAsync(Process, Action?, CancellationToken)" />, 可指定锁名与时限（测试用）
    /// </summary>
    internal static async Task<MinionAttachLease?> EnterAsync
    (
        Process                  gameProcess,
        Action?                  queued,
        string                   mutexName,
        MinionAttachQueueTimings timings,
        CancellationToken        cancellationToken
    )
    {
        var pid = SafePid(gameProcess);

        CrossProcessMutex? mutex;

        try
        {
            mutex = await CrossProcessMutex.AcquireAsync(mutexName, TimeSpan.Zero).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            mutex = null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Minion] 打不开挂载锁 {Name}, 不排队直接挂载 PID={Pid}", mutexName, pid);
            return new MinionAttachLease(null, gameProcess, timings, false);
        }

        if (mutex != null)
            return new MinionAttachLease(mutex, gameProcess, timings, false);

        Log.Information("[Minion] 本机另有游戏正在挂载 Minion, 排队等待 PID={Pid}", pid);

        try
        {
            queued?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Minion] 上报排队失败");
        }

        var stopwatch = Stopwatch.StartNew();
        var acquire   = CrossProcessMutex.AcquireAsync(mutexName, timings.LockWait);

        using (var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            var exited   = WaitForExitQuietlyAsync(gameProcess, timings.Poll, stop.Token);
            var canceled = Task.Delay(Timeout.Infinite, stop.Token);
            var first    = await Task.WhenAny(acquire, exited, canceled).ConfigureAwait(false);
            stop.Cancel();

            if (first != acquire)
            {
                // 不再要这把锁; 之后真拿到了也要马上放掉, 否则它会一直被这个没人管的线程占着
                _ = acquire.ContinueWith
                (
                    task =>
                    {
                        if (task.IsCompletedSuccessfully)
                            task.Result.Dispose();
                        else
                            _ = task.Exception;
                    },
                    TaskScheduler.Default
                );

                Log.Information
                (
                    "[Minion] 排队等挂载锁时{Reason}, 不再挂载 PID={Pid}",
                    cancellationToken.IsCancellationRequested ? "已取消" : "游戏已退出",
                    pid
                );
                return null;
            }
        }

        try
        {
            mutex = await acquire.ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Log.Warning("[Minion] 等挂载锁超过 {Timeout:0} 分钟, 不加锁挂载 PID={Pid}", timings.LockWait.TotalMinutes, pid);
            return new MinionAttachLease(null, gameProcess, timings, true);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Minion] 等挂载锁失败, 不加锁挂载 PID={Pid}", pid);
            return new MinionAttachLease(null, gameProcess, timings, true);
        }

        Log.Information("[Minion] 排队 {Elapsed:0.0}s 后拿到挂载锁 PID={Pid}", stopwatch.Elapsed.TotalSeconds, pid);
        return new MinionAttachLease(mutex, gameProcess, timings, true);
    }

    /// <summary>
    ///     进程的映像路径是否在 <paramref name="directory" /> 下（不区分大小写）
    /// </summary>
    internal static bool IsUnder(string? imagePath, string directory)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
            return false;

        try
        {
            var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(imagePath).StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    ///     找一个映像在 <paramref name="installPath" /> 下、且不是 <paramref name="excludedPids" /> 里的 MinionLauncher 进程; 没有返回 null。
    ///     读不到路径（无权访问、进程刚退出）的跳过
    /// </summary>
    internal static Process? FindLauncherUnder(string installPath, IReadOnlyCollection<int> excludedPids)
    {
        Process[] candidates;

        try
        {
            candidates = Process.GetProcessesByName(LAUNCHER_PROCESS_NAME);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[Minion] 列 MinionLauncher 进程失败");
            return null;
        }

        Process? found = null;

        foreach (var candidate in candidates)
        {
            if (found == null && !excludedPids.Contains(candidate.Id) && IsUnder(TryGetImagePath(candidate), installPath))
            {
                found = candidate;
                continue;
            }

            candidate.Dispose();
        }

        return found;
    }

    private static string? TryGetImagePath(Process process)
    {
        try
        {
            return PlatformHelpers.GetProcessExecutablePath(process);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[Minion] 读进程 {Pid} 的映像路径失败", process.Id);
            return null;
        }
    }

    /// <summary>
    ///     等进程退出; 取消时正常返回（不抛异常）。拿不到等待句柄时改为按 <paramref name="poll" /> 轮询 HasExited
    /// </summary>
    internal static async Task WaitForExitQuietlyAsync(Process process, TimeSpan poll, CancellationToken cancellationToken)
    {
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[Minion] 拿不到进程的退出通知, 改为轮询");
        }

        try
        {
            while (!HasExited(process))
                await Task.Delay(poll, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch
        {
            return true;
        }
    }

    internal static int SafePid(Process process)
    {
        try
        {
            return process.Id;
        }
        catch
        {
            return 0;
        }
    }
}

/// <summary>
///     拿到的挂载锁。满足任一条件就放锁: 调 <see cref="Dispose" />、<see cref="ReleaseAfterAttachAsync" /> 判定这次注入做完、
///     占锁超过 <see cref="MinionAttachQueueTimings.HoldCap" />、游戏进程退出。放锁只发生一次。
/// </summary>
public sealed class MinionAttachLease : IDisposable
{
    private readonly object                   gate     = new();
    private readonly CancellationTokenSource  lifetime = new();
    private readonly TaskCompletionSource     released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Process                  gameProcess;
    private readonly MinionAttachQueueTimings timings;
    private readonly int                      pid;
    private          CrossProcessMutex?       mutex;
    private          bool                     isReleased;

    internal MinionAttachLease(CrossProcessMutex? mutex, Process gameProcess, MinionAttachQueueTimings timings, bool waited)
    {
        this.mutex       = mutex;
        this.gameProcess = gameProcess;
        this.timings     = timings;
        Waited           = waited;
        HoldsLock        = mutex != null;
        pid              = MinionAttachQueue.SafePid(gameProcess);

        if (mutex == null)
        {
            Release(null);
            return;
        }

        _ = WatchCapAsync();
        _ = WatchGameExitAsync();
    }

    /// <summary>拿锁前是否排过队</summary>
    public bool Waited { get; }

    /// <summary>是否真的持有挂载锁（锁打不开或等超时时不持有）</summary>
    public bool HoldsLock { get; }

    /// <summary>放锁后完成</summary>
    public Task Released => released.Task;

    /// <summary>放锁的原因, 未放锁时为 null</summary>
    public string? ReleaseReason { get; private set; }

    /// <summary>
    ///     MinionLauncher 已正常退出: 按目录与 TensorCore 情况等这次注入做完再放锁（design §2.4）。
    ///     不抛异常; 返回的任务在放锁后完成, 调用方一般不等它。
    /// </summary>
    /// <param name="installPath">这次挂载用的 Minion 目录</param>
    /// <param name="tensorCoreEnabled">目录里 TensorCore 是否开着; null = 不知道</param>
    /// <param name="ownLauncherPids">本次自己拉起的 MinionLauncher 进程号, 认第二个 MinionLauncher 时排除</param>
    public async Task ReleaseAfterAttachAsync(string installPath, bool? tensorCoreEnabled, IReadOnlyCollection<int> ownLauncherPids)
    {
        var token = lifetime.Token;

        try
        {
            if (tensorCoreEnabled == false)
            {
                await Task.Delay(timings.NoTensorCoreGrace, token).ConfigureAwait(false);
                Release("TensorCore 没开, 挂载完成");
                return;
            }

            if (tensorCoreEnabled == null || !MinionClientDir.IsClientDirPath(installPath))
            {
                Log.Information("[Minion] 认不出这个游戏的 TensorCore 注入, {Grace:0}s 后放挂载锁 PID={Pid}", timings.SharedInstallGrace.TotalSeconds, pid);
                await Task.Delay(timings.SharedInstallGrace, token).ConfigureAwait(false);
                Release("MinionLauncher 已退出, 按固定间隔放锁");
                return;
            }

            using var second = await WaitForSecondLauncherAsync(installPath, ownLauncherPids, token).ConfigureAwait(false);

            if (second == null)
            {
                Log.Warning("[Minion] {Timeout:0}s 内没见到 TensorCore 的 MinionLauncher, 放挂载锁 PID={Pid}", timings.SecondLauncherAppear.TotalSeconds, pid);
                Release("没等到 TensorCore 注入");
                return;
            }

            Log.Information("[Minion] TensorCore 已拉起 MinionLauncher PID={LauncherPid}, 等它退出（游戏 PID={Pid}）", MinionAttachQueue.SafePid(second), pid);

            using var exitTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            exitTimeout.CancelAfter(timings.SecondLauncherExit);

            try
            {
                await second.WaitForExitAsync(exitTimeout.Token).ConfigureAwait(false);
                Release("TensorCore 注入完成");
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                Log.Warning("[Minion] TensorCore 的 MinionLauncher {Timeout:0}s 没退出, 放挂载锁 PID={Pid}", timings.SecondLauncherExit.TotalSeconds, pid);
                Release("TensorCore 注入超时");
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // 已因别的原因放锁
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Minion] 判断 Minion 注入是否做完时出错, 放挂载锁 PID={Pid}", pid);
            Release("判断注入出错");
        }
    }

    /// <summary>立即放锁（挂载失败、取消）</summary>
    public void Dispose() => Release("挂载结束");

    private async Task<Process?> WaitForSecondLauncherAsync(string installPath, IReadOnlyCollection<int> ownLauncherPids, CancellationToken token)
    {
        var stopwatch = Stopwatch.StartNew();

        while (true)
        {
            if (MinionAttachQueue.FindLauncherUnder(installPath, ownLauncherPids) is { } found)
                return found;

            if (stopwatch.Elapsed >= timings.SecondLauncherAppear)
                return null;

            await Task.Delay(timings.Poll, token).ConfigureAwait(false);
        }
    }

    private async Task WatchCapAsync()
    {
        try
        {
            await Task.Delay(timings.HoldCap, lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        Log.Warning("[Minion] 挂载排队超时放锁: 占锁超过 {Cap:0}s PID={Pid}", timings.HoldCap.TotalSeconds, pid);
        Release("挂载排队超时放锁");
    }

    private async Task WatchGameExitAsync()
    {
        await MinionAttachQueue.WaitForExitQuietlyAsync(gameProcess, timings.Poll, lifetime.Token).ConfigureAwait(false);

        if (!lifetime.IsCancellationRequested)
            Release("游戏已退出");
    }

    private void Release(string? reason)
    {
        CrossProcessMutex? held;

        lock (gate)
        {
            if (isReleased)
                return;

            isReleased    = true;
            ReleaseReason = reason ?? "未持锁";
            held          = mutex;
            mutex         = null;
        }

        lifetime.Cancel();

        if (held != null)
        {
            held.Dispose();
            Log.Information("[Minion] 放挂载锁（{Reason}）PID={Pid}", reason, pid);
        }

        released.TrySetResult();
    }
}
