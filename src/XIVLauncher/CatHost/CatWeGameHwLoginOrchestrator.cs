using Serilog;

namespace XIVLauncher.CatHost;

/// <summary>硬件自动登录走到最后是哪一种结局</summary>
public enum CatWeGameHwLoginOutcome
{
    /// <summary>登录成功、并点了「启动」, 之后照常由 version.dll 抓令牌</summary>
    Success,

    /// <summary>退回员工手动（带原因）: 没接硬件、输入被打断、认不出页面、超时等, 都不算上号失败</summary>
    FallBackToManual,

    /// <summary>WeGame 提示密码错误: 不反复重试（避免锁号）, 退回请员工联系客户核对</summary>
    PasswordRejected,

    /// <summary>停在等客户在手机 QQ 上确认: 本机点不了, 要提醒客户</summary>
    NeedsPhoneConfirm
}

/// <summary>硬件自动登录的结果</summary>
public sealed record CatWeGameHwLoginResult(CatWeGameHwLoginOutcome Outcome, string? Reason = null)
{
    /// <summary>登录成功</summary>
    public static CatWeGameHwLoginResult Success() =>
        new(CatWeGameHwLoginOutcome.Success);

    /// <summary>退回员工手动, 带给工作室员工看的原因</summary>
    public static CatWeGameHwLoginResult Fallback(string reason) =>
        new(CatWeGameHwLoginOutcome.FallBackToManual, reason);

    /// <summary>密码错误</summary>
    public static readonly CatWeGameHwLoginResult PasswordRejected = new(CatWeGameHwLoginOutcome.PasswordRejected);

    /// <summary>要客户在手机 QQ 上确认</summary>
    public static readonly CatWeGameHwLoginResult NeedsPhoneConfirm = new(CatWeGameHwLoginOutcome.NeedsPhoneConfirm);
}

/// <summary>
///     用本机的硬件键鼠（CH9329）在 WeGame 登录窗口里自动输入 QQ 账号密码、点登录, 登录成功后点「启动」。
///     <para>
///         硬件按键只落到前台窗口, 所以每次输入前都把登录窗口切到前台、并核对前台仍是它, 不是就立刻中止本次、整段重来（最多 <see cref="MaxAttempts" /> 次）;
///         仍不成功、本机没接硬件、密码含硬件键盘打不出的字符时, 都退回员工手动（不判上号失败）。密码错误不重试。停在等手机确认时交回由上层提醒客户。
///     </para>
///     <para>坐标按 StartFor 拉起的 1210×680 登录窗口量（见 window-geometry.md）, 按实际客户区尺寸线性换算; 页面校验与「启动」坐标的图像阈值留实机核对。</para>
/// </summary>
public sealed class CatWeGameHwLoginFlow(ICatHidKeyboard keyboard, ICatWeGameScreen screen, ICatWeGameForeground foreground)
{
    /// <summary>账号下拉框（1210×680 客户区坐标; 是下拉框, 换号要先清空）</summary>
    private static readonly (int X, int Y) AccountBox = (150, 336);

    /// <summary>密码框</summary>
    private static readonly (int X, int Y) PasswordBox = (150, 376);

    /// <summary>「登录」按钮（橙色实心）</summary>
    private static readonly (int X, int Y) LoginButton = (150, 466);

    /// <summary>QQ 页签</summary>
    private static readonly (int X, int Y) QqTab = (125, 277);

    /// <summary>从默认页进账号密码页的「QQ 账号密码登录」入口</summary>
    private static readonly (int X, int Y) QqPasswordEntry = (150, 633);

    /// <summary>「启动」坐标量的基准窗口尺寸（登录后主窗口约 1600×900, 右下角「启动」; 精确值留实机核对）</summary>
    private static readonly (int W, int H) StartButtonBasis = (1600, 900);

    /// <summary>主窗口右下角的「启动」（基于 <see cref="StartButtonBasis" />, 实机核对前的估算值）</summary>
    private static readonly (int X, int Y) StartButton = (1480, 835);

    /// <summary>整段输入最多重来几次（前台被抢走一次算一次）</summary>
    public int MaxAttempts { get; init; } = 3;

    /// <summary>每个硬件动作之后停顿多久（等窗口反应）</summary>
    public TimeSpan ActionPause { get; init; } = TimeSpan.FromMilliseconds(150);

    /// <summary>导航到账号密码页时每点一下等多久</summary>
    public TimeSpan NavPause { get; init; } = TimeSpan.FromMilliseconds(400);

    /// <summary>导航到账号密码页最多试几轮</summary>
    public int NavRounds { get; init; } = 3;

