using System.Diagnostics;
using System.IO;
using XIVLauncher.InGame;
using XIVLauncher.Minion;
using Xunit;

namespace XIVLauncher.Test.CatHost;

/// <summary>
///     游戏记录: 接管时的唯一依据, 读写要可靠, 清理不能误删活着的游戏
/// </summary>
[Collection(nameof(GameRecordsTests))]
public sealed class GameRecordsTests : IDisposable
{
    private readonly string originalDirectory = GameRecords.Directory;
    private readonly string tempDirectory     = Path.Combine(Path.GetTempPath(), "dml-game-records-" + Guid.NewGuid().ToString("N"));

    public GameRecordsTests() => GameRecords.Directory = tempDirectory;

    public void Dispose()
    {
        GameRecords.Directory = originalDirectory;

        if (Directory.Exists(tempDirectory))
            Directory.Delete(tempDirectory, true);
    }

    private static GameRecord NewRecord(Process game) =>
        new()
        {
            Pid                       = game.Id,
            ProcessStartedAt          = MinionOccupancy.GetProcessStartedAt(game),
            OperationId               = "op-1",
            Channel                   = "sdo",
            AccountName               = "acc",
            AccountUserName           = "acc",
            AreaName                  = "陆行鸟",
            Dalamud                   = true,
            DalamudRequested          = true,
            RestartNoThirdPlugins     = true,
            DcTravelPort              = 51234,
            SndaId                    = "123",
            Tgt                       = "ENCRYPTED-TGT",
            Guid                      = "ENCRYPTED-GUID",
            CrashDialogTimeoutSeconds = 600,
            GuardPid                  = Environment.ProcessId,
            GuardStartedAt            = MinionOccupancy.GetProcessStartedAt(Process.GetCurrentProcess()),
            UpdatedAt                 = DateTimeOffset.UtcNow
        };

    [Fact]
    public void WriteThenRead_RoundTrips()
    {
        using var game = StartSleeper();

        try
        {
            var record = NewRecord(game);

            Assert.True(GameRecords.Write(record));

            var read = GameRecords.Read(game.Id);
            Assert.NotNull(read);
            Assert.Equal(record.DcTravelPort, read.DcTravelPort);
            Assert.Equal(record.Tgt, read.Tgt);
            Assert.True(read.RestartNoThirdPlugins);
            Assert.Equal(record.ProcessStartedAt.ToUnixTimeMilliseconds(), read.ProcessStartedAt.ToUnixTimeMilliseconds());
            Assert.Empty(Directory.GetFiles(tempDirectory, "*.tmp"));
        }
        finally
        {
            game.Kill();
        }
    }

    [Fact]
    public void ReadMatching_RejectsReusedPid()
    {
        using var game = StartSleeper();

        try
        {
            var record = NewRecord(game);
            GameRecords.Write(record);

            Assert.NotNull(GameRecords.ReadMatching(game.Id, record.ProcessStartedAt));
            Assert.Null(GameRecords.ReadMatching(game.Id, record.ProcessStartedAt.AddMinutes(-5)));
        }
        finally
        {
            game.Kill();
        }
    }

    [Fact]
    public void Delete_WithStartTime_DoesNotDeleteAnotherProcessesRecord()
    {
        using var game = StartSleeper();

        try
        {
            var record = NewRecord(game);
            GameRecords.Write(record);

            GameRecords.Delete(game.Id, record.ProcessStartedAt.AddMinutes(-5));
            Assert.NotNull(GameRecords.Read(game.Id));

            GameRecords.Delete(game.Id, record.ProcessStartedAt);
            Assert.Null(GameRecords.Read(game.Id));
        }
        finally
        {
            game.Kill();
        }
    }

    [Fact]
    public void PruneStale_RemovesExitedGames_KeepsLiveAndOrphanCrashHandlerOnes()
    {
        using var live = StartSleeper();

        try
        {
            var exitedPid   = FindUnusedPid();
            var orphanedPid = FindUnusedPid(exitedPid - 4);

            GameRecords.Write(NewRecord(live));
            GameRecords.Write(NewRecord(live) with { Pid = exitedPid });
            GameRecords.Write(NewRecord(live) with { Pid = orphanedPid });

            GameRecords.PruneStale(pid => pid == orphanedPid);

            Assert.NotNull(GameRecords.Read(live.Id));
            Assert.Null(GameRecords.Read(exitedPid));
            Assert.NotNull(GameRecords.Read(orphanedPid));
            Assert.Single(GameRecords.ReadAllLive());
        }
        finally
        {
            live.Kill();
        }
    }

