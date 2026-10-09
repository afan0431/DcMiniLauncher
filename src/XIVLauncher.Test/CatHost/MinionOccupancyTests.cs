using System.Diagnostics;
using System.IO;
using XIVLauncher.CatHost;
using XIVLauncher.Minion;
using Xunit;

namespace XIVLauncher.Test.CatHost;

[Collection(nameof(MinionOccupancyTests))]
public sealed class MinionOccupancyTests : IDisposable
{
    private readonly string originalDirectory = MinionOccupancy.Directory;
    private readonly string tempDirectory     = Path.Combine(Path.GetTempPath(), "cat-minion-occupancy-" + Guid.NewGuid().ToString("N"));

    public MinionOccupancyTests()
    {
        MinionOccupancy.Directory                            = tempDirectory;
        MinionAppStatusReporter.IsMinionAppRunningOverride = () => false;
    }

    public void Dispose()
    {
        MinionOccupancy.Directory                            = originalDirectory;
        MinionAppStatusReporter.IsMinionAppRunningOverride = null;

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

    [Fact]
    public async Task Reserve_International_WritesLaunchVariantAndUid()
    {
        using var game = StartSleeper();

        try
        {
            var (error, reserved) = await CatMinionReservations.ReserveAsync(Card(MinionCards.VARIANT_GLOBAL), game, "seAccount", MinionOccupancy.GetProcessStartedAt);

            Assert.Null(error);
            Assert.True(reserved);

            var record = MinionOccupancy.Read(game.Id)!;
            Assert.Equal(MinionCards.VARIANT_GLOBAL, record.Variant);
            Assert.Equal("0123456789abcdef", record.CardFingerprint);
            Assert.Equal(UID, record.MinionUid);
            Assert.Equal("seAccount", record.AccountName);
            Assert.Equal(ExpectedKeycodeMd5, record.KeycodeMd5);

            var json = await File.ReadAllTextAsync(MinionOccupancy.FilePath(game.Id));
            Assert.Contains($"\"keycodeMd5\": \"{ExpectedKeycodeMd5}\"", json);
            Assert.DoesNotContain(KEYCODE, json);
            Assert.DoesNotContain(FORUM_PASSWORD, json);
            Assert.DoesNotContain(FORUM_ID, json);
        }
        finally
        {
            game.Kill();
        }
    }

    [Fact]
    public async Task Reserve_SameUidOnAnotherLiveGame_IsAlreadyAttached()
    {
        using var first  = StartSleeper();
        using var second = StartSleeper();

        try
        {
            MinionOccupancy.Write(NewRecord(first) with { MinionUid = UID });

            var (error, reserved) = await CatMinionReservations.ReserveAsync(Card(MinionCards.VARIANT_CN), second, "acc", MinionOccupancy.GetProcessStartedAt);

            Assert.Equal(CatCodes.ALREADY_ATTACHED, error?.Code);
            Assert.False(reserved);
            Assert.Null(MinionOccupancy.Read(second.Id));

            // 同一个游戏重挂（force）: 不算占用, 也不再写预占
            var (again, reservedAgain) = await CatMinionReservations.ReserveAsync(Card(MinionCards.VARIANT_CN), first, "acc", MinionOccupancy.GetProcessStartedAt);

            Assert.Null(again);
            Assert.False(reservedAgain);
        }
        finally
        {
            first.Kill();
            second.Kill();
        }
    }

    [Fact]
    public async Task Read_OldRecordWithoutKeycodeMd5_IsNull()
    {
        Directory.CreateDirectory(tempDirectory);
        await File.WriteAllTextAsync
        (
            MinionOccupancy.FilePath(1234),
            """
            {
              "pid": 1234,
              "processStartedAt": "2026-10-01T00:00:00.000Z",
              "cardFingerprint": "0123456789abcdef",
              "variant": "cn",
              "minionUid": "0123456789abcdef0123456789abcdef",
              "attachedAt": "2026-10-01T00:00:05.000Z"
            }
            """
        );

        var record = MinionOccupancy.Read(1234)!;

        Assert.Equal(UID, record.MinionUid);
        Assert.Null(record.KeycodeMd5);
    }

    private const string UID            = "0123456789abcdef0123456789abcdef";
    private const string KEYCODE        = "FFXIVXFAKE2222222222222222222222";
    private const string FORUM_ID       = "fake-forum-user";
    private const string FORUM_PASSWORD = "fake-forum-pass";

    /// <summary>MD5(ASCII(KEYCODE)) 的小写十六进制</summary>
    private static readonly string ExpectedKeycodeMd5 = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.ASCII.GetBytes(KEYCODE))).ToLowerInvariant();

    private static CatMinionLaunch Card(string variant) =>
        new("0123456789abcdef", variant, new CatSecret(KEYCODE), UID, FORUM_ID, new CatSecret(FORUM_PASSWORD));

    private static MinionOccupancyRecord NewRecord(Process process) =>
        new()
        {
            Pid              = process.Id,
            ProcessStartedAt = MinionOccupancy.GetProcessStartedAt(process),
            CardFingerprint  = "0123456789abcdef",
            Variant          = MinionCards.VARIANT_CN,
            AccountName      = "acc",
            AttachedAt       = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
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
