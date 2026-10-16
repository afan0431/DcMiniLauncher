using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using XIVLauncher.CatHost;
using Xunit;

namespace XIVLauncher.Test.CatHost;

/// <summary>
///     模拟模式的上号自动开工: 模拟 Afan 的回报序列（done、blocked、暂停、没有任务单）与等级, 以及经外壳方法分发的完整一轮
/// </summary>
public sealed class CatSimulatedAfanTests : IDisposable
{
    private const int PID = 4242;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private readonly string                                        shots  = Path.Combine(Path.GetTempPath(), "dml-sim-shots-" + Guid.NewGuid().ToString("N"));
    private readonly ConcurrentQueue<(string Method, JsonNode Params)> events = new();
    private readonly CatJobChannel                                 channel;

    public CatSimulatedAfanTests()
    {
        channel = new CatJobChannel(() => "op-1", (method, parameters) => events.Enqueue((method, JsonNode.Parse(JsonSerializer.Serialize(parameters, CatProtocol.JsonOptions))!)));
        channel.GameStarted(PID);
    }

    public void Dispose()
    {
        if (Directory.Exists(shots))
            Directory.Delete(shots, true);
    }

    private CatSimulatedAfan NewAfan(CatSimulatedAfan.Behavior behavior = CatSimulatedAfan.Behavior.Normal) =>
        new(channel, PID, behavior) { ShotsDirectory = shots, Heartbeat = TimeSpan.FromSeconds(60) };

    private JsonObject[] Reports() =>
        events.Where(x => x.Method == CatJobChannel.PROGRESS_EVENT).Select(x => x.Params.AsObject()).ToArray();

    private int[][] Levels() =>
        events.Where(x => x.Method == CatJobChannel.LEVELS_EVENT).Select(x => x.Params["levels"]!.AsArray().Select(v => v!.GetValue<int>()).ToArray()).ToArray();

    /// <summary>推进到 done 或步数用完, 每步间隔 1 秒（不触发心跳）</summary>
    private static DateTime Run(CatSimulatedAfan afan, DateTime start, int steps)
    {
        for (var i = 0; i < steps; i++)
            afan.Tick(start += TimeSpan.FromSeconds(1));

        return start;
    }

    [Fact]
    public void Normal_RunsTheAutoItemsInOrder_WithCompletionShots_ThenDone()
    {
        channel.SetJob(CatJobChannelTests.Job(3));
        var afan = NewAfan();

        Run(afan, DateTime.UtcNow, 30);

        var reports = Reports();
        var states  = reports.Select(x => x["state"]!.GetValue<string>()).ToArray();

        Assert.Equal("waiting", states[0]);
        Assert.Equal("done", states[^1]);
        Assert.Equal(1, states.Count(x => x == "done"));
        Assert.Contains("running", states);
        Assert.Equal(Enumerable.Range(1, reports.Length).Select(x => (long)x), reports.Select(x => x["seq"]!.GetValue<long>()));

        // 回报只带 routine 不为 null 的两项, 做完时都到了目标
        var last = reports[^1]["items"]!.AsArray();
        Assert.Equal(["a", "b"], last.Select(x => x!["itemId"]!.GetValue<string>()));
        Assert.Equal([30L, 120L], last.Select(x => x!["current"]!.GetValue<long>()));
        Assert.All(last, x => Assert.True(x!["done"]!.GetValue<bool>()));

        // 先做 order 0 的驯兽师, 再做金碟币
        var actives = reports.Select(x => x["activeItemId"]?.GetValue<string>()).OfType<string>().Distinct().ToArray();
        Assert.Equal(["a", "b"], actives);

        // 每项做完一张完成图, 文件真的写了, 名字是 <pid>-<seq>.png
        var taken = reports.Where(x => x["shots"] != null).SelectMany(x => x["shots"]!.AsArray().Select(s => (Seq: x["seq"]!.GetValue<long>(), Shot: s!))).ToArray();
        Assert.Equal(["a", "b"], taken.Select(x => x.Shot["itemId"]!.GetValue<string>()));
        Assert.All(taken, x => Assert.Equal("completion", x.Shot["kind"]!.GetValue<string>()));
        Assert.All(taken, x => Assert.Equal(Path.Combine(shots, $"{PID}-{x.Seq}.png"), x.Shot["file"]!.GetValue<string>()));
        Assert.All(taken, x => Assert.True(new FileInfo(x.Shot["file"]!.GetValue<string>()).Length > 100));

        Assert.All(reports, x => Assert.Equal("七色花", x["character"]!["name"]!.GetValue<string>()));
        Assert.All(reports, x => Assert.Equal(3, x["revision"]!.GetValue<long>()));
        Assert.All(reports, x => Assert.Equal("op-1", x["operationId"]!.GetValue<string>()));

        // 驯兽师（ClassJob 43 → 下标 32）的等级随进度上涨, 骑士（19 → 下标 1）停在任务单里的 94
        var levels = Levels();
        Assert.Equal(18, levels[0][32]);
        Assert.Equal(30, levels[^1][32]);
        Assert.All(levels, x => Assert.Equal(94, x[1]));
        Assert.All(levels, x => Assert.Equal(CatSimulatedAfan.LEVEL_COUNT, x.Length));

        // 做完之后没有变化就不再发（心跳间隔没到）
        var count = Reports().Length;
        Run(afan, DateTime.UtcNow.AddSeconds(31), 5);
        Assert.Equal(count, Reports().Length);
    }

