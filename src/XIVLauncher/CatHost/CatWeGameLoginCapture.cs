using System.Diagnostics;
using System.Globalization;
using Serilog;
using XIVLauncher.Account;
using XIVLauncher.Common.Game;
using XIVLauncher.Login.WeGame;

namespace XIVLauncher.CatHost;

/// <summary>
///     账号库里的一行 WeGame 号: 账号名是 WeGame 给的用户号, 备注里通常写着客户的 QQ 号或手机号
/// </summary>
public sealed record CatWeGameRow(string UserName, string? Note);

/// <summary>
///     就地登录 WeGame 时对本机的操作（游戏目录、WeGame 客户端、取登录信息）, 单独抽出来好让测试替换
/// </summary>
public interface ICatWeGameLoginEnvironment
{
    /// <summary>
    ///     设置里 WeGame 版游戏目录下的 sdologin 目录; 游戏目录没设、不是 WeGame 版游戏、或 sdologin 目录不完整时返回 null
    /// </summary>
    string? FindSdologinDir();

    /// <summary>
    ///     结束 WeGame 客户端的进程, 免得它用记住的上一个号自动登录; 不碰游戏进程
    /// </summary>
    void StopWeGameClient();

    /// <summary>
    ///     拉起 WeGame 并等员工登录, 返回 WeGame 用户号和登录信息。
    ///     本机已有别的进程在等时抛 <see cref="WeGameCapturePipeBusyException" />, 写不进 sdologin 目录时抛 <see cref="VersionDllPermissionDeniedException" />
    /// </summary>
    /// <param name="sdologinDir">sdologin 目录</param>
    /// <param name="beforeLaunch">确认本机没有别的进程在等之后、拉起 WeGame 之前调用</param>
    /// <param name="afterLaunch">拉起 WeGame 之后、开始等登录之前调用</param>
    /// <param name="cancellationToken">取消（超时或关闭请求）</param>
    Task<WeGameCaptureResult> CaptureAsync(string sdologinDir, Action beforeLaunch, Action afterLaunch, CancellationToken cancellationToken);
}

/// <summary>
///     就地登录 WeGame 时对账号库里 WeGame 行的读写, 单独抽出来好让测试替换
/// </summary>
public interface ICatWeGameAccountStore
{
    /// <summary>本进程启动时加载的 WeGame 行</summary>
    IReadOnlyList<CatWeGameRow> ListRows();

    /// <summary>从数据库重新读出 WeGame 行（含其它进程刚写入的）</summary>
    IReadOnlyList<CatWeGameRow> ReloadRows();

    /// <summary>读这一行已存的登录信息; 没有或读不出时返回 null</summary>
    Task<string?> ReadTokenAsync(CatWeGameRow row);

    /// <summary>清掉这一行已存的登录信息; 期间别的进程存了新的就不动</summary>
    void ClearToken(CatWeGameRow row);

    /// <summary>
    ///     把刚取到的登录信息存进用户号对应的那一行, 没有这一行就新建; 只动这一行
    /// </summary>
    /// <param name="userId">WeGame 用户号</param>
    /// <param name="token">登录信息</param>
    /// <param name="note">要写进备注的号; null = 不动备注</param>
    Task<CatWeGameRow> SaveCapturedAsync(string userId, string token, string? note);
}

/// <summary>
///     WeGame 号的无界面登录: 先用账号库里存下的登录信息; 没有可用的、且 launch 带了 weGameLogin 时, 在本机拉起 WeGame,
///     等员工在 WeGame 窗口里登录, 取到登录信息后存进账号库（备注写上号请求里的号）再接着登录。密码不经过本进程。
///     <para>
///         进入就地登录的三种情况: 账号库里找不到这个号; 找到了但没有存登录信息; 存下的登录信息被盛趣明确拒绝。
///         备注对上多行、网络错误、盛趣要求客户验证等其它失败不进。launch 没带 weGameLogin 时一律不进, 失败码与消息同原先。
///     </para>
///     <para>工作台外壳靠程序集里有没有这个类型名判断启动器是否支持就地登录, 名字和命名空间不能改。</para>
/// </summary>
public sealed class CatWeGameLoginCapture(ICatWeGameLoginEnvironment environment, ICatWeGameAccountStore store, CatLogRedactor redactor)
{
    /// <summary>
    ///     launch 没带 weGameLogin 时给工作室员工看的处理办法（消息里不说「令牌」这类内部词）
    /// </summary>
    private const string LOGIN_IN_UI_HINT = "请在这台电脑的 DcMiniLauncher 界面版里用 WeGame 方式重新登录一次";

