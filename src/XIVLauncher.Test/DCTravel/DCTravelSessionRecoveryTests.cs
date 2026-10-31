using System.Net;
using System.Net.Http;
using System.Text;
using XIVLauncher.DCTravel;
using XIVLauncher.InGame;
using Xunit;

namespace XIVLauncher.Test.DCTravel;

/// <summary>
///     跨区会话未初始化时的自动恢复、查角色不吞异常、登出超时降级
/// </summary>
public sealed class DCTravelSessionRecoveryTests
{
    private const string BUSY = """{"return_code":-10242504,"return_message":"服务器忙，请稍后重试"}""";
    private const string OK_EMPTY_ROLES = """{"return_code":0,"data":{"resultCode":0,"roleList":"[]"}}""";
    private const string OK_PAGE = """{"return_code":0,"data":{}}""";

    /// <summary>pageInit 的前 N 次回「服务器忙」, 之后成功; 其余接口一律返回空角色列表</summary>
    private sealed class StubHandler(int pageInitFailures) : HttpMessageHandler
    {
        private int pageInitCalls;

        public int PageInitCalls => Volatile.Read(ref pageInitCalls);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body;

            if (request.RequestUri!.AbsolutePath.EndsWith("pageInit", StringComparison.Ordinal))
                body = Interlocked.Increment(ref pageInitCalls) <= pageInitFailures ? BUSY : OK_PAGE;
            else
                body = OK_EMPTY_ROLES;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private static DCTravelClient NewClient(StubHandler handler)
    {
        var client = new DCTravelClient(string.Empty, handler)
        {
            RequestRecoveryCooldown = TimeSpan.Zero,
            RecoveryBackoff         = _ => TimeSpan.FromMilliseconds(20)
        };
        return client;
    }

    [Fact]
    public async Task Request_WhenUninitialized_RecoversSessionThenContinues()
    {
        var handler = new StubHandler(0);
        using var client = NewClient(handler);

        var roles = await client.QueryRoleList(1, 1);

        Assert.Empty(roles);
        Assert.True(client.IsInitialized);
        Assert.Equal(1, handler.PageInitCalls);
    }

    [Fact]
    public async Task Request_WhenRecoveryFails_ThrowsRealReason_AndLaterRecovers()
    {
        var handler = new StubHandler(1);
        using var client = NewClient(handler);

        var ex = await Assert.ThrowsAsync<DCTravelAPIException>(() => client.QueryRoleList(1, 1));
        Assert.Contains("重建会话失败", ex.Message);
        Assert.Contains("RefreshDcTravelSessionIdFunc", ex.Message);
        Assert.False(client.IsInitialized);

        var roles = await client.QueryRoleList(1, 1);
        Assert.Empty(roles);
        Assert.True(client.IsInitialized);
    }

    [Fact]
    public async Task Request_DuringCooldown_ReusesLastFailure_WithoutHittingServer()
    {
        var handler = new StubHandler(100);
        using var client = NewClient(handler);
        client.RequestRecoveryCooldown = TimeSpan.FromMinutes(1);

        await Assert.ThrowsAsync<DCTravelAPIException>(() => client.QueryRoleList(1, 1));
        var callsAfterFirst = handler.PageInitCalls;

        var ex = await Assert.ThrowsAsync<DCTravelAPIException>(() => client.QueryRoleList(1, 1));

        Assert.Contains("刚重建会话失败", ex.Message);
        Assert.Equal(callsAfterFirst, handler.PageInitCalls);
    }

    [Fact]
    public async Task KeepCookieAlive_WhenUninitialized_RetriesInBackgroundUntilInitialized()
    {
        var handler = new StubHandler(2);
        using var client = NewClient(handler);

        _ = client.KeepCookieAlive();

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!client.IsInitialized && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        client.EndSession();

        Assert.True(client.IsInitialized);
        Assert.Equal(3, handler.PageInitCalls);
    }

