using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Serilog;
using XIVLauncher.Account.Cred;
using XIVLauncher.Common.Constant;
using XIVLauncher.InGame;
using XIVLauncher.Minion;

namespace XIVLauncher.CatHost;

/// <summary>
///     界面版守着的一个游戏: 进入守护时的记录, 以及交接时现取最新登录凭证、跨区会话的办法
/// </summary>
public sealed class CatUiGuardGame
{
    /// <summary>游戏进程</summary>
    public required Process Process { get; init; }

    /// <summary>
    ///     进入守护时要写的守护记录; 其中的凭证（<see cref="GameRecord.Tgt" />、<see cref="GameRecord.Guid" />）和守护者
    ///     由 <see cref="CatUiGuard" /> 填, 这里给的值不用
    /// </summary>
    public required GameRecord Record { get; init; }

    /// <summary>当前的登录凭证（明文 TGT、guid）; 进入守护和交接时各取一次, 写记录前加密</summary>
    public Func<(string? Tgt, string? Guid)> Credentials { get; init; } = () => (null, null);

    /// <summary>当前的盛趣跨区网页会话（明文 nsessionid）; 交接时取, 写记录前加密</summary>
    public Func<string?> TravelSession { get; init; } = () => null;

    /// <summary>跨区监听是否开着</summary>
    public Func<bool> TravelListening { get; init; } = () => false;
}

/// <summary>
///     交接通道的描述文件 <c>&lt;RoamingPath&gt;\cat-guard\&lt;本进程 pid&gt;.json</c>: Cat 工作台据此连管道、握手、核对管道另一端是不是这个进程
/// </summary>
/// <param name="PipeName">管道名（dml-ui-guard- 加 32 位十六进制）</param>
/// <param name="Token">握手令牌</param>
/// <param name="LauncherPid">本进程号</param>
/// <param name="LauncherStartedAt">本进程创建时间（UTC ISO 8601）</param>
public sealed record CatUiGuardDescriptor(string PipeName, string Token, int LauncherPid, string LauncherStartedAt);

/// <summary>
///     handoff 回复里交出去的一个游戏
/// </summary>
/// <param name="Pid">游戏进程号</param>
/// <param name="ProcessStartedAt">游戏进程创建时间（UTC ISO 8601）</param>
/// <param name="AccountName">守护记录里的账号名</param>
/// <param name="MinionFingerprint">挂着的 Minion 卡的指纹; 没挂为 null</param>
public sealed record CatUiHandedGame(int Pid, string ProcessStartedAt, string AccountName, string? MinionFingerprint);

/// <summary>
///     界面版 handoff 的回复: 接受时带交出去的全部游戏; 拒绝时带 <see cref="CatCodes" /> 失败码
/// </summary>
/// <param name="Accepted">是否已交接（本进程随后退出）</param>
/// <param name="Code">拒绝时的失败码</param>
/// <param name="Message">拒绝原因</param>
/// <param name="Games">接受时交出去的游戏</param>
public sealed record CatUiHandOffResult(bool Accepted, string? Code = null, string? Message = null, IReadOnlyList<CatUiHandedGame>? Games = null)
{
    /// <summary>不交接</summary>
    public static CatUiHandOffResult Rejected(string code, string message) => new(false, code, message);
}

/// <summary>
///     界面版启动器守游戏期间的守护记录与交接通道: 每个守着的游戏写一份 <see cref="GameRecord" /> 并拿着守护锁;
///     第一个游戏进入守护时开一个只对当前用户开放的命名管道（dml-cat/1 握手）并写描述文件, 最后一个游戏结束守护时关掉。
///     管道上只受理 handoff: 所有游戏都能交接时重写记录（最新凭证与跨区会话）、回复游戏列表, 然后本进程以
///     <see cref="CatHostRuntime.EXIT_HANDED_OFF" /> 退出, 游戏、记录、伴随程序、端口文件都不动, 由 Cat 工作台的无界面副本接管;
///     有任何一个不能交接就整体拒绝, 照常守护。
/// </summary>
public sealed class CatUiGuard : ICatRpcHandler
{
    /// <summary>描述文件目录名（在 RoamingPath 下）</summary>
    public const string DESCRIPTOR_FOLDER_NAME = "cat-guard";

