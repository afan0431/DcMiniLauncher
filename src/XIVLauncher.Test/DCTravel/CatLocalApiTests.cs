using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using XIVLauncher.CatHost;
using XIVLauncher.DCTravel;
using XIVLauncher.Test.CatHost;
using Xunit;

namespace XIVLauncher.Test.DCTravel;

/// <summary>
///     本机接口 <c>/cat/job</c>、<c>/cat/progress</c> 走真 HTTP: 返回码、体、拒绝带 Origin 的请求; 跨区监听与模拟模式的独立监听都挂得上
/// </summary>
public sealed class CatLocalApiTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static CatJobChannel NewChannel(List<string> published) =>
        new(() => "op-1", (method, _) =>
            {
                lock (published)
                    published.Add(method);
            }
        );

    [Fact]
    public async Task StandaloneListener_ServesJobAndProgress()
    {
        var published = new List<string>();
        var channel   = NewChannel(published);
        var port      = FreePort();

        using var listener = new CatLocalListener(port, channel);
        Assert.True(await listener.StartAsync(Timeout));

        var baseUrl = $"http://127.0.0.1:{port}/cat";

        Assert.Equal(HttpStatusCode.NotFound, (await Http.GetAsync($"{baseUrl}/job?pid=1234")).StatusCode);

        channel.GameStarted(1234);
        Assert.Equal(HttpStatusCode.NoContent, (await Http.GetAsync($"{baseUrl}/job?pid=1234")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Http.GetAsync($"{baseUrl}/job")).StatusCode);

        channel.SetJob(CatJobChannelTests.Job(2));
        var job = await Http.GetAsync($"{baseUrl}/job?pid=1234");
        Assert.Equal(HttpStatusCode.OK, job.StatusCode);
        Assert.Equal("application/json", job.Content.Headers.ContentType?.MediaType);
        Assert.Equal("七色花", JsonNode.Parse(await job.Content.ReadAsStringAsync())!["character"]!["name"]!.GetValue<string>());

        var progress = await Http.PostAsync($"{baseUrl}/progress", new StringContent(CatJobChannelTests.Progress(1234, 1), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, progress.StatusCode);
        Assert.Equal(2, JsonNode.Parse(await progress.Content.ReadAsStringAsync())!["revision"]!.GetValue<long>());

        var malformed = await Http.PostAsync($"{baseUrl}/progress", new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.NotNull(JsonNode.Parse(await malformed.Content.ReadAsStringAsync())!["error"]);

        var otherPid = await Http.PostAsync($"{baseUrl}/progress", new StringContent(CatJobChannelTests.Progress(999, 1), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NotFound, otherPid.StatusCode);

        lock (published)
            Assert.Equal([CatJobChannel.PROGRESS_EVENT], published);
    }

    [Fact]
    public async Task StandaloneListener_RejectsCrossOriginRequests()
    {
        var published = new List<string>();
        var channel   = NewChannel(published);
        channel.GameStarted(1234);
        channel.SetJob(CatJobChannelTests.Job(2));

        var port = FreePort();

        using var listener = new CatLocalListener(port, channel);
        Assert.True(await listener.StartAsync(Timeout));

        using var get = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/cat/job?pid=1234");
        get.Headers.Add("Origin", "https://example.invalid");
        Assert.Equal(HttpStatusCode.Forbidden, (await Http.SendAsync(get)).StatusCode);

        using var post = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/cat/progress")
        {
            Content = new StringContent(CatJobChannelTests.Progress(1234, 1), Encoding.UTF8, "application/json")
        };
        post.Headers.Add("Origin", "https://example.invalid");
        Assert.Equal(HttpStatusCode.Forbidden, (await Http.SendAsync(post)).StatusCode);

        lock (published)
            Assert.Empty(published);
    }

    [Fact]
    public async Task StandaloneListener_OversizedBodyIs400()
    {
        var channel = NewChannel([]);
        channel.GameStarted(1234);

        var port = FreePort();

        using var listener = new CatLocalListener(port, channel);
        Assert.True(await listener.StartAsync(Timeout));

        var body     = new string(' ', 70 * 1024) + CatJobChannelTests.Progress(1234, 1);
        var response = await Http.PostAsync($"http://127.0.0.1:{port}/cat/progress", new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task DcTravelListener_ServesTheSameRoutes_AndKeepsItsOwn()
    {
        var channel = NewChannel([]);
        channel.GameStarted(1234);
        channel.SetJob(CatJobChannelTests.Job(5));

        var port = FreePort();

        using var client   = new DCTravelClient(string.Empty);
        using var listener = new DCTravelListener(client, port, false);

        // 没装处理方时两条路由回 404
        _ = listener.StartAsync();
        Assert.True(await listener.WaitListeningAsync(Timeout));
        Assert.Equal(HttpStatusCode.NotFound, (await Http.GetAsync($"http://127.0.0.1:{port}/cat/job?pid=1234")).StatusCode);

        listener.CatEndpoints = channel;

        var job = await Http.GetAsync($"http://127.0.0.1:{port}/cat/job?pid=1234");
        Assert.Equal(HttpStatusCode.OK, job.StatusCode);
        Assert.Equal(5, JsonNode.Parse(await job.Content.ReadAsStringAsync())!["revision"]!.GetValue<long>());

        // 原有的游戏内跨区路由照旧（没装钩子时回成形的错误, 不是 404）
        var status = await Http.GetAsync($"http://127.0.0.1:{port}/dctravel/ingame-travel/status?pid=1234");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.False(JsonDocument.Parse(await status.Content.ReadAsStringAsync()).RootElement.GetProperty("Ok").GetBoolean());

        listener.StopListening();
    }
}
