using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Serilog;
using XIVLauncher.DCTravel;

namespace XIVLauncher.CatHost;

/// <summary>
///     game.levels 里的当前角色（同一轮 WHOAMI 读到的）: name = 角色名; contentId = ContentId（字符串, 17 位超出 Lua double 精度）;
///     homeWorldId = 原始服务器的 World 表行号, 读不到为 0
/// </summary>
public sealed record CatLevelsCharacter(string Name, string ContentId, int HomeWorldId);

/// <summary>
///     game.duty 的参数。inDuty 为 null（未知）时也照样写出 <c>"inDuty": null</c>, 不随序列化选项省掉
/// </summary>
/// <param name="OperationId">当前操作</param>
/// <param name="Pid">游戏进程号</param>
/// <param name="InDuty">是否在副本中; null = 读到过值之后接连读不到</param>
/// <param name="At">读到的时刻, ISO 时间字符串（与 processStartedAt 同格式）</param>
public sealed record CatDutyEvent
(
    string?                                                       OperationId,
    int                                                           Pid,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] bool? InDuty,
    string                                                        At
);

/// <summary>
///     Cat 上号自动开工的任务单通道（约定 cat-job/1）: 工作台经管道方法 setJob 交来任务单, 游戏内 Afan 经本机接口
///     <c>GET /cat/job?pid=</c> 取、<c>POST /cat/progress</c> 报进度; 回报原样转成 game.jobProgress, 小模块读到的职业等级转成 game.levels,
///     读到的"是否在副本中"转成 game.duty。
///     一个无界面进程一份, 只认它守着的那个游戏的 pid。
///     工作台按这个类型（<c>XIVLauncher.CatHost.CatJobChannel</c>）在不在程序集里判断登录器支不支持 setJob, 改名要同步改工作台外壳的探测。
/// </summary>
public sealed class CatJobChannel : ICatLocalEndpoints
{
    /// <summary>任务单格式</summary>
    public const string JOB_SCHEMA = "cat-job/1";

    /// <summary>回报格式</summary>
    public const string PROGRESS_SCHEMA = "cat-progress/1";

    /// <summary>setJob 方法名</summary>
    public const string METHOD = "setJob";

    /// <summary>回报事件</summary>
    public const string PROGRESS_EVENT = "game.jobProgress";

    /// <summary>等级读数事件</summary>
    public const string LEVELS_EVENT = "game.levels";

    /// <summary>是否在副本中的事件</summary>
    public const string DUTY_EVENT = "game.duty";

    /// <summary>任务单原文上限</summary>
    public const int MAX_JOB_BYTES = 256 * 1024;

    /// <summary>回报 state 的取值</summary>
    public static readonly IReadOnlySet<string> States = new HashSet<string>(StringComparer.Ordinal)
    {
        "waiting", "running", "paused", "stopped", "blocked", "mismatch", "idle", "done"
    };

    /// <summary>必须带 reason 的 state</summary>
    public static readonly IReadOnlySet<string> StatesWithReason = new HashSet<string>(StringComparer.Ordinal) { "blocked", "mismatch" };

    /// <summary>
    ///     回报 shots[].kind 的取值: start = 开工图, completion = 完成图, progress = 进度图。
    ///     一张图对应多项时每项各带一条、file 相同, 不按 file 去重
    /// </summary>
    public static readonly IReadOnlyList<string> ShotKinds = ["start", "completion", "progress"];

    private readonly Func<string?>          operationId;
    private readonly Action<string, object> publish;
    private readonly object                 stateLock = new();

    /// <summary>每个游戏进程收到过的最大 seq（约定: 同一进程内严格递增, Afan 重载也不回退）</summary>
    private readonly Dictionary<int, long> lastSeqs = [];

    /// <summary>每个游戏进程上次发的等级读数（角色 + 数组）</summary>
    private readonly Dictionary<int, (string ContentId, int[] Levels)> lastLevels = [];

    /// <summary>每个游戏进程上次发的"是否在副本中"（null = 发过"未知"）</summary>
    private readonly Dictionary<int, bool?> lastDuty = [];

    /// <summary>每个游戏进程上一次经本机接口收下回报的时刻（Environment.TickCount64）</summary>
    private readonly Dictionary<int, long> lastPostedAt = [];

    private string? jobJson;
    private long    revision;
    private int?    gamePid;
    private string? unavailableReason;
    private string? shotsDirectory;

