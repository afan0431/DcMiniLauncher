using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;
using XIVLauncher.Minion;

namespace XIVLauncher.InGame;

/// <summary>
///     <see cref="GameRecord.Channel" /> 的取值: 账号库里的行类型
/// </summary>
public static class GameRecordChannels
{
    public const string SDO = "sdo";

    public const string WE_GAME = "weGame";
}

/// <summary>
///     一个在跑的游戏的守护所需的全部信息, 守护进程死了之后新进程凭它接管（热更新 / 崩溃恢复）。
///     凭证字段（<see cref="Tgt" />、<see cref="Guid" />）存的是经账号库加密后的串; 账号库选了「不加密」时不存（这个目录本机所有用户可读）,
///     接管后改用快速登录凭证刷新票据。不存任何卡密和密码。
/// </summary>
public sealed record GameRecord
{
    public required int Pid { get; init; }

    public required DateTimeOffset ProcessStartedAt { get; init; }

    /// <summary>Cat 的上号操作号; 界面版启动为 null</summary>
    public string? OperationId { get; init; }

    /// <summary>账号库里的行类型: sdo / weGame</summary>
    public required string Channel { get; init; }

    /// <summary>上号请求带的账号名（WeGame 号按备注找到时与账号库的账号名不同）</summary>
    public required string AccountName { get; init; }

    /// <summary>账号库里那一行的账号名, 接管时按它找行</summary>
    public required string AccountUserName { get; init; }

    public string? AreaName { get; init; }

    /// <summary>本次启动是否注入了 Dalamud（决定接管后要不要盯崩溃处理器）</summary>
    public bool Dalamud { get; init; }

    /// <summary>上号请求是否要 Dalamud（崩溃重开时按它决定）</summary>
    public bool DalamudRequested { get; init; }

    /// <summary>崩溃处理器回「按原模式重开」时用的模式</summary>
    public bool RestartNoDalamud { get; init; }

    public bool RestartNoThirdPlugins { get; init; }

    public bool RestartNoPlugins { get; init; }

    /// <summary>游戏命令行 XL.DcTraveler 里的端口; 0 = 没开跨区服务</summary>
    public int DcTravelPort { get; init; }

    public string? SndaId { get; init; }

    /// <summary>加密后的 TGT; 接管时用它重建登录刷新, 免得重新登录</summary>
    public string? Tgt { get; init; }

    /// <summary>加密后的 guid, 与 <see cref="Tgt" /> 成对</summary>
    public string? Guid { get; init; }

    public string? MinionFingerprint { get; init; }

    public string? MinionVariant { get; init; }

    public bool AutoEnter { get; init; }

    public string? CharacterName { get; init; }

    public string? CharacterHomeWorld { get; init; }

    public int CrashDialogTimeoutSeconds { get; init; }

    /// <summary>当前守护者进程; 接管时据此判断原守护者是否还活着</summary>
    public required int GuardPid { get; init; }

    public required DateTimeOffset GuardStartedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>
///     在 <c>%ProgramData%\DcMiniLauncher\games\&lt;pid&gt;.json</c> 存每个在跑游戏的 <see cref="GameRecord" />。
///     游戏退出（不再重开）时删除; 守护进程被强杀留下的由 <see cref="PruneStale" /> 清掉。
/// </summary>
public static class GameRecords
{
    private const string FILE_EXTENSION = ".json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy   = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented          = true,
        Converters             = { new UtcTimestampConverter() }
    };

    /// <summary>
    ///     记录目录, 测试时可替换
    /// </summary>
    public static string Directory { get; set; } = Path.Combine
    (
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "DcMiniLauncher",
        "games"
    );

    public static string FilePath(int processId) =>
        Path.Combine(Directory, $"{processId}{FILE_EXTENSION}");

    /// <summary>替换记录时目标正被别人读着, 重试几次</summary>
    private const int WRITE_ATTEMPTS = 5;

    /// <summary>
    ///     写入（覆盖）记录; 先写临时文件再替换, 读的一方不会看到半个文件。失败只记日志
    /// </summary>
    public static bool Write(GameRecord record)
    {
        string? tmpPath = null;

        try
        {
            System.IO.Directory.CreateDirectory(Directory);

            var path = FilePath(record.Pid);
            tmpPath = $"{path}.{System.Guid.NewGuid():N}.tmp";
            File.WriteAllText(tmpPath, JsonSerializer.Serialize(record, JsonOptions));

            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(tmpPath, path, true);
                    return true;
                }
                catch (IOException) when (attempt < WRITE_ATTEMPTS)
                {
                    // 别的进程正读着这份记录（替换要删除权限）, 稍等再试
                    Thread.Sleep(20 * attempt);
                }
                catch (UnauthorizedAccessException) when (attempt < WRITE_ATTEMPTS)
                {
                    Thread.Sleep(20 * attempt);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[GameRecord] 写游戏记录失败 PID={Pid}", record.Pid);

            try
            {
                if (tmpPath != null && File.Exists(tmpPath))
                    File.Delete(tmpPath);
            }
            catch
            {
                // 留下的临时文件由 PruneStale 清
            }

            return false;
        }
    }

    /// <summary>
    ///     读出某个游戏的记录, 没有或损坏返回 null。读时允许别人同时替换或删除这个文件
    /// </summary>
    public static GameRecord? Read(int processId)
    {
        try
        {
            var path = FilePath(processId);

            if (!File.Exists(path))
                return null;

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<GameRecord>(stream, JsonOptions);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[GameRecord] 读游戏记录失败 PID={Pid}", processId);
            return null;
        }
    }

