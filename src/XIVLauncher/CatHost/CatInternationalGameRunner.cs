using System.Diagnostics;
using System.IO;
using Serilog;
using XIVLauncher.Common;
using XIVLauncher.Common.Game;
using XIVLauncher.Common.Game.International;
using XIVLauncher.Minion;

namespace XIVLauncher.CatHost;

/// <summary>
///     国际服（Square Enix 账号, Windows 版, 账号密码, 不带一次性密码）的无界面启动:
///     检查目录与版本 → 登录 → 准备 Dalamud → 启动游戏 → 等 Dalamud 落地 → 挂 Minion → 守到游戏退出。
///     <para>
///         与国服启动器 <see cref="CatRealGameRunner" /> 是两个互不相干的类: 不用账号库、设备信息、大区、跨区服务, 不打补丁（有更新只报
///         gameUpdateRequired）。对外部世界的依赖全在 <see cref="ICatInternationalEnvironment" /> 里。
///     </para>
///     <para>
///         <b>崩溃不自动重启。</b>国服的自动重启靠国服 Dalamud 的约定: 注入器带 <c>--managed-restart</c>, 崩溃处理器把「要不要重启」编码进退出码
///         （RestartMonitor 里的 0x12345670–0x12345674）。goatcorp 原版 Dalamud 没有这个参数, 也没有这套退出码, 所以这里完全不接 RestartMonitor:
///         不找崩溃处理器进程、不看它的退出码、不判断它有没有弹窗 —— 国服那套判定不会在国际服上误触发重启, 也不会因为等崩溃处理器而卡住。
///         游戏进程退出就发 game.exited（不带 reason）; 不发 game.crashed / game.restarted, launch 里的 crashDialogTimeoutSeconds 对国际服不起作用。
///         如果原版崩溃处理器弹出对话框并让游戏进程停着不退, 这里会一直显示运行中, 直到有人处理对话框或外壳发 close（close 会结束游戏进程）。
///     </para>
/// </summary>
public sealed class CatInternationalGameRunner(CatLogRedactor redactor, ICatInternationalEnvironment environment) : ICatGameRunner
{
    private readonly CancellationTokenSource closeCts  = new();
    private readonly object                  closeLock = new();

    private CatLaunchRequest          request  = null!;
    private CatInternationalSettings  settings = null!;
    private DirectoryInfo             gamePath = null!;
    private string                    userName = null!;
    private Process?                  currentProcess;
    private bool                      closeRequested;
    private TimeSpan                  closeTimeout = TimeSpan.FromSeconds(CatProtocol.DEFAULT_CLOSE_TIMEOUT_SECONDS);
    private int                       cleaned;

    /// <summary>等 Dalamud.dll 出现在游戏进程里的上限</summary>
    public TimeSpan DalamudInjectTimeout { get; init; } = TimeSpan.FromMinutes(1);

    private bool IsCloseRequested
    {
        get
        {
            lock (closeLock)
                return closeRequested;
        }
    }

    /// <inheritdoc />
    public async Task<int> RunAsync(CatLaunchRequest launchRequest, ICatLaunchReporter reporter, CancellationToken cancellationToken)
    {
        request = launchRequest;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, closeCts.Token);
        ICatInternationalLoginClient? loginClient = null;

