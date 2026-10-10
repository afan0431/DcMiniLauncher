using System.Diagnostics;
using Serilog;
using XIVLauncher.Account;
using XIVLauncher.Account.Cred;
using XIVLauncher.Minion;

namespace XIVLauncher.InGame;

/// <summary>
///     一个守护进程（无界面或界面版）对它守着的游戏的守护记录与守护锁: 记录的写、改、删都经同一把锁, 不会互相覆盖;
///     记着写过记录的游戏（删时核对创建时间, 防进程号复用后误删）和拿着的守护锁（游戏结束时放掉）
/// </summary>
public sealed class GameRecordWriter
{
    /// <summary>本进程的创建时间, 写进守护记录的「守护者」</summary>
    public static DateTimeOffset SelfStartedAt { get; } = MinionOccupancy.GetProcessStartedAt(Process.GetCurrentProcess());

    private readonly object stateLock = new();

    /// <summary>写过守护记录的游戏进程 → 它的创建时间</summary>
    private readonly Dictionary<int, DateTimeOffset> recordedGames = [];

    /// <summary>本进程守着的游戏 → 它的守护锁</summary>
    private readonly Dictionary<int, IDisposable> guardClaims = [];

    /// <summary>本进程对守护记录的写、改、删都经这把锁</summary>
    private readonly SemaphoreSlim recordLock = new(1, 1);

    /// <summary>
    ///     认领一个游戏的守护权并拿着, 直到 <see cref="Delete" />。拿不到（已有别的进程在守）返回 false
    /// </summary>
    public bool Claim(int processId, DateTimeOffset processStartedAt)
    {
        if (GameRecords.TryClaimGuard(processId, processStartedAt) is not { } claim)
            return false;

        Hold(processId, claim);
        return true;
    }

    /// <summary>
    ///     拿着一个已认领的守护锁（如受理 adopt 时认领的）, 直到 <see cref="Delete" />
    /// </summary>
    public void Hold(int processId, IDisposable claim)
    {
        lock (stateLock)
            guardClaims[processId] = claim;
    }

    /// <summary>
    ///     记下这个游戏的记录已由本进程写好（删记录时按这里的创建时间核对）
    /// </summary>
    public void MarkRecorded(int processId, DateTimeOffset processStartedAt)
    {
        lock (stateLock)
            recordedGames[processId] = processStartedAt;
    }

    /// <summary>
    ///     这个游戏的记录是否由本进程写好且还没删; 是则给出它的创建时间
    /// </summary>
    public bool TryGetRecorded(int processId, out DateTimeOffset processStartedAt)
    {
        lock (stateLock)
            return recordedGames.TryGetValue(processId, out processStartedAt);
    }

    /// <summary>
    ///     在记录锁下生成并写一份记录（生成返回 null 则不写）。失败只记日志, 返回 false
    /// </summary>
    public async Task<bool> WriteAsync(Func<Task<GameRecord?>> build)
    {
        await recordLock.WaitAsync().ConfigureAwait(false);

        try
        {
            return await build().ConfigureAwait(false) is { } record && GameRecords.Write(record);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[GameRecord] 写守护记录失败（不影响游戏, 只是本进程死了之后接管不了）");
            return false;
        }
        finally
        {
            recordLock.Release();
        }
    }

    /// <summary>
    ///     写一个刚开始守护的游戏的记录, 写成了就记下它（之后的改写、删除都认这份）
    /// </summary>
    public async Task<bool> WriteNewAsync(int processId, DateTimeOffset processStartedAt, Func<Task<GameRecord?>> build)
    {
        var written = await WriteAsync(build).ConfigureAwait(false);

        if (written)
            MarkRecorded(processId, processStartedAt);

        return written;
    }

    /// <summary>
    ///     游戏退出（或崩溃重开换了新进程）时删掉它的守护记录并放掉守护锁
    /// </summary>
    public void Delete(int processId)
    {
        DateTimeOffset startedAt;
        IDisposable?   claim;
        bool           recorded;

        lock (stateLock)
        {
            recorded = recordedGames.Remove(processId, out startedAt);
            guardClaims.Remove(processId, out claim);
        }

        if (recorded)
        {
            recordLock.Wait();

            try
            {
                GameRecords.Delete(processId, startedAt);
            }
            finally
            {
                recordLock.Release();
            }
        }

        claim?.Dispose();
    }

    /// <summary>
    ///     加密凭证写进记录; 账号库选了「不加密」时不存（记录目录本机所有用户可读）
    /// </summary>
    public static async Task<string?> EncryptOrNullAsync(AccountManager accountManager, string? text) =>
        string.IsNullOrEmpty(text) || accountManager.CurrentCredType == CredType.NoEncryption
            ? null
            : await accountManager.Encrypt(text).ConfigureAwait(false);
}