    /// <summary>等交接回复送到工作台的上限</summary>
    private static readonly TimeSpan ReplyDeliveryTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    ///     界面版用的实例
    /// </summary>
    public static CatUiGuard Current { get; } = new
    (
        () => Path.Combine(Paths.RoamingPath, DESCRIPTOR_FOLDER_NAME),
        // Windows Hello 加密的账号库不能交接, 凭证也不存（免得为加密弹出验证）
        text => App.AccountManager.CurrentCredType == CredType.WindowsHello
                    ? Task.FromResult<string?>(null)
                    : GameRecordWriter.EncryptOrNullAsync(App.AccountManager, text),
        () => App.AccountManager.CurrentCredType,
        ExitProcess
    );

    private readonly Func<string>                 descriptorFolder;
    private readonly Func<string?, Task<string?>> encryptOrNull;
    private readonly Func<CredType>               credType;
    private readonly Action<int>                  exit;

    private readonly GameRecordWriter records = new();

    /// <summary>交接与进出守护互斥: 交接定论之前游戏不会进来或离开, 已交接之后离开的一律不做收尾</summary>
    private readonly SemaphoreSlim handOffLock = new(1, 1);

    /// <summary>守着的游戏、启动计数、管道状态都经这把锁</summary>
    private readonly object gate = new();

    private readonly Dictionary<int, CatUiGuardGame> games = [];

    private int                      starting;
    private bool                     handedOff;
    private CatRpcServer?            server;
    private CancellationTokenSource? serverCts;
    private string?                  descriptorPath;

    /// <summary>
    ///     创建一个实例（还不开管道）
    /// </summary>
    /// <param name="descriptorFolder">描述文件目录</param>
    /// <param name="encryptOrNull">凭证加密; 账号库不加密时返回 null（不存）</param>
    /// <param name="credType">账号库的加密方式</param>
    /// <param name="exit">交接后结束本进程</param>
    internal CatUiGuard(Func<string> descriptorFolder, Func<string?, Task<string?>> encryptOrNull, Func<CredType> credType, Action<int> exit)
    {
        this.descriptorFolder = descriptorFolder;
        this.encryptOrNull    = encryptOrNull;
        this.credType         = credType;
        this.exit             = exit;
    }

    /// <summary>
    ///     是否已交接（本进程即将退出）
    /// </summary>
    public bool HandedOff
    {
        get
        {
            lock (gate)
                return handedOff;
        }
    }

    /// <summary>
    ///     当前描述文件路径; 交接通道没开时为 null
    /// </summary>
    public string? DescriptorPath
    {
        get
        {
            lock (gate)
                return descriptorPath;
        }
    }

    /// <summary>
    ///     开始起一个游戏（起游戏、挂 Minion、注模块, 直到 <see cref="EnterAsync" /> 写好记录）: 期间 handoff 一律回 busy。
    ///     返回值释放时结束（可重复释放）
    /// </summary>
    public IDisposable BeginStarting()
    {
        lock (gate)
            starting++;

        return new StartingScope(this);
    }

    /// <summary>
    ///     一个游戏进入守护: 认领守护锁、写守护记录（缺账号信息时不写, 之后的 handoff 回 gameNotFound）, 交接通道还没开就开。
    ///     任何失败只记日志, 不影响游戏
    /// </summary>
    public async Task EnterAsync(CatUiGuardGame game)
    {
        var pid       = game.Record.Pid;
        var startedAt = game.Record.ProcessStartedAt;

        await handOffLock.WaitAsync().ConfigureAwait(false);

        try
        {
            if (!records.Claim(pid, startedAt))
                Log.Warning("[CatUiGuard] 游戏 {Pid} 的守护锁拿不到（不该发生）, 照常守护但别的进程可能也来接管", pid);

            if (string.IsNullOrEmpty(game.Record.SndaId) || string.IsNullOrEmpty(game.Record.AccountUserName))
                Log.Warning("[CatUiGuard] 游戏 {Pid} 缺账号信息, 不写守护记录, 这个游戏不能交接", pid);
            else
            {
                var written = await records.WriteNewAsync
                              (
                                  pid,
                                  startedAt,
                                  async () =>
                                  {
                                      var (tgt, guid) = game.Credentials();

                                      return game.Record with
                                      {
                                          Tgt = await encryptOrNull(tgt).ConfigureAwait(false),
                                          Guid = await encryptOrNull(guid).ConfigureAwait(false),
                                          DcTravelSession = null,
                                          GuardPid = Environment.ProcessId,
                                          GuardStartedAt = GameRecordWriter.SelfStartedAt,
                                          UpdatedAt = DateTimeOffset.UtcNow
                                      };
                                  }
                              ).ConfigureAwait(false);

                Log.Information("[CatUiGuard] 游戏 {Pid} 进入守护, 守护记录{Written}", pid, written ? "已写" : "没写成");
            }

            lock (gate)
                games[pid] = game;

            StartChannel();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[CatUiGuard] 登记守护失败（不影响游戏）");
        }
        finally
        {
            handOffLock.Release();
        }
    }