    /// <summary>点了登录后等登录结果的上限</summary>
    public TimeSpan LoginResultTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>等登录结果时多久看一次</summary>
    public TimeSpan LoginResultPoll { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    ///     跑一遍自动登录: 找硬件 → 导航到 QQ 账号密码页 → 填账号密码、点登录 → 等登录结果。
    ///     停在等手机确认时返回 <see cref="CatWeGameHwLoginOutcome.NeedsPhoneConfirm" />, 由上层提醒客户后再调 <see cref="ResumeAfterPhoneConfirmAsync" />。
    /// </summary>
    public async Task<CatWeGameHwLoginResult> RunAsync(string account, CatSecret password, CancellationToken cancellationToken)
    {
        if (!keyboard.TryOpen())
        {
            Log.Information("[CatHost] 本机没有可用的硬件键鼠, WeGame 退回员工手动登录");
            return CatWeGameHwLoginResult.Fallback("本机未接硬件键鼠, 请在这台电脑手动登录");
        }

        if (await NavigateToQqPasswordAsync(cancellationToken).ConfigureAwait(false) is { } navFailure)
            return navFailure;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (await TryTypeAndLoginAsync(account, password, cancellationToken).ConfigureAwait(false))
            {
                case AttemptResult.Ok:
                    return await AwaitLoginResultAsync(false, cancellationToken).ConfigureAwait(false);

                case AttemptResult.Unmappable:
                    return CatWeGameHwLoginResult.Fallback("账号或密码里有硬件键盘打不出的字符, 请在这台电脑手动登录");

                case AttemptResult.NoWindow:
                    return CatWeGameHwLoginResult.Fallback("找不到 WeGame 登录窗口, 请在这台电脑手动登录");

                default: // Interrupted
                    Log.Warning("[CatHost] WeGame 硬件自动输入第 {Attempt} 次被打断（前台已不是登录窗）, 重来", attempt);
                    await DelayAsync(ActionPause, cancellationToken).ConfigureAwait(false);
                    continue;
            }
        }

        return CatWeGameHwLoginResult.Fallback("自动输入多次被打断, 请在这台电脑手动登录");
    }

    /// <summary>
    ///     客户在手机 QQ 上确认后接着等: 继续等主窗口出现、点「启动」; 这一段不再把「等确认」当新情况报出去
    /// </summary>
    public Task<CatWeGameHwLoginResult> ResumeAfterPhoneConfirmAsync(CancellationToken cancellationToken) =>
        AwaitLoginResultAsync(true, cancellationToken);

    /// <summary>一次输入尝试的结果</summary>
    private enum AttemptResult
    {
        /// <summary>账号密码都输完、登录也点了</summary>
        Ok,

        /// <summary>前台被抢走, 本次中止（可重来）</summary>
        Interrupted,

        /// <summary>账号或密码含硬件键盘打不出的字符</summary>
        Unmappable,

        /// <summary>找不到登录窗口</summary>
        NoWindow
    }

