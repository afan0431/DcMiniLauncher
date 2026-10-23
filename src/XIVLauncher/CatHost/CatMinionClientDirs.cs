using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;
using XIVLauncher.Common.Util;
using XIVLauncher.InGame;
using XIVLauncher.Minion;

namespace XIVLauncher.CatHost;

/// <summary>
///     客户端目录的使用者: 当前负责它的守护进程（Cat 上号的无界面登录器进程）与它挂着的游戏。
///     两者有一个还活着, 这个目录就还在用, 别的进程清残留时不能删
/// </summary>
public sealed record CatMinionClientDirOwner
{
    /// <summary>守护进程 PID</summary>
    public required int OwnerPid { get; init; }

    /// <summary>守护进程创建时间（防进程号复用）</summary>
    public required DateTimeOffset OwnerStartedAt { get; init; }

    /// <summary>挂着这个目录的游戏 PID; 还没挂到游戏上为 null</summary>
    public int? GamePid { get; init; }

    /// <summary>游戏进程创建时间</summary>
    public DateTimeOffset? GameStartedAt { get; init; }

    /// <summary>写入时间</summary>
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>
///     挂载前准备客户端目录的结果: InstallPath = 挂载用的目录（null = 照旧用主目录）; TensorCoreEnabled = 那个目录里 TensorCore 开没开（null = 不知道）;
///     ErrorCode / ErrorMessage = 准备失败, 不挂, 按 Minion 结果上报
/// </summary>
public sealed record CatMinionClientDirPrepared(string? InstallPath, bool? TensorCoreEnabled, string? ErrorCode = null, string? ErrorMessage = null)
{
    /// <summary>直接挂主目录</summary>
    public static CatMinionClientDirPrepared MainInstall { get; } = new(null, null);

    /// <summary>准备失败</summary>
    public bool Failed => ErrorCode != null;

    /// <summary>失败结果</summary>
    public static CatMinionClientDirPrepared Fail(string code, string message) => new(null, null, code, message);
}

/// <summary>
///     一个守护进程（一次上号）用的 Minion 客户端目录: 首次挂载前组装, 崩溃重开、补挂时复用, 交接时随守护记录交给接管方, 游戏最终退出后收尾删除
/// </summary>
public interface ICatMinionClientDirs
{
    /// <summary>当前持有的客户端目录; 没有为 null</summary>
    string? CurrentPath { get; }

    /// <summary>
    ///     挂载前准备: 已持有目录时复用并把使用者改成这个游戏; 否则清掉本机残留后按卡里的配置组装一个新的。
    ///     设置里关掉客户端目录时返回 <see cref="CatMinionClientDirPrepared.MainInstall" />
    /// </summary>
    Task<CatMinionClientDirPrepared> PrepareAsync(CatMinionLaunch card, string operationId, Process game, DateTimeOffset gameStartedAt, CancellationToken cancellationToken);

    /// <summary>
    ///     接管: 认下守护记录里的客户端目录, 把使用者改成本进程与这个游戏。路径为空或已不是客户端目录时什么都不做
    /// </summary>
    void Adopt(string? clientDir, int gamePid, DateTimeOffset gameStartedAt);

    /// <summary>
    ///     游戏最终退出（不再重开、没有交接）后收尾: 写回主目录并删掉客户端目录。没有持有目录时什么都不做; 出错只记日志
    /// </summary>
    Task RetireAsync();
}

/// <summary>
///     <see cref="ICatMinionClientDirs" /> 的实现, 用本机 Minion 主目录与 <see cref="MinionClientDir" />
/// </summary>
public sealed class CatMinionClientDirs : ICatMinionClientDirs
{
    /// <summary>客户端目录里记使用者的文件名</summary>
    public const string OWNER_FILE_NAME = "cat-client-owner.json";

    /// <summary>清残留与组装期间持有, 免得别的进程把正在组装（还没写使用者）的目录当残留删掉</summary>
    private const string MUTEX_NAME = @"Local\DcMiniLauncher-MinionClientDirs";

    /// <summary>目录编号里操作号部分的长度上限</summary>
    private const int MAX_OPERATION_ID_LENGTH = 32;

    private static readonly TimeSpan MutexTimeout = TimeSpan.FromSeconds(60);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy   = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented          = true
    };

    private readonly Func<bool>    enabled;
    private readonly Func<string>  mainInstall;
    private readonly Func<string, string> clientsRoot;
    private readonly SemaphoreSlim gate = new(1, 1);

    private MinionClientDir? dir;

    /// <summary>
    ///     enabled = 是否用客户端目录（默认读设置 <c>MinionClientDirEnabled</c>）; mainInstall = Minion 主目录（默认 <see cref="MinionInstall.InstallPath" />）;
    ///     clientsRoot = 由主目录得出客户端目录根（默认 <see cref="MinionClientDir.RootFor" />, 测试里换成临时目录）
    /// </summary>
    public CatMinionClientDirs(Func<bool>? enabled = null, Func<string>? mainInstall = null, Func<string, string>? clientsRoot = null)
    {
        this.enabled     = enabled ?? (() => App.Settings?.MinionClientDirEnabled ?? true);
        this.mainInstall = mainInstall ?? (() => MinionInstall.InstallPath);
        this.clientsRoot = clientsRoot ?? MinionClientDir.RootFor;
    }

