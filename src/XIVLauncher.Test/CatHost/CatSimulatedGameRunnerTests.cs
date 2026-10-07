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
        WeGameLoginDelay   = TimeSpan.FromMilliseconds(50),
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
    public async Task Run_WeGameLogin_WaitsForWeGameLoginFirst_ThenContinuesAsUsual()
    {
        var reporter = new RecordingReporter();
        var run = runner.RunAsync
        (
            new CatLaunchRequest("op", "acc", false, null, null, Platform: XIVLauncher.Common.Game.XIVAccountType.WeGame, WeGameLogin: true),
            reporter,
            CancellationToken.None
        );

        await reporter.Running.Task.WaitAsync(Timeout);

        using (var placeholder = Process.GetProcessById(reporter.Pid!.Value))
            placeholder.Kill();

        await run.WaitAsync(Timeout);
        Assert.Equal
        (
            ["stage:preparing", "stage:waitingWeGameLogin", "stage:preparing", "stage:starting", "started", "stage:running", "exited"],
            reporter.Entries
        );
    }

    [Fact]
    public async Task Run_WeGameLogin_CloseWhileWaiting_ReportsCancelled()
    {
        var waiting  = new CatSimulatedGameRunner { StepDelay = TimeSpan.FromMilliseconds(10), WeGameLoginDelay = TimeSpan.FromMinutes(5) };
        var reporter = new RecordingReporter();
        var run = waiting.RunAsync
        (
            new CatLaunchRequest("op", "acc", false, null, null, Platform: XIVLauncher.Common.Game.XIVAccountType.WeGame, WeGameLogin: true),
            reporter,
            CancellationToken.None
        );

        var deadline = DateTime.UtcNow + Timeout;

        while (!reporter.Entries.Contains("stage:waitingWeGameLogin") && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        await waiting.CloseAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(CatLaunchHost.EXIT_LAUNCH_FAILED, await run.WaitAsync(Timeout));
        Assert.Equal(["stage:preparing", "stage:waitingWeGameLogin", "failed:cancelled"], reporter.Entries);
    }

    [Theory]
    [InlineData(CatWeGameScan.Qq)]
    [InlineData(CatWeGameScan.WeChat)]
    public async Task Run_WeGameScan_SendsAFakeQrChallenge_AndClearsItBeforeContinuing(CatWeGameScan scan)
    {
        var reporter = new RecordingReporter();
        var run      = runner.RunAsync(WeGameScanRequest("acc", scan), reporter, CancellationToken.None);

        await reporter.Running.Task.WaitAsync(Timeout);

        using (var placeholder = Process.GetProcessById(reporter.Pid!.Value))
            placeholder.Kill();

        await run.WaitAsync(Timeout);
        Assert.Equal
        (
            [
                "stage:preparing", "stage:waitingWeGameLogin", "challenge:qrcode:q-1", "cleared:q-1", "stage:preparing",
                "stage:starting", "started", "stage:running", "exited"
            ],
            reporter.Entries
        );

        var challenge = Assert.Single(reporter.Challenges);
        Assert.Equal(CatSimulatedGameRunner.SIMULATED_QR_LINK, challenge.Link);
        Assert.Equal(120, challenge.ExpiresInSeconds);

        // image 是一张真的 PNG
        Assert.Equal([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], Convert.FromBase64String(challenge.Image!).Take(8).ToArray());
    }

    [Fact]
    public async Task Run_WeGameScan_ScanFailAccount_SendsSwitchFailedInsteadOfQr()
    {
        var reporter = new RecordingReporter();
        var run      = runner.RunAsync(WeGameScanRequest("scanfail:acc", CatWeGameScan.WeChat), reporter, CancellationToken.None);

        await reporter.Running.Task.WaitAsync(Timeout);

        using (var placeholder = Process.GetProcessById(reporter.Pid!.Value))
            placeholder.Kill();

        await run.WaitAsync(Timeout);
        Assert.Equal
        (
            ["stage:preparing", "stage:waitingWeGameLogin", "scanSwitchFailed:weChat", "stage:preparing", "stage:starting", "started", "stage:running", "exited"],
            reporter.Entries
        );
    }

    [Fact]
    public async Task Run_WeGameScan_SmsAccount_WaitsForConfirmSms()
    {
        var reporter = new RecordingReporter();
        var run      = runner.RunAsync(WeGameScanRequest("sms:acc", CatWeGameScan.Qq), reporter, CancellationToken.None);

        // 还没到短信那一步: 没有可确认的
        Assert.Equal(CatCodes.NOT_RUNNING, runner.ConfirmWeGameSms("s-1").Code);

        var deadline = DateTime.UtcNow + Timeout;

        while (!reporter.Entries.Contains("challenge:sms:s-1") && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        // 没收到确认之前一直停在这里
        await Task.Delay(200);
        Assert.Equal(["stage:preparing", "stage:waitingWeGameLogin", "challenge:qrcode:q-1", "cleared:q-1", "challenge:sms:s-1"], reporter.Entries);
        Assert.Equal("1069070069", reporter.Challenges.Last().Phone);

        Assert.Equal(CatCodes.NOT_RUNNING, runner.ConfirmWeGameSms("s-2").Code);
        Assert.True(runner.ConfirmWeGameSms("s-1").Accepted);

        await reporter.Running.Task.WaitAsync(Timeout);

        using (var placeholder = Process.GetProcessById(reporter.Pid!.Value))
            placeholder.Kill();

        await run.WaitAsync(Timeout);
        Assert.Equal
        (
            [
                "stage:preparing", "stage:waitingWeGameLogin", "challenge:qrcode:q-1", "cleared:q-1", "challenge:sms:s-1", "cleared:s-1", "stage:preparing",
                "stage:starting", "started", "stage:running", "exited"
            ],
            reporter.Entries
        );
    }

    [Fact]
    public async Task Run_WeGameScan_CloseWhileChallengeIsOpen_ClearsIt_AndReportsCancelled()
    {
        var waiting  = new CatSimulatedGameRunner { StepDelay = TimeSpan.FromMilliseconds(10), WeGameLoginDelay = TimeSpan.FromMilliseconds(200) };
        var reporter = new RecordingReporter();
        var run      = waiting.RunAsync(WeGameScanRequest("sms:acc", CatWeGameScan.Qq), reporter, CancellationToken.None);

        var deadline = DateTime.UtcNow + Timeout;

        while (!reporter.Entries.Contains("challenge:sms:s-1") && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        await waiting.CloseAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(CatLaunchHost.EXIT_LAUNCH_FAILED, await run.WaitAsync(Timeout));
        Assert.Equal
        (
            ["stage:preparing", "stage:waitingWeGameLogin", "challenge:qrcode:q-1", "cleared:q-1", "challenge:sms:s-1", "cleared:s-1", "failed:cancelled"],
            reporter.Entries
        );
    }

    [Fact]
    public async Task Run_WeGameScanPrefixes_WithoutWeGameScan_ChangeNothing()
    {
        var reporter = new RecordingReporter();
        var run = runner.RunAsync
        (
            new CatLaunchRequest("op", "sms:acc", false, null, null, Platform: XIVLauncher.Common.Game.XIVAccountType.WeGame, WeGameLogin: true),
            reporter,
            CancellationToken.None
        );

        await reporter.Running.Task.WaitAsync(Timeout);

        using (var placeholder = Process.GetProcessById(reporter.Pid!.Value))
            placeholder.Kill();

        await run.WaitAsync(Timeout);
        Assert.Equal
        (
            ["stage:preparing", "stage:waitingWeGameLogin", "stage:preparing", "stage:starting", "started", "stage:running", "exited"],
            reporter.Entries
        );
    }

    private static CatLaunchRequest WeGameScanRequest(string account, CatWeGameScan scan) =>
        new("op", account, false, null, null, Platform: XIVLauncher.Common.Game.XIVAccountType.WeGame, WeGameLogin: true, WeGameScan: scan);

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

    private static CatLaunchRequest AutoEnterRequest(string account, bool minion = false, string? character = null, string? homeWorld = null) =>
        new("op", account, false, minion ? "0123456789abcdef" : null, minion ? "cn" : null, AutoEnter: true, CharacterName: character, CharacterHomeWorld: homeWorld);

    private async Task<string[]> RunAutoEnterUntilAsync(CatLaunchRequest request, RecordingReporter reporter, string lastEntry, Func<Task>? midway = null)
    {
        var run = runner.RunAsync(request, reporter, CancellationToken.None);

        if (midway != null)
            await midway();

        await WaitUntilAsync(() => reporter.Entries.Contains(lastEntry));

        // 再等几个节拍, 确认后面没有多余的事件
        await Task.Delay(100);

        using (var placeholder = Process.GetProcessById(reporter.Pid!.Value))
            placeholder.Kill();

        Assert.Equal(0, await run.WaitAsync(Timeout));
        return [.. reporter.Entries];
    }

    [Fact]
    public async Task AutoEnter_SingleCharacter_GoesStraightIn()
    {
        var reporter = new RecordingReporter();
        var entries  = await RunAutoEnterUntilAsync(AutoEnterRequest("acc", character: "小白", homeWorld: "HongYuHai"), reporter, "stage:inWorld");

        Assert.Equal
        (
            [
                "stage:preparing", "stage:starting", "started", "stage:running",
                "stage:enteringLobby", "characters:auto:1", "stage:enteringWorld", "character:小白@HongYuHai", "stage:inWorld",
                "exited"
            ],
            entries
        );

        var entered = Assert.Single(reporter.EnteredCharacters);
        Assert.Equal(CatSimulatedGameRunner.SIMULATED_CONTENT_ID_PREFIX + "1", entered.ContentId);
        Assert.Equal("红玉海", entered.HomeWorldName);
    }

    [Fact]
    public async Task AutoEnter_WithMinion_AttachesMinionBeforeEnteringTheLobby()
    {
        var reporter = new RecordingReporter();
        var entries  = await RunAutoEnterUntilAsync(AutoEnterRequest("acc", true), reporter, "stage:inWorld");

        Assert.Equal
        (
            [
                "stage:preparing", "stage:starting", "started",
                "stage:attachingMinion", "agent:minion:ok", "stage:running",
                "stage:enteringLobby", "characters:auto:1", "stage:enteringWorld", "character:模拟角色@LaNuoXiYa", "stage:inWorld",
                "exited"
            ],
            entries
        );
    }

    [Fact]
    public async Task AutoEnter_SeveralCharacters_WaitsForSelectCharacter()
    {
        var reporter = new RecordingReporter();

        Assert.Equal(CatCodes.NOT_RUNNING, ((ICatGameRunner)runner).SelectCharacter(CatSimulatedGameRunner.SIMULATED_CONTENT_ID_PREFIX + "2").Code);

        var entries = await RunAutoEnterUntilAsync
                      (
                          AutoEnterRequest(CatSimulatedGameRunner.CHARACTERS_PREFIX + "acc", true),
                          reporter,
                          "stage:inWorld",
                          async () =>
                          {
                              await WaitUntilAsync(() => reporter.Entries.Contains("characters:choose:3"));
                              await Task.Delay(100);

                              // 等人选的时候不往下走; Minion 在这之前已经挂好
                              Assert.Equal("characters:choose:3", reporter.Entries.Last());
                              Assert.Contains("agent:minion:ok", reporter.Entries);

                              Assert.Equal(CatCodes.INVALID_PARAMS, runner.SelectCharacter("999").Code);
                              Assert.True(runner.SelectCharacter(CatSimulatedGameRunner.SIMULATED_CONTENT_ID_PREFIX + "2").Accepted);
                          }
                      );

        Assert.Equal
        (
            [
                "stage:preparing", "stage:starting", "started",
                "stage:attachingMinion", "agent:minion:ok", "stage:running",
                "stage:enteringLobby", "stage:awaitingCharacterChoice", "characters:choose:3",
                "stage:enteringWorld", "character:模拟角色二@HongYuHai", "stage:inWorld",
                "exited"
            ],
            entries
        );
        Assert.Equal(CatCodes.NOT_RUNNING, runner.SelectCharacter(CatSimulatedGameRunner.SIMULATED_CONTENT_ID_PREFIX + "1").Code);
    }

    [Fact]
    public async Task AutoEnter_Travelling_SwitchesAreaFirst()
    {
        var reporter = new RecordingReporter();
        var entries  = await RunAutoEnterUntilAsync(AutoEnterRequest(CatSimulatedGameRunner.TRAVEL_PREFIX + "acc"), reporter, "stage:inWorld");

        Assert.Equal
        (
            [
                "stage:preparing", "stage:starting", "started", "stage:running",
                "stage:enteringLobby", "characters:auto:1", "stage:switchingArea", "characters:auto:1",
                "stage:enteringWorld", "character:模拟角色@LaNuoXiYa", "stage:inWorld",
                "exited"
            ],
            entries
        );
        Assert.True(reporter.CharacterLists.First().Characters[0].Travelling);
        Assert.Equal("BaiYinXiang", reporter.EnteredCharacters.Single().CurrentWorld);
    }

    [Fact]
    public async Task AutoEnter_Queue_ReportsPositions()
    {
        var reporter = new RecordingReporter();
        var entries  = await RunAutoEnterUntilAsync(AutoEnterRequest(CatSimulatedGameRunner.QUEUE_PREFIX + "acc"), reporter, "stage:inWorld");

        Assert.Equal
        (
            [
                "stage:preparing", "stage:starting", "started", "stage:running",
                "stage:enteringLobby", "characters:auto:1", "stage:enteringWorld", "queue:3", "queue:1", "stage:enteringWorld",
                "character:模拟角色@LaNuoXiYa", "stage:inWorld",
                "exited"
            ],
            entries
        );
    }

    [Theory]
    [InlineData("stop:", "moduleUnavailable")]
    [InlineData("stop:lobbyError", "lobbyError")]
    public async Task AutoEnter_Stop_LeavesTheGameRunning_WithMinionAlreadyAttached(string account, string code)
    {
        var reporter = new RecordingReporter();
        var entries  = await RunAutoEnterUntilAsync(AutoEnterRequest(account, true), reporter, $"stopped:{code}");

        Assert.Equal
        (
            [
                "stage:preparing", "stage:starting", "started",
                "stage:attachingMinion", "agent:minion:ok", "stage:running",
                "stage:enteringLobby", $"stopped:{code}", "stage:running",
                "exited"
            ],
            entries
        );
    }

    [Fact]
    public async Task AutoEnter_PlaceholderKilledWhileWaitingForChoice_ExitsCleanly()
    {
        var reporter = new RecordingReporter();
        var run      = runner.RunAsync(AutoEnterRequest(CatSimulatedGameRunner.CHARACTERS_PREFIX + "acc"), reporter, CancellationToken.None);

        await WaitUntilAsync(() => reporter.Entries.Contains("characters:choose:3"));

        using (var placeholder = Process.GetProcessById(reporter.Pid!.Value))
            placeholder.Kill();

        Assert.Equal(0, await run.WaitAsync(Timeout));
        Assert.Equal("exited", reporter.Entries.Last());
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
