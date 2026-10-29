using System.Diagnostics;
using System.IO;
using XIVLauncher.Minion;
using Xunit;

namespace XIVLauncher.Test.CatHost;

/// <summary>
///     本机挂 Minion 排队: 用真实的睡眠子进程当游戏和 MinionLauncher, 每个用例用自己的锁名, 时限换成短的
/// </summary>
public sealed class MinionAttachQueueTests : IDisposable
{
    private static readonly TimeSpan WAIT = TimeSpan.FromSeconds(10);

    private readonly string        mutexName     = $@"Local\DcMiniLauncher-MinionAttachTest-{Guid.NewGuid():N}";
    private readonly string        tempDirectory = Path.Combine(Path.GetTempPath(), $"minion-attach-queue-{Guid.NewGuid():N}");
    private readonly List<Process> processes     = [];

    private static MinionAttachQueueTimings Fast => new()
    {
        LockWait             = TimeSpan.FromSeconds(30),
        HoldCap              = TimeSpan.FromSeconds(30),
        NoTensorCoreGrace    = TimeSpan.FromMilliseconds(100),
        SharedInstallGrace   = TimeSpan.FromMilliseconds(300),
        SecondLauncherAppear = TimeSpan.FromSeconds(10),
        SecondLauncherExit   = TimeSpan.FromSeconds(10),
        Poll                 = TimeSpan.FromMilliseconds(100)
    };

    public void Dispose()
    {
        foreach (var process in processes)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill();

                process.WaitForExit(5000);
            }
            catch
            {
                // 已退出
            }