    [Fact]
    public void GuardClaim_IsExclusive_AndReleasedOnDispose()
    {
        var startedAt = DateTimeOffset.UtcNow;

        using (var first = GameRecords.TryClaimGuard(4321, startedAt))
        {
            Assert.NotNull(first);
            Assert.Null(GameRecords.TryClaimGuard(4321, startedAt));
            Assert.True(GameRecords.IsGuarded(4321, startedAt));
        }

        Assert.False(GameRecords.IsGuarded(4321, startedAt));
        Assert.False(File.Exists(GameRecords.GuardLockPath(4321, startedAt)), "放掉时删掉锁文件");

        using var again = GameRecords.TryClaimGuard(4321, startedAt);
        Assert.NotNull(again);
    }

    /// <summary>
    ///     守护进程被强杀: 系统放掉它的锁, 新进程马上能认领（接管靠它）
    /// </summary>
    [Fact]
    public async Task GuardClaim_HeldByKilledProcess_CanBeClaimedRightAway()
    {
        var startedAt = DateTimeOffset.UtcNow;
        var lockPath  = GameRecords.GuardLockPath(5678, startedAt);
        Directory.CreateDirectory(tempDirectory);

        var script = $"$f=[IO.File]::Open('{lockPath}','OpenOrCreate','ReadWrite','None'); [Console]::Out.WriteLine('ready'); [Console]::Out.Flush(); Start-Sleep 60";
        var startInfo = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(script);

        using var guard = Process.Start(startInfo)!;

        try
        {
            Assert.Equal("ready", await guard.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)));
            Assert.True(GameRecords.IsGuarded(5678, startedAt));
            Assert.Null(GameRecords.TryClaimGuard(5678, startedAt));
        }
        finally
        {
            guard.Kill(true);
            await guard.WaitForExitAsync();
        }

        using var claim = GameRecords.TryClaimGuard(5678, startedAt);
        Assert.NotNull(claim);
    }

    /// <summary>
    ///     别的进程正读着记录时替换会失败（Windows 不许覆盖打开着的文件）; 读只占一小会儿, 写入要重试等到它
    /// </summary>
    [Fact]
    public async Task Write_RetriesWhileAnotherReaderBrieflyHoldsTheFile()
    {
        using var game = StartSleeper();

        try
        {
            var record = NewRecord(game);
            Assert.True(GameRecords.Write(record));

            var reader = new FileStream(GameRecords.FilePath(game.Id), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var release = Task.Run(async () =>
            {
                await Task.Delay(50);
                await reader.DisposeAsync();
            });

            Assert.True(GameRecords.Write(record with { DcTravelPort = 50000 }));
            await release;

            Assert.Equal(50000, GameRecords.Read(game.Id)?.DcTravelPort);
            Assert.Empty(Directory.GetFiles(tempDirectory, "*.tmp"));
        }
        finally
        {
            game.Kill();
        }
    }

    [Fact]
    public void PruneStale_RemovesOldTempFiles_KeepsFreshOnes()
    {
        Directory.CreateDirectory(tempDirectory);
        var old   = Path.Combine(tempDirectory, "1234.json.old.tmp");
        var fresh = Path.Combine(tempDirectory, "1234.json.fresh.tmp");
        File.WriteAllText(old, "{}");
        File.WriteAllText(fresh, "{}");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddMinutes(-10));

        GameRecords.PruneStale();

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(fresh), "刚写的临时文件可能正被别的进程用, 留着");
    }

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

        return Process.Start(startInfo)!;
    }

    private static int FindUnusedPid(int from = 4_000_000)
    {
        for (var pid = from; pid > 1000; pid -= 4)
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

        throw new InvalidOperationException("找不到空闲的进程号");
    }
}