    /// <summary>等登录结束后最多等观察器多久收尾</summary>
    private static readonly TimeSpan WatcherStopTimeout = TimeSpan.FromSeconds(5);

    private string? capturedUserName;
    private string? capturedToken;

    private volatile CatWeGameChallengeWatcher? watcher;

    /// <summary>拉起 WeGame 后等员工登录的上限</summary>
    public TimeSpan LoginTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>
    ///     等登录期间用它看 WeGame 的窗口, 把要客户配合的验证报给工作台（见 <see cref="CatWeGameChallengeWatcher" />）; 为 null 时不看
    /// </summary>
    public ICatWeGameScreen? Screen { get; init; }

    /// <summary>等登录期间多久看一次窗口; 不设用观察器自己的间隔</summary>
    public TimeSpan? WatchInterval { get; init; }

    /// <summary>
    ///     客户说设备验证的短信已经发了; 不在等登录时回 notRunning
    /// </summary>
    public CatAcceptResult ConfirmSms(string challengeId) =>
        watcher?.ConfirmSms(challengeId) ?? CatAcceptResult.Rejected(CatCodes.NOT_RUNNING, "当前没有在等 WeGame 的设备验证");

    /// <summary>
    ///     找上号请求指的那一行: 账号库里 WeGame 行的账号名是 WeGame 给的用户号, 请求带的通常是客户的 QQ 号或手机号,
    ///     所以按账号名找不到时再按备注找。都找不到且允许就地登录时, 等员工登录后建出这一行。
    /// </summary>
    public async Task<CatWeGameRow> FindRowAsync(CatLaunchRequest request, ICatLaunchReporter reporter, CancellationToken cancellationToken)
    {
        var row = CatRealGameRunner.ResolveWeGameAccount(store.ListRows(), request.AccountName, x => x.UserName, x => x.Note, out var match);

        switch (match)
        {
            case CatWeGameAccountMatch.ByName:
                return row!;

            case CatWeGameAccountMatch.ByNote:
                Log.Information("[CatHost] 按备注找到 WeGame 号: 请求的号={Requested}, 账号库里的账号名={UserName}", request.AccountName, row!.UserName);
                return row;

            case CatWeGameAccountMatch.AmbiguousNote:
                throw AmbiguousNote(request);

            default:
                if (request.WeGameLogin)
                    return await CaptureAsync(request, null, reporter, cancellationToken).ConfigureAwait(false);

                throw new CatLaunchException
                (
                    CatCodes.AUTHORIZATION_REQUIRED,
                    $"DcMiniLauncher 账号库里没有这个 WeGame 号：先在这台电脑的 DcMiniLauncher 界面版里登录一次，并把这个号的备注填上客户的 QQ 号或手机号（{request.AccountName}）"
                );
        }
    }

    /// <summary>
    ///     用这一行的登录信息登录盛趣。没有登录信息、或存下的被盛趣明确拒绝（此时先清掉）时, 允许就地登录就等员工在 WeGame 里登录后再登一次;
    ///     刚取到的登录信息仍被拒绝则不再重来。
    /// </summary>
    /// <param name="request">上号请求</param>
    /// <param name="row">这个号在账号库里的那一行</param>
    /// <param name="reporter">报告</param>
    /// <param name="login">用给定的登录信息登录盛趣; 失败时抛登录库的异常</param>
    /// <param name="cancellationToken">取消</param>
    public async Task<TLogin> LoginAsync<TLogin>
    (
        CatLaunchRequest                              request,
        CatWeGameRow                                  row,
        ICatLaunchReporter                            reporter,
        Func<string, CancellationToken, Task<TLogin>> login,
        CancellationToken                             cancellationToken
    )
    {
        var justCaptured = string.Equals(capturedUserName, row.UserName, StringComparison.Ordinal);
        var token        = justCaptured ? capturedToken : await store.ReadTokenAsync(row).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(token))
        {
            if (!request.WeGameLogin)
                throw new CatLaunchException(CatCodes.AUTHORIZATION_REQUIRED, $"这个 WeGame 号还没在这台电脑登录过, {LOGIN_IN_UI_HINT}");

            row          = await CaptureAsync(request, row, reporter, cancellationToken).ConfigureAwait(false);
            token        = capturedToken!;
            justCaptured = true;
        }

