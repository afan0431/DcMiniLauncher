using System.IO;
using System.Net.Http;
using XIVLauncher.Account;
using XIVLauncher.Account.Cred;
using XIVLauncher.CatHost;
using XIVLauncher.Common.Game;
using XIVLauncher.Login.Exceptions;
using XIVLauncher.Login.WeGame;
using Xunit;

namespace XIVLauncher.Test.CatHost;

/// <summary>
///     WeGame 号就地登录: 全部用假的本机环境和假的账号库, 不拉起 WeGame、不结束进程、不碰真实账号库
/// </summary>
public sealed class CatWeGameLoginCaptureTests
{
    private const string REQUESTED = "123456";
    private const string USER_ID   = "10000000000000001";
    private const string TOKEN     = "captured-token-AAAA";

    private const string UI_HINT = "请在这台电脑的 DcMiniLauncher 界面版里用 WeGame 方式重新登录一次";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly FakeEnvironment   environment = new();
    private readonly FakeStore         store       = new();
    private readonly RecordingReporter reporter    = new();
    private readonly CatLogRedactor    redactor    = new();
    private readonly List<string>      loginTokens = [];

    /// <summary>工作台外壳靠程序集里有没有这个类型名判断启动器是否支持就地登录, 名字和命名空间不能改</summary>
    [Fact]
    public void TypeNameIsStable()
    {
        Assert.Equal("XIVLauncher.CatHost.CatWeGameLoginCapture", typeof(CatWeGameLoginCapture).FullName);
        Assert.Same(typeof(CatInternationalGameRunner).Assembly, typeof(CatWeGameLoginCapture).Assembly);
        Assert.Equal("waitingWeGameLogin", CatStages.WAITING_WE_GAME_LOGIN);
    }