    /// <summary>经本机接口来的同一游戏的两条回报至少隔多久, 更密的回 429</summary>
    public TimeSpan MinProgressInterval { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>回报 shots 里的图只能在这个目录下; 不设用 <see cref="CatShots.Directory" /></summary>
    public string ShotsDirectory
    {
        get => shotsDirectory ?? CatShots.Directory;
        set => shotsDirectory = value;
    }

    /// <summary>
    ///     游戏内取不到任务单（本机接口端口没开成、接管时绑不回）的原因; 不为 null 时 setJob 回 unsupported。为 null = 可用
    /// </summary>
    public string? UnavailableReason
    {
        get
        {
            lock (stateLock)
                return unavailableReason;
        }
        set
        {
            lock (stateLock)
                unavailableReason = value;
        }
    }

    /// <summary>
    ///     创建
    /// </summary>
    /// <param name="operationId">本实例的 operationId（还没 launch / adopt 时为 null）</param>
    /// <param name="publish">
    ///     发事件（方法名, 参数）。<b>必须非阻塞</b>（只入队）: 回报、等级与副本状态在本类的锁里发, 这样 <see cref="GameEnded" /> 之后（game.exited 之前）
    ///     不会再有这个游戏的事件插进来
    /// </param>
    public CatJobChannel(Func<string?> operationId, Action<string, object> publish)
    {
        this.operationId = operationId;
        this.publish     = publish;
    }

    /// <summary>setJob 接受了新的任务单（不含从守护记录恢复）; 真实启动器据此改写守护记录</summary>
    public event Action? Changed;

    /// <summary>当前任务单原文, 没有为 null</summary>
    public string? JobJson
    {
        get
        {
            lock (stateLock)
                return jobJson;
        }
    }

    /// <summary>当前任务单的 revision, 没有为 0</summary>
    public long Revision
    {
        get
        {
            lock (stateLock)
                return jobJson == null ? 0 : revision;
        }
    }

    /// <summary>当前任务单（写守护记录用）, 没有为 null</summary>
    public JsonElement? JobElement
    {
        get
        {
            var json = JobJson;

            if (json == null)
                return null;

            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
    }

    /// <summary>当前守护的游戏进程号, 没有为 null</summary>
    public int? GamePid
    {
        get
        {
            lock (stateLock)
                return gamePid;
        }
    }

    /// <summary>
    ///     setJob: 校验后替换任务单。job 不是对象、没有整数 revision、太大 → invalidParams;
    ///     schema 不认识、sessionId 与 operationId 不一致、还没 launch / adopt → jobMismatch; revision 不比已有的新 → stale
    /// </summary>
    public CatSetJobResult SetJob(JsonElement? job)
    {
        if (Validate(job, out var raw, out var newRevision, out var sessionId) is { } rejected)
            return rejected;

        var current = operationId();

        if (current == null)
            return CatSetJobResult.Rejected(CatCodes.JOB_MISMATCH, "本进程还没有 launch 或 adopt, 不知道任务单属于哪一次上号");

        if (!string.Equals(sessionId, current, StringComparison.Ordinal))
            return CatSetJobResult.Rejected(CatCodes.JOB_MISMATCH, $"任务单的 sessionId 与本次上号的 operationId（{current}）不一致");

        lock (stateLock)
        {
            if (unavailableReason != null)
                return CatSetJobResult.Rejected(CatCodes.UNSUPPORTED, unavailableReason);

            if (jobJson != null && newRevision <= revision)
                return CatSetJobResult.StaleOk();

            jobJson  = raw;
            revision = newRevision;
        }

        Log.Information("[CatHost] 收到任务单 revision={Revision}, {Summary}", newRevision, Summarize(job!.Value));

        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[CatHost] 任务单变更的后续处理出错");
        }

        return CatSetJobResult.Ok();
    }

    /// <summary>
    ///     接管时从守护记录恢复任务单（工作台在 game.adopted 之后还会再发一次, 这里只是兜底）。
    ///     格式不对、不是这次上号的、或不比已有的新时不恢复, 返回是否恢复了
    /// </summary>
    public bool Restore(JsonElement? job)
    {
        if (job == null || Validate(job, out var raw, out var newRevision, out var sessionId) != null)
            return false;

        if (!string.Equals(sessionId, operationId(), StringComparison.Ordinal))
            return false;

        lock (stateLock)
        {
            if (jobJson != null && newRevision <= revision)
                return false;

            jobJson  = raw;
            revision = newRevision;
        }

        Log.Information("[CatHost] 从守护记录恢复任务单 revision={Revision}", newRevision);
        return true;
    }

    /// <summary>开始守护一个游戏进程（启动、崩溃重开、接管）: 之后只认这个 pid</summary>
    public void GameStarted(int pid)
    {
        lock (stateLock)
        {
            gamePid = pid;

            foreach (var other in lastSeqs.Keys.Where(x => x != pid).ToArray())
                lastSeqs.Remove(other);

            foreach (var other in lastLevels.Keys.Where(x => x != pid).ToArray())
                lastLevels.Remove(other);

            foreach (var other in lastDuty.Keys.Where(x => x != pid).ToArray())
                lastDuty.Remove(other);

            foreach (var other in lastPostedAt.Keys.Where(x => x != pid).ToArray())
                lastPostedAt.Remove(other);
        }
    }

    /// <summary>游戏已退出（不再重开）</summary>
    public void GameEnded(int pid)
    {
        lock (stateLock)
        {
            if (gamePid == pid)
                gamePid = null;
        }
    }

    /// <inheritdoc />
    public CatLocalResponse GetJob(int? pid)
    {
        if (pid is not > 0)
            return new CatLocalResponse(400, CatLocalJson.Error("pid 必须是正整数"));

        lock (stateLock)
        {
            if (gamePid != pid)
                return new CatLocalResponse(404, CatLocalJson.Error($"pid {pid} 不是本启动器守护的游戏"));

            return jobJson == null ? new CatLocalResponse(204) : new CatLocalResponse(200, jobJson);
        }
    }

    /// <inheritdoc />
    public CatLocalResponse PostProgress(string body) =>
        PostProgress(body, true);

    /// <summary>
    ///     收一条回报。rateLimited = 经本机接口来的（同一游戏两条之间不到 <see cref="MinProgressInterval" /> 回 429）; 模拟模式自己产生的回报不限
    /// </summary>
    internal CatLocalResponse PostProgress(string body, bool rateLimited)
    {
        if (ParseProgress(body, ShotsDirectory, out var progress, out var pid, out var seq) is { } error)
            return new CatLocalResponse(400, CatLocalJson.Error(error));

        // 先取 operationId 再进锁: 取它要拿外壳的锁, 两把锁不嵌套
        var  operation = operationId();
        var  now       = Environment.TickCount64;
        long currentRevision;

        lock (stateLock)
        {
            if (gamePid != pid)
                return new CatLocalResponse(404, CatLocalJson.Error($"pid {pid} 不是本启动器守护的游戏"));

            if (rateLimited && lastPostedAt.TryGetValue(pid, out var lastAt) && now - lastAt < (long)MinProgressInterval.TotalMilliseconds)
                return new CatLocalResponse(429, CatLocalJson.Error($"回报太密, 两条之间至少隔 {MinProgressInterval.TotalMilliseconds:F0} 毫秒"));

            lastPostedAt[pid] = now;
            currentRevision   = jobJson == null ? 0 : revision;

            // seq 在同一游戏进程内严格递增: 不大于已收最大值的就是重发或乱序, 不再发
            if (!lastSeqs.TryGetValue(pid, out var last) || seq > last)
            {
                lastSeqs[pid]            = seq;
                progress!["operationId"] = operation;
                publish(PROGRESS_EVENT, progress);
            }
        }

        return new CatLocalResponse(200, JsonSerializer.Serialize(new Dictionary<string, long> { ["revision"] = currentRevision }));
    }

    /// <summary>
    ///     小模块读到的全部职业等级（下标为 ExpArrayIndex）与同一轮读到的当前角色: 角色或数组与这个游戏上次发的不同才发 game.levels。
    ///     数组全为 0、角色没有 ContentId 时不发; pid ≤ 0 = 当前守护的游戏, 给了 pid 但不是当前守护的游戏时丢掉
    /// </summary>
    public void ReportLevels(int pid, CatLevelsCharacter character, IReadOnlyList<int> levels)
    {
        if (levels.Count == 0 || levels.All(x => x == 0) || string.IsNullOrEmpty(character.ContentId) || character.ContentId == "0")
            return;

        var operation = operationId();
        var copy      = levels.ToArray();

        lock (stateLock)
        {
            if (gamePid is not { } current || (pid > 0 && pid != current))
                return;

            if (lastLevels.TryGetValue(current, out var last) && last.ContentId == character.ContentId && last.Levels.SequenceEqual(copy))
                return;

            lastLevels[current] = (character.ContentId, copy);
            Log.Information("[CatHost] 职业等级 pid={Pid}: {Levels}", current, string.Join(',', copy));

            publish
            (
                LEVELS_EVENT,
                new
                {
                    operationId = operation,
                    pid         = current,
                    character   = new { name = character.Name, contentId = character.ContentId, homeWorldId = character.HomeWorldId },
                    levels      = copy
                }
            );
        }
    }

    /// <summary>
    ///     小模块读到的"是否在副本中"（Conditions 的 BoundByDuty 类标志）: 与这个游戏上次发的不同才发 game.duty（第一次读到也发）。
    ///     inDuty = null 表示读到过值之后接连读不到（未知）, 去重时与 true / false 一样当作一个值; 这个游戏还没发过值时的 null 不发。
    ///     at = 读到的时刻; pid ≤ 0 = 当前守护的游戏, 给了 pid 但不是当前守护的游戏（含游戏已退出）时丢掉
    /// </summary>
    public void ReportDuty(int pid, bool? inDuty, DateTimeOffset at)
    {
        var operation = operationId();

        lock (stateLock)
        {
            if (gamePid is not { } current || (pid > 0 && pid != current))
                return;

            if (lastDuty.TryGetValue(current, out var last) ? last == inDuty : inDuty == null)
                return;

            lastDuty[current] = inDuty;
            Log.Information("[CatHost] 副本状态 pid={Pid}: {InDuty}", current, inDuty switch { true => "在副本中", false => "不在副本中", null => "未知" });

            publish(DUTY_EVENT, new CatDutyEvent(operation, current, inDuty, CatProtocol.FormatTimestamp(at)));
        }
    }

    /// <summary>
    ///     校验任务单的形状; 通过返回 null 并给出原文、revision、sessionId
    /// </summary>
    private static CatSetJobResult? Validate(JsonElement? job, out string raw, out long newRevision, out string? sessionId)
    {
        raw         = string.Empty;
        newRevision = 0;
        sessionId   = null;

        if (job is not { ValueKind: JsonValueKind.Object } element)
            return CatSetJobResult.Rejected(CatCodes.INVALID_PARAMS, "job 必须是对象");

        raw = element.GetRawText();

        if (Encoding.UTF8.GetByteCount(raw) > MAX_JOB_BYTES)
            return CatSetJobResult.Rejected(CatCodes.INVALID_PARAMS, $"任务单超过 {MAX_JOB_BYTES} 字节");

        var schema = element.TryGetProperty("schema", out var schemaElement) && schemaElement.ValueKind == JsonValueKind.String
                         ? schemaElement.GetString()
                         : null;

        if (!string.Equals(schema, JOB_SCHEMA, StringComparison.Ordinal))
            return CatSetJobResult.Rejected(CatCodes.JOB_MISMATCH, $"不认识的任务单格式 {schema ?? "(没有 schema)"}, 本启动器只认 {JOB_SCHEMA}");

        if (!element.TryGetProperty("revision", out var revisionElement) ||
            revisionElement.ValueKind != JsonValueKind.Number ||
            !revisionElement.TryGetInt64(out newRevision) ||
            newRevision < 0)
            return CatSetJobResult.Rejected(CatCodes.INVALID_PARAMS, "revision 必须是非负整数");

        sessionId = element.TryGetProperty("sessionId", out var sessionElement) && sessionElement.ValueKind == JsonValueKind.String
                        ? sessionElement.GetString()
                        : null;

        return null;
    }

    /// <summary>
    ///     解析并校验一条回报; 通过返回 null, 否则返回 400 的原因。seq 是 Unix 毫秒起算的大数（13 位）, 全程按 long 处理;
    ///     shots 里的图只能是 <paramref name="shotsDirectory" /> 下、文件名为 <c>&lt;本条回报的 pid&gt;-&lt;数字&gt;.png</c> 的文件
    ///     （数字不必是本条回报的 seq, 按 file 取图; 一张图挂多项时多条共用同一个 file）
    /// </summary>
    internal static string? ParseProgress(string body, string shotsDirectory, out JsonObject? progress, out int pid, out long seq)
    {
        progress = null;
        pid      = 0;
        seq      = 0;

        if (string.IsNullOrWhiteSpace(body))
            return "请求体为空";

        JsonNode? node;

        try
        {
            node = JsonNode.Parse(body);

            // 每一层的对象都展开一遍: 有重复的键在这里就报（不展开要到发事件序列化时才抛, 事件就丢了）
            Materialize(node);
        }
        catch (JsonException ex)
        {
            return $"不是有效的 JSON: {ex.Message}";
        }
        catch (ArgumentException)
        {
            return "JSON 对象里有重复的键";
        }

        if (node is not JsonObject value)
            return "回报必须是 JSON 对象";

        if (!string.Equals(StringOf(value, "schema"), PROGRESS_SCHEMA, StringComparison.Ordinal))
            return $"schema 必须是 {PROGRESS_SCHEMA}";

        if (!TryGetInteger(value, "pid", out var pidValue) || pidValue is <= 0 or > int.MaxValue)
            return "pid 必须是正整数";

        if (!TryGetInteger(value, "revision", out var revisionValue) || revisionValue < 0)
            return "revision 必须是非负整数";

        if (!TryGetInteger(value, "seq", out seq) || seq < 1)
            return "seq 必须是从 1 开始的整数";

        var state = StringOf(value, "state");

        if (state == null || !States.Contains(state))
            return $"state 只能是 {string.Join(" / ", States)}";

        if (StatesWithReason.Contains(state) && (value["reason"] is not JsonObject reason || string.IsNullOrWhiteSpace(StringOf(reason, "code"))))
            return $"state 为 {state} 时必须带 reason.code";

        foreach (var name in new[] { "items", "shots" })
        {
            if (value[name] is { } list && list is not JsonArray)
                return $"{name} 必须是数组";
        }

        if (value["shots"] is JsonArray shots)
        {
            foreach (var shot in shots)
            {
                if (shot is not JsonObject entry || StringOf(entry, "itemId") is not { Length: > 0 })
                    return "shots 里每一项都要有 itemId";

                if (StringOf(entry, "kind") is not { } kind || !ShotKinds.Contains(kind))
                    return $"shots 的 kind 只能是 {string.Join(" / ", ShotKinds)}";

                if (!IsShotFile(StringOf(entry, "file"), (int)pidValue, shotsDirectory))
                    return $"shots 的 file 只能是 {shotsDirectory} 下的 {pidValue}-<seq>.png";
            }
        }

        progress = value;
        pid      = (int)pidValue;
        return null;
    }

    /// <summary>
    ///     截图文件是否在截图目录下（只这一层）、文件名是不是 <c>&lt;pid&gt;-&lt;数字&gt;.png</c> 且 pid 与回报一致
    /// </summary>
    internal static bool IsShotFile(string? file, int pid, string shotsDirectory)
    {
        if (string.IsNullOrWhiteSpace(file) || !Path.IsPathFullyQualified(file))
            return false;

        string full;
        string directory;

        try
        {
            full      = Path.GetFullPath(file);
            directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(shotsDirectory));
        }
        catch (Exception)
        {
            return false;
        }

        if (!string.Equals(Path.GetDirectoryName(full), directory, StringComparison.OrdinalIgnoreCase))
            return false;

        var name   = Path.GetFileName(full);
        var prefix = $"{pid}-";

        if (!name.StartsWith(prefix, StringComparison.Ordinal) || !name.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            return false;

        var number = name[prefix.Length..^4];
        return number.Length is > 0 and <= 19 && number.All(char.IsAsciiDigit);
    }

    private static void Materialize(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject value:
                foreach (var property in value)
                    Materialize(property.Value);
                break;

            case JsonArray list:
                foreach (var item in list)
                    Materialize(item);
                break;
        }
    }

