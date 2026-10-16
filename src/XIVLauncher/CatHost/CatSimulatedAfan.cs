using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Serilog;

namespace XIVLauncher.CatHost;

/// <summary>
///     模拟模式里的游戏内 Afan（供 Cat 不开游戏联调上号自动开工）: 每一步按当前任务单推进一次, 回报经
///     <see cref="CatJobChannel.PostProgress" />（与真 Afan 的 <c>POST /cat/progress</c> 同一条路, 一样校验、去重、发 game.jobProgress）,
///     职业等级经 <see cref="CatJobChannel.ReportLevels" />（有变化才发 game.levels）。规则见 <see cref="CatSimulatedGameRunner" />。
/// </summary>
internal sealed class CatSimulatedAfan
{
    /// <summary>模拟的方式</summary>
    public enum Behavior
    {
        /// <summary>照任务单一项项做到 done</summary>
        Normal,

        /// <summary>第一项走两步后回报 blocked（noProgress）, 直到任务单 revision 变了才接着做</summary>
        Blocked,

        /// <summary>不产生回报（由测试工具往 /cat/progress 发）, 只报等级</summary>
        Manual
    }

    /// <summary>职业等级数组的长度（国服 PlayerState._classJobLevels）</summary>
    public const int LEVEL_COUNT = 35;

    /// <summary>回报里的 afan 版本号</summary>
    public const string VERSION = "sim";

    /// <summary>
    ///     ClassJob 表行号 → ExpArrayIndex（国服客户端 ClassJob 表, 2026-10-16 用 Dalamud 自带的 Lumina 从本机 sqpack 读出）
    /// </summary>
    public static readonly IReadOnlyDictionary<int, int> ExpArrayIndex = new Dictionary<int, int>
    {
        [1] = 1, [2] = 0, [3] = 2, [4] = 4, [5] = 3, [6] = 6, [7] = 5,
        [8] = 7, [9] = 8, [10] = 9, [11] = 10, [12] = 11, [13] = 12, [14] = 13, [15] = 14,
        [16] = 15, [17] = 16, [18] = 17,
        [19] = 1, [20] = 0, [21] = 2, [22] = 4, [23] = 3, [24] = 6, [25] = 5,
        [26] = 18, [27] = 18, [28] = 18, [29] = 19, [30] = 19, [31] = 20, [32] = 21, [33] = 22,
        [34] = 23, [35] = 24, [36] = 25, [37] = 26, [38] = 27, [39] = 28, [40] = 29, [41] = 30, [42] = 31,
        [43] = 32
    };

    /// <summary>没有任务单时报的等级: 下标 1（剑术师 / 骑士）100、6（幻术师 / 白魔法师）90, 其余 0</summary>
    public static readonly IReadOnlyList<int> SampleLevels = Enumerable.Range(0, LEVEL_COUNT).Select(i => i switch { 1 => 100, 6 => 90, _ => 0 }).ToArray();

    private readonly CatJobChannel jobs;
    private readonly int           pid;
    private readonly Behavior      behavior;

    private readonly Dictionary<string, ItemProgress> progress = new(StringComparer.Ordinal);

    private long      seq;
    private long?     lastRevision;
    private bool      characterChecked;
    private bool      progressShotTaken;
    private long?     blockedAtRevision;
    private bool      blockedOnce;
    private string?   lastFingerprint;
    private DateTime  lastSentAt = DateTime.MinValue;

    /// <summary>
    ///     创建
    /// </summary>
    /// <param name="jobs">任务单通道</param>
    /// <param name="pid">（占位）游戏进程号</param>
    /// <param name="behavior">模拟的方式</param>
    public CatSimulatedAfan(CatJobChannel jobs, int pid, Behavior behavior)
    {
        this.jobs     = jobs;
        this.pid      = pid;
        this.behavior = behavior;
    }