            process.Dispose();
        }

        try
        {
            Directory.Delete(tempDirectory, true);
        }
        catch
        {
            // 临时目录, 删不掉不影响结果
        }
    }

    [Fact]
    public async Task SecondContender_WaitsUntilFirstReleases()
    {
        var first = await EnterAsync(StartSleeper());
        Assert.NotNull(first);
        Assert.False(first.Waited);
        Assert.True(first.HoldsLock);

        var queued = 0;
        var second = MinionAttachQueue.EnterAsync(StartSleeper(), () => Interlocked.Increment(ref queued), mutexName, Fast, CancellationToken.None);

        await Task.Delay(500);
        Assert.False(second.IsCompleted);
        Assert.Equal(1, queued);

        first.Dispose();

        var lease = await second.WaitAsync(WAIT);
        Assert.NotNull(lease);
        Assert.True(lease.Waited);
        Assert.True(lease.HoldsLock);
        lease.Dispose();
    }

    [Fact]
    public async Task Lease_ReleasesAtHoldCap()
    {
        var lease = await EnterAsync(StartSleeper(), Fast with { HoldCap = TimeSpan.FromMilliseconds(300) });
        Assert.NotNull(lease);

        await lease.Released.WaitAsync(WAIT);
        Assert.Equal("挂载排队超时放锁", lease.ReleaseReason);

        var next = await EnterAsync(StartSleeper());
        Assert.NotNull(next);
        Assert.False(next.Waited);
        next.Dispose();
    }

    [Fact]
    public async Task Lease_ReleasesWhenGameExits()
    {
        var game  = StartSleeper();
        var lease = await EnterAsync(game);
        Assert.NotNull(lease);

        game.Kill();

        await lease.Released.WaitAsync(WAIT);
        Assert.Equal("游戏已退出", lease.ReleaseReason);
    }

    [Fact]
    public async Task WaitingContender_GivesUpWhenItsGameExits_AndDoesNotKeepTheLock()
    {
        var first = await EnterAsync(StartSleeper());
        Assert.NotNull(first);

        var game    = StartSleeper();
        var waiting = MinionAttachQueue.EnterAsync(game, null, mutexName, Fast, CancellationToken.None);
        await Task.Delay(300);
        game.Kill();

        Assert.Null(await waiting.WaitAsync(WAIT));

        first.Dispose();

        // 放弃排队的那一方后来拿到锁也会马上放掉, 下一个不会一直等
        var next = await MinionAttachQueue.EnterAsync(StartSleeper(), null, mutexName, Fast, CancellationToken.None).WaitAsync(WAIT);
        Assert.NotNull(next);
        next.Dispose();
    }

    [Fact]
    public async Task WaitingContender_ReturnsNullWhenCancelled()
    {
        var first = await EnterAsync(StartSleeper());
        Assert.NotNull(first);

        using var cancel  = new CancellationTokenSource();
        var       waiting = MinionAttachQueue.EnterAsync(StartSleeper(), null, mutexName, Fast, cancel.Token);
        await Task.Delay(300);
        cancel.Cancel();

        Assert.Null(await waiting.WaitAsync(WAIT));
        first.Dispose();
    }

    [Fact]
    public async Task TensorCoreOff_ReleasesAfterShortGrace()
    {
        var lease = await EnterAsync(StartSleeper());
        Assert.NotNull(lease);

        await lease.ReleaseAfterAttachAsync(ClientDir(), false, []).WaitAsync(WAIT);

        Assert.Equal("TensorCore 没开, 挂载完成", lease.ReleaseReason);
    }

    [Fact]
    public async Task MainInstall_ReleasesAfterFixedGrace_EvenWithTensorCore()
    {
        var lease = await EnterAsync(StartSleeper());
        Assert.NotNull(lease);

        var main = Path.Combine(tempDirectory, "MINI");
        Directory.CreateDirectory(main);
        var stopwatch = Stopwatch.StartNew();

        await lease.ReleaseAfterAttachAsync(main, true, []).WaitAsync(WAIT);

        Assert.Equal("MinionLauncher 已退出, 按固定间隔放锁", lease.ReleaseReason);
        Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(250));
    }

    [Fact]
    public async Task TensorCoreOn_HoldsUntilSecondLauncherUnderClientDirExits()
    {
        var clientDir = ClientDir();
        var launcher  = CopyLauncher(clientDir);
        var lease     = await EnterAsync(StartSleeper());
        Assert.NotNull(lease);

        // 本次自己拉起的那个 MinionLauncher 不算第二段
        var own   = Start(launcher, 30);
        var watch = lease.ReleaseAfterAttachAsync(clientDir, true, [own.Id]);

        await Task.Delay(800);
        Assert.False(lease.Released.IsCompleted);

        var second = Start(launcher, 3);
        await Task.Delay(800);
        Assert.False(second.HasExited);
        Assert.False(lease.Released.IsCompleted);

        await watch.WaitAsync(WAIT);
        Assert.True(second.HasExited);
        Assert.Equal("TensorCore 注入完成", lease.ReleaseReason);
    }

    [Fact]
    public async Task TensorCoreOn_IgnoresLauncherOutsideClientDir()
    {
        var clientDir = ClientDir();
        var other     = CopyLauncher(Path.Combine(tempDirectory, "MINI-clients", "other"));
        var lease     = await EnterAsync(StartSleeper(), Fast with { SecondLauncherAppear = TimeSpan.FromMilliseconds(1500) });
        Assert.NotNull(lease);

        Start(other, 30);

        await lease.ReleaseAfterAttachAsync(clientDir, true, []).WaitAsync(WAIT);

        Assert.Equal("没等到 TensorCore 注入", lease.ReleaseReason);
    }

    [Fact]
    public async Task TensorCoreOn_ReleasesWhenSecondLauncherDoesNotExitInTime()
    {
        var clientDir = ClientDir();
        var launcher  = CopyLauncher(clientDir);
        var lease     = await EnterAsync(StartSleeper(), Fast with { SecondLauncherExit = TimeSpan.FromMilliseconds(500) });
        Assert.NotNull(lease);

        Start(launcher, 30);

        await lease.ReleaseAfterAttachAsync(clientDir, true, []).WaitAsync(WAIT);

        Assert.Equal("TensorCore 注入超时", lease.ReleaseReason);
    }

    [Theory]
    [InlineData(@"E:\MINI-clients\123\MinionLauncher_64.exe", @"E:\MINI-clients\123", true)]
    [InlineData(@"e:\mini-clients\123\minionlauncher_64.exe", @"E:\MINI-clients\123\", true)]
    [InlineData(@"E:\MINI-clients\1234\MinionLauncher_64.exe", @"E:\MINI-clients\123", false)]
    [InlineData(@"E:\MINI\MinionLauncher_64.exe", @"E:\MINI-clients\123", false)]
    [InlineData(null, @"E:\MINI-clients\123", false)]
    public void IsUnder_MatchesOnlyPathsInsideTheDirectory(string? imagePath, string directory, bool expected) =>
        Assert.Equal(expected, MinionAttachQueue.IsUnder(imagePath, directory));

    private Task<MinionAttachLease?> EnterAsync(Process game, MinionAttachQueueTimings? timings = null) =>
        MinionAttachQueue.EnterAsync(game, null, mutexName, timings ?? Fast, CancellationToken.None).WaitAsync(WAIT);

    private string ClientDir()
    {
        var path = Path.Combine(tempDirectory, "MINI-clients", "x");
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>把系统的 PING.EXE 复制成 <c>MinionLauncher_64.exe</c>, 进程名与映像路径都和真的一样</summary>
    private static string CopyLauncher(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "MinionLauncher_64.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), path, true);
        return path;
    }

    private Process StartSleeper() =>
        Start(Path.Combine(Environment.SystemDirectory, "PING.EXE"), 60);

    /// <summary>ping 本机 <paramref name="count" /> 次, 约睡 count-1 秒</summary>
    private Process Start(string exe, int count)
    {
        var startInfo = new ProcessStartInfo(exe)
        {
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardOutput = true
        };
        startInfo.ArgumentList.Add("-n");
        startInfo.ArgumentList.Add(count.ToString());
        startInfo.ArgumentList.Add("127.0.0.1");

        var process = Process.Start(startInfo)!;
        process.BeginOutputReadLine();
        processes.Add(process);
        return process;
    }
}
