using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XIVLauncher.CatHost;
using Xunit;

namespace XIVLauncher.Test.CatHost;

/// <summary>
///     WeGame QQ 账号密码的硬件自动登录编排: 全用假的硬件键鼠 / 屏幕 / 前台, 不接硬件、不拉起 WeGame。
///     覆盖: 没接硬件、非法字符、前台被打断即停与重试上限、密码错不重试、客户做验证期间不超时、超时、导航、成功点「启动」。
/// </summary>
public sealed class CatWeGameHwLoginTests
{
    private const string ACCOUNT  = "123456";
    private const string PASSWORD = "pw1234Ab";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly StubHidKeyboard     keyboard   = new();
    private readonly StubWeGameScreen    screen      = new();
    private readonly StubWeGameForeground foreground = new();

    /// <summary>这个 kind 的名字跟服务器 / 工作台约定一致, 不能改</summary>
    [Fact]
    public void QqConfirmKind_IsStable()
    {
        Assert.Equal("qqConfirm", CatWeGameChallengeKinds.QQ_CONFIRM);
    }

    [Fact]
    public async Task NoHardware_FallsBackToManual()
    {
        keyboard.OpenResult = false;

        var result = await Flow().RunAsync(ACCOUNT, Secret(PASSWORD), CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(CatWeGameHwLoginOutcome.FallBackToManual, result.Outcome);
        Assert.Contains("未接硬件键鼠", result.Reason);
        Assert.Empty(keyboard.Clicks);
        Assert.Equal(0, keyboard.Typed.Length);
    }

    [Fact]
    public async Task NonAsciiPassword_FallsBackToManual_WithoutLeavingChars()
    {
        var result = await Flow().RunAsync(ACCOUNT, Secret("密码123"), CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(CatWeGameHwLoginOutcome.FallBackToManual, result.Outcome);
        Assert.Contains("打不出的字符", result.Reason);
        // 账号是 ASCII 先打了, 密码遇到中文一个字都不打
        Assert.Equal(ACCOUNT, keyboard.Typed.ToString());
    }

    [Fact]
    public async Task HappyPath_TypesAccountAndPassword_ClicksLoginThenStart()
    {
        FlipSignedInOnLoginClick();

        var result = await Flow().RunAsync(ACCOUNT, Secret(PASSWORD), CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(CatWeGameHwLoginOutcome.Success, result.Outcome);
        Assert.Equal(ACCOUNT + PASSWORD, keyboard.Typed.ToString());
        Assert.Contains((HidModifiers.Ctrl, HidKey.A), keyboard.Presses);
        Assert.Contains((HidModifiers.None, HidKey.Delete), keyboard.Presses);
        Assert.Contains((150, 466), keyboard.Clicks);   // 「登录」
        Assert.Contains((1480, 835), keyboard.Clicks);  // 主窗口「启动」
    }

    [Fact]
    public async Task AccountAlreadyMatches_SkipsClearAndAccountInput()
    {
        screen.AccountIsMatch = true;
        FlipSignedInOnLoginClick();

        var result = await Flow().RunAsync(ACCOUNT, Secret(PASSWORD), CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(CatWeGameHwLoginOutcome.Success, result.Outcome);
        Assert.Equal(PASSWORD, keyboard.Typed.ToString());   // 只打了密码, 没重输账号
        Assert.Empty(keyboard.Presses);                      // 没 Ctrl+A / Delete
    }

    [Fact]
    public async Task ForegroundLostOnce_RetriesAndSucceeds()
    {
        foreground.InterruptUntilCheck = 1;   // 第一次核对前台返回 false
        FlipSignedInOnLoginClick();

        var result = await Flow().RunAsync(ACCOUNT, Secret(PASSWORD), CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(CatWeGameHwLoginOutcome.Success, result.Outcome);
        Assert.Equal(2, foreground.FocusCount);   // 第一次被打断, 第二次成功
    }

    [Fact]
    public async Task ForegroundAlwaysLost_FallsBackAfterThreeAttempts()
    {
        foreground.InterruptUntilCheck = 1000;   // 永远不是前台

        var result = await Flow().RunAsync(ACCOUNT, Secret(PASSWORD), CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(CatWeGameHwLoginOutcome.FallBackToManual, result.Outcome);
        Assert.Contains("多次被打断", result.Reason);
        Assert.Equal(3, foreground.FocusCount);
        Assert.Equal(0, keyboard.Typed.Length);   // 从没真的打出去
    }

    [Fact]
    public async Task PasswordRejected_DoesNotRetry()
    {
        screen.PasswordRejectedProbe = () => true;

        var result = await Flow().RunAsync(ACCOUNT, Secret(PASSWORD), CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(CatWeGameHwLoginOutcome.PasswordRejected, result.Outcome);
        Assert.Equal(1, keyboard.Clicks.Count(c => c == (150, 466)));   // 只点了一次登录, 没重试
    }

    [Fact]
    public async Task CustomerVerifying_ExtendsTheDeadline_UntilSignedIn()
    {
        // 点了登录后弹出验证码 / 手机确认, 客户做了 1 秒才完成: 远超过 200ms 的等结果上限, 也不退回手动
        var popupUntil = DateTime.UtcNow + TimeSpan.FromSeconds(1);
        screen.CaptureProbe = () =>
        {
            if (DateTime.UtcNow < popupUntil)
                return new CatWeGameLoginWindow(1210, 680, Popup: new CatWeGamePopup(CatWeGamePopupKind.QqConfirm));

            foreground.SignedIn = true;
            return null;
        };

        var result = await ShortTimeoutFlow().RunAsync(ACCOUNT, Secret(PASSWORD), CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(CatWeGameHwLoginOutcome.Success, result.Outcome);
        Assert.Contains((1480, 835), keyboard.Clicks);
    }

    [Fact]
    public async Task CustomerVerifying_ThenNothing_TimesOutCountingFromTheLastPopup()
    {
        var started    = DateTime.UtcNow;
        var popupUntil = started + TimeSpan.FromMilliseconds(800);
        screen.CaptureProbe = () => DateTime.UtcNow < popupUntil
                                        ? new CatWeGameLoginWindow(1210, 680, Popup: new CatWeGamePopup(CatWeGamePopupKind.CaptchaReady))
                                        : new CatWeGameLoginWindow(1210, 680);

        var result = await ShortTimeoutFlow().RunAsync(ACCOUNT, Secret(PASSWORD), CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(CatWeGameHwLoginOutcome.FallBackToManual, result.Outcome);
        Assert.Contains("超时", result.Reason);
        Assert.True(DateTime.UtcNow - started >= TimeSpan.FromMilliseconds(800));
    }

    [Fact]
    public async Task NoLoginResult_TimesOut_FallsBackToManual()
    {
        var flow = new CatWeGameHwLoginFlow(keyboard, screen, foreground)
        {
            ActionPause        = TimeSpan.Zero,
            NavPause           = TimeSpan.Zero,
            LoginResultPoll    = TimeSpan.FromMilliseconds(5),
            LoginResultTimeout = TimeSpan.FromMilliseconds(60)
        };

        var result = await flow.RunAsync(ACCOUNT, Secret(PASSWORD), CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(CatWeGameHwLoginOutcome.FallBackToManual, result.Outcome);
        Assert.Contains("超时", result.Reason);
    }

    [Fact]
    public async Task NotOnPasswordPage_NavigatesByClickingTabAndEntry()
    {
        screen.QqPasswordPage = null;   // 认不出在哪一页: 点 QQ 页签、再点「QQ 账号密码登录」
        FlipSignedInOnLoginClick();

        var flow = new CatWeGameHwLoginFlow(keyboard, screen, foreground)
        {
            ActionPause        = TimeSpan.Zero,
            NavPause           = TimeSpan.Zero,
            NavRounds          = 1,
            LoginResultPoll    = TimeSpan.FromMilliseconds(1),
            LoginResultTimeout = TimeSpan.FromSeconds(1)
        };

        var result = await flow.RunAsync(ACCOUNT, Secret(PASSWORD), CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(CatWeGameHwLoginOutcome.Success, result.Outcome);
        Assert.Contains((125, 277), screen.Clicks);   // QQ 页签
        Assert.Contains((150, 633), screen.Clicks);   // 「QQ 账号密码登录」
    }

    [Fact]
    public async Task Clicker_WithHardware_FocusesChecksForegroundAndClicksOnScreen()
    {
        foreground.Login = new CatWeGameWindowRect(100, 50, 1210, 680);
        var clicker = new CatWeGameClicker(screen, keyboard, foreground);

        Assert.True(await clicker.ClickAsync(620, 449, CancellationToken.None));
        Assert.True(await clicker.ClickAsync(382, 450, CancellationToken.None));

        Assert.Equal([(720, 499), (482, 500)], keyboard.Clicks);
        Assert.Equal(2, foreground.FocusCount);
        Assert.Equal(1, keyboard.TryOpenCount);   // 有没有硬件只探测一次
        Assert.Empty(screen.Clicks);
    }

    [Fact]
    public async Task Clicker_WithHardware_ForegroundNotLoginWindow_DoesNotClick()
    {
        foreground.InterruptUntilCheck = 1;
        var clicker = new CatWeGameClicker(screen, keyboard, foreground);

        Assert.False(await clicker.ClickAsync(620, 449, CancellationToken.None));
        Assert.Empty(keyboard.Clicks);
        Assert.Empty(screen.Clicks);
    }

    [Fact]
    public async Task Clicker_WithoutHardware_SendsWindowMessages()
    {
        keyboard.OpenResult = false;

        Assert.True(await new CatWeGameClicker(screen, keyboard, foreground).ClickAsync(620, 449, CancellationToken.None));
        Assert.True(await new CatWeGameClicker(screen, null, null).ClickAsync(382, 450, CancellationToken.None));

        Assert.Equal([(620, 449), (382, 450)], screen.Clicks);
        Assert.Empty(keyboard.Clicks);
        Assert.Equal(0, foreground.FocusCount);
    }

    private CatWeGameHwLoginFlow ShortTimeoutFlow() =>
        new(keyboard, screen, foreground)
        {
            ActionPause        = TimeSpan.Zero,
            NavPause           = TimeSpan.Zero,
            LoginResultPoll    = TimeSpan.FromMilliseconds(5),
            LoginResultTimeout = TimeSpan.FromMilliseconds(200)
        };

    private CatWeGameHwLoginFlow Flow() =>
        new(keyboard, screen, foreground)
        {
            ActionPause        = TimeSpan.Zero,
            NavPause           = TimeSpan.Zero,
            LoginResultPoll    = TimeSpan.FromMilliseconds(1),
            LoginResultTimeout = TimeSpan.FromSeconds(2)
        };

    private void FlipSignedInOnLoginClick() =>
        keyboard.OnClick = (_, y) =>
        {
            if (y == 466)
                foreground.SignedIn = true;
        };

    private static CatSecret Secret(string value) => new(value);
}
