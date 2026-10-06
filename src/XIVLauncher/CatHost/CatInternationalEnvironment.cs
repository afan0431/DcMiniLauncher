using System.Diagnostics;
using System.IO;
using Serilog;
using XIVLauncher.Common;
using XIVLauncher.Common.Constant;
using XIVLauncher.Common.Game;
using XIVLauncher.Common.Game.International;
using XIVLauncher.Common.Util;
using XIVLauncher.Dalamud;
using XIVLauncher.Dalamud.International;
using XIVLauncher.Minion;
using XIVLauncher.Startup;

namespace XIVLauncher.CatHost;

/// <summary>国际服无界面启动用到的设置项（从启动器设置里读出来的一份快照）</summary>
/// <param name="GamePath">国际服游戏目录; 没设置为 null</param>
/// <param name="Language">客户端语言</param>
/// <param name="DpiAwareness">DPI 感知</param>
/// <param name="DalamudLoadMethod">Dalamud 加载方式</param>
/// <param name="DalamudInjectionDelayMs">Dalamud 注入延迟（毫秒）</param>
public sealed record CatInternationalSettings
(
    string?           GamePath,
    ClientLanguage    Language,
    DPIAwareness      DpiAwareness,
    DalamudLoadMethod DalamudLoadMethod,
    int               DalamudInjectionDelayMs
);

/// <summary>国际服登录与版本检查（真实实现包着 <see cref="InternationalLauncher" />）</summary>
public interface ICatInternationalLoginClient : IDisposable
{
    /// <summary>boot 是否需要更新</summary>
    Task<bool> IsBootUpdateRequiredAsync(DirectoryInfo gamePath, CancellationToken cancellationToken);

    /// <summary>登录服务是否开放</summary>
    Task<InternationalGateStatus> GetLoginStatusAsync(CancellationToken cancellationToken);

    /// <summary>游戏是否开放</summary>
    Task<InternationalGateStatus> GetGateStatusAsync(ClientLanguage language, CancellationToken cancellationToken);

    /// <summary>登录并上报版本</summary>
    Task<InternationalLoginResult> LoginAsync(string userName, string password, DirectoryInfo gamePath, ClientLanguage language, CancellationToken cancellationToken);
}

/// <summary>准备好的国际服 Dalamud（真实实现包着 <see cref="DalamudSession" />）</summary>
public interface ICatInternationalDalamudSession
{
    /// <summary>对运行中的游戏补注入</summary>
    void InjectGame(int gamePid);
}

/// <summary>国际服挂 Minion 用到的操作（真实实现走与国服相同的选行、预占、挂载、停机补报）</summary>
public interface ICatInternationalMinion
{
    /// <summary>按请求里的卡选国际服行; 选不出时返回 null 并给出失败码与消息</summary>
    MinionAccount? SelectRow(CatLaunchRequest request, ICatLaunchReporter reporter, int? selfPid, out (string Code, string Message) error);

    /// <summary>加跨进程锁选行并为这个游戏写预占记录</summary>
    Task<(MinionAccount? Row, (string Code, string Message) Error, bool Reserved)> ReserveRowAsync(CatLaunchRequest request, Process process, ICatLaunchReporter reporter);

    /// <summary>这一行挂载时会用到的敏感值（卡密、Minion 密码）, 供登记脱敏</summary>
    IEnumerable<string?> SecretsOf(MinionAccount row);

    /// <summary>挂载（国际服行）</summary>
    Task<MinionAttachResult> AttachAsync(MinionAccount row, Process process, DirectoryInfo gamePath, bool dalamudInjected, string accountName, CancellationToken cancellationToken);

    /// <summary>撤掉预占</summary>
    void ReleaseReservation(int gamePid);

    /// <summary>占用记录里这个游戏进程是否已挂着 Minion</summary>
    bool IsAttached(Process process);

    /// <summary>游戏退出后告诉 MINIONAPP 这一行停机了</summary>
    void ReportStopped(int gamePid);
}