    /// <summary>每一步的间隔</summary>
    public TimeSpan Step { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>没有变化时多久重发一次（心跳）</summary>
    public TimeSpan Heartbeat { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>模拟截图写到哪里</summary>
    public string ShotsDirectory { get; init; } = CatShots.Directory;

    /// <summary>
    ///     一直推进到取消（占位进程退出或收到 close）
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                Tick(DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[CatSimAfan] 模拟回报这一步出错");
            }

            await Task.Delay(Step, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     推进一步: 按任务单推进并在有变化（或到了心跳时间）时回报一次, 再按推进后的进度报等级
    /// </summary>
    internal void Tick(DateTime nowUtc)
    {
        var job = SimJob.Parse(jobs.JobJson);

        if (job != null && job.Revision != lastRevision)
        {
            Merge(job);
            lastRevision = job.Revision;
        }

        if (behavior != Behavior.Manual)
        {
            var (state, reason, active, shots) = Advance(job);
            Send(job, state, reason, active, shots, nowUtc);
        }

        jobs.ReportLevels(pid, Levels(job));
    }

    /// <summary>任务单里练级项的当前等级填进 35 项数组（按 ExpArrayIndex）, 其余为 0; 没有任务单时用 <see cref="SampleLevels" /></summary>
    private int[] Levels(SimJob? job)
    {
        if (job == null)
            return SampleLevels.ToArray();

        var levels = new int[LEVEL_COUNT];

        foreach (var item in job.Items)
        {
            if (item.Kind != "leveling" || item.ClassJobId is not { } classJob || !ExpArrayIndex.TryGetValue(classJob, out var index))
                continue;

            var current = progress.TryGetValue(item.ItemId, out var local) && local.Current is { } value ? value : item.Current ?? 0;
            levels[index] = Math.Max(levels[index], (int)Math.Clamp(current, 0, 200));
        }

        return levels;
    }

    /// <summary>新的 revision: 新增的项按任务单起步, 已有的项保留已做的进度（任务单落后时不倒退）, 删掉的项不再管</summary>
    private void Merge(SimJob job)
    {
        var alive = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in job.Items.Where(x => x.Routine != null))
        {
            alive.Add(item.ItemId);

            if (!progress.TryGetValue(item.ItemId, out var local))
            {
                progress[item.ItemId] = new ItemProgress(item.Current, item.Done || item.Reached);
                continue;
            }

            if (item.Current is { } fromJob && (local.Current == null || fromJob > local.Current))
                local.Current = fromJob;

            local.Done |= item.Done;
        }

        foreach (var removed in progress.Keys.Where(x => !alive.Contains(x)).ToArray())
            progress.Remove(removed);
    }

    private (string State, JsonObject? Reason, string? Active, List<(string ItemId, string Kind)> Shots) Advance(SimJob? job)
    {
        var shots = new List<(string, string)>();

        if (job == null)
            return ("waiting", null, null, shots);

        // 进世界后先核对角色: 第一步只报 waiting
        if (!characterChecked)
        {
            characterChecked = true;
            return ("waiting", null, null, shots);
        }

        if (blockedAtRevision is { } blockedRevision)
        {
            if (job.Revision <= blockedRevision)
                return ("blocked", Reason("noProgress", "30 分钟没有进展"), FirstPending(job)?.ItemId, shots);

            blockedAtRevision = null;
        }

        if (job.Desired == "pause")
            return ("paused", null, FirstPending(job)?.ItemId, shots);

        if (job.Desired == "stop")
            return ("stopped", null, null, shots);

        if (!job.Items.Any(x => x.Routine != null))
            return ("idle", null, null, shots);

        var active = FirstPending(job);

        if (active == null)
            return ("done", null, null, shots);

        var local = progress[active.ItemId];

        if (behavior == Behavior.Blocked && !blockedOnce && local.Steps >= 2)
        {
            blockedOnce       = true;
            blockedAtRevision = job.Revision;
            return ("blocked", Reason("noProgress", "30 分钟没有进展"), active.ItemId, shots);
        }

        local.Steps++;

        if (local.Current is { } current && active.Target is { } target)
        {
            local.StartedFrom ??= current;
            var step = Math.Max(1, (long)Math.Ceiling((target - local.StartedFrom.Value) / 5.0));
            local.Current = Math.Min(target, current + step);
            local.Done    = local.Current >= target;
        }
        else
            local.Done = local.Steps >= 3;

        if (!progressShotTaken && job.ProgressAt != null)
        {
            progressShotTaken = true;
            shots.Add((active.ItemId, "progress"));
        }

        if (local.Done && job.CompletionShots)
            shots.Add((active.ItemId, "completion"));

        return FirstPending(job) == null ? ("done", null, null, shots) : ("running", null, FirstPending(job)!.ItemId, shots);
    }

    /// <summary>按 order 第一个还没做完的可自动做的项</summary>
    private SimItem? FirstPending(SimJob job) =>
        job.Items.Where(x => x.Routine != null)
           .OrderBy(x => x.Order)
           .FirstOrDefault(x => progress.TryGetValue(x.ItemId, out var local) && !local.Done && !(local.Current is { } c && x.Target is { } t && c >= t));

    private void Send(SimJob? job, string state, JsonObject? reason, string? active, List<(string ItemId, string Kind)> shots, DateTime nowUtc)
    {
        var items = new JsonArray();

        if (job != null)
        {
            foreach (var item in job.Items.Where(x => x.Routine != null).OrderBy(x => x.Order))
            {
                if (!progress.TryGetValue(item.ItemId, out var local))
                    continue;

                items.Add(new JsonObject { ["itemId"] = item.ItemId, ["current"] = local.Current, ["done"] = local.Done });
            }
        }

        var fingerprint = $"{job?.Revision}|{state}|{reason?.ToJsonString()}|{active}|{items.ToJsonString()}";

        if (shots.Count == 0 && fingerprint == lastFingerprint && nowUtc - lastSentAt < Heartbeat)
            return;

        var number = ++seq;
        var at     = CatProtocol.FormatTimestamp(nowUtc);
        var report = new JsonObject
        {
            ["schema"]       = CatJobChannel.PROGRESS_SCHEMA,
            ["pid"]          = pid,
            ["revision"]     = job?.Revision ?? 0,
            ["seq"]          = number,
            ["state"]        = state,
            ["reason"]       = reason,
            ["activeItemId"] = active,
            ["items"]        = items
        };

        if (shots.Count > 0)
        {
            var list = new JsonArray();

            foreach (var (itemId, kind) in shots)
            {
                if (WriteShot(itemId, kind, number) is { } file)
                    list.Add(new JsonObject { ["itemId"] = itemId, ["kind"] = kind, ["file"] = file, ["at"] = at });
            }

            if (list.Count > 0)
                report["shots"] = list;
        }

        report["character"] = job?.Character is JsonObject character
                                  ? new JsonObject { ["name"] = character["name"]?.DeepClone(), ["homeWorldId"] = character["homeWorldId"]?.DeepClone() }
                                  : null;
        report["afan"]      = VERSION;
        report["at"]        = at;

        var response = jobs.PostProgress(report.ToJsonString());

        if (response.StatusCode != 200)
            Log.Warning("[CatSimAfan] 模拟回报没有被接受: {Status} {Body}", response.StatusCode, response.Json);

        lastFingerprint = fingerprint;
        lastSentAt      = nowUtc;
    }

    /// <summary>写一张模拟截图（二维码图, 内容是 pid、项与种类）, 返回完整路径; 写不了返回 null</summary>
    private string? WriteShot(string itemId, string kind, long number)
    {
        try
        {
            System.IO.Directory.CreateDirectory(ShotsDirectory);
            var path = Path.Combine(ShotsDirectory, $"{pid}-{number}.png");
            File.WriteAllBytes(path, new CatZxingQrCodec().EncodePng($"cat-sim pid={pid} item={itemId} kind={kind} seq={number}"));
            return path;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[CatSimAfan] 写模拟截图失败");
            return null;
        }
    }

    private static JsonObject Reason(string code, string text) =>
        new() { ["code"] = code, ["text"] = text };

    private sealed class ItemProgress(long? current, bool done)
    {
        public long? Current { get; set; } = current;

        public bool Done { get; set; } = done;

        /// <summary>这一项开始做时的数值, 每步加 (目标 − 它) / 5</summary>
        public long? StartedFrom { get; set; }

        public int Steps { get; set; }
    }

    private sealed record SimItem(string ItemId, int Order, string? Kind, int? ClassJobId, long? Current, long? Target, bool Done, string? Routine)
    {
        public bool Reached => Current is { } current && Target is { } target && current >= target;
    }

    /// <summary>模拟用得着的任务单字段</summary>
    private sealed record SimJob(long Revision, string Desired, IReadOnlyList<SimItem> Items, bool CompletionShots, string? ProgressAt, JsonNode? Character)
    {
        public static SimJob? Parse(string? json)
        {
            if (json == null)
                return null;

            var root = JsonNode.Parse(json)!.AsObject();
            var items = new List<SimItem>();

            if (root["items"] is JsonArray list)
            {
                foreach (var node in list)
                {
                    if (node is not JsonObject item || Text(item["itemId"]) is not { } itemId)
                        continue;

                    items.Add
                    (
                        new SimItem
                        (
                            itemId,
                            (int)(Number(item["order"]) ?? 0),
                            Text(item["kind"]),
                            Number(item["classJobId"]) is { } classJob ? (int)classJob : null,
                            Number(item["current"]),
                            Number(item["target"]),
                            item["done"] is JsonValue done && done.GetValueKind() == JsonValueKind.True,
                            Text(item["routine"])
                        )
                    );
                }
            }

            var shots = root["shots"] as JsonObject;

            return new SimJob
            (
                Number(root["revision"]) ?? 0,
                Text(root["desired"]) ?? "run",
                items,
                shots?["completion"] is not JsonValue completion || completion.GetValueKind() != JsonValueKind.False,
                Text(shots?["progressAt"]),
                root["character"]
            );
        }

        private static string? Text(JsonNode? node) =>
            node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

        private static long? Number(JsonNode? node)
        {
            if (node is not JsonValue value || value.GetValueKind() != JsonValueKind.Number)
                return null;

            if (value.TryGetValue(out long whole))
                return whole;

            return value.TryGetValue(out double real) ? (long)Math.Floor(real) : null;
        }
    }
}