    /// <summary>
    ///     一个游戏结束守护（退出, 或崩溃后要重开）: 删记录、放守护锁, 没有在守的游戏时关交接通道。
    ///     返回 false = 本进程已交接, 调用方不得再做任何收尾（游戏、伴随程序、登记都留给接管的进程）
    /// </summary>
    public bool Leave(int processId)
    {
        handOffLock.Wait();

        try
        {
            if (HandedOff)
                return false;

            bool known;

            lock (gate)
                known = games.Remove(processId);

            records.Delete(processId);

            if (known)
                Log.Information("[CatUiGuard] 游戏 {Pid} 结束守护, 守护记录已删", processId);

            StopChannelIfIdle();
            return true;
        }
        finally
        {
            handOffLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<object?> HandleAsync(string method, JsonElement parameters, CancellationToken cancellationToken)
    {
        if (method != CatHandOff.METHOD)
            throw new CatRpcException(CatRpcException.METHOD_NOT_FOUND, $"未知方法: {method}");

        await handOffLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var result = await PrepareHandOffAsync().ConfigureAwait(false);

            if (result.Accepted)
                _ = Task.Run(FinishHandOffAsync, CancellationToken.None);
            else
                Log.Information("[CatUiGuard] 不交接: {Code} {Message}", result.Code, result.Message);

            return result;
        }
        finally
        {
            handOffLock.Release();
        }
    }

    /// <summary>
    ///     判定能不能交接; 能则重写全部记录、挡住新的游戏内跨区、标记已交接。全有或全无: 拒绝时已挡的跨区全部放掉
    /// </summary>
    private async Task<CatUiHandOffResult> PrepareHandOffAsync()
    {
        CatUiGuardGame[] guarded;

        lock (gate)
        {
            if (handedOff)
                return CatUiHandOffResult.Rejected(CatCodes.BUSY, "已交接, 本进程即将退出");

            if (starting > 0)
                return CatUiHandOffResult.Rejected(CatCodes.BUSY, "有游戏正在启动（起游戏、挂 Minion 或注入模块）, 稍后再试");

            guarded = games.Values.ToArray();
        }

        if (guarded.Length == 0)
            return CatUiHandOffResult.Rejected(CatCodes.GAME_NOT_FOUND, "本进程没有在守的游戏");

        if (guarded.FirstOrDefault(HasExited) is { } exited)
            return CatUiHandOffResult.Rejected(CatCodes.BUSY, $"游戏 {exited.Record.Pid} 已退出（可能开着崩溃对话框或正在重开）, 稍后再试");

        var accountsCredType = credType();

        // 无人值守的接管进程解不开 Windows Hello 加密的凭证, 接管会失败, 游戏就没人守了
        if (accountsCredType == CredType.WindowsHello)
            return CatUiHandOffResult.Rejected(CatCodes.UNSUPPORTED, "账号库使用 Windows Hello 加密, 无人值守的接管进程解不开凭证");

        // 账号库不加密时记录里不存 TGT 和网页会话, 接管方续不上跨区
        if (accountsCredType == CredType.NoEncryption && guarded.Any(x => SafeInvoke(x.TravelListening)))
            return CatUiHandOffResult.Rejected(CatCodes.UNSUPPORTED, "账号库没有加密, 守护记录里不存登录凭证, 交接后游戏内跨区会不可用");

        if (guarded.FirstOrDefault(x => !records.TryGetRecorded(x.Record.Pid, out _)) is { } unrecorded)
            return CatUiHandOffResult.Rejected(CatCodes.GAME_NOT_FOUND, $"游戏 {unrecorded.Record.Pid} 没有守护记录, 交接后没有进程接得了");

        // 不再接新的换大区（做到一半会被进程退出截断）; 已有一次在进行就等它结束
        var held = new List<int>();

        foreach (var game in guarded)
        {
            if (!InGameTravelJobs.TryHold(game.Record.Pid))
                return RejectAndRelease(held, CatCodes.BUSY, $"游戏 {game.Record.Pid} 的游戏内跨区正在进行, 等它结束再交接");

            held.Add(game.Record.Pid);
        }

        var handed = new List<CatUiHandedGame>();

        foreach (var game in guarded)
        {
            var pid = game.Record.Pid;
            records.TryGetRecorded(pid, out var startedAt);

            GameRecord? rewritten = null;

            var written = await records.WriteAsync
                          (async () =>
                              {
                                  if (GameRecords.ReadMatching(pid, startedAt) is not { } record)
                                      return null;

                                  var (tgt, guid) = game.Credentials();
                                  var session     = game.TravelSession();

                                  rewritten = record with
                                  {
                                      Tgt = await encryptOrNull(tgt).ConfigureAwait(false) ?? record.Tgt,
                                      Guid = await encryptOrNull(guid).ConfigureAwait(false) ?? record.Guid,
                                      DcTravelSession = await encryptOrNull(session).ConfigureAwait(false),
                                      UpdatedAt = DateTimeOffset.UtcNow
                                  };

                                  return rewritten;
                              }
                          ).ConfigureAwait(false);

            if (!written || rewritten == null)
                return RejectAndRelease(held, CatCodes.GAME_NOT_FOUND, $"游戏 {pid} 的守护记录读不到或写不进去, 交接后没有进程接得了");

            handed.Add(new CatUiHandedGame(pid, CatProtocol.FormatTimestamp(rewritten.ProcessStartedAt), rewritten.AccountName, rewritten.MinionFingerprint));
        }

        // 写记录期间游戏退出了（多半是崩了）: 接管方接不了已经退出的游戏, 由本进程照常处理崩溃重开或收尾
        if (guarded.FirstOrDefault(HasExited) is { } gone)
            return RejectAndRelease(held, CatCodes.BUSY, $"游戏 {gone.Record.Pid} 刚退出（可能开着崩溃对话框）, 稍后再试");

        lock (gate)
        {
            if (starting == 0)
                handedOff = true;
        }

        if (!HandedOff)
            return RejectAndRelease(held, CatCodes.BUSY, "有游戏正在启动（起游戏、挂 Minion 或注入模块）, 稍后再试");

        Log.Information
        (
            "[CatUiGuard] 交接: {Count} 个游戏（{Pids}）的记录已更新, 不登出、不删记录, 回复送达后本进程退出",
            handed.Count,
            string.Join(", ", handed.Select(x => x.Pid))
        );

        return new CatUiHandOffResult(true, Games: handed);
    }

    private static CatUiHandOffResult RejectAndRelease(List<int> held, string code, string message)
    {
        foreach (var pid in held)
            InGameTravelJobs.Release(pid);

        return CatUiHandOffResult.Rejected(code, message);
    }

    /// <summary>
    ///     交接已定: 等回复送到、删描述文件, 然后结束本进程（不做任何收尾）
    /// </summary>
    private async Task FinishHandOffAsync()
    {
        CatRpcServer? current;
        string?       path;

        lock (gate)
        {
            current = server;
            path    = descriptorPath;
        }

        try
        {
            if (current != null && !await current.WaitForDeliveryAsync(ReplyDeliveryTimeout).ConfigureAwait(false))
                Log.Warning("[CatUiGuard] 交接回复 {Seconds} 秒内没能确认送达, 照常退出", ReplyDeliveryTimeout.TotalSeconds);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[CatUiGuard] 等交接回复送达失败, 照常退出");
        }

        DeleteFile(path);
        Log.Information("[CatUiGuard] 已交接, 本进程以 {ExitCode} 退出（游戏、记录、伴随程序都不动）", CatHostRuntime.EXIT_HANDED_OFF);
        exit(CatHostRuntime.EXIT_HANDED_OFF);
    }

    /// <summary>
    ///     交接通道还没开就开: 清掉进程已不在的旧描述文件, 起管道, 写描述文件。失败只记日志（这个进程的游戏就交接不了）
    /// </summary>
    private void StartChannel()
    {
        lock (gate)
        {
            if (server != null)
                return;
        }

        var folder = descriptorFolder();
        PruneDescriptors(folder);

        var pipeName  = CatProtocol.UI_GUARD_PIPE_NAME_PREFIX + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var token     = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var path      = Path.Combine(folder, $"{Environment.ProcessId}.json");
        var newServer = new CatRpcServer(pipeName, token, this, AppUtil.GetAssemblyVersion() ?? string.Empty);

        try
        {
            newServer.Listen();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[CatUiGuard] 交接通道的管道创建失败, 这个进程守着的游戏不能交接");
            newServer.Dispose();
            return;
        }

        var descriptor = new CatUiGuardDescriptor(pipeName, token, Environment.ProcessId, CatProtocol.FormatTimestamp(GameRecordWriter.SelfStartedAt));

        if (!WriteDescriptor(path, descriptor))
        {
            newServer.Dispose();
            return;
        }

        var cts = new CancellationTokenSource();

        lock (gate)
        {
            server         = newServer;
            serverCts      = cts;
            descriptorPath = path;
        }

        _ = Task.Run(() => newServer.RunAsync(cts.Token), CancellationToken.None);
        Log.Information("[CatUiGuard] 交接通道已开: 管道 {PipeName}, 描述文件 {Path}", pipeName, path);
    }

    /// <summary>
    ///     没有在守、也没有在起的游戏时关交接通道并删描述文件; 已交接时不动（由交接收尾删）
    /// </summary>
    private void StopChannelIfIdle()
    {
        CatRpcServer?            stopping;
        CancellationTokenSource? cts;
        string?                  path;

        lock (gate)
        {
            if (handedOff || games.Count > 0 || starting > 0 || server == null)
                return;

            stopping       = server;
            cts            = serverCts;
            path           = descriptorPath;
            server         = null;
            serverCts      = null;
            descriptorPath = null;

            // 在锁里删: 出锁后新开的通道写的是同一个文件名, 不能被这里删掉
            DeleteFile(path);
        }

        cts?.Cancel();
        stopping.Dispose();
        Log.Information("[CatUiGuard] 没有在守的游戏, 交接通道已关");
    }

    private void EndStarting()
    {
        lock (gate)
            starting--;

        StopChannelIfIdle();
    }

    /// <summary>
    ///     写描述文件: 先写临时文件再替换, 读的一方不会看到半个文件
    /// </summary>
    private static bool WriteDescriptor(string path, CatUiGuardDescriptor descriptor)
    {
        var tmpPath = $"{path}.{Guid.NewGuid():N}.tmp";

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(tmpPath, JsonSerializer.Serialize(descriptor, CatProtocol.JsonOptions));
            File.Move(tmpPath, path, true);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[CatUiGuard] 写描述文件失败, 这个进程守着的游戏不能交接");
            DeleteFile(tmpPath);
            return false;
        }
    }

