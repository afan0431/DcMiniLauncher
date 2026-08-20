using Serilog;
using XIVLauncher.Common.Util;
using XIVLauncher.DCTravel;
using XIVLauncher.InGame;
using XIVLauncher.Login;
using XIVLauncher.Login.Workflow;

namespace XIVLauncher.Windows.ViewModel.Main.Services;

public sealed class DCTravelRuntimeService : ILoginSessionRefreshSink, IDisposable
{
    private const int MAINTENANCE_RECOVERY_INTERVAL_MINUTES = 5;

    private readonly Action<string> setSdoAreaAction;

    private CancellationTokenSource? recoveryCts;
    private Task?                    recoveryTask;
    private int                      sessionVersion;

    public DCTravelClient    Client   { get; }
    public DCTravelListener? Listener { get; private set; }

    /// <summary>
    ///     超域旅行维护状态变更事件, 供 ViewModel 订阅刷新 UI。
    /// </summary>
    public event Action<DCTravelMaintenanceState>? MaintenanceStateChanged;

    /// <summary>
    ///     当前超域旅行监听端口。0 表示未启动监听。
    /// </summary>
    public int DcTravelPort { get; private set; }

    public DCTravelRuntimeService(Action<string> setSdoAreaAction)
    {
        ArgumentNullException.ThrowIfNull(setSdoAreaAction);

        this.setSdoAreaAction = setSdoAreaAction;
        Client = new DCTravelClient(string.Empty)
        {
            SetSdoAreaFunc = name => this.setSdoAreaAction(name)
        };

        Client.MaintenanceDetected += () =>
        {
            if (Listener == null)
                return;

            Log.Warning("[DCTravelListener] 运行时检测到超域旅行服务维护, 启动恢复定时器");
            StartMaintenanceRecovery(Volatile.Read(ref sessionVersion));
            MaintenanceStateChanged?.Invoke(DCTravelMaintenanceState.UnderMaintenance);
        };
    }

    public void Bind(LoginSessionRefreshContext context) =>
        Client.BindLoginSessionRefresh(context);

    /// <summary>
    ///     建立超域旅行会话并开监听端口, 返回端口号。
    /// </summary>
    /// <remarks>
    ///     这里**不**按「本次是否注入 Dalamud / Minion」来决定启不启：本方法是唯一建立会话
    ///     （BeginSession + GetValidCookie + 保活）的地方, 启动器自己的超域传送页面也用同一个
    ///     <see cref="Client" />。按代理去卡, 会把「什么都不注」的用户的超域传送一起卡死。
    ///     游戏内那半自己会挑执行者: 有 Dalamud → 插件读 <c>XL.DcTraveler</c> 端口;
    ///     只 Minion → 注入的 native 模块被启动器经命名管道驱动（F4），不走这个端口。
    /// </remarks>
    public async Task<int> StartAsync()
    {
        Stop();

        Client.BeginSession();
        var version = Volatile.Read(ref sessionVersion);
        DcTravelPort                            = APIHelper.GetAvailablePort();
        RunningGameRegistry.CurrentDcTravelPort = DcTravelPort;

        // 上一轮被强杀时留下的端口文件会让游戏内那侧一直往死端口上撞, 先扫掉
        RunningGameRegistry.PruneStalePortFiles();

        // 无论初始化是否成功, 始终启动监听器 —— 游戏内插件可通过 RPC 错误区分维护状态
        var inGameTravel = new InGameTravelCoordinator(Client);

        Listener = new DCTravelListener(Client, DcTravelPort, false)
        {
            // F4: 游戏内换大区的三条接口 —— bot / 游戏内 UI 用普通 HTTP 就能驱动:
            //   POST /dctravel/ingame-travel          发起（默认立刻返回, 传 wait:true 才同步等）
            //   GET  /dctravel/ingame-travel/status   查进度（游戏内 UI 显示「正在排队…」靠它）
            //   GET  /dctravel/ingame-travel/areas    可选目标 + 拥挤度, 填下拉框用
            InGameTravelHandler        = inGameTravel.HandleAsync,
            // 没跑过的 pid 也要给个成形的对象 —— 直接把 null 序列化出去就是字面量 "null",
            // 游戏内那侧 json.decode 得到 nil, 会当成「回应解析失败」而不是「还没跑过」
            InGameTravelStatusProvider = pid => pid is { } id
                                                    ? InGameTravelJobs.Get(id) ?? new InGameTravelStatus { Pid = id }
                                                    : (object)InGameTravelJobs.All(),
            InGameTravelAreasProvider  = inGameTravel.QueryTargetsAsync,
            //   POST /dctravel/ingame-travel/switch-area   换登录大区(标题界面): 不动角色、不下单、无冷却
            //   GET  /dctravel/ingame-travel/login-areas   可切换的大区列表
            InGameSwitchAreaHandler    = inGameTravel.HandleSwitchAreaAsync,
            InGameLoginAreasProvider   = InGameTravelCoordinator.QueryLoginAreasAsync,
            //   GET/POST /dctravel/ingame-travel/settings  抄 DcTraveler 那四项设置
            InGameTravelSettingsHandler = InGameTravelCoordinator.HandleSettings
        };

        _ = Listener.StartAsync();
        Log.Information("[DCTravelListener] 打开监听端口: {DcTravelPort}", DcTravelPort);

        try
        {
            await Client.GetValidCookie().ConfigureAwait(false);
            _ = Client.KeepCookieAlive();

            if (Client.MaintenanceState == DCTravelMaintenanceState.UnderMaintenance)
            {
                Log.Warning("[DCTravelListener] 超域旅行服务维护中, 启动后台恢复定时器");
                StartMaintenanceRecovery(version);
            }
        }
        catch (Exception ex)
        {
            // 初始化失败但监听器已启动, 插件会收到可读的错误
            Log.Warning(ex, "[DCTravelListener] 超域旅行初始化失败, 监听器仍在运行");

            if (Client.MaintenanceState == DCTravelMaintenanceState.UnderMaintenance)
                StartMaintenanceRecovery(version);
        }

        MaintenanceStateChanged?.Invoke(Client.MaintenanceState);
        return DcTravelPort;
    }

