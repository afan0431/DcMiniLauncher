using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using XIVLauncher.Minion;
using Xunit;

namespace XIVLauncher.Test.CatHost;

/// <summary>
///     游戏退出后给 MINIONAPP 报「停机」: 包格式、占用记录带 UID、清理残留记录时补报
/// </summary>
[Collection(nameof(MinionOccupancyTests))]
public sealed class MinionStopReportTests : IDisposable
{
    private const string UID = "0123456789abcdef0123456789ABCDEF";

    private readonly string    originalDirectory = MinionOccupancy.Directory;
    private readonly string    tempDirectory     = Path.Combine(Path.GetTempPath(), "cat-minion-stop-" + Guid.NewGuid().ToString("N"));
    private readonly UdpClient listener          = new(new IPEndPoint(IPAddress.Loopback, 0));

    public MinionStopReportTests()
    {
        MinionOccupancy.Directory                            = tempDirectory;
        MinionAppStatusReporter.IsMinionAppRunningOverride = () => true;
        MinionAppStatusReporter.PortOverride               = ((IPEndPoint)listener.Client.LocalEndPoint!).Port;
    }

    public void Dispose()
    {
        MinionOccupancy.Directory                            = originalDirectory;
        MinionAppStatusReporter.IsMinionAppRunningOverride = null;
        MinionAppStatusReporter.PortOverride               = null;
        listener.Dispose();

        if (Directory.Exists(tempDirectory))
            Directory.Delete(tempDirectory, true);
    }