    /// <inheritdoc />
    public string? CurrentPath => dir?.DirectoryPath;

    /// <inheritdoc />
    public async Task<CatMinionClientDirPrepared> PrepareAsync(CatMinionLaunch card, string operationId, Process game, DateTimeOffset gameStartedAt, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (dir is { } current)
            {
                // 崩溃重开或补挂: 同一个目录接着用, 不重新组装（游戏里切过的配置要留着）
                if (Directory.Exists(current.DirectoryPath))
                {
                    TryWriteOwner(current.DirectoryPath, game.Id, gameStartedAt);
                    return Prepared(current);
                }

                Log.Warning("[Minion] 客户端目录 {Dir} 已不在, 重新组装", current.DirectoryPath);
                dir = null;
            }

            if (!enabled())
            {
                if (card.Profile != null || card.EnsureMods is { Count: > 0 })
                    Log.Information("[Minion] 设置里关掉了客户端目录, 直接挂主目录, 不按配置 {Profile}、强制打开 {Mods} 摆插件", card.Profile ?? "(无)", string.Join(",", card.EnsureMods ?? []));

                return CatMinionClientDirPrepared.MainInstall;
            }

            var main = mainInstall();
            var root = clientsRoot(main);

            using var mutex = await CrossProcessMutex.TryAcquireAsync(MUTEX_NAME, MutexTimeout).ConfigureAwait(false);

            if (mutex == null)
                Log.Warning("[Minion] 等客户端目录锁超时, 这次不清残留目录, 照常组装");
            else
                await CleanupStaleAsync(root, cancellationToken).ConfigureAwait(false);

            var path = FreePath(root, BuildId(operationId, game.Id));

            MinionClientDir assembled;

            try
            {
                assembled = await Task.Run(() => MinionClientDir.Assemble(main, path, card.Profile, card.EnsureMods), cancellationToken).ConfigureAwait(false);
            }
            catch (MinionClientDirException ex)
            {
                Log.Warning(ex, "[Minion] 组装客户端目录失败: {Error}", ex.Error);
                return FailureOf(ex);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warning(ex, "[Minion] 组装客户端目录 {Dir} 失败", path);
                return CatMinionClientDirPrepared.Fail(CatCodes.ATTACH_FAILED, $"组装 Minion 客户端目录失败: {ex.Message}");
            }

            foreach (var mod in assembled.MissingMods)
                Log.Warning("[Minion] 上号要求打开插件 {Mod}, 但本机 Minion 没有装, 客户端目录里没有它", mod);

            if (!TryWriteOwner(assembled.DirectoryPath, game.Id, gameStartedAt))
                Log.Warning("[Minion] 客户端目录 {Dir} 的使用者没写成, 本进程退出后它可能被别的进程提前清掉", assembled.DirectoryPath);

            dir = assembled;
            return Prepared(assembled);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public void Adopt(string? clientDir, int gamePid, DateTimeOffset gameStartedAt)
    {
        if (string.IsNullOrWhiteSpace(clientDir))
            return;

        MinionClientDir? opened;

        try
        {
            opened = MinionClientDir.Open(clientDir);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Minion] 读回客户端目录 {Dir} 失败, 不接管它", clientDir);
            return;
        }

        if (opened == null)
        {
            Log.Warning("[Minion] 守护记录里的客户端目录 {Dir} 已不在或没有组装信息, 不接管它", clientDir);
            return;
        }

        dir = opened;
        TryWriteOwner(opened.DirectoryPath, gamePid, gameStartedAt);
        Log.Information("[Minion] 已接管客户端目录 {Dir}（配置={Profile}）", opened.DirectoryPath, opened.Snapshot.Profile ?? "(照主目录)");
    }

