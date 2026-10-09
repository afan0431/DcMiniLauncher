using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using XIVLauncher.CatHost;
using XIVLauncher.Common;
using XIVLauncher.Common.Game;
using XIVLauncher.Common.Game.Exceptions;
using XIVLauncher.Common.Game.International;
using XIVLauncher.Dalamud;
using XIVLauncher.Minion;
using XIVLauncher.Test.CatHost;
using Xunit;

namespace XIVLauncher.Test.International;

/// <summary>
///     国际服启动器对着假环境跑: 不联网、不登录、不启动游戏（游戏用一个 ping 占位进程代替）
/// </summary>
[Collection(SerilogCaptureCollection.NAME)]
public sealed class CatInternationalGameRunnerTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private const string ACCOUNT   = "seAccountName";
    private const string PASSWORD  = "Pw-国际服-7731!";
    private const string UNIQUE_ID = "uid9876543210fedcba9876543210fedcba";
    private const string CARD      = "0123456789abcdef";
    private const string KEYCODE   = "FFXIVXFAKE1111111111111111111111";
    private const string MINION_ID = "0123456789abcdef0123456789abcdef";
    private const string FORUM_ID  = "fake-forum-user";
    private const string FORUM_PW  = "fake-forum-pass!7";

    private readonly FakeGameDirectory game = new();
    private readonly FakeEnvironment   environment;
    private readonly CatLogRedactor    redactor = new();
    private readonly RecordingReporter reporter = new();

    public CatInternationalGameRunnerTests()
    {
        File.WriteAllText(Path.Combine(game.Root.FullName, "game", "ffxiv_dx11.exe"), "exe");
        environment = new FakeEnvironment(game.Root.FullName);
    }

    public void Dispose()
    {
        environment.KillGame();
        game.Dispose();
    }

    private CatInternationalGameRunner NewRunner() =>
        new(redactor, environment) { DalamudInjectTimeout = TimeSpan.FromSeconds(5) };

    private static CatLaunchRequest Request(bool dalamud = false, bool minion = false, string account = ACCOUNT, string? password = PASSWORD) =>
        new
        (
            "op-intl",
            account,
            dalamud,
            minion ? new CatMinionLaunch(CARD, MinionCards.VARIANT_GLOBAL, new CatSecret(KEYCODE), MINION_ID, FORUM_ID, new CatSecret(FORUM_PW)) : null,
            IsInternational: true,
            Password: password == null ? null : new CatSecret(password)
        );

    private async Task<int> RunToFailureAsync(CatLaunchRequest request) =>
        await NewRunner().RunAsync(request, reporter, CancellationToken.None).WaitAsync(Timeout);

    private void AssertFailed(string code, string? messagePart = null)
    {
        Assert.Equal($"failed:{code}", reporter.Entries.Last());
        Assert.DoesNotContain("started", reporter.Entries);
        Assert.Null(environment.GameProcess);

        if (messagePart != null)
            Assert.Contains(messagePart, reporter.Messages.Last());

        AssertNothingSensitive();
    }

    private void AssertNothingSensitive()
    {
        foreach (var message in reporter.Messages)
        {
            Assert.DoesNotContain(PASSWORD, message);
            Assert.DoesNotContain(UNIQUE_ID, message);
            Assert.DoesNotContain(KEYCODE, message);
            Assert.DoesNotContain(FORUM_PW, message);

            // 给员工看的话不出现内部词
            foreach (var word in new[] { "令牌", "凭证", "sid", "unique id", "UniqueId", "_STORED_" })
                Assert.DoesNotContain(word, message, StringComparison.OrdinalIgnoreCase);
        }
    }

    #region 起进程之前的每个失败出口

    [Fact]
    public async Task EmailAsAccountName_IsAuthorizationRequired_BeforeAnyNetwork()
    {
        Assert.Equal(CatLaunchHost.EXIT_LAUNCH_FAILED, await RunToFailureAsync(Request(account: "someone@example.com")));

        AssertFailed(CatCodes.AUTHORIZATION_REQUIRED, "不能填邮箱");
        Assert.Empty(environment.Calls);
    }

    [Fact]
    public async Task MissingPassword_IsAuthorizationRequired()
    {
        await RunToFailureAsync(Request(password: null));

        AssertFailed(CatCodes.AUTHORIZATION_REQUIRED, "密码");
        Assert.Empty(environment.Calls);
    }

    [Fact]
    public async Task InitializationFailure_IsLaunchFailed()
    {
        environment.InitializationError = new InvalidOperationException("配置文件读不了");

        await RunToFailureAsync(Request());

        AssertFailed(CatCodes.LAUNCH_FAILED, "初始化失败");
    }

    [Fact]
    public async Task GamePath_NotSet_IsInvalidGamePath()
    {
        environment.Settings = environment.Settings with { GamePath = null };

        await RunToFailureAsync(Request());

        AssertFailed(CatCodes.INVALID_GAME_PATH, "还没有设置国际服游戏目录");
        Assert.Empty(environment.Calls);
    }

    [Fact]
    public async Task GamePath_IsChineseClient_IsInvalidGamePath()
    {
        Directory.CreateDirectory(Path.Combine(game.Root.FullName, "sdo"));

        await RunToFailureAsync(Request());

        AssertFailed(CatCodes.INVALID_GAME_PATH, "国服客户端");
    }

    [Fact]
    public async Task MinionNotConfigured_FailsBeforeLogin()
    {
        environment.FakeMinion.CheckError = (CatCodes.MINION_NOT_CONFIGURED, "找不到 Minion 安装目录");

        await RunToFailureAsync(Request(minion: true));

        AssertFailed(CatCodes.MINION_NOT_CONFIGURED);
        Assert.Empty(environment.Calls);
    }

    [Fact]
    public async Task KillSwitch_LocalBootNewerThanCutOff_IsLaunchFailed_WithoutLogin()
    {
        environment.Config = new InternationalClientConfig("https://x/{0}/{1}", "2026.01.01.0000.0000", InternationalClientConfigSource.Remote);

        await RunToFailureAsync(Request());

        AssertFailed(CatCodes.LAUNCH_FAILED, "登录方式有变动");
        Assert.Equal(["config"], environment.Calls);
    }

    [Fact]
    public async Task BootUpdateRequired_IsGameUpdateRequired()
    {
        environment.Login.BootUpdateRequired = true;

        await RunToFailureAsync(Request());

        AssertFailed(CatCodes.GAME_UPDATE_REQUIRED, "请在这台电脑上用官方启动器更新国际服客户端");
        Assert.Equal(["config", "boot"], environment.Calls);
        Assert.True(environment.Login.Disposed);
    }

    [Fact]
    public async Task BootCheck_NetworkError_IsNetworkError()
    {
        environment.Login.BootError = new HttpRequestException("No such host is known.");

        await RunToFailureAsync(Request());

        AssertFailed(CatCodes.NETWORK_ERROR);
    }

    [Fact]
    public async Task LoginService_Closed_IsLaunchFailedMaintenance()
    {
        environment.Login.LoginStatus = false;

        await RunToFailureAsync(Request());

        AssertFailed(CatCodes.LAUNCH_FAILED, "国际服正在维护");
        Assert.Equal(["config", "boot", "loginStatus"], environment.Calls);
    }

    [Fact]
    public async Task LoginService_Unreachable_IsNetworkError()
    {
        environment.Login.LoginStatusError = new TaskCanceledException("timeout");

        await RunToFailureAsync(Request());

        AssertFailed(CatCodes.NETWORK_ERROR);
    }

    [Fact]
    public async Task DalamudUnavailable_IsReportedAfterTheSingleLogin_AndNoGameIsStarted()
    {
        environment.DalamudError = "国际服的 Dalamud 还没有适配现在的游戏版本";

        await RunToFailureAsync(Request(dalamud: true));

        AssertFailed(CatCodes.DALAMUD_UNAVAILABLE, "还没有适配");
        Assert.Equal(["config", "boot", "loginStatus", "login", "gate", "dalamud"], environment.Calls);
        Assert.Equal(["stage:preparing", "stage:updatingDalamud", $"failed:{CatCodes.DALAMUD_UNAVAILABLE}"], reporter.Entries);
    }

    /// <summary>登录在 Dalamud 准备之前: 密码不对立刻报, 不去下载 Dalamud</summary>
    [Fact]
    public async Task LoginRejected_WithDalamudRequested_FailsWithoutPreparingDalamud()
    {
        environment.Login.LoginError = new InternationalLoginRejectedException("ID or password is incorrect.");

        await RunToFailureAsync(Request(dalamud: true));

        AssertFailed(CatCodes.AUTHORIZATION_REQUIRED, "国际服登录被拒绝");
        Assert.DoesNotContain("dalamud", environment.Calls);
        Assert.DoesNotContain("stage:updatingDalamud", reporter.Entries);
    }

    [Fact]
    public async Task UnexpectedLoginError_IsLaunchFailed_WithPlainMessage_AndDetailsOnlyInLocalLog()
    {
        environment.Login.LoginError = new InvalidOperationException("Sequence contains no elements");

        using var logs = new CapturedLogs();
        await RunToFailureAsync(Request());

        AssertFailed(CatCodes.LAUNCH_FAILED, "没预料到的错误");
        Assert.DoesNotContain(reporter.Messages, x => x.Contains("InvalidOperationException") || x.Contains("Sequence contains"));
        Assert.Contains("InvalidOperationException", logs.All);
    }

    [Fact]
    public async Task LoginRejected_IsAuthorizationRequired_WithSeTextRedacted()
    {
        // SE 的原文里万一带着密码, 也要被遮住
        environment.Login.LoginError = new InternationalLoginRejectedException($"ID or password is incorrect.\\r\\nYou entered {PASSWORD}");

        await RunToFailureAsync(Request());

        AssertFailed(CatCodes.AUTHORIZATION_REQUIRED, "国际服登录被拒绝: ID or password is incorrect. You entered ***");
        Assert.Equal(["config", "boot", "loginStatus", "login"], environment.Calls);
        Assert.Equal((ACCOUNT, PASSWORD, ClientLanguage.German), environment.Login.LoginArguments);
    }

    [Fact]
    public async Task LoginRejected_WithoutText_StillSaysRejected()
    {
        environment.Login.LoginError = new InternationalLoginRejectedException(null);

        await RunToFailureAsync(Request());

        AssertFailed(CatCodes.AUTHORIZATION_REQUIRED, "国际服登录被拒绝");
    }

    [Theory]
    [InlineData(InternationalLoginState.NoService, CatCodes.LAUNCH_FAILED, "没有游戏资格")]
    [InlineData(InternationalLoginState.NoTerms, CatCodes.LAUNCH_FAILED, "用户协议")]
    [InlineData(InternationalLoginState.NeedsPatchGame, CatCodes.GAME_UPDATE_REQUIRED, "请在这台电脑上用官方启动器更新国际服客户端")]
    [InlineData(InternationalLoginState.NeedsPatchBoot, CatCodes.GAME_UPDATE_REQUIRED, "请在这台电脑上用官方启动器更新国际服客户端")]
    public async Task LoginState_NotOk_MapsToCode(InternationalLoginState state, string code, string messagePart)
    {
        environment.Login.LoginState = state;

        await RunToFailureAsync(Request());

        AssertFailed(code, messagePart);
    }

    [Fact]
    public async Task GateClosed_AfterLogin_IsLaunchFailedMaintenance()
    {
        environment.Login.GateStatus = new InternationalGateStatus { Status = false, Message = ["All Worlds Maintenance", "until 10:00 (GMT)"] };

        await RunToFailureAsync(Request());

        AssertFailed(CatCodes.LAUNCH_FAILED, "国际服正在维护, 等维护结束再上号: All Worlds Maintenance until 10:00 (GMT)");
        Assert.Equal(["config", "boot", "loginStatus", "login", "gate"], environment.Calls);
    }

    [Fact]
    public async Task GateUnreachable_IsNetworkError()
    {
        environment.Login.GateError = new HttpRequestException("reset");

        await RunToFailureAsync(Request());

        AssertFailed(CatCodes.NETWORK_ERROR);
    }

    [Fact]
    public async Task GameExecutableMissing_IsInvalidGamePath()
    {
        // 目录检查之后、起进程之前被删（或损坏）
        environment.Login.OnLogin = () => File.Delete(Path.Combine(game.Root.FullName, "game", "ffxiv_dx11.exe"));

        await RunToFailureAsync(Request());

        AssertFailed(CatCodes.INVALID_GAME_PATH, "游戏可执行文件");
    }

    [Fact]
    public async Task ProcessCreationFailure_IsLaunchFailed_WithoutSecrets()
    {
        environment.StartError = new InvalidOperationException($"CreateProcess failed for args {UNIQUE_ID}");

        using var logs = new CapturedLogs();
        await RunToFailureAsync(Request());

        AssertFailed(CatCodes.LAUNCH_FAILED, "启动游戏进程失败");
        Assert.DoesNotContain(reporter.Messages, x => x.Contains("InvalidOperationException") || x.Contains("CreateProcess"));
        Assert.Contains("CreateProcess failed", logs.All);
        Assert.DoesNotContain(UNIQUE_ID, logs.All);
        Assert.DoesNotContain(PASSWORD, logs.All);
    }

    [Fact]
    public async Task ConfigFromBuiltinFallback_WarnsButContinues()
    {
        environment.Config = new InternationalClientConfig("https://x/{0}/{1}", null, InternationalClientConfigSource.Builtin);
        environment.Login.BootUpdateRequired = true;

        await RunToFailureAsync(Request());

        Assert.Contains(reporter.Messages, x => x.Contains("程序里自带的"));
        Assert.DoesNotContain(reporter.Messages, x => x.Contains("kamori"));
        Assert.Equal($"failed:{CatCodes.GAME_UPDATE_REQUIRED}", reporter.Entries.Last());
    }

    [Fact]
    public async Task CloseDuringPreparation_IsCancelled_AndNoGameIsStarted()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        environment.Login.LoginGate = gate.Task;

        var runner = NewRunner();
        var run    = runner.RunAsync(Request(), reporter, CancellationToken.None);

        await environment.Login.LoginEntered.Task.WaitAsync(Timeout);
        await runner.CloseAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(CatLaunchHost.EXIT_LAUNCH_FAILED, await run.WaitAsync(Timeout));
        Assert.Equal($"failed:{CatCodes.CANCELLED}", reporter.Entries.Last());
        Assert.Null(environment.GameProcess);
        gate.TrySetResult();
    }

    /// <summary>下号恰好撞上网络错误（或别的失败）: 还没起游戏就按取消报, 不报成启动失败, 也不往外抛</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CloseDuringPreparation_WhileStepFailsWithNonCancellationError_IsStillCancelled(bool rejected)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        environment.Login.LoginGate                  = gate.Task;
        environment.Login.LoginGateIgnoresCancellation = true;
        environment.Login.LoginError                 = rejected ? new InternationalLoginRejectedException("locked") : new HttpRequestException("connection reset");

        var runner = NewRunner();
        var run    = runner.RunAsync(Request(dalamud: true), reporter, CancellationToken.None);

        await environment.Login.LoginEntered.Task.WaitAsync(Timeout);
        await runner.CloseAsync(TimeSpan.FromSeconds(1));
        gate.TrySetResult();

        Assert.Equal(CatLaunchHost.EXIT_LAUNCH_FAILED, await run.WaitAsync(Timeout));
        Assert.Equal($"failed:{CatCodes.CANCELLED}", reporter.Entries.Last());
        Assert.Single(reporter.Entries, x => x.StartsWith("failed:", StringComparison.Ordinal));
        Assert.Null(environment.GameProcess);
    }

    #endregion

    #region 启动之后

    [Fact]
    public async Task HappyPath_WithDalamudAndMinion_EmitsEventsInOrder_AndStartsEncryptedGame()
    {
        using var logs = new CapturedLogs();
        var runner = NewRunner();
        var run    = runner.RunAsync(Request(dalamud: true, minion: true), reporter, CancellationToken.None);

        await reporter.Running.Task.WaitAsync(Timeout);
        Assert.False(run.IsCompleted);

        // 起游戏的请求
        var start = environment.StartRequest!;
        Assert.Equal(Path.Combine(game.Root.FullName, "game", "ffxiv_dx11.exe"), start.ExePath);
        Assert.Equal(Path.Combine(game.Root.FullName, "game"), start.WorkingDirectory);
        Assert.StartsWith("//**sqex0003", start.Arguments);
        Assert.EndsWith("**//", start.Arguments);
        Assert.DoesNotContain(UNIQUE_ID, start.Arguments);
        Assert.Equal(DPIAwareness.Unaware, start.DpiAwareness);
        Assert.True(environment.StartedWithDalamud);

        // Minion 按国际服挂在这个游戏上, 用的是 launch 带来的卡号、编号和论坛账号
        Assert.Equal((environment.GameProcess!.Id, game.Root.FullName, true, ACCOUNT), environment.FakeMinion.Attached);
        var attachedCard = environment.FakeMinion.AttachedCard!;
        Assert.Equal(MinionCards.VARIANT_GLOBAL, attachedCard.Variant);
        Assert.Equal(CARD, attachedCard.Fingerprint);
        Assert.Equal(MINION_ID, attachedCard.Uid);
        Assert.Equal(KEYCODE, attachedCard.Keycode.Reveal());
        Assert.Equal(FORUM_ID, attachedCard.ForumId);
        Assert.Equal(FORUM_PW, attachedCard.ForumPassword.Reveal());

        environment.KillGame();
        Assert.Equal(CatHostRuntime.EXIT_OK, await run.WaitAsync(Timeout));

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
        Assert.Equal(["config", "boot", "loginStatus", "login", "gate", "dalamud", "start"], environment.Calls);
        Assert.True(environment.Login.Disposed);

        AssertNothingSensitive();
        Assert.DoesNotContain(PASSWORD, logs.All);
        Assert.DoesNotContain(UNIQUE_ID, logs.All);
        Assert.DoesNotContain(KEYCODE, logs.All);
        Assert.DoesNotContain(FORUM_PW, logs.All);

        // 卡号和论坛密码已登记脱敏
        Assert.Equal("*** ***", redactor.Redact($"{KEYCODE} {FORUM_PW}"));
    }

    [Fact]
    public async Task HappyPath_WithoutAgents_SkipsDalamudAndMinion_AndStartsDirectly()
    {
        var run = NewRunner().RunAsync(Request(), reporter, CancellationToken.None);
        await reporter.Running.Task.WaitAsync(Timeout);

        Assert.False(environment.StartedWithDalamud);
        Assert.Null(environment.FakeMinion.Attached);

        environment.KillGame();
        await run.WaitAsync(Timeout);

        Assert.Equal(["stage:preparing", "stage:starting", "started", "stage:running", "exited"], reporter.Entries);
        Assert.Equal(["config", "boot", "loginStatus", "login", "gate", "start"], environment.Calls);
    }

    /// <summary>国际服不做崩溃重启: 游戏异常退出只发 game.exited, 不发 crashed / restarted, 也不再登录、不再起进程</summary>
    [Fact]
    public async Task GameDiesUnexpectedly_OnlyExited_NoCrashedNoRestart()
    {
        var run = NewRunner().RunAsync(Request(dalamud: true), reporter, CancellationToken.None);
        await reporter.Running.Task.WaitAsync(Timeout);

        environment.KillGame();
        Assert.Equal(CatHostRuntime.EXIT_OK, await run.WaitAsync(Timeout));

        Assert.Equal("exited", reporter.Entries.Last());
        Assert.DoesNotContain("crashed", reporter.Entries);
        Assert.DoesNotContain("restarted", reporter.Entries);
        Assert.Equal(1, environment.Calls.Count(x => x == "login"));
        Assert.Equal(1, environment.Calls.Count(x => x == "start"));
    }

    [Fact]
    public async Task Close_WhileRunning_ExitsWithClosedReason()
    {
        var runner = NewRunner();
        var run    = runner.RunAsync(Request(), reporter, CancellationToken.None);
        await reporter.Running.Task.WaitAsync(Timeout);

        await runner.CloseAsync(TimeSpan.Zero).WaitAsync(Timeout);

        Assert.Equal(CatHostRuntime.EXIT_OK, await run.WaitAsync(Timeout));
        Assert.Equal($"exited:{CatExitReasons.CLOSED}", reporter.Entries.Last());
        Assert.True(environment.GameProcess!.HasExited);
    }

    [Fact]
    public async Task DalamudNeverLoads_ReportsAgentFailure_ButGameKeepsRunning()
    {
        environment.DalamudLoaded = false;
        var runner = new CatInternationalGameRunner(redactor, environment) { DalamudInjectTimeout = TimeSpan.FromMilliseconds(600) };
        var run    = runner.RunAsync(Request(dalamud: true), reporter, CancellationToken.None);

        await reporter.Running.Task.WaitAsync(Timeout);

        Assert.Contains($"agent:dalamud:{CatCodes.DALAMUD_UNAVAILABLE}", reporter.Entries);
        Assert.False(environment.GameProcess!.HasExited);

        environment.KillGame();
        await run.WaitAsync(Timeout);
    }

    [Fact]
    public async Task MinionAttachFails_ReportsAttachFailed_ReleasesReservation_AndGameKeepsRunning()
    {
        environment.FakeMinion.AttachResult = MinionAttachResult.Failed("MinionLauncher 报错: ERROR: Invalid Game exe path!");
        var run = NewRunner().RunAsync(Request(minion: true), reporter, CancellationToken.None);

        await reporter.Running.Task.WaitAsync(Timeout);

        Assert.Contains($"agent:minion:{CatCodes.ATTACH_FAILED}", reporter.Entries);
        Assert.Equal([environment.GameProcess!.Id], environment.FakeMinion.Released);
        Assert.False(environment.GameProcess.HasExited);

        environment.KillGame();
        await run.WaitAsync(Timeout);
        Assert.Equal("exited", reporter.Entries.Last());
    }

    [Fact]
    public async Task MinionCardBusyAtAttachTime_ReportsAlreadyAttached_AndGameKeepsRunning()
    {
        environment.FakeMinion.ReserveError = (CatCodes.ALREADY_ATTACHED, "这张卡已挂在这台电脑的另一个游戏上");
        var run = NewRunner().RunAsync(Request(minion: true), reporter, CancellationToken.None);

        await reporter.Running.Task.WaitAsync(Timeout);

        Assert.Contains($"agent:minion:{CatCodes.ALREADY_ATTACHED}", reporter.Entries);
        Assert.Null(environment.FakeMinion.Attached);

        environment.KillGame();
        await run.WaitAsync(Timeout);
    }

    [Fact]
    public async Task Inject_Minion_RespectsAlreadyAttached_AndForce()
    {
        var runner = NewRunner();
        var run    = runner.RunAsync(Request(minion: true), reporter, CancellationToken.None);
        await reporter.Running.Task.WaitAsync(Timeout);

        environment.FakeMinion.IsAttachedResult = true;
        environment.FakeMinion.AttachCount      = 0;

        await runner.InjectAsync(false, true, false, reporter, CancellationToken.None).WaitAsync(Timeout);
        Assert.Equal(0, environment.FakeMinion.AttachCount);
        Assert.Contains($"agent:minion:ok", reporter.Entries);

        await runner.InjectAsync(false, true, true, reporter, CancellationToken.None).WaitAsync(Timeout);
        Assert.Equal(1, environment.FakeMinion.AttachCount);

        environment.KillGame();
        await run.WaitAsync(Timeout);
    }

    [Fact]
    public async Task Inject_Dalamud_IntoRunningGame_UsesPreparedSession()
    {
        environment.DalamudLoaded = false;
        var runner = NewRunner();
        var run    = runner.RunAsync(Request(), reporter, CancellationToken.None);
        await reporter.Running.Task.WaitAsync(Timeout);

        environment.OnInject = () => environment.DalamudLoaded = true;
        await runner.InjectAsync(true, false, false, reporter, CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(environment.GameProcess!.Id, environment.InjectedPid);
        Assert.Equal("agent:dalamud:ok", reporter.Entries.Reverse().Skip(1).First());
        Assert.Equal("stage:running", reporter.Entries.Last());

        environment.KillGame();
        await run.WaitAsync(Timeout);
    }

    [Fact]
    public async Task Inject_AfterGameExited_ReportsNotRunning()
    {
        var runner = NewRunner();
        var run    = runner.RunAsync(Request(), reporter, CancellationToken.None);
        await reporter.Running.Task.WaitAsync(Timeout);
        environment.KillGame();
        await run.WaitAsync(Timeout);

        await runner.InjectAsync(true, true, false, reporter, CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal([$"agent:dalamud:{CatCodes.NOT_RUNNING}", $"agent:minion:{CatCodes.NOT_RUNNING}"], reporter.Entries.TakeLast(2));
    }

    #endregion

    #region 失败码映射表

    public static TheoryData<Exception, string> ExceptionCodes =>
        new()
        {
            { new InternationalLoginRejectedException("locked"), CatCodes.AUTHORIZATION_REQUIRED },
            { new InternationalInvalidResponseException(InternationalInvalidResponseKind.StoredNotFound, "x"), CatCodes.LAUNCH_FAILED },
            { new InternationalInvalidResponseException(InternationalInvalidResponseKind.RestartupRequested, "x"), CatCodes.LAUNCH_FAILED },
            { new InternationalInvalidResponseException(InternationalInvalidResponseKind.LoginReplyMalformed, "x"), CatCodes.LAUNCH_FAILED },
            { new InternationalInvalidResponseException(InternationalInvalidResponseKind.GameVersionGone, "x"), CatCodes.GAME_UPDATE_REQUIRED },
            { new InternationalInvalidResponseException(InternationalInvalidResponseKind.UniqueIdMissing, "x"), CatCodes.LAUNCH_FAILED },
            { new InternationalInvalidResponseException(InternationalInvalidResponseKind.BootCheckFailed, "x"), CatCodes.LAUNCH_FAILED },
            { new InvalidVersionFilesException(), CatCodes.INVALID_GAME_PATH },
            { new FileNotFoundException("boot\\ffxivboot.exe"), CatCodes.INVALID_GAME_PATH },
            { new DirectoryNotFoundException("boot"), CatCodes.INVALID_GAME_PATH },
            { new UnauthorizedAccessException("denied"), CatCodes.INVALID_GAME_PATH },
            { new HttpRequestException("dns"), CatCodes.NETWORK_ERROR },
            { new HttpRequestException("io", new IOException("connection reset")), CatCodes.NETWORK_ERROR },
            { new TaskCanceledException("timeout"), CatCodes.NETWORK_ERROR },
            { new TimeoutException(), CatCodes.NETWORK_ERROR },
            { new JsonException("bad"), CatCodes.NETWORK_ERROR },
            { new InvalidOperationException("boom"), CatCodes.LAUNCH_FAILED }
        };

    [Theory]
    [MemberData(nameof(ExceptionCodes))]
    public void FromException_MapsToCode_AndMessageIsPlainChinese(Exception exception, string expectedCode)
    {
        var (code, message) = CatInternationalLoginFailures.FromException(exception, x => x);

        Assert.Equal(expectedCode, code);
        Assert.False(string.IsNullOrWhiteSpace(message));
        Assert.Contains("国际服", message);

        foreach (var word in new[] { "令牌", "凭证", "sid", "unique", "STORED" })
            Assert.DoesNotContain(word, message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FallbackMessages_CarryNoTypeOrEnumNames()
    {
        var (_, fromException) = CatInternationalLoginFailures.FromException(new InvalidOperationException("boom detail"), x => x);
        var fromState          = CatInternationalLoginFailures.FromState((InternationalLoginState)999)!.Value.Message;

        foreach (var message in new[] { fromException, fromState })
        {
            Assert.Equal(CatInternationalLoginFailures.UNEXPECTED_ERROR_MESSAGE, message);
            Assert.DoesNotContain("Exception", message);
            Assert.DoesNotContain("boom", message);
            Assert.DoesNotContain("999", message);
        }
    }

    [Fact]
    public void FromState_Ok_IsNull_AndEveryOtherStateHasACode()
    {
        Assert.Null(CatInternationalLoginFailures.FromState(InternationalLoginState.Ok));

        foreach (var state in Enum.GetValues<InternationalLoginState>().Where(x => x != InternationalLoginState.Ok))
            Assert.NotNull(CatInternationalLoginFailures.FromState(state));
    }

    [Fact]
    public void DescribeRejection_TruncatesLongSeText_AndFlattensNewlines()
    {
        var message = CatInternationalLoginFailures.DescribeRejection("line1\\r\\nline2\n" + new string('x', 500), x => x);

        Assert.StartsWith("国际服登录被拒绝: line1 line2 ", message);
        Assert.Contains("…", message);
        Assert.True(message.Length < 200);
    }

    #endregion

    #region 假环境

    private sealed class FakeEnvironment(string gamePath) : ICatInternationalEnvironment
    {
        public List<string> Calls { get; } = [];

        public Exception? InitializationError { get; set; }

        public CatInternationalSettings Settings { get; set; } = new(gamePath, ClientLanguage.German, DPIAwareness.Unaware, DalamudLoadMethod.EntryPoint, 0);

        public InternationalClientConfig Config { get; set; } = new("https://launcher.finalfantasyxiv.com/v740/index.html?rc_lang={0}&time={1}", null, InternationalClientConfigSource.Remote);

        public FakeLoginClient Login { get; } = new();

        public FakeMinionOps FakeMinion { get; } = new();

        public string? DalamudError { get; set; }

        public bool DalamudLoaded { get; set; } = true;

        public Exception? StartError { get; set; }

        public GameStartRequest? StartRequest { get; private set; }

        public bool StartedWithDalamud { get; private set; }

        public Process? GameProcess { get; private set; }

        public int? InjectedPid { get; private set; }

        public Action? OnInject { get; set; }

        public Task EnsureInitializedAsync() =>
            InitializationError == null ? Task.CompletedTask : Task.FromException(InitializationError);

        public CatInternationalSettings ReadSettings() => Settings;

        public Task<InternationalClientConfig> GetClientConfigAsync(CancellationToken cancellationToken)
        {
            Record("config");
            return Task.FromResult(Config);
        }

        public ICatInternationalLoginClient CreateLoginClient(InternationalClientConfig config, Action<string> onSecret)
        {
            Login.Owner    = this;
            Login.OnSecret = onSecret;
            return Login;
        }

        public (ICatInternationalDalamudSession? Session, string? Error) PrepareDalamud(DirectoryInfo path, CatInternationalSettings settings)
        {
            Record("dalamud");
            return DalamudError == null ? (new FakeSession(this), null) : (null, DalamudError);
        }

        public Process StartGame(GameStartRequest startRequest, ICatInternationalDalamudSession? dalamud)
        {
            Record("start");

            if (StartError != null)
                throw StartError;

            StartRequest       = startRequest;
            StartedWithDalamud = dalamud != null;

            // 占位进程代替游戏
            var startInfo = new ProcessStartInfo
            {
                FileName        = Path.Combine(Environment.SystemDirectory, "PING.EXE"),
                UseShellExecute = false,
                CreateNoWindow  = true
            };
            startInfo.ArgumentList.Add("-t");
            startInfo.ArgumentList.Add("127.0.0.1");

            GameProcess = Process.Start(startInfo)!;
            return GameProcess;
        }

        public bool IsDalamudLoaded(Process process) => DalamudLoaded;

        public ICatInternationalMinion Minion => FakeMinion;

        public void Record(string call)
        {
            lock (Calls)
                Calls.Add(call);
        }

        public void KillGame()
        {
            try
            {
                if (GameProcess is { HasExited: false })
                    GameProcess.Kill();
            }
            catch
            {
                // 已退出
            }
        }

        private sealed class FakeSession(FakeEnvironment owner) : ICatInternationalDalamudSession
        {
            public void InjectGame(int gamePid)
            {
                owner.InjectedPid = gamePid;
                owner.OnInject?.Invoke();
            }
        }
    }

    private sealed class FakeLoginClient : ICatInternationalLoginClient
    {
        public FakeEnvironment Owner { get; set; } = null!;

        public Action<string>? OnSecret { get; set; }

        public bool BootUpdateRequired { get; set; }

        public Exception? BootError { get; set; }

        public bool LoginStatus { get; set; } = true;

        public Exception? LoginStatusError { get; set; }

        public InternationalGateStatus GateStatus { get; set; } = new() { Status = true };

        public Exception? GateError { get; set; }

        public InternationalLoginState LoginState { get; set; } = InternationalLoginState.Ok;

        public Exception? LoginError { get; set; }

        public Action? OnLogin { get; set; }

        public Task? LoginGate { get; set; }

        public bool LoginGateIgnoresCancellation { get; set; }

        public TaskCompletionSource LoginEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public (string UserName, string Password, ClientLanguage Language)? LoginArguments { get; private set; }

        public bool Disposed { get; private set; }

        public Task<bool> IsBootUpdateRequiredAsync(DirectoryInfo gamePath, CancellationToken cancellationToken)
        {
            Owner.Record("boot");
            return BootError == null ? Task.FromResult(BootUpdateRequired) : Task.FromException<bool>(BootError);
        }

        public Task<InternationalGateStatus> GetLoginStatusAsync(CancellationToken cancellationToken)
        {
            Owner.Record("loginStatus");
            return LoginStatusError == null
                       ? Task.FromResult(new InternationalGateStatus { Status = LoginStatus })
                       : Task.FromException<InternationalGateStatus>(LoginStatusError);
        }

        public Task<InternationalGateStatus> GetGateStatusAsync(ClientLanguage language, CancellationToken cancellationToken)
        {
            Owner.Record("gate");
            return GateError == null ? Task.FromResult(GateStatus) : Task.FromException<InternationalGateStatus>(GateError);
        }

        public async Task<InternationalLoginResult> LoginAsync(string userName, string password, DirectoryInfo gamePath, ClientLanguage language, CancellationToken cancellationToken)
        {
            Owner.Record("login");
            LoginArguments = (userName, password, language);
            LoginEntered.TrySetResult();

            if (LoginGate != null)
                await (LoginGateIgnoresCancellation ? LoginGate : LoginGate.WaitAsync(cancellationToken));

            if (LoginError != null)
                throw LoginError;

            OnLogin?.Invoke();

            var ok = LoginState is InternationalLoginState.Ok or InternationalLoginState.NeedsPatchGame;

            if (ok)
                OnSecret?.Invoke(UNIQUE_ID);

            return new InternationalLoginResult { State = LoginState, UniqueId = ok ? UNIQUE_ID : null, Region = 3, MaxExpansion = 5 };
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class FakeMinionOps : ICatInternationalMinion
    {
        public (string Code, string Message)? CheckError { get; set; }

        public (string Code, string Message)? ReserveError { get; set; }

        public MinionAttachResult AttachResult { get; set; } = MinionAttachResult.Succeeded();

        public (int Pid, string GamePath, bool DalamudInjected, string AccountName)? Attached { get; private set; }

        public CatMinionLaunch? AttachedCard { get; private set; }

        public int AttachCount { get; set; }

        public bool IsAttachedResult { get; set; }

        public List<int> Released { get; } = [];

        public (string Code, string Message)? Check(CatLaunchRequest request) => CheckError;

        public Task<((string Code, string Message)? Error, bool Reserved)> ReserveAsync(CatLaunchRequest request, Process process) =>
            Task.FromResult<((string Code, string Message)?, bool)>(ReserveError == null ? (null, true) : (ReserveError, false));

        public Task<MinionAttachResult> AttachAsync(CatMinionLaunch minion, Process process, DirectoryInfo gamePath, bool dalamudInjected, string accountName, CancellationToken cancellationToken)
        {
            AttachCount++;
            Attached     = (process.Id, gamePath.FullName, dalamudInjected, accountName);
            AttachedCard = minion;
            return Task.FromResult(AttachResult);
        }

        public void ReleaseReservation(int gamePid) => Released.Add(gamePid);

        public bool IsAttached(Process process) => IsAttachedResult;
    }

    #endregion
}
