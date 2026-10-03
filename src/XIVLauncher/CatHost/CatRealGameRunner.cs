using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using Serilog;
using XIVLauncher.Account;
using XIVLauncher.Account.Cred;
using XIVLauncher.Account.DeviceProfiles;
using XIVLauncher.Common;
using XIVLauncher.Common.Game;
using XIVLauncher.Common.Game.Exceptions;
using XIVLauncher.CompanionApp;
using XIVLauncher.Dalamud;
using XIVLauncher.GamePatchV3.Update;
using XIVLauncher.GamePatchV3.Update.Models;
using XIVLauncher.InGame;
using XIVLauncher.Login.Channels;
using XIVLauncher.Login.Client;
using XIVLauncher.Login.Models;
using XIVLauncher.Minion;
using XIVLauncher.Support;
using XIVLauncher.Windows.ViewModel.Main.Services;

namespace XIVLauncher.CatHost;

/// <summary>
///     启动失败, 带协议失败码
/// </summary>
public sealed class CatLaunchException(string code, string message) : Exception(message)
{
    /// <summary>协议失败码</summary>
    public string Code { get; } = code;
}

/// <summary>
///     无界面启动核心: 与界面路径（GameLaunchFlow.StartGameAndCompanionApp）同样的步骤, 但不读界面状态、不弹任何框,
///     所有原本弹框的地方改为发事件 / 返回失败码。本进程只服务一个账号, 跨区会话也只属于这一个号。
/// </summary>
public sealed class CatRealGameRunner(CatLogRedactor redactor, Func<Task> ensureInitialized) : ICatGameRunner
{
    /// <summary>等 Dalamud.dll 出现在游戏进程里的上限</summary>
    private static readonly TimeSpan DalamudInjectTimeout = TimeSpan.FromMinutes(1);

    private readonly Launcher            launcher            = new();
    private readonly CompanionAppService companionAppService = new();
    private readonly LoginClient         loginClient         = new();
    private readonly SemaphoreSlim       quickKeyLock        = new(1, 1);
    private readonly object              cleanupLock         = new();
    private readonly HashSet<int>        cleanedPids         = [];

    private CatLaunchRequest       request        = null!;
    private AccountManager         accountManager = null!;
    private XIVAccount             account        = null!;
    private DirectoryInfo          gamePath       = null!;
    private DeviceProfileSnapshot  device         = null!;
    private GameLaunchContext      context        = null!;
    private DCTravelRuntimeService? dcTravel;
    private MinionAccount?         minionRow;
    private string?                quickKey;
    private FFXIVProcess?          currentProcess;
    private int                    lastPid;
    private int?                   lastExitCode;

    /// <inheritdoc />
    public async Task<int> RunAsync(CatLaunchRequest launchRequest, ICatLaunchReporter reporter, CancellationToken cancellationToken)
    {
        request = launchRequest;

        try
        {
            await PrepareAsync(reporter, cancellationToken).ConfigureAwait(false);

            using var final = await RunGameAsync(RestartMonitor.RestartOptions.Normal, null, reporter, cancellationToken).ConfigureAwait(false);

            reporter.Exited(lastPid, lastExitCode);
            return 0;
        }
        catch (CatLaunchException ex)
        {
            reporter.Failed(ex.Code, ex.Message);
            return CatLaunchHost.EXIT_LAUNCH_FAILED;
        }
        finally
        {
            dcTravel?.Dispose();
        }
    }