    /// <summary>
    ///     某个游戏的守护锁文件。守护它的进程独占打开着这个文件, 进程死了系统自动释放, 所以「锁打不开」= 有人在守
    /// </summary>
    public static string GuardLockPath(int processId, DateTimeOffset processStartedAt) =>
        Path.Combine(Directory, $"{processId}-{processStartedAt.ToUnixTimeMilliseconds() / 1000}.guard");

    /// <summary>
    ///     认领一个游戏的守护权: 成功返回锁（守护期间一直拿着, 不再守时 Dispose）, 已有别的进程在守时返回 null。
    ///     正常启动与接管都要先拿到它, 同一个游戏不会有两个守护进程（检查和认领是同一个动作, 没有空档）
    /// </summary>
    public static IDisposable? TryClaimGuard(int processId, DateTimeOffset processStartedAt)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var stream = new FileStream(GuardLockPath(processId, processStartedAt), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return new GuardClaim(stream);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    ///     这个游戏现在有没有守护进程（试一下能不能拿到守护锁, 拿到马上放掉）
    /// </summary>
    public static bool IsGuarded(int processId, DateTimeOffset processStartedAt)
    {
        if (!File.Exists(GuardLockPath(processId, processStartedAt)))
            return false;

        using var claim = TryClaimGuard(processId, processStartedAt);
        return claim == null;
    }

    private sealed class GuardClaim(FileStream stream) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
                return;

            var path = stream.Name;
            stream.Dispose();

            try
            {
                File.Delete(path);
            }
            catch
            {
                // 别人刚好又拿到了锁, 文件留给它
            }
        }
    }

    /// <summary>
    ///     删除某个游戏的记录; 给了 <paramref name="processStartedAt" /> 时只删同一个进程的（防进程号复用后误删别人的）
    /// </summary>
    public static void Delete(int processId, DateTimeOffset? processStartedAt = null)
    {
        try
        {
            if (processStartedAt is { } startedAt && Read(processId) is { } current && !SameStart(current.ProcessStartedAt, startedAt))
                return;

            var path = FilePath(processId);

            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[GameRecord] 删游戏记录失败 PID={Pid}", processId);
        }
    }

    /// <summary>
    ///     进程号 + 创建时间都对得上的记录; 对不上（进程号被复用）返回 null
    /// </summary>
    public static GameRecord? ReadMatching(int processId, DateTimeOffset processStartedAt) =>
        Read(processId) is { } record && SameStart(record.ProcessStartedAt, processStartedAt) ? record : null;


    /// <summary>
    ///     游戏进程还活着的全部记录
    /// </summary>
    public static IReadOnlyList<GameRecord> ReadAllLive() =>
        ReadAll().Where(x => x.Record != null && MinionOccupancy.IsSameProcessAlive(x.Pid, x.Record.ProcessStartedAt))
                 .Select(x => x.Record!)
                 .ToList();

    /// <summary>
    ///     清掉游戏进程已不在的记录（守护进程被强杀时它自己删不掉）。
    ///     游戏已退出但它的崩溃处理器还开着的不清: 接管的进程还要靠记录读崩溃处理器的决定
    /// </summary>
    public static void PruneStale(Func<int, bool>? hasOrphanCrashHandler = null)
    {
        foreach (var (pid, record) in ReadAll())
        {
            if (record != null && MinionOccupancy.IsSameProcessAlive(pid, record.ProcessStartedAt))
                continue;

            if (record != null && hasOrphanCrashHandler?.Invoke(pid) == true)
                continue;

            Delete(pid);
            Log.Information("[GameRecord] 清掉残留游戏记录 PID={Pid}", pid);
        }

        // 写到一半被强杀留下的临时文件、守护者已不在的锁文件
        foreach (var pattern in new[] { "*.tmp", "*.guard" })
        {
            string[] leftovers;

            try
            {
                leftovers = System.IO.Directory.Exists(Directory) ? System.IO.Directory.GetFiles(Directory, pattern) : [];
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[GameRecord] 枚举残留文件失败");
                continue;
            }

            foreach (var file in leftovers)
            {
                try
                {
                    if (pattern == "*.tmp" && File.GetLastWriteTimeUtc(file) > DateTime.UtcNow.AddMinutes(-1))
                        continue;

                    // 锁文件正被守护者独占打开时删不掉, 正好留着
                    File.Delete(file);
                }
                catch
                {
                    // 在用
                }
            }
        }
    }

    private static bool SameStart(DateTimeOffset a, DateTimeOffset b) =>
        Math.Abs((a - b).TotalSeconds) < 2;

    private static List<(int Pid, GameRecord? Record)> ReadAll()
    {
        string[] files;

        try
        {
            if (!System.IO.Directory.Exists(Directory))
                return [];

            files = System.IO.Directory.GetFiles(Directory, $"*{FILE_EXTENSION}");
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[GameRecord] 枚举游戏记录失败");
            return [];
        }

        var result = new List<(int, GameRecord?)>();

        foreach (var file in files)
        {
            if (int.TryParse(Path.GetFileNameWithoutExtension(file), out var pid))
                result.Add((pid, Read(pid)));
        }

        return result;
    }
}
