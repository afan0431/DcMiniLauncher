using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using XIVLauncher.CatHost;
using Xunit;

namespace XIVLauncher.Test.CatHost;

/// <summary>
///     任务单通道（cat-job/1）: setJob 的校验与 stale、Afan 两条本机接口的返回码、回报原样转发与 seq 去重、等级有变化才发
/// </summary>
public sealed class CatJobChannelTests
{
    private const string OPERATION = "op-1";

    private readonly ConcurrentQueue<(string Method, string Json)> events = new();
    private string? operationId = OPERATION;

    private CatJobChannel NewChannel() =>
        new(() => operationId, (method, parameters) => events.Enqueue((method, JsonSerializer.Serialize(parameters, CatProtocol.JsonOptions))));

    internal static JsonElement Job(long revision, string sessionId = OPERATION, string schema = CatJobChannel.JOB_SCHEMA, string desired = "run") =>
        JsonDocument.Parse
        (
            $$"""
              {
                "schema": "{{schema}}",
                "sessionId": "{{sessionId}}",
                "taskId": "t-1",
                "revision": {{revision}},
                "desired": "{{desired}}",
                "character": { "name": "七色花", "homeWorld": "萌芽池", "homeWorldId": 1167 },
                "items": [
                  { "itemId": "a", "order": 0, "kind": "leveling", "title": "驯兽师", "classJobId": 43, "start": 1, "current": 18, "target": 30, "unit": null, "done": false, "routine": "bst-level" },
                  { "itemId": "b", "order": 1, "kind": "collect", "title": "金碟币", "classJobId": null, "start": 0, "current": 35, "target": 120, "unit": "万", "done": false, "routine": "mgp" },
                  { "itemId": "c", "order": 2, "kind": "leveling", "title": "骑士", "classJobId": 19, "start": 90, "current": 94, "target": 100, "unit": null, "done": false, "routine": null }
                ],
                "shots": { "completion": true, "progressAt": null }
              }
              """
        ).RootElement.Clone();

    internal static string Progress(int pid, long seq, string state = "running", string? reason = null, long revision = 1) =>
        $$"""
          {"schema":"cat-progress/1","pid":{{pid}},"revision":{{revision}},"seq":{{seq}},"state":"{{state}}","reason":{{reason ?? "null"}},"activeItemId":"a",
           "items":[{"itemId":"a","current":19,"done":false}],"character":{"name":"七色花","homeWorldId":1167},"afan":"0.4.11","at":"2026-10-16T14:02:11Z"}
          """;

    [Fact]
    public void SetJob_AcceptsNewer_StaleForSameOrOlder()
    {
        var channel = NewChannel();
        var changed = 0;
        channel.Changed += () => changed++;

        Assert.Equal(CatSetJobResult.Ok(), channel.SetJob(Job(3)));
        Assert.Equal(3, channel.Revision);

        Assert.Equal(CatSetJobResult.StaleOk(), channel.SetJob(Job(3)));
        Assert.Equal(CatSetJobResult.StaleOk(), channel.SetJob(Job(2)));
        Assert.Equal(CatSetJobResult.Ok(), channel.SetJob(Job(4, desired: "pause")));

        Assert.Equal(2, changed);
        Assert.Equal(4, channel.Revision);
        Assert.Contains("\"pause\"", channel.JobJson);
    }

    [Fact]
    public void SetJob_RejectsJobsForSomeoneElse()
    {
        var channel = NewChannel();

        Assert.Equal(CatCodes.JOB_MISMATCH, channel.SetJob(Job(1, sessionId: "other")).Code);
        Assert.Equal(CatCodes.JOB_MISMATCH, channel.SetJob(Job(1, schema: "cat-job/2")).Code);

        operationId = null;
        Assert.Equal(CatCodes.JOB_MISMATCH, channel.SetJob(Job(1)).Code);

        Assert.Null(channel.JobJson);
        Assert.Equal(0, channel.Revision);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("""{"schema":"cat-job/1","sessionId":"op-1"}""")]
    [InlineData("""{"schema":"cat-job/1","sessionId":"op-1","revision":-1}""")]
    [InlineData("""{"schema":"cat-job/1","sessionId":"op-1","revision":1.5}""")]
    [InlineData("""{"schema":"cat-job/1","sessionId":"op-1","revision":"3"}""")]
    public void SetJob_MalformedIsInvalidParams(string json)
    {
        var result = NewChannel().SetJob(JsonDocument.Parse(json).RootElement.Clone());

        Assert.False(result.Accepted);
        Assert.Equal(CatCodes.INVALID_PARAMS, result.Code);
    }

    [Fact]
    public void SetJob_Missing_IsInvalidParams() =>
        Assert.Equal(CatCodes.INVALID_PARAMS, NewChannel().SetJob(null).Code);