    [Fact]
    public void ClientProcessNames_NeverIncludeTheGameOrTheService()
    {
        Assert.Equal(["wegame", "wegame_env", "tgp_daemon", "rail"], CatWeGameLoginRealEnvironment.ClientProcessNames);
        Assert.DoesNotContain(CatWeGameLoginRealEnvironment.ClientProcessNames, x => x.Contains("ffxiv", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("wegameservice", CatWeGameLoginRealEnvironment.ClientProcessNames);
    }

    #region 三种情况都进入等待登录

    [Fact]
    public async Task NoRow_WaitsForLogin_ThenCreatesRowWithRequestedNumberAsNote()
    {
        environment.Result = new WeGameCaptureResult(USER_ID, TOKEN);
        var capture = Create();

        var row    = await capture.FindRowAsync(Request(true), reporter, CancellationToken.None);
        var result = await capture.LoginAsync(Request(true), row, reporter, LoginOk, CancellationToken.None);

        Assert.Equal(new CatWeGameRow(USER_ID, REQUESTED), row);
        Assert.Equal((USER_ID, TOKEN, (string?)REQUESTED), Assert.Single(store.Saved));
        Assert.Equal("ok:" + TOKEN, result);
        Assert.Equal([TOKEN], loginTokens);

        // 先确认没人在等、结束 WeGame 客户端, 拉起后才报等待; 取到后回到准备; 只等这一次
        Assert.Equal(["stop", "launch"], environment.Calls);
        Assert.Equal(["stage:waitingWeGameLogin", "stage:preparing"], reporter.Entries);
        Assert.Equal(0, store.ReadTokenCount);
    }

    [Fact]
    public async Task RowWithoutToken_WaitsForLogin_ThenUpdatesTheSameRow()
    {
        store.Add(USER_ID, "老王 QQ123456", null);
        environment.Result = new WeGameCaptureResult(USER_ID, TOKEN);
        var capture = Create();

        var row    = await capture.FindRowAsync(Request(true), reporter, CancellationToken.None);
        var result = await capture.LoginAsync(Request(true), row, reporter, LoginOk, CancellationToken.None);

        Assert.Equal("ok:" + TOKEN, result);
        Assert.Equal(["stage:waitingWeGameLogin", "stage:preparing"], reporter.Entries);

        // 已经对得上的行不动备注
        Assert.Equal((USER_ID, TOKEN, (string?)null), Assert.Single(store.Saved));
        Assert.Equal("老王 QQ123456", store.Rows.Single().Note);
    }

    [Fact]
    public async Task RejectedToken_IsCleared_ThenWaitsForLogin_AndLogsInWithTheNewOne()
    {
        store.Add(USER_ID, REQUESTED, "old-token-BBBB");
        environment.Result = new WeGameCaptureResult(USER_ID, TOKEN);
        var capture = Create();

        var row = await capture.FindRowAsync(Request(true), reporter, CancellationToken.None);
        var result = await capture.LoginAsync
                     (
                         Request(true),
                         row,
                         reporter,
                         (token, _) =>
                         {
                             loginTokens.Add(token);
                             return token == TOKEN ? Task.FromResult("ok") : throw Rejected();
                         },
                         CancellationToken.None
                     );

        Assert.Equal("ok", result);
        Assert.Equal(["old-token-BBBB", TOKEN], loginTokens);
        Assert.Equal([USER_ID], store.Cleared);
        Assert.Equal((USER_ID, TOKEN, (string?)null), Assert.Single(store.Saved));
        Assert.Equal(["stage:waitingWeGameLogin", "stage:preparing"], reporter.Entries);
    }

    [Fact]
    public async Task ExistingRowWithEmptyNote_GetsRequestedNumberAsNote()
    {
        // 员工在界面版登录过这个号但没填备注: 上号请求带的是 QQ 号, 按账号名和备注都找不到
        store.Add(USER_ID, "", "old-token-BBBB");
        environment.Result = new WeGameCaptureResult(USER_ID, TOKEN);

        var row = await Create().FindRowAsync(Request(true), reporter, CancellationToken.None);

        Assert.Equal(new CatWeGameRow(USER_ID, REQUESTED), row);
        Assert.Equal((USER_ID, TOKEN, (string?)REQUESTED), Assert.Single(store.Saved));
        Assert.Single(store.Rows);
    }

    #endregion

    #region 不进入等待登录的失败

    [Fact]
    public async Task FreshlyCapturedTokenRejected_DoesNotWaitAgain()
    {
        environment.Result = new WeGameCaptureResult(USER_ID, TOKEN);
        var capture = Create();
        var row     = await capture.FindRowAsync(Request(true), reporter, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<CatLaunchException>(() => capture.LoginAsync<string>(Request(true), row, reporter, (_, _) => throw Rejected(), CancellationToken.None));

        Assert.Equal(CatCodes.AUTHORIZATION_REQUIRED, ex.Code);
        Assert.Equal("刚在 WeGame 里登录的这个号被盛趣拒绝了, 请重新上号: 第三方验证失败（返回码 -10742165）", ex.Message);
        Assert.Equal(1, environment.CaptureCount);
        Assert.Equal([USER_ID], store.Cleared);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NetworkErrorOnLogin_NeverWaitsForLogin(bool weGameLogin)
    {
        store.Add(USER_ID, REQUESTED, "old-token-BBBB");
        var capture = Create();
        var row     = await capture.FindRowAsync(Request(weGameLogin), reporter, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<CatLaunchException>
                 (() => capture.LoginAsync<string>(Request(weGameLogin), row, reporter, (_, _) => throw new HttpRequestException("连接超时"), CancellationToken.None));

        Assert.Equal(CatCodes.NETWORK_ERROR, ex.Code);
        Assert.Equal("连不上盛趣登录服务器, 稍后重试即可: 连接超时", ex.Message);
        Assert.Equal(0, environment.CaptureCount);
        Assert.Empty(store.Cleared);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RiskControlOnLogin_NeverWaitsForLogin(bool weGameLogin)
    {
        store.Add(USER_ID, REQUESTED, "old-token-BBBB");
        var capture = Create();
        var row     = await capture.FindRowAsync(Request(weGameLogin), reporter, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<CatLaunchException>
                 (
                     () => capture.LoginAsync<string>
                     (
                         Request(weGameLogin),
                         row,
                         reporter,
                         (_, _) => throw new LoginException((int)LoginExceptionCode.RiskEnvironment, "登录环境存在风险"),
                         CancellationToken.None
                     )
                 );

        Assert.Equal(CatCodes.RISK_CONTROL, ex.Code);
        Assert.Equal(0, environment.CaptureCount);
        Assert.Empty(store.Cleared);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AmbiguousNote_NeverWaitsForLogin(bool weGameLogin)
    {
        store.Add("10000000000000001", "大号 123456", "t1-AAAAAA");
        store.Add("10000000000000002", "小号(123456)", "t2-AAAAAA");

        var ex = await Assert.ThrowsAsync<CatLaunchException>(() => Create().FindRowAsync(Request(weGameLogin), reporter, CancellationToken.None));

        Assert.Equal(CatCodes.AUTHORIZATION_REQUIRED, ex.Code);
        Assert.Equal("DcMiniLauncher 里有多个 WeGame 号的备注写着 123456，请只留一个", ex.Message);
        Assert.Equal(0, environment.CaptureCount);
    }

    [Fact]
    public async Task CapturedUserBelongsToAnotherCustomer_IsRefused_AndNothingIsSaved()
    {
        store.Add(USER_ID, "654321", "other-token-CCCC");
        environment.Result = new WeGameCaptureResult(USER_ID, TOKEN);

        var ex = await Assert.ThrowsAsync<CatLaunchException>(() => Create().FindRowAsync(Request(true), reporter, CancellationToken.None));

        Assert.Equal(CatCodes.AUTHORIZATION_REQUIRED, ex.Code);
        Assert.Equal("登录的 WeGame 账号已经绑定了客户 654321，请确认登录的是不是这个号", ex.Message);
        Assert.Empty(store.Saved);
        Assert.Equal("654321", store.Rows.Single().Note);
        Assert.Equal("other-token-CCCC", store.Tokens[USER_ID]);
    }

    [Fact]
    public async Task CapturedUserIsNotTheRowBoundToThisNumber_IsRefused_AndNothingIsSaved()
    {
        store.Add(USER_ID, REQUESTED, null);
        environment.Result = new WeGameCaptureResult("10000000000000009", TOKEN);
        var capture = Create();
        var row     = await capture.FindRowAsync(Request(true), reporter, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<CatLaunchException>(() => capture.LoginAsync(Request(true), row, reporter, LoginOk, CancellationToken.None));

        Assert.Equal(CatCodes.AUTHORIZATION_REQUIRED, ex.Code);
        Assert.Equal("登录的 WeGame 账号不是 DcMiniLauncher 里记着 123456 的那一个，请确认登录的是不是这个号", ex.Message);
        Assert.Empty(store.Saved);
        Assert.Empty(loginTokens);
        Assert.Single(store.Rows);
    }

    [Fact]
    public async Task AnotherWaitInProgressOnThisMachine_ReportsBusy_WithoutTouchingWeGame()
    {
        environment.Failure = new WeGameCapturePipeBusyException(new IOException("All pipe instances are busy."));

        var ex = await Assert.ThrowsAsync<CatLaunchException>(() => Create().FindRowAsync(Request(true), reporter, CancellationToken.None));

        Assert.Equal(CatCodes.BUSY, ex.Code);
        Assert.Equal("这台电脑正在等另一个 WeGame 号登录，请先完成或取消那一个", ex.Message);

        // 没轮到自己: 不结束 WeGame 客户端（那会打断正在等的那个号）, 不报等待
        Assert.Empty(environment.Calls);
        Assert.Empty(reporter.Entries);
        Assert.Empty(store.Saved);
    }

    [Fact]
    public async Task NobodyLogsIn_TimesOut()
    {
        var capture = new CatWeGameLoginCapture(environment, store, redactor) { LoginTimeout = TimeSpan.FromMilliseconds(100) };

        var ex = await Assert.ThrowsAsync<CatLaunchException>(() => capture.FindRowAsync(Request(true), reporter, CancellationToken.None)).WaitAsync(Timeout);

        Assert.Equal(CatCodes.AUTHORIZATION_REQUIRED, ex.Code);
        Assert.StartsWith("WeGame 等了 ", ex.Message);
        Assert.EndsWith(" 分钟没有登录，请重新上号", ex.Message);
        Assert.Equal(["stage:waitingWeGameLogin"], reporter.Entries);
        Assert.Empty(store.Saved);
    }

    [Fact]
    public void DefaultTimeout_IsTenMinutes()
    {
        var capture = Create();

        Assert.Equal(TimeSpan.FromMinutes(10), capture.LoginTimeout);
        Assert.Equal("WeGame 等了 10 分钟没有登录，请重新上号", CatWeGameLoginCapture.TimeoutMessage(capture.LoginTimeout));
    }

    [Fact]
    public async Task CloseWhileWaiting_IsCancelled_NotAFailure()
    {
        using var close = new CancellationTokenSource();
        var run = Create().FindRowAsync(Request(true), reporter, close.Token);

        await environment.Waiting.Task.WaitAsync(Timeout);
        Assert.False(run.IsCompleted);
        await close.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run).WaitAsync(Timeout);
        Assert.Equal(["stage:waitingWeGameLogin"], reporter.Entries);
        Assert.Empty(store.Saved);
    }

    [Fact]
    public async Task GameDirectoryNotConfigured_ReportsInvalidGamePath_WithoutLaunchingWeGame()
    {
        environment.SdologinDir = null;

        var ex = await Assert.ThrowsAsync<CatLaunchException>(() => Create().FindRowAsync(Request(true), reporter, CancellationToken.None));

        Assert.Equal(CatCodes.INVALID_GAME_PATH, ex.Code);
        Assert.Equal("DcMiniLauncher 设置里的 WeGame 版游戏目录无效, 请在界面版「设置」里重新选择", ex.Message);
        Assert.Equal(0, environment.CaptureCount);
        Assert.Empty(environment.Calls);
        Assert.Empty(reporter.Entries);
    }

    [Fact]
    public async Task VersionDllNotWritable_ReportsInvalidGamePath_WithoutElevation()
    {
        environment.Failure = new VersionDllPermissionDeniedException(@"X:\res\version.dll", @"X:\game\sdo\sdologin\version.dll", new UnauthorizedAccessException());

        var ex = await Assert.ThrowsAsync<CatLaunchException>(() => Create().FindRowAsync(Request(true), reporter, CancellationToken.None));

        Assert.Equal(CatCodes.INVALID_GAME_PATH, ex.Code);
        Assert.Equal("请用管理员身份打开一次 DcMiniLauncher 界面版完成 WeGame 设置", ex.Message);
        Assert.Empty(environment.Calls);
        Assert.Empty(reporter.Entries);
    }

    [Fact]
    public async Task StoppingWeGameClientFails_StillWaitsForLogin()
    {
        environment.StopFailure = new InvalidOperationException("拒绝访问");
        environment.Result      = new WeGameCaptureResult(USER_ID, TOKEN);

        var row = await Create().FindRowAsync(Request(true), reporter, CancellationToken.None);

        Assert.Equal(USER_ID, row.UserName);
    }

    [Fact]
    public async Task CapturedToken_IsRedacted()
    {
        environment.Result = new WeGameCaptureResult(USER_ID, TOKEN);

        await Create().FindRowAsync(Request(true), reporter, CancellationToken.None);

        Assert.Equal("登录信息 ***", redactor.Redact($"登录信息 {TOKEN}"));
    }

    #endregion

    #region 旧请求（不带 weGameLogin）

    [Fact]
    public async Task Legacy_NoRow_FailsWithTheOriginalMessage()
    {
        var ex = await Assert.ThrowsAsync<CatLaunchException>(() => Create().FindRowAsync(Request(false), reporter, CancellationToken.None));

        Assert.Equal(CatCodes.AUTHORIZATION_REQUIRED, ex.Code);
        Assert.Equal
        (
            "DcMiniLauncher 账号库里没有这个 WeGame 号：先在这台电脑的 DcMiniLauncher 界面版里登录一次，并把这个号的备注填上客户的 QQ 号或手机号（123456）",
            ex.Message
        );
        AssertNeverTouchedWeGame();
    }

    [Fact]
    public async Task Legacy_RowWithoutToken_FailsWithTheOriginalMessage()
    {
        store.Add(USER_ID, REQUESTED, null);
        var capture = Create();
        var row     = await capture.FindRowAsync(Request(false), reporter, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<CatLaunchException>(() => capture.LoginAsync(Request(false), row, reporter, LoginOk, CancellationToken.None));

        Assert.Equal(CatCodes.AUTHORIZATION_REQUIRED, ex.Code);
        Assert.Equal($"这个 WeGame 号还没在这台电脑登录过, {UI_HINT}", ex.Message);
        Assert.Empty(loginTokens);
        AssertNeverTouchedWeGame();
    }

    [Fact]
    public async Task Legacy_RejectedToken_IsCleared_AndFailsWithTheOriginalMessage()
    {
        store.Add(USER_ID, REQUESTED, "old-token-BBBB");
        var capture = Create();
        var row     = await capture.FindRowAsync(Request(false), reporter, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<CatLaunchException>(() => capture.LoginAsync<string>(Request(false), row, reporter, (_, _) => throw Rejected(), CancellationToken.None));

        Assert.Equal(CatCodes.AUTHORIZATION_REQUIRED, ex.Code);
        Assert.Equal($"这个 WeGame 号的登录已失效, {UI_HINT}: 第三方验证失败（返回码 -10742165）", ex.Message);
        Assert.Equal([USER_ID], store.Cleared);
        AssertNeverTouchedWeGame();
    }

    [Fact]
    public async Task Legacy_SavedTokenStillWorks_LogsInDirectly()
    {
        store.Add(USER_ID, REQUESTED, "old-token-BBBB");
        var capture = Create();
        var row     = await capture.FindRowAsync(Request(false), reporter, CancellationToken.None);

        Assert.Equal("ok:old-token-BBBB", await capture.LoginAsync(Request(false), row, reporter, LoginOk, CancellationToken.None));
        AssertNeverTouchedWeGame();
    }

    [Fact]
    public async Task SavedTokenStillWorks_WithWeGameLogin_DoesNotOpenWeGame()
    {
        store.Add(USER_ID, REQUESTED, "old-token-BBBB");
        var capture = Create();
        var row     = await capture.FindRowAsync(Request(true), reporter, CancellationToken.None);

        Assert.Equal("ok:old-token-BBBB", await capture.LoginAsync(Request(true), row, reporter, LoginOk, CancellationToken.None));
        AssertNeverTouchedWeGame();
    }

    #endregion

    #region 账号库（临时目录里的真实实现）

    [Fact]
    public async Task Store_SavesNewRow_LikeTheOneTheUiCreates_AndLeavesOtherRowsAlone()
    {
        using var library = await TempLibrary.CreateAsync();
        library.Manager.AddAccount(NewAccount("sdoUser", XIVAccountType.Sdo, "别的号"));

        var accountStore = new CatWeGameAccountStore(library.Manager);
        Assert.Empty(accountStore.ListRows());
        Assert.Empty(accountStore.ReloadRows());

        var row = await accountStore.SaveCapturedAsync(USER_ID, TOKEN, REQUESTED);

        Assert.Equal(new CatWeGameRow(USER_ID, REQUESTED), row);
        Assert.Equal(TOKEN, await accountStore.ReadTokenAsync(row));

        // 另开一个账号库实例（当作界面版或下一次上号的进程）也读得到
        var reopened = library.Reopen();
        var saved    = reopened.FindAccount(USER_ID, XIVAccountType.WeGame)!;
        Assert.Equal(REQUESTED, saved.UserDefinedName);
        Assert.Equal(USER_ID, saved.WeGameLoginAccount);
        Assert.True(saved.QuickLoginEnabled);
        Assert.False(saved.DeviceProfileDynamicEnabled);
        Assert.False(string.IsNullOrEmpty(saved.WeGameQuickLoginSecret));
        Assert.Equal($"{USER_ID}|WeGame", saved.ID);

        var other = reopened.FindAccount("sdoUser", XIVAccountType.Sdo)!;
        Assert.Equal("别的号", other.UserDefinedName);
        Assert.Equal(0, other.SortOrder);
        Assert.Equal(2, reopened.Accounts.Count);

        // 下一次上号按备注找得到这一行
        var nextStore = new CatWeGameAccountStore(reopened);
        Assert.Equal(row, Assert.Single(nextStore.ListRows()));
    }

    [Fact]
    public async Task Store_UpdatesRowAddedByAnotherProcess_AndKeepsItsNoteWhenAsked()
    {
        using var library = await TempLibrary.CreateAsync();
        var accountStore = new CatWeGameAccountStore(library.Manager);
        Assert.Empty(accountStore.ListRows());

        // 本进程加载之后, 界面版登录了这个号并填了备注
        var ui = library.Reopen();
        ui.AddAccount(NewAccount(USER_ID, XIVAccountType.WeGame, "老王 QQ123456"));

        var reloaded = Assert.Single(accountStore.ReloadRows());
        Assert.Equal(new CatWeGameRow(USER_ID, "老王 QQ123456"), reloaded);

        await accountStore.SaveCapturedAsync(USER_ID, TOKEN, null);

        var saved = library.Reopen().FindAccount(USER_ID, XIVAccountType.WeGame)!;
        Assert.Equal("老王 QQ123456", saved.UserDefinedName);
        Assert.Equal("initial-area", saved.AreaName);
        Assert.Equal(TOKEN, await library.Manager.Decrypt(saved.WeGameQuickLoginSecret));
    }

    [Fact]
    public async Task Store_ClearToken_OnlyClearsTheTokenItRead()
    {
        using var library = await TempLibrary.CreateAsync();
        var existing = NewAccount(USER_ID, XIVAccountType.WeGame, REQUESTED);
        existing.WeGameQuickLoginSecret = "old-token-BBBB";
        library.Manager.AddAccount(existing);

        var accountStore = new CatWeGameAccountStore(library.Manager);
        var row          = Assert.Single(accountStore.ListRows());
        Assert.Equal("old-token-BBBB", await accountStore.ReadTokenAsync(row));

        // 期间界面版存了新的: 不清
        var ui     = library.Reopen();
        var uiSide = ui.FindAccount(USER_ID, XIVAccountType.WeGame)!;
        uiSide.WeGameQuickLoginSecret = "new-token-from-ui";
        ui.Save(uiSide);

        accountStore.ClearToken(row);
        Assert.Equal("new-token-from-ui", library.Reopen().FindAccount(USER_ID, XIVAccountType.WeGame)!.WeGameQuickLoginSecret);

        // 读到的就是被拒的那一枚: 清掉, 备注还在
        Assert.Equal("new-token-from-ui", await accountStore.ReadTokenAsync(row));
        accountStore.ClearToken(row);

        var cleared = library.Reopen().FindAccount(USER_ID, XIVAccountType.WeGame)!;
        Assert.Null(cleared.WeGameQuickLoginSecret);
        Assert.Equal(REQUESTED, cleared.UserDefinedName);
    }

    #endregion

    private CatWeGameLoginCapture Create() =>
        new(environment, store, redactor);

    private static CatLaunchRequest Request(bool weGameLogin) =>
        new("op", REQUESTED, false, null, null, Platform: XIVAccountType.WeGame, WeGameLogin: weGameLogin);

    private Task<string> LoginOk(string token, CancellationToken cancellationToken)
    {
        loginTokens.Add(token);
        return Task.FromResult("ok:" + token);
    }

    private static LoginException Rejected() =>
        new((int)LoginExceptionCode.ThirdPartyVerificationFailed, "第三方验证失败");

    private void AssertNeverTouchedWeGame()
    {
        Assert.Equal(0, environment.CaptureCount);
        Assert.Empty(environment.Calls);
        Assert.Empty(reporter.Entries);
        Assert.Empty(store.Saved);
        Assert.Equal(0, store.ReloadCount);
    }

    private static XIVAccount NewAccount(string userName, XIVAccountType type, string note)
    {
        var account = new XIVAccount
        {
            AccountType        = type,
            SdoLoginAccount    = userName,
            WeGameLoginAccount = userName,
            UserDefinedName    = note,
            AreaName           = "initial-area"
        };
        account.GenerateID();
        return account;
    }

    /// <summary>
    ///     假的本机环境: 不拉起 WeGame、不结束任何进程; 没给结果时一直等到被取消
    /// </summary>
    private sealed class FakeEnvironment : ICatWeGameLoginEnvironment
    {
        public string? SdologinDir { get; set; } = @"X:\fake\FF14\sdo\sdologin";

        public WeGameCaptureResult? Result { get; set; }

        public Exception? Failure { get; set; }

        public Exception? StopFailure { get; set; }

        public List<string> Calls { get; } = [];

        public int CaptureCount { get; private set; }

        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string? FindSdologinDir() => SdologinDir;

        public void StopWeGameClient()
        {
            Calls.Add("stop");

            if (StopFailure != null)
                throw StopFailure;
        }

        public async Task<WeGameCaptureResult> CaptureAsync(string sdologinDir, Action beforeLaunch, Action afterLaunch, CancellationToken cancellationToken)
        {
            CaptureCount++;
            Assert.Equal(SdologinDir, sdologinDir);

            // 与真实实现同一个顺序: 先占住管道（占不到或写不进目录就在这里失败）, 再结束客户端、拉起 WeGame
            if (Failure != null)
                throw Failure;

            beforeLaunch();
            Calls.Add("launch");
            afterLaunch();
            Waiting.TrySetResult();

            if (Result != null)
                return Result;

            await Task.Delay(System.Threading.Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("不会走到这里");
        }
    }

    /// <summary>
    ///     内存里的假账号库
    /// </summary>
    private sealed class FakeStore : ICatWeGameAccountStore
    {
        public List<CatWeGameRow> Rows { get; } = [];

        public Dictionary<string, string?> Tokens { get; } = new(StringComparer.Ordinal);

        public List<(string UserId, string Token, string? Note)> Saved { get; } = [];

        public List<string> Cleared { get; } = [];

        public int ReloadCount { get; private set; }

        public int ReadTokenCount { get; private set; }

        public void Add(string userName, string? note, string? token)
        {
            Rows.Add(new CatWeGameRow(userName, note));
            Tokens[userName] = token;
        }

        public IReadOnlyList<CatWeGameRow> ListRows() => Rows.ToArray();

        public IReadOnlyList<CatWeGameRow> ReloadRows()
        {
            ReloadCount++;
            return Rows.ToArray();
        }

        public Task<string?> ReadTokenAsync(CatWeGameRow row)
        {
            ReadTokenCount++;
            return Task.FromResult(Tokens.GetValueOrDefault(row.UserName));
        }

        public void ClearToken(CatWeGameRow row)
        {
            Cleared.Add(row.UserName);
            Tokens[row.UserName] = null;
        }

        public Task<CatWeGameRow> SaveCapturedAsync(string userId, string token, string? note)
        {
            Saved.Add((userId, token, note));

            var index = Rows.FindIndex(x => x.UserName == userId);
            var row   = new CatWeGameRow(userId, note ?? (index < 0 ? null : Rows[index].Note));

            if (index < 0)
                Rows.Add(row);
            else
                Rows[index] = row;

            Tokens[userId] = token;
            return Task.FromResult(row);
        }
    }

    /// <summary>
    ///     临时目录里的账号库, 不加密（不碰系统凭据管理器）
    /// </summary>
    private sealed class TempLibrary : IDisposable
    {
        private readonly string roamingPath = Path.Combine(Path.GetTempPath(), "dml-wegame-login-tests", Guid.NewGuid().ToString("N"));

        private TempLibrary() =>
            Directory.CreateDirectory(roamingPath);

        public AccountManager Manager { get; private set; } = null!;

        public static async Task<TempLibrary> CreateAsync()
        {
            var library = new TempLibrary();
            library.Manager = new AccountManager(new SettingsStore(), library.roamingPath);
            Assert.True((await library.Manager.ChangeCredTypeAsync(CredType.NoEncryption)).Succeeded);
            return library;
        }

        public AccountManager Reopen() =>
            new(new SettingsStore(), roamingPath);

        public void Dispose()
        {
            try
            {
                Directory.Delete(roamingPath, true);
            }
            catch (Exception)
            {
                // 数据库连接由终结器释放, 目录可能暂时删不掉
            }
        }

        private sealed class SettingsStore : IAccountSettingsStore
        {
            public string CurrentAccountID { get; set; } = string.Empty;
        }
    }
}