    /// <inheritdoc />
    public async Task InjectAsync(bool dalamud, bool minion, ICatLaunchReporter reporter, CancellationToken cancellationToken)
    {
        var process = currentProcess;

        if (process == null || process.UnderlyingProcess.HasExited)
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
                var row = ReloadMinionRow(out var error);

                if (row == null)
                    reporter.Agent(CatAgentKinds.MINION, false, error.Code, error.Message);
                else
                {
                    minionRow = row;
                    await AttachMinionAsync(process, IsDalamudLoaded(process.UnderlyingProcess), reporter, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            if (!process.UnderlyingProcess.HasExited)
                reporter.Stage(CatStages.RUNNING);
        }
    }

    #region 准备

    private async Task PrepareAsync(ICatLaunchReporter reporter, CancellationToken cancellationToken)
    {
        reporter.Stage(CatStages.PREPARING);

        try
        {
            await ensureInitialized().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[CatHost] 初始化设置与账号库失败");
            throw new CatLaunchException(CatCodes.LAUNCH_FAILED, $"DcMiniLauncher 初始化失败: {ex.Message}");
        }

        accountManager = App.AccountManager;

        if (accountManager.CurrentCredType == CredType.WindowsHello)
            throw new CatLaunchException(CatCodes.AUTHORIZATION_REQUIRED, "账号库使用 Windows Hello 加密, 无人值守时无法解密已保存的凭证");

        account = accountManager.FindAccount(request.AccountName, XIVAccountType.Sdo)
                  ?? throw new CatLaunchException(CatCodes.AUTHORIZATION_REQUIRED, "DcMiniLauncher 账号库里没有这个号, 需要先授权");

        // 其它进程可能刚更新过这个号的凭证和设备, 启动前从数据库刷新这一行
        accountManager.RefreshFromDatabase(account);

        gamePath = App.Settings.GetGamePath(XIVAccountType.Sdo) is { Exists: true } path
                       ? path
                       : throw new CatLaunchException(CatCodes.INVALID_GAME_PATH, "DcMiniLauncher 设置里的国服游戏目录无效");

        await CheckGameUpdateAsync(cancellationToken).ConfigureAwait(false);

        if (request.Minion)
            minionRow = ReloadMinionRow(out var minionError) ?? throw new CatLaunchException(minionError.Code, minionError.Message);

        device = CatDeviceProfiles.Resolve(accountManager, account, out var isPerAccount)
                 ?? throw new CatLaunchException(CatCodes.AUTHORIZATION_REQUIRED, "这个号开了独立设备, 但账号库里找不到它的设备信息, 需要重新授权");

        if (!isPerAccount)
            reporter.Log("warning", "这个号在账号库里用的是共享设备, 不是独立设备");

        var areas = await LoadAreasAsync().ConfigureAwait(false);
        var area  = areas.FirstOrDefault(x => string.Equals(x.AreaName, account.AreaName, StringComparison.Ordinal)) ?? areas[0];

        dcTravel = new DCTravelRuntimeService(SyncAreaFromDcTravel);

        var loginResult = await LoginWithSavedCredentialAsync(reporter, cancellationToken).ConfigureAwait(false);

        context = new GameLaunchContext(loginResult, area, areas, XIVAccountType.Sdo);

        if (!string.IsNullOrEmpty(quickKey))
            dcTravel.ConfigureQuickLoginRefresh(RefreshSessionIdByQuickLoginAsync);

        context.DcTravelPort = await dcTravel.StartAsync().ConfigureAwait(false);
        Log.Information("[CatHost] 跨区会话已建立, 端口 {Port}, 大区 {Area}", context.DcTravelPort, area.AreaName);
    }

    private async Task CheckGameUpdateAsync(CancellationToken cancellationToken)
    {
        GameUpdateCheckResult checkResult;

        try
        {
            checkResult = await GameUpdater.Check(gamePath, false, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error(ex, "[CatHost] 启动前补丁检查失败");
            throw new CatLaunchException(CatCodes.LAUNCH_FAILED, $"检查游戏更新失败: {ex.Message}");
        }

        if (checkResult.NeedsUpdate)
            throw new CatLaunchException(CatCodes.GAME_UPDATE_REQUIRED, "游戏有待安装的补丁, 请先在 DcMiniLauncher 界面里更新游戏");
    }

    private static async Task<LoginArea[]> LoadAreasAsync()
    {
        LoginArea[] areas;

        try
        {
            areas = await LoginArea.Get().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[CatHost] 获取大区列表失败");
            throw new CatLaunchException(CatCodes.LAUNCH_FAILED, $"获取大区列表失败: {ex.Message}");
        }

        if (areas.Length == 0)
            throw new CatLaunchException(CatCodes.LAUNCH_FAILED, "获取大区列表失败: 列表为空");

        return areas;
    }

    private MinionAccount? ReloadMinionRow(out (string Code, string Message) error)
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

        var row = MinionCards.FindByCard(rows, request.CardFingerprint!, request.Variant!);

        if (row == null)
            error = (CatCodes.MINION_CARD_NOT_FOUND, $"本机 Minion Accounts.json 里找不到卡 {request.CardFingerprint} 的{(request.Variant == MinionCards.VARIANT_GLOBAL ? "国际服" : "国服")}注入行");

        return row;
    }

    #endregion

    #region 登录与票据

    private async Task<LoginResult> LoginWithSavedCredentialAsync(ICatLaunchReporter reporter, CancellationToken cancellationToken)
    {
        var secretsAvailable = !accountManager.HasUnavailableSecrets(account);
        Exception? lastError = null;

        var savedKey = secretsAvailable && !string.IsNullOrEmpty(account.SdoQuickLoginSecret)
                           ? await accountManager.Decrypt(account.SdoQuickLoginSecret).ConfigureAwait(false)
                           : null;

        if (!string.IsNullOrEmpty(savedKey))
        {
            redactor.Register(savedKey);

            try
            {
                var result = await loginClient.LoginAsync
                             (
                                 LoginType.QuickLogin,
                                 new LoginRequest
                                 {
                                     Account                 = account.UserName,
                                     Secret                  = savedKey,
                                     QuickLoginEnabled       = true,
                                     DeviceProfile           = device,
                                     LoginSessionRefreshSink = dcTravel,
                                     ShowLoginMessage        = message => Log.Information("[CatHost] 登录: {Message}", message)
                                 },
                                 cancellationToken
                             ).ConfigureAwait(false);

                quickKey = savedKey;
                await PersistQuickKeyAsync(result.OAuthLogin?.QuickLoginSecret).ConfigureAwait(false);
                return EnsureLoginOk(result);
            }
            catch (CatLaunchException)
            {
                throw;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                Log.Warning(ex, "[CatHost] 用已保存的快速登录凭证登录失败");
                reporter.Log("warning", $"快速登录失败: {ex.Message}");
                lastError = ex;
            }
        }

        var savedPassword = secretsAvailable && !string.IsNullOrEmpty(account.SdoPassword)
                                ? await accountManager.Decrypt(account.SdoPassword).ConfigureAwait(false)
                                : null;

        if (!string.IsNullOrEmpty(savedPassword))
        {
            redactor.Register(savedPassword);

            try
            {
                var result = await loginClient.LoginAsync
                             (
                                 LoginType.Static,
                                 new LoginRequest
                                 {
                                     Account                 = account.UserName,
                                     Secret                  = savedPassword,
                                     QuickLoginEnabled       = false,
                                     DeviceProfile           = device,
                                     LoginSessionRefreshSink = dcTravel,
                                     ShowLoginMessage        = message => Log.Information("[CatHost] 登录: {Message}", message),

                                     // 无人值守: 需要短信 / 图形验证码时不弹框, 直接按需要授权处理
                                     PromptTextInput    = (_, _, _) => null,
                                     PromptCaptchaInput = _ => null
                                 },
                                 cancellationToken
                             ).ConfigureAwait(false);

                return EnsureLoginOk(result);
            }
            catch (CatLaunchException)
            {
                throw;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                Log.Warning(ex, "[CatHost] 用已保存的密码登录失败");
                lastError = ex;
            }
        }

        throw new CatLaunchException
        (
            CatCodes.AUTHORIZATION_REQUIRED,
            lastError == null
                ? "这个号在账号库里没有可用的快速登录凭证或密码, 需要先授权"
                : $"已保存的凭证登录失败, 需要重新授权: {lastError.Message}"
        );
    }

    private static LoginResult EnsureLoginOk(LoginResult result) =>
        result.State switch
        {
            LoginState.Ok when result.OAuthLogin != null => result,
            LoginState.NeedsPatchGame                    => throw new CatLaunchException(CatCodes.GAME_UPDATE_REQUIRED, "游戏需要更新"),
            LoginState.NeedsPatchBoot                    => throw new CatLaunchException(CatCodes.LAUNCH_FAILED, "部分游戏文件损坏, 需要重新安装游戏"),
            LoginState.NoService                         => throw new CatLaunchException(CatCodes.LAUNCH_FAILED, "该账号无游戏游玩权限"),
            LoginState.NoTerms                           => throw new CatLaunchException(CatCodes.LAUNCH_FAILED, "该账号尚未接受游玩使用条款"),
            _                                            => throw new CatLaunchException(CatCodes.AUTHORIZATION_REQUIRED, $"登录未成功 ({result.State}), 需要重新授权")
        };

    private async Task PersistQuickKeyAsync(string? newKey)
    {
        if (string.IsNullOrEmpty(newKey) || string.Equals(newKey, quickKey, StringComparison.Ordinal))
            return;

        quickKey = newKey;
        redactor.Register(newKey);

        try
        {
            var encrypted = await accountManager.Encrypt(newKey).ConfigureAwait(false);

            if (string.IsNullOrEmpty(encrypted))
                return;

            // 只改这个号自己那一行的快速登录凭证, 不动当前账号选择和设备设置
            account.SdoQuickLoginSecret = encrypted;
            accountManager.Save(account);
            Log.Information("[CatHost] 已更新这个号的快速登录凭证");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[CatHost] 保存新的快速登录凭证失败");
        }
    }

    private async Task<string> RefreshSessionIdByQuickLoginAsync()
    {
        await quickKeyLock.WaitAsync().ConfigureAwait(false);

        try
        {
            var result = await loginClient.LoginBySessionKey(account.UserName, quickKey!, dcTravel, device).ConfigureAwait(false);
            await PersistQuickKeyAsync(result.OAuthLogin?.QuickLoginSecret).ConfigureAwait(false);

            var oauth = result.OAuthLogin;

            if (oauth == null)
                return string.Empty;

            if (!string.IsNullOrEmpty(oauth.SessionID))
                return oauth.SessionID;

            if (string.IsNullOrEmpty(oauth.TGT) || string.IsNullOrEmpty(oauth.Guid))
                return string.Empty;

            var sessionId = await new LoginChannelContext(device).GetSessionIdAsync(oauth.TGT, oauth.Guid).ConfigureAwait(false);
            redactor.Register(sessionId);
            return sessionId;
        }
        finally
        {
            quickKeyLock.Release();
        }
    }

    private async Task EnsureFreshSessionIdAsync()
    {
        var oauthLogin = context.LoginResult.OAuthLogin!;

        if (!string.IsNullOrEmpty(oauthLogin.TGT) && !string.IsNullOrEmpty(oauthLogin.Guid))
        {
            redactor.Register(oauthLogin.TGT);

            try
            {
                var loginContext = new LoginChannelContext(oauthLogin.DeviceProfile ?? device);
                oauthLogin.SessionID = await loginContext.GetSessionIdAsync(oauthLogin.TGT, oauthLogin.Guid).ConfigureAwait(false);
                redactor.Register(oauthLogin.SessionID);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[CatHost] 通过 TGT 获取 session ticket 失败");
                throw new CatLaunchException(CatCodes.AUTHORIZATION_REQUIRED, "登录会话已过期, 需要重新授权");
            }
        }

        if (string.IsNullOrEmpty(oauthLogin.SessionID))
            throw new CatLaunchException(CatCodes.AUTHORIZATION_REQUIRED, "登录异常: 没有拿到 session ticket");

        if (string.IsNullOrEmpty(oauthLogin.SndaID))
            throw new CatLaunchException(CatCodes.AUTHORIZATION_REQUIRED, "登录异常: SNDAID 为空");
    }

    private void SyncAreaFromDcTravel(string areaName)
    {
        var matched = context?.Areas.FirstOrDefault(x => string.Equals(x.AreaName, areaName, StringComparison.Ordinal));

        if (matched == null)
        {
            Log.Warning("[CatHost] 当前登录上下文中不存在大区 {AreaName}, 忽略同步请求", areaName);
            return;
        }

        account.AreaName = areaName;
        accountManager.Save(account);
        context!.Area = matched;
        Log.Information("[CatHost] 已同步启动大区为 {AreaName} (ID={AreaID})", areaName, matched.AreaID);
    }

    #endregion

    #region 启动与生命周期

    /// <summary>
    ///     起一次游戏并守到它退出; Dalamud 崩溃处理器要求重启时递归起新进程（进程号会变）。返回最后一个游戏进程。
    /// </summary>
    private async Task<FFXIVProcess> RunGameAsync
    (
        RestartMonitor.RestartOptions options,
        int?                          restartedFromPid,
        ICatLaunchReporter            reporter,
        CancellationToken             cancellationToken
    )
    {
        var (launched, dalamudOk, companionAppManager) = await StartOnceAsync(options, restartedFromPid, reporter, cancellationToken).ConfigureAwait(false);
        FFXIVProcess result = launched;

        try
        {
            if (dalamudOk)
            {
                await launcher.RestartMonitor
                              .MonitorAsync
                              (
                                  launched,
                                  options,
                                  async restartOptions =>
                                  {
                                      CleanupProcess(launched, companionAppManager);

                                      try
                                      {
                                          result = await RunGameAsync(restartOptions, launched.ProcessID, reporter, cancellationToken).ConfigureAwait(false);
                                          return result;
                                      }
                                      catch (CatLaunchException ex)
                                      {
                                          reporter.Log("error", $"崩溃后重启游戏失败: {ex.Message}");
                                          return null;
                                      }
                                  },
                                  cancellationToken
                              )
                              .ConfigureAwait(false);
            }
            else
                await launched.UnderlyingProcess.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CleanupProcess(launched, companionAppManager);
        }

        return result;
    }

    private async Task<(FFXIVProcess Process, bool DalamudOk, CompanionAppManager? Companion)> StartOnceAsync
    (
        RestartMonitor.RestartOptions options,
        int?                          restartedFromPid,
        ICatLaunchReporter            reporter,
        CancellationToken             cancellationToken
    )
    {
        var dalamudOk = false;
        DalamudSession? dalamudSession = null;

        if (request.Dalamud && !options.ForceNoDalamud)
        {
            reporter.Stage(CatStages.UPDATING_DALAMUD);

            var (session, error) = await Task.Run(() => PrepareDalamud(options.NoPlugins, options.NoThirdPlugins), cancellationToken).ConfigureAwait(false);

            if (session == null)
            {
                // 首次启动时 Dalamud 不可用就不起游戏; 崩溃重启时游戏必须照常起来, 只报告 Dalamud 失败
                if (restartedFromPid == null)
                    throw new CatLaunchException(CatCodes.DALAMUD_UNAVAILABLE, error!);

                reporter.Agent(CatAgentKinds.DALAMUD, false, CatCodes.DALAMUD_UNAVAILABLE, error);
            }
            else
            {
                dalamudSession = session;
                dalamudOk      = true;
            }
        }

        // 票据单次有效且有时效, Dalamud 准备完再现取
        await EnsureFreshSessionIdAsync().ConfigureAwait(false);

        context.InGameAgents = (dalamudOk ? InGameAgents.Dalamud : InGameAgents.None) | (request.Minion ? InGameAgents.Minion : InGameAgents.None);
        Log.Information("[CatHost] 本次启动的游戏内代理: {InGameAgents}", context.InGameAgents);

        reporter.Stage(CatStages.STARTING);

        var launched = await Task.Run(() => LaunchProcess(dalamudOk, dalamudSession), cancellationToken).ConfigureAwait(false);
        var process  = launched.UnderlyingProcess;
        var startedAt = MinionOccupancy.GetProcessStartedAt(process);

        currentProcess = launched;

        if (restartedFromPid is { } oldPid)
            reporter.Restarted(oldPid, launched.ProcessID, startedAt);
        else
            reporter.Started(launched.ProcessID, startedAt);

        RunningGameRegistry.Register(process, context.InGameAgents, context.DcTravelPort);

        CompanionAppManager? companionAppManager = null;

        try
        {
            companionAppManager = companionAppService.StartCompanionApps(launched.ProcessID);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[CatHost] 启动伴随程序失败");
            reporter.Log("warning", $"启动伴随程序失败: {ex.Message}");
        }

        if (dalamudOk)
        {
            reporter.Stage(CatStages.INJECTING);
            var loaded = await WaitForDalamudAsync(process, cancellationToken).ConfigureAwait(false);
            reporter.Agent(CatAgentKinds.DALAMUD, loaded, loaded ? null : CatCodes.DALAMUD_UNAVAILABLE, loaded ? null : "游戏进程里没有等到 Dalamud 加载");
        }

        if (request.Minion)
        {
            await AttachMinionAsync(launched, dalamudOk, reporter, cancellationToken).ConfigureAwait(false);

            if (MiniModuleInjector.ModulePath.Exists)
            {
                try
                {
                    await MiniModuleGate.RunAsync(process, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "[CatHost] 游戏内模块自检时发生异常（游戏不受影响）");
                }
            }
        }

        if (!process.HasExited)
            reporter.Stage(CatStages.RUNNING);

        return (launched, dalamudOk, companionAppManager);
    }

    private FFXIVProcess LaunchProcess(bool dalamudOk, DalamudSession? dalamudSession)
    {
        var settings          = App.Settings;
        var oauthLogin        = context.LoginResult.OAuthLogin!;
        var dotnetRuntimePath = App.Dalamud.Updater.Runtime;

        Process? StartGame(GameStartRequest startRequest)
        {
            if (dalamudOk && dalamudSession != null)
            {
                var compat = "RunAsInvoker ";
                compat += startRequest.DpiAwareness switch
                {
                    DPIAwareness.Aware   => "HighDPIAware",
                    DPIAwareness.Unaware => "DPIUnaware",
                    _                    => throw new ArgumentOutOfRangeException()
                };
                startRequest.Environment.Add("__COMPAT_LAYER", compat);

                if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DALAMUD_RUNTIME")))
                    startRequest.Environment.Add("DALAMUD_RUNTIME", dotnetRuntimePath.FullName);

                if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOTNET_ROOT")))
                    startRequest.Environment.Add("DOTNET_ROOT", dotnetRuntimePath.FullName);

                if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOTNET_MULTILEVEL_LOOKUP")))
                    startRequest.Environment.Add("DOTNET_MULTILEVEL_LOOKUP", "0");

                return dalamudSession.LaunchGame(new FileInfo(startRequest.ExePath), startRequest.Arguments, startRequest.Environment);
            }

            return NativeAclFix.LaunchGame
            (
                startRequest.WorkingDirectory,
                startRequest.ExePath,
                startRequest.Arguments,
                startRequest.Environment,
                startRequest.DpiAwareness,
                process => new GameArgumentInterop.Fixer(process).Fix()
            );
        }

        FFXIVProcess? launched;

        try
        {
            launched = launcher.LaunchGame
            (
                StartGame,
                oauthLogin.SessionID,
                oauthLogin.SndaID,
                context.DcTravelPort,
                context.Area.AreaID,
                context.Area.AreaLobby,
                context.Area.AreaGM,
                context.Area.AreaConfigUpload,
                Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(context.Areas))),
                settings.AdditionalLaunchArgs,
                gamePath,
                settings.EncryptArgumentsV2,
                settings.DPIAwareness
            );
        }
        catch (BinaryNotPresentException ex)
        {
            Log.Error(ex, "[CatHost] 找不到游戏可执行文件");
            throw new CatLaunchException(CatCodes.INVALID_GAME_PATH, "找不到游戏可执行文件, 可能需要重新安装游戏");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[CatHost] 启动游戏进程失败");
            throw new CatLaunchException(CatCodes.LAUNCH_FAILED, $"启动游戏进程失败: {ex.GetType().Name}: {ex.Message}");
        }

