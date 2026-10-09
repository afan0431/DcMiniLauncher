using System.Diagnostics;
using System.IO;
using XIVLauncher.CatHost;
using XIVLauncher.InGame;
using XIVLauncher.Minion;
using Xunit;

namespace XIVLauncher.Test.CatHost;

/// <summary>
///     adopt: 接管一个原守护进程已不在的游戏。校验、拒绝分支、与 launch 互斥、成功后报 game.adopted
/// </summary>
[Collection(nameof(GameRecordsTests))]
public sealed class CatAdoptTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private const string CARD = "0123456789abcdef";

    private readonly string originalDirectory = GameRecords.Directory;
    private readonly string tempDirectory     = Path.Combine(Path.GetTempPath(), "dml-adopt-" + Guid.NewGuid().ToString("N"));
    private readonly List<Process> processes  = [];
    private readonly List<(string Method, string Json)> events = [];

    public CatAdoptTests() => GameRecords.Directory = tempDirectory;

    public void Dispose()
    {
        foreach (var process in processes)
        {
            try
            {
                process.Kill();
            }
            catch
            {
                // 已退出
            }

            process.Dispose();
        }

        GameRecords.Directory = originalDirectory;

        try
        {
            if (Directory.Exists(tempDirectory))
                Directory.Delete(tempDirectory, true);
        }
        catch (IOException)
        {
            // 失败的测试可能还拿着守护锁; 临时目录, 不影响结果
        }
    }

    private sealed class AdoptingRunner : ICatGameRunner
    {
        public CatAdoptRequest? Adopt { get; private set; }

        public TaskCompletionSource<int> Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<int> RunAsync(CatLaunchRequest request, ICatLaunchReporter reporter, CancellationToken cancellationToken) => Finish.Task;

        public Task<int> AdoptAsync(CatAdoptRequest request, ICatLaunchReporter reporter, CancellationToken cancellationToken)
        {
            Adopt = request;
            reporter.Adopted(request.Pid, request.ProcessStartedAt);
            reporter.Stage(CatStages.RUNNING);
            return Finish.Task;
        }

        public Task InjectAsync(bool dalamud, bool minion, bool force, ICatLaunchReporter reporter, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task CloseAsync(TimeSpan gracefulTimeout)
        {
            Finish.TrySetResult(0);
            return Task.CompletedTask;
        }
    }

    private CatLaunchHost NewHost(ICatGameRunner runner) =>
        new(runner, (method, parameters) => Record(method, parameters), new CatLogRedactor());

    private Task Record(string method, object parameters)
    {
        lock (events)
            events.Add((method, System.Text.Json.JsonSerializer.Serialize(parameters, CatProtocol.JsonOptions)));

        return Task.CompletedTask;
    }

    private Process StartSleeper()
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
        processes.Add(process);
        return process;
    }

    /// <summary>给 <paramref name="game" /> 写一份守护记录, 守护者默认是一个不存在的进程（原守护进程已死）</summary>
    private static GameRecord WriteRecord(int pid, DateTimeOffset startedAt, int? guardPid = null, DateTimeOffset? guardStartedAt = null, string? card = CARD)
    {
        var record = new GameRecord
        {
            Pid                       = pid,
            ProcessStartedAt          = startedAt,
            OperationId               = "op-old",
            Channel                   = GameRecordChannels.SDO,
            AccountName               = "acc",
            AccountUserName           = "acc",
            AreaName                  = "陆行鸟",
            Dalamud                   = true,
            DalamudRequested          = true,
            RestartNoPlugins          = true,
            DcTravelPort              = 51234,
            SndaId                    = "123456",
            MinionFingerprint         = card,
            MinionVariant             = card == null ? null : MinionCards.VARIANT_CN,
            AutoEnter                 = true,
            CharacterName             = "角色",
            CrashDialogTimeoutSeconds = 300,
            GuardPid                  = guardPid ?? UnusedPid(),
            GuardStartedAt            = guardStartedAt ?? DateTimeOffset.UtcNow.AddHours(-1),
            UpdatedAt                 = DateTimeOffset.UtcNow
        };

        Assert.True(GameRecords.Write(record));
        return record;
    }

    private static string Iso(DateTimeOffset value) => CatProtocol.FormatTimestamp(value);

    [Fact]
    public void InvalidParams_AreRejected()
    {
        var host = NewHost(new AdoptingRunner());

        Assert.Equal(CatCodes.INVALID_PARAMS, host.Adopt(null).Code);
        Assert.Equal(CatCodes.INVALID_PARAMS, host.Adopt(new CatAdoptParams("op", null, Iso(DateTimeOffset.UtcNow))).Code);
        Assert.Equal(CatCodes.INVALID_PARAMS, host.Adopt(new CatAdoptParams(" ", 1234, Iso(DateTimeOffset.UtcNow))).Code);
        Assert.Equal(CatCodes.INVALID_PARAMS, host.Adopt(new CatAdoptParams("op", 1234, "不是时间")).Code);
        Assert.Equal(CatCodes.INVALID_PARAMS, host.Adopt(new CatAdoptParams("op", 1234, Iso(DateTimeOffset.UtcNow), 0)).Code);
    }

    [Fact]
    public void NoRecord_IsGameNotFound()
    {
        using var game = StartSleeper();
        var host = NewHost(new AdoptingRunner());

        var result = host.Adopt(new CatAdoptParams("op", game.Id, Iso(MinionOccupancy.GetProcessStartedAt(game))));

        Assert.False(result.Accepted);
        Assert.Equal(CatCodes.GAME_NOT_FOUND, result.Code);
    }

    [Fact]
    public void ReusedPid_IsGameNotFound()
    {
        var game      = StartSleeper();
        var startedAt = MinionOccupancy.GetProcessStartedAt(game);
        WriteRecord(game.Id, startedAt.AddMinutes(-10));

        var result = NewHost(new AdoptingRunner()).Adopt(new CatAdoptParams("op", game.Id, Iso(startedAt)));

        Assert.Equal(CatCodes.GAME_NOT_FOUND, result.Code);
    }

    [Fact]
    public void LiveGuard_IsAlreadyGuarded()
    {
        var game      = StartSleeper();
        var startedAt = MinionOccupancy.GetProcessStartedAt(game);
        var record    = WriteRecord(game.Id, startedAt);

        // 原守护进程还拿着守护锁
        using var held = GameRecords.TryClaimGuard(game.Id, record.ProcessStartedAt);
        Assert.NotNull(held);

        var result = NewHost(new AdoptingRunner()).Adopt(new CatAdoptParams("op", game.Id, Iso(startedAt)));

        Assert.Equal(CatCodes.ALREADY_GUARDED, result.Code);
    }

    /// <summary>
    ///     两个进程同时接管同一个游戏（比如外壳超时重试）: 只能有一个成功, 不会出现两个守护进程同时重开同一个号
    /// </summary>
    [Fact]
    public async Task ConcurrentAdopts_OnlyOneWins()
    {
        var game      = StartSleeper();
        var startedAt = MinionOccupancy.GetProcessStartedAt(game);
        WriteRecord(game.Id, startedAt);

        var first  = new AdoptingRunner();
        var second = new AdoptingRunner();
        var hostA  = NewHost(first);
        var hostB  = NewHost(second);

        var results = await Task.WhenAll
        (
            Task.Run(() => hostA.Adopt(new CatAdoptParams("op-a", game.Id, Iso(startedAt)))),
            Task.Run(() => hostB.Adopt(new CatAdoptParams("op-b", game.Id, Iso(startedAt))))
        );

        Assert.Single(results, x => x.Accepted);
        Assert.Single(results, x => x.Code == CatCodes.ALREADY_GUARDED);

        // 赢的那个守完（游戏结束）后放掉锁, 之后又能接管
        var winner = results[0].Accepted ? hostA : hostB;
        first.Finish.TrySetResult(0);
        second.Finish.TrySetResult(0);
        await winner.Completion.WaitAsync(Timeout);
        Assert.False(GameRecords.IsGuarded(game.Id, startedAt));
    }

    [Fact]
    public void IncompleteRecord_IsGameNotFound()
    {
        var game      = StartSleeper();
        var startedAt = MinionOccupancy.GetProcessStartedAt(game);
        var record    = WriteRecord(game.Id, startedAt);
        GameRecords.Write(record with { SndaId = null });

        var result = NewHost(new AdoptingRunner()).Adopt(new CatAdoptParams("op", game.Id, Iso(startedAt)));

        Assert.Equal(CatCodes.GAME_NOT_FOUND, result.Code);
    }

    [Fact]
    public void ExitedGame_IsNotRunning()
    {
        var pid       = UnusedPid();
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-30);
        WriteRecord(pid, startedAt);

        var result = NewHost(new AdoptingRunner()).Adopt(new CatAdoptParams("op", pid, Iso(startedAt)));

        Assert.Equal(CatCodes.NOT_RUNNING, result.Code);
    }

    [Fact]
    public void DifferentCard_IsRejected()
    {
        var game      = StartSleeper();
        var startedAt = MinionOccupancy.GetProcessStartedAt(game);
        WriteRecord(game.Id, startedAt);

        var minion = new CatMinionParams("fedcba9876543210", MinionCards.VARIANT_CN, "KEY", new string('a', 32), "forum", "pass");
        var result = NewHost(new AdoptingRunner()).Adopt(new CatAdoptParams("op", game.Id, Iso(startedAt), null, minion));

        Assert.Equal(CatCodes.INVALID_PARAMS, result.Code);
    }

    [Fact]
    public async Task Accepted_RunnerGetsRecord_GameAdoptedIsPublished_LaunchIsThenRejected()
    {
        var game      = StartSleeper();
        var startedAt = MinionOccupancy.GetProcessStartedAt(game);
        var record    = WriteRecord(game.Id, startedAt);
        var runner    = new AdoptingRunner();
        var host      = NewHost(runner);

        var result = host.Adopt(new CatAdoptParams("op-new", game.Id, Iso(startedAt)));
        Assert.True(result.Accepted, result.Message);

        await WaitUntilAsync(() => runner.Adopt != null);
        Assert.Equal("op-new", runner.Adopt!.OperationId);
        Assert.Equal(game.Id, runner.Adopt.Pid);
        Assert.Equal(record.DcTravelPort, runner.Adopt.Record.DcTravelPort);
        Assert.Equal(300, runner.Adopt.CrashDialogTimeoutSeconds);
        Assert.Null(runner.Adopt.MinionCard);

        Assert.Equal("op-new", host.OperationId);
        Assert.Equal(CatCodes.ALREADY_LAUNCHED, host.Launch(new CatLaunchParams("op-2", "acc", false, null)).Code);
        // 同一个游戏再接管一次: 守护锁已在本进程手里
        Assert.Equal(CatCodes.ALREADY_GUARDED, host.Adopt(new CatAdoptParams("op-3", game.Id, Iso(startedAt))).Code);

        await host.DrainEventsAsync(Timeout);
        Assert.Equal(game.Id, host.GetStatus().Pid);

        lock (events)
        {
            var adopted = Assert.Single(events, x => x.Method == "game.adopted");
            Assert.Contains("\"operationId\":\"op-new\"", adopted.Json);
            Assert.Contains($"\"pid\":{game.Id}", adopted.Json);
        }

        runner.Finish.SetResult(0);
        Assert.Equal(0, await host.Completion.WaitAsync(Timeout));
    }

    [Fact]
    public void LaunchThenAdopt_IsRejected()
    {
        var game      = StartSleeper();
        var startedAt = MinionOccupancy.GetProcessStartedAt(game);
        WriteRecord(game.Id, startedAt);

        var host = NewHost(new AdoptingRunner());
        Assert.True(host.Launch(new CatLaunchParams("op-1", "acc", false, null)).Accepted);

        Assert.Equal(CatCodes.ALREADY_LAUNCHED, host.Adopt(new CatAdoptParams("op-2", game.Id, Iso(startedAt))).Code);
    }

    [Fact]
    public async Task RunnerWithoutAdoptSupport_ReportsUnsupported()
    {
        var game      = StartSleeper();
        var startedAt = MinionOccupancy.GetProcessStartedAt(game);
        WriteRecord(game.Id, startedAt);

        var host = NewHost(new FakeGameRunner());
        Assert.True(host.Adopt(new CatAdoptParams("op", game.Id, Iso(startedAt))).Accepted);

        Assert.Equal(CatLaunchHost.EXIT_LAUNCH_FAILED, await host.Completion.WaitAsync(Timeout));
        await host.DrainEventsAsync(Timeout);

        lock (events)
        {
            var failed = Assert.Single(events, x => x.Method == "launch.failed");
            Assert.Contains($"\"code\":\"{CatCodes.UNSUPPORTED}\"", failed.Json);
        }
    }

    private static int UnusedPid()
    {
        for (var pid = 4_000_000 - Random.Shared.Next(1000) * 4; pid > 1000; pid -= 4)
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

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;

        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException();

            await Task.Delay(20);
        }
    }
}