    public void ConfigureQuickLoginRefresh(Func<Task<string>> refreshGameSessionIdByQuickLoginFunc)
    {
        ArgumentNullException.ThrowIfNull(refreshGameSessionIdByQuickLoginFunc);
        Client.RefreshGameSessionIDByQuickLoginFunc = refreshGameSessionIdByQuickLoginFunc;
    }

    public void Stop()
    {
        Interlocked.Increment(ref sessionVersion);
        StopMaintenanceRecovery();

        var listener = Listener;
        Listener     = null;
        DcTravelPort = 0;

        RunningGameRegistry.CurrentDcTravelPort = 0;

        try
        {
            listener?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "无法关闭 DCTravelListener");
        }
    }

    public void Dispose()
    {
        Stop();
        Client.Dispose();
    }

    #region 维护自动恢复

    private void StartMaintenanceRecovery(int version)
    {
        if (recoveryTask is { IsCompleted: false })
            return;

        StopMaintenanceRecovery();

        recoveryCts  = new CancellationTokenSource();
        recoveryTask = RunMaintenanceRecoveryLoopAsync(version, recoveryCts.Token);
    }

    private void StopMaintenanceRecovery()
    {
        recoveryCts?.Cancel();
        recoveryCts?.Dispose();
        recoveryCts  = null;
        recoveryTask = null;
    }

    private async Task RunMaintenanceRecoveryLoopAsync(int version, CancellationToken ct)
    {
        Log.Information("[DCTravelListener] 维护恢复定时器已启动, 间隔 {Interval} 分钟", MAINTENANCE_RECOVERY_INTERVAL_MINUTES);

        while (!ct.IsCancellationRequested && version == Volatile.Read(ref sessionVersion))
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(MAINTENANCE_RECOVERY_INTERVAL_MINUTES), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                var state = await Client.TryRecoverFromMaintenanceAsync().ConfigureAwait(false);

                if (state == DCTravelMaintenanceState.Normal && version == Volatile.Read(ref sessionVersion))
                {
                    Log.Information("[DCTravelListener] 超域旅行服务已恢复, 重新启动保活");
                    // 旧 Listener 无需重启 —— 它引用同一 Client, 恢复后 RPC 控制器直接可用
                    _ = Client.KeepCookieAlive();

                    MaintenanceStateChanged?.Invoke(state);
                    break;
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[DCTravelListener] 维护恢复检查失败");
            }
        }

        Log.Information("[DCTravelListener] 维护恢复定时器已停止");
    }

    #endregion
}
