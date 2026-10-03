using System.Diagnostics;
using XIVLauncher.CatHost;
using Xunit;

namespace XIVLauncher.Test.CatHost;

public sealed class CatSimulatedGameRunnerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly CatSimulatedGameRunner runner = new() { StepDelay = TimeSpan.FromMilliseconds(10) };

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
}
