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
        channel = new CatJobChannel(() => "op-1", (method, parameters) => events.Enqueue((method, JsonNode.Parse(JsonSerializer.Serialize(parameters, CatProtocol.JsonOptions))!)))
        {
            ShotsDirectory = shots
        };
        channel.GameStarted(PID);
    }

    public void Dispose()
    {
        if (Directory.Exists(shots))
            Directory.Delete(shots, true);
    }

    private static readonly CatCharacterInfo Entered = new("4000000000000001", "七色花", "MengYaChi", "MengYaChi", "萌芽池", "萌芽池", false, true);

    private CatSimulatedAfan NewAfan(CatSimulatedAfan.Behavior behavior = CatSimulatedAfan.Behavior.Normal) =>
        new(channel, PID, behavior, Entered) { ShotsDirectory = shots, Heartbeat = TimeSpan.FromSeconds(60), SeqStart = 0 };

    private JsonObject[] Reports() =>
        events.Where(x => x.Method == CatJobChannel.PROGRESS_EVENT).Select(x => x.Params.AsObject()).ToArray();

    private int[][] Levels() =>
        events.Where(x => x.Method == CatJobChannel.LEVELS_EVENT).Select(x => x.Params["levels"]!.AsArray().Select(v => v!.GetValue<int>()).ToArray()).ToArray();

    private bool?[] Duty() =>
        events.Where(x => x.Method == CatJobChannel.DUTY_EVENT).Select(x => x.Params["inDuty"]?.GetValue<bool>()).ToArray();

    private string[] States() =>
        Reports().Select(x => x["state"]!.GetValue<string>()).ToArray();

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

        // 等级读数带当前角色: 名字与 contentId 是游戏里的, 原始服务器 Id 取任务单里的
        var character = events.First(x => x.Method == CatJobChannel.LEVELS_EVENT).Params["character"]!;
        Assert.Equal("七色花", character["name"]!.GetValue<string>());
        Assert.Equal("4000000000000001", character["contentId"]!.GetValue<string>());
        Assert.Equal(1167, character["homeWorldId"]!.GetValue<int>());

        // 做完之后没有变化就不再发（心跳间隔没到）
        var count = Reports().Length;
        Run(afan, DateTime.UtcNow.AddSeconds(31), 5);
        Assert.Equal(count, Reports().Length);
    }

    private JsonNode[] StartShots() =>
        Reports().Where(x => x["shots"] != null).SelectMany(x => x["shots"]!.AsArray().Select(s => s!)).Where(x => x["kind"]!.GetValue<string>() == "start").ToArray();

    [Fact]
    public void StartShots_InTheFirstRunningReport_OnePicturePerScreen_OnEachItem()
    {
        channel.SetJob(CatJobChannelTests.JobWithStartShots(3));
        Run(NewAfan(), DateTime.UtcNow, 40);

        var reports = Reports();
        var states  = States();

        Assert.Equal("waiting", states[0]);
        Assert.Null(reports[0]["shots"]);
        Assert.Equal("running", states[1]);
        Assert.Equal("done", states[^1]);

        // 第一条 running 只带开工图、还没推进: 数值是开工前的
        var first = reports[1];
        Assert.Equal("a", first["activeItemId"]!.GetValue<string>());
        Assert.Equal(["a", "b", "d", "e"], first["items"]!.AsArray().Select(x => x!["itemId"]!.GetValue<string>()));
        Assert.Equal([18L, 35L, 0L], first["items"]!.AsArray().Take(3).Select(x => x!["current"]!.GetValue<long>()));

        // 练级两项（驯兽师与人做的骑士, 都是战斗职业）共用一张, 金碟币与声誉共用一张; 勾选类、主线和 startShot 为 false 的不拍
        var shots = first["shots"]!.AsArray();
        Assert.Equal(["a", "b", "c", "d"], shots.Select(x => x!["itemId"]!.GetValue<string>()));
        Assert.All(shots, x => Assert.Equal("start", x!["kind"]!.GetValue<string>()));

        var files = shots.Select(x => x!["file"]!.GetValue<string>()).ToArray();
        Assert.Equal(files[0], files[2]);
        Assert.Equal(files[1], files[3]);
        Assert.NotEqual(files[0], files[1]);
        Assert.All(files, x => Assert.Matches($@"\\{PID}-\d+\.png$", x));
        Assert.All(files.Distinct(), x => Assert.True(new FileInfo(x).Length > 100));

        // 之后照常推进, 不再拍开工图; seq 严格递增
        Assert.True(reports[2]["items"]![0]!["current"]!.GetValue<long>() > 18);
        Assert.Equal(4, StartShots().Length);

        var seqs = reports.Select(x => x["seq"]!.GetValue<long>()).ToArray();
        Assert.All(seqs.Zip(seqs.Skip(1)), x => Assert.True(x.Second > x.First));
    }

    [Fact]
    public void StartShots_WaitForRun_AndAreTakenOnlyOncePerGame()
    {
        channel.SetJob(CatJobChannelTests.JobWithStartShots(3, desired: "pause"));
        var afan = NewAfan();
        var now  = Run(afan, DateTime.UtcNow, 3);

        Assert.Equal(["waiting", "paused"], States());
        Assert.Empty(StartShots());

        channel.SetJob(CatJobChannelTests.JobWithStartShots(4));
        now = Run(afan, now, 2);

        Assert.Equal(["waiting", "paused", "running", "running"], States());
        Assert.Equal(4, Reports()[2]["shots"]!.AsArray().Count);

        // 服务器还没把 startShot 改成 false 时暂停再继续: 这个游戏里不再补拍
        channel.SetJob(CatJobChannelTests.JobWithStartShots(5, desired: "pause"));
        now = Run(afan, now, 2);
        channel.SetJob(CatJobChannelTests.JobWithStartShots(6));
        Run(afan, now, 40);

        Assert.Equal(4, StartShots().Length);
        Assert.Equal("done", States()[^1]);
    }

    [Fact]
    public void StartShots_NothingToShoot_StartsRightAway()
    {
        var job = JsonNode.Parse(CatJobChannelTests.JobWithStartShots(3).GetRawText())!.AsObject();

        // 只剩勾选类与主线要拍: 都不知道开哪个界面, 不拍
        foreach (var item in job["items"]!.AsArray().Where(x => x!["itemId"]!.GetValue<string>() is "a" or "b" or "c" or "d"))
            item!["startShot"] = false;

        channel.SetJob(JsonDocument.Parse(job.ToJsonString()).RootElement.Clone());
        Run(NewAfan(), DateTime.UtcNow, 2);

        Assert.Equal(["waiting", "running"], States());
        Assert.Null(Reports()[1]["shots"]);
        Assert.True(Reports()[1]["items"]![0]!["current"]!.GetValue<long>() > 18);
    }

    /// <summary><see cref="CatJobChannelTests.JobWithStartShots" /> 只留给定的项, 每项再经 change 改字段</summary>
    private static JsonElement JobWithItems(string[] keep, Action<JsonObject>? change = null)
    {
        var job   = JsonNode.Parse(CatJobChannelTests.JobWithStartShots(3).GetRawText())!.AsObject();
        var items = new JsonArray(job["items"]!.AsArray().Where(x => keep.Contains(x!["itemId"]!.GetValue<string>())).Select(x => x!.DeepClone()).ToArray());

        foreach (var item in items)
            change?.Invoke(item!.AsObject());

        job["items"] = items;
        return JsonDocument.Parse(job.ToJsonString()).RootElement.Clone();
    }

    [Fact]
    public void StartShots_NoAutoItems_GoInTheFirstIdleReport_ThenIdleHeartbeatsWithout()
    {
        // 只有人做的项: 骑士、白魔法师为战斗职业练级（共用一张）, 主线不知道开哪个界面（不拍）
        channel.SetJob(JobWithItems(["c", "f", "g"], x => x["startShot"] = true));
        var afan = NewAfan();
        var now  = Run(afan, DateTime.UtcNow, 3);

        Assert.Equal(["waiting", "idle"], States());

        var shots = Reports()[1]["shots"]!.AsArray();
        Assert.Equal(["c", "g"], shots.Select(x => x!["itemId"]!.GetValue<string>()));
        Assert.All(shots, x => Assert.Equal("start", x!["kind"]!.GetValue<string>()));
        Assert.Equal(shots[0]!["file"]!.GetValue<string>(), shots[1]!["file"]!.GetValue<string>());
        Assert.True(new FileInfo(shots[0]!["file"]!.GetValue<string>()).Length > 100);

        // 之后照常心跳, 不再带图
        afan.Tick(now.AddSeconds(61));
        Assert.Equal(["waiting", "idle", "idle"], States());
        Assert.Null(Reports()[2]["shots"]);
    }

    [Theory]
    [InlineData(false, "idle")]
    [InlineData(true, "running")]
    public void StartShots_OnlyCheckboxOrUnknownScreens_TakeNone(bool autoCheckbox, string state)
    {
        // 守护天节（勾选类, 自动做或人做）与主线（人做, 不知道开哪个界面）
        channel.SetJob(autoCheckbox ? JobWithItems(["e"]) : JobWithItems(["e", "f"], x => x["routine"] = null));
        Run(NewAfan(), DateTime.UtcNow, 5);

        Assert.Equal(["waiting", state], States()[..2]);
        Assert.Empty(StartShots());
        Assert.Null(Reports()[1]["shots"]);
    }

    [Fact]
    public void StartShots_AutoItemsAlreadyDone_GoInTheDoneReport()
    {
        // 金碟币已经到目标（服务器不再要它的开工图）, 人做的骑士还要
        channel.SetJob
        (
            JobWithItems
            (
                ["b", "c"],
                x =>
                {
                    if (x["itemId"]!.GetValue<string>() != "b")
                        return;

                    x["current"]   = 120;
                    x["startShot"] = false;
                }
            )
        );
        Run(NewAfan(), DateTime.UtcNow, 3);

        Assert.Equal(["waiting", "done"], States());

        var shot = Assert.Single(Reports()[1]["shots"]!.AsArray());
        Assert.Equal("c", shot!["itemId"]!.GetValue<string>());
        Assert.Equal("start", shot["kind"]!.GetValue<string>());
    }

    [Fact]
    public void StartShots_CombatAndCraftLeveling_OnePictureEach_OnTheirOwnItems()
    {
        // 骑士（战斗）与刻木匠（生产采集, ClassJob 8）同一单: 战斗职业小页一张、制作&采集职业小页一张
        channel.SetJob
        (
            JobWithItems
            (
                ["c", "g"],
                x =>
                {
                    x["startShot"] = true;

                    if (x["itemId"]!.GetValue<string>() != "g")
                        return;

                    x["title"]      = "刻木匠";
                    x["classJobId"] = 8;
                }
            )
        );
        Run(NewAfan(), DateTime.UtcNow, 3);

        Assert.Equal(["waiting", "idle"], States());

        var shots = Reports()[1]["shots"]!.AsArray();
        Assert.Equal(["c", "g"], shots.Select(x => x!["itemId"]!.GetValue<string>()));
        Assert.All(shots, x => Assert.Equal("start", x!["kind"]!.GetValue<string>()));

        var files = shots.Select(x => x!["file"]!.GetValue<string>()).ToArray();
        Assert.NotEqual(files[0], files[1]);
        Assert.All(files, x => Assert.True(new FileInfo(x).Length > 100));
    }

    [Theory]
    [InlineData("leveling", 43, "bst-level", "jobs-combat")]
    [InlineData("leveling", 19, null, "jobs-combat")]
    [InlineData("leveling", null, null, "jobs-combat")]
    [InlineData("leveling", 7, null, "jobs-combat")]
    [InlineData("leveling", 8, null, "jobs-craft")]
    [InlineData("leveling", 15, null, "jobs-craft")]
    [InlineData("leveling", 18, null, "jobs-craft")]
    [InlineData("collect", null, "mgp", "currency")]
    [InlineData("collect", null, "fallguys-rep", "currency")]
    [InlineData("periodic", null, "allsaints-2026", null)]
    [InlineData("msq", null, null, null)]
    [InlineData("other", null, null, null)]
    public void StartShotScreen_LevelingByCombatOrCraft_MgpAndFallGuysTogether_OthersNone(string kind, int? classJobId, string? routine, string? screen) =>
        Assert.Equal(screen, CatSimulatedAfan.StartShotScreen(kind, classJobId, routine));

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

    /// <summary>Job(revision) 改 a 驯兽师的 current / target / done, b 金碟币记为已做完</summary>
    private static JsonElement JobWithA(long revision, long current, long target, bool done)
    {
        var job = JsonNode.Parse(CatJobChannelTests.Job(revision).GetRawText())!.AsObject();
        var a   = job["items"]![0]!.AsObject();
        a["current"] = current;
        a["target"]  = target;
        a["done"]    = done;
        var b = job["items"]![1]!.AsObject();
        b["current"] = 120;
        b["done"]    = true;
        return JsonDocument.Parse(job.ToJsonString()).RootElement.Clone();
    }

    [Fact]
    public void Done_ThenTargetRaised_GoesOnToTheNewTarget()
    {
        channel.SetJob(CatJobChannelTests.Job(3));
        var afan = NewAfan();
        var now  = Run(afan, DateTime.UtcNow, 30);
        Assert.Equal("done", States()[^1]);

        // 任务单落后（还写着旧的 18 / 30、没做完）: 已到目标, 不倒退, 仍是做完
        channel.SetJob(JobWithA(4, 18, 30, false));
        now = Run(afan, now, 3);
        Assert.Equal("done", States()[^1]);
        Assert.Equal(30L, Reports()[^1]["items"]![0]!["current"]!.GetValue<long>());

        // 员工把驯兽师目标调到 40: 接着做到 40, 再报做完
        var before = Reports().Length;
        channel.SetJob(JobWithA(5, 30, 40, false));
        Run(afan, now, 30);

        var after = Reports().Skip(before).ToArray();
        Assert.Equal("running", after[0]["state"]!.GetValue<string>());
        Assert.Equal("a", after[0]["activeItemId"]!.GetValue<string>());
        Assert.Equal("done", after[^1]["state"]!.GetValue<string>());
        Assert.Equal(1, after.Count(x => x["state"]!.GetValue<string>() == "done"));
        Assert.Equal(40L, after[^1]["items"]![0]!["current"]!.GetValue<long>());
        Assert.True(after[^1]["items"]![0]!["done"]!.GetValue<bool>());
        Assert.Equal(5, after[^1]["revision"]!.GetValue<long>());

        // 做到新目标再挂一张完成图
        var completion = after.Where(x => x["shots"] != null).SelectMany(x => x["shots"]!.AsArray()).Where(x => x!["kind"]!.GetValue<string>() == "completion").ToArray();
        Assert.Equal(["a"], completion.Select(x => x!["itemId"]!.GetValue<string>()));
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

    /// <summary>把任务单里指定项的 selected 改成给定值（null = 去掉这个字段, 当作旧版 Cat）</summary>
    private static JsonElement WithSelected(JsonElement source, long revision, params (string ItemId, bool? Selected)[] picks)
    {
        var job = JsonNode.Parse(source.GetRawText())!.AsObject();
        job["revision"] = revision;

        foreach (var item in job["items"]!.AsArray().Select(x => x!.AsObject()))
        {
            foreach (var (itemId, selected) in picks.Where(x => x.ItemId == item["itemId"]!.GetValue<string>()))
            {
                if (selected is { } value)
                    item["selected"] = value;
                else
                    item.Remove("selected");
            }
        }

        return JsonDocument.Parse(job.ToJsonString()).RootElement.Clone();
    }

    [Fact]
    public void Selected_OnlyTheTickedItemsAreRun_DoneMeansTheTickedOnesAreDone()
    {
        channel.SetJob(WithSelected(CatJobChannelTests.Job(3), 3, ("a", false), ("b", true)));
        Run(NewAfan(), DateTime.UtcNow, 30);

        var reports = Reports();
        Assert.Equal("done", States()[^1]);
        Assert.Equal(1, States().Count(x => x == "done"));

        // 只做勾上的金碟币; 没勾的驯兽师不碰, 回报里照样带它的进度（停在任务单的 18, 没做完）
        Assert.Equal(["b"], reports.Select(x => x["activeItemId"]?.GetValue<string>()).OfType<string>().Distinct());
        var last = reports[^1]["items"]!.AsArray();
        Assert.Equal(["a", "b"], last.Select(x => x!["itemId"]!.GetValue<string>()));
        Assert.Equal([18L, 120L], last.Select(x => x!["current"]!.GetValue<long>()));
        Assert.Equal([false, true], last.Select(x => x!["done"]!.GetValue<bool>()));

        // 完成图只有勾上的那一项; 驯兽师等级不涨
        var completion = reports.Where(x => x["shots"] != null).SelectMany(x => x["shots"]!.AsArray()).ToArray();
        Assert.Equal(["b"], completion.Select(x => x!["itemId"]!.GetValue<string>()));
        Assert.All(Levels(), x => Assert.Equal(18, x[32]));
    }

    [Fact]
    public void Selected_MissingFieldCountsAsTicked_LikeTheOldCat()
    {
        channel.SetJob(WithSelected(CatJobChannelTests.Job(3), 3, ("a", null), ("b", true)));
        Run(NewAfan(), DateTime.UtcNow, 30);

        Assert.Equal("done", States()[^1]);
        Assert.Equal(["a", "b"], Reports().Select(x => x["activeItemId"]?.GetValue<string>()).OfType<string>().Distinct());
        Assert.All(Reports()[^1]["items"]!.AsArray(), x => Assert.True(x!["done"]!.GetValue<bool>()));
    }

    [Fact]
    public void Selected_UntickedWhileRunning_StopsThatItem_AndGoesOnWithTheRest()
    {
        channel.SetJob(CatJobChannelTests.Job(3));
        var afan = NewAfan();
        var now  = Run(afan, DateTime.UtcNow, 3);
        Assert.Equal("a", Reports()[^1]["activeItemId"]!.GetValue<string>());

        // 员工在做驯兽师时去勾: 驯兽师停在这一步的数值, 接着做金碟币, 做完金碟币就是 done
        channel.SetJob(WithSelected(CatJobChannelTests.Job(3), 4, ("a", false)));
        var frozen = Reports()[^1]["items"]![0]!["current"]!.GetValue<long>();
        var before = Reports().Length;
        Run(afan, now, 30);

        var after = Reports().Skip(before).ToArray();
        Assert.Equal("b", after[0]["activeItemId"]!.GetValue<string>());
        Assert.DoesNotContain(after, x => x["activeItemId"]?.GetValue<string>() == "a");
        Assert.All(after, x => Assert.Equal(frozen, x["items"]![0]!["current"]!.GetValue<long>()));
        Assert.Equal("done", after[^1]["state"]!.GetValue<string>());
        Assert.False(after[^1]["items"]![0]!["done"]!.GetValue<bool>());
    }

    [Fact]
    public void Selected_NothingTicked_IsIdle()
    {
        channel.SetJob(WithSelected(CatJobChannelTests.Job(3), 3, ("a", false), ("b", false)));
        Run(NewAfan(), DateTime.UtcNow, 3);

        Assert.Equal(["waiting", "idle"], States());
    }

    [Fact]
    public void Selected_UntickedAutoItem_HasNoStartShot_HumanItemsStillDo()
    {
        channel.SetJob(WithSelected(CatJobChannelTests.JobWithStartShots(3), 3, ("b", false)));
        Run(NewAfan(), DateTime.UtcNow, 40);

        // 金碟币没勾: 不拍它的开工图（糖豆人声誉照样拍）; 人做的骑士照旧和驯兽师共用一张
        Assert.Equal(["a", "c", "d"], StartShots().Select(x => x["itemId"]!.GetValue<string>()));
        Assert.Equal("done", States()[^1]);
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
    public void Mismatch_ReportsTheEnteredCharacter_AndDoesNothing()
    {
        channel.SetJob(CatJobChannelTests.Job(3));
        Run(NewAfan(CatSimulatedAfan.Behavior.Mismatch), DateTime.UtcNow, 6);

        var reports = Reports();
        Assert.Equal(["waiting", "mismatch"], reports.Select(x => x["state"]!.GetValue<string>()));
        Assert.Equal("error", reports[1]["reason"]!["code"]!.GetValue<string>());
        Assert.Contains("七色花", reports[1]["reason"]!["text"]!.GetValue<string>());
        Assert.Equal([18L, 35L], reports[1]["items"]!.AsArray().Select(x => x!["current"]!.GetValue<long>()));
    }

    [Fact]
    public void Unsupported_BlocksOnceForTheFirstItem_ThenDoesTheRest()
    {
        channel.SetJob(CatJobChannelTests.Job(3));
        Run(NewAfan(CatSimulatedAfan.Behavior.Unsupported), DateTime.UtcNow, 20);

        var reports = Reports();
        var states  = reports.Select(x => x["state"]!.GetValue<string>()).ToArray();

        Assert.Equal(["waiting", "blocked"], states[..2]);
        Assert.Equal("unsupported", reports[1]["reason"]!["code"]!.GetValue<string>());
        Assert.Contains("bst-level", reports[1]["reason"]!["text"]!.GetValue<string>());
        Assert.Equal("a", reports[1]["activeItemId"]!.GetValue<string>());

        // 跳过驯兽师, 只做金碟币
        Assert.Equal("done", states[^1]);
        Assert.Equal(1, states.Count(x => x == "blocked"));
        var last = reports[^1]["items"]!.AsArray();
        Assert.Equal([18L, 120L], last.Select(x => x!["current"]!.GetValue<long>()));
        Assert.Equal([false, true], last.Select(x => x!["done"]!.GetValue<bool>()));
    }

    [Fact]
    public void Seq_StartsFromUnixMilliseconds_LikeTheRealAfan()
    {
        channel.SetJob(CatJobChannelTests.Job(3));
        var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var afan = new CatSimulatedAfan(channel, PID, CatSimulatedAfan.Behavior.Normal, Entered) { ShotsDirectory = shots };
        Run(afan, DateTime.UtcNow, 30);

        var seqs = Reports().Select(x => x["seq"]!.GetValue<long>()).ToArray();
        Assert.True(seqs[0] > before && seqs[0] > int.MaxValue);
        Assert.Equal(Enumerable.Range(0, seqs.Length).Select(i => seqs[0] + i), seqs);

        // 截图文件名里的 seq 也是这个大数
        var file = Reports().Where(x => x["shots"] != null).SelectMany(x => x["shots"]!.AsArray()).First()!["file"]!.GetValue<string>();
        Assert.Matches(@"\\4242-\d{13}\.png$", file);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void Duty_GoesInAndOutWhileRunning_AndIsOutOnceDone()
    {
        channel.SetJob(CatJobChannelTests.Job(3));
        Run(NewAfan(), DateTime.UtcNow, 30);

        // waiting → 驯兽师 3 步（一把两步 + 一步在外）→ 金碟币 → done; 有变化才发
        Assert.Equal([false, true, false, true, false, true, false], Duty());

        var first = events.First(x => x.Method == CatJobChannel.DUTY_EVENT).Params;
        Assert.Equal(PID, first["pid"]!.GetValue<int>());
        Assert.Equal("op-1", first["operationId"]!.GetValue<string>());
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$", first["at"]!.GetValue<string>());
    }

    [Fact]
    public void Duty_WithoutJobOrWhilePaused_IsOut()
    {
        Run(NewAfan(), DateTime.UtcNow, 3);
        Assert.Equal([false], Duty());

        channel.SetJob(CatJobChannelTests.Job(3, desired: "pause"));
        Run(NewAfan(), DateTime.UtcNow, 3);
        Assert.Equal([false], Duty());
    }

    [Fact]
    public void AlwaysInDuty_StaysInWhateverTheJobDoes()
    {
        channel.SetJob(CatJobChannelTests.Job(3));
        var afan = new CatSimulatedAfan(channel, PID, CatSimulatedAfan.Behavior.Normal, Entered) { ShotsDirectory = shots, SeqStart = 0, AlwaysInDuty = true };

        Run(afan, DateTime.UtcNow, 30);

        Assert.Equal([true], Duty());
        Assert.Equal("done", States()[^1]);
    }

    [Fact]
    public void Finish_InTheMiddleOfARound_FinishesThatRoundThenStops()
    {
        channel.SetJob(CatJobChannelTests.Job(3));
        var afan = NewAfan();
        var now  = Run(afan, DateTime.UtcNow, 2);

        Assert.Equal([false, true], Duty());

        // 这一把还差一步: 再走一步（仍在副本中）, 走完出来, 然后停下
        channel.SetJob(CatJobChannelTests.Job(4, desired: "finish"));
        now = Run(afan, now, 1);
        Assert.Equal("running", States()[^1]);
        Assert.Equal([false, true], Duty());

        now = Run(afan, now, 1);
        Assert.Equal("running", States()[^1]);
        Assert.Equal([false, true, false], Duty());

        Run(afan, now, 3);
        Assert.Equal(["waiting", "running", "running", "running", "stopped"], States());
        Assert.Equal([false, true, false], Duty());
        Assert.Equal(27L, Reports()[^1]["items"]![0]!["current"]!.GetValue<long>());
    }

    [Fact]
    public void Finish_OutsideARound_StopsRightAway()
    {
        channel.SetJob(CatJobChannelTests.Job(3, desired: "finish"));
        Run(NewAfan(), DateTime.UtcNow, 4);

        Assert.Equal(["waiting", "stopped"], States());
        Assert.Equal([false], Duty());
    }

    [Fact]
    public void DutyLost_BackToTitleForAFewSteps_ReportsUnknownOnce_ThenReadsAgain()
    {
        channel.SetJob(CatJobChannelTests.Job(3));
        var afan = new CatSimulatedAfan(channel, PID, CatSimulatedAfan.Behavior.Normal, Entered) { ShotsDirectory = shots, SeqStart = 0, AlwaysInDuty = true, DutyLost = true };

        var now = Run(afan, DateTime.UtcNow, CatSimulatedAfan.DUTY_LOST_AFTER_STEPS);
        Assert.Equal<bool?>([true], Duty());

        // 掉线的第 1 步还不报, 接连第 2 步报一次未知, 之后不再报
        now = Run(afan, now, 1);
        Assert.Equal<bool?>([true], Duty());

        now = Run(afan, now, CatSimulatedAfan.DUTY_LOST_STEPS - 1);
        Assert.Equal<bool?>([true, null], Duty());

        var unknown = events.Last(x => x.Method == CatJobChannel.DUTY_EVENT).Params.AsObject();
        Assert.True(unknown.ContainsKey("inDuty"));
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$", unknown["at"]!.GetValue<string>());

        // 回到世界: 照常读; 任务单的推进不受影响
        Run(afan, now, 30);
        Assert.Equal<bool?>([true, null, true], Duty());
        Assert.Equal("done", States()[^1]);
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
        Assert.True(received.All(x => x.Method is not (CatJobChannel.PROGRESS_EVENT or CatJobChannel.LEVELS_EVENT or CatJobChannel.DUTY_EVENT)));
    }

    [Fact]
    public async Task SimulatedRunner_InDutyPrefix_StaysInDuty_AndCombinesWithTheJobPrefixes()
    {
        var received = new BlockingCollection<(string Method, JsonNode Params)>();
        var duty     = new ConcurrentQueue<bool>();
        var runner = new CatSimulatedGameRunner
        {
            StepDelay         = TimeSpan.FromMilliseconds(10),
            AfanStep          = TimeSpan.FromMilliseconds(30),
            ShotsDirectory    = shots,
            PortFileDirectory = Path.Combine(shots, "ports")
        };
        var host = new CatLaunchHost
        (
            runner,
            (method, parameters) =>
            {
                var node = JsonNode.Parse(JsonSerializer.Serialize(parameters, CatProtocol.JsonOptions))!;

                if (method == CatJobChannel.DUTY_EVENT)
                    duty.Enqueue(node["inDuty"]!.GetValue<bool>());

                received.Add((method, node));
                return Task.CompletedTask;
            },
            new CatLogRedactor()
        );

        Assert.True(host.Launch(new CatLaunchParams("op-1", CatSimulatedGameRunner.IN_DUTY_PREFIX + CatSimulatedGameRunner.JOB_BLOCKED_PREFIX + "acc", false, null)).Accepted);

        var job = JsonDocument.Parse(JsonSerializer.Serialize(new { job = CatJobChannelTests.Job(1) })).RootElement.Clone();
        Assert.True(((CatSetJobResult)(await host.HandleAsync(CatJobChannel.METHOD, job, CancellationToken.None))!).Accepted);

        var pid   = Wait(received, "game.started")["pid"]!.GetValue<int>();
        var first = Wait(received, CatJobChannel.DUTY_EVENT);

        Assert.True(first["inDuty"]!.GetValue<bool>());
        Assert.Equal(pid, first["pid"]!.GetValue<int>());
        Assert.Equal("op-1", first["operationId"]!.GetValue<string>());

        // blocked: 照样生效
        Wait(received, CatJobChannel.PROGRESS_EVENT, x => x["state"]!.GetValue<string>() == "blocked");

        using (var placeholder = Process.GetProcessById(pid))
            placeholder.Kill();

        Wait(received, "game.exited");
        Assert.Equal(0, await host.Completion.WaitAsync(Timeout));
        Assert.Equal([true], duty);
    }

    [Fact]
    public async Task SimulatedRunner_DutyLostPrefix_ReportsUnknown_ThenComesBack()
    {
        var received = new BlockingCollection<(string Method, JsonNode Params)>();
        var runner = new CatSimulatedGameRunner
        {
            StepDelay         = TimeSpan.FromMilliseconds(10),
            AfanStep          = TimeSpan.FromMilliseconds(30),
            ShotsDirectory    = shots,
            PortFileDirectory = Path.Combine(shots, "ports")
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

        // 两个副本状态前缀顺序不限
        Assert.True(host.Launch(new CatLaunchParams("op-1", CatSimulatedGameRunner.DUTY_LOST_PREFIX + CatSimulatedGameRunner.IN_DUTY_PREFIX + "acc", false, null)).Accepted);

        var pid = Wait(received, "game.started")["pid"]!.GetValue<int>();

        Assert.True(Wait(received, CatJobChannel.DUTY_EVENT)["inDuty"]!.GetValue<bool>());

        var unknown = Wait(received, CatJobChannel.DUTY_EVENT).AsObject();
        Assert.True(unknown.ContainsKey("inDuty"));
        Assert.Null(unknown["inDuty"]);
        Assert.Equal(pid, unknown["pid"]!.GetValue<int>());

        Assert.True(Wait(received, CatJobChannel.DUTY_EVENT)["inDuty"]!.GetValue<bool>());

        using (var placeholder = Process.GetProcessById(pid))
            placeholder.Kill();

        Wait(received, "game.exited");
        Assert.Equal(0, await host.Completion.WaitAsync(Timeout));
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