    [Fact]
    public void StopPacket_Is40Bytes_UidPidZeroStatusZero()
    {
        var packet = MinionAppStatusReporter.BuildPacket(UID, null, 0, MinionAppStatusReporter.STATUS_NONE)!;

        Assert.Equal(40, packet.Length);
        Assert.Equal(Convert.FromHexString(UID), packet[..16]);
        Assert.All(packet[16..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void RunningPacket_CarriesKeycodeMd5_PidLittleEndian_AndStatus5()
    {
        var packet = MinionAppStatusReporter.BuildPacket(UID, "KEY", 0x01020304, MinionAppStatusReporter.STATUS_RUNNING)!;

        Assert.Equal(System.Security.Cryptography.MD5.HashData("KEY"u8.ToArray()), packet[16..32]);
        Assert.Equal(new byte[] { 0x04, 0x03, 0x02, 0x01 }, packet[32..36]);
        Assert.Equal(5, packet[36]);
        Assert.Equal(new byte[] { 0, 0, 0 }, packet[37..40]);
    }

    [Fact]
    public void BuildPacket_RejectsUidThatIsNot32Hex()
    {
        Assert.Null(MinionAppStatusReporter.BuildPacket("abc", null, 0, 0));
        Assert.Null(MinionAppStatusReporter.BuildPacket("zz23456789abcdef0123456789abcdef", null, 0, 0));
    }

    [Fact]
    public async Task Record_RoundTripsMinionUid_AndWritesMillisecondTimestamps()
    {
        using var process = StartSleeper();

        try
        {
            MinionOccupancy.Write(NewRecord(process.Id, MinionOccupancy.GetProcessStartedAt(process), UID) with { AttachedAt = DateTimeOffset.UtcNow });

            var json = await File.ReadAllTextAsync(MinionOccupancy.FilePath(process.Id));
            Assert.Contains($"\"minionUid\": \"{UID}\"", json);
            Assert.Matches("\"attachedAt\": \"\\d{4}-\\d{2}-\\d{2}T\\d{2}:\\d{2}:\\d{2}\\.\\d{3}Z\"", json);
            Assert.Equal(UID, MinionOccupancy.Read(process.Id)!.MinionUid);
        }
        finally
        {
            process.Kill();
        }
    }

    [Fact]
    public async Task PruneStale_ReportsStopForDeadRecordWithUid_ThenDeletesIt()
    {
        var deadPid = FindUnusedPid();
        MinionOccupancy.Write(NewRecord(deadPid, DateTimeOffset.UtcNow.AddMinutes(-5), UID));

        MinionOccupancy.PruneStale();

        var packet = await ReceiveAsync();
        Assert.Equal(Convert.FromHexString(UID), packet[..16]);
        Assert.Equal(0u, BitConverter.ToUInt32(packet, 32));
        Assert.Equal(0, packet[36]);
        Assert.False(File.Exists(MinionOccupancy.FilePath(deadPid)));
    }

    [Fact]
    public async Task PruneStale_DoesNotReportStop_WhenSameRowIsAttachedToLiveGame()
    {
        using var live = StartSleeper();

        try
        {
            var deadPid = FindUnusedPid();
            MinionOccupancy.Write(NewRecord(deadPid, DateTimeOffset.UtcNow.AddMinutes(-5), UID));
            MinionOccupancy.Write(NewRecord(live.Id, MinionOccupancy.GetProcessStartedAt(live), UID));

            MinionOccupancy.PruneStale();

            Assert.False(File.Exists(MinionOccupancy.FilePath(deadPid)));
            Assert.True(File.Exists(MinionOccupancy.FilePath(live.Id)));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReceiveAsync(TimeSpan.FromMilliseconds(500)));
        }
        finally
        {
            live.Kill();
        }
    }

    [Fact]
    public async Task ReportStopped_Skips_WhenSameUidIsAttachedToAnotherLiveGame()
    {
        using var live = StartSleeper();

        try
        {
            MinionOccupancy.Write(NewRecord(live.Id, MinionOccupancy.GetProcessStartedAt(live), UID));

            MinionAppStatusReporter.ReportStopped(FindUnusedPid(), UID);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReceiveAsync(TimeSpan.FromMilliseconds(500)));
        }
        finally
        {
            live.Kill();
        }
    }

    [Fact]
    public async Task WriteAndDeleteOnExit_ReportsStopWhenGameExits()
    {
        using var process = StartSleeper();
        MinionOccupancy.WriteAndDeleteOnExit(NewRecord(process.Id, MinionOccupancy.GetProcessStartedAt(process), UID), process);

        process.Kill();

        var packet = await ReceiveAsync();
        Assert.Equal(Convert.FromHexString(UID), packet[..16]);
        Assert.Equal(0, packet[36]);
    }

    [Fact]
    public async Task ReportStopped_SendsTheStopPacketTwice()
    {
        var originalDelay = MinionAppStatusReporter.StopResendDelay;
        MinionAppStatusReporter.StopResendDelay = TimeSpan.FromMilliseconds(100);

        try
        {
            MinionAppStatusReporter.ReportStopped(FindUnusedPid(), UID);

            var first  = await ReceiveAsync();
            var second = await ReceiveAsync();

            Assert.Equal(first, second);
            Assert.Equal(0, second[36]);
        }
        finally
        {
            MinionAppStatusReporter.StopResendDelay = originalDelay;
        }
    }

    [Fact]
    public async Task ReportStopped_DoesNotRepeat_WhenRowIsAttachedToAnotherGameMeanwhile()
    {
        var originalDelay = MinionAppStatusReporter.StopResendDelay;
        MinionAppStatusReporter.StopResendDelay = TimeSpan.FromMilliseconds(400);

        using var live = StartSleeper();

        try
        {
            MinionAppStatusReporter.ReportStopped(FindUnusedPid(), UID);
            await ReceiveAsync();

            MinionOccupancy.Write(NewRecord(live.Id, MinionOccupancy.GetProcessStartedAt(live), UID));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReceiveAsync(TimeSpan.FromMilliseconds(1200)));
        }
        finally
        {
            MinionAppStatusReporter.StopResendDelay = originalDelay;
            live.Kill();
        }
    }

    [Fact]
    public void ReadAllLive_SkipsDeadRecords()
    {
        using var live = StartSleeper();

        try
        {
            MinionOccupancy.Write(NewRecord(live.Id, MinionOccupancy.GetProcessStartedAt(live), UID));
            MinionOccupancy.Write(NewRecord(FindUnusedPid(), DateTimeOffset.UtcNow, "ffffffffffffffffffffffffffffffff"));

            var records = MinionOccupancy.ReadAllLive();

            Assert.Single(records);
            Assert.Equal(UID, records[0].MinionUid);
        }
        finally
        {
            live.Kill();
        }
    }

    private async Task<byte[]> ReceiveAsync(TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        var result = await listener.ReceiveAsync(cts.Token);
        return result.Buffer;
    }

    private static MinionOccupancyRecord NewRecord(int pid, DateTimeOffset startedAt, string uid) =>
        new()
        {
            Pid              = pid,
            ProcessStartedAt = startedAt,
            CardFingerprint  = "0123456789abcdef",
            Variant          = MinionCards.VARIANT_CN,
            AccountName      = "acc",
            MinionUid        = uid,
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
        for (var pid = 4_000_000 + Random.Shared.Next(0, 1000) * 4; pid > 1000; pid -= 4)
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
}
