using System.Text.Json.Nodes;
using XIVLauncher.CatHost;
using XIVLauncher.Common.Game;
using Xunit;

namespace XIVLauncher.Test.CatHost;

public sealed class CatRpcServerTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly string                  pipeName = CatTestNames.NewPipeName();
    private readonly string                  token    = CatTestNames.NewToken();
    private readonly FakeGameRunner          runner   = new();
    private readonly CatLaunchHost           host;
    private readonly CatRpcServer            server;
    private readonly CancellationTokenSource cts = new();

    public CatRpcServerTests()
    {
        CatRpcServer? created = null;
        host    = new CatLaunchHost(runner, (method, parameters) => created!.NotifyAsync(method, parameters), new CatLogRedactor());
        created = new CatRpcServer(pipeName, token, host, "test-version");
        server  = created;
        server.Listen();
        _ = server.RunAsync(cts.Token);
    }

    public void Dispose()
    {
        cts.Cancel();
        runner.Finish.TrySetResult(0);
        server.Dispose();
    }

    [Fact]
    public async Task Hello_WithCorrectToken_ReturnsProtocolVersion()
    {
        await using var client = await CatTestClient.ConnectAsync(pipeName, Timeout);

        var response = await client.RequestAsync("hello", new { token });

        Assert.Equal("dml-cat/1", response["result"]!["protocolVersion"]!.GetValue<string>());
        Assert.Equal("test-version", response["result"]!["launcherVersion"]!.GetValue<string>());
    }

    [Fact]
    public async Task Hello_WithWrongToken_ClosesConnection()
    {
        await using var client = await CatTestClient.ConnectAsync(pipeName, Timeout);

        await client.WriteLineAsync("""{"jsonrpc":"2.0","id":1,"method":"hello","params":{"token":"wrong-token-wrong-token"}}""");

        await client.Closed.Task.WaitAsync(Timeout);
        Assert.False(server.HasClient);
    }

    [Fact]
    public async Task FirstFrameNotHello_ClosesConnection()
    {
        await using var client = await CatTestClient.ConnectAsync(pipeName, Timeout);

        await client.WriteLineAsync("""{"jsonrpc":"2.0","id":1,"method":"status","params":{}}""");

        await client.Closed.Task.WaitAsync(Timeout);
    }

    [Fact]
    public async Task Status_BeforeLaunch_IsIdle()
    {
        await using var client = await ConnectAndHelloAsync();

        var response = await client.RequestAsync("status", new { });

        Assert.Equal("idle", response["result"]!["stage"]!.GetValue<string>());
        Assert.Null(response["result"]!["pid"]);
    }

    [Fact]
    public async Task UnknownMethod_ReturnsMethodNotFound()
    {
        await using var client = await ConnectAndHelloAsync();

        var response = await client.RequestAsync("game.launch", new { });

        Assert.Equal(-32601, response["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task Launch_InvalidFingerprint_IsRejected()
    {
        await using var client = await ConnectAndHelloAsync();

        var response = await client.RequestAsync
        (
            "launch",
            new { operationId = "op1", accountName = "acc", dalamud = false, minion = new { cardFingerprint = "XYZ", variant = "cn" } }
        );

        Assert.False(response["result"]!["accepted"]!.GetValue<bool>());
        Assert.Equal("invalidParams", response["result"]!["code"]!.GetValue<string>());
        Assert.False(host.HasLaunch);
    }

    [Fact]
    public async Task Launch_IsAcceptedOnlyOnce_AndPassesParameters()
    {
        await using var client = await ConnectAndHelloAsync();

        var first = await client.RequestAsync
        (
            "launch",
            new { operationId = "op1", accountName = "acc", dalamud = true, minion = new { cardFingerprint = "0123456789abcdef", variant = "global" }, areaName = " 豆豆柴 " }
        );
        var second = await client.RequestAsync("launch", new { operationId = "op2", accountName = "acc", dalamud = false });

        Assert.True(first["result"]!["accepted"]!.GetValue<bool>());
        Assert.False(second["result"]!["accepted"]!.GetValue<bool>());
        Assert.Equal("alreadyLaunched", second["result"]!["code"]!.GetValue<string>());

        await runner.Started.Task.WaitAsync(Timeout);
        Assert.Equal(new CatLaunchRequest("op1", "acc", true, "0123456789abcdef", "global", AreaName: "豆豆柴"), runner.Request);
    }

    [Theory]
    [InlineData("weGame")]
    [InlineData("WEGAME")]
    [InlineData(" wegame ")]
    public async Task Launch_PassesWeGamePlatform_CaseInsensitive(string platform)
    {
        await using var client = await ConnectAndHelloAsync();

        var response = await client.RequestAsync("launch", new { operationId = "op1", accountName = "acc", dalamud = false, platform });

        Assert.True(response["result"]!["accepted"]!.GetValue<bool>());
        await runner.Started.Task.WaitAsync(Timeout);
        Assert.Equal(XIVAccountType.WeGame, runner.Request!.Platform);
        Assert.True(runner.Request.IsWeGame);
        Assert.Equal(new CatLaunchRequest("op1", "acc", false, null, null, Platform: XIVAccountType.WeGame), runner.Request);
    }

    [Theory]
    [InlineData("shengqu")]
    [InlineData("ShengQu")]
    [InlineData(null)] // 不带 platform: 按盛趣, 与加这个字段之前一样
    public async Task Launch_ShengquOrMissingPlatform_IsSdo(string? platform)
    {
        await using var client = await ConnectAndHelloAsync();

        var response = platform == null
                           ? await client.RequestAsync("launch", new { operationId = "op1", accountName = "acc", dalamud = false })
                           : await client.RequestAsync("launch", new { operationId = "op1", accountName = "acc", dalamud = false, platform });

        Assert.True(response["result"]!["accepted"]!.GetValue<bool>());
        await runner.Started.Task.WaitAsync(Timeout);
        Assert.Equal(XIVAccountType.Sdo, runner.Request!.Platform);
        Assert.False(runner.Request.IsWeGame);
    }

    [Theory]
    [InlineData("international")]
    [InlineData("INTERNATIONAL")]
    [InlineData(" International ")]
    public async Task Launch_International_WithPassword_IsAccepted_AndPasswordNeverPrinted(string platform)
    {
        const string PASSWORD = "Pw-国际服!x9";
        await using var client = await ConnectAndHelloAsync();

        var response = await client.RequestAsync
        (
            "launch",
            new { operationId = "op1", accountName = "seAccount", dalamud = true, platform, password = PASSWORD, areaName = "Elemental" }
        );

        Assert.True(response["result"]!["accepted"]!.GetValue<bool>());
        await runner.Started.Task.WaitAsync(Timeout);

        var request = runner.Request!;
        Assert.True(request.IsInternational);
        Assert.Equal(CatPlatform.International, request.Channel);
        Assert.False(request.IsWeGame);
        Assert.Equal(PASSWORD, request.Password!.Reveal());
        Assert.DoesNotContain(PASSWORD, request.ToString());
        Assert.Equal("***", request.Password.ToString());

        // 密码已登记脱敏: 启动器不小心把它写进任何发给外壳的文字, 都会被遮住
        runner.Reporter!.Log("error", $"登录失败 {PASSWORD} / {Uri.EscapeDataString(PASSWORD)}");
        runner.Reporter.Failed(CatCodes.AUTHORIZATION_REQUIRED, $"国际服登录被拒绝: {PASSWORD}");

        var seen = new List<(string Method, JsonNode? Params)>();
        client.WaitForEvent("launch.failed", Timeout, seen);
        Assert.All(seen, x => Assert.DoesNotContain(PASSWORD, x.Params?.ToJsonString(CatProtocol.JsonOptions) ?? string.Empty));
        Assert.All(seen, x => Assert.DoesNotContain(Uri.EscapeDataString(PASSWORD), x.Params?.ToJsonString(CatProtocol.JsonOptions) ?? string.Empty));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Launch_International_WithoutPassword_IsRejected(string? password)
    {
        await using var client = await ConnectAndHelloAsync();

        var response = password == null
                           ? await client.RequestAsync("launch", new { operationId = "op1", accountName = "seAccount", dalamud = false, platform = "international" })
                           : await client.RequestAsync("launch", new { operationId = "op1", accountName = "seAccount", dalamud = false, platform = "international", password });

        Assert.False(response["result"]!["accepted"]!.GetValue<bool>());
        Assert.Equal("invalidParams", response["result"]!["code"]!.GetValue<string>());
        Assert.False(host.HasLaunch);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("shengqu")]
    [InlineData("weGame")]
    public async Task Launch_DomesticPlatform_IgnoresPassword(string? platform)
    {
        const string PASSWORD = "should-not-be-kept";
        await using var client = await ConnectAndHelloAsync();

        var response = platform == null
                           ? await client.RequestAsync("launch", new { operationId = "op1", accountName = "acc", dalamud = false, password = PASSWORD })
                           : await client.RequestAsync("launch", new { operationId = "op1", accountName = "acc", dalamud = false, platform, password = PASSWORD });

        Assert.True(response["result"]!["accepted"]!.GetValue<bool>());
        await runner.Started.Task.WaitAsync(Timeout);
        Assert.False(runner.Request!.IsInternational);
        Assert.Null(runner.Request.Password);

        // 与不带 password 时得到的请求完全相同
        var expectedType = platform == "weGame" ? XIVAccountType.WeGame : XIVAccountType.Sdo;
        Assert.Equal(new CatLaunchRequest("op1", "acc", false, null, null, Platform: expectedType), runner.Request);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("weGame", false)]
    [InlineData("international", true)]
    public async Task Launch_RunnerIsChosenByPlatform_OnlyWhenLaunchIsAccepted(string? platform, bool expectInternational)
    {
        var domestic      = new FakeGameRunner();
        var international = new FakeGameRunner();
        var calls         = 0;

        var factoryHost = new CatLaunchHost
        (
            launchRequest =>
            {
                calls++;
                return launchRequest.IsInternational ? international : domestic;
            },
            (_, _) => Task.CompletedTask,
            new CatLogRedactor()
        );

        // 被拒绝的 launch 不建启动器
        Assert.False(factoryHost.Launch(new CatLaunchParams("op1", "acc", false, null, Platform: "international")).Accepted);
        Assert.Equal(0, calls);

        Assert.True(factoryHost.Launch(new CatLaunchParams("op1", "acc", false, null, Platform: platform, Password: "pw-123456")).Accepted);
        Assert.Equal(1, calls);

        var chosen = expectInternational ? international : domestic;
        var other  = expectInternational ? domestic : international;
        await chosen.Started.Task.WaitAsync(Timeout);
        Assert.False(other.Started.Task.IsCompleted);

        // close 交给选中的那个启动器
        Assert.True(factoryHost.Close(new CatCloseParams(1)).Accepted);
        await chosen.Closed.Task.WaitAsync(Timeout);
        Assert.False(other.Closed.Task.IsCompleted);

        chosen.Finish.TrySetResult(0);
    }

    [Fact]
    public void LaunchParams_ToString_DoesNotPrintPassword()
    {
        var parameters = new CatLaunchParams("op1", "seAccount", true, null, Platform: "international", Password: "TopSecret-123");

        Assert.DoesNotContain("TopSecret-123", parameters.ToString());
        Assert.DoesNotContain("TopSecret-123", $"{parameters}");
    }

    [Theory]
    [InlineData(null, true, CatPlatform.Shengqu)]
    [InlineData(" ", true, CatPlatform.Shengqu)]
    [InlineData("shengqu", true, CatPlatform.Shengqu)]
    [InlineData("WEGAME", true, CatPlatform.WeGame)]
    [InlineData("international", true, CatPlatform.International)]
    [InlineData("International", true, CatPlatform.International)]
    [InlineData(" INTERNATIONAL ", true, CatPlatform.International)]
    [InlineData("global", false, CatPlatform.Shengqu)]
    [InlineData("intl", false, CatPlatform.Shengqu)]
    [InlineData("steam", false, CatPlatform.Shengqu)]
    public void Platform_TryParsePlatform(string? platform, bool expectedOk, CatPlatform expected)
    {
        Assert.Equal(expectedOk, CatPlatforms.TryParsePlatform(platform, out var channel));
        Assert.Equal(expected, channel);
    }

    [Fact]
    public void Redactor_RegisterSecret_HidesShortValuesAndEncodedForms()
    {
        var redactor = new CatLogRedactor();
        redactor.Register("abc");           // 普通登记: 短于 6 个字符不登记
        redactor.RegisterSecret("p@ 1");    // 必须遮住的值: 不看长度, 连同编码形式

        Assert.Equal("abc *** *** ***", redactor.Redact("abc p@ 1 p%40%201 p%40+1"));
    }

    [Theory]
    [InlineData("steam")]
    [InlineData("sdo")]
    [InlineData("we game")]
    public async Task Launch_UnknownPlatform_IsRejected(string platform)
    {
        await using var client = await ConnectAndHelloAsync();

        var response = await client.RequestAsync("launch", new { operationId = "op1", accountName = "acc", dalamud = false, platform });

        Assert.False(response["result"]!["accepted"]!.GetValue<bool>());
        Assert.Equal("invalidParams", response["result"]!["code"]!.GetValue<string>());
        Assert.False(host.HasLaunch);
    }

    [Theory]
    [InlineData(null, true, XIVAccountType.Sdo)]
    [InlineData("", true, XIVAccountType.Sdo)]
    [InlineData("  ", true, XIVAccountType.Sdo)]
    [InlineData("shengqu", true, XIVAccountType.Sdo)]
    [InlineData("SHENGQU", true, XIVAccountType.Sdo)]
    [InlineData("weGame", true, XIVAccountType.WeGame)]
    [InlineData("wegame", true, XIVAccountType.WeGame)]
    [InlineData("WeGame", true, XIVAccountType.WeGame)]
    [InlineData("wegame2", false, XIVAccountType.Sdo)]
    [InlineData("global", false, XIVAccountType.Sdo)]
    public void Platform_TryParse(string? platform, bool expectedOk, XIVAccountType expected)
    {
        Assert.Equal(expectedOk, CatPlatforms.TryParse(platform, out var accountType));
        Assert.Equal(expected, accountType);
    }

    [Theory]
    // 账号名精确相同优先, 不看备注
    [InlineData("10000000000000001", "10000000000000001", CatWeGameAccountMatch.ByName)]
    [InlineData("13800001111", "13800001111", CatWeGameAccountMatch.ByName)] // 有一行的账号名就是这个号: 用它, 不管别的行备注里也写着
    // 备注里某一段数字完全相等
    [InlineData("123456", "10000000000000001", CatWeGameAccountMatch.ByNote)]
    [InlineData(" 123456 ", "10000000000000001", CatWeGameAccountMatch.ByNote)]
    [InlineData("987654321", "10000000000000002", CatWeGameAccountMatch.ByNote)] // 备注「老王 QQ987654321/手机13900002222」
    [InlineData("13900002222", "10000000000000002", CatWeGameAccountMatch.ByNote)]
    // 只是某段数字的一部分: 不算
    [InlineData("12345", null, CatWeGameAccountMatch.None)]
    [InlineData("23456", null, CatWeGameAccountMatch.None)]
    [InlineData("1234567", null, CatWeGameAccountMatch.None)]
    [InlineData("9876543210", null, CatWeGameAccountMatch.None)]
    // 两行的备注都写着: 不猜
    [InlineData("555666", null, CatWeGameAccountMatch.AmbiguousNote)]
    // 不是纯数字的号不按备注找
    [InlineData("老王", null, CatWeGameAccountMatch.None)]
    [InlineData("QQ987654321", null, CatWeGameAccountMatch.None)]
    [InlineData("123 456", null, CatWeGameAccountMatch.None)]
    // 没有账号名的残行不参与
    [InlineData("777888", null, CatWeGameAccountMatch.None)]
    [InlineData("", null, CatWeGameAccountMatch.None)]
    [InlineData(null, null, CatWeGameAccountMatch.None)]
    public void ResolveWeGameAccount_MatchesNameThenNote(string? requested, string? expectedName, CatWeGameAccountMatch expectedMatch)
    {
        (string Name, string? Note)[] rows =
        [
            ("10000000000000001", "123456"),
            ("10000000000000002", "老王 QQ987654321/手机13900002222"),
            ("10000000000000003", "555666 大号"),
            ("10000000000000004", "小号(555666)"),
            ("10000000000000005", null),
            ("13800001111", ""),
            ("10000000000000006", "13800001111"),
            ("", "777888")
        ];
        var boxed = rows.Select(x => Tuple.Create(x.Name, x.Note)).ToArray();

        var row = CatRealGameRunner.ResolveWeGameAccount(boxed, requested, x => x.Item1, x => x.Item2, out var match);

        Assert.Equal(expectedMatch, match);
        Assert.Equal(expectedName, row?.Item1);
    }

    [Theory]
    [InlineData("猫小胖", "豆豆柴", "猫小胖", false)] // 账号库记的优先: 超域旅行后角色在别的大区
    [InlineData(null, "豆豆柴", "豆豆柴", true)]     // 账号库没记: 用资料里的
    [InlineData("不存在", "豆豆柴", "豆豆柴", true)] // 账号库记的对不上: 用资料里的
    [InlineData(null, "不存在", null, false)]        // 资料里的对不上: 不悄悄取第一个
    [InlineData(null, null, null, false)]
    public void ResolveArea_PrefersSavedThenRequested(string? saved, string? requested, string? expected, bool expectedFromRequest)
    {
        string[] areas = ["陆行鸟", "莫古力", "猫小胖", "豆豆柴"];

        var area = CatRealGameRunner.ResolveArea(areas, saved, requested, x => x, out var fromRequest);

        Assert.Equal(expected, area);
        Assert.Equal(expectedFromRequest, fromRequest);
    }

    [Fact]
    public async Task Events_CarryOperationId_AndUpdateStatus()
    {
        await using var client = await ConnectAndHelloAsync();
        await client.RequestAsync("launch", new { operationId = "op-42", accountName = "acc", dalamud = false });
        await runner.Started.Task.WaitAsync(Timeout);

        runner.Reporter!.Stage(CatStages.STARTING);
        runner.Reporter.Started(1234, DateTimeOffset.UtcNow);
        runner.Reporter.Stage(CatStages.RUNNING);

        var started = client.WaitForEvent("game.started", Timeout);
        Assert.Equal("op-42", started.Params!["operationId"]!.GetValue<string>());
        Assert.Equal(1234, started.Params["pid"]!.GetValue<int>());

        client.WaitForEvent("game.stage", Timeout);
        var status = await client.RequestAsync("status", new { });
        Assert.Equal("running", status["result"]!["stage"]!.GetValue<string>());
        Assert.Equal(1234, status["result"]!["pid"]!.GetValue<int>());

        var inject = await client.RequestAsync("inject", new { minion = true });
        Assert.True(inject["result"]!["accepted"]!.GetValue<bool>());
        var agent = client.WaitForEvent("game.agent", Timeout);
        Assert.Equal("minion", agent.Params!["kind"]!.GetValue<string>());
    }

    [Fact]
    public async Task EventsWhileDisconnected_AreDeliveredAfterReconnect()
    {
        var first = await ConnectAndHelloAsync();
        await first.RequestAsync("launch", new { operationId = "op1", accountName = "acc", dalamud = false });
        await runner.Started.Task.WaitAsync(Timeout);
        await first.DisposeAsync();

        await WaitUntilAsync(() => !server.HasClient);

        runner.Reporter!.Started(777, DateTimeOffset.UtcNow);
        runner.Reporter.Exited(777, 0);
        await WaitUntilAsync(() => server.BufferedEventCount == 2);

        await using var second = await ConnectAndHelloAsync();
        var seen = new List<(string Method, JsonNode? Params)>();
        second.WaitForEvent("game.exited", Timeout, seen);

        Assert.Equal(["game.started", "game.exited"], seen.Select(x => x.Method));
        Assert.Equal(0, server.BufferedEventCount);
    }

    [Fact]
    public async Task Close_BeforeLaunch_EndsProcessWithExitOk_AndRejectsLaterLaunch()
    {
        await using var client = await ConnectAndHelloAsync();

        var close = await client.RequestAsync("close", new { });
        Assert.True(close["result"]!["accepted"]!.GetValue<bool>());
        Assert.Equal(0, await host.Completion.WaitAsync(Timeout));

        var launch = await client.RequestAsync("launch", new { operationId = "op1", accountName = "acc", dalamud = false });
        Assert.False(launch["result"]!["accepted"]!.GetValue<bool>());
        Assert.Equal("closing", launch["result"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Close_AfterLaunch_AsksRunnerToClose_WithTimeout_AndIsIdempotent()
    {
        await using var client = await ConnectAndHelloAsync();
        await client.RequestAsync("launch", new { operationId = "op1", accountName = "acc", dalamud = false });
        await runner.Started.Task.WaitAsync(Timeout);
        runner.Reporter!.Started(4321, DateTimeOffset.UtcNow);
        runner.Reporter.Stage(CatStages.RUNNING);

        var first  = await client.RequestAsync("close", new { timeoutSeconds = 7 });
        var second = await client.RequestAsync("close", new { });

        Assert.True(first["result"]!["accepted"]!.GetValue<bool>());
        Assert.True(second["result"]!["accepted"]!.GetValue<bool>());
        await runner.Closed.Task.WaitAsync(Timeout);
        Assert.Equal(TimeSpan.FromSeconds(7), runner.CloseTimeout);

        var inject = await client.RequestAsync("inject", new { minion = true });
        Assert.Equal("notRunning", inject["result"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Close_InvalidTimeout_IsRejected()
    {
        await using var client = await ConnectAndHelloAsync();

        var close = await client.RequestAsync("close", new { timeoutSeconds = 100000 });

        Assert.False(close["result"]!["accepted"]!.GetValue<bool>());
        Assert.Equal("invalidParams", close["result"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Launch_PassesCrashDialogTimeout_AndRejectsOutOfRange()
    {
        await using var client = await ConnectAndHelloAsync();

        var bad = await client.RequestAsync("launch", new { operationId = "op1", accountName = "acc", dalamud = true, crashDialogTimeoutSeconds = 0 });
        Assert.Equal("invalidParams", bad["result"]!["code"]!.GetValue<string>());

        var ok = await client.RequestAsync("launch", new { operationId = "op1", accountName = "acc", dalamud = true, crashDialogTimeoutSeconds = 30 });
        Assert.True(ok["result"]!["accepted"]!.GetValue<bool>());

        await runner.Started.Task.WaitAsync(Timeout);
        Assert.Equal(TimeSpan.FromSeconds(30), runner.Request!.CrashDialogTimeout);
    }

    [Fact]
    public async Task Inject_PassesForce_ToRunner()
    {
        await using var client = await ConnectAndHelloAsync();
        await client.RequestAsync("launch", new { operationId = "op1", accountName = "acc", dalamud = false });
        await runner.Started.Task.WaitAsync(Timeout);
        runner.Reporter!.Started(1234, DateTimeOffset.UtcNow);
        runner.Reporter.Stage(CatStages.RUNNING);

        await client.RequestAsync("inject", new { minion = true });
        var plain = client.WaitForEvent("game.agent", Timeout);
        Assert.Equal("alreadyAttached", plain.Params!["code"]!.GetValue<string>());

        await client.RequestAsync("inject", new { minion = true, force = true });
        var forced = client.WaitForEvent("game.agent", Timeout);
        Assert.Null(forced.Params!["code"]);
    }

    [Fact]
    public async Task Crashed_And_ExitedWithReason_AreSentWithOperationId()
    {
        await using var client = await ConnectAndHelloAsync();
        await client.RequestAsync("launch", new { operationId = "op-c", accountName = "acc", dalamud = true });
        await runner.Started.Task.WaitAsync(Timeout);

        runner.Reporter!.Crashed(55);
        runner.Reporter.Exited(55, 1, CatExitReasons.RESTART_FAILED, CatCodes.AUTHORIZATION_REQUIRED, "票据刷新失败");

        var crashed = client.WaitForEvent("game.crashed", Timeout);
        Assert.Equal("op-c", crashed.Params!["operationId"]!.GetValue<string>());
        Assert.Equal(55, crashed.Params["pid"]!.GetValue<int>());

        var exited = client.WaitForEvent("game.exited", Timeout);
        Assert.Equal("restartFailed", exited.Params!["reason"]!.GetValue<string>());
        Assert.Equal("authorizationRequired", exited.Params["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task BufferOverflow_DropsLogEventsFirst()
    {
        await server.NotifyAsync("game.started", new { pid = 1 });

        for (var i = 0; i < CatRpcServer.MAX_BUFFERED_EVENTS + 20; i++)
            await server.NotifyAsync("launcher.log", new { level = "information", message = $"log {i}" });

        await server.NotifyAsync("game.exited", new { pid = 1 });

        Assert.Equal(CatRpcServer.MAX_BUFFERED_EVENTS, server.BufferedEventCount);

        await using var client = await ConnectAndHelloAsync();
        var seen = new List<(string Method, JsonNode? Params)>();
        client.WaitForEvent("game.exited", Timeout, seen);

        Assert.Equal("game.started", seen[0].Method);
        Assert.Equal(CatRpcServer.MAX_BUFFERED_EVENTS, seen.Count);
    }

    [Fact]
    public async Task Host_DoesNotBlockReporter_WhenPublishingHangs()
    {
        var never = new TaskCompletionSource();
        var stuck = new CatLaunchHost(new FakeGameRunner(), (_, _) => never.Task, new CatLogRedactor());

        var elapsed = System.Diagnostics.Stopwatch.StartNew();

        for (var i = 0; i < 100; i++)
            stuck.Stage(CatStages.RUNNING);

        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(2));
        Assert.True(stuck.PendingEventCount > 0);
        Assert.False(await stuck.DrainEventsAsync(TimeSpan.FromMilliseconds(100)));
        never.TrySetResult();
        Assert.True(await stuck.DrainEventsAsync(Timeout));
    }

    [Fact]
    public async Task Redactor_MasksRegisteredSecretsInEvents()
    {
        var redactor = new CatLogRedactor();
        redactor.Register("super-secret-ticket");

        Assert.Equal("ticket=*** ok", redactor.Redact("ticket=super-secret-ticket ok"));
        await Task.CompletedTask;
    }

    private async Task<CatTestClient> ConnectAndHelloAsync()
    {
        var client   = await CatTestClient.ConnectAsync(pipeName, Timeout);
        var response = await client.RequestAsync("hello", new { token });
        Assert.NotNull(response["result"]);
        await WaitUntilAsync(() => server.HasClient);
        return client;
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