    /// <summary>
    ///     导航到 QQ 账号密码页: 已经在就不动; 不在就点 QQ 页签、再点「QQ 账号密码登录」。几轮后仍明确不是账号密码页才退回手动; 认不出就按在页上继续试
    /// </summary>
    private async Task<CatWeGameHwLoginResult?> NavigateToQqPasswordAsync(CancellationToken cancellationToken)
    {
        for (var round = 0; round < NavRounds; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var window = screen.CaptureLoginWindow();

            if (window == null)
            {
                await DelayAsync(NavPause, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (screen.OnQqPasswordPage() == true)
                return null;

            ClickOnWindow(window, QqTab);
            await DelayAsync(NavPause, cancellationToken).ConfigureAwait(false);
            ClickOnWindow(window, QqPasswordEntry);
            await DelayAsync(NavPause, cancellationToken).ConfigureAwait(false);
        }

        return screen.OnQqPasswordPage() == false
                   ? CatWeGameHwLoginResult.Fallback("进不了 QQ 账号密码登录页, 请在这台电脑手动登录")
                   : null;
    }

    /// <summary>
    ///     一次尝试: 切前台 → 账号（已是目标号则跳过, 否则清空重输）→ 密码 → 点登录。每个动作前核对前台仍是登录窗, 不是就中止
    /// </summary>
    private async Task<AttemptResult> TryTypeAndLoginAsync(string account, CatSecret password, CancellationToken cancellationToken)
    {
        if (foreground.LoginWindow() is not { } rect)
            return AttemptResult.NoWindow;

        foreground.FocusLoginWindow();

        if (!foreground.LoginWindowIsForeground())
            return AttemptResult.Interrupted;

        if (!screen.AccountMatches(account))
        {
            if (!await HwClickAsync(AccountBox, rect, cancellationToken).ConfigureAwait(false))
                return AttemptResult.Interrupted;

            if (!await HwPressAsync(HidModifiers.Ctrl, HidKey.A, cancellationToken).ConfigureAwait(false)
                || !await HwPressAsync(HidModifiers.None, HidKey.Delete, cancellationToken).ConfigureAwait(false))
                return AttemptResult.Interrupted;

            var typedAccount = await TypeGuardedAsync(account, cancellationToken).ConfigureAwait(false);

            if (typedAccount != AttemptResult.Ok)
                return typedAccount;
        }

        if (!await HwClickAsync(PasswordBox, rect, cancellationToken).ConfigureAwait(false))
            return AttemptResult.Interrupted;

        var typedPassword = await TypeGuardedAsync(password.Reveal(), cancellationToken).ConfigureAwait(false);

        if (typedPassword != AttemptResult.Ok)
            return typedPassword;

        return await HwClickAsync(LoginButton, rect, cancellationToken).ConfigureAwait(false)
                   ? AttemptResult.Ok
                   : AttemptResult.Interrupted;
    }

    /// <summary>
    ///     等登录结果: 登录窗没了 + 主窗口出现 = 成功（点「启动」）; 密码错 = 不重试; 停在等手机确认 = 交回提醒客户; 超时 = 退回手动
    /// </summary>
    private async Task<CatWeGameHwLoginResult> AwaitLoginResultAsync(bool phoneConfirmReported, CancellationToken cancellationToken)
    {
        var deadline = Environment.TickCount64 + (long)LoginResultTimeout.TotalMilliseconds;

        while (Environment.TickCount64 <= deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (foreground.MainWindow() is { } main && foreground.LoginWindow() is null)
            {
                Log.Information("[CatHost] WeGame 登录成功, 点「启动」进游戏");
                await ClickStartAsync(main, cancellationToken).ConfigureAwait(false);
                return CatWeGameHwLoginResult.Success();
            }

            if (screen.PasswordRejected())
            {
                Log.Warning("[CatHost] WeGame 提示密码错误, 不重试, 退回请员工核对");
                return CatWeGameHwLoginResult.PasswordRejected;
            }

            if (!phoneConfirmReported && screen.AwaitingPhoneConfirm())
            {
                Log.Information("[CatHost] WeGame 停在等手机 QQ 确认, 提醒客户");
                return CatWeGameHwLoginResult.NeedsPhoneConfirm;
            }

            await DelayAsync(LoginResultPoll, cancellationToken).ConfigureAwait(false);
        }

        return CatWeGameHwLoginResult.Fallback("等 WeGame 登录结果超时, 请在这台电脑手动登录");
    }

    /// <summary>登录成功后点主窗口右下角的「启动」</summary>
    private async Task ClickStartAsync(CatWeGameWindowRect main, CancellationToken cancellationToken)
    {
        var (x, y) = ScaleTo(StartButton, StartButtonBasis, main.ClientWidth, main.ClientHeight);
        var size   = foreground.PrimaryScreen();
        await keyboard.ClickAsync(main.ScreenX + x, main.ScreenY + y, size.Width, size.Height, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>逐字符硬件输入, 每个字符前核对前台仍是登录窗; 遇硬件键盘打不出的字符返回 Unmappable</summary>
    private async Task<AttemptResult> TypeGuardedAsync(string text, CancellationToken cancellationToken)
    {
        foreach (var c in text)
        {
            if (!foreground.LoginWindowIsForeground())
                return AttemptResult.Interrupted;

            if (!await keyboard.TypeTextAsync(c.ToString(), cancellationToken).ConfigureAwait(false))
                return AttemptResult.Unmappable;
        }

        return AttemptResult.Ok;
    }

    /// <summary>硬件鼠标点登录窗口里的一个（1210×680 基准）位置; 前台已不是登录窗时不点、返回 false</summary>
    private async Task<bool> HwClickAsync((int X, int Y) point, CatWeGameWindowRect rect, CancellationToken cancellationToken)
    {
        if (!foreground.LoginWindowIsForeground())
            return false;

        var (x, y) = CatWeGameChallengeWatcher.Scale(point, rect.ClientWidth, rect.ClientHeight);
        var size   = foreground.PrimaryScreen();
        await keyboard.ClickAsync(rect.ScreenX + x, rect.ScreenY + y, size.Width, size.Height, cancellationToken).ConfigureAwait(false);
        await DelayAsync(ActionPause, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>硬件按一个（带修饰键的）键; 前台已不是登录窗时不按、返回 false</summary>
    private async Task<bool> HwPressAsync(HidModifiers modifiers, HidKey key, CancellationToken cancellationToken)
    {
        if (!foreground.LoginWindowIsForeground())
            return false;

        await keyboard.PressAsync(modifiers, key, cancellationToken).ConfigureAwait(false);
        await DelayAsync(ActionPause, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>用后台截图管线点登录窗口里的一个（1210×680 基准）位置（导航用, 不需要前台）</summary>
    private void ClickOnWindow(CatWeGameLoginWindow window, (int X, int Y) point)
    {
        var (x, y) = CatWeGameChallengeWatcher.Scale(point, window.Width, window.Height);
        screen.ClickLoginWindow(x, y);
    }

    /// <summary>把按某个基准尺寸量的点换算到实际客户区尺寸</summary>
    private static (int X, int Y) ScaleTo((int X, int Y) point, (int W, int H) basis, int width, int height) =>
        (
            (int)Math.Round(point.X * (double)width  / basis.W, MidpointRounding.AwayFromZero),
            (int)Math.Round(point.Y * (double)height / basis.H, MidpointRounding.AwayFromZero)
        );

    private static async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (delay > TimeSpan.Zero)
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
    }
}