    [Fact]
    public void DefaultRecoveryBackoff_DoublesFromFifteenSeconds_CappedAtFiveMinutes()
    {
        Assert.Equal(TimeSpan.FromSeconds(15), DCTravelClient.DefaultRecoveryBackoff(0));
        Assert.Equal(TimeSpan.FromSeconds(30), DCTravelClient.DefaultRecoveryBackoff(1));
        Assert.Equal(TimeSpan.FromSeconds(60), DCTravelClient.DefaultRecoveryBackoff(2));
        Assert.Equal(TimeSpan.FromSeconds(300), DCTravelClient.DefaultRecoveryBackoff(5));
        Assert.Equal(TimeSpan.FromSeconds(300), DCTravelClient.DefaultRecoveryBackoff(50));
    }

    [Fact]
    public async Task FindCharacter_WhenQueryFails_PropagatesReason_NotNull()
    {
        using var client = NewClient(new StubHandler(100));
        var coordinator  = new InGameTravelCoordinator(client);
        var group        = new DCTravelGroup { AreaID = 1, GroupID = 1 };

        var ex = await Assert.ThrowsAsync<DCTravelAPIException>(() => coordinator.FindCharacterAsync("Zepher", group));

        Assert.Contains("重建会话失败", ex.Message);
    }

    [Fact]
    public async Task FindCharacter_WhenRoleListHasNoSuchName_ReturnsNull()
    {
        using var client = NewClient(new StubHandler(0));
        var coordinator  = new InGameTravelCoordinator(client);
        var group        = new DCTravelGroup { AreaID = 1, GroupID = 1 };

        Assert.Null(await coordinator.FindCharacterAsync("Zepher", group));
    }

    private sealed class ScriptedModule(params string[] responses) : IMiniModuleChannel
    {
        private readonly Queue<string> queue = new(responses);

        public List<string> Sent { get; } = [];

        public Task<string> SendAsync(string command, CancellationToken cancellationToken)
        {
            Sent.Add(command);
            return Task.FromResult(queue.Count > 0 ? queue.Dequeue() : "FAIL unexpected");
        }
    }

    [Fact]
    public async Task Logout_Ok_SendsOnlyLogout()
    {
        var module = new ScriptedModule("OK");

        await InGameTravelService.LogoutFromWorldAsync(module, CancellationToken.None);

        Assert.Equal(["LOGOUT"], module.Sent);
    }

    [Fact]
    public async Task Logout_Timeout_StillInWorld_FallsBackToDirect()
    {
        var module = new ScriptedModule("FAIL logout-timeout", "OK where=ingame", "OK");

        await InGameTravelService.LogoutFromWorldAsync(module, CancellationToken.None);

        Assert.Equal(["LOGOUT", "WHERE", "LOGOUT DIRECT"], module.Sent);
    }

    [Fact]
    public async Task Logout_Timeout_AlreadyOutOfWorld_DoesNotSendDirect()
    {
        var module = new ScriptedModule("FAIL logout-timeout", "OK where=charaselect");

        await InGameTravelService.LogoutFromWorldAsync(module, CancellationToken.None);

        Assert.Equal(["LOGOUT", "WHERE"], module.Sent);
    }

    [Fact]
    public async Task Logout_TimeoutThenDirectFails_ThrowsWithBothResponses()
    {
        var module = new ScriptedModule("FAIL logout-timeout", "OK where=ingame", "FAIL busy");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => InGameTravelService.LogoutFromWorldAsync(module, CancellationToken.None));

        Assert.Contains("logout-timeout", ex.Message);
        Assert.Contains("FAIL busy", ex.Message);
    }

    [Fact]
    public async Task Logout_OtherFailure_DoesNotRetry()
    {
        var module = new ScriptedModule("FAIL no-agent");

        await Assert.ThrowsAsync<InvalidOperationException>(() => InGameTravelService.LogoutFromWorldAsync(module, CancellationToken.None));

        Assert.Equal(["LOGOUT"], module.Sent);
    }
}
