using System.Collections.Concurrent;
using System.IO;
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

    /// <summary>截图目录（只用来比对路径, 不用真的存在）</summary>
    internal static readonly string ShotsDirectory = Path.Combine(Path.GetTempPath(), "dml-cat-shots-for-tests");

    private CatJobChannel NewChannel(TimeSpan? minInterval = null) =>
        new(() => operationId, (method, parameters) => events.Enqueue((method, JsonSerializer.Serialize(parameters, CatProtocol.JsonOptions))))
        {
            ShotsDirectory      = ShotsDirectory,
            MinProgressInterval = minInterval ?? TimeSpan.Zero
        };

    internal static string ProgressWithShot(int pid, long seq, string file, string kind = "completion") =>
        Progress(pid, seq).TrimEnd()[..^1] + $$""","shots":[{"itemId":"a","kind":"{{kind}}","file":{{JsonSerializer.Serialize(file)}},"at":"2026-10-16T14:02:11Z"}]}""";

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

    /// <summary>startShot 各项: a 驯兽师 / c 骑士（人做）为练级、b 金碟币 / d 糖豆人声誉为货币一组、e 守护天节为勾选类、f 主线不知道开哪个界面, 都为 true; g 白魔法师为 false</summary>
    internal static readonly bool[] StartShotFlags = [true, true, true, true, true, true, false];

    internal static JsonElement JobWithStartShots(long revision, string desired = "run") =>
        JsonDocument.Parse
        (
            $$"""
              {
                "schema": "{{CatJobChannel.JOB_SCHEMA}}",
                "sessionId": "{{OPERATION}}",
                "taskId": "t-1",
                "revision": {{revision}},
                "desired": "{{desired}}",
                "character": { "name": "七色花", "homeWorld": "萌芽池", "homeWorldId": 1167 },
                "items": [
                  { "itemId": "a", "order": 0, "kind": "leveling", "title": "驯兽师", "classJobId": 43, "start": 1, "current": 18, "target": 30, "unit": null, "done": false, "routine": "bst-level", "startShot": true },
                  { "itemId": "b", "order": 1, "kind": "collect", "title": "金碟币", "classJobId": null, "start": 0, "current": 35, "target": 120, "unit": "万", "done": false, "routine": "mgp", "startShot": true },
                  { "itemId": "c", "order": 2, "kind": "leveling", "title": "骑士", "classJobId": 19, "start": 90, "current": 94, "target": 100, "unit": null, "done": false, "routine": null, "startShot": true },
                  { "itemId": "d", "order": 3, "kind": "collect", "title": "糖豆人声誉", "classJobId": null, "start": 0, "current": 0, "target": 5000, "unit": null, "done": false, "routine": "fallguys-rep", "startShot": true },
                  { "itemId": "e", "order": 4, "kind": "periodic", "title": "守护天节", "classJobId": null, "start": null, "current": null, "target": null, "unit": null, "done": false, "routine": "allsaints-2026", "startShot": true },
                  { "itemId": "f", "order": 5, "kind": "msq", "title": "主线", "classJobId": null, "start": 600, "current": 655, "target": 700, "unit": null, "done": false, "routine": null, "startShot": true },
                  { "itemId": "g", "order": 6, "kind": "leveling", "title": "白魔法师", "classJobId": 24, "start": 90, "current": 90, "target": 100, "unit": null, "done": false, "routine": null, "startShot": false }
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

    [Fact]
    public void PostProgress_SeqNotAboveTheLargestSeen_IsNotPublishedAgain()
    {
        var channel = NewChannel();
        channel.GameStarted(1234);

        // Afan 以加载时的毫秒时间为起点: 数很大, 只增不减
        Assert.Equal(200, channel.PostProgress(Progress(1234, 1760600000000)).StatusCode);
        Assert.Equal(200, channel.PostProgress(Progress(1234, 1760600000002)).StatusCode);
        Assert.Equal(200, channel.PostProgress(Progress(1234, 1760600000001)).StatusCode);
        Assert.Equal(200, channel.PostProgress(Progress(1234, 1760600000002)).StatusCode);
        Assert.Equal(200, channel.PostProgress(Progress(1234, 1760600000003)).StatusCode);

        Assert.Equal
        (
            [1760600000000L, 1760600000002L, 1760600000003L],
            events.Where(x => x.Method == CatJobChannel.PROGRESS_EVENT).Select(x => JsonNode.Parse(x.Json)!["seq"]!.GetValue<long>())
        );
    }

    [Fact]
    public void PostProgress_AfterTheGameEnded_IsNotPublished()
    {
        var channel = NewChannel();
        channel.GameStarted(1234);
        channel.GameEnded(1234);

        Assert.Equal(404, channel.PostProgress(Progress(1234, 1)).StatusCode);
        Assert.Empty(events);
    }

    [Fact]
    public void PostProgress_UnixMillisecondSeq_StaysLongAllTheWay()
    {
        var channel = NewChannel();
        channel.GameStarted(1234);

        // Afan 的 seq 以加载时的 Unix 毫秒为起点: 13 位, 超出 int32; 截图文件名里的 seq 也是它
        const long seq  = 1797000000000;
        var        file = Path.Combine(ShotsDirectory, $"1234-{seq}.png");

        Assert.Equal(200, channel.PostProgress(ProgressWithShot(1234, seq, file)).StatusCode);
        Assert.Equal(200, channel.PostProgress(Progress(1234, seq + 1)).StatusCode);
        Assert.Equal(200, channel.PostProgress(Progress(1234, seq)).StatusCode);

        var published = events.Where(x => x.Method == CatJobChannel.PROGRESS_EVENT).Select(x => JsonNode.Parse(x.Json)!).ToArray();
        Assert.Equal([seq, seq + 1], published.Select(x => x["seq"]!.GetValue<long>()));
        Assert.Equal(file, published[0]["shots"]![0]!["file"]!.GetValue<string>());
        Assert.Contains("\"seq\":1797000000000", events.First().Json);
    }

    [Theory]
    [InlineData(true, "1234-1797000000000.png", "completion")]
    [InlineData(true, "1234-17.PNG", "progress")]
    [InlineData(true, "1234-17.png", "start")]
    [InlineData(false, "1234-17.png", "Start")]
    [InlineData(false, "1234-17.png", "")]
    [InlineData(false, "999-1797000000000.png", "completion")]
    [InlineData(false, "1234-abc.png", "completion")]
    [InlineData(false, "1234-17.jpg", "completion")]
    [InlineData(false, "1234-.png", "completion")]
    [InlineData(false, "sub\\1234-17.png", "completion")]
    [InlineData(false, "..\\1234-17.png", "completion")]
    [InlineData(false, "1234-17.png", "thumbnail")]
    public void PostProgress_ShotFilesMustBeThisGamesPngsInTheShotsDirectory(bool accepted, string name, string kind)
    {
        var channel = NewChannel();
        channel.GameStarted(1234);

        var response = channel.PostProgress(ProgressWithShot(1234, 1, Path.Combine(ShotsDirectory, name), kind));

        Assert.Equal(accepted ? 200 : 400, response.StatusCode);
    }

    [Theory]
    [InlineData("1234-17.png")]
    [InlineData("C:\\Windows\\Temp\\1234-17.png")]
    public void PostProgress_RelativeOrForeignShotPath_Is400(string file)
    {
        var channel = NewChannel();
        channel.GameStarted(1234);

        Assert.Equal(400, channel.PostProgress(ProgressWithShot(1234, 1, file)).StatusCode);
        Assert.Empty(events);
    }

    [Fact]
    public void PostProgress_OneShotForSeveralItems_EachItemCarriesTheSameFile_IsPublishedAsIs()
    {
        var channel = NewChannel();
        channel.GameStarted(1234);

        // 开工图: 练级两项（都是战斗职业）共用一张、金碟币与声誉共用一张; 文件名里的数字不必是这条回报的 seq
        const long seq    = 1797000000002;
        var        jobs   = JsonSerializer.Serialize(Path.Combine(ShotsDirectory, "1234-1797000000000.png"));
        var        wallet = JsonSerializer.Serialize(Path.Combine(ShotsDirectory, "1234-1797000000001.png"));
        var body = Progress(1234, seq).TrimEnd()[..^1] +
                   $$"""
                     ,"shots":[
                       {"itemId":"a","kind":"start","file":{{jobs}},"at":"2026-10-16T14:02:11Z"},
                       {"itemId":"b","kind":"start","file":{{wallet}},"at":"2026-10-16T14:02:11Z"},
                       {"itemId":"c","kind":"start","file":{{jobs}},"at":"2026-10-16T14:02:11Z"},
                       {"itemId":"d","kind":"start","file":{{wallet}},"at":"2026-10-16T14:02:11Z"}]}
                     """;

        Assert.Equal(200, channel.PostProgress(body).StatusCode);

        var shots = JsonNode.Parse(Assert.Single(events, x => x.Method == CatJobChannel.PROGRESS_EVENT).Json)!["shots"]!.AsArray();
        Assert.Equal(["a", "b", "c", "d"], shots.Select(x => x!["itemId"]!.GetValue<string>()));
        Assert.All(shots, x => Assert.Equal("start", x!["kind"]!.GetValue<string>()));
        Assert.Equal(shots[0]!["file"]!.GetValue<string>(), shots[2]!["file"]!.GetValue<string>());
        Assert.Equal(shots[1]!["file"]!.GetValue<string>(), shots[3]!["file"]!.GetValue<string>());
        Assert.NotEqual(shots[0]!["file"]!.GetValue<string>(), shots[1]!["file"]!.GetValue<string>());
    }

    [Fact]
    public void Job_ItemFieldsLikeStartShot_ComeBackAsIs_FromGetJobAndAfterRestore()
    {
        var channel = NewChannel();
        channel.GameStarted(1234);

        var job = JobWithStartShots(3);
        Assert.True(channel.SetJob(job).Accepted);

        // 登录器不改任务单: /cat/job 原样返回
        var served = channel.GetJob(1234).Json!;
        Assert.Equal(job.GetRawText(), served);
        Assert.Equal(StartShotFlags, JsonNode.Parse(served)!["items"]!.AsArray().Select(x => x!["startShot"]!.GetValue<bool>()));

        // 接管: 从守护记录里的任务单恢复后照样原样
        var adopted = NewChannel();
        adopted.GameStarted(1234);
        Assert.True(adopted.Restore(channel.JobElement));
        Assert.Equal(job.GetRawText(), adopted.GetJob(1234).Json);
    }

    [Fact]
    public void PostProgress_TooFrequentFromTheLocalApi_Is429()
    {
        var channel = NewChannel(TimeSpan.FromSeconds(30));
        channel.GameStarted(1234);

        Assert.Equal(200, channel.PostProgress(Progress(1234, 1)).StatusCode);
        Assert.Equal(429, channel.PostProgress(Progress(1234, 2)).StatusCode);

        // 模拟模式自己产生的回报不限
        Assert.Equal(200, channel.PostProgress(Progress(1234, 3), false).StatusCode);

        Assert.Equal(2, events.Count(x => x.Method == CatJobChannel.PROGRESS_EVENT));
    }

    [Fact]
    public void SetJob_WhenTheLocalApiIsUnavailable_IsUnsupported()
    {
        var channel = NewChannel();
        channel.UnavailableReason = "本机接口端口没开成";

        var result = channel.SetJob(Job(1));
        Assert.Equal(CatCodes.UNSUPPORTED, result.Code);
        Assert.Null(channel.JobJson);

        channel.UnavailableReason = null;
        Assert.True(channel.SetJob(Job(1)).Accepted);
    }

    [Fact]
    public async Task SetJob_ConcurrentWithReads_NeverShowsAHalfJob_AndRevisionOnlyGrows()
    {
        var channel = NewChannel();
        channel.GameStarted(1234);

        using var stop  = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var        seen = new ConcurrentBag<long>();

        var writers = Enumerable.Range(0, 4).Select
        (w => Task.Run
            (() =>
                {
                    for (var revision = w + 1; revision <= 400; revision += 4)
                        channel.SetJob(Job(revision));
                }
            )
        );

        var readers = Enumerable.Range(0, 4).Select
        (r => Task.Run
            (() =>
                {
                    long last = 0;
                    long seq  = r * 1_000_000;

                    while (!stop.IsCancellationRequested && last < 400)
                    {
                        var job = channel.GetJob(1234);

                        if (job.StatusCode == 200)
                        {
                            var revision = JsonNode.Parse(job.Json!)!["revision"]!.GetValue<long>();
                            Assert.True(revision >= last);
                            last = revision;
                            seen.Add(revision);
                        }

                        var posted = channel.PostProgress(Progress(1234, ++seq));
                        Assert.Equal(200, posted.StatusCode);
                        Assert.True(JsonNode.Parse(posted.Json!)!["revision"]!.GetValue<long>() >= 0);
                    }
                }
            )
        );

        await Task.WhenAll(writers.Concat(readers));

        Assert.Equal(400, channel.Revision);
        Assert.NotEmpty(seen);
    }

    [Fact]
    public async Task Host_AfterGameExited_NoMoreEventsForThatGame()
    {
        var runner    = new FakeGameRunner();
        var published = new ConcurrentQueue<string>();
        var host      = new CatLaunchHost(runner, (method, _) => { published.Enqueue(method); return Task.CompletedTask; }, new CatLogRedactor());

        Assert.True(host.Launch(new CatLaunchParams(OPERATION, "acc", false, null)).Accepted);
        await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        host.Jobs.MinProgressInterval = TimeSpan.Zero;
        host.Started(4321, DateTimeOffset.UtcNow);
        Assert.Equal(200, host.Jobs.PostProgress(Progress(4321, 1)).StatusCode);
        host.Levels(0, Seven, [90, 80]);
        host.Duty(0, true, DateTimeOffset.UtcNow);

        host.Exited(4321, 0);

        Assert.Equal(404, host.Jobs.PostProgress(Progress(4321, 2)).StatusCode);
        host.Levels(4321, Seven, [91, 80]);
        host.Levels(0, Seven, [92, 80]);
        host.Duty(4321, false, DateTimeOffset.UtcNow);
        host.Duty(0, false, DateTimeOffset.UtcNow);

        Assert.True(await host.DrainEventsAsync(TimeSpan.FromSeconds(5)));
        var order = published.ToArray();
        var exitedAt = Array.IndexOf(order, "game.exited");

        Assert.True(exitedAt >= 0);
        Assert.DoesNotContain(order[(exitedAt + 1)..], x => x is CatJobChannel.PROGRESS_EVENT or CatJobChannel.LEVELS_EVENT or CatJobChannel.DUTY_EVENT);
        Assert.Equal(1, order.Count(x => x == CatJobChannel.PROGRESS_EVENT));
        Assert.Equal(1, order.Count(x => x == CatJobChannel.LEVELS_EVENT));
        Assert.Equal(1, order.Count(x => x == CatJobChannel.DUTY_EVENT));

        runner.Finish.TrySetResult(0);
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
    [InlineData("""{"schema":"cat-progress/1","pid":1234,"pid":1234,"revision":1,"seq":1,"state":"running"}""")]
    [InlineData("""{"schema":"cat-progress/1","pid":1234,"revision":1,"seq":1,"state":"running","items":[{"itemId":"a","itemId":"b"}]}""")]
    public void PostProgress_MalformedIs400(string body)
    {
        var channel = NewChannel();
        channel.GameStarted(1234);

        var response = channel.PostProgress(body);

        Assert.Equal(400, response.StatusCode);
        Assert.NotNull(JsonNode.Parse(response.Json!)!["error"]);
        Assert.Empty(events);
    }

    private static readonly CatLevelsCharacter Seven = new("七色花", "40000000000000017", 1167);

    private static readonly CatLevelsCharacter Other = new("别的角色", "40000000000000099", 1042);

    [Fact]
    public void ReportLevels_OnlyWhenTheArrayOrTheCharacterChanges_AndAgainForANewGame()
    {
        var channel = NewChannel();
        channel.GameStarted(1234);

        channel.ReportLevels(1234, Seven, [100, 80, 0]);
        channel.ReportLevels(0, Seven, [100, 80, 0]);
        channel.ReportLevels(1234, Seven, [100, 81, 0]);

        // 人换了个角色, 等级碰巧一样也要发
        channel.ReportLevels(0, Other, [100, 81, 0]);

        channel.GameStarted(5678);
        channel.ReportLevels(0, Other, [100, 81, 0]);

        var published = events.Where(x => x.Method == CatJobChannel.LEVELS_EVENT).Select(x => JsonNode.Parse(x.Json)!).ToArray();

        Assert.Equal(4, published.Length);
        Assert.Equal([1234, 1234, 1234, 5678], published.Select(x => x["pid"]!.GetValue<int>()));
        Assert.Equal([100, 81, 0], published[1]["levels"]!.AsArray().Select(x => x!.GetValue<int>()));
        Assert.All(published, x => Assert.Equal(OPERATION, x["operationId"]!.GetValue<string>()));

        var character = published[0]["character"]!;
        Assert.Equal("七色花", character["name"]!.GetValue<string>());
        Assert.Equal("40000000000000017", character["contentId"]!.GetValue<string>());
        Assert.Equal(1167, character["homeWorldId"]!.GetValue<int>());
        Assert.Equal("40000000000000099", published[2]["character"]!["contentId"]!.GetValue<string>());
    }

    [Fact]
    public void ReportLevels_AllZeroOrNoCharacter_IsDropped()
    {
        var channel = NewChannel();
        channel.GameStarted(1234);

        channel.ReportLevels(0, Seven, [0, 0, 0]);
        channel.ReportLevels(0, Seven, []);
        channel.ReportLevels(0, Seven with { ContentId = "" }, [100, 80, 0]);
        channel.ReportLevels(0, Seven with { ContentId = "0" }, [100, 80, 0]);

        Assert.Empty(events);
    }

    [Fact]
    public void ReportLevels_ForAnotherPidOrWithoutAGame_IsDropped()
    {
        var channel = NewChannel();
        channel.ReportLevels(0, Seven, [1, 2, 3]);

        channel.GameStarted(1234);
        channel.ReportLevels(999, Seven, [1, 2, 3]);

        channel.GameEnded(1234);
        channel.ReportLevels(1234, Seven, [1, 2, 3]);

        Assert.Empty(events);
    }

    [Fact]
    public void ReportDuty_FirstReadingAndEachChange_AndAgainForANewGame()
    {
        var channel = NewChannel();
        var at      = new DateTimeOffset(2026, 10, 16, 22, 2, 11, 345, TimeSpan.FromHours(8));
        channel.GameStarted(1234);

        channel.ReportDuty(1234, false, at);
        channel.ReportDuty(0, false, at);
        channel.ReportDuty(0, true, at);
        channel.ReportDuty(1234, true, at);
        channel.ReportDuty(0, false, at);

        // 崩溃重开 / 接管: 新的游戏第一次读到照样发
        channel.GameStarted(5678);
        channel.ReportDuty(0, false, at);

        var published = events.Where(x => x.Method == CatJobChannel.DUTY_EVENT).Select(x => JsonNode.Parse(x.Json)!.AsObject()).ToArray();

        Assert.Equal(4, published.Length);
        Assert.Equal([false, true, false, false], published.Select(x => x["inDuty"]!.GetValue<bool>()));
        Assert.Equal([1234, 1234, 1234, 5678], published.Select(x => x["pid"]!.GetValue<int>()));
        Assert.All(published, x => Assert.Equal(["operationId", "pid", "inDuty", "at"], x.Select(p => p.Key)));
        Assert.All(published, x => Assert.Equal(OPERATION, x["operationId"]!.GetValue<string>()));
        Assert.Equal("2026-10-16T14:02:11.345Z", published[0]["at"]!.GetValue<string>());
    }

    [Fact]
    public void ReportDuty_Unknown_IsDedupedLikeAValue_AndWrittenAsNull()
    {
        var channel = NewChannel();
        var at      = new DateTimeOffset(2026, 10, 16, 22, 2, 11, 345, TimeSpan.FromHours(8));
        channel.GameStarted(1234);

        // 还没发过值: 未知不发
        channel.ReportDuty(0, null, at);
        channel.ReportDuty(0, true, at);
        channel.ReportDuty(0, null, at);
        channel.ReportDuty(0, null, at);
        channel.ReportDuty(0, true, at);
        channel.ReportDuty(0, false, at);

        // 新的游戏: 还没发过值, 未知照样不发
        channel.GameStarted(5678);
        channel.ReportDuty(0, null, at);

        var published = events.Where(x => x.Method == CatJobChannel.DUTY_EVENT).Select(x => JsonNode.Parse(x.Json)!.AsObject()).ToArray();

        Assert.Equal<bool?>([true, null, true, false], published.Select(x => x["inDuty"]?.GetValue<bool>()));
        Assert.All(published, x => Assert.Equal(["operationId", "pid", "inDuty", "at"], x.Select(p => p.Key)));
        Assert.Contains("\"inDuty\":null", events.Where(x => x.Method == CatJobChannel.DUTY_EVENT).ElementAt(1).Json, StringComparison.Ordinal);
        Assert.All(published, x => Assert.Equal("2026-10-16T14:02:11.345Z", x["at"]!.GetValue<string>()));
    }

    [Fact]
    public void ReportDuty_ForAnotherPidOrWithoutAGame_IsDropped()
    {
        var channel = NewChannel();
        channel.ReportDuty(0, true, DateTimeOffset.UtcNow);

        channel.GameStarted(1234);
        channel.ReportDuty(999, true, DateTimeOffset.UtcNow);

        channel.GameEnded(1234);
        channel.ReportDuty(1234, true, DateTimeOffset.UtcNow);
        channel.ReportDuty(0, false, DateTimeOffset.UtcNow);

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

    [Theory]
    [InlineData("international")]
    [InlineData("authOnly")]
    public async Task Host_SetJob_NobodyWillFetchIt_IsUnsupported(string kind)
    {
        var runner = new FakeGameRunner();
        var host   = new CatLaunchHost(runner, (_, _) => Task.CompletedTask, new CatLogRedactor());

        var launch = kind == "international"
                         ? new CatLaunchParams(OPERATION, "acc", false, null, Platform: CatPlatforms.INTERNATIONAL, Password: "fake-password")
                         : new CatLaunchParams(OPERATION, "acc", false, null, Platform: CatPlatforms.WE_GAME, AuthOnly: true);

        Assert.True(host.Launch(launch).Accepted);
        await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var result = (CatSetJobResult)(await host.HandleAsync(CatJobChannel.METHOD, Params(Job(1)), CancellationToken.None))!;

        Assert.False(result.Accepted);
        Assert.Equal(CatCodes.UNSUPPORTED, result.Code);
        Assert.Null(host.Jobs.JobJson);

        runner.Finish.TrySetResult(0);
    }

    [Fact]
    public async Task Host_SetJob_WhileClosing_IsBusy()
    {
        var runner = new FakeGameRunner();
        var host   = new CatLaunchHost(runner, (_, _) => Task.CompletedTask, new CatLogRedactor());

        Assert.True(host.Launch(new CatLaunchParams(OPERATION, "acc", false, null)).Accepted);
        await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        host.Started(4321, DateTimeOffset.UtcNow);
        host.Stage(CatStages.RUNNING);

        Assert.True(host.Close(new CatCloseParams(5)).Accepted);

        var result = (CatSetJobResult)(await host.HandleAsync(CatJobChannel.METHOD, Params(Job(1)), CancellationToken.None))!;

        Assert.False(result.Accepted);
        Assert.Equal(CatCodes.BUSY, result.Code);

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
