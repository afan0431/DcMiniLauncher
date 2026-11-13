using System.Diagnostics;
using Serilog;
using XIVLauncher.InGame;

namespace XIVLauncher.CatHost;

/// <summary>
///     国际服自动进入角色的游戏一侧: 管道（<c>minilauncher-&lt;pid&gt;</c>）由注入游戏的游戏内模块开, 命令与应答和国服一致。
///     与 <see cref="CatAutoEnterRealGame" /> 的区别: 自己不注入任何东西, 只连模块的管道; 没有大区（国际服一个入口）, 也没有服务器表、超域。
///     管道闸与 <see cref="Release" /> 的语义和国服一致: 发命令时才占, 长时间等待前放开。
///     工作台靠这个类型在不在程序集里判断登录器支不支持国际服自动进入, 改名要同步改工作台外壳的探测。
/// </summary>
internal sealed class CatInternationalAutoEnter : ICatAutoEnterGame, IDisposable
{
    /// <summary>国际服只有一个入口, 没有可切换的大区; 给编排一个固定的名字</summary>
    public const string AREA_NAME = "国际服";

    private const int PIPE_CONNECT_TIMEOUT_MS = 10_000;

    private readonly Process   process;
    private readonly Stopwatch clock = Stopwatch.StartNew();

    private MiniModuleClient? module;
    private IDisposable?      gate;

    /// <summary>
    ///     创建
    /// </summary>
    /// <param name="process">国际服游戏进程</param>
    public CatInternationalAutoEnter(Process process)
    {
        this.process = process;
    }

    /// <inheritdoc />
    public string CurrentAreaName => AREA_NAME;

    /// <inheritdoc />
    public IReadOnlyList<string> AreaNames => [AREA_NAME];

    /// <inheritdoc />
    public int ProcessId => process.Id;

    /// <inheritdoc />
    public TimeSpan Elapsed => clock.Elapsed;

    /// <inheritdoc />
    public bool ModuleBusy => gate == null && InGameTravelService.IsGateHeld(process.Id);

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

    /// <summary>
    ///     不注入: 插件由国际服 Dalamud 在游戏里加载, 管道在它加载后才开。这里只确认游戏还在, 等管道由编排的握手重试兜着（见 CatAutoEnterTimings.ModuleReadyTimeout）
    /// </summary>
    public Task<string?> AttachAsync(CancellationToken cancellationToken) =>
        Task.FromResult<string?>(HasExited ? "游戏进程已退出" : null);

    /// <inheritdoc />
    public async Task<string> SendAsync(string command, CancellationToken cancellationToken)
    {
        if (module == null)
        {
            gate ??= InGameTravelService.TryEnterGate(process.Id) ?? throw new InvalidOperationException("游戏内插件正被别的操作占用");

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

    /// <summary>
    ///     国际服不查服务器表: 插件报的服务器名就是英文名（与 Cat 存的原始服务器同写法）, 直接按名字匹配; 没有表时角色列表不带中文服务器名
    /// </summary>
    public Task<IReadOnlyList<CatAutoEnterWorld>> LoadWorldsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CatAutoEnterWorld>>([]);

    /// <inheritdoc />
    public Task<string?> SwitchAreaAsync(string areaName, CancellationToken cancellationToken) =>
        Task.FromResult<string?>("国际服没有大区可以切换");

    /// <inheritdoc />
    public void AreaEntered(string areaName)
    {
        Log.Debug("[CatAutoEnter] 国际服角色已进入游戏, 不记大区");
    }

    /// <inheritdoc />
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, cancellationToken);

    /// <inheritdoc />
    public void Dispose() => Release();
}