    [Fact]
    public void Heartbeat_ResendsTheSameStateAfterTheInterval()
    {
        channel.SetJob(CatJobChannelTests.Job(3, desired: "pause"));
        var afan = NewAfan();
        var now  = DateTime.UtcNow;

        afan.Tick(now);
        afan.Tick(now.AddSeconds(1));
        afan.Tick(now.AddSeconds(2));
        afan.Tick(now.AddSeconds(62));

        Assert.Equal(["waiting", "paused", "paused"], Reports().Select(x => x["state"]!.GetValue<string>()));
    }

    [Fact]
    public void Blocked_StopsAfterTwoSteps_UntilANewRevision()
    {
        channel.SetJob(CatJobChannelTests.Job(3));
        var afan = NewAfan(CatSimulatedAfan.Behavior.Blocked);
        var now  = Run(afan, DateTime.UtcNow, 8);

        var states = Reports().Select(x => x["state"]!.GetValue<string>()).ToArray();
        Assert.Equal(["waiting", "running", "running", "blocked"], states);

        var blocked = Reports()[^1];
        Assert.Equal("noProgress", blocked["reason"]!["code"]!.GetValue<string>());
        Assert.Equal("a", blocked["activeItemId"]!.GetValue<string>());

        // 员工处理后任务单 revision +1: 接着做到完
        channel.SetJob(CatJobChannelTests.Job(4));
        Run(afan, now, 30);

        states = Reports().Select(x => x["state"]!.GetValue<string>()).ToArray();
        Assert.Equal("done", states[^1]);
        Assert.Equal(1, states.Count(x => x == "blocked"));
        Assert.Equal(4, Reports()[^1]["revision"]!.GetValue<long>());
    }

    [Fact]
    public void PauseAndStop_HoldThePosition_ThenRunResumes()
    {
        channel.SetJob(CatJobChannelTests.Job(3));
        var afan = NewAfan();
        var now  = Run(afan, DateTime.UtcNow, 3);

        var before = Reports()[^1]["items"]![0]!["current"]!.GetValue<long>();

        channel.SetJob(CatJobChannelTests.Job(4, desired: "pause"));
        now = Run(afan, now, 3);
        Assert.Equal("paused", Reports()[^1]["state"]!.GetValue<string>());
        Assert.Equal(before, Reports()[^1]["items"]![0]!["current"]!.GetValue<long>());

        channel.SetJob(CatJobChannelTests.Job(5, desired: "stop"));
        now = Run(afan, now, 2);
        Assert.Equal("stopped", Reports()[^1]["state"]!.GetValue<string>());

        channel.SetJob(CatJobChannelTests.Job(6));
        Run(afan, now, 1);
        Assert.Equal("running", Reports()[^1]["state"]!.GetValue<string>());
        Assert.True(Reports()[^1]["items"]![0]!["current"]!.GetValue<long>() > before);
    }

    [Fact]
    public void WithoutJob_WaitsAndReportsSampleLevels()
    {
        var afan = NewAfan();
        Run(afan, DateTime.UtcNow, 3);

        var report = Assert.Single(Reports());
        Assert.Equal("waiting", report["state"]!.GetValue<string>());
        Assert.Equal(0, report["revision"]!.GetValue<long>());
        Assert.Empty(report["items"]!.AsArray());

        var levels = Assert.Single(Levels());
        Assert.Equal(CatSimulatedAfan.SampleLevels, levels);
    }

    [Fact]
    public void NoAutoItems_IsIdle()
    {
        var job = JsonNode.Parse(CatJobChannelTests.Job(3).GetRawText())!.AsObject();

        foreach (var item in job["items"]!.AsArray())
            item!["routine"] = null;

        channel.SetJob(JsonDocument.Parse(job.ToJsonString()).RootElement.Clone());
        Run(NewAfan(), DateTime.UtcNow, 3);

        Assert.Equal(["waiting", "idle"], Reports().Select(x => x["state"]!.GetValue<string>()));
    }