        try
        {
            return await login(token, cancellationToken).ConfigureAwait(false);
        }
        catch (CatLaunchException)
        {
            throw;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            var failure = OnLoginFailed(row, ex, justCaptured);

            if (justCaptured || !request.WeGameLogin || !CatLoginFailures.ShouldClearWeGameToken(ex))
                throw failure;
        }

        row = await CaptureAsync(request, row, reporter, cancellationToken).ConfigureAwait(false);

        try
        {
            return await login(capturedToken!, cancellationToken).ConfigureAwait(false);
        }
        catch (CatLaunchException)
        {
            throw;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw OnLoginFailed(row, ex, true);
        }
    }

    /// <summary>
    ///     等够 <paramref name="timeout" /> 没人登录时的失败消息
    /// </summary>
    internal static string TimeoutMessage(TimeSpan timeout) =>
        $"WeGame 等了 {timeout.TotalMinutes.ToString("0.##", CultureInfo.InvariantCulture)} 分钟没有登录，请重新上号";

    /// <summary>
    ///     登录盛趣失败: 记日志, 盛趣明确说这份登录信息不行（第三方验证失败）时把已存的清掉, 给出要报的失败
    /// </summary>
    private CatLaunchException OnLoginFailed(CatWeGameRow row, Exception exception, bool justCaptured)
    {
        var code   = CatLoginFailures.ToWeGameLoginCode(exception);
        var detail = CatLoginFailures.Describe(exception);
        Log.Warning(exception, "[CatHost] 用{Source} WeGame 令牌登录失败 ({Code}): {Detail}", justCaptured ? "刚取到的" : "已保存的", code, detail);

        if (CatLoginFailures.ShouldClearWeGameToken(exception))
            store.ClearToken(row);

        // 消息原样给工作室员工看, 不出现「令牌」这类内部说法
        return new CatLaunchException
        (
            code,
            code switch
            {
                CatCodes.NETWORK_ERROR                               => $"连不上盛趣登录服务器, 稍后重试即可: {detail}",
                CatCodes.RISK_CONTROL                                => $"盛趣要求客户验证: {detail}",
                CatCodes.AUTHORIZATION_REQUIRED when justCaptured    => $"刚在 WeGame 里登录的这个号被盛趣拒绝了, 请重新上号: {detail}",
                CatCodes.AUTHORIZATION_REQUIRED                      => $"这个 WeGame 号的登录已失效, {LOGIN_IN_UI_HINT}: {detail}",
                _                                                    => $"WeGame 号登录出错: {exception.GetType().Name}: {detail}"
            }
        );
    }

    /// <summary>
    ///     在本机拉起 WeGame 等员工登录, 取到后存进账号库, 返回存进去的那一行。
    ///     <paramref name="expectedRow" /> 是账号库里已经对上这个号的那一行（没有则为 null）: 登录的不是它就不存。
    /// </summary>
    private async Task<CatWeGameRow> CaptureAsync(CatLaunchRequest request, CatWeGameRow? expectedRow, ICatLaunchReporter reporter, CancellationToken cancellationToken)
    {
        var sdologinDir = environment.FindSdologinDir()
                          ?? throw new CatLaunchException(CatCodes.INVALID_GAME_PATH, "DcMiniLauncher 设置里的 WeGame 版游戏目录无效, 请在界面版「设置」里重新选择");

        Log.Information("[CatHost] 这个 WeGame 号在本机没有可用的登录信息, 拉起 WeGame 等员工登录: 请求的号={Requested}", request.AccountName);

        WeGameCaptureResult captured;

        using (var watching = new CancellationTokenSource())
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            var watch = Task.CompletedTask;

            timeout.CancelAfter(LoginTimeout);

            try
            {
                captured = await environment.CaptureAsync
                           (
                               sdologinDir,
                               StopWeGameClient,
                               () =>
                               {
                                   reporter.Stage(CatStages.WAITING_WE_GAME_LOGIN);
                                   watch = StartWatcher(request, reporter, watching.Token);
                               },
                               timeout.Token
                           ).ConfigureAwait(false);
            }
            catch (Exception ex) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException("等 WeGame 登录时收到关闭请求", ex, cancellationToken);
            }
            catch (Exception ex) when (timeout.IsCancellationRequested)
            {
                Log.Warning(ex, "[CatHost] 等了 {Timeout} 没有人在 WeGame 里登录", LoginTimeout);
                throw new CatLaunchException(CatCodes.WE_GAME_LOGIN_TIMEOUT, TimeoutMessage(LoginTimeout));
            }
            catch (WeGameCapturePipeBusyException ex)
            {
                Log.Warning(ex, "[CatHost] 本机已有别的进程在等 WeGame 登录");
                throw new CatLaunchException(CatCodes.WE_GAME_LOGIN_BUSY, "这台电脑正在等另一个 WeGame 号登录，请先完成或取消那一个");
            }
            catch (VersionDllPermissionDeniedException ex)
            {
                // 无界面模式不弹 UAC、不提权
                Log.Warning(ex, "[CatHost] 写不进 sdologin 目录: {Path}", ex.DestinationPath);
                throw new CatLaunchException(CatCodes.WE_GAME_SETUP_REQUIRED, "请用管理员身份打开一次 DcMiniLauncher 界面版完成 WeGame 设置");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[CatHost] 拉起 WeGame 等登录时出错");
                throw new CatLaunchException(CatCodes.LAUNCH_FAILED, $"拉起 WeGame 等登录时出错: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                // 不管是取到了、超时还是被取消, 都先让观察器停下并把没清的验证报成已清除
                await StopWatcherAsync(watching, watch).ConfigureAwait(false);
            }
        }

        redactor.RegisterSecret(captured.Token);

        var userId    = captured.UserId?.Trim();
        var requested = request.AccountName.Trim();

        if (string.IsNullOrEmpty(userId) || string.IsNullOrWhiteSpace(captured.Token))
            throw new CatLaunchException(CatCodes.LAUNCH_FAILED, "WeGame 交回的登录信息不完整, 请重新上号");

        cancellationToken.ThrowIfCancellationRequested();
        reporter.Stage(CatStages.PREPARING);

        // 等登录的这段时间里别的进程可能动过账号库, 重新读一遍再判断
        var rows  = store.ReloadRows();
        var bound = CatRealGameRunner.ResolveWeGameAccount(rows, requested, x => x.UserName, x => x.Note, out var match);

        if (match == CatWeGameAccountMatch.AmbiguousNote)
            throw AmbiguousNote(request);

        // 这个号在账号库里已经对着另一个 WeGame 用户号: 要么登录错了号, 要么那一行的备注写错了, 都不能自动改
        if ((expectedRow ?? bound) is { } expected && !string.Equals(expected.UserName, userId, StringComparison.Ordinal))
        {
            Log.Warning("[CatHost] 登录的 WeGame 用户号 {UserId} 不是账号库里 {Requested} 对着的 {Expected}, 不保存", userId, requested, expected.UserName);
            throw new CatLaunchException
            (
                CatCodes.WE_GAME_ACCOUNT_MISMATCH,
                $"登录的 WeGame 账号不是 DcMiniLauncher 里记着 {requested} 的那一个，请确认登录的是不是这个号"
            );
        }

        // 登录的用户号已经是别的客户的: 不覆盖
        var existing = rows.FirstOrDefault(x => string.Equals(x.UserName, userId, StringComparison.Ordinal));

        if (existing != null && !string.IsNullOrWhiteSpace(existing.Note) && !ReferenceEquals(existing, bound))
        {
            Log.Warning("[CatHost] 登录的 WeGame 用户号 {UserId} 的备注是 {Note}, 对不上请求的号 {Requested}, 不保存", userId, existing.Note, requested);
            throw new CatLaunchException
            (
                CatCodes.WE_GAME_ACCOUNT_MISMATCH,
                $"登录的 WeGame 账号已经绑定了客户 {existing.Note.Trim()}，请确认登录的是不是这个号"
            );
        }

        CatWeGameRow saved;

        try
        {
            // 这一行已经对得上请求的号时不动它的备注（员工可能在里面写了别的话）
            saved = await store.SaveCapturedAsync(userId, captured.Token, existing != null && ReferenceEquals(existing, bound) ? null : requested).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[CatHost] 保存 WeGame 登录信息失败");
            throw new CatLaunchException(CatCodes.LAUNCH_FAILED, $"保存 WeGame 登录信息失败: {ex.Message}");
        }

        Log.Information("[CatHost] 已存下 WeGame 登录信息: 请求的号={Requested}, 账号库里的账号名={UserName}", requested, saved.UserName);

        capturedUserName = saved.UserName;
        capturedToken    = captured.Token;
        return saved;
    }

    /// <summary>
    ///     WeGame 已拉起: 开始看它的窗口（没给 <see cref="Screen" /> 时不看）
    /// </summary>
    private Task StartWatcher(CatLaunchRequest request, ICatLaunchReporter reporter, CancellationToken cancellationToken)
    {
        if (Screen == null)
            return Task.CompletedTask;

        var created = WatchInterval is { } interval
                          ? new CatWeGameChallengeWatcher(Screen, reporter, redactor, request.WeGameScan) { Interval = interval, ClickSettle = interval }
                          : new CatWeGameChallengeWatcher(Screen, reporter, redactor, request.WeGameScan);

        watcher = created;
        return Task.Run(() => created.RunAsync(cancellationToken), CancellationToken.None);
    }

    private async Task StopWatcherAsync(CancellationTokenSource watching, Task watch)
    {
        watcher = null;

        try
        {
            await watching.CancelAsync().ConfigureAwait(false);

            if (await Task.WhenAny(watch, Task.Delay(WatcherStopTimeout)).ConfigureAwait(false) != watch)
                Log.Warning("[CatHost] 看 WeGame 窗口的观察器没有按时停下");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[CatHost] 停止看 WeGame 窗口时出错");
        }
    }

    private void StopWeGameClient()
    {
        try
        {
            environment.StopWeGameClient();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[CatHost] 结束 WeGame 客户端进程失败, 继续拉起 WeGame");
        }
    }

    private static CatLaunchException AmbiguousNote(CatLaunchRequest request) =>
        new(request.WeGameLogin ? CatCodes.WE_GAME_ACCOUNT_AMBIGUOUS : CatCodes.AUTHORIZATION_REQUIRED, $"DcMiniLauncher 里有多个 WeGame 号的备注写着 {request.AccountName}，请只留一个");
}

