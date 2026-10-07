using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using XIVLauncher.CatHost;
using XIVLauncher.Common.Game;
using XIVLauncher.Login.WeGame;
using Xunit;

namespace XIVLauncher.Test.CatHost;

/// <summary>
///     等 WeGame 登录期间看窗口、报验证、自动切扫码页: 屏幕全部是假的, 不找真窗口、不截屏、不发点击
/// </summary>
public sealed class CatWeGameChallengeWatcherTests
{
    private const string QQ_LINK     = "https://txz.qq.com/p?k=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA&f=1";
    private const string QQ_LINK_2   = "https://txz.qq.com/p?k=BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB&f=1";
    private const string WECHAT_LINK = "https://open.weixin.qq.com/connect/confirm?uuid=CCCCCCCCCCCCCCCC";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];

    private readonly FakeScreen        screen   = new();
    private readonly RecordingReporter reporter = new();
    private readonly CatLogRedactor    redactor = new();

    /// <summary>工作台外壳靠程序集里有没有这个类型名判断启动器是否支持把验证转给客户, 名字和命名空间不能改</summary>
    [Fact]
    public void TypeNameIsStable()
    {
        Assert.Equal("XIVLauncher.CatHost.CatWeGameChallengeWatcher", typeof(CatWeGameChallengeWatcher).FullName);
        Assert.Same(typeof(CatWeGameLoginCapture).Assembly, typeof(CatWeGameChallengeWatcher).Assembly);
    }

    [Theory]
    [InlineData(null, true, null)]
    [InlineData("", true, null)]
    [InlineData("  ", true, null)]
    [InlineData("qq", true, CatWeGameScan.Qq)]
    [InlineData("QQ", true, CatWeGameScan.Qq)]
    [InlineData("weChat", true, CatWeGameScan.WeChat)]
    [InlineData(" wechat ", true, CatWeGameScan.WeChat)]
    [InlineData("weixin", false, null)]
    [InlineData("password", false, null)]
    public void Scan_TryParse(string? value, bool expectedOk, CatWeGameScan? expected)
    {
        Assert.Equal(expectedOk, CatWeGameScans.TryParse(value, out var scan));
        Assert.Equal(expected, scan);
        Assert.Equal("qq", CatWeGameScans.Name(CatWeGameScan.Qq));
        Assert.Equal("weChat", CatWeGameScans.Name(CatWeGameScan.WeChat));
    }

    #region 二维码（只看不点）

    [Fact]
    public async Task Qr_IsReportedOnce_AndAgainOnlyWhenItsContentChanges()
    {
        var watcher = Create(null);
        screen.Window = Qr(QQ_LINK);

        await TickAsync(watcher, 3);

        var first = Assert.Single(reporter.Challenges);
        Assert.Equal(new CatWeGameChallenge("qrcode", "q-1", Convert.ToBase64String(Png), QQ_LINK, 120), first);
        Assert.Equal(["challenge:qrcode:q-1"], reporter.Entries);

        // 过期刷新: 算新的验证, 不单独清旧的
        screen.Window = Qr(QQ_LINK_2);
        await TickAsync(watcher, 2);

        Assert.Equal(["challenge:qrcode:q-1", "challenge:qrcode:q-2"], reporter.Entries);
        Assert.Equal(QQ_LINK_2, reporter.Challenges.Last().Link);
        Assert.Empty(screen.Clicks);
    }

    [Fact]
    public async Task Qr_IsClearedOnlyAfterTwoMissesInARow()
    {
        var watcher = Create(null);
        screen.Window = Qr(QQ_LINK);
        await TickAsync(watcher, 1);

        // 重绘的一瞬间识别不到: 不算
        screen.Window = Page();
        await TickAsync(watcher, 1);
        screen.Window = Qr(QQ_LINK);
        await TickAsync(watcher, 1);
        Assert.Equal(["challenge:qrcode:q-1"], reporter.Entries);

        screen.Window = Page();
        await TickAsync(watcher, 1);
        Assert.Equal(["challenge:qrcode:q-1"], reporter.Entries);
        await TickAsync(watcher, 3);
        Assert.Equal(["challenge:qrcode:q-1", "cleared:q-1"], reporter.Entries);

        // 清掉之后同一个码又出现: 重新报
        screen.Window = Qr(QQ_LINK);
        await TickAsync(watcher, 1);
        Assert.Equal(["challenge:qrcode:q-1", "cleared:q-1", "challenge:qrcode:q-2"], reporter.Entries);
    }

    [Fact]
    public async Task Qr_IsCleared_WhenTheLoginWindowIsGone()
    {
        var watcher = Create(null);
        screen.Window = Qr(QQ_LINK);
        await TickAsync(watcher, 1);

        screen.Window = null;
        await TickAsync(watcher, 2);

        Assert.Equal(["challenge:qrcode:q-1", "cleared:q-1"], reporter.Entries);
    }

    [Fact]
    public async Task WithoutScan_NeverClicks_WhateverTheWindowShows()
    {
        var watcher = Create(null);

        screen.Window = Page();
        await TickAsync(watcher, 10);
        screen.Window = null;
        await TickAsync(watcher, 3);
        screen.Window = Qr(QQ_LINK);
        await TickAsync(watcher, 3);

        Assert.Empty(screen.Clicks);
        Assert.Equal(0, screen.ConfirmCount);
        Assert.Equal(["challenge:qrcode:q-1"], reporter.Entries);
    }

    [Fact]
    public async Task QrContent_IsRedacted_AndNotPrintedByToString()
    {
        screen.Window = Qr(QQ_LINK);
        await TickAsync(Create(null), 1);

        Assert.Equal("二维码 ***", redactor.Redact($"二维码 {QQ_LINK}"));
        Assert.DoesNotContain("txz.qq.com", reporter.Challenges.Single().ToString());
        Assert.DoesNotContain(Convert.ToBase64String(Png), reporter.Challenges.Single().ToString());
    }

    #endregion

    #region 自动切扫码页

    [Fact]
    public async Task Qq_ClicksTab_ThenScanEntry_AndStopsOnceQrShows()
    {
        // 停在账号密码页: 点页签没有二维码, 再点「QQ 扫码登录」才有
        screen.Window  = Page();
        screen.OnClick = (x, y) => (x, y) == (208, 630) ? Qr(QQ_LINK) : Page();
        var watcher = Create(CatWeGameScan.Qq);

        // 窗口刚出现的那一轮不点
        await TickAsync(watcher, 1);
        Assert.Empty(screen.Clicks);

        await TickAsync(watcher, 1);
        Assert.Equal([(125, 277), (150, 630), (208, 630)], screen.Clicks);
        Assert.Equal(["challenge:qrcode:q-1"], reporter.Entries);

        // 切好后不久二维码没了而登录窗口还在（WeGame 启动完自己换了页）: 再切一次, 换页后的二维码作为新的一条报
        screen.Window = Page();
        await TickAsync(watcher, 5);
        Assert.Equal([(125, 277), (150, 630), (208, 630), (125, 277), (150, 630), (208, 630)], screen.Clicks);
        Assert.Equal(["challenge:qrcode:q-1", "cleared:q-1"], reporter.Entries.Take(2));
    }

    [Fact]
    public async Task Qq_AnotherAppsQrOnTheWindow_IsNotTakenAsSwitched_NorReported()
    {
        // WeGame 刚启动先闪一下上次用的微信二维码: 点了 QQ 页签窗口还没反应, 看到的仍是微信的码
        screen.Window  = Qr(WECHAT_LINK);
        screen.OnClick = (x, y) => (x, y) == (208, 630) ? Qr(QQ_LINK) : Qr(WECHAT_LINK);
        var watcher = Create(CatWeGameScan.Qq);

        await TickAsync(watcher, 2);

        Assert.Equal([(125, 277), (150, 630), (208, 630)], screen.Clicks);
        Assert.Equal(["challenge:qrcode:q-1"], reporter.Entries);
        Assert.Equal(CatWeGameScan.WeChat, CatWeGameChallengeWatcher.QrAppOf(WECHAT_LINK));
        Assert.Equal(CatWeGameScan.Qq, CatWeGameChallengeWatcher.QrAppOf(QQ_LINK));
        Assert.Null(CatWeGameChallengeWatcher.QrAppOf("https://example.invalid/x"));
    }

    [Fact]
    public async Task WindowThatIgnoresClicks_IsRetriedWithoutCountingRounds_UntilItResponds()
    {
        // WeGame 刚启动: 窗口在了但点了画面不变。不计轮数、不报切换失败; 等它开始响应后照常切好
        var frozen = new CatWeGameLoginWindow(1210, 680, PanelThumb: [10, 10, 200, 10]);
        screen.Window  = frozen;
        screen.OnClick = (_, _) => frozen;
        var watcher = Create(CatWeGameScan.Qq);

        await TickAsync(watcher, 8);

        Assert.Empty(reporter.Entries);
        Assert.True(screen.Clicks.Count > 9, "超过三轮仍在重试");

        screen.OnClick = (x, y) => (x, y) == (208, 630) ? Qr(QQ_LINK) : new CatWeGameLoginWindow(1210, 680, PanelThumb: [10, 200, 10, 10]);
        await TickAsync(watcher, 1);

        Assert.Equal(["challenge:qrcode:q-1"], reporter.Entries);
    }

    [Fact]
    public async Task ExpiredQr_IsRefreshedByClickingIt_AndTheNewOneIsReported()
    {
        screen.Window  = Qr(QQ_LINK);
        screen.OnClick = (_, _) => Qr(QQ_LINK);
        var watcher = Create(CatWeGameScan.Qq);
        await TickAsync(watcher, 2);
        var clicks = screen.Clicks.Count;

        // 失效: 变暗的那张不带内容; 点正中的刷新图标后出新码, 直接作为新的一条报, 中间不撤
        screen.Window  = new CatWeGameLoginWindow(1210, 680, QrExpired: true);
        screen.OnClick = (x, y) => (x, y) == (150, 379) ? Qr(QQ_LINK + "2") : new CatWeGameLoginWindow(1210, 680, QrExpired: true);
        await TickAsync(watcher, 1);

        Assert.Equal((150, 379), screen.Clicks.Last());
        Assert.Equal(clicks + 1, screen.Clicks.Count);
        Assert.Equal(["challenge:qrcode:q-1", "challenge:qrcode:q-2"], reporter.Entries);
    }

    [Fact]
    public async Task ExpiredQr_WithoutScanRequested_IsNeverClicked()
    {
        screen.Window = new CatWeGameLoginWindow(1210, 680, QrExpired: true);
        await TickAsync(Create(null), 4);

        Assert.Empty(screen.Clicks);
    }

    [Fact]
    public async Task Qq_QrGoneLongAfterTheSwitch_IsLeftAlone()
    {
        // 隔得久了二维码才没（员工自己换了登录方式）: 不再点
        screen.Window  = Page();
        screen.OnClick = (x, y) => (x, y) == (208, 630) ? Qr(QQ_LINK) : Page();
        var watcher = new CatWeGameChallengeWatcher(screen, reporter, redactor, CatWeGameScan.Qq) { ClickSettle = TimeSpan.Zero, SettleTicks = 2, ResettleWindow = TimeSpan.FromMilliseconds(-1) };

        await TickAsync(watcher, 2);
        Assert.Equal(3, screen.Clicks.Count);

        screen.Window = Page();
        await TickAsync(watcher, 5);
        Assert.Equal(3, screen.Clicks.Count);
        Assert.Equal(["challenge:qrcode:q-1", "cleared:q-1"], reporter.Entries);
    }

    [Fact]
    public async Task Qq_AlreadyOnScanPage_ClicksOnlyTheTab()
    {
        // 上次用的就是扫码: 点完页签已经有二维码, 再点同一位置的「QQ 账号密码登录」会切走, 所以不能点
        screen.Window  = Page();
        screen.OnClick = (_, _) => Qr(QQ_LINK);
        var watcher = Create(CatWeGameScan.Qq);

        await TickAsync(watcher, 4);

        Assert.Equal([(125, 277)], screen.Clicks);
        Assert.Equal(["challenge:qrcode:q-1"], reporter.Entries);
    }

    [Fact]
    public async Task QrOfTheOtherMethod_ShownBeforeSwitching_IsNotReported()
    {
        // 窗口停在微信二维码, 要的是 QQ: 仍然先点 QQ 页签, 微信的码不发给客户
        screen.Window  = Qr(WECHAT_LINK);
        screen.OnClick = (_, _) => Qr(QQ_LINK);
        var watcher = Create(CatWeGameScan.Qq);

        await TickAsync(watcher, 3);

        Assert.Equal([(125, 277)], screen.Clicks);
        Assert.Equal(QQ_LINK, Assert.Single(reporter.Challenges).Link);
    }

    [Fact]
    public async Task WeChat_ClicksTab_ThenOtherAccount_WhenQuickLoginIsShown()
    {
        screen.Window  = Page();
        screen.OnClick = (x, y) => (x, y) == (150, 483) ? Qr(WECHAT_LINK) : Page();
        var watcher = Create(CatWeGameScan.WeChat);

        await TickAsync(watcher, 4);

        Assert.Equal([(175, 277), (150, 483)], screen.Clicks);
        Assert.Equal(WECHAT_LINK, Assert.Single(reporter.Challenges).Link);
    }

    [Fact]
    public async Task WeChat_QrRightAfterTab_ClicksOnlyTheTab()
    {
        screen.Window  = Page();
        screen.OnClick = (_, _) => Qr(WECHAT_LINK);

        await TickAsync(Create(CatWeGameScan.WeChat), 4);

        Assert.Equal([(175, 277)], screen.Clicks);
    }

    [Fact]
    public async Task ClickPositions_AreScaledToTheActualWindowSize()
    {
        screen.Window  = new CatWeGameLoginWindow(2420, 1020);
        screen.OnClick = (x, y) => (x, y) == (416, 945) ? new CatWeGameLoginWindow(2420, 1020, QQ_LINK, Png) : new CatWeGameLoginWindow(2420, 1020);

        await TickAsync(Create(CatWeGameScan.Qq), 2);

        Assert.Equal([(250, 416), (300, 945), (416, 945)], screen.Clicks);
        Assert.Equal((125, 277), CatWeGameChallengeWatcher.Scale((125, 277), 1210, 680));
        Assert.Equal((63, 139), CatWeGameChallengeWatcher.Scale((125, 277), 605, 340));
    }

    [Fact]
    public async Task ThreeRoundsWithoutQr_ReportsSwitchFailedOnce_ThenOnlyObserves()
    {
        screen.Window = Page();
        var watcher = Create(CatWeGameScan.Qq);

        await TickAsync(watcher, 10);

        Assert.Equal
        (
            [(125, 277), (150, 630), (208, 630), (125, 277), (150, 630), (208, 630), (125, 277), (150, 630), (208, 630)],
            screen.Clicks
        );
        Assert.Equal(["scanSwitchFailed:qq"], reporter.Entries);

        // 员工手动切到了扫码页: 照常报, 不再点
        screen.Window = Qr(QQ_LINK);
        await TickAsync(watcher, 2);

        Assert.Equal(9, screen.Clicks.Count);
        Assert.Equal(["scanSwitchFailed:qq", "challenge:qrcode:q-1"], reporter.Entries);
    }

    [Fact]
    public async Task SwitchFailed_ReportsTheRequestedScan()
    {
        screen.Window = Page();

        await TickAsync(Create(CatWeGameScan.WeChat), 10);

        Assert.Equal(["scanSwitchFailed:weChat"], reporter.Entries);
        Assert.Equal(6, screen.Clicks.Count);
    }

    [Fact]
    public async Task WindowGoneWhileClicking_DoesNotCountAsARound()
    {
        // 员工自己登录了, 登录窗口没了: 不点、不报切换失败
        screen.Window    = Page();
        screen.ClickFails = true;
        var watcher = Create(CatWeGameScan.Qq);

        await TickAsync(watcher, 12);

        Assert.DoesNotContain(reporter.Entries, x => x.StartsWith("scanSwitchFailed", StringComparison.Ordinal));

        screen.Window = null;
        await TickAsync(watcher, 5);
        Assert.Empty(reporter.Entries);
    }

    [Fact]
    public async Task NoLoginWindowYet_DoesNotClick()
    {
        var watcher = Create(CatWeGameScan.Qq);

        await TickAsync(watcher, 5);

        Assert.Empty(screen.Clicks);
        Assert.Empty(reporter.Entries);
    }

    #endregion

    #region 设备验证短信

    [Fact]
    public async Task Sms_AppearsThenConfirmed_AndWindowGone_IsCleared()
    {
        var watcher = Create(null);
        screen.Sms = new CatWeGameSmsPrompt("AQLLJCAQMS", "1069070069", "请编辑手机短信：AQLLJCAQMS 发送到号码：1069070069");

        await TickAsync(watcher, 2);

        Assert.Equal
        (
            new CatWeGameChallenge("sms", "s-1", Code: "AQLLJCAQMS", Phone: "1069070069", Text: "请编辑手机短信：AQLLJCAQMS 发送到号码：1069070069"),
            Assert.Single(reporter.Challenges)
        );

        // 编号对不上的不点
        var stale = watcher.ConfirmSms("s-9");
        Assert.False(stale.Accepted);
        Assert.Equal(CatCodes.NOT_RUNNING, stale.Code);
        Assert.Equal(0, screen.ConfirmCount);

        Assert.True(watcher.ConfirmSms("s-1").Accepted);
        Assert.Equal(1, screen.ConfirmCount);

        // 还在等结果时客户又点了一次「我已发送」: 不重复点
        Assert.True(watcher.ConfirmSms("s-1").Accepted);
        Assert.Equal(1, screen.ConfirmCount);

        screen.Sms = null;
        await TickAsync(watcher, 1);

        Assert.Equal(["challenge:sms:s-1", "cleared:s-1"], reporter.Entries);
        Assert.Equal(CatCodes.NOT_RUNNING, watcher.ConfirmSms("s-1").Code);
    }

    [Fact]
    public async Task Sms_ConfirmedButWindowStays_ReportsNotPassed_AndCanBeConfirmedAgain()
    {
        var watcher = Create(null);
        screen.Sms = new CatWeGameSmsPrompt("AQLLJCAQMS", "1069070069", "原文");
        await TickAsync(watcher, 1);

        Assert.True(watcher.ConfirmSms("s-1").Accepted);

        // 间隔 2 秒、等 5 秒: 第 3 轮窗口还在才算没通过
        await TickAsync(watcher, 2);
        Assert.Equal(["challenge:sms:s-1"], reporter.Entries);
        await TickAsync(watcher, 1);
        Assert.Equal(["challenge:sms:s-1", "smsResult:s-1:notPassed"], reporter.Entries);

        await TickAsync(watcher, 3);
        Assert.Equal(2, reporter.Entries.Count);

        Assert.True(watcher.ConfirmSms("s-1").Accepted);
        Assert.Equal(2, screen.ConfirmCount);
    }

    [Fact]
    public async Task Sms_ContentChangesAfterConfirm_ReportsNotPassed_ThenTheNewOne()
    {
        var watcher = Create(null);
        screen.Sms = new CatWeGameSmsPrompt("AQLLJCAQMS", "1069070069", "原文一");
        await TickAsync(watcher, 1);
        Assert.True(watcher.ConfirmSms("s-1").Accepted);

        screen.Sms = new CatWeGameSmsPrompt("ZZTOPNEWCODE", "1069070069", "原文二");
        await TickAsync(watcher, 1);

        Assert.Equal(["challenge:sms:s-1", "smsResult:s-1:notPassed", "challenge:sms:s-2"], reporter.Entries);
        Assert.Equal("ZZTOPNEWCODE", reporter.Challenges.Last().Code);
        Assert.Equal(CatCodes.NOT_RUNNING, watcher.ConfirmSms("s-1").Code);
    }

    [Fact]
    public async Task Sms_ContentChangesWithoutConfirm_ReportsOnlyTheNewOne()
    {
        var watcher = Create(null);
        screen.Sms = new CatWeGameSmsPrompt("AQLLJCAQMS", "1069070069", "原文一");
        await TickAsync(watcher, 1);

        screen.Sms = new CatWeGameSmsPrompt("ZZTOPNEWCODE", "1069070069", "原文二");
        await TickAsync(watcher, 2);

        Assert.Equal(["challenge:sms:s-1", "challenge:sms:s-2"], reporter.Entries);
    }

    [Fact]
    public async Task Sms_HandledByStaffWithoutConfirm_IsClearedAfterTwoMisses()
    {
        var watcher = Create(null);
        screen.Sms = new CatWeGameSmsPrompt("AQLLJCAQMS", "1069070069", "原文");
        await TickAsync(watcher, 1);

        screen.Sms = null;
        await TickAsync(watcher, 1);
        Assert.Equal(["challenge:sms:s-1"], reporter.Entries);
        await TickAsync(watcher, 1);
        Assert.Equal(["challenge:sms:s-1", "cleared:s-1"], reporter.Entries);
    }

    [Fact]
    public async Task Sms_ConfirmWhenWindowAlreadyGone_ReturnsNotRunning()
    {
        var watcher = Create(null);
        screen.Sms = new CatWeGameSmsPrompt("AQLLJCAQMS", "1069070069", "原文");
        await TickAsync(watcher, 1);

        screen.ConfirmFails = true;
        var result = watcher.ConfirmSms("s-1");

        Assert.False(result.Accepted);
        Assert.Equal(CatCodes.NOT_RUNNING, result.Code);
    }

    [Fact]
    public async Task Sms_CodeIsRedacted()
    {
        screen.Sms = new CatWeGameSmsPrompt("AQLLJCAQMS", "1069070069", "原文");
        await TickAsync(Create(null), 1);

        Assert.Equal("短信 ***", redactor.Redact("短信 AQLLJCAQMS"));
        Assert.DoesNotContain("AQLLJCAQMS", reporter.Challenges.Single().ToString());
    }

    [Fact]
    public void ConfirmSms_BeforeAnyPrompt_ReturnsNotRunning()
    {
        var result = Create(null).ConfirmSms("s-1");

        Assert.False(result.Accepted);
        Assert.Equal(CatCodes.NOT_RUNNING, result.Code);
        Assert.Equal(0, screen.ConfirmCount);
    }

    #endregion

    #region 运行与停止

    [Fact]
    public async Task Run_WhenCancelled_ClearsOutstandingChallenges_AndStopsLooking()
    {
        screen.Window = Qr(QQ_LINK);
        screen.Sms    = new CatWeGameSmsPrompt("AQLLJCAQMS", "1069070069", "原文");

        using var stop = new CancellationTokenSource();
        var watcher = new CatWeGameChallengeWatcher(screen, reporter, redactor, null) { Interval = TimeSpan.FromMilliseconds(10) };
        var run     = watcher.RunAsync(stop.Token);

        await WaitUntilAsync(() => reporter.Entries.Count >= 2);
        await stop.CancelAsync();
        await run.WaitAsync(Timeout);

        Assert.Equal(["challenge:sms:s-1", "challenge:qrcode:q-1", "cleared:q-1", "cleared:s-1"], reporter.Entries);

        var captures = screen.CaptureCount;
        await Task.Delay(100);
        Assert.Equal(captures, screen.CaptureCount);
        Assert.Equal(CatCodes.NOT_RUNNING, watcher.ConfirmSms("s-1").Code);
    }

    [Fact]
    public async Task Run_KeepsGoing_WhenLookingAtTheWindowThrows()
    {
        screen.CaptureFailures = 3;
        screen.Window          = Qr(QQ_LINK);

        using var stop = new CancellationTokenSource();
        var watcher = new CatWeGameChallengeWatcher(screen, reporter, redactor, null) { Interval = TimeSpan.FromMilliseconds(10) };
        var run     = watcher.RunAsync(stop.Token);

        await WaitUntilAsync(() => !reporter.Entries.IsEmpty);
        await stop.CancelAsync();
        await run.WaitAsync(Timeout);

        Assert.Equal(["challenge:qrcode:q-1", "cleared:q-1"], reporter.Entries);
    }

    #endregion

    #region 接进等登录的流程

    [Fact]
    public async Task Capture_WatchesFromLaunchUntilLoginIsCaptured_ThenClears()
    {
        var environment = new WaitingEnvironment();
        screen.Window = Qr(QQ_LINK);
        var capture = CreateCapture(environment);

        var run = capture.FindRowAsync(Request(null), reporter, CancellationToken.None);
        await WaitUntilAsync(() => reporter.Entries.Contains("challenge:qrcode:q-1"));

        environment.Result.TrySetResult(new WeGameCaptureResult("10000000000000001", "captured-token-AAAA"));
        await run.WaitAsync(Timeout);

        Assert.Equal(["stage:waitingWeGameLogin", "challenge:qrcode:q-1", "cleared:q-1", "stage:preparing"], reporter.Entries);
        Assert.Empty(screen.Clicks);

        var captures = screen.CaptureCount;
        await Task.Delay(100);
        Assert.Equal(captures, screen.CaptureCount);
    }

    [Fact]
    public async Task Capture_WithScan_SwitchesToScanPage()
    {
        var environment = new WaitingEnvironment();
        screen.Window  = Page();
        screen.OnClick = (x, y) => (x, y) == (208, 630) ? Qr(QQ_LINK) : Page();
        var capture = CreateCapture(environment);

        var run = capture.FindRowAsync(Request(CatWeGameScan.Qq), reporter, CancellationToken.None);
        await WaitUntilAsync(() => reporter.Entries.Contains("challenge:qrcode:q-1"));

        environment.Result.TrySetResult(new WeGameCaptureResult("10000000000000001", "captured-token-AAAA"));
        await run.WaitAsync(Timeout);

        Assert.Equal([(125, 277), (150, 630), (208, 630)], screen.Clicks);
    }

    [Fact]
    public async Task Capture_Timeout_StopsWatching_AndClears()
    {
        screen.Window = Qr(QQ_LINK);
        var capture = new CatWeGameLoginCapture(new WaitingEnvironment(), new EmptyStore(), redactor)
        {
            Screen        = screen,
            WatchInterval = TimeSpan.FromMilliseconds(10),
            LoginTimeout  = TimeSpan.FromMilliseconds(300)
        };

        var ex = await Assert.ThrowsAsync<CatLaunchException>(() => capture.FindRowAsync(Request(null), reporter, CancellationToken.None)).WaitAsync(Timeout);

        Assert.Equal(CatCodes.WE_GAME_LOGIN_TIMEOUT, ex.Code);
        Assert.Equal(["stage:waitingWeGameLogin", "challenge:qrcode:q-1", "cleared:q-1"], reporter.Entries);

        var captures = screen.CaptureCount;
        await Task.Delay(100);
        Assert.Equal(captures, screen.CaptureCount);
    }

    [Fact]
    public async Task Capture_Close_StopsWatching_AndClears()
    {
        screen.Window = Qr(QQ_LINK);
        using var close = new CancellationTokenSource();
        var capture = CreateCapture(new WaitingEnvironment());

        var run = capture.FindRowAsync(Request(null), reporter, close.Token);
        await WaitUntilAsync(() => reporter.Entries.Contains("challenge:qrcode:q-1"));
        await close.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run).WaitAsync(Timeout);
        Assert.Equal(["stage:waitingWeGameLogin", "challenge:qrcode:q-1", "cleared:q-1"], reporter.Entries);

        var captures = screen.CaptureCount;
        await Task.Delay(100);
        Assert.Equal(captures, screen.CaptureCount);
    }

    [Fact]
    public async Task Capture_ConfirmSms_OnlyWorksWhileWaitingForLogin()
    {
        var environment = new WaitingEnvironment();
        screen.Sms = new CatWeGameSmsPrompt("AQLLJCAQMS", "1069070069", "原文");
        var capture = CreateCapture(environment);

        Assert.Equal(CatCodes.NOT_RUNNING, capture.ConfirmSms("s-1").Code);

        var run = capture.FindRowAsync(Request(null), reporter, CancellationToken.None);
        await WaitUntilAsync(() => reporter.Entries.Contains("challenge:sms:s-1"));

        Assert.True(capture.ConfirmSms("s-1").Accepted);
        Assert.Equal(1, screen.ConfirmCount);

        screen.Sms = null;
        await WaitUntilAsync(() => reporter.Entries.Contains("cleared:s-1"));

        environment.Result.TrySetResult(new WeGameCaptureResult("10000000000000001", "captured-token-AAAA"));
        await run.WaitAsync(Timeout);

        Assert.Equal(CatCodes.NOT_RUNNING, capture.ConfirmSms("s-1").Code);
        Assert.Equal(["stage:waitingWeGameLogin", "challenge:sms:s-1", "cleared:s-1", "stage:preparing"], reporter.Entries);
    }

    [Fact]
    public async Task Capture_WithoutScreen_LooksAtNothing()
    {
        var environment = new WaitingEnvironment();
        environment.Result.TrySetResult(new WeGameCaptureResult("10000000000000001", "captured-token-AAAA"));
        var capture = new CatWeGameLoginCapture(environment, new EmptyStore(), redactor);

        await capture.FindRowAsync(Request(CatWeGameScan.Qq), reporter, CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(["stage:waitingWeGameLogin", "stage:preparing"], reporter.Entries);
        Assert.Equal(0, screen.CaptureCount);
    }

    [Fact]
    public async Task Capture_Busy_NeverStartsWatching()
    {
        screen.Window = Qr(QQ_LINK);
        var environment = new WaitingEnvironment { Failure = new WeGameCapturePipeBusyException(new IOException("busy")) };

        var ex = await Assert.ThrowsAsync<CatLaunchException>(() => CreateCapture(environment).FindRowAsync(Request(CatWeGameScan.Qq), reporter, CancellationToken.None));

        Assert.Equal(CatCodes.WE_GAME_LOGIN_BUSY, ex.Code);
        Assert.Empty(reporter.Entries);
        Assert.Equal(0, screen.CaptureCount);
        Assert.Empty(screen.Clicks);
    }

    #endregion

    #region 二维码的识别与生成（纯计算, 不碰屏幕）

    [Fact]
    public void Codec_DecodesWhatItEncodes()
    {
        var codec = new CatZxingQrCodec();

        var (pixels, width, height) = LoadPng(codec.EncodePng(QQ_LINK));

        Assert.True(width == height && width >= 200);
        Assert.Equal(QQ_LINK, codec.Decode(pixels, width, height));
    }

    [Fact]
    public void Codec_FindsASmallQrInsideAWindowSizedScreenshot_EvenWhenTheFourthByteIsZero()
    {
        var codec = new CatZxingQrCodec();
        var (qr, qrWidth, qrHeight) = LoadPng(codec.EncodePng(QQ_LINK));

        // 1210×680 的浅灰底, 左侧 (86,314) 起贴一个每格 3 像素的二维码; 第 4 个字节全是 0（截窗得到的像素常常这样）
        const int WIDTH = 1210, HEIGHT = 680, LEFT = 86, TOP = 300, MODULE = 3, SOURCE_MODULE = 8;
        var canvas = new byte[WIDTH * HEIGHT * 4];

        for (var i = 0; i < WIDTH * HEIGHT; i++)
            canvas[i * 4] = canvas[i * 4 + 1] = canvas[i * 4 + 2] = 245;

        var modules = qrWidth / SOURCE_MODULE;

        for (var y = 0; y < modules * MODULE; y++)
        {
            for (var x = 0; x < modules * MODULE; x++)
            {
                var source = (y / MODULE * SOURCE_MODULE * qrWidth + x / MODULE * SOURCE_MODULE) * 4;
                var target = ((TOP + y) * WIDTH + LEFT + x) * 4;
                canvas[target] = canvas[target + 1] = canvas[target + 2] = qr[source];
            }
        }

        Assert.True(qrHeight == qrWidth && TOP + modules * MODULE < HEIGHT);

        var link = codec.Decode(canvas, WIDTH, HEIGHT)
                   ?? codec.Decode(CatWeGameRealScreen.Upscale(canvas, WIDTH, HEIGHT, 2), WIDTH * 2, HEIGHT * 2);

        Assert.Equal(QQ_LINK, link);
    }

    [Fact]
    public void Codec_NoQr_ReturnsNull()
    {
        var blank = new byte[200 * 200 * 4];
        Array.Fill(blank, (byte)255);

        Assert.Null(new CatZxingQrCodec().Decode(blank, 200, 200));
        Assert.Null(new CatZxingQrCodec().Decode([], 0, 0));
    }

    [Fact]
    public void Upscale_RepeatsEachPixel()
    {
        byte[] source = [1, 2, 3, 4, 5, 6, 7, 8];

        var scaled = CatWeGameRealScreen.Upscale(source, 2, 1, 2);

        Assert.Equal
        (
            [1, 2, 3, 4, 1, 2, 3, 4, 5, 6, 7, 8, 5, 6, 7, 8, 1, 2, 3, 4, 1, 2, 3, 4, 5, 6, 7, 8, 5, 6, 7, 8],
            scaled
        );
    }

    [Fact]
    public void PackPoint_PutsXInLowWord_AndYInHighWord()
    {
        Assert.Equal((IntPtr)((277 << 16) | 125), CatWeGameRealScreen.PackPoint(125, 277));
        Assert.Equal((IntPtr)(630 << 16 | 208), CatWeGameRealScreen.PackPoint(208, 630));
    }

    /// <summary>真实实现在设备验证窗口检查完之前一律当作没有这个窗口, 这两个方法不碰屏幕</summary>
    [Fact]
    public void RealScreen_SmsPrompt_IsNotImplementedYet()
    {
        var real = new CatWeGameRealScreen(new CatZxingQrCodec());

        Assert.Null(real.FindSmsPrompt());
        Assert.False(real.ConfirmSmsPrompt());
    }

    #endregion

    private CatWeGameChallengeWatcher Create(CatWeGameScan? scan) =>
        new(screen, reporter, redactor, scan) { ClickSettle = TimeSpan.Zero, SettleTicks = 2 };

    private CatWeGameLoginCapture CreateCapture(ICatWeGameLoginEnvironment environment) =>
        new(environment, new EmptyStore(), redactor) { Screen = screen, WatchInterval = TimeSpan.FromMilliseconds(10) };

    private static CatLaunchRequest Request(CatWeGameScan? scan) =>
        new("op", "123456", false, null, null, Platform: XIVAccountType.WeGame, WeGameLogin: true, WeGameScan: scan);

    private static async Task TickAsync(CatWeGameChallengeWatcher watcher, int times)
    {
        for (var i = 0; i < times; i++)
            await watcher.TickAsync(CancellationToken.None);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;

        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "等待的情况没有出现");
            await Task.Delay(5);
        }
    }

    private static CatWeGameLoginWindow Page() =>
        new(CatWeGameChallengeWatcher.DESIGN_WIDTH, CatWeGameChallengeWatcher.DESIGN_HEIGHT);

    private static CatWeGameLoginWindow Qr(string link) =>
        new(CatWeGameChallengeWatcher.DESIGN_WIDTH, CatWeGameChallengeWatcher.DESIGN_HEIGHT, link, Png);

    private static (byte[] Pixels, int Width, int Height) LoadPng(byte[] png)
    {
        using var stream = new MemoryStream(png);
        var frame     = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var pixels    = new byte[converted.PixelWidth * converted.PixelHeight * 4];
        converted.CopyPixels(pixels, converted.PixelWidth * 4, 0);
        return (pixels, converted.PixelWidth, converted.PixelHeight);
    }

    /// <summary>
    ///     假屏幕: 窗口上显示什么由测试摆, 点击只记下来（并按 OnClick 换一幅画面）
    /// </summary>
    private sealed class FakeScreen : ICatWeGameScreen
    {
        private readonly object sync = new();

        private CatWeGameLoginWindow? window;
        private CatWeGameSmsPrompt?   sms;
        private int                   captureCount;

        public CatWeGameLoginWindow? Window
        {
            get
            {
                lock (sync)
                    return window;
            }
            set
            {
                lock (sync)
                    window = value;
            }
        }

        public CatWeGameSmsPrompt? Sms
        {
            get
            {
                lock (sync)
                    return sms;
            }
            set
            {
                lock (sync)
                    sms = value;
            }
        }

        public Func<int, int, CatWeGameLoginWindow?>? OnClick { get; set; }

        public bool ClickFails { get; set; }

        public bool ConfirmFails { get; set; }

        public int CaptureFailures { get; set; }

        public List<(int X, int Y)> Clicks { get; } = [];

        public int ConfirmCount { get; private set; }

        public int CaptureCount => Volatile.Read(ref captureCount);

        public CatWeGameLoginWindow? CaptureLoginWindow()
        {
            Interlocked.Increment(ref captureCount);

            if (CaptureFailures > 0)
            {
                CaptureFailures--;
                throw new InvalidOperationException("截窗失败");
            }

            return Window;
        }

        public bool ClickLoginWindow(int x, int y)
        {
            if (ClickFails)
                return false;

            Clicks.Add((x, y));

            if (OnClick != null)
                Window = OnClick(x, y);

            return true;
        }

        public CatWeGameSmsPrompt? FindSmsPrompt() => Sms;

        public bool ConfirmSmsPrompt()
        {
            if (ConfirmFails)
                return false;

            ConfirmCount++;
            return true;
        }
    }

    /// <summary>
    ///     假的本机环境: 不拉起 WeGame; 报完"已拉起"后一直等到测试给出结果或被取消
    /// </summary>
    private sealed class WaitingEnvironment : ICatWeGameLoginEnvironment
    {
        public TaskCompletionSource<WeGameCaptureResult> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Exception? Failure { get; init; }

        public string? FindSdologinDir() => @"X:\fake\FF14\sdo\sdologin";

        public void StopWeGameClient()
        {
        }

        public async Task<WeGameCaptureResult> CaptureAsync(string sdologinDir, Action beforeLaunch, Action afterLaunch, CancellationToken cancellationToken)
        {
            if (Failure != null)
                throw Failure;

            beforeLaunch();
            afterLaunch();
            return await Result.Task.WaitAsync(cancellationToken);
        }
    }

    /// <summary>
    ///     空的假账号库: 存什么就回什么
    /// </summary>
    private sealed class EmptyStore : ICatWeGameAccountStore
    {
        public IReadOnlyList<CatWeGameRow> ListRows() => [];

        public IReadOnlyList<CatWeGameRow> ReloadRows() => [];

        public Task<string?> ReadTokenAsync(CatWeGameRow row) => Task.FromResult<string?>(null);

        public void ClearToken(CatWeGameRow row)
        {
        }

        public Task<CatWeGameRow> SaveCapturedAsync(string userId, string token, string? note) =>
            Task.FromResult(new CatWeGameRow(userId, note));
    }
}
