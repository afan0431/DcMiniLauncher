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
    public void IsGuardAlive_FalseForSelfAndForDeadGuard()
    {
        using var guard = StartSleeper();

        try
        {
            var record = NewRecord(guard);

            Assert.False(GameRecords.IsGuardAlive(record), "守护者是本进程时不算「别人还在守」");
            Assert.True(GameRecords.IsGuardAlive(record with { GuardPid = guard.Id, GuardStartedAt = MinionOccupancy.GetProcessStartedAt(guard) }));
            Assert.False(GameRecords.IsGuardAlive(record with { GuardPid = FindUnusedPid() }));
        }
        finally
        {
            guard.Kill();
        }
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