/// <summary>
///     就地登录 WeGame 对本机的真实操作
/// </summary>
public sealed class CatWeGameLoginRealEnvironment(Func<string?> gameRoot) : ICatWeGameLoginEnvironment
{
    /// <summary>
    ///     要结束的 WeGame 客户端进程名。只有这几个: 游戏进程（ffxiv_dx11）、游戏运行时用到的 rail 和系统服务（wegameservice）不在其中
    /// </summary>
    internal static readonly string[] ClientProcessNames = ["wegame", "wegame_env", "tgp_daemon"];

    private static readonly TimeSpan ClientExitTimeout = TimeSpan.FromSeconds(5);

    /// <inheritdoc />
    public string? FindSdologinDir()
    {
        var root = gameRoot();

        if (!WeGamePathValidator.IsValidGameRoot(root))
            return null;

        var sdologinDir = WeGamePathValidator.DeriveSdologinDir(root!);
        return WeGamePathValidator.IsValidSdologinDir(sdologinDir) ? sdologinDir : null;
    }

    /// <inheritdoc />
    public void StopWeGameClient()
    {
        foreach (var name in ClientProcessNames)
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    try
                    {
                        // 只结束这一个进程, 不连带子进程: 由 WeGame 拉起的东西不归这里管
                        process.Kill();
                        process.WaitForExit(ClientExitTimeout);
                        Log.Information("[CatHost] 已结束 WeGame 客户端进程 {Name} (PID={ProcessID})", name, process.Id);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "[CatHost] 结束 WeGame 客户端进程 {Name} 失败", name);
                    }
                }
            }
        }
    }

    /// <inheritdoc />
    public Task<WeGameCaptureResult> CaptureAsync(string sdologinDir, Action beforeLaunch, Action afterLaunch, CancellationToken cancellationToken) =>
        new WeGameLoginCapturer().CaptureAsync(sdologinDir, cancellationToken, new LogProgress(), beforeLaunch, afterLaunch);

    private sealed class LogProgress : IProgress<string>
    {
        public void Report(string value) =>
            Log.Information("[CatHost] WeGame 登录: {Message}", value);
    }
}