    [Fact]
    public void GetJob_AnswersOnlyTheGuardedGame()
    {
        var channel = NewChannel();

        Assert.Equal(400, channel.GetJob(null).StatusCode);
        Assert.Equal(404, channel.GetJob(1234).StatusCode);

        channel.GameStarted(1234);
        Assert.Equal(204, channel.GetJob(1234).StatusCode);
        Assert.Null(channel.GetJob(1234).Json);
        Assert.Equal(404, channel.GetJob(999).StatusCode);

        channel.SetJob(Job(3));
        var response = channel.GetJob(1234);
        Assert.Equal(200, response.StatusCode);
        Assert.Equal(3, JsonNode.Parse(response.Json!)!["revision"]!.GetValue<long>());
        Assert.Equal("七色花", JsonNode.Parse(response.Json!)!["character"]!["name"]!.GetValue<string>());

        channel.GameEnded(1234);
        Assert.Equal(404, channel.GetJob(1234).StatusCode);
    }

    [Fact]
    public void PostProgress_PublishesTheReportAsIsPlusOperationId_OncePerSeq()
    {
        var channel = NewChannel();
        channel.GameStarted(1234);
        channel.SetJob(Job(3));

        var first = channel.PostProgress(Progress(1234, 1));
        Assert.Equal(200, first.StatusCode);
        Assert.Equal(3, JsonNode.Parse(first.Json!)!["revision"]!.GetValue<long>());

        // 重发同一条: 照样 200, 不再发事件
        Assert.Equal(200, channel.PostProgress(Progress(1234, 1)).StatusCode);
        Assert.Equal(200, channel.PostProgress(Progress(1234, 2, "blocked", """{"code":"noProgress","text":"30 分钟没有进展"}""")).StatusCode);

        var published = events.Where(x => x.Method == CatJobChannel.PROGRESS_EVENT).Select(x => JsonNode.Parse(x.Json)!.AsObject()).ToArray();
        Assert.Equal(2, published.Length);

        var report = published[0];
        Assert.Equal(OPERATION, report["operationId"]!.GetValue<string>());
        Assert.Equal("cat-progress/1", report["schema"]!.GetValue<string>());
        Assert.Equal(1234, report["pid"]!.GetValue<int>());
        Assert.Equal(1, report["seq"]!.GetValue<long>());
        Assert.True(report.ContainsKey("reason"));
        Assert.Null(report["reason"]);
        Assert.Equal("0.4.11", report["afan"]!.GetValue<string>());
        Assert.Equal(19, report["items"]![0]!["current"]!.GetValue<int>());

        Assert.Equal("noProgress", published[1]["reason"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public void PostProgress_WithoutJob_AnswersRevisionZero()
    {
        var channel = NewChannel();
        channel.GameStarted(1234);

        var response = channel.PostProgress(Progress(1234, 1, "waiting", revision: 0));

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(0, JsonNode.Parse(response.Json!)!["revision"]!.GetValue<long>());
    }

    [Fact]
    public void PostProgress_OtherPidIs404_AndANewGameStartsSeqAgain()
    {
        var channel = NewChannel();
        channel.GameStarted(1234);

        Assert.Equal(404, channel.PostProgress(Progress(999, 1)).StatusCode);
        Assert.Equal(200, channel.PostProgress(Progress(1234, 1)).StatusCode);

        // 崩溃重开: 新进程的 Afan 从 1 数起
        channel.GameStarted(5678);
        Assert.Equal(404, channel.PostProgress(Progress(1234, 2)).StatusCode);
        Assert.Equal(200, channel.PostProgress(Progress(5678, 1)).StatusCode);

        Assert.Equal(2, events.Count(x => x.Method == CatJobChannel.PROGRESS_EVENT));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"schema":"cat-progress/2","pid":1234,"revision":1,"seq":1,"state":"running"}""")]
    [InlineData("""{"schema":"cat-progress/1","revision":1,"seq":1,"state":"running"}""")]
    [InlineData("""{"schema":"cat-progress/1","pid":"1234","revision":1,"seq":1,"state":"running"}""")]
    [InlineData("""{"schema":"cat-progress/1","pid":1234,"revision":-1,"seq":1,"state":"running"}""")]
    [InlineData("""{"schema":"cat-progress/1","pid":1234,"revision":1,"seq":0,"state":"running"}""")]
    [InlineData("""{"schema":"cat-progress/1","pid":1234,"revision":1,"seq":1,"state":"working"}""")]
    [InlineData("""{"schema":"cat-progress/1","pid":1234,"revision":1,"seq":1,"state":"blocked","reason":null}""")]
    [InlineData("""{"schema":"cat-progress/1","pid":1234,"revision":1,"seq":1,"state":"mismatch","reason":{"text":"角色不符"}}""")]
    [InlineData("""{"schema":"cat-progress/1","pid":1234,"revision":1,"seq":1,"state":"running","items":{}}""")]
    [InlineData("""{"schema":"cat-progress/1","pid":1234,"revision":1,"seq":1,"state":"running","shots":"x"}""")]
    public void PostProgress_MalformedIs400(string body)
    {
        var channel = NewChannel();
        channel.GameStarted(1234);

        var response = channel.PostProgress(body);

        Assert.Equal(400, response.StatusCode);
        Assert.NotNull(JsonNode.Parse(response.Json!)!["error"]);
        Assert.Empty(events);
    }

    [Fact]
    public void ReportLevels_OnlyWhenChanged_AndAgainForANewGame()
    {
        var channel = NewChannel();
        channel.GameStarted(1234);

        channel.ReportLevels(1234, [100, 80, 0]);
        channel.ReportLevels(0, [100, 80, 0]);
        channel.ReportLevels(1234, [100, 81, 0]);

        channel.GameStarted(5678);
        channel.ReportLevels(0, [100, 81, 0]);

        var published = events.Where(x => x.Method == CatJobChannel.LEVELS_EVENT).Select(x => JsonNode.Parse(x.Json)!).ToArray();

        Assert.Equal(3, published.Length);
        Assert.Equal([1234, 1234, 5678], published.Select(x => x["pid"]!.GetValue<int>()));
        Assert.Equal([100, 81, 0], published[1]["levels"]!.AsArray().Select(x => x!.GetValue<int>()));
        Assert.All(published, x => Assert.Equal(OPERATION, x["operationId"]!.GetValue<string>()));
    }

    [Fact]
    public void ReportLevels_WithoutAGame_IsDropped()
    {
        NewChannel().ReportLevels(0, [1, 2, 3]);
        Assert.Empty(events);
    }

    [Fact]
    public void Restore_TakesOnlyThisOperationsJob_WithoutRaisingChanged()
    {
        var channel = NewChannel();
        var changed = 0;
        channel.Changed += () => changed++;

        Assert.False(channel.Restore(null));
        Assert.False(channel.Restore(Job(5, sessionId: "other")));
        Assert.True(channel.Restore(Job(5)));
        Assert.Equal(5, channel.Revision);

        // 工作台随后再发的同一版不算新的
        Assert.Equal(CatSetJobResult.StaleOk(), channel.SetJob(Job(5)));
        Assert.Equal(0, changed);
    }

    [Fact]
    public void JobElement_RoundTripsTheOriginal()
    {
        var channel = NewChannel();
        channel.SetJob(Job(7));

        var element = channel.JobElement!.Value;

        Assert.Equal(7, element.GetProperty("revision").GetInt64());
        Assert.Equal(3, element.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Host_DispatchesSetJob_AndTracksTheGamePid()
    {
        var runner = new FakeGameRunner();
        var host   = new CatLaunchHost(runner, (_, _) => Task.CompletedTask, new CatLogRedactor());

        // 还没 launch: 不知道任务单属于哪一次上号
        var early = (CatSetJobResult)(await host.HandleAsync(CatJobChannel.METHOD, Params(Job(1)), CancellationToken.None))!;
        Assert.Equal(CatCodes.JOB_MISMATCH, early.Code);

        Assert.True(host.Launch(new CatLaunchParams(OPERATION, "acc", false, null)).Accepted);
        await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var accepted = (CatSetJobResult)(await host.HandleAsync(CatJobChannel.METHOD, Params(Job(1)), CancellationToken.None))!;
        Assert.True(accepted.Accepted);
        Assert.Null(accepted.Stale);

        var stale = (CatSetJobResult)(await host.HandleAsync(CatJobChannel.METHOD, Params(Job(1)), CancellationToken.None))!;
        Assert.True(stale.Stale);

        Assert.Equal(404, host.Jobs.GetJob(4321).StatusCode);
        host.Started(4321, DateTimeOffset.UtcNow);
        Assert.Equal(200, host.Jobs.GetJob(4321).StatusCode);
        Assert.Same(host.Jobs, ((ICatLaunchReporter)host).Jobs);

        host.Exited(4321, 0);
        Assert.Equal(404, host.Jobs.GetJob(4321).StatusCode);

        runner.Finish.TrySetResult(0);
    }

    [Fact]
    public void SetJobResult_SerializesLikeTheContract()
    {
        Assert.Equal("""{"accepted":true}""", JsonSerializer.Serialize(CatSetJobResult.Ok(), CatProtocol.JsonOptions));
        Assert.Equal("""{"accepted":true,"stale":true}""", JsonSerializer.Serialize(CatSetJobResult.StaleOk(), CatProtocol.JsonOptions));
        Assert.StartsWith("""{"accepted":false,"code":"jobMismatch",""", JsonSerializer.Serialize(CatSetJobResult.Rejected(CatCodes.JOB_MISMATCH, "x"), CatProtocol.JsonOptions));
    }

    private static JsonElement Params(JsonElement job) =>
        JsonDocument.Parse(JsonSerializer.Serialize(new { job })).RootElement.Clone();
}