/// <summary>
///     国际服启动器对外部世界的全部依赖（设置、网络、Dalamud、进程、Minion）。真实实现见 <see cref="CatInternationalRealEnvironment" />;
///     单测换成假的, 所以启动器本身不碰 App 全局状态。
/// </summary>
public interface ICatInternationalEnvironment
{
    /// <summary>等启动器设置加载完</summary>
    Task EnsureInitializedAsync();

    /// <summary>读设置</summary>
    CatInternationalSettings ReadSettings();

    /// <summary>取登录页地址模板等运行时配置; 不抛网络异常</summary>
    Task<InternationalClientConfig> GetClientConfigAsync(CancellationToken cancellationToken);

    /// <summary>建登录客户端; <paramref name="onSecret" /> 在每取得一个敏感值时被调用</summary>
    ICatInternationalLoginClient CreateLoginClient(InternationalClientConfig config, Action<string> onSecret);

    /// <summary>下载 / 校验国际服 Dalamud 并核对它支持的游戏版本; 不可用时返回 null 与原因</summary>
    (ICatInternationalDalamudSession? Session, string? Error) PrepareDalamud(DirectoryInfo gamePath, CatInternationalSettings settings);

    /// <summary>创建游戏进程: 有 Dalamud 就经它的注入器启动, 否则直接启动</summary>
    Process StartGame(GameStartRequest startRequest, ICatInternationalDalamudSession? dalamud);

    /// <summary>游戏进程里是否已有 Dalamud</summary>
    bool IsDalamudLoaded(Process process);

    /// <summary>Minion</summary>
    ICatInternationalMinion Minion { get; }
}

/// <summary>
///     真实环境: 设置取自启动器设置（与界面版同一份）, 登录走 <see cref="InternationalLauncher" />,
///     Dalamud 走 goatcorp 原版那套并放在 <see cref="InternationalRoot" /> 之下, 与国服 Dalamud 的目录完全分开。
/// </summary>
internal sealed class CatInternationalRealEnvironment(Func<Task> ensureInitialized) : ICatInternationalEnvironment
{
    private readonly object                       dalamudLock = new();
    private          InternationalDalamudService? dalamudService;

    /// <summary>
    ///     国际服专用的根目录: %APPDATA%\XIVLauncherCN\international（跟着 --roamingPath 走）。
    ///     其下是国际服 Dalamud 的本体、运行时、资源、配置、插件、日志, 以及登录页地址模板的本地缓存; 国服的东西一样都不放这里
    /// </summary>
    public static DirectoryInfo InternationalRoot => new(Path.Combine(Paths.RoamingPath, "international"));

    public Task EnsureInitializedAsync() => ensureInitialized();

    public CatInternationalSettings ReadSettings()
    {
        var settings = App.Settings;

        return new CatInternationalSettings
        (
            settings.InternationalGamePath?.FullName,
            settings.InternationalLanguage,
            settings.DPIAwareness,
            settings.DalamudLoadMethod,
            (int)settings.DalamudInjectionDelayMS
        );
    }

    public Task<InternationalClientConfig> GetClientConfigAsync(CancellationToken cancellationToken) =>
        new InternationalClientConfigProvider(new FileInfo(Path.Combine(InternationalRoot.FullName, "launcherClientConfig.json"))).GetAsync(cancellationToken);

    public ICatInternationalLoginClient CreateLoginClient(InternationalClientConfig config, Action<string> onSecret) =>
        new LoginClient
        (
            new InternationalLauncher
            (
                config.FrontierUrl,
                InternationalLauncher.GenerateAcceptLanguage(),
                new InternationalLauncherOptions { OnSecret = onSecret }
            )
        );

