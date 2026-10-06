using System.Diagnostics;
using XIVLauncher.CatHost;
using Xunit;

namespace XIVLauncher.Test.CatHost;

public sealed class CatSimulatedGameRunnerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly CatSimulatedGameRunner runner = new()
    {
        StepDelay          = TimeSpan.FromMilliseconds(10),
        CrashAfter         = TimeSpan.FromMilliseconds(200),
        CrashDialogTimeout = TimeSpan.FromMilliseconds(200)
    };

    [Fact]
    public async Task Run_EmitsEventsInRealOrder_AndExitsWhenPlaceholderIsKilled()
    {
        var reporter = new RecordingReporter();
        var run      = runner.RunAsync(new CatLaunchRequest("op", "acc", true, "0123456789abcdef", "cn"), reporter, CancellationToken.None);

        await reporter.Running.Task.WaitAsync(Timeout);
        Assert.False(run.IsCompleted);

        using (var placeholder = Process.GetProcessById(reporter.Pid!.Value))
            placeholder.Kill();

        Assert.Equal(0, await run.WaitAsync(Timeout));
        Assert.Equal
        (
            [
                "stage:preparing",
                "stage:updatingDalamud",
                "stage:starting",
                "started",
                "stage:injecting",
                "agent:dalamud:ok",
                "stage:attachingMinion",
                "agent:minion:ok",
                "stage:running",
                "exited"
            ],
            reporter.Entries
        );
    }

    [Fact]
    public async Task Run_WithoutAgents_SkipsInjectionStages()
    {
        var reporter = new RecordingReporter();
        var run      = runner.RunAsync(new CatLaunchRequest("op", "acc", false, null, null), reporter, CancellationToken.None);

        await reporter.Running.Task.WaitAsync(Timeout);

        using (var placeholder = Process.GetProcessById(reporter.Pid!.Value))
            placeholder.Kill();

        await run.WaitAsync(Timeout);
        Assert.Equal(["stage:preparing", "stage:starting", "started", "stage:running", "exited"], reporter.Entries);
    }

    [Fact]
    public async Task Run_WeGamePlatform_EmitsSameEventsAsShengqu()
    {
        var reporter = new RecordingReporter();
        var run = runner.RunAsync
        (
            new CatLaunchRequest("op", "acc", false, null, null, Platform: XIVLauncher.Common.Game.XIVAccountType.WeGame),
            reporter,
            CancellationToken.None
        );

        await reporter.Running.Task.WaitAsync(Timeout);

        using (var placeholder = Process.GetProcessById(reporter.Pid!.Value))
            placeholder.Kill();

        await run.WaitAsync(Timeout);
        Assert.Equal(["stage:preparing", "stage:starting", "started", "stage:running", "exited"], reporter.Entries);
    }

    [Fact]
    public async Task Run_International_EmitsSameEventsAsShengqu_AndNeverPrintsPassword()
    {
        const string PASSWORD = "Sim-Pass-9981";
        var reporter = new RecordingReporter();
        var run = runner.RunAsync
        (
            new CatLaunchRequest("op", "seAccount", true, "0123456789abcdef", "global", IsInternational: true, Password: new CatSecret(PASSWORD)),
            reporter,
            CancellationToken.None
        );

        await reporter.Running.Task.WaitAsync(Timeout);

        using (var placeholder = Process.GetProcessById(reporter.Pid!.Value))
            placeholder.Kill();

        await run.WaitAsync(Timeout);
        Assert.Equal
        (
            [
                "stage:preparing",
                "stage:updatingDalamud",
                "stage:starting",
                "started",
                "stage:injecting",
                "agent:dalamud:ok",
                "stage:attachingMinion",
                "agent:minion:ok",
                "stage:running",
                "exited"
            ],
            reporter.Entries
        );
        Assert.Contains(reporter.Messages, x => x.Contains("国际服"));
        Assert.DoesNotContain(reporter.Messages, x => x.Contains(PASSWORD));
    }

    [Fact]
    public async Task Run_MissingCard_FailsWithMinionCardNotFound_BeforeStarting()
    {
        var reporter = new RecordingReporter();

        var exitCode = await runner.RunAsync
                                   (
                                       new CatLaunchRequest("op", "acc", false, CatSimulatedGameRunner.MISSING_CARD_FINGERPRINT, "cn"),
                                       reporter,
                                       CancellationToken.None
                                   )
                                   .WaitAsync(Timeout);

        Assert.Equal(CatLaunchHost.EXIT_LAUNCH_FAILED, exitCode);
        Assert.Equal(["stage:preparing", "failed:minionCardNotFound"], reporter.Entries);
    }

    [Fact]
    public async Task Run_FailPrefixedAccount_FailsWithGivenCode()
    {
        var reporter = new RecordingReporter();

        await runner.RunAsync(new CatLaunchRequest("op", "fail:authorizationRequired", true, null, null), reporter, CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(["stage:preparing", "failed:authorizationRequired"], reporter.Entries);
    }

    [Fact]
    public async Task Run_NetFail_FailsWithNetworkError()
    {
        var reporter = new RecordingReporter();

        var exitCode = await runner.RunAsync(new CatLaunchRequest("op", "netfail", true, null, null), reporter, CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(CatLaunchHost.EXIT_LAUNCH_FAILED, exitCode);
        Assert.Equal(["stage:preparing", "failed:networkError"], reporter.Entries);
    }

    [Theory]
    [InlineData("dalamud", "agent:dalamud:dalamudUnavailable")]
    [InlineData("minion", "agent:minion:attachFailed")]
    public async Task Run_AgentFail_ReportsAgentFailure_AndKeepsRunning(string kind, string expected)
    {
        var reporter = new RecordingReporter();
        var run      = runner.RunAsync(new CatLaunchRequest("op", $"agentfail:{kind}", true, "0123456789abcdef", "cn"), reporter, CancellationToken.None);

        await reporter.Running.Task.WaitAsync(Timeout);
        Assert.Contains(expected, reporter.Entries);

        await runner.CloseAsync(TimeSpan.Zero);
        await run.WaitAsync(Timeout);
        Assert.Equal("exited:closed", reporter.Entries.Last());
    }

    [Fact]
    public async Task Run_CrashRestart_EmitsRestartedWithNewPid_ThenAgentsAgain()
    {
        var reporter = new RecordingReporter();
        var run      = runner.RunAsync(new CatLaunchRequest("op", "crash:restart", true, null, null), reporter, CancellationToken.None);

        await reporter.Restart.Task.WaitAsync(Timeout);
        await WaitUntilAsync(() => reporter.Entries.Count(x => x == "stage:running") == 2);

        using (var placeholder = Process.GetProcessById(reporter.Pid!.Value))
            placeholder.Kill();

        Assert.Equal(0, await run.WaitAsync(Timeout));
        Assert.Equal(2, reporter.Entries.Count(x => x == "agent:dalamud:ok"));
        Assert.Equal("exited", reporter.Entries.Last());
    }

    [Fact]
    public async Task Run_CrashDialog_EmitsCrashed_ThenExitsAfterTimeout()
    {
        var reporter = new RecordingReporter();

        var exitCode = await runner.RunAsync(new CatLaunchRequest("op", "crash:dialog", true, null, null), reporter, CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(0, exitCode);
        Assert.Equal(["crashed", "exited:crashDialogTimeout"], reporter.Entries.TakeLast(2));
        Assert.DoesNotContain("restarted", reporter.Entries);
    }

    [Fact]
    public async Task Close_WhileRunning_ExitsWithReasonClosed()
    {
        var reporter = new RecordingReporter();
        var run      = runner.RunAsync(new CatLaunchRequest("op", "acc", false, null, null), reporter, CancellationToken.None);

        await reporter.Running.Task.WaitAsync(Timeout);
        await runner.CloseAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(0, await run.WaitAsync(Timeout));
        Assert.Equal("exited:closed", reporter.Entries.Last());
    }

    [Fact]
    public async Task Close_BeforeGameStarts_FailsWithCancelled()
    {
        var slow     = new CatSimulatedGameRunner { StepDelay = TimeSpan.FromSeconds(5) };
        var reporter = new RecordingReporter();
        var run      = slow.RunAsync(new CatLaunchRequest("op", "acc", true, null, null), reporter, CancellationToken.None);

        await WaitUntilAsync(() => reporter.Entries.Contains("stage:preparing"));
        await slow.CloseAsync(TimeSpan.Zero);

        Assert.Equal(CatLaunchHost.EXIT_LAUNCH_FAILED, await run.WaitAsync(Timeout));
        Assert.Equal("failed:cancelled", reporter.Entries.Last());
        Assert.DoesNotContain("started", reporter.Entries);
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
