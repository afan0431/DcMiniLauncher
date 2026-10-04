using System.Diagnostics;
using System.IO;
using XIVLauncher.Minion;
using Xunit;

namespace XIVLauncher.Test.CatHost;

[Collection(nameof(MinionOccupancyTests))]
public sealed class MinionOccupancyTests : IDisposable
{
    private readonly string originalDirectory = MinionOccupancy.Directory;
    private readonly string tempDirectory     = Path.Combine(Path.GetTempPath(), "cat-minion-occupancy-" + Guid.NewGuid().ToString("N"));

    public MinionOccupancyTests() =>
        MinionOccupancy.Directory = tempDirectory;

    public void Dispose()
    {
        MinionOccupancy.Directory = originalDirectory;

        if (Directory.Exists(tempDirectory))
            Directory.Delete(tempDirectory, true);
    }

    [Fact]
    public async Task WriteAndDeleteOnExit_WritesRecord_AndDeletesWhenProcessExits()
    {
        using var process = StartSleeper();
        var record = NewRecord(process);

        MinionOccupancy.WriteAndDeleteOnExit(record, process);

        var path = MinionOccupancy.FilePath(process.Id);
        Assert.True(File.Exists(path));

        var json = await File.ReadAllTextAsync(path);
        Assert.Contains("\"cardFingerprint\": \"0123456789abcdef\"", json);
        Assert.Contains("\"variant\": \"cn\"", json);
        Assert.Contains("\"accountName\": \"acc\"", json);
        Assert.Equal(record, MinionOccupancy.Read(process.Id));

        process.Kill();
        await WaitUntilAsync(() => !File.Exists(path));
    }

    [Fact]
    public void PruneStale_RemovesRecordsOfExitedProcesses_KeepsLiveOnes()
    {
        using var live = StartSleeper();

        try
        {
            MinionOccupancy.Write(NewRecord(live));
            MinionOccupancy.Write(NewRecord(live) with { Pid = FindUnusedPid() });

            MinionOccupancy.PruneStale();

            Assert.Single(Directory.GetFiles(tempDirectory, "minion-*.json"));
            Assert.True(File.Exists(MinionOccupancy.FilePath(live.Id)));
        }
        finally
        {
            live.Kill();
        }
    }

    [Fact]
    public void PruneStale_RemovesRecordWhosePidWasReused()
    {
        using var live = StartSleeper();

        try
        {
            MinionOccupancy.Write(NewRecord(live) with { ProcessStartedAt = DateTimeOffset.UtcNow.AddHours(-3) });

            MinionOccupancy.PruneStale();

            Assert.False(File.Exists(MinionOccupancy.FilePath(live.Id)));
        }
        finally
        {
            live.Kill();
        }
    }

    private static MinionOccupancyRecord NewRecord(Process process) =>
        new()
        {
            Pid              = process.Id,
            ProcessStartedAt = MinionOccupancy.GetProcessStartedAt(process),
            CardFingerprint  = "0123456789abcdef",
            Variant          = MinionCards.VARIANT_CN,
            AccountName      = "acc",
            AttachedAt       = DateTimeOffset.UtcNow
        };

    private static Process StartSleeper()
    {
        var startInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "PING.EXE"))
        {
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardOutput = true
        };
        startInfo.ArgumentList.Add("-n");
        startInfo.ArgumentList.Add("60");
        startInfo.ArgumentList.Add("127.0.0.1");

        var process = Process.Start(startInfo)!;
        process.BeginOutputReadLine();
        return process;
    }

    private static int FindUnusedPid()
    {
        for (var pid = 4_000_000; pid > 1000; pid -= 4)
        {
            try
            {
                using var _ = Process.GetProcessById(pid);
            }
            catch (ArgumentException)
            {
                return pid;
            }
        }

        throw new InvalidOperationException("找不到未使用的进程号");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);

        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException();

            await Task.Delay(50);
        }
    }
}