    public (ICatInternationalDalamudSession? Session, string? Error) PrepareDalamud(DirectoryInfo gamePath, CatInternationalSettings settings)
    {
        try
        {
            new DalamudCompatibilityCheck().EnsureCompatibility();
        }
        catch (IDalamudCompatibilityCheck.NoRedistsException ex)
        {
            Log.Error(ex, "[CatHost] 未找到 Dalamud 所需的 Redists");
            return (null, "Dalamud 需要安装 Microsoft Visual C++ 2015-2019 Redistributable");
        }
        catch (IDalamudCompatibilityCheck.ArchitectureNotSupportedException ex)
        {
            Log.Error(ex, "[CatHost] 不受支持的本地环境架构");
            return (null, "Dalamud 仅支持 64 位 Windows");
        }

        try
        {
            var service = GetDalamudService();
            var session = service.CreateSession
            (
                gamePath,
                new DalamudLaunchOptions(settings.DalamudLoadMethod, settings.DalamudInjectionDelayMs, false, false, false),
                settings.Language,
                InternationalDalamudService.BuildTroubleshootingJson
                (
                    gamePath,
                    settings.DalamudLoadMethod,
                    settings.DalamudInjectionDelayMs,
                    true,
                    (int)settings.DpiAwareness,
                    AppUtil.GetAssemblyVersion() ?? string.Empty,
                    AppUtil.GetGitHash()         ?? string.Empty
                )
            );

            service.Updater.Run();

            if (session.EnsureReady(gamePath) == DalamudSession.DalamudInstallState.Ok)
                return (new DalamudSessionAdapter(session, service.Updater), null);

            return
            (
                null,
                $"国际服的 Dalamud 还没有适配现在的游戏版本（游戏是 {Repository.Ffxiv.GetVer(gamePath)}, Dalamud 只支持 {service.Updater.ResolvedBranch?.SupportedGameVer ?? "未知"}）, " +
                "游戏更新后通常要等几天; 可以先不注入 Dalamud 上号"
            );
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[CatHost] 准备国际服 Dalamud 时发生错误");
            return (null, $"下载国际服 Dalamud 相关文件异常: {ex.Message}");
        }
    }

    /// <summary>
    ///     出处: goatcorp XIVLauncher.Common.Windows/WindowsGameRunner.cs:22-40（有 Dalamud 加 __COMPAT_LAYER 后交给注入器, 否则挂起创建 → 关 SeDebug → 恢复）;
    ///     WindowsDalamudRunner.cs:54-55（DALAMUD_RUNTIME / DOTNET_ROOT）; DalamudLauncher.cs:123-125（DALAMUD_BRANCH）。
    ///     不调国服的 GameArgumentInterop.Fixer（那是改写国服游戏里盛趣登录函数用的）。
    /// </summary>
    public Process StartGame(GameStartRequest startRequest, ICatInternationalDalamudSession? dalamud)
    {
        if (dalamud is DalamudSessionAdapter adapter)
        {
            var compat = "RunAsInvoker ";
            compat += startRequest.DpiAwareness switch
            {
                DPIAwareness.Aware   => "HighDPIAware",
                DPIAwareness.Unaware => "DPIUnaware",
                _                    => throw new ArgumentOutOfRangeException()
            };

            startRequest.Environment["__COMPAT_LAYER"]  = compat;
            startRequest.Environment["DALAMUD_RUNTIME"] = adapter.Updater.Runtime.FullName;
            startRequest.Environment["DOTNET_ROOT"]     = adapter.Updater.Runtime.FullName;

            if (!string.IsNullOrWhiteSpace(adapter.Updater.ResolvedBranch?.Track))
                startRequest.Environment["DALAMUD_BRANCH"] = adapter.Updater.ResolvedBranch.Track;

            return adapter.Session.LaunchGame(new FileInfo(startRequest.ExePath), startRequest.Arguments, startRequest.Environment);
        }

        return NativeAclFix.LaunchGame
        (
            startRequest.WorkingDirectory,
            startRequest.ExePath,
            startRequest.Arguments,
            startRequest.Environment,
            startRequest.DpiAwareness,
            _ => { }
        );
    }

    public bool IsDalamudLoaded(Process process)
    {
        try
        {
            return !process.HasExited && FFXIVProcess.IsDalamudInjected(process);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[CatHost] 检查 Dalamud 是否注入失败");
            return false;
        }
    }