    private static string? StringOf(JsonObject value, string name) =>
        value[name] is JsonValue text && text.GetValueKind() == JsonValueKind.String ? text.GetValue<string>() : null;

    private static bool TryGetInteger(JsonObject value, string name, out long result)
    {
        result = 0;
        return value[name] is JsonValue number && number.GetValueKind() == JsonValueKind.Number && number.TryGetValue(out result);
    }

    /// <summary>日志里的任务单摘要: desired 与项数, 不记角色名</summary>
    private static string Summarize(JsonElement job)
    {
        var desired = job.TryGetProperty("desired", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : "?";
        var items   = job.TryGetProperty("items", out var i) && i.ValueKind == JsonValueKind.Array ? i.GetArrayLength() : 0;
        var auto    = 0;
        var picked  = 0;

        if (items > 0)
        {
            foreach (var item in i.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("routine", out var routine) || routine.ValueKind != JsonValueKind.String)
                    continue;

                auto++;

                // selected 缺省（旧版 Cat）按勾上算
                if (!item.TryGetProperty("selected", out var selected) || selected.ValueKind != JsonValueKind.False)
                    picked++;
            }
        }

        return $"desired={desired}, 项 {items} 个（可自动做 {auto} 个, 勾上 {picked} 个）";
    }
}
