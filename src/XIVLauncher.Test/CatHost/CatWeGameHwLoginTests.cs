using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XIVLauncher.CatHost;
using Xunit;

namespace XIVLauncher.Test.CatHost;

/// <summary>
///     WeGame QQ 账号密码自动登录的编排: 全用假的硬件键盘 / 屏幕 / 前台, 不接硬件、不拉起 WeGame。
///     覆盖: 没接硬件、打不出的字符、等登录窗、导航、前台与焦点被抢走即停与重试上限、密码错不重试、客户做验证期间不超时、超时、
///     登录后点「启动」（首次勾协议、确认选大区）与启动超时。
/// </summary>
public sealed class CatWeGameHwLoginTests
{
    private const string ACCOUNT  = "123456";
    private const string PASSWORD = "pw1234Ab";

    /// <summary>主界面上能点的「启动」</summary>
    private static readonly CatWeGameMainWindow Ready = new(1600, 900, StartReady: true, AgreementOpen: false, AgreeAllChecked: false);

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly StubHidKeyboard      keyboard   = new();
    private readonly StubWeGameScreen     screen     = new();
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
        Assert.Empty(screen.Clicks);
        Assert.Equal(0, keyboard.Typed.Length);
    }

    [Fact]
    public async Task UntypeablePassword_FallsBack_BeforeTypingAnything()
    {
        var result = await Flow().RunAsync(ACCOUNT, Secret("密码123"), CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(CatWeGameHwLoginOutcome.FallBackToManual, result.Outcome);
        Assert.Contains("打不出的字符", result.Reason);
        Assert.Equal(0, keyboard.Typed.Length);
        Assert.Empty(screen.Clicks);
    }

    [Fact]
    public async Task HappyPath_TypesAccountAndPassword_ClicksLoginThenStart()
    {
        SignInOnLoginClick();
        StartGameOnStartClick();

        var result = await Flow().RunAsync(ACCOUNT, Secret(PASSWORD), CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(CatWeGameHwLoginOutcome.Success, result.Outcome);
        Assert.Equal(ACCOUNT + PASSWORD, keyboard.Typed.ToString());
        // 账号框: 全选删除; 密码框: 到末尾、退格清空
        Assert.Equal((HidModifiers.Ctrl, HidKey.A), keyboard.Presses[0]);
        Assert.Equal((HidModifiers.None, HidKey.Delete), keyboard.Presses[1]);
        Assert.Equal((HidModifiers.None, HidKey.End), keyboard.Presses[2]);
        Assert.Equal(24, keyboard.Presses.Count(p => p.Key == HidKey.Backspace));
        Assert.Equal([(150, 336), (150, 466)], screen.Clicks);   // 账号框、「登录」（已在账号密码页, 不用导航）
        Assert.Equal([(1382, 848)], screen.MainClicks);          // 「启动」
        Assert.Equal(1, foreground.PasswordFocusCount);
    }

    [Fact]
    public async Task Clicks_AreScaledToTheActualWindowSize()
    {
        screen.Window = new CatWeGameLoginWindow(605, 340);
        screen.Main   = null;
        SignInOnLoginClick(new CatWeGameMainWindow(800, 450, true, false, false));
        StartGameOnStartClick();

        var result = await Flow().RunAsync(ACCOUNT, Secret(PASSWORD), CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(CatWeGameHwLoginOutcome.Success, result.Outcome);
        Assert.Equal([(75, 168), (75, 233)], screen.Clicks);
        Assert.Equal([(691, 424)], screen.MainClicks);
    }

    [Fact]
    public async Task LoginWindowNotDrawnYet_WaitsForTheTabs()
    {
        var drawn = DateTime.UtcNow + TimeSpan.FromMilliseconds(300);
        screen.CaptureProbe = () => new CatWeGameLoginWindow(1210, 680, TabsVisible: DateTime.UtcNow >= drawn);
        SignInOnLoginClick();
        StartGameOnStartClick();

        var result = await Flow().RunAsync(ACCOUNT, Secret(PASSWORD), CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(CatWeGameHwLoginOutcome.Success, result.Outcome);
        Assert.True(DateTime.UtcNow >= drawn);
    }

    [Fact]
    public async Task LoginWindowNeverAppears_FallsBack()
    {
        screen.Window = null;

        var result = await new CatWeGameHwLoginFlow(keyboard, screen, foreground)
                     {
                         ActionPause     = TimeSpan.Zero,
                         NavPause        = TimeSpan.Zero,
                         LoginResultPoll = TimeSpan.FromMilliseconds(5),
                         WindowTimeout   = TimeSpan.FromMilliseconds(100)
                     }.RunAsync(ACCOUNT, Secret(PASSWORD), CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(CatWeGameHwLoginOutcome.FallBackToManual, result.Outcome);
        Assert.Contains("登录窗口没有出现", result.Reason);
        Assert.Equal(0, keyboard.Typed.Length);
    }

    [Fact]
    public async Task NotOnPasswordPage_NavigatesByClickingTabAndEntry()
    {
        foreground.PasswordVisible = false;
        screen.OnClick = (x, y) =>
        {
            if ((x, y) == (150, 633))
                foreground.PasswordVisible = true;
        };
        SignInOnLoginClick();
        StartGameOnStartClick();

        var result = await Flow().RunAsync(ACCOUNT, Secret(PASSWORD), CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(CatWeGameHwLoginOutcome.Success, result.Outcome);
        Assert.Equal((125, 277), screen.Clicks[0]);   // QQ 页签
        Assert.Equal((150, 633), screen.Clicks[1]);   // 「QQ 账号密码登录」
    }

    [Fact]
    public async Task NeverReachesPasswordPage_FallsBack_WithoutTyping()
    {
        foreground.PasswordVisible = false;

        var result = await Flow().RunAsync(ACCOUNT, Secret(PASSWORD), CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(CatWeGameHwLoginOutcome.FallBackToManual, result.Outcome);
        Assert.Contains("进不了 QQ 账号密码登录页", result.Reason);
        Assert.Equal(3, screen.Clicks.Count(c => c == (125, 277)));
        Assert.Equal(0, keyboard.Typed.Length);
    }

    [Fact]
    public async Task ForegroundLostOnce_RetriesAndSucceeds()
    {
        foreground.ForegroundLostUntil = 1;   // 第一次核对前台返回 false
        SignInOnLoginClick();
        StartGameOnStartClick();

        var result = await Flow().RunAsync(ACCOUNT, Secret(PASSWORD), CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(CatWeGameHwLoginOutcome.Success, result.Outcome);
        Assert.Equal(2, foreground.FocusCount);   // 第一次被打断, 第二次成功
        Assert.Equal(ACCOUNT + PASSWORD, keyboard.Typed.ToString());
    }

    [Fact]
    public async Task CannotBringLoginWindowToFront_FallsBackAfterThreeAttempts()
    {
        foreground.FocusResult = false;

        var result = await Flow().RunAsync(ACCOUNT, Secret(PASSWORD), CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(CatWeGameHwLoginOutcome.FallBackToManual, result.Outcome);
        Assert.Contains("多次被打断", result.Reason);
        Assert.Equal(3, foreground.FocusCount);
        Assert.Equal(0, keyboard.Typed.Length);
        Assert.Empty(keyboard.Presses);
    }

    [Fact]
    public async Task PasswordFocusLostWhileTyping_StopsAndRetypesFromTheStart()
    {
        // 第一轮清空密码框时都在, 打到第 2 个字焦点没了: 停下, 第二轮从账号开始重来
        var checks = 0;
        foreground.PasswordFocusLostUntil = 0;
        keyboard.OnType = _ =>
        {
            if (keyboard.Typed.Length == ACCOUNT.Length + 1 && checks++ == 0)
                foreground.PasswordFocusLostUntil = int.MaxValue;
        };
        screen.OnClick = (_, y) =>
        {
            if (y == 336)
                foreground.PasswordFocusLostUntil = 0;
        };
        SignInOnLoginClick();
        StartGameOnStartClick();

        var result = await Flow().RunAsync(ACCOUNT, Secret(PASSWORD), CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(CatWeGameHwLoginOutcome.Success, result.Outcome);
        Assert.Equal(ACCOUNT + PASSWORD[0] + ACCOUNT + PASSWORD, keyboard.Typed.ToString());
        Assert.Equal(1, screen.Clicks.Count(c => c == (150, 466)));   // 只在打完整的那一轮点了登录
    }

    [Fact]
    public async Task PasswordRejected_DoesNotRetry()
    {
        screen.PasswordRejectedProbe = () => true;

        var result = await Flow().RunAsync(ACCOUNT, Secret(PASSWORD), CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(CatWeGameHwLoginOutcome.PasswordRejected, result.Outcome);
        Assert.Equal(1, screen.Clicks.Count(c => c == (150, 466)));   // 只点了一次登录, 没重试
        Assert.Empty(screen.MainClicks);
    }

    [Fact]
    public async Task CustomerVerifying_ExtendsTheDeadline_UntilSignedIn()
    {
        // 点了登录后弹出验证码 / 手机确认, 客户做了 1 秒才完成: 远超过 200ms 的等结果上限, 也不退回手动
        var loginClicked = false;
        var popupUntil   = DateTime.MaxValue;
        screen.OnClick = (_, y) =>
        {
            if (y != 466)
                return;

            loginClicked = true;
            popupUntil   = DateTime.UtcNow + TimeSpan.FromSeconds(1);
        };
        screen.CaptureProbe = () =>
        {
            if (!loginClicked)
                return new CatWeGameLoginWindow(1210, 680);

            if (DateTime.UtcNow < popupUntil)
                return new CatWeGameLoginWindow(1210, 680, Popup: new CatWeGamePopup(CatWeGamePopupKind.QqConfirm));

            foreground.SignedIn = true;
            screen.Main         = Ready;
            return null;
        };
        StartGameOnStartClick();

        var result = await ShortTimeoutFlow().RunAsync(ACCOUNT, Secret(PASSWORD), CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(CatWeGameHwLoginOutcome.Success, result.Outcome);
        Assert.Contains((1382, 848), screen.MainClicks);
    }

    [Fact]
    public async Task CustomerVerifying_ThenNothing_TimesOutCountingFromTheLastPopup()
    {
        var loginClicked = DateTime.MaxValue;
        screen.OnClick = (_, y) =>
        {
            if (y == 466)
                loginClicked = DateTime.UtcNow;
        };
        screen.CaptureProbe = () => DateTime.UtcNow - loginClicked < TimeSpan.FromMilliseconds(800)
                                        ? new CatWeGameLoginWindow(1210, 680, Popup: new CatWeGamePopup(CatWeGamePopupKind.CaptchaReady))
                                        : new CatWeGameLoginWindow(1210, 680);

        var result = await ShortTimeoutFlow().RunAsync(ACCOUNT, Secret(PASSWORD), CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(CatWeGameHwLoginOutcome.FallBackToManual, result.Outcome);
        Assert.Contains("超时", result.Reason);
        Assert.True(DateTime.UtcNow - loginClicked >= TimeSpan.FromMilliseconds(800));
    }

    [Fact]
    public async Task NoLoginResult_TimesOut_FallsBackToManual()
    {
        var result = await ShortTimeoutFlow().RunAsync(ACCOUNT, Secret(PASSWORD), CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(CatWeGameHwLoginOutcome.FallBackToManual, result.Outcome);
        Assert.Contains("等 WeGame 登录结果超时", result.Reason);
        Assert.Empty(screen.MainClicks);
    }

    [Fact]
    public async Task FirstStart_TicksTheAgreement_ClicksStart_ConfirmsRegion()
    {
        // 首次启动: 点「启动」弹协议 → 勾「以上所有内容」→ 再点「启动」→ 弹选大区 → 点「确定」→ 游戏登录组件起来
        var main = Ready;
        SignInOnLoginClick();
        screen.MainProbe = () => main;
        screen.OnMainClick = (x, y) =>
        {
            if ((x, y) == (1382, 848))
            {
                if (!main.AgreementOpen && main.StartReady && screen.MainClicks.Count == 1)
                    main = main with { AgreementOpen = true, StartReady = true };
                else if (main.AgreementOpen && main.AgreeAllChecked)
                {
                    main                    = main with { AgreementOpen = false, StartReady = false };
                    screen.RegionDialogOpen = true;
                }
            }
            else if ((x, y) == (1277, 777))
                main = main with { AgreeAllChecked = !main.AgreeAllChecked };
        };
        screen.OnRegionConfirm = () =>
        {
            screen.RegionDialogOpen = false;
            foreground.GameStarted  = true;
        };

        var result = await Flow().RunAsync(ACCOUNT, Secret(PASSWORD), CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(CatWeGameHwLoginOutcome.Success, result.Outcome);
        Assert.Equal([(1382, 848), (1277, 777), (1382, 848)], screen.MainClicks);
        Assert.Equal(1, screen.RegionConfirms);
    }

    [Fact]
    public async Task AgreementTick_NotYetDrawn_IsNotClickedAgainRightAway()
    {
        // 勾了以后截图还是没勾（还没画出来）: 等 AgreeSettle 之后才再点, 不会马上又把它勾掉
        SignInOnLoginClick(new CatWeGameMainWindow(1600, 900, true, AgreementOpen: true, AgreeAllChecked: false));

        var result = await new CatWeGameHwLoginFlow(keyboard, screen, foreground)
                     {
                         ActionPause     = TimeSpan.Zero,
                         NavPause        = TimeSpan.Zero,
                         LoginResultPoll = TimeSpan.FromMilliseconds(5),
                         AgreeSettle     = TimeSpan.FromSeconds(30),
                         StartTimeout    = TimeSpan.FromMilliseconds(200)
                     }.RunAsync(ACCOUNT, Secret(PASSWORD), CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(CatWeGameHwLoginOutcome.FallBackToManual, result.Outcome);
        Assert.Equal([(1277, 777)], screen.MainClicks);
    }

    [Fact]
    public async Task StartBusy_DoesNotClickAgain_UntilTheButtonIsReadyAndRetryIsDue()
    {
        // 点了「启动」后按钮一直是「正在启动」: 不再点
        SignInOnLoginClick();
        screen.OnMainClick = (_, _) => screen.Main = Ready with { StartReady = false };

        var result = await new CatWeGameHwLoginFlow(keyboard, screen, foreground)
                     {
                         ActionPause     = TimeSpan.Zero,
                         NavPause        = TimeSpan.Zero,
                         LoginResultPoll = TimeSpan.FromMilliseconds(5),
                         StartRetry      = TimeSpan.Zero,
                         StartTimeout    = TimeSpan.FromMilliseconds(200)
                     }.RunAsync(ACCOUNT, Secret(PASSWORD), CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(CatWeGameHwLoginOutcome.FallBackToManual, result.Outcome);
        Assert.Contains("点「启动」", result.Reason);
        Assert.Equal([(1382, 848)], screen.MainClicks);
    }

    [Fact]
    public async Task StartReadyAgain_ClicksAgainAfterTheRetryInterval()
    {
        SignInOnLoginClick();

        var result = await new CatWeGameHwLoginFlow(keyboard, screen, foreground)
                     {
                         ActionPause     = TimeSpan.Zero,
                         NavPause        = TimeSpan.Zero,
                         LoginResultPoll = TimeSpan.FromMilliseconds(5),
                         StartRetry      = TimeSpan.FromMilliseconds(100),
                         StartTimeout    = TimeSpan.FromMilliseconds(350)
                     }.RunAsync(ACCOUNT, Secret(PASSWORD), CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(CatWeGameHwLoginOutcome.FallBackToManual, result.Outcome);
        Assert.InRange(screen.MainClicks.Count, 2, 4);
    }

    [Theory]
    [InlineData(1210, 680, true)]
    [InlineData(960, 540, true)]
    [InlineData(864, 486, true)]
    [InlineData(720, 540, false)]
    [InlineData(577, 288, false)]
    public void LoginWindowShape_IsSixteenByNine_NotTheRegionDialog(int width, int height, bool expected)
    {
        Assert.Equal(expected, CatWeGameWindows.IsLoginShape(width, height));
    }

    [Theory]
    [InlineData(720, 540, true)]
    [InlineData(1210, 680, false)]
    [InlineData(300, 225, false)]
    public void RegionDialogShape(int width, int height, bool expected)
    {
        Assert.Equal(expected, CatWeGameWindows.IsRegionDialogShape(width, height));
    }

    [Theory]
    [InlineData(376, 680, true)]
    [InlineData(336, 680, false)]   // 账号框那一行
    [InlineData(466, 680, false)]   // 「登录」那一行
    [InlineData(188, 340, true)]
    public void PasswordRow(double centerY, int clientHeight, bool expected)
    {
        Assert.Equal(expected, CatWeGameWindows.InPasswordRow(centerY, clientHeight));
    }

    private CatWeGameHwLoginFlow ShortTimeoutFlow() =>
        new(keyboard, screen, foreground)
        {
            ActionPause        = TimeSpan.Zero,
            NavPause           = TimeSpan.Zero,
            LoginResultPoll    = TimeSpan.FromMilliseconds(5),
            LoginResultTimeout = TimeSpan.FromMilliseconds(200),
            StartTimeout       = TimeSpan.FromSeconds(2)
        };

    private CatWeGameHwLoginFlow Flow() =>
        new(keyboard, screen, foreground)
        {
            ActionPause        = TimeSpan.Zero,
            NavPause           = TimeSpan.Zero,
            AgreeSettle        = TimeSpan.Zero,
            LoginResultPoll    = TimeSpan.FromMilliseconds(1),
            LoginResultTimeout = TimeSpan.FromSeconds(2),
            StartTimeout       = TimeSpan.FromSeconds(2)
        };

    /// <summary>点「登录」后登录窗消失、主界面出来</summary>
    private void SignInOnLoginClick(CatWeGameMainWindow? main = null)
    {
        var previous = screen.OnClick;
        screen.OnClick = (x, y) =>
        {
            previous?.Invoke(x, y);

            if ((x, y) == CatWeGameChallengeWatcher.Scale((150, 466), screen.Window?.Width ?? 1210, screen.Window?.Height ?? 680))
            {
                foreground.SignedIn = true;
                screen.Main         = main ?? Ready;
            }
        };
    }

    /// <summary>点「启动」后游戏的登录组件起来</summary>
    private void StartGameOnStartClick() =>
        screen.OnMainClick = (_, _) => foreground.GameStarted = true;

    private static CatSecret Secret(string value) => new(value);
}
