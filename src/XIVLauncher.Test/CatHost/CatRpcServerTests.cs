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
            new { operationId = "op1", accountName = "acc", dalamud = true, minion = new { cardFingerprint = "0123456789abcdef", variant = "global" } }
        );
        var second = await client.RequestAsync("launch", new { operationId = "op2", accountName = "acc", dalamud = false });

        Assert.True(first["result"]!["accepted"]!.GetValue<bool>());
        Assert.False(second["result"]!["accepted"]!.GetValue<bool>());
        Assert.Equal("alreadyLaunched", second["result"]!["code"]!.GetValue<string>());

        await runner.Started.Task.WaitAsync(Timeout);
        Assert.Equal(new CatLaunchRequest("op1", "acc", true, "0123456789abcdef", "global"), runner.Request);
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
        Assert.Equal(2, server.BufferedEventCount);

        await using var second = await ConnectAndHelloAsync();
        var seen = new List<(string Method, JsonNode? Params)>();
        second.WaitForEvent("game.exited", Timeout, seen);

        Assert.Equal(["game.started", "game.exited"], seen.Select(x => x.Method));
        Assert.Equal(0, server.BufferedEventCount);
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
