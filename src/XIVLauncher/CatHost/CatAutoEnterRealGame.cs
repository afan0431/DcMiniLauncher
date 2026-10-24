using System.Diagnostics;
using Serilog;
using XIVLauncher.DCTravel;
using XIVLauncher.InGame;
using XIVLauncher.Login.Models;

namespace XIVLauncher.CatHost;

/// <summary>
///     自动进入角色的真实游戏一侧: 注入并连上游戏内模块, 换大厅复用 <see cref="InGameTravelService" /> 的那套步骤。
///     管道和换服的闸（按进程号）只在发命令时占着, <see cref="Release" /> 后别的操作（游戏内跨区、读选角列表）才用得了模块。
///     不自动进入角色的游戏读职业等级（<see cref="CatLevelWatch" />）也用它注入和通话。
/// </summary>
internal sealed class CatAutoEnterRealGame : ICatAutoEnterGame, IDisposable
{
    private const int PIPE_CONNECT_TIMEOUT_MS = 10_000;

    private readonly Process                   process;
    private readonly DCTravelClient            travelClient;
    private readonly IReadOnlyList<LoginArea>  areas;
    private readonly Action<string>            areaEntered;
    private readonly Stopwatch                 clock = Stopwatch.StartNew();

    private MiniModuleClient? module;
    private IDisposable?      gate;
    private IReadOnlyList<CatAutoEnterWorld>? worlds;

    /// <summary>
    ///     创建
    /// </summary>
    /// <param name="process">游戏进程</param>
    /// <param name="travelClient">超域会话（现签票据、查服务器表）</param>
    /// <param name="areas">大区列表（含各大区的主机名）</param>
    /// <param name="currentAreaName">游戏启动时连的大区</param>
    /// <param name="areaEntered">角色在某个大区进入游戏后调用</param>
    public CatAutoEnterRealGame(Process process, DCTravelClient travelClient, IReadOnlyList<LoginArea> areas, string currentAreaName, Action<string> areaEntered)
    {
        this.process      = process;
        this.travelClient = travelClient;
        this.areas        = areas;
        this.areaEntered  = areaEntered;
        CurrentAreaName   = currentAreaName;
    }

    /// <inheritdoc />
    public string CurrentAreaName { get; private set; }

    /// <inheritdoc />
    public int ProcessId => process.Id;

    /// <summary>
    ///     接管来的游戏（原守护进程起的）: 里面没有模块时, 补注入受环境变量 <see cref="CatModuleInjection.ADOPT_INJECT_VARIABLE" /> 控制,
    ///     并且多个游戏同时被接管（更新启动器后）时经跨进程锁一个一个注、注完隔一会儿再放下一个
    /// </summary>
    public bool AdoptedGame { get; init; }

    /// <summary>
    ///     只为读当前角色和职业等级而注入（不自动进入角色的游戏）: 模块按只读模式装, 不装标题守卫和选角右键菜单, 游戏的行为与没注入时一样。
    ///     模块已经在了（带 Minion 的上号已按完整模式注过）就照旧
    /// </summary>
    public bool ReadOnlyModule { get; init; }

    /// <inheritdoc />
    public IReadOnlyList<string> AreaNames => areas.Select(x => x.AreaName).ToArray();

    /// <inheritdoc />
    public TimeSpan Elapsed => clock.Elapsed;

    /// <inheritdoc />
    public bool ModuleBusy =>
        InGameTravelJobs.IsRunning(process.Id) || (gate == null && InGameTravelService.IsGateHeld(process.Id));

    /// <inheritdoc />
    public bool HasExited
    {
        get
        {
            try
            {
                return process.HasExited;
            }
            catch (Exception)
            {
                return true;
            }
        }
    }

    /// <inheritdoc />
    public Task<string?> AttachAsync(CancellationToken cancellationToken) =>
        CatModuleInjection.InjectWhenReadyAsync(new CatProcessModuleTarget(process), ReadOnlyModule, AdoptedGame, null, cancellationToken);

    /// <inheritdoc />
    public async Task<string> SendAsync(string command, CancellationToken cancellationToken)
    {
        if (module == null)
        {
            if (InGameTravelJobs.IsRunning(process.Id))
                throw new InvalidOperationException("这个客户端正在换大区, 游戏内模块被占用");

            gate ??= InGameTravelService.TryEnterGate(process.Id) ?? throw new InvalidOperationException("游戏内模块正被别的操作占用");

            var client = new MiniModuleClient(process.Id);

            try
            {
                await client.ConnectAsync(PIPE_CONNECT_TIMEOUT_MS, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                client.Dispose();
                Release();
                throw;
            }

            module = client;
        }

        try
        {
            return await module.SendAsync(command, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Release();
            throw;
        }
    }

    /// <inheritdoc />
    public void Release()
    {
        module?.Dispose();
        module = null;
        gate?.Dispose();
        gate = null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CatAutoEnterWorld>> LoadWorldsAsync(CancellationToken cancellationToken)
    {
        if (worlds != null)
            return worlds;

        try
        {
            var source = await travelClient.QueryGroupListTravelSource().ConfigureAwait(false);

            worlds = source.SelectMany(area => area.GroupList.Select(group => new CatAutoEnterWorld(group.GroupCode ?? string.Empty, group.GroupName, area.AreaName)))
                           .Where(x => !string.IsNullOrEmpty(x.Code))
                           .ToArray();

            return worlds;
        }
        catch (Exception ex)
        {
            // 拿不到表时照常往下走: 只是角色列表不带中文服务器名, 也判断不了超域中的角色在哪个大区
            Log.Warning(ex, "[CatAutoEnter] 取服务器表失败");
            return [];
        }
    }

    /// <inheritdoc />
    public async Task<string?> SwitchAreaAsync(string areaName, CancellationToken cancellationToken)
    {
        var area = areas.FirstOrDefault(x => string.Equals(x.AreaName, areaName, StringComparison.Ordinal));

        if (area == null)
            return $"大区列表里没有 {areaName}";

        // 换大厅的步骤全程走本对象的通道: 闸和管道由这里占着, 不让换服服务自己再去连
        var result = await new InGameTravelService(travelClient)
                           .SwitchLoginAreaAsync(this, process.Id, area, null, cancellationToken)
                           .ConfigureAwait(false);

        if (!result.Ok)
            return result.Message;

        CurrentAreaName = area.AreaName;
        return null;
    }

    /// <inheritdoc />
    public void AreaEntered(string areaName)
    {
        try
        {
            areaEntered(areaName);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[CatAutoEnter] 记录角色所在大区失败");
        }
    }

    /// <inheritdoc />
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, cancellationToken);

    /// <inheritdoc />
    public void Dispose() => Release();
}