/// <summary>
///     账号库里的 WeGame 行。读写都只针对单独一行, 不动当前账号选择、设备设置和别的行。
/// </summary>
public sealed class CatWeGameAccountStore(AccountManager accountManager) : ICatWeGameAccountStore
{
    private readonly Dictionary<string, string?> lastSecrets = new(StringComparer.Ordinal);

    private List<XIVAccount> accounts = [];

    /// <inheritdoc />
    public IReadOnlyList<CatWeGameRow> ListRows()
    {
        accounts = accountManager.Accounts.Where(x => x.AccountType == XIVAccountType.WeGame).ToList();
        return ToRows();
    }

    /// <inheritdoc />
    public IReadOnlyList<CatWeGameRow> ReloadRows()
    {
        var reloaded = new List<XIVAccount>();

        foreach (var record in accountManager.ReadStoredAccounts())
        {
            if (record.AccountType != XIVAccountType.WeGame || string.IsNullOrWhiteSpace(record.UserName) || string.IsNullOrWhiteSpace(record.ID))
                continue;

            // 本进程已经拿着的行继续用同一个对象, 其它进程新加的行用刚读出的副本
            var account = accounts.FirstOrDefault(x => string.Equals(x.ID, record.ID, StringComparison.Ordinal)) ?? record;
            accountManager.RefreshFromDatabase(account);
            reloaded.Add(account);
        }

        accounts = reloaded;
        return ToRows();
    }