    /// <inheritdoc />
    public async Task RetireAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);

        try
        {
            if (dir is not { } current)
                return;

            dir = null;

            try
            {
                var result = await MinionClientDir.RetireAsync(current.DirectoryPath, current.Snapshot.MainInstall).ConfigureAwait(false);

                Log.Information
                (
                    "[Minion] 客户端目录 {Dir} 已收尾: 写回配置={Profiles}, 写回文件={Files}, 已删除={Deleted}, 警告 {Warnings} 条",
                    current.DirectoryPath,
                    result.ProfilesWrittenBack,
                    result.FilesWrittenBack,
                    result.Deleted,
                    result.Warnings.Count
                );
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[Minion] 收尾客户端目录 {Dir} 失败, 下次上号时按残留清理", current.DirectoryPath);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    ///     客户端目录的编号: <c>&lt;首个游戏 PID&gt;-&lt;操作号&gt;</c>; 操作号只留字母、数字、<c>-</c>、<c>_</c>, 最长 32 个字符, 留空时只用 PID
    /// </summary>
    public static string BuildId(string? operationId, int gamePid)
    {
        var builder = new StringBuilder();

        foreach (var ch in operationId ?? string.Empty)
        {
            if (builder.Length >= MAX_OPERATION_ID_LENGTH)
                break;

            if (char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_')
                builder.Append(ch);
        }

        return builder.Length == 0 ? gamePid.ToString() : $"{gamePid}-{builder}";
    }

    /// <summary>
    ///     客户端目录是否还在用: 守护进程或它记下的游戏有一个还活着（按 PID 与创建时间核对）。没有使用者记录的一律算不在用
    /// </summary>
    /// <param name="owner">使用者记录</param>
    /// <param name="isAlive">按 PID 与创建时间判断进程是否还活着</param>
    public static bool IsInUse(CatMinionClientDirOwner? owner, Func<int, DateTimeOffset?, bool> isAlive)
    {
        if (owner == null)
            return false;

        if (isAlive(owner.OwnerPid, owner.OwnerStartedAt))
            return true;

        return owner.GamePid is { } gamePid && isAlive(gamePid, owner.GameStartedAt);
    }

    /// <summary>
    ///     读客户端目录里的使用者记录; 没有或读不出为 null
    /// </summary>
    public static CatMinionClientDirOwner? ReadOwner(string clientDir)
    {
        var path = Path.Combine(clientDir, OWNER_FILE_NAME);

        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<CatMinionClientDirOwner>(File.ReadAllText(path), JsonOptions) : null;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[Minion] 读客户端目录使用者 {Path} 失败", path);
            return null;
        }
    }

    /// <summary>
    ///     写客户端目录里的使用者记录（先写临时文件再替换）
    /// </summary>
    public static void WriteOwner(string clientDir, CatMinionClientDirOwner owner)
    {
        var path = Path.Combine(clientDir, OWNER_FILE_NAME);
        var tmp  = path + ".tmp";

        File.WriteAllText(tmp, JsonSerializer.Serialize(owner, JsonOptions), new UTF8Encoding(false));
        File.Move(tmp, path, true);
    }

    /// <summary>
    ///     清掉客户端目录根（<c>MINI-clients</c>）下已没人用的客户端目录（先写回再删）
    /// </summary>
    private static async Task CleanupStaleAsync(string root, CancellationToken cancellationToken)
    {
        try
        {
            var removed = await MinionClientDir.CleanupStaleAsync(root, id => IsInUse(ReadOwner(Path.Combine(root, id)), MinionOccupancy.IsSameProcessAlive), cancellationToken)
                                               .ConfigureAwait(false);

            if (removed > 0)
                Log.Information("[Minion] 挂载前清掉了 {Count} 个残留的客户端目录", removed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Minion] 清理残留客户端目录失败, 照常组装");
        }
    }

    /// <summary>
    ///     编号对应的目录已被占（不该发生）时在后面加一段随机串
    /// </summary>
    private static string FreePath(string root, string id)
    {
        var path = Path.Combine(root, id);

        while (Directory.Exists(path) || File.Exists(path))
            path = Path.Combine(root, $"{id}-{Guid.NewGuid().ToString("N")[..6]}");

        return path;
    }

    private static CatMinionClientDirPrepared Prepared(MinionClientDir clientDir)
    {
        bool? tensorCore;

        try
        {
            tensorCore = clientDir.IsModEnabled("TensorCore");
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[Minion] 看不出客户端目录 {Dir} 里 TensorCore 开没开", clientDir.DirectoryPath);
            tensorCore = null;
        }

        return new CatMinionClientDirPrepared(clientDir.DirectoryPath, tensorCore);
    }

    /// <summary>
    ///     组装失败对应的 Minion 结果失败码与文案
    /// </summary>
    internal static CatMinionClientDirPrepared FailureOf(MinionClientDirException exception) =>
        exception.Error switch
        {
            MinionClientDirError.SettingsUnreadable => CatMinionClientDirPrepared.Fail(CatCodes.MINION_SETTINGS_UNREADABLE, "Minion 配置文件读不出"),
            MinionClientDirError.ProfileNotFound    => CatMinionClientDirPrepared.Fail(CatCodes.MINION_PROFILE_NOT_FOUND, exception.Message),
            MinionClientDirError.MainInstallMissing => CatMinionClientDirPrepared.Fail(CatCodes.MINION_NOT_CONFIGURED, exception.Message),
            _                                       => CatMinionClientDirPrepared.Fail(CatCodes.ATTACH_FAILED, $"组装 Minion 客户端目录失败: {exception.Message}")
        };

    private static bool TryWriteOwner(string clientDir, int gamePid, DateTimeOffset gameStartedAt)
    {
        try
        {
            WriteOwner
            (
                clientDir,
                new CatMinionClientDirOwner
                {
                    OwnerPid       = Environment.ProcessId,
                    OwnerStartedAt = GameRecordWriter.SelfStartedAt,
                    GamePid        = gamePid,
                    GameStartedAt  = gameStartedAt,
                    UpdatedAt      = DateTimeOffset.UtcNow
                }
            );
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Minion] 写客户端目录 {Dir} 的使用者失败", clientDir);
            return false;
        }
    }
}