        try
        {
            loginClient = await PrepareAsync(reporter, linked.Token).ConfigureAwait(false);

            // 先登录再准备 Dalamud: 密码不对、号不能玩、游戏要更新这些都能立刻报出来, 不用等 Dalamud 下载完（首次下载要几分钟）。
            // 只登录这一次, Dalamud 准备完不再重新登录。goatcorp 也是这个顺序: 登录后才等 Dalamud 更新器
            //（MainWindowViewModel.cs:446 登录 → :1072-1117 StartGameAndAddon 里 HoldForUpdate）, 它还允许把登录得到的会话值缓存一天（CommonUniqueIdCache.cs）。
            var login = await LoginAsync(loginClient, linked.Token).ConfigureAwait(false);

            loginClient.Dispose();
            loginClient = null;

            var dalamud = await PrepareDalamudAsync(reporter, linked.Token).ConfigureAwait(false);

            var process = await StartAsync(login, dalamud, reporter, linked.Token).ConfigureAwait(false);

            // 国际服不做崩溃重启（见类注释）: 只等游戏进程自己退出
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            Cleanup(process);
            reporter.Exited(process.Id, TryGetExitCode(process), IsCloseRequested ? CatExitReasons.CLOSED : null);

            return CatHostRuntime.EXIT_OK;
        }
        catch (Exception ex) when (currentProcess == null && IsCloseRequested)
        {
            // 还没起游戏就收到了关闭请求: 不管这一刻手头的步骤是被取消、恰好网络出错、还是已经判成了别的失败, 都按取消报
            if (ex is not OperationCanceledException)
                LogException("启动途中收到关闭请求, 当时的步骤以这个错误结束", ex);

            reporter.Failed(CatCodes.CANCELLED, "启动途中收到关闭请求, 没有起游戏");
            return CatLaunchHost.EXIT_LAUNCH_FAILED;
        }
        catch (CatLaunchException ex) when (currentProcess == null)
        {
            reporter.Failed(ex.Code, ex.Message);
            return CatLaunchHost.EXIT_LAUNCH_FAILED;
        }
        catch (Exception ex) when (currentProcess != null)
        {
            return await GuardAfterErrorAsync(ex, reporter).ConfigureAwait(false);
        }
        finally
        {
            loginClient?.Dispose();
        }
    }

    /// <inheritdoc />
    public async Task CloseAsync(TimeSpan gracefulTimeout)
    {
        Process? process;

        lock (closeLock)
        {
            if (closeRequested)
                return;

            closeRequested = true;
            closeTimeout   = gracefulTimeout;
            process        = currentProcess;
        }

        Log.Information("[CatHost] 收到关闭请求（国际服）");

        // 取消启动途中的步骤
        await closeCts.CancelAsync().ConfigureAwait(false);

        // 游戏还没起来时由启动流程自己收尾; 正在创建进程时由创建方看到关闭请求后关
        if (process != null)
            await CatGameCloser.CloseAsync(process, gracefulTimeout).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task InjectAsync(bool dalamud, bool minion, bool force, ICatLaunchReporter reporter, CancellationToken cancellationToken)
    {
        var process = currentProcess;

        if (process == null || process.HasExited)
        {
            if (dalamud)
                reporter.Agent(CatAgentKinds.DALAMUD, false, CatCodes.NOT_RUNNING, "游戏进程已退出");
            if (minion)
                reporter.Agent(CatAgentKinds.MINION, false, CatCodes.NOT_RUNNING, "游戏进程已退出");
            return;
        }

        try
        {
            if (dalamud)
                await InjectDalamudIntoRunningAsync(process, reporter, cancellationToken).ConfigureAwait(false);

            if (minion)
            {
                if (!force && environment.Minion.IsAttached(process))
                    reporter.Agent(CatAgentKinds.MINION, true, CatCodes.ALREADY_ATTACHED, "这个游戏已经挂着 Minion, 没有重复挂; 要重新挂请带 force");
                else
                    await AttachMinionAsync(process, environment.IsDalamudLoaded(process), reporter, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (!process.HasExited)
                reporter.Stage(CatStages.RUNNING);
        }
    }

    #region 准备

    /// <summary>
    ///     不用登录就能查的都先查: 设置、账号名、游戏目录、Minion 行、杀开关、boot 版本、登录服务是否开放。返回后面登录要用的客户端。
    /// </summary>
    private async Task<ICatInternationalLoginClient> PrepareAsync(ICatLaunchReporter reporter, CancellationToken cancellationToken)
    {
        reporter.Stage(CatStages.PREPARING);

        try
        {
            await environment.EnsureInitializedAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[CatHost] 初始化设置失败");
            throw new CatLaunchException(CatCodes.LAUNCH_FAILED, $"DcMiniLauncher 初始化失败: {ex.Message}");
        }

        if (request.Password == null || request.Password.Reveal().Length == 0)
            throw new CatLaunchException(CatCodes.AUTHORIZATION_REQUIRED, "上号请求里没有这个国际服账号的密码");

        // 密码在接受 launch 时已登记脱敏; 这里再登记一次, 不依赖调用方
        redactor.RegisterSecret(request.Password.Reveal());

        // 出处: goatcorp MainWindowViewModel.cs:243-250（要的是 SE 账号名不是邮箱）, :263（去掉空格）
        userName = InternationalLauncher.NormalizeUserName(request.AccountName);

        if (userName.Contains('@'))
            throw new CatLaunchException(CatCodes.AUTHORIZATION_REQUIRED, "国际服要填 Square Enix 账号名, 不能填邮箱, 请核对任务资料里的账号");

        settings = environment.ReadSettings();

        var pathState = InternationalGamePath.Check(settings.GamePath);

        if (pathState != InternationalGamePathState.Ok)
        {
            throw new CatLaunchException
            (
                CatCodes.INVALID_GAME_PATH,
                $"{InternationalGamePath.Describe(pathState)}, 请在这台电脑的 DcMiniLauncher 界面版「设置」里选择国际服游戏目录"
            );
        }

        gamePath = new DirectoryInfo(settings.GamePath!);

        if (request.Minion)
        {
            // 起游戏前先确认这张卡在本机有对应的国际服行; 真正选哪一行在挂载前加锁再选并预占
            _ = environment.Minion.SelectRow(request, reporter, null, out var minionError) ?? throw new CatLaunchException(minionError.Code, minionError.Message);
        }

        var config = await environment.GetClientConfigAsync(cancellationToken).ConfigureAwait(false);

        if (config.Source != InternationalClientConfigSource.Remote)
        {
            reporter.Log
            (
                "warning",
                config.Source == InternationalClientConfigSource.Cache
                    ? "取不到国际服登录页的最新地址, 这次用的是本机上次存下的"
                    : "取不到国际服登录页的最新地址, 本机也没有存过, 这次用的是程序里自带的（可能已经过期; 这次登录要是失败, 请先检查这台电脑的网络）"
            );
        }

        string bootVersion;

        try
        {
            bootVersion = Repository.Boot.GetVer(gamePath);
        }
        catch (Exception ex)
        {
            throw ToLaunchException("读取国际服客户端版本", ex);
        }

        // 杀开关: SE 改了登录流程时 goatcorp 会远程停用它自己的启动器（MainWindowViewModel.cs:206-222）, 这里照同一个开关停
        if (config.IsBootVersionCutOff(bootVersion))
        {
            throw new CatLaunchException
            (
                CatCodes.LAUNCH_FAILED,
                "国际服最近更新后登录方式有变动, 这个版本的 DcMiniLauncher 暂时不能自动上国际服的号, 请先手动上号"
            );
        }

        var loginClient = environment.CreateLoginClient(config, redactor.RegisterSecret);

        try
        {
            bool bootUpdateRequired;

            try
            {
                bootUpdateRequired = await loginClient.IsBootUpdateRequiredAsync(gamePath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw ToLaunchException("检查国际服客户端版本", ex);
            }

            if (bootUpdateRequired)
                throw new CatLaunchException(CatCodes.GAME_UPDATE_REQUIRED, $"国际服客户端有更新, {CatInternationalLoginFailures.UPDATE_HINT}");

            // 登录服务关着就不去登录（goatcorp MainWindowViewModel.cs:384-426: 查不到或为 false 都不登录）
            InternationalGateStatus loginStatus;

            try
            {
                loginStatus = await loginClient.GetLoginStatusAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                LogException("查询国际服登录服务状态失败", ex);
                throw new CatLaunchException(CatCodes.NETWORK_ERROR, "连不上国际服的服务器（可能正在维护, 也可能是这台电脑的网络问题）, 稍后重试即可");
            }

            if (!loginStatus.Status)
                throw new CatLaunchException(CatCodes.LAUNCH_FAILED, "国际服正在维护, 登录服务暂时关闭, 等维护结束再上号");

            return loginClient;
        }
        catch
        {
            loginClient.Dispose();
            throw;
        }
    }

    /// <summary>
    ///     下载 / 校验国际服 Dalamud。在登录之后做（理由见 <see cref="RunAsync" />）
    /// </summary>
    private async Task<ICatInternationalDalamudSession?> PrepareDalamudAsync(ICatLaunchReporter reporter, CancellationToken cancellationToken)
    {
        if (!request.Dalamud)
            return null;

        reporter.Stage(CatStages.UPDATING_DALAMUD);

        var (session, error) = await Task.Run(() => environment.PrepareDalamud(gamePath, settings), cancellationToken).ConfigureAwait(false);

        // 首次启动时 Dalamud 不可用就不起游戏（与国服一致）
        return session ?? throw new CatLaunchException(CatCodes.DALAMUD_UNAVAILABLE, error ?? "国际服 Dalamud 不可用");
    }

    private async Task<InternationalLoginResult> LoginAsync(ICatInternationalLoginClient loginClient, CancellationToken cancellationToken)
    {
        InternationalLoginResult login;

        try
        {
            login = await loginClient.LoginAsync(userName, request.Password!.Reveal(), gamePath, settings.Language, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw ToLaunchException("国际服登录", ex);
        }

        if (CatInternationalLoginFailures.FromState(login.State) is { } failure)
        {
            Log.Warning("[CatHost] 国际服登录结果: {State}", login.State);
            throw new CatLaunchException(failure.Code, failure.Message);
        }

        if (string.IsNullOrEmpty(login.UniqueId))
            throw new CatLaunchException(CatCodes.LAUNCH_FAILED, "国际服的版本服务器返回异常（可能正在维护）, 请稍后再试");

        redactor.RegisterSecret(login.UniqueId);

        // 游戏在维护就不启动（goatcorp MainWindowViewModel.cs:675, 318-376: 查不到或为 false 都不启动）
        InternationalGateStatus gate;

        try
        {
            gate = await loginClient.GetGateStatusAsync(settings.Language, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            LogException("查询国际服维护状态失败", ex);
            throw new CatLaunchException(CatCodes.NETWORK_ERROR, "连不上国际服的服务器（可能正在维护, 也可能是这台电脑的网络问题）, 稍后重试即可");
        }

        if (!gate.Status)
        {
            var detail = string.Join(" ", gate.Message?.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()) ?? []);

            if (detail.Length > 100)
                detail = detail[..100] + "…";

            throw new CatLaunchException(CatCodes.LAUNCH_FAILED, detail.Length == 0 ? "国际服正在维护, 等维护结束再上号" : $"国际服正在维护, 等维护结束再上号: {redactor.Redact(detail)}");
        }

        return login;
    }

    #endregion

    #region 启动与收尾

    private async Task<Process> StartAsync
    (
        InternationalLoginResult         login,
        ICatInternationalDalamudSession? dalamud,
        ICatLaunchReporter               reporter,
        CancellationToken                startToken
    )
    {
        startToken.ThrowIfCancellationRequested();
        reporter.Stage(CatStages.STARTING);

        var exePath = InternationalLauncher.GetGameExePath(gamePath);

        if (!File.Exists(exePath))
            throw new CatLaunchException(CatCodes.INVALID_GAME_PATH, "找不到国际服的游戏可执行文件, 可能需要用官方启动器修复或重装");

        string arguments;

        try
        {
            // 参数一律加密（goatcorp 界面版的缺省值, MainWindow.xaml.cs:199）: 里面有登录得到的会话值, 明文会出现在进程命令行和详细日志里。
            // 不带启动器设置里的附加启动参数: 那是给国服客户端配的
            arguments = InternationalLauncher.BuildGameArguments
                                             (
                                                 login.UniqueId!,
                                                 login.Region,
                                                 login.MaxExpansion,
                                                 settings.Language,
                                                 Repository.Ffxiv.GetVer(gamePath)
                                             )
                                             .BuildEncrypted();
        }
        catch (Exception ex)
        {
            throw ToLaunchException("准备国际服启动参数", ex);
        }

        var startRequest = new GameStartRequest(exePath, Path.Combine(gamePath.FullName, "game"), arguments, new Dictionary<string, string>(), settings.DpiAwareness);

        Process process;

        try
        {
            // 进程创建本身不可取消; 创建期间收到 close 时, 创建完由这里负责关掉
            process = await Task.Run(() => environment.StartGame(startRequest, dalamud), CancellationToken.None).ConfigureAwait(false)
                      ?? throw new InvalidOperationException("游戏进程为空");
        }
        catch (Exception ex)
        {
            LogException("启动国际服游戏进程失败", ex);
            throw new CatLaunchException(CatCodes.LAUNCH_FAILED, "启动游戏进程失败, 详细原因在这台电脑的 DcMiniLauncher 日志里");
        }

        bool closeNow;

        // 先记下进程再做别的: 之后任何异常都按「游戏已起来」处理, 不会把活着的游戏报成启动失败
        lock (closeLock)
        {
            currentProcess = process;
            closeNow       = closeRequested;
        }

        reporter.Started(process.Id, SafeProcessStartedAt(process));
        Log.Information("[CatHost] 国际服游戏进程已创建 (PID={ProcessID}, Dalamud={Dalamud}, Minion={Minion})", process.Id, dalamud != null, request.Minion);

        if (closeNow)
        {
            _ = CatGameCloser.CloseAsync(process, closeTimeout);
            return process;
        }

        try
        {
            if (dalamud != null)
            {
                reporter.Stage(CatStages.INJECTING);
                var loaded = await WaitForDalamudAsync(process, startToken).ConfigureAwait(false);
                reporter.Agent(CatAgentKinds.DALAMUD, loaded, loaded ? null : CatCodes.DALAMUD_UNAVAILABLE, loaded ? null : "游戏进程里没有等到 Dalamud 加载");
            }

            if (request.Minion)
                await AttachMinionAsync(process, dalamud != null, reporter, startToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (IsCloseRequested)
        {
            Log.Information("[CatHost] 收到关闭请求, 不再等注入");
        }

        if (!process.HasExited && !IsCloseRequested)
            reporter.Stage(CatStages.RUNNING);

        return process;
    }

    /// <summary>
    ///     游戏起来后出了意外异常: 照样等游戏结束、补报 Minion 停机, 再发 game.exited。期间 close 仍可用。
    /// </summary>
    private async Task<int> GuardAfterErrorAsync(Exception exception, ICatLaunchReporter reporter)
    {
        const string DETAIL = "游戏起来后启动器内部出错, 详细原因在这台电脑的 DcMiniLauncher 日志里";

        var detail  = DETAIL;
        var process = currentProcess!;

        LogException("守护国际服游戏时出错", exception);
        reporter.Log("error", $"{detail}; 游戏不受影响, 等它结束后才会报告已退出");

        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[CatHost] 等游戏进程退出失败");
        }

        Cleanup(process);

        var closed = IsCloseRequested;
        reporter.Exited(process.Id, TryGetExitCode(process), closed ? CatExitReasons.CLOSED : CatExitReasons.GUARD_ERROR, null, closed ? null : detail);
        return CatLaunchHost.EXIT_GUARD_ERROR;
    }

    private void Cleanup(Process process)
    {
        if (Interlocked.Exchange(ref cleaned, 1) == 1)
            return;

        Log.Information("[CatHost] 国际服游戏进程已退出 (PID={ProcessID}, ExitCode=0x{ExitCode:X8})", process.Id, (uint)(TryGetExitCode(process) ?? 0));

        try
        {
            // 告诉 MINIONAPP 这一行停机了, 否则它会过一分钟自己拉个新客户端
            environment.Minion.ReportStopped(process.Id);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[CatHost] 补报 Minion 停机失败");
        }
    }

    #endregion

    #region Dalamud 与 Minion

    private async Task InjectDalamudIntoRunningAsync(Process process, ICatLaunchReporter reporter, CancellationToken cancellationToken)
    {
        if (environment.IsDalamudLoaded(process))
        {
            reporter.Agent(CatAgentKinds.DALAMUD, true, null, "Dalamud 已在游戏里");
            return;
        }

        reporter.Stage(CatStages.UPDATING_DALAMUD);

        var (session, error) = await Task.Run(() => environment.PrepareDalamud(gamePath, settings), cancellationToken).ConfigureAwait(false);

        if (session == null)
        {
            reporter.Agent(CatAgentKinds.DALAMUD, false, CatCodes.DALAMUD_UNAVAILABLE, error);
            return;
        }

        reporter.Stage(CatStages.INJECTING);

        try
        {
            await Task.Run(() => session.InjectGame(process.Id), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[CatHost] 补注入国际服 Dalamud 失败");
            reporter.Agent(CatAgentKinds.DALAMUD, false, CatCodes.DALAMUD_UNAVAILABLE, $"补注入 Dalamud 失败: {ex.Message}");
            return;
        }

        var loaded = await WaitForDalamudAsync(process, cancellationToken).ConfigureAwait(false);
        reporter.Agent(CatAgentKinds.DALAMUD, loaded, loaded ? null : CatCodes.DALAMUD_UNAVAILABLE, loaded ? null : "游戏进程里没有等到 Dalamud 加载");
    }

    private async Task AttachMinionAsync(Process process, bool dalamudInjected, ICatLaunchReporter reporter, CancellationToken cancellationToken)
    {
        reporter.Stage(CatStages.ATTACHING_MINION);

        var minion = environment.Minion;
        var (minionRow, error, reserved) = await minion.ReserveRowAsync(request, process, reporter).ConfigureAwait(false);

        if (minionRow == null)
        {
            reporter.Agent(CatAgentKinds.MINION, false, error.Code, error.Message);
            return;
        }

        foreach (var secret in minion.SecretsOf(minionRow))
            redactor.Register(secret);

        var ok = false;

        try
        {
            var result = await minion.AttachAsync(minionRow, process, gamePath, dalamudInjected, request.AccountName, cancellationToken).ConfigureAwait(false);
            ok = result.Ok;

            if (IsCloseRequested)
                Log.Information("[CatHost] 收到关闭请求, Minion 挂载结果不再上报: {Ok}", result.Ok);
            else if (result.Ok)
                reporter.Agent(CatAgentKinds.MINION, true);
            else
            {
                Log.Error("[CatHost] Minion 挂载失败: {Error}", result.Error);
                reporter.Agent(CatAgentKinds.MINION, false, CatCodes.ATTACH_FAILED, result.Error);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[CatHost] 挂载 Minion 时发生未处理异常");
            reporter.Agent(CatAgentKinds.MINION, false, CatCodes.ATTACH_FAILED, $"挂载 Minion 时出错: {ex.Message}");
        }
        finally
        {
            // 没挂上就撤掉预占, 别让这一行一直显示被占用
            if (reserved && !ok)
                minion.ReleaseReservation(process.Id);
        }
    }

    private async Task<bool> WaitForDalamudAsync(Process process, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        while (stopwatch.Elapsed < DalamudInjectTimeout)
        {
            if (process.HasExited)
                return false;

            if (environment.IsDalamudLoaded(process))
                return true;

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
        }

        return environment.IsDalamudLoaded(process);
    }

    #endregion

    #region 公共

    /// <summary>
    ///     把登录、版本检查中的异常换成带失败码的启动失败; 本地日志只记脱敏后的文字, 不直接记异常对象
    /// </summary>
    private CatLaunchException ToLaunchException(string what, Exception exception)
    {
        LogException($"{what}失败", exception);

        var (code, message) = CatInternationalLoginFailures.FromException(exception, redactor.Redact);
        return new CatLaunchException(code, message);
    }

    private void LogException(string what, Exception exception) =>
        Log.Error("[CatHost] {What}: {Detail}", what, redactor.Redact(exception.ToString()));

    /// <summary>
    ///     进程创建时间; 读不到时用当前时间近似并记日志
    /// </summary>
    internal static DateTimeOffset SafeProcessStartedAt(Process process)
    {
        try
        {
            return MinionOccupancy.GetProcessStartedAt(process);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[CatHost] 读取游戏进程创建时间失败, 用当前时间代替");
            return DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }
    }

    private static int? TryGetExitCode(Process process)
    {
        try
        {
            return process.HasExited ? process.ExitCode : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    #endregion
}
