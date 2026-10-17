using System.Text.Json;
using XIVLauncher.InGame;

namespace XIVLauncher.CatHost;

/// <summary>
///     任务单与守护记录之间的接线（真实启动器用）: 记录新写、交接时带上当前任务单; setJob 换了任务单就改写当前游戏的记录;
///     记录还没写好时换的, 记下来等记录写好后补写; 交接停止中或已交接的不再改（记录已归接管方）; 接管时从记录恢复。
///     没有任务单通道（外壳不支持）时什么也不做
/// </summary>
internal sealed class CatJobRecords
{
    private readonly GameRecordWriter records;
    private readonly CatJobChannel?   jobs;
    private readonly Func<int?>       currentPid;
    private readonly Func<bool>       handingOff;

    /// <summary>有一次改写因为记录还没写好被跳过, 记录写好后要补写</summary>
    private int dirty;

    /// <summary>
    ///     创建并接上任务单通道的变更通知
    /// </summary>
    /// <param name="records">本进程的守护记录</param>
    /// <param name="jobs">任务单通道, 外壳不支持时为 null</param>
    /// <param name="currentPid">当前守着的游戏进程号, 还没有为 null</param>
    /// <param name="handingOff">是否正在交接停止或已交接</param>
    public CatJobRecords(GameRecordWriter records, CatJobChannel? jobs, Func<int?> currentPid, Func<bool> handingOff)
    {
        this.records    = records;
        this.jobs       = jobs;
        this.currentPid = currentPid;
        this.handingOff = handingOff;

        if (jobs != null)
            jobs.Changed += () => _ = RewriteAsync();
    }

    /// <summary>写进新记录的任务单, 没有为 null</summary>
    public JsonElement? Current => jobs?.JobElement;

    /// <summary>
    ///     改写已有记录时用的任务单: 内存里有就用内存里的（更新）, 否则保留记录里原有的
    /// </summary>
    public JsonElement? CurrentOr(JsonElement? fromRecord) => jobs?.JobElement ?? fromRecord;

    /// <summary>
    ///     接管: 从记录恢复任务单（不是这次上号的不恢复）, 并马上认这个游戏, 游戏内 Afan 取任务单不用等到 game.adopted
    /// </summary>
    public void Restore(GameRecord record, int pid)
    {
        jobs?.Restore(record.CatJob);
        jobs?.GameStarted(pid);
    }

    /// <summary>
    ///     把当前任务单写进当前游戏的守护记录; 记录还没写好就记下, 等 <see cref="RecordWrittenAsync" /> 补写
    /// </summary>
    public Task<bool> RewriteAsync() =>
        records.WriteAsync
        (() =>
            {
                if (handingOff())
                    return Task.FromResult<GameRecord?>(null);

                if (currentPid() is not { } pid || !records.TryGetRecorded(pid, out var startedAt))
                {
                    Interlocked.Exchange(ref dirty, 1);
                    return Task.FromResult<GameRecord?>(null);
                }

                Interlocked.Exchange(ref dirty, 0);

                return Task.FromResult
                (
                    GameRecords.ReadMatching(pid, startedAt) is { } record
                        ? record with { CatJob = jobs?.JobElement, UpdatedAt = DateTimeOffset.UtcNow }
                        : null
                );
            }
        );

    /// <summary>
    ///     记录刚写好（启动、崩溃重开、接管）: 期间被跳过的改写补一次
    /// </summary>
    public Task RecordWrittenAsync() =>
        Interlocked.Exchange(ref dirty, 0) == 1 ? RewriteAsync() : Task.CompletedTask;
}
