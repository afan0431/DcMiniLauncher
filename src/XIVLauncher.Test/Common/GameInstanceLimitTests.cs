using System.Diagnostics;
using XIVLauncher.Common.Game;
using Xunit;

namespace XIVLauncher.Test.Common;

/// <summary>
///     解除多开限制: 用真实的子进程占住命名互斥体, 从外部关掉它的句柄
/// </summary>
public sealed class GameInstanceLimitTests : IDisposable
{
    private readonly string   id = Guid.NewGuid().ToString("N");
    private          Process? holder;

    public void Dispose()
    {
        if (holder == null)
            return;

        try
        {
            if (!holder.HasExited)
                holder.Kill();

            holder.WaitForExit(5000);
        }
        catch (InvalidOperationException)
        {
        }

        holder.Dispose();
    }

    [Theory]
    [InlineData(@"\Sessions\1\BaseNamedObjects\6AA83AB5-BAC4-4a36-9F66-A309770760CB_ffxiv_game00", true)]
    [InlineData(@"\BaseNamedObjects\6aa83ab5-bac4-4a36-9f66-a309770760cb_ffxiv_game01", true)]
    [InlineData(@"\Sessions\1\BaseNamedObjects\DcMiniLauncher-MinionAttach", false)]
    public void IsGameSlotName_RecognizesKernelObjectNames(string name, bool expected) =>
        Assert.Equal(expected, GameInstanceLimit.IsGameSlotName(name));

    [Fact]
    public void ReleaseIn_ClosesOnlyMatchingMutexInOtherProcess()
    {
        var target = $@"Local\cat-instance-limit-{id}-slot";
        var other  = $@"Local\cat-instance-limit-{id}-other";

        holder = StartHolder(target, other);

        Assert.True(MutexExists(target));
        Assert.True(MutexExists(other));

        var closed = GameInstanceLimit.ReleaseIn([holder.Id], name => name.EndsWith($"cat-instance-limit-{id}-slot", StringComparison.Ordinal));

        Assert.Equal(1, closed);
        Assert.False(MutexExists(target));
        Assert.True(MutexExists(other));
        Assert.False(holder.HasExited);
    }

    [Fact]
    public void ReleaseIn_SkipsProcessThatCannotBeOpened()
    {
        var closed = GameInstanceLimit.ReleaseIn([int.MaxValue - 1], _ => true);

        Assert.Equal(0, closed);
    }

    [Fact]
    public void Launch_StartsOneGameAtATime()
    {
        var lockName = $@"Local\cat-launch-lock-{id}";
        var running  = 0;
        var peak     = 0;

        int Start()
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref peak, now);
            Thread.Sleep(200);
            Interlocked.Decrement(ref running);
            return now;
        }

        var launches = Enumerable.Range(0, 3)
                                 .Select(_ => Task.Factory.StartNew(
                                             () => GameInstanceLimit.Launch(lockName, () => 0, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(1), Start),
                                             TaskCreationOptions.LongRunning))
                                 .ToArray();

        Assert.True(Task.WaitAll(launches, TimeSpan.FromSeconds(10)));
        Assert.Equal(1, peak);
    }

    [Fact]
    public void Launch_GoesOnWhenReleaseHangs()
    {
        using var never = new ManualResetEventSlim();
        var       watch = Stopwatch.StartNew();

        var result = GameInstanceLimit.Launch($@"Local\cat-launch-lock-{id}", () =>
        {
            never.Wait();
            return 0;
        }, TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(200), () => 42);

        never.Set();
        Assert.Equal(42, result);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Launch_GoesOnWhenLockIsHeldTooLong()
    {
        var       lockName = $@"Local\cat-launch-lock-{id}";
        using var held     = new ManualResetEventSlim();
        using var done     = new ManualResetEventSlim();

        var holderThread = new Thread(() =>
        {
            using var other = new Mutex(true, lockName);
            held.Set();
            done.Wait();
            other.ReleaseMutex();
        });
        holderThread.Start();
        Assert.True(held.Wait(TimeSpan.FromSeconds(5)));

        try
        {
            var result = GameInstanceLimit.Launch(lockName, () => 0, TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(1), () => 7);
            Assert.Equal(7, result);
        }
        finally
        {
            done.Set();
            holderThread.Join(TimeSpan.FromSeconds(5));
        }
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;

        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }

    private static Process StartHolder(params string[] names)
    {
        var creates = string.Join("; ", names.Select((name, i) => $"$m{i} = [System.Threading.Mutex]::new($false, '{name}')"));
        var script  = $"{creates}; [Console]::Out.WriteLine('ready'); [Console]::Out.Flush(); Start-Sleep -Seconds 60";

        var process = Process.Start(new ProcessStartInfo("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", script])
        {
            RedirectStandardOutput = true,
            UseShellExecute        = false,
            CreateNoWindow         = true
        }) ?? throw new InvalidOperationException("无法启动占用互斥体的子进程");

        var line = process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
        Assert.Equal("ready", line);
        return process;
    }

    private static bool MutexExists(string name)
    {
        if (!Mutex.TryOpenExisting(name, out var mutex))
            return false;

        mutex.Dispose();
        return true;
    }
}