        Troubleshooting.LogTroubleshooting(gamePath);

        return launched ?? throw new CatLaunchException(CatCodes.LAUNCH_FAILED, "游戏进程为空");
    }

    private void CleanupProcess(FFXIVProcess launched, CompanionAppManager? companionAppManager)
    {
        lock (cleanupLock)
        {
            if (!cleanedPids.Add(launched.ProcessID))
                return;
        }

        try
        {
            companionAppService.StopCompanionApps(launched.ProcessID, companionAppManager);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[CatHost] 停止伴随程序失败");
        }

        RunningGameRegistry.Unregister(launched.ProcessID);

        lastPid      = launched.ProcessID;
        lastExitCode = TryGetExitCode(launched);

        Log.Information("[CatHost] 游戏进程已退出 (PID={ProcessID}, ExitCode=0x{ExitCode:X8})", launched.ProcessID, (uint)(lastExitCode ?? 0));

        // 告诉 MINIONAPP 这一行停机了, 否则它会过一分钟自己拉个新客户端
        MinionAppStatusReporter.ReportStopped(launched.ProcessID);
    }

    private static int? TryGetExitCode(FFXIVProcess process)
    {
        try
        {
            return process.UnderlyingProcess.HasExited ? process.ExitCode : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    #endregion

    #region Dalamud 与 Minion

    private (DalamudSession? Session, string? Error) PrepareDalamud(bool noPlugins, bool noThird)
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
            var settings = App.Settings;
            var session = App.Dalamud.CreateLauncher
            (
                gamePath,
                new DalamudLaunchOptions(settings.DalamudLoadMethod, (int)settings.DalamudInjectionDelayMS, false, noPlugins, noThird)
            );

            App.Dalamud.RunUpdater();

            return session.EnsureReady(gamePath) == DalamudSession.DalamudInstallState.Ok
                       ? (session, null)
                       : (null, "Dalamud 尚未准备完成（下载或校验未通过）");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[CatHost] 准备 Dalamud 时发生错误");
            return (null, $"下载 Dalamud 相关文件异常: {ex.Message}");
        }
    }

    private async Task InjectDalamudIntoRunningAsync(FFXIVProcess process, ICatLaunchReporter reporter, CancellationToken cancellationToken)
    {
        if (IsDalamudLoaded(process.UnderlyingProcess))
        {
            reporter.Agent(CatAgentKinds.DALAMUD, true, null, "Dalamud 已在游戏里");
            return;
        }

        reporter.Stage(CatStages.UPDATING_DALAMUD);

        var (session, error) = await Task.Run(() => PrepareDalamud(false, false), cancellationToken).ConfigureAwait(false);

        if (session == null)
        {
            reporter.Agent(CatAgentKinds.DALAMUD, false, CatCodes.DALAMUD_UNAVAILABLE, error);
            return;
        }

        reporter.Stage(CatStages.INJECTING);

        try
        {
            await Task.Run(() => session.InjectGame(process.ProcessID), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[CatHost] 补注入 Dalamud 失败");
            reporter.Agent(CatAgentKinds.DALAMUD, false, CatCodes.DALAMUD_UNAVAILABLE, $"补注入 Dalamud 失败: {ex.Message}");
            return;
        }

        var loaded = await WaitForDalamudAsync(process.UnderlyingProcess, cancellationToken).ConfigureAwait(false);
        reporter.Agent(CatAgentKinds.DALAMUD, loaded, loaded ? null : CatCodes.DALAMUD_UNAVAILABLE, loaded ? null : "游戏进程里没有等到 Dalamud 加载");
    }

    private async Task AttachMinionAsync(FFXIVProcess launched, bool dalamudInjected, ICatLaunchReporter reporter, CancellationToken cancellationToken)
    {
        reporter.Stage(CatStages.ATTACHING_MINION);

        if (minionRow == null)
        {
            reporter.Agent(CatAgentKinds.MINION, false, CatCodes.MINION_CARD_NOT_FOUND, "没有可挂的 Minion 行");
            return;
        }

        if (!string.IsNullOrEmpty(minionRow.Keycode))
            redactor.Register(minionRow.Keycode);
        redactor.Register(App.Settings.MinionPassword);

        try
        {
            var result = await MinionAttacher.AttachAsync(minionRow, launched.UnderlyingProcess, gamePath, dalamudInjected, account.UserName, cancellationToken)
                                             .ConfigureAwait(false);

            if (result.Ok)
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
    }

    private static async Task<bool> WaitForDalamudAsync(Process process, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        while (stopwatch.Elapsed < DalamudInjectTimeout)
        {
            if (process.HasExited)
                return false;

            if (IsDalamudLoaded(process))
                return true;

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
        }

        return IsDalamudLoaded(process);
    }

    private static bool IsDalamudLoaded(Process process)
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

    #endregion
}
