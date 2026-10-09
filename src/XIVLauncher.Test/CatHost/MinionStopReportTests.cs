using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using XIVLauncher.CatHost;
using XIVLauncher.Minion;
using Xunit;

namespace XIVLauncher.Test.CatHost;

/// <summary>
///     本机开着 MINIONAPP 时按它的协议报状态: 包格式、只在 MINIONAPP 运行时发、编号与卡号 MD5 取自 launch 参数、游戏退出与清理残留记录时报「停机」
/// </summary>
[Collection(nameof(MinionOccupancyTests))]
public sealed class MinionStopReportTests : IDisposable
{
    private const string UID            = "0123456789abcdef0123456789abcdef";
    private const string KEYCODE        = "FFXIVXFAKE3333333333333333333333";
    private const string FORUM_ID       = "fake-forum-user";
    private const string FORUM_PASSWORD = "fake-forum-pass";

    private static readonly byte[] KeycodeMd5Bytes = MD5.HashData(Encoding.ASCII.GetBytes(KEYCODE));
    private static readonly string KeycodeMd5      = Convert.ToHexString(KeycodeMd5Bytes).ToLowerInvariant();

    private readonly string    originalDirectory = MinionOccupancy.Directory;
    private readonly string    tempDirectory     = Path.Combine(Path.GetTempPath(), "cat-minion-stop-" + Guid.NewGuid().ToString("N"));
    private readonly UdpClient listener          = new(new IPEndPoint(IPAddress.Loopback, 0));

    private bool minionAppRunning = true;