    public ICatInternationalMinion Minion { get; } = new CatInternationalMinion();

    private InternationalDalamudService GetDalamudService()
    {
        lock (dalamudLock)
        {
            return dalamudService ??= new InternationalDalamudService
            (
                InternationalDalamudService.CreateHostPaths(InternationalRoot, new DirectoryInfo(AppContext.BaseDirectory)),
                new AppDalamudGameVersionProvider(),
                new InternationalDalamudUpdaterOptions { ProgressSink = new CatDalamudProgressSink() }
            );
        }
    }

    private sealed class LoginClient(InternationalLauncher launcher) : ICatInternationalLoginClient
    {
        public Task<bool> IsBootUpdateRequiredAsync(DirectoryInfo gamePath, CancellationToken cancellationToken) =>
            launcher.IsBootUpdateRequiredAsync(gamePath, cancellationToken);

        public Task<InternationalGateStatus> GetLoginStatusAsync(CancellationToken cancellationToken) =>
            launcher.GetLoginStatusAsync(cancellationToken);

        public Task<InternationalGateStatus> GetGateStatusAsync(ClientLanguage language, CancellationToken cancellationToken) =>
            launcher.GetGateStatusAsync(language, cancellationToken);

        public Task<InternationalLoginResult> LoginAsync(string userName, string password, DirectoryInfo gamePath, ClientLanguage language, CancellationToken cancellationToken) =>
            launcher.LoginAsync(userName, password, gamePath, language, cancellationToken);

        public void Dispose() =>
            launcher.Dispose();
    }

    private sealed class DalamudSessionAdapter(DalamudSession session, InternationalDalamudUpdater updater) : ICatInternationalDalamudSession
    {
        public DalamudSession Session { get; } = session;

        public InternationalDalamudUpdater Updater { get; } = updater;

        public void InjectGame(int gamePid) =>
            Session.InjectGame(gamePid);
    }
}

/// <summary>
///     国际服挂 Minion: 选行、预占、占用判断、停机补报与国服（CatRealGameRunner）是同一套做法和同一把跨进程锁,
///     区别只有按国际服行挂载（注入文件与 -path 见 <see cref="MinionAttacher" />）。
///     这里是照国服那几段另写的一份, 没有去改国服启动器让两边共用 —— 国服路径正在生产使用, 保持它一行不动。
/// </summary>
internal sealed class CatInternationalMinion : ICatInternationalMinion
{
    /// <summary>与国服启动器用同一把锁（CatRealGameRunner.MINION_SELECT_MUTEX_NAME）: 国服和国际服的号可能同时抢同一张卡</summary>
    private const string MINION_SELECT_MUTEX_NAME = @"Local\DcMiniLauncher-MinionSelect";

    private static readonly TimeSpan MinionSelectMutexTimeout = TimeSpan.FromSeconds(30);

