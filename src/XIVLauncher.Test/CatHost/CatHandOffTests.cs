using XIVLauncher.CatHost;
using Xunit;

namespace XIVLauncher.Test.CatHost;

/// <summary>
///     handoff（交接停止）: 只在游戏稳定运行时接受; 被拒一切照旧; 接受后发 game.handedOff、以 EXIT_HANDED_OFF 结束, 之后不再发任何事件、不再接受 close / inject
/// </summary>
public sealed class CatHandOffTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static readonly DateTimeOffset StartedAt = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    private const int GAME_PID = 4242;

    private readonly List<(string Method, string Json)> events = [];

    private sealed class HandOffRunner : ICatGameRunner
    {
        private ICatLaunchReporter? reporter;

        public CatAcceptResult? PrepareResult { get; set; }

        public int PrepareCalls { get; private set; }

        public bool Closed { get; private set; }

        public TaskCompletionSource<int> Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ICatLaunchReporter Reporter => reporter!;

        public Task<int> RunAsync(CatLaunchRequest request, ICatLaunchReporter launchReporter, CancellationToken cancellationToken)
        {
            reporter = launchReporter;
            launchReporter.Started(GAME_PID, StartedAt);
            launchReporter.Stage(CatStages.RUNNING);
            return Finish.Task;
        }

        public Task<CatAcceptResult?> PrepareHandOffAsync(CancellationToken cancellationToken)
        {
            PrepareCalls++;
            return Task.FromResult(PrepareResult);
        }

        public Task InjectAsync(bool dalamud, bool minion, bool force, ICatLaunchReporter launchReporter, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task CloseAsync(TimeSpan gracefulTimeout)
        {
            Closed = true;
            Finish.TrySetResult(0);
            return Task.CompletedTask;
        }
    }

    private CatLaunchHost NewHost(ICatGameRunner runner) =>
        new(runner, Record, new CatLogRedactor());

    private Task Record(string method, object parameters)
    {
        lock (events)
            events.Add((method, System.Text.Json.JsonSerializer.Serialize(parameters, CatProtocol.JsonOptions)));

        return Task.CompletedTask;
    }

    private async Task<(CatLaunchHost Host, HandOffRunner Runner)> LaunchedAsync()
    {
        var runner = new HandOffRunner();
        var host   = NewHost(runner);

        Assert.True(host.Launch(new CatLaunchParams("op-1", "acc", true, null)).Accepted);
        await WaitUntilAsync(() => host.GetStatus().Stage == CatStages.RUNNING);
        return (host, runner);
    }

    [Fact]
    public async Task BeforeLaunch_IsNotRunning()
    {
        var host = NewHost(new HandOffRunner());

        Assert.Equal(CatCodes.NOT_RUNNING, (await host.HandOffAsync()).Code);
    }

    [Theory]
    [InlineData(CatStages.STARTING)]
    [InlineData(CatStages.ENTERING_LOBBY)]
    [InlineData(CatStages.AWAITING_CHARACTER_CHOICE)]
    [InlineData(CatStages.QUEUEING)]
    public async Task MidFlowStage_IsBusy_AndRunnerIsNotAsked(string stage)
    {
        var (host, runner) = await LaunchedAsync();
        runner.Reporter.Stage(stage);

        Assert.Equal(CatCodes.BUSY, (await host.HandOffAsync()).Code);
        Assert.Equal(0, runner.PrepareCalls);
    }

    [Fact]
    public async Task RunnerRefuses_EverythingStaysAsBefore()
    {
        var (host, runner) = await LaunchedAsync();
        runner.PrepareResult = CatAcceptResult.Rejected(CatCodes.BUSY, "游戏内跨区正在进行");

        var result = await host.HandOffAsync();
        Assert.Equal(CatCodes.BUSY, result.Code);
        Assert.False(host.Completion.IsCompleted);

        // 被拒后仍能正常下号, 也能再试交接
        runner.PrepareResult = null;
        Assert.True((await host.HandOffAsync()).Accepted);
        Assert.Equal(2, runner.PrepareCalls);
    }

    [Fact]
    public async Task Accepted_PublishesHandedOff_ExitsWithHandedOff_ThenStaysSilent()
    {
        var (host, runner) = await LaunchedAsync();

        Assert.True((await host.HandOffAsync()).Accepted);
        Assert.Equal(CatHostRuntime.EXIT_HANDED_OFF, await host.Completion.WaitAsync(Timeout));

        // 交接后: 不再关游戏、不再补注入、不能再交接一次
        Assert.Equal(CatCodes.BUSY, host.Close(new CatCloseParams(null)).Code);
        Assert.False(runner.Closed);
        Assert.Equal(CatCodes.BUSY, host.Inject(new CatInjectParams(true, null)).Code);
        Assert.Equal(CatCodes.BUSY, (await host.HandOffAsync()).Code);

        // 启动器里残留的动静（例如游戏恰好在这时崩了）不再往外发
        runner.Reporter.Crashed(GAME_PID);
        runner.Reporter.Exited(GAME_PID, 1);

        await host.DrainEventsAsync(Timeout);

        lock (events)
        {
            var handedOff = Assert.Single(events, x => x.Method == "game.handedOff");
            Assert.Contains("\"operationId\":\"op-1\"", handedOff.Json);
            Assert.Contains($"\"pid\":{GAME_PID}", handedOff.Json);
            Assert.Contains($"\"processStartedAt\":\"{CatProtocol.FormatTimestamp(StartedAt)}\"", handedOff.Json);
            Assert.Equal("game.handedOff", events[^1].Method);
            Assert.DoesNotContain(events, x => x.Method is "game.exited" or "game.crashed");
        }
    }

    [Fact]
    public async Task InWorld_IsAccepted()
    {
        var (host, runner) = await LaunchedAsync();
        runner.Reporter.Stage(CatStages.IN_WORLD);

        Assert.True((await host.HandOffAsync()).Accepted);
    }

    [Fact]
    public async Task AfterClose_IsClosing()
    {
        var (host, _) = await LaunchedAsync();
        Assert.True(host.Close(new CatCloseParams(1)).Accepted);

        Assert.Equal(CatCodes.CLOSING, (await host.HandOffAsync()).Code);
    }

    [Fact]
    public async Task RunnerWithoutSupport_IsUnsupported()
    {
        var runner = new FakeGameRunner();
        var host   = NewHost(runner);
        Assert.True(host.Launch(new CatLaunchParams("op-1", "acc", false, null)).Accepted);
        await runner.Started.Task.WaitAsync(Timeout);
        runner.Reporter!.Started(GAME_PID, StartedAt);
        runner.Reporter.Stage(CatStages.RUNNING);

        Assert.Equal(CatCodes.UNSUPPORTED, (await host.HandOffAsync()).Code);
        Assert.False(host.Completion.IsCompleted);
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