    public MinionStopReportTests()
    {
        MinionOccupancy.Directory                            = tempDirectory;
        MinionAppStatusReporter.IsMinionAppRunningOverride = () => minionAppRunning;
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
    public void KeycodeMd5Hex_IsLowercaseMd5OfAsciiKeycode()
    {
        Assert.Equal(KeycodeMd5, MinionAppStatusReporter.KeycodeMd5Hex(KEYCODE));
        Assert.Matches("^[0-9a-f]{32}$", MinionAppStatusReporter.KeycodeMd5Hex(KEYCODE)!);
        Assert.Null(MinionAppStatusReporter.KeycodeMd5Hex(""));
        Assert.Null(MinionAppStatusReporter.KeycodeMd5Hex(null));
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
        var packet = MinionAppStatusReporter.BuildPacket(UID, KeycodeMd5, 0x01020304, MinionAppStatusReporter.STATUS_RUNNING)!;

        Assert.Equal(KeycodeMd5Bytes, packet[16..32]);
        Assert.Equal(new byte[] { 0x04, 0x03, 0x02, 0x01 }, packet[32..36]);
        Assert.Equal(5, packet[36]);
        Assert.Equal(new byte[] { 0, 0, 0 }, packet[37..40]);
    }

    [Fact]
    public void BuildPacket_RejectsUidThatIsNot32Hex_AndZeroesMalformedKeycodeMd5()
    {
        Assert.Null(MinionAppStatusReporter.BuildPacket("abc", null, 0, 0));
        Assert.Null(MinionAppStatusReporter.BuildPacket("zz23456789abcdef0123456789abcdef", null, 0, 0));

        var packet = MinionAppStatusReporter.BuildPacket(UID, "not-a-md5", 0, 0)!;
        Assert.All(packet[16..32], b => Assert.Equal(0, b));
    }

    [Fact]
    public async Task SeedRunningStatus_SendsRunningPacket_ThenStopUsesSameCard()
    {
        using var game = StartSleeper();

        try
        {
            MinionAppStatusReporter.SeedRunningStatus(UID, KeycodeMd5, "0123456789abcdef", game);

            var running = await ReceiveAsync();
            Assert.Equal(Convert.FromHexString(UID), running[..16]);
            Assert.Equal(KeycodeMd5Bytes, running[16..32]);
            Assert.Equal((uint)game.Id, BitConverter.ToUInt32(running, 32));
            Assert.Equal(MinionAppStatusReporter.STATUS_RUNNING, running[36]);

            game.Kill();
            await game.WaitForExitAsync();

            // 没有占用记录也按本进程记下的卡报
            MinionAppStatusReporter.ReportStopped(game.Id);

            var stopped = await ReceiveAsync();
            Assert.Equal(Convert.FromHexString(UID), stopped[..16]);
            Assert.Equal(KeycodeMd5Bytes, stopped[16..32]);
            Assert.Equal(0u, BitConverter.ToUInt32(stopped, 32));
            Assert.Equal(MinionAppStatusReporter.STATUS_NONE, stopped[36]);
        }
        finally
        {
            if (!game.HasExited)
                game.Kill();
        }
    }

    [Fact]
    public async Task NothingIsSent_WhenMinionAppIsNotRunning()
    {
        minionAppRunning = false;
        using var game = StartSleeper();

        try
        {
            MinionAppStatusReporter.SeedRunningStatus(UID, KeycodeMd5, "0123456789abcdef", game);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReceiveAsync(TimeSpan.FromMilliseconds(500)));

            game.Kill();
            await game.WaitForExitAsync();

            MinionAppStatusReporter.ReportStopped(game.Id);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReceiveAsync(TimeSpan.FromMilliseconds(500)));
        }
        finally
        {
            if (!game.HasExited)
                game.Kill();
        }
    }

    [Fact]
    public async Task Record_RoundTripsUidAndKeycodeMd5_WithoutKeycode()
    {
        using var process = StartSleeper();

        try
        {
            MinionOccupancy.Write(NewRecord(process.Id, MinionOccupancy.GetProcessStartedAt(process)) with { AttachedAt = DateTimeOffset.UtcNow });

            var json = await File.ReadAllTextAsync(MinionOccupancy.FilePath(process.Id));
            Assert.Contains($"\"minionUid\": \"{UID}\"", json);
            Assert.Contains($"\"keycodeMd5\": \"{KeycodeMd5}\"", json);
            Assert.DoesNotContain(KEYCODE, json);
            Assert.Matches("\"attachedAt\": \"\\d{4}-\\d{2}-\\d{2}T\\d{2}:\\d{2}:\\d{2}\\.\\d{3}Z\"", json);

            var record = MinionOccupancy.Read(process.Id)!;
            Assert.Equal(UID, record.MinionUid);
            Assert.Equal(KeycodeMd5, record.KeycodeMd5);
        }
        finally
        {
            process.Kill();
        }
    }

    [Fact]
    public async Task ReservedFromLaunch_ThenGameGone_PruneReportsStopWithLaunchUidAndKeycodeMd5()
    {
        using var game = StartSleeper();
        var       card = new CatMinionLaunch("0123456789abcdef", MinionCards.VARIANT_CN, new CatSecret(KEYCODE), UID, FORUM_ID, new CatSecret(FORUM_PASSWORD));

        var (error, reserved) = await CatMinionReservations.ReserveAsync(card, game, "acc", MinionOccupancy.GetProcessStartedAt);
        Assert.Null(error);
        Assert.True(reserved);

        var json = await File.ReadAllTextAsync(MinionOccupancy.FilePath(game.Id));
        Assert.DoesNotContain(KEYCODE, json);
        Assert.DoesNotContain(FORUM_ID, json);
        Assert.DoesNotContain(FORUM_PASSWORD, json);

        game.Kill();
        await game.WaitForExitAsync();

        MinionOccupancy.PruneStale();

        var packet = await ReceiveAsync();
        Assert.Equal(Convert.FromHexString(UID), packet[..16]);
        Assert.Equal(KeycodeMd5Bytes, packet[16..32]);
        Assert.Equal(0u, BitConverter.ToUInt32(packet, 32));
        Assert.Equal(MinionAppStatusReporter.STATUS_NONE, packet[36]);
        Assert.False(File.Exists(MinionOccupancy.FilePath(game.Id)));
    }

    [Fact]
    public async Task PruneStale_OldRecordWithoutKeycodeMd5_ReportsStopWithZeroKeyMd5()
    {
        var deadPid = FindUnusedPid();
        MinionOccupancy.Write(NewRecord(deadPid, DateTimeOffset.UtcNow.AddMinutes(-5)) with { KeycodeMd5 = null });

        MinionOccupancy.PruneStale();

        var packet = await ReceiveAsync();
        Assert.Equal(Convert.FromHexString(UID), packet[..16]);
        Assert.All(packet[16..32], b => Assert.Equal(0, b));
        Assert.Equal(0, packet[36]);
        Assert.False(File.Exists(MinionOccupancy.FilePath(deadPid)));
    }

    [Fact]
    public async Task PruneStale_DoesNotReportStop_WhenSameUidIsAttachedToLiveGame()
    {
        using var live = StartSleeper();

        try
        {
            var deadPid = FindUnusedPid();
            MinionOccupancy.Write(NewRecord(deadPid, DateTimeOffset.UtcNow.AddMinutes(-5)));
            MinionOccupancy.Write(NewRecord(live.Id, MinionOccupancy.GetProcessStartedAt(live)));

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
            MinionOccupancy.Write(NewRecord(live.Id, MinionOccupancy.GetProcessStartedAt(live)));

            MinionAppStatusReporter.ReportStopped(FindUnusedPid(), UID, KeycodeMd5);

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
        MinionOccupancy.WriteAndDeleteOnExit(NewRecord(process.Id, MinionOccupancy.GetProcessStartedAt(process)), process);

        process.Kill();

        var packet = await ReceiveAsync();
        Assert.Equal(Convert.FromHexString(UID), packet[..16]);
        Assert.Equal(KeycodeMd5Bytes, packet[16..32]);
        Assert.Equal(0, packet[36]);
    }

    [Fact]
    public async Task ReportStopped_SendsTheStopPacketTwice()
    {
        var originalDelay = MinionAppStatusReporter.StopResendDelay;
        MinionAppStatusReporter.StopResendDelay = TimeSpan.FromMilliseconds(100);

        try
        {
            MinionAppStatusReporter.ReportStopped(FindUnusedPid(), UID, KeycodeMd5);

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
    public async Task ReportStopped_DoesNotRepeat_WhenUidIsAttachedToAnotherGameMeanwhile()
    {
        var originalDelay = MinionAppStatusReporter.StopResendDelay;
        MinionAppStatusReporter.StopResendDelay = TimeSpan.FromMilliseconds(400);

        using var live = StartSleeper();

        try
        {
            MinionAppStatusReporter.ReportStopped(FindUnusedPid(), UID, KeycodeMd5);
            await ReceiveAsync();

            MinionOccupancy.Write(NewRecord(live.Id, MinionOccupancy.GetProcessStartedAt(live)));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReceiveAsync(TimeSpan.FromMilliseconds(1200)));
        }
        finally
        {
            MinionAppStatusReporter.StopResendDelay = originalDelay;
            live.Kill();
        }
    }

    private async Task<byte[]> ReceiveAsync(TimeSpan? timeout = null)
    {
        using var cts    = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        var       result = await listener.ReceiveAsync(cts.Token);
        return result.Buffer;
    }

    private static MinionOccupancyRecord NewRecord(int pid, DateTimeOffset startedAt) =>
        new()
        {
            Pid              = pid,
            ProcessStartedAt = startedAt,
            CardFingerprint  = "0123456789abcdef",
            Variant          = MinionCards.VARIANT_CN,
            AccountName      = "acc",
            MinionUid        = UID,
            KeycodeMd5       = KeycodeMd5,
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