    public MinionAccount? SelectRow(CatLaunchRequest request, ICatLaunchReporter reporter, int? selfPid, out (string Code, string Message) error)
    {
        error = default;

        if (!request.Minion)
        {
            error = (CatCodes.MINION_NOT_CONFIGURED, "launch 时没有指定 Minion 卡");
            return null;
        }

        var installPath = MinionAccounts.InstallPath;

        if (!MinionAccounts.IsLauncherPresent(installPath))
        {
            error = (CatCodes.MINION_NOT_CONFIGURED, $"找不到 {MinionAccounts.GetLauncherExePath(installPath)}, 请在 DcMiniLauncher「设置 → Minion」里指定安装目录");
            return null;
        }

        if (string.IsNullOrWhiteSpace(App.Settings.MinionId) || string.IsNullOrWhiteSpace(App.Settings.MinionPassword))
        {
            error = (CatCodes.MINION_NOT_CONFIGURED, "DcMiniLauncher「设置 → Minion」里的 Minion 账号或密码没填");
            return null;
        }

        IReadOnlyList<MinionAccount> rows;

        try
        {
            rows = MinionAccounts.LoadAccounts(installPath);
        }
        catch (Exception ex)
        {
            error = (CatCodes.MINION_NOT_CONFIGURED, $"读取 Minion 账号文件失败: {ex.Message}");
            return null;
        }

        var occupied = MinionOccupancy.ReadAllLive()
                                      .Where(x => x.Pid != selfPid)
                                      .Select(x => x.MinionUid)
                                      .Where(uid => !string.IsNullOrWhiteSpace(uid))
                                      .Select(uid => uid!.Trim())
                                      .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var selection = MinionCards.SelectRow(rows, request.CardFingerprint!, MinionCards.VARIANT_GLOBAL, MinionCards.ConfiguredCnGameRoots(), occupied);
        var row       = selection.Row;

        Log.Information("[CatHost] 按卡 {Fingerprint} 选国际服行: {Notes}", request.CardFingerprint, string.Join(" | ", selection.Notes));

        if (selection.AllOccupied)
            reporter.Log("warning", $"卡 {request.CardFingerprint} 的国际服行都已挂在别的游戏上, 仍挂第一行, 可能把那边的 Minion 顶掉");

        if (row == null)
            error = (CatCodes.MINION_CARD_NOT_FOUND, $"本机 Minion Accounts.json 里找不到卡 {request.CardFingerprint} 的国际服注入行（按该行的游戏执行程序是否位于国服游戏目录之下区分）");

        return row;
    }

    public async Task<(MinionAccount? Row, (string Code, string Message) Error, bool Reserved)> ReserveRowAsync(CatLaunchRequest request, Process process, ICatLaunchReporter reporter)
    {
        using var gate = await CrossProcessMutex.TryAcquireAsync(MINION_SELECT_MUTEX_NAME, MinionSelectMutexTimeout).ConfigureAwait(false);

        var row = SelectRow(request, reporter, process.Id, out var error);

        if (row == null)
            return (null, error, false);

        var startedAt = CatInternationalGameRunner.SafeProcessStartedAt(process);

        // 这个游戏已有自己的记录（force 重挂）时不再写预占
        if (MinionOccupancy.Read(process.Id) is { } existing && existing.ProcessStartedAt == startedAt)
            return (row, default, false);

        var reserved = !string.IsNullOrWhiteSpace(row.Keycode) &&
                       MinionOccupancy.Write
                       (
                           new MinionOccupancyRecord
                           {
                               Pid              = process.Id,
                               ProcessStartedAt = startedAt,
                               CardFingerprint  = MinionCards.Fingerprint(row.Keycode),
                               Variant          = MinionCards.VARIANT_GLOBAL,
                               AccountName      = request.AccountName,
                               MinionUid        = string.IsNullOrWhiteSpace(row.Uid) ? null : row.Uid.Trim(),
                               AttachedAt       = DateTimeOffset.UtcNow
                           }
                       );

        return (row, default, reserved);
    }

    public IEnumerable<string?> SecretsOf(MinionAccount row) =>
        [row.Keycode, App.Settings.MinionPassword];

    public Task<MinionAttachResult> AttachAsync(MinionAccount row, Process process, DirectoryInfo gamePath, bool dalamudInjected, string accountName, CancellationToken cancellationToken) =>
        MinionAttacher.AttachAsync(row, process, gamePath, dalamudInjected, accountName, cancellationToken, MinionCards.VARIANT_GLOBAL);

    public void ReleaseReservation(int gamePid) =>
        MinionOccupancy.Delete(gamePid);

    public bool IsAttached(Process process)
    {
        try
        {
            return MinionOccupancy.Read(process.Id) is { } record &&
                   record.ProcessStartedAt == MinionOccupancy.GetProcessStartedAt(process);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[CatHost] 读取 Minion 占用记录失败");
            return false;
        }
    }

    public void ReportStopped(int gamePid) =>
        MinionAppStatusReporter.ReportStopped(gamePid);
}