    /// <summary>
    ///     删掉写它的进程已不在的描述文件、写到一半留下的临时文件
    /// </summary>
    private static void PruneDescriptors(string folder)
    {
        string[] files;

        try
        {
            files = Directory.Exists(folder) ? Directory.GetFiles(folder) : [];
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[CatUiGuard] 枚举描述文件失败");
            return;
        }

        foreach (var file in files)
        {
            try
            {
                if (file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                {
                    if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddMinutes(-1))
                        File.Delete(file);

                    continue;
                }

                if (!file.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || IsLiveDescriptor(file))
                    continue;

                File.Delete(file);
                Log.Information("[CatUiGuard] 清掉残留描述文件 {Path}", file);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[CatUiGuard] 清残留描述文件失败 {Path}", file);
            }
        }
    }

    private static bool IsLiveDescriptor(string file)
    {
        try
        {
            using var stream     = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var       descriptor = JsonSerializer.Deserialize<CatUiGuardDescriptor>(stream, CatProtocol.JsonOptions);

            return descriptor != null &&
                   DateTimeOffset.TryParse(descriptor.LauncherStartedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var startedAt) &&
                   MinionOccupancy.IsSameProcessAlive(descriptor.LauncherPid, startedAt);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void DeleteFile(string? path)
    {
        try
        {
            if (path != null && File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[CatUiGuard] 删文件失败 {Path}", path);
        }
    }

    private static bool HasExited(CatUiGuardGame game)
    {
        try
        {
            return game.Process.HasExited;
        }
        catch (Exception)
        {
            return true;
        }
    }

    private static bool SafeInvoke(Func<bool> check)
    {
        try
        {
            return check();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[CatUiGuard] 查跨区监听失败, 按开着处理");
            return true;
        }
    }

    private static void ExitProcess(int exitCode)
    {
        Log.CloseAndFlush();
        Environment.Exit(exitCode);
    }

    private sealed class StartingScope(CatUiGuard owner) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
                owner.EndStarting();
        }
    }
}
