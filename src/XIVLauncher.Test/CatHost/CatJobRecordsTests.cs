using System.IO;
using System.Text.Json;
using XIVLauncher.CatHost;
using XIVLauncher.InGame;
using Xunit;

namespace XIVLauncher.Test.CatHost;

/// <summary>
///     任务单与守护记录的接线: 新记录带上任务单、setJob 改写记录、记录没写好时补写、交接中不改、接管时恢复并马上认游戏
/// </summary>
[Collection(nameof(GameRecordsTests))]
public sealed class CatJobRecordsTests : IDisposable
{
    private const int PID = 4242;

    private static readonly DateTimeOffset StartedAt = DateTimeOffset.FromUnixTimeMilliseconds(1_797_000_000_000);

    private readonly string           originalDirectory = GameRecords.Directory;
    private readonly string           tempDirectory     = Path.Combine(Path.GetTempPath(), "dml-job-records-" + Guid.NewGuid().ToString("N"));
    private readonly GameRecordWriter writer            = new();
    private readonly CatJobChannel    channel           = new(() => "op-1", (_, _) => { });

    private int? currentPid;
    private bool handingOff;

    public CatJobRecordsTests() => GameRecords.Directory = tempDirectory;

    public void Dispose()
    {
        GameRecords.Directory = originalDirectory;

        if (Directory.Exists(tempDirectory))
            Directory.Delete(tempDirectory, true);
    }

    private CatJobRecords NewRecords() => new(writer, channel, () => currentPid, () => handingOff);

    private static GameRecord Record(JsonElement? job = null) =>
        new()
        {
            Pid              = PID,
            ProcessStartedAt = StartedAt,
            OperationId      = "op-1",
            Channel          = GameRecordChannels.SDO,
            AccountName      = "acc",
            AccountUserName  = "acc",
            GuardPid         = Environment.ProcessId,
            GuardStartedAt   = StartedAt,
            UpdatedAt        = DateTimeOffset.UtcNow,
            CatJob           = job
        };

    private static long? RevisionOnDisk() =>
        GameRecords.Read(PID)?.CatJob is { } job ? job.GetProperty("revision").GetInt64() : null;

    /// <summary>等 Changed 触发的后台改写落盘</summary>
    private static async Task<long?> WaitRevisionOnDisk(long expected)
    {
        for (var i = 0; i < 100; i++)
        {
            if (RevisionOnDisk() == expected)
                return expected;

            await Task.Delay(20);
        }

        return RevisionOnDisk();
    }

    [Fact]
    public async Task SetJob_RewritesTheCurrentGamesRecord()
    {
        var jobRecords = NewRecords();
        currentPid = PID;

        Assert.True(await writer.WriteNewAsync(PID, StartedAt, () => Task.FromResult<GameRecord?>(Record(jobRecords.Current))));
        Assert.Null(RevisionOnDisk());

        channel.SetJob(CatJobChannelTests.Job(3));
        Assert.Equal(3, await WaitRevisionOnDisk(3));

        channel.SetJob(CatJobChannelTests.Job(4));
        Assert.Equal(4, await WaitRevisionOnDisk(4));

        // 其它字段不动
        Assert.Equal("acc", GameRecords.Read(PID)!.AccountUserName);
    }

    [Fact]
    public async Task SetJob_BeforeTheRecordExists_IsWrittenOnceTheRecordIs()
    {
        var jobRecords = NewRecords();

        // 游戏还没起来: 改写被跳过, 记成要补
        channel.SetJob(CatJobChannelTests.Job(2));
        await Task.Delay(50);

        currentPid = PID;

        // 记录生成时还拿着旧内容（例如 setJob 恰好落在生成之后）, 写好后补写一次
        Assert.True(await writer.WriteNewAsync(PID, StartedAt, () => Task.FromResult<GameRecord?>(Record())));
        Assert.Null(RevisionOnDisk());

        await jobRecords.RecordWrittenAsync();
        Assert.Equal(2, RevisionOnDisk());
    }

    [Fact]
    public async Task HandingOff_LeavesTheRecordAlone()
    {
        var jobRecords = NewRecords();
        currentPid = PID;
        Assert.True(await writer.WriteNewAsync(PID, StartedAt, () => Task.FromResult<GameRecord?>(Record(CatJobChannelTests.Job(1)))));

        handingOff = true;
        Assert.False(await jobRecords.RewriteAsync());
        channel.SetJob(CatJobChannelTests.Job(5));
        await Task.Delay(100);

        Assert.Equal(1, RevisionOnDisk());
    }

    [Fact]
    public void CurrentOr_PrefersTheNewerJobInMemory_ElseKeepsTheRecords()
    {
        var jobRecords = NewRecords();
        var fromRecord = CatJobChannelTests.Job(1);

        Assert.Equal(1, jobRecords.CurrentOr(fromRecord)!.Value.GetProperty("revision").GetInt64());

        channel.SetJob(CatJobChannelTests.Job(6));

        Assert.Equal(6, jobRecords.CurrentOr(fromRecord)!.Value.GetProperty("revision").GetInt64());
        Assert.Equal(6, jobRecords.Current!.Value.GetProperty("revision").GetInt64());
    }

    [Fact]
    public void Restore_TakesTheRecordsJob_AndAnswersTheGameRightAway()
    {
        var jobRecords = NewRecords();

        jobRecords.Restore(Record(CatJobChannelTests.Job(7)), PID);

        Assert.Equal(7, channel.Revision);
        Assert.Equal(200, channel.GetJob(PID).StatusCode);
    }

    [Fact]
    public void Restore_WithoutAJob_StillAnswersTheGame()
    {
        var jobRecords = NewRecords();

        jobRecords.Restore(Record(), PID);

        Assert.Equal(204, channel.GetJob(PID).StatusCode);
    }
}
