using System.Text.Json.Nodes;
using XIVLauncher.CatHost;
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
