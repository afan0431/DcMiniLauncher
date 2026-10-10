using System.Text.Json;
using System.Text.Json.Nodes;
using XIVLauncher.CatHost;
using XIVLauncher.Common.Game;
using XIVLauncher.Minion;
using Xunit;

namespace XIVLauncher.Test.CatHost;

public sealed class CatRpcServerTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private const string FINGERPRINT    = "0123456789abcdef";
    private const string KEYCODE        = "FFXIVXFAKE0000000000000000000000000000";
    private const string UID            = "0123456789abcdef0123456789abcdef";
    private const string FORUM_ID       = "fake-forum-user";
    private const string FORUM_PASSWORD = "fake-forum-pass!9";

    private static object MinionJson(string variant) =>
        new { cardFingerprint = FINGERPRINT, variant, keycode = KEYCODE, uid = UID, forumId = FORUM_ID, forumPassword = FORUM_PASSWORD };

    private static CatMinionLaunch ExpectedMinion(string variant) =>
        new(FINGERPRINT, variant, new CatSecret(KEYCODE), UID, FORUM_ID, new CatSecret(FORUM_PASSWORD));

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
            new { operationId = "op1", accountName = "acc", dalamud = true, minion = MinionJson("global"), areaName = " 豆豆柴 " }
        );
        var second = await client.RequestAsync("launch", new { operationId = "op2", accountName = "acc", dalamud = false });

        Assert.True(first["result"]!["accepted"]!.GetValue<bool>());
        Assert.False(second["result"]!["accepted"]!.GetValue<bool>());
        Assert.Equal("alreadyLaunched", second["result"]!["code"]!.GetValue<string>());

        await runner.Started.Task.WaitAsync(Timeout);
        Assert.Equal(new CatLaunchRequest("op1", "acc", true, ExpectedMinion("global"), AreaName: "豆豆柴"), runner.Request);
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
        Assert.Equal(new CatLaunchRequest("op1", "acc", false, null, Platform: XIVAccountType.WeGame), runner.Request);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)] // 不带 weGameLogin: 与加这个字段之前一样
    public async Task Launch_WeGame_PassesWeGameLoginFlag(bool? weGameLogin)
    {
        await using var client = await ConnectAndHelloAsync();

        var response = weGameLogin == null
                           ? await client.RequestAsync("launch", new { operationId = "op1", accountName = "123456", dalamud = false, platform = "weGame" })
                           : await client.RequestAsync("launch", new { operationId = "op1", accountName = "123456", dalamud = false, platform = "weGame", weGameLogin });

        Assert.True(response["result"]!["accepted"]!.GetValue<bool>());
        await runner.Started.Task.WaitAsync(Timeout);
        Assert.Equal(weGameLogin == true, runner.Request!.WeGameLogin);
        Assert.Equal(new CatLaunchRequest("op1", "123456", false, null, Platform: XIVAccountType.WeGame, WeGameLogin: weGameLogin == true), runner.Request);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("shengqu")]
    [InlineData("international")]
    public async Task Launch_WeGameLogin_OnOtherPlatform_IsRejected(string? platform)
    {
        await using var client = await ConnectAndHelloAsync();

        var response = platform == null
                           ? await client.RequestAsync("launch", new { operationId = "op1", accountName = "acc", dalamud = false, weGameLogin = true, password = "pw-123456" })
                           : await client.RequestAsync("launch", new { operationId = "op1", accountName = "acc", dalamud = false, platform, weGameLogin = true, password = "pw-123456" });

        Assert.False(response["result"]!["accepted"]!.GetValue<bool>());
        Assert.Equal("invalidParams", response["result"]!["code"]!.GetValue<string>());
        Assert.False(host.HasLaunch);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("shengqu")]
    [InlineData("international")]
    public async Task Launch_WeGameLoginFalse_OnOtherPlatform_IsAccepted(string? platform)
    {
        await using var client = await ConnectAndHelloAsync();

        var response = platform == null
                           ? await client.RequestAsync("launch", new { operationId = "op1", accountName = "acc", dalamud = false, weGameLogin = false, password = "pw-123456" })
                           : await client.RequestAsync("launch", new { operationId = "op1", accountName = "acc", dalamud = false, platform, weGameLogin = false, password = "pw-123456" });

        Assert.True(response["result"]!["accepted"]!.GetValue<bool>());
        await runner.Started.Task.WaitAsync(Timeout);
        Assert.False(runner.Request!.WeGameLogin);
    }

    [Fact]
    public async Task Launch_WeGame_WithHandedOffToken_PassesIt_AndNeverPrintsIt()
    {
        const string WE_GAME_TOKEN = "handed-off-wegame-token-7f3a";
        await using var client = await ConnectAndHelloAsync();

        var response = await client.RequestAsync
        (
            "launch",
            new { operationId = "op1", accountName = "123456", dalamud = false, platform = "weGame", weGameLogin = true, weGameToken = WE_GAME_TOKEN, weGameAccountId = " 76561197988926417 " }
        );

        Assert.True(response["result"]!["accepted"]!.GetValue<bool>());
        await runner.Started.Task.WaitAsync(Timeout);

        var request = runner.Request!;
        Assert.Equal(WE_GAME_TOKEN, request.WeGameToken!.Reveal());
        Assert.Equal("76561197988926417", request.WeGameAccountId);
        Assert.Equal
        (
            new CatLaunchRequest
            (
                "op1",
                "123456",
                false,
                null,
                Platform: XIVAccountType.WeGame,
                WeGameLogin: true,
                WeGameToken: new CatSecret(WE_GAME_TOKEN),
                WeGameAccountId: "76561197988926417"
            ),
            request
        );
        Assert.DoesNotContain(WE_GAME_TOKEN, request.ToString());
        Assert.DoesNotContain(WE_GAME_TOKEN, $"{request}");

        // 已登记脱敏: 启动器不小心把它写进任何发给外壳的文字, 都会被遮住
        runner.Reporter!.Log("error", $"登录失败 {WE_GAME_TOKEN}");
        runner.Reporter.Failed(CatCodes.AUTHORIZATION_REQUIRED, $"WeGame 登录被拒绝: {WE_GAME_TOKEN}");

        var seen = new List<(string Method, JsonNode? Params)>();
        client.WaitForEvent("launch.failed", Timeout, seen);
        Assert.All(seen, x => Assert.DoesNotContain(WE_GAME_TOKEN, x.Params?.ToJsonString(CatProtocol.JsonOptions) ?? string.Empty));
    }

    [Fact]
    public async Task Launch_WeGame_WithoutHandedOffToken_IsUnchanged()
    {
        await using var client = await ConnectAndHelloAsync();

        var response = await client.RequestAsync("launch", new { operationId = "op1", accountName = "123456", dalamud = false, platform = "weGame", weGameToken = "", weGameAccountId = " " });

        Assert.True(response["result"]!["accepted"]!.GetValue<bool>());
        await runner.Started.Task.WaitAsync(Timeout);
        Assert.Equal(new CatLaunchRequest("op1", "123456", false, null, Platform: XIVAccountType.WeGame), runner.Request);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("shengqu")]
    [InlineData("international")]
    public async Task Launch_HandedOffToken_OnOtherPlatform_IsRejected(string? platform)
    {
        await using var client = await ConnectAndHelloAsync();

        var response = platform == null
                           ? await client.RequestAsync("launch", new { operationId = "op1", accountName = "acc", dalamud = false, password = "pw-123456", weGameToken = "token-123456", weGameAccountId = "123" })
                           : await client.RequestAsync("launch", new { operationId = "op1", accountName = "acc", dalamud = false, platform, password = "pw-123456", weGameToken = "token-123456", weGameAccountId = "123" });

        Assert.False(response["result"]!["accepted"]!.GetValue<bool>());
        Assert.Equal("invalidParams", response["result"]!["code"]!.GetValue<string>());
        Assert.DoesNotContain("token-123456", response.ToJsonString());
        Assert.False(host.HasLaunch);
    }

    [Theory]
    [InlineData("token-123456", null)]
    [InlineData("token-123456", "")]
    [InlineData(null, "76561197988926417")]
    [InlineData(" ", "76561197988926417")]
    public async Task Launch_HandedOffToken_WithOnlyOneField_IsRejected(string? weGameToken, string? weGameAccountId)
    {
        await using var client = await ConnectAndHelloAsync();

        var response = await client.RequestAsync("launch", new { operationId = "op1", accountName = "123456", dalamud = false, platform = "weGame", weGameToken, weGameAccountId });

        Assert.False(response["result"]!["accepted"]!.GetValue<bool>());
        Assert.Equal("invalidParams", response["result"]!["code"]!.GetValue<string>());
        Assert.False(host.HasLaunch);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("7656119798892641x")]
    [InlineData("-1")]
    [InlineData("765 611")]
    [InlineData("７６５６")]
    public async Task Launch_HandedOffToken_AccountIdNotDigits_IsRejected(string weGameAccountId)
    {
        await using var client = await ConnectAndHelloAsync();

        var response = await client.RequestAsync("launch", new { operationId = "op1", accountName = "123456", dalamud = false, platform = "weGame", weGameToken = "token-123456", weGameAccountId });

        Assert.False(response["result"]!["accepted"]!.GetValue<bool>());
        Assert.Equal("invalidParams", response["result"]!["code"]!.GetValue<string>());
        Assert.False(host.HasLaunch);
    }

    [Fact]
    public async Task Launch_WeGame_AuthOnly_PassesIt_AndDropsMinionAndAutoEnter()
    {
        const string WE_GAME_TOKEN = "handed-off-wegame-token-auth-only";
        await using var client = await ConnectAndHelloAsync();

        var response = await client.RequestAsync
        (
            "launch",
            new
            {
                operationId     = "op1",
                accountName     = "123456",
                dalamud         = true,
                minion          = MinionJson(MinionCards.VARIANT_CN),
                platform        = "weGame",
                weGameLogin     = true,
                weGameScan      = "qq",
                autoEnter       = true,
                weGameToken     = WE_GAME_TOKEN,
                weGameAccountId = "76561197988926417",
                authOnly        = true
            }
        );

        Assert.True(response["result"]!["accepted"]!.GetValue<bool>());
        await runner.Started.Task.WaitAsync(Timeout);

        var request = runner.Request!;
        Assert.True(request.AuthOnly);
        Assert.Null(request.MinionCard);
        Assert.False(request.AutoEnter);
        Assert.Equal
        (
            new CatLaunchRequest
            (
                "op1",
                "123456",
                true,
                null,
                Platform: XIVAccountType.WeGame,
                WeGameLogin: true,
                WeGameScan: CatWeGameScan.Qq,
                WeGameToken: new CatSecret(WE_GAME_TOKEN),
                WeGameAccountId: "76561197988926417",
                AuthOnly: true
            ),
            request
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)] // 不带 authOnly: 与加这个字段之前一样
    public async Task Launch_WeGame_WithoutAuthOnly_IsUnchanged(bool? authOnly)
    {
        await using var client = await ConnectAndHelloAsync();

        var response = authOnly == null
                           ? await client.RequestAsync("launch", new { operationId = "op1", accountName = "123456", dalamud = false, platform = "weGame", autoEnter = true })
                           : await client.RequestAsync("launch", new { operationId = "op1", accountName = "123456", dalamud = false, platform = "weGame", autoEnter = true, authOnly });

        Assert.True(response["result"]!["accepted"]!.GetValue<bool>());
        await runner.Started.Task.WaitAsync(Timeout);
        Assert.Equal(new CatLaunchRequest("op1", "123456", false, null, Platform: XIVAccountType.WeGame, AutoEnter: true), runner.Request);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("shengqu")]
    [InlineData("international")]
    public async Task Launch_AuthOnly_OnOtherPlatform_IsRejected(string? platform)
    {
        await using var client = await ConnectAndHelloAsync();

        var response = platform == null
                           ? await client.RequestAsync("launch", new { operationId = "op1", accountName = "acc", dalamud = false, password = "pw-123456", authOnly = true })
                           : await client.RequestAsync("launch", new { operationId = "op1", accountName = "acc", dalamud = false, platform, password = "pw-123456", authOnly = true });

        Assert.False(response["result"]!["accepted"]!.GetValue<bool>());
        Assert.Equal("invalidParams", response["result"]!["code"]!.GetValue<string>());
        Assert.False(host.HasLaunch);
    }

    [Fact]
    public async Task AuthOnly_Authorized_IsPublishedWithTheAgreedFields_AndEndsTheLaunch()
    {
        const string WE_GAME_TOKEN = "handed-off-wegame-token-authorized";
        await using var client = await ConnectAndHelloAsync();
        await client.RequestAsync
        (
            "launch",
            new { operationId = "op1", accountName = "123456", dalamud = false, platform = "weGame", weGameLogin = true, weGameToken = WE_GAME_TOKEN, weGameAccountId = "76561197988926417", authOnly = true }
        );
        await runner.Started.Task.WaitAsync(Timeout);

        runner.Reporter!.Stage(CatStages.PREPARING);
        runner.Reporter.Authorized("76561197988926417", true);

        var seen       = new List<(string Method, JsonNode? Params)>();
        var authorized = client.WaitForEvent("launch.authorized", Timeout, seen).Params!;
        Assert.Equal
        (
            """{"operationId":"op1","weGameAccountId":"76561197988926417","captured":true}""",
            authorized.ToJsonString(CatProtocol.JsonOptions)
        );
        Assert.Equal(["game.stage", "launch.authorized"], seen.Select(x => x.Method));
        Assert.All(seen, x => Assert.DoesNotContain(WE_GAME_TOKEN, x.Params?.ToJsonString(CatProtocol.JsonOptions) ?? string.Empty));
        Assert.True(host.IsAuthorized);
        Assert.False(host.HasStarted);

        var status = await client.RequestAsync("status", new { });
        Assert.Equal("authorized", status["result"]!["stage"]!.GetValue<string>());
        Assert.Null(status["result"]!["pid"]);

        // 已结束: 不再收设备验证、不能补注入或交接, close 直接回成功且不去关游戏
        var sms = await client.RequestAsync("weGame.confirmSms", new { challengeId = "s-1" });
        Assert.Equal("notRunning", sms["result"]!["code"]!.GetValue<string>());
        Assert.Empty(runner.ConfirmedSms);

        var inject = await client.RequestAsync("inject", new { dalamud = true });
        Assert.Equal("notRunning", inject["result"]!["code"]!.GetValue<string>());

        var handOff = await client.RequestAsync(CatHandOff.METHOD, new { });
        Assert.Equal("notRunning", handOff["result"]!["code"]!.GetValue<string>());

        var close = await client.RequestAsync("close", new { });
        Assert.True(close["result"]!["accepted"]!.GetValue<bool>());
        Assert.False(runner.Closed.Task.IsCompleted);

        runner.Finish.TrySetResult(CatHostRuntime.EXIT_OK);
        Assert.Equal(CatHostRuntime.EXIT_OK, await host.Completion.WaitAsync(Timeout));
    }

    [Fact]
    public void LaunchParams_DeserializeAuthOnly_AndPrintIt()
    {
        var parameters = JsonSerializer.Deserialize<CatLaunchParams>
        (
            """{"operationId":"op1","accountName":"123456","dalamud":false,"platform":"weGame","authOnly":true}""",
            CatProtocol.JsonOptions
        )!;

        Assert.True(parameters.AuthOnly);
        Assert.Contains("AuthOnly = True", parameters.ToString());
    }

    [Fact]
    public void LaunchParams_ToString_DoesNotPrintWeGameToken()
    {
        var parameters = new CatLaunchParams("op1", "123456", false, null, Platform: "weGame", WeGameToken: "TopSecret-WeGame-1", WeGameAccountId: "76561197988926417");

        Assert.DoesNotContain("TopSecret-WeGame-1", parameters.ToString());
        Assert.DoesNotContain("TopSecret-WeGame-1", $"{parameters}");
        Assert.Contains("76561197988926417", parameters.ToString());
    }

    [Fact]
    public void LaunchParams_DeserializeHandedOffTokenFromCamelCaseFields()
    {
        var parameters = JsonSerializer.Deserialize<CatLaunchParams>
        (
            """{"operationId":"op1","accountName":"123456","dalamud":false,"platform":"weGame","weGameToken":"t-123456","weGameAccountId":"76561197988926417"}""",
            CatProtocol.JsonOptions
        )!;

        Assert.Equal("t-123456", parameters.WeGameToken);
        Assert.Equal("76561197988926417", parameters.WeGameAccountId);
    }

    [Theory]
    [InlineData("qq", CatWeGameScan.Qq)]
    [InlineData("QQ", CatWeGameScan.Qq)]
    [InlineData("weChat", CatWeGameScan.WeChat)]
    [InlineData(" wechat ", CatWeGameScan.WeChat)]
    [InlineData(null, null)] // 不带 weGameScan: 不切换, 与加这个字段之前一样
    [InlineData("", null)]
    public async Task Launch_WeGameLogin_PassesWeGameScan(string? weGameScan, CatWeGameScan? expected)
    {
        await using var client = await ConnectAndHelloAsync();

        var response = weGameScan == null
                           ? await client.RequestAsync("launch", new { operationId = "op1", accountName = "123456", dalamud = false, platform = "weGame", weGameLogin = true })
                           : await client.RequestAsync("launch", new { operationId = "op1", accountName = "123456", dalamud = false, platform = "weGame", weGameLogin = true, weGameScan });

        Assert.True(response["result"]!["accepted"]!.GetValue<bool>());
        await runner.Started.Task.WaitAsync(Timeout);
        Assert.Equal(expected, runner.Request!.WeGameScan);
        Assert.Equal(new CatLaunchRequest("op1", "123456", false, null, Platform: XIVAccountType.WeGame, WeGameLogin: true, WeGameScan: expected), runner.Request);
    }

    [Theory]
    [InlineData("weGame", true, "weixin")]   // 不认识的取值
    [InlineData("weGame", true, "password")]
    [InlineData("weGame", false, "qq")]      // 没带 weGameLogin
    [InlineData("weGame", null, "weChat")]
    [InlineData("shengqu", null, "qq")]      // 别的渠道
    [InlineData(null, null, "qq")]
    public async Task Launch_WeGameScan_InvalidOrWithoutWeGameLogin_IsRejected(string? platform, bool? weGameLogin, string weGameScan)
    {
        await using var client = await ConnectAndHelloAsync();

        var response = await client.RequestAsync("launch", new { operationId = "op1", accountName = "acc", dalamud = false, platform, weGameLogin, weGameScan });

        Assert.False(response["result"]!["accepted"]!.GetValue<bool>());
        Assert.Equal("invalidParams", response["result"]!["code"]!.GetValue<string>());
        Assert.False(host.HasLaunch);
    }

    [Fact]
    public async Task WeGameNotifications_ArePublishedWithTheAgreedNamesAndFields()
    {
        await using var client = await ConnectAndHelloAsync();
        await client.RequestAsync("launch", new { operationId = "op1", accountName = "123456", dalamud = false, platform = "weGame", weGameLogin = true, weGameScan = "qq" });
        await runner.Started.Task.WaitAsync(Timeout);

        var reporter = runner.Reporter!;
        reporter.WeGameChallenge(new CatWeGameChallenge("qrcode", "q-3", "UE5H", "https://txz.qq.com/p?k=abc", 120));
        reporter.WeGameChallenge(new CatWeGameChallenge("sms", "s-1", Code: "AQLLJCAQMS", Phone: "1069070069", Text: "窗口原文"));
        reporter.WeGameSmsResult("s-1", false);
        reporter.WeGameChallengeCleared("q-3");
        reporter.WeGameScanSwitchFailed("qq");

        var qrcode = client.WaitForEvent("weGame.challenge", Timeout).Params!;
        Assert.Equal
        (
            """{"operationId":"op1","kind":"qrcode","challengeId":"q-3","image":"UE5H","link":"https://txz.qq.com/p?k=abc","expiresInSeconds":120}""",
            qrcode.ToJsonString(CatProtocol.JsonOptions)
        );

        var sms = client.WaitForEvent("weGame.challenge", Timeout).Params!;
        Assert.Equal
        (
            """{"operationId":"op1","kind":"sms","challengeId":"s-1","code":"AQLLJCAQMS","phone":"1069070069","text":"窗口原文"}""",
            sms.ToJsonString(CatProtocol.JsonOptions)
        );

        Assert.Equal
        (
            """{"operationId":"op1","challengeId":"s-1","passed":false}""",
            client.WaitForEvent("weGame.smsResult", Timeout).Params!.ToJsonString(CatProtocol.JsonOptions)
        );
        Assert.Equal
        (
            """{"operationId":"op1","challengeId":"q-3"}""",
            client.WaitForEvent("weGame.challengeCleared", Timeout).Params!.ToJsonString(CatProtocol.JsonOptions)
        );
        Assert.Equal
        (
            """{"operationId":"op1","scan":"qq"}""",
            client.WaitForEvent("weGame.scanSwitchFailed", Timeout).Params!.ToJsonString(CatProtocol.JsonOptions)
        );
    }

    [Fact]
    public async Task ConfirmSms_IsAnsweredImmediately_ByTheRunner()
    {
        await using var client = await ConnectAndHelloAsync();

        // 还没 launch: 没有在等的验证
        var early = await client.RequestAsync("weGame.confirmSms", new { challengeId = "s-1" });
        Assert.False(early["result"]!["accepted"]!.GetValue<bool>());
        Assert.Equal("notRunning", early["result"]!["code"]!.GetValue<string>());

        await client.RequestAsync("launch", new { operationId = "op1", accountName = "123456", dalamud = false, platform = "weGame", weGameLogin = true });
        await runner.Started.Task.WaitAsync(Timeout);

        var missing = await client.RequestAsync("weGame.confirmSms", new { });
        Assert.Equal("invalidParams", missing["result"]!["code"]!.GetValue<string>());

        var accepted = await client.RequestAsync("weGame.confirmSms", new { challengeId = " s-1 " });
        Assert.True(accepted["result"]!["accepted"]!.GetValue<bool>());

        var stale = await client.RequestAsync("weGame.confirmSms", new { challengeId = "s-0" });
        Assert.False(stale["result"]!["accepted"]!.GetValue<bool>());
        Assert.Equal("notRunning", stale["result"]!["code"]!.GetValue<string>());

        Assert.Equal(["s-1", "s-0"], runner.ConfirmedSms);
    }

    [Fact]
    public void ConfirmSms_RunnersThatNeverWaitForWeGame_AnswerNotRunning()
    {
        ICatGameRunner international = new CatInternationalGameRunner(new CatLogRedactor(), null!);

        var result = international.ConfirmWeGameSms("s-1");

        Assert.False(result.Accepted);
        Assert.Equal(CatCodes.NOT_RUNNING, result.Code);
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
    [InlineData("cn", false)]
    [InlineData("global", true)]
    public async Task Launch_International_MinionVariantMustBeGlobal(string variant, bool expectedAccepted)
    {
        await using var client = await ConnectAndHelloAsync();

        var response = await client.RequestAsync
        (
            "launch",
            new
            {
                operationId = "op1",
                accountName = "seAccount",
                dalamud     = false,
                platform    = "international",
                password    = "pw-123456",
                minion      = MinionJson(variant)
            }
        );

        Assert.Equal(expectedAccepted, response["result"]!["accepted"]!.GetValue<bool>());

        if (!expectedAccepted)
            Assert.Equal("invalidParams", response["result"]!["code"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("keycode")]
    [InlineData("forumId")]
    [InlineData("forumPassword")]
    public async Task Launch_MinionMissingKeycodeOrForumAccount_IsMinionNotConfigured(string missing)
    {
        await using var client = await ConnectAndHelloAsync();

        var minion = new Dictionary<string, object?>
        {
            ["cardFingerprint"] = FINGERPRINT,
            ["variant"]         = "cn",
            ["keycode"]         = missing == "keycode" ? "" : KEYCODE,
            ["uid"]             = UID,
            ["forumId"]         = missing == "forumId" ? null : FORUM_ID,
            ["forumPassword"]   = missing == "forumPassword" ? "" : FORUM_PASSWORD
        };

        var response = await client.RequestAsync("launch", new { operationId = "op1", accountName = "acc", dalamud = false, minion });

        Assert.False(response["result"]!["accepted"]!.GetValue<bool>());
        Assert.Equal(CatCodes.MINION_NOT_CONFIGURED, response["result"]!["code"]!.GetValue<string>());
        Assert.False(host.HasLaunch);
    }

    [Fact]
    public async Task Launch_MinionWithoutAnyNewField_IsMinionNotConfigured()
    {
        await using var client = await ConnectAndHelloAsync();

        // 只带指纹和 variant（旧工作台的样子）
        var response = await client.RequestAsync
        (
            "launch",
            new { operationId = "op1", accountName = "acc", dalamud = false, minion = new { cardFingerprint = FINGERPRINT, variant = "cn" } }
        );

        Assert.False(response["result"]!["accepted"]!.GetValue<bool>());
        Assert.Equal(CatCodes.MINION_NOT_CONFIGURED, response["result"]!["code"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("")]
    [InlineData("0123456789abcdef0123456789abcde")]
    [InlineData("0123456789abcdef0123456789abcdef0")]
    [InlineData("0123456789abcdef0123456789abcdeg")]
    [InlineData("11112222-3333-4444-5555-666677778888")]
    public async Task Launch_MinionUidNot32Hex_IsInvalidParams(string uid)
    {
        await using var client = await ConnectAndHelloAsync();

        var response = await client.RequestAsync
        (
            "launch",
            new
            {
                operationId = "op1",
                accountName = "acc",
                dalamud     = false,
                minion      = new { cardFingerprint = FINGERPRINT, variant = "cn", keycode = KEYCODE, uid, forumId = FORUM_ID, forumPassword = FORUM_PASSWORD }
            }
        );

        Assert.False(response["result"]!["accepted"]!.GetValue<bool>());
        Assert.Equal(CatCodes.INVALID_PARAMS, response["result"]!["code"]!.GetValue<string>());
        Assert.DoesNotContain(KEYCODE, response.ToJsonString());
        Assert.DoesNotContain(FORUM_PASSWORD, response.ToJsonString());
    }

    [Fact]
    public void MinionUid_AcceptsUpperCaseHex()
    {
        Assert.True(MinionCards.IsValidUid(UID.ToUpperInvariant()));
        Assert.True(MinionCards.IsValidUid(UID));
    }

    [Fact]
    public async Task Launch_Minion_KeycodeAndForumPasswordNeverPrinted()
    {
        await using var client = await ConnectAndHelloAsync();

        var response = await client.RequestAsync("launch", new { operationId = "op1", accountName = "acc", dalamud = true, minion = MinionJson("cn") });

        Assert.True(response["result"]!["accepted"]!.GetValue<bool>());
        await runner.Started.Task.WaitAsync(Timeout);

        var request = runner.Request!;
        Assert.Equal(KEYCODE, request.MinionCard!.Keycode.Reveal());
        Assert.Equal(FORUM_PASSWORD, request.MinionCard.ForumPassword.Reveal());
        Assert.Equal(UID, request.MinionCard.Uid);
        Assert.Equal(FORUM_ID, request.MinionCard.ForumId);

        foreach (var text in new[] { request.ToString(), request.MinionCard.ToString(), $"{request}" })
        {
            Assert.DoesNotContain(KEYCODE, text);
            Assert.DoesNotContain(FORUM_PASSWORD, text);
        }

        // 已登记脱敏: 启动器不小心把它们写进任何发给外壳的文字, 都会被遮住
        runner.Reporter!.Log("error", $"挂载失败 -minionkey={KEYCODE} -minionpass={FORUM_PASSWORD} {Uri.EscapeDataString(FORUM_PASSWORD)}");
        runner.Reporter.Failed(CatCodes.LAUNCH_FAILED, $"出错: {KEYCODE} {FORUM_PASSWORD}");

        var seen = new List<(string Method, JsonNode? Params)>();
        client.WaitForEvent("launch.failed", Timeout, seen);
        Assert.All(seen, x => Assert.DoesNotContain(KEYCODE, x.Params?.ToJsonString(CatProtocol.JsonOptions) ?? string.Empty));
        Assert.All(seen, x => Assert.DoesNotContain(FORUM_PASSWORD, x.Params?.ToJsonString(CatProtocol.JsonOptions) ?? string.Empty));
        Assert.All(seen, x => Assert.DoesNotContain(Uri.EscapeDataString(FORUM_PASSWORD), x.Params?.ToJsonString(CatProtocol.JsonOptions) ?? string.Empty));
    }

    [Fact]
    public void MinionParams_ToString_PrintsOnlyFingerprintAndVariant()
    {
        var minion     = new CatMinionParams(FINGERPRINT, "cn", KEYCODE, UID, FORUM_ID, FORUM_PASSWORD);
        var parameters = new CatLaunchParams("op1", "acc", true, minion);

        foreach (var text in new[] { minion.ToString(), parameters.ToString(), $"{parameters}" })
        {
            Assert.Contains(FINGERPRINT, text);
            Assert.DoesNotContain(KEYCODE, text);
            Assert.DoesNotContain(FORUM_PASSWORD, text);
            Assert.DoesNotContain(FORUM_ID, text);
            Assert.DoesNotContain(UID, text);
        }
    }

    [Fact]
    public void MinionParams_DeserializeFromCamelCaseFields()
    {
        var parameters = JsonSerializer.Deserialize<CatLaunchParams>
        (
            $$$"""{"operationId":"op1","accountName":"acc","dalamud":false,"minion":{"cardFingerprint":"{{{FINGERPRINT}}}","variant":"global","keycode":"{{{KEYCODE}}}","uid":"{{{UID}}}","forumId":"{{{FORUM_ID}}}","forumPassword":"{{{FORUM_PASSWORD}}}"}}""",
            CatProtocol.JsonOptions
        )!;

        Assert.Equal(new CatMinionParams(FINGERPRINT, "global", KEYCODE, UID, FORUM_ID, FORUM_PASSWORD), parameters.Minion);
    }

    /// <summary>工作台外壳靠程序集里有没有这个类型名判断启动器是否按 launch 带来的卡号直接挂 Minion, 名字和命名空间不能改</summary>
    [Fact]
    public void MinionDirect_TypeNameIsStable()
    {
        Assert.Equal("XIVLauncher.CatHost.CatMinionDirect", typeof(CatMinionDirect).FullName);
    }

    /// <summary>工作台外壳靠程序集里有没有这个类型名判断启动器是否支持国际服, 名字和命名空间不能改</summary>
    [Fact]
    public void InternationalRunner_TypeNameIsStable()
    {
        Assert.Equal("XIVLauncher.CatHost.CatInternationalGameRunner", typeof(CatInternationalGameRunner).FullName);
        Assert.True(typeof(ICatGameRunner).IsAssignableFrom(typeof(CatInternationalGameRunner)));
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
        Assert.Equal(new CatLaunchRequest("op1", "acc", false, null, Platform: expectedType), runner.Request);
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
    // 不是纯数字的号: 备注整串相同才算
    [InlineData("wang@example.com", "10000000000000007", CatWeGameAccountMatch.ByNote)]
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
            ("10000000000000007", " wang@example.com "),
            ("", "777888")
        ];
        var boxed = rows.Select(x => Tuple.Create(x.Name, x.Note)).ToArray();

        var row = CatRealGameRunner.ResolveWeGameAccount(boxed, requested, x => x.Item1, x => x.Item2, out var match);

        Assert.Equal(expectedMatch, match);
        Assert.Equal(expectedName, row?.Item1);
    }

    [Theory]
    [InlineData("陆行鸟", "豆豆柴", "豆豆柴", true)]   // 资料优先: 账号库记的是游戏里最后换到的大区
    [InlineData(null, "豆豆柴", "豆豆柴", true)]       // 账号库没记: 用资料里的
    [InlineData("不存在", "豆豆柴", "豆豆柴", true)]   // 账号库记的对不上: 用资料里的
    [InlineData("猫小胖", "不存在", null, true)]       // 资料里的对不上: 不退回账号库记的, 也不悄悄取第一个
    [InlineData(null, "不存在", null, true)]
    [InlineData("猫小胖", null, "猫小胖", false)]      // 请求没带大区: 用账号库记的
    [InlineData("猫小胖", "", "猫小胖", false)]
    [InlineData("不存在", null, null, false)]
    [InlineData(null, null, null, false)]
    public void ResolveArea_PrefersRequestedThenSaved(string? saved, string? requested, string? expected, bool expectedFromRequest)
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

    [Fact]
    public async Task Launch_WithoutAutoEnter_KeepsTheOldBehaviour()
    {
        await using var client = await ConnectAndHelloAsync();

        await client.RequestAsync("launch", new { operationId = "op1", accountName = "acc", dalamud = false });
        await runner.Started.Task.WaitAsync(Timeout);

        Assert.False(runner.Request!.AutoEnter);
        Assert.Null(runner.Request.CharacterName);
        Assert.Null(runner.Request.CharacterHomeWorld);
    }

    [Fact]
    public async Task Launch_WithCharacterAndAutoEnter_PassesThemToTheRunner()
    {
        await using var client = await ConnectAndHelloAsync();

        var response = await client.RequestAsync
        (
            "launch",
            new { operationId = "op1", accountName = "acc", dalamud = false, autoEnter = true, character = new { name = " 小白 ", homeWorld = "LaNuoXiYa" } }
        );

        Assert.True(response["result"]!["accepted"]!.GetValue<bool>());
        await runner.Started.Task.WaitAsync(Timeout);
        Assert.True(runner.Request!.AutoEnter);
        Assert.Equal("小白", runner.Request.CharacterName);
        Assert.Equal("LaNuoXiYa", runner.Request.CharacterHomeWorld);
    }

    [Fact]
    public async Task Launch_AutoEnterWithoutCharacter_IsAccepted()
    {
        await using var client = await ConnectAndHelloAsync();

        await client.RequestAsync("launch", new { operationId = "op1", accountName = "acc", dalamud = false, autoEnter = true, character = new { } });
        await runner.Started.Task.WaitAsync(Timeout);

        Assert.True(runner.Request!.AutoEnter);
        Assert.Null(runner.Request.CharacterName);
    }

    [Fact]
    public async Task Launch_International_IgnoresAutoEnter()
    {
        await using var client = await ConnectAndHelloAsync();

        await client.RequestAsync
        (
            "launch",
            new { operationId = "op1", accountName = "se", dalamud = false, platform = "international", password = "Pw-123456", autoEnter = true, character = new { name = "Xiao Bai" } }
        );
        await runner.Started.Task.WaitAsync(Timeout);

        Assert.False(runner.Request!.AutoEnter);
        Assert.Equal("Xiao Bai", runner.Request.CharacterName);
    }

    [Fact]
    public async Task Launch_CharacterNameTooLong_IsRejected()
    {
        await using var client = await ConnectAndHelloAsync();

        var response = await client.RequestAsync
        (
            "launch",
            new { operationId = "op1", accountName = "acc", dalamud = false, autoEnter = true, character = new { name = new string('名', CatLaunchHost.MAX_CHARACTER_NAME_LENGTH + 1) } }
        );

        Assert.False(response["result"]!["accepted"]!.GetValue<bool>());
        Assert.Equal("invalidParams", response["result"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task SelectCharacter_IsOnlyAcceptedWhileWaitingForAChoice()
    {
        await using var client = await ConnectAndHelloAsync();

        var early = await client.RequestAsync("selectCharacter", new { contentId = "11" });
        Assert.Equal("notRunning", early["result"]!["code"]!.GetValue<string>());

        await client.RequestAsync("launch", new { operationId = "op1", accountName = "acc", dalamud = false, autoEnter = true });
        await runner.Started.Task.WaitAsync(Timeout);
        runner.Reporter!.Started(1234, DateTimeOffset.UtcNow);
        runner.Reporter.Stage(CatStages.RUNNING);
        runner.Reporter.Stage(CatStages.ENTERING_LOBBY);

        var notYet = await client.RequestAsync("selectCharacter", new { contentId = "11" });
        Assert.Equal("notRunning", notYet["result"]!["code"]!.GetValue<string>());

        runner.Reporter.Stage(CatStages.AWAITING_CHARACTER_CHOICE);

        var missing = await client.RequestAsync("selectCharacter", new { });
        Assert.Equal("invalidParams", missing["result"]!["code"]!.GetValue<string>());

        var odd = await client.RequestAsync("selectCharacter", new { contentId = "11 OR 1=1" });
        Assert.Equal("invalidParams", odd["result"]!["code"]!.GetValue<string>());

        var accepted = await client.RequestAsync("selectCharacter", new { contentId = " 11 " });
        Assert.True(accepted["result"]!["accepted"]!.GetValue<bool>());
        Assert.Equal(["11"], runner.SelectedCharacters);

        runner.Reporter.Stage(CatStages.ENTERING_WORLD);

        var late = await client.RequestAsync("selectCharacter", new { contentId = "11" });
        Assert.Equal("notRunning", late["result"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task AutoEnterEvents_CarryTheDocumentedPayloads_AndInjectStillWorksAfterRunning()
    {
        await using var client = await ConnectAndHelloAsync();

        await client.RequestAsync("launch", new { operationId = "op1", accountName = "acc", dalamud = false, autoEnter = true });
        await runner.Started.Task.WaitAsync(Timeout);

        var reporter = runner.Reporter!;
        var listed   = new CatCharacterInfo("4611686018427387905", "小白", "LaNuoXiYa", "BaiYinXiang", "拉诺西亚", null, true, true);

        reporter.Started(1234, DateTimeOffset.UtcNow);
        reporter.Stage(CatStages.RUNNING);
        reporter.Characters(true, [listed]);
        reporter.Queueing(7);
        reporter.Queueing(null);
        reporter.AutoEnterStopped(CatAutoEnterStopCodes.LOBBY_ERROR, "大厅提示: 断开了");
        reporter.Character(listed);
        reporter.Stage(CatStages.IN_WORLD);

        var characters = client.WaitForEvent("game.characters", Timeout).Params!;
        Assert.Equal("op1", characters["operationId"]!.GetValue<string>());
        Assert.True(characters["needsChoice"]!.GetValue<bool>());

        var first = characters["characters"]![0]!;
        Assert.Equal("4611686018427387905", first["contentId"]!.GetValue<string>());
        Assert.Equal("小白", first["name"]!.GetValue<string>());
        Assert.Equal("LaNuoXiYa", first["homeWorld"]!.GetValue<string>());
        Assert.Equal("BaiYinXiang", first["currentWorld"]!.GetValue<string>());
        Assert.Equal("拉诺西亚", first["homeWorldName"]!.GetValue<string>());
        Assert.Null(first["currentWorldName"]);
        Assert.True(first["travelling"]!.GetValue<bool>());
        Assert.True(first["loginable"]!.GetValue<bool>());

        var queue = client.WaitForEvent("game.stage", Timeout).Params!;
        Assert.Equal("queueing", queue["stage"]!.GetValue<string>());
        Assert.Equal(7, queue["queuePosition"]!.GetValue<int>());

        var queueUnknown = client.WaitForEvent("game.stage", Timeout).Params!;
        Assert.Equal("queueing", queueUnknown["stage"]!.GetValue<string>());
        Assert.Null(queueUnknown["queuePosition"]);

        var stopped = client.WaitForEvent("game.autoEnterStopped", Timeout).Params!;
        Assert.Equal("lobbyError", stopped["code"]!.GetValue<string>());
        Assert.Equal("大厅提示: 断开了", stopped["message"]!.GetValue<string>());

        var entered = client.WaitForEvent("game.character", Timeout).Params!;
        Assert.Equal("op1", entered["operationId"]!.GetValue<string>());
        Assert.Equal("4611686018427387905", entered["contentId"]!.GetValue<string>());
        Assert.Equal("小白", entered["name"]!.GetValue<string>());
        Assert.Equal("LaNuoXiYa", entered["homeWorld"]!.GetValue<string>());
        Assert.Equal("BaiYinXiang", entered["currentWorld"]!.GetValue<string>());
        Assert.Null(entered["travelling"]);

        Assert.Equal("inWorld", client.WaitForEvent("game.stage", Timeout).Params!["stage"]!.GetValue<string>());

        var status = await client.RequestAsync("status", new { });
        Assert.Equal("inWorld", status["result"]!["stage"]!.GetValue<string>());

        // 进了游戏之后照样能补注入
        var inject = await client.RequestAsync("inject", new { minion = true });
        Assert.True(inject["result"]!["accepted"]!.GetValue<bool>());
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