    [Fact]
    public void Manual_SendsNoReports_ButStillLevels()
    {
        channel.SetJob(CatJobChannelTests.Job(3));
        Run(NewAfan(CatSimulatedAfan.Behavior.Manual), DateTime.UtcNow, 5);

        Assert.Empty(Reports());
        Assert.Single(Levels());
    }

    [Fact]
    public void ExpArrayIndex_MatchesTheChineseClient()
    {
        Assert.Equal(32, CatSimulatedAfan.ExpArrayIndex[43]); // 驯兽师
        Assert.Equal(20, CatSimulatedAfan.ExpArrayIndex[31]); // 机工士
        Assert.Equal(25, CatSimulatedAfan.ExpArrayIndex[36]); // 青魔法师
        Assert.Equal(1, CatSimulatedAfan.ExpArrayIndex[19]);  // 骑士 = 剑术师
        Assert.Equal(18, CatSimulatedAfan.ExpArrayIndex[28]); // 学者 = 秘术师
    }

    [Fact]
    public async Task SimulatedRunner_ThroughTheHost_ServesTheJobAndRunsToDone()
    {
        var ports    = Path.Combine(shots, "ports");
        var received = new BlockingCollection<(string Method, JsonNode Params)>();
        var runner = new CatSimulatedGameRunner
        {
            StepDelay         = TimeSpan.FromMilliseconds(10),
            AfanStep          = TimeSpan.FromMilliseconds(30),
            ShotsDirectory    = shots,
            PortFileDirectory = ports
        };
        var host = new CatLaunchHost
        (
            runner,
            (method, parameters) =>
            {
                received.Add((method, JsonNode.Parse(JsonSerializer.Serialize(parameters, CatProtocol.JsonOptions))!));
                return Task.CompletedTask;
            },
            new CatLogRedactor()
        );

        Assert.True(host.Launch(new CatLaunchParams("op-1", "acc", false, null)).Accepted);

        // 工作台在 launch 被接受后就交任务单（这时游戏还没起来）
        var job = JsonDocument.Parse(JsonSerializer.Serialize(new { job = CatJobChannelTests.Job(1) })).RootElement.Clone();
        Assert.True(((CatSetJobResult)(await host.HandleAsync(CatJobChannel.METHOD, job, CancellationToken.None))!).Accepted);

        var pid = Wait(received, "game.started")["pid"]!.GetValue<int>();

        // 本机接口开好后发一条 launcher.log 写明地址; 端口文件写好, /cat/job 走真 HTTP 取得到
        var announced = Wait(received, "launcher.log", x => x["message"]!.GetValue<string>().Contains("/cat/job", StringComparison.Ordinal));
        Assert.Contains($"pid={pid}", announced["message"]!.GetValue<string>());

        var portFile = Path.Combine(ports, $"dctravel-{pid}.port");
        Assert.True(File.Exists(portFile));

        using (var http = new HttpClient())
        {
            var response = await http.GetAsync($"http://127.0.0.1:{File.ReadAllText(portFile)}/cat/job?pid={pid}");
            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(1, JsonNode.Parse(await response.Content.ReadAsStringAsync())!["revision"]!.GetValue<long>());
        }

        var deadline = DateTime.UtcNow + Timeout;
        JsonNode? done = null;

        while (done == null && DateTime.UtcNow < deadline)
        {
            var progress = Wait(received, CatJobChannel.PROGRESS_EVENT);

            if (progress["state"]!.GetValue<string>() == "done")
                done = progress;
        }

        Assert.NotNull(done);
        Assert.Equal(pid, done["pid"]!.GetValue<int>());
        Assert.Equal("op-1", done["operationId"]!.GetValue<string>());

        using (var placeholder = Process.GetProcessById(pid))
            placeholder.Kill();

        Wait(received, "game.exited");
        Assert.Equal(0, await host.Completion.WaitAsync(Timeout));
        Assert.False(File.Exists(portFile));
        Assert.True(received.All(x => x.Method != CatJobChannel.PROGRESS_EVENT));
    }

    private static JsonNode Wait(BlockingCollection<(string Method, JsonNode Params)> received, string method, Func<JsonNode, bool>? match = null)
    {
        var deadline = DateTime.UtcNow + Timeout;

        while (true)
        {
            var remaining = deadline - DateTime.UtcNow;

            if (remaining <= TimeSpan.Zero || !received.TryTake(out var item, remaining))
                throw new TimeoutException($"没有等到事件 {method}");

            if (item.Method == method && (match == null || match(item.Params)))
                return item.Params;
        }
    }
}