    /// <inheritdoc />
    public async Task<string?> ReadTokenAsync(CatWeGameRow row)
    {
        var account = GetAccount(row);
        var secret  = accountManager.HasUnavailableSecrets(account) ? null : account.WeGameQuickLoginSecret;

        lastSecrets[account.ID] = secret;
        return string.IsNullOrEmpty(secret) ? null : await accountManager.Decrypt(secret).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void ClearToken(CatWeGameRow row)
    {
        try
        {
            var account = GetAccount(row);
            accountManager.RefreshFromDatabase(account);

            if (!string.Equals(account.WeGameQuickLoginSecret, lastSecrets.GetValueOrDefault(account.ID), StringComparison.Ordinal))
            {
                Log.Information("[CatHost] 这个号的 WeGame 令牌刚被别的进程更新过, 不清");
                return;
            }

            account.WeGameQuickLoginSecret = null;
            accountManager.Save(account);
            Log.Information("[CatHost] 已清掉这个号失效的 WeGame 令牌");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[CatHost] 清掉失效的 WeGame 令牌失败");
        }
    }

    /// <inheritdoc />
    public async Task<CatWeGameRow> SaveCapturedAsync(string userId, string token, string? note)
    {
        var secret = await accountManager.Encrypt(token).ConfigureAwait(false);

        if (string.IsNullOrEmpty(secret))
            throw new InvalidOperationException("登录信息加密失败");

        var account = accounts.FirstOrDefault(x => string.Equals(x.UserName, userId, StringComparison.Ordinal));

        if (account == null)
        {
            // 与界面版登录后新建的 WeGame 行相同: 开快速登录, 设备沿用共享设备; 大区留空, 由启动流程按上号请求填
            account = new XIVAccount
            {
                QuickLoginEnabled  = true,
                SdoLoginAccount    = userId,
                WeGameLoginAccount = userId,
                AccountType        = XIVAccountType.WeGame,
                AreaName           = string.Empty,
                UserDefinedName    = note ?? string.Empty,
                SortOrder          = accountManager.ReadStoredAccounts().Count
            };

            AccountManager.ApplyResolvedDeviceProfile(account, accountManager.ResolveDeviceProfile(userId, XIVAccountType.WeGame));
            account.GenerateID();
            accounts.Add(account);
        }
        else
        {
            accountManager.RefreshFromDatabase(account);
            account.QuickLoginEnabled = true;

            if (note != null)
                account.UserDefinedName = note;
        }

        account.WeGameQuickLoginSecret = secret;

        // 只写这一行: 新行不进内存里的账号列表, 免得触发整库保存
        accountManager.Save(account);
        await accountManager.CredProvider.ClearCache().ConfigureAwait(false);

        lastSecrets[account.ID] = secret;
        return new CatWeGameRow(account.UserName, account.UserDefinedName);
    }

    /// <summary>
    ///     这一行在账号库里的账号对象
    /// </summary>
    public XIVAccount GetAccount(CatWeGameRow row) =>
        accounts.FirstOrDefault(x => string.Equals(x.UserName, row.UserName, StringComparison.Ordinal))
        ?? throw new InvalidOperationException($"账号库里没有 WeGame 号 {row.UserName}");

    private CatWeGameRow[] ToRows() =>
        accounts.Select(x => new CatWeGameRow(x.UserName, x.UserDefinedName)).ToArray();
}
