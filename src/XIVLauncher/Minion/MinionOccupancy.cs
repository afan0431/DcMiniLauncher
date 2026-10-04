using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace XIVLauncher.Minion;

/// <summary>
///     Minion 占用记录的内容: 哪张卡挂在哪个游戏进程上。不含 Keycode, 只含不可逆指纹。
/// </summary>
public sealed record MinionOccupancyRecord
{
    public required int Pid { get; init; }

    public required DateTimeOffset ProcessStartedAt { get; init; }

    public required string CardFingerprint { get; init; }

    public required string Variant { get; init; }

    public string? AccountName { get; init; }

    public required DateTimeOffset AttachedAt { get; init; }
}

/// <summary>
///     在 <c>%ProgramData%\DcMiniLauncher\minion-&lt;pid&gt;.json</c> 记录 Minion 占用, 游戏进程退出时删除。
///     界面启动和无界面启动挂 Minion 都经过 <see cref="MinionAttacher" />, 两种都会写。
/// </summary>
public static class MinionOccupancy
{
    private const string FILE_PREFIX = "minion-";

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
        "DcMiniLauncher"
    );

    /// <summary>
    ///     某个游戏进程的记录文件路径
    /// </summary>
    public static string FilePath(int processId) =>
        Path.Combine(Directory, $"{FILE_PREFIX}{processId}{FILE_EXTENSION}");

    /// <summary>
    ///     写入记录; 失败只记日志, 不影响已经挂上的 Minion
    /// </summary>
    public static bool Write(MinionOccupancyRecord record)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);

            var path    = FilePath(record.Pid);
            var tmpPath = path + ".tmp";
            File.WriteAllText(tmpPath, JsonSerializer.Serialize(record, JsonOptions));
            File.Move(tmpPath, path, true);

            Log.Information("[Minion] 已写占用记录: PID={Pid}, 卡={Fingerprint}, 行={Variant}", record.Pid, record.CardFingerprint, record.Variant);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Minion] 写占用记录失败 PID={Pid}", record.Pid);
            return false;
        }
    }

    /// <summary>
    ///     读出某个游戏进程的记录, 没有或损坏返回 null
    /// </summary>
    public static MinionOccupancyRecord? Read(int processId)
    {
        try
        {
            var path = FilePath(processId);
            return File.Exists(path) ? JsonSerializer.Deserialize<MinionOccupancyRecord>(File.ReadAllText(path), JsonOptions) : null;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[Minion] 读占用记录失败 PID={Pid}", processId);
            return null;
        }
    }

    /// <summary>
    ///     删除某个游戏进程的记录
    /// </summary>
    public static void Delete(int processId)
    {
        try
        {
            var path = FilePath(processId);

            if (!File.Exists(path))
                return;

            File.Delete(path);
            Log.Information("[Minion] 已删占用记录: PID={Pid}", processId);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Minion] 删占用记录失败 PID={Pid}", processId);
        }
    }

    /// <summary>
    ///     写入记录并在游戏进程退出时自动删除
    /// </summary>
    public static void WriteAndDeleteOnExit(MinionOccupancyRecord record, Process gameProcess)
    {
        if (!Write(record))
            return;

        _ = Task.Run
        (async () =>
            {
                try
                {
                    await gameProcess.WaitForExitAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "[Minion] 等游戏进程退出失败 PID={Pid}", record.Pid);
                }

                // 同一 PID 若已被新挂载覆盖（进程号复用）则不删别人的记录
                if (Read(record.Pid) is { } current && current.ProcessStartedAt != record.ProcessStartedAt)
                    return;

                Delete(record.Pid);
            }
        );
    }

    /// <summary>
    ///     清掉进程已不在（或进程号已被别的进程复用）的残留记录
    /// </summary>
    public static void PruneStale()
    {
        string[] files;

        try
        {
            if (!System.IO.Directory.Exists(Directory))
                return;

            files = System.IO.Directory.GetFiles(Directory, $"{FILE_PREFIX}*{FILE_EXTENSION}");
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[Minion] 枚举占用记录失败");
            return;
        }

        foreach (var file in files)
        {
            var name = Path.GetFileNameWithoutExtension(file);

            if (!int.TryParse(name.AsSpan(FILE_PREFIX.Length), out var pid))
                continue;

            if (IsSameProcessAlive(pid, Read(pid)?.ProcessStartedAt))
                continue;

            Delete(pid);
        }
    }

    /// <summary>
    ///     进程创建时间（UTC, 精确到毫秒）
    /// </summary>
    public static DateTimeOffset GetProcessStartedAt(Process process) =>
        TruncateToMilliseconds(new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero));

    private static DateTimeOffset TruncateToMilliseconds(DateTimeOffset value) =>
        new(value.Ticks - value.Ticks % TimeSpan.TicksPerMillisecond, value.Offset);

    private static bool IsSameProcessAlive(int pid, DateTimeOffset? expectedStartedAt)
    {
        try
        {
            using var process = Process.GetProcessById(pid);

            if (expectedStartedAt == null)
                return true;

            return Math.Abs((GetProcessStartedAt(process) - expectedStartedAt.Value).TotalSeconds) < 2;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (Exception ex)
        {
            // 读不到进程信息（如权限不足）时保留记录, 宁可留着也不误删活着的游戏的记录
            Log.Debug(ex, "[Minion] 检查 PID={Pid} 失败, 保留占用记录", pid);
            return true;
        }
    }
}

/// <summary>
///     时间按 UTC ISO 8601（以 Z 结尾）读写
/// </summary>
internal sealed class UtcTimestampConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        DateTimeOffset.Parse(reader.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToUniversalTime();

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture));
}
