using Serilog;

namespace XIVLauncher.CatHost;

/// <summary>硬件自动登录走到最后是哪一种结局</summary>
public enum CatWeGameHwLoginOutcome
{
    /// <summary>登录成功、并点开了游戏, 之后照常由 version.dll 取登录信息</summary>
    Success,

    /// <summary>退回员工手动（带原因）: 没接硬件、输入被打断、进不了账号密码页、超时等, 都不算上号失败</summary>
    FallBackToManual,

    /// <summary>WeGame 提示密码错误: 不反复重试（避免锁号）, 退回请员工联系客户核对</summary>
    PasswordRejected
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
}

/// <summary>
///     在 WeGame 登录窗口里自动输入 QQ 账号密码登录, 登录后点开游戏。全程不动鼠标: 点击都是发给窗口的后台鼠标消息, 只有打字用本机的硬件键盘（CH9329,
///     WeGame 的密码框挡掉软件模拟的按键）。
///     <para>
///         步骤: 等登录窗画好 → 点 QQ 页签、「QQ 账号密码登录」进账号密码页 → 切前台 → 点账号框、清空、打账号 → 把焦点放进密码框、清空、打密码 → 点「登录」
///         → 等登录结果 → 主界面上点「启动」（首次启动先勾协议、确认选大区）, 直到游戏的登录组件起来。
///     </para>
///     <para>
///         硬件按键只落到前台窗口里有焦点的控件, 所以每个按键前都核对前台和焦点, 不对就中止本次、整段重来（最多 <see cref="MaxAttempts" /> 次）;
///         仍不成功、本机没接硬件、账号密码含硬件键盘打不出的字符时, 都退回员工手动（不判上号失败）。密码错误不重试。
///         点了登录之后的图片验证码与手机 QQ 确认由 <see cref="CatWeGameChallengeWatcher" /> 报给客户; 客户在做验证期间不按 <see cref="LoginResultTimeout" /> 超时。
///     </para>
///     <para>登录窗坐标按 StartFor 拉起的 1210×680 量, 主界面按 1600×900 量, 都按实际客户区尺寸换算（11-08 开发机实测）。</para>
/// </summary>
public sealed class CatWeGameHwLoginFlow(ICatHidKeyboard keyboard, ICatWeGameScreen screen, ICatWeGameForeground foreground)
{
    /// <summary>账号下拉框（1210×680 客户区坐标; 是下拉框, 换号要先清空）</summary>
    private static readonly (int X, int Y) AccountBox = (150, 336);

    /// <summary>「登录」按钮（橙色实心）</summary>
    private static readonly (int X, int Y) LoginButton = (150, 466);

    /// <summary>QQ 页签</summary>
    private static readonly (int X, int Y) QqTab = (125, 277);

    /// <summary>QQ 页签下「快捷安全登录」页底部的「QQ 账号密码登录」</summary>
    private static readonly (int X, int Y) QqPasswordEntry = (150, 633);

    /// <summary>清空密码框时连按几下退格（记住的密码显示 10 个点, QQ 密码最长 16 位）</summary>
    private const int PASSWORD_CLEAR_KEYS = 24;

    /// <summary>退格分几下一组按, 每组之前核对焦点还在密码框里</summary>
    private const int PASSWORD_CLEAR_CHUNK = 8;

    /// <summary>整段输入最多重来几次（前台或焦点被抢走一次算一次）</summary>
    public int MaxAttempts { get; init; } = 3;

    /// <summary>每个动作之后停顿多久（等窗口反应）</summary>
    public TimeSpan ActionPause { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>导航到账号密码页时每点一下等多久</summary>
    public TimeSpan NavPause { get; init; } = TimeSpan.FromMilliseconds(1200);

    /// <summary>导航到账号密码页最多试几轮</summary>
    public int NavRounds { get; init; } = 3;

    /// <summary>等登录窗口出现并画好（页签画出来）的上限</summary>
    public TimeSpan WindowTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>点了登录后等登录结果的上限; 登录窗里有弹窗（客户在做验证）时从最后一次看到弹窗起重新算</summary>
    public TimeSpan LoginResultTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>等窗口、等登录结果、等游戏启动时多久看一次</summary>
    public TimeSpan LoginResultPoll { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>登录后点开游戏的上限</summary>
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>点了「启动」后按钮又变回能点的样子, 隔多久再点一次</summary>
    public TimeSpan StartRetry { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>勾了协议后等多久再看勾上没有（没勾上才再点）</summary>
    public TimeSpan AgreeSettle { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>本机时间, 测试可替换（判断游戏登录组件是不是点了「启动」之后才起来的）</summary>
    public Func<DateTime> Now { get; init; } = () => DateTime.Now;

    /// <summary>
    ///     跑一遍自动登录
    /// </summary>
    public async Task<CatWeGameHwLoginResult> RunAsync(string account, CatSecret password, CancellationToken cancellationToken)
    {
        if (!keyboard.TryOpen())
        {
            Log.Information("[CatHost] 本机没有可用的硬件键鼠, WeGame 退回员工手动登录");
            return CatWeGameHwLoginResult.Fallback("本机未接硬件键鼠, 请在这台电脑手动登录");
        }

        if (!CatHid9329Protocol.CanType(account) || !CatHid9329Protocol.CanType(password.Reveal()))
            return CatWeGameHwLoginResult.Fallback("账号或密码里有硬件键盘打不出的字符, 请在这台电脑手动登录");

        if (await WaitForLoginWindowAsync(cancellationToken).ConfigureAwait(false) is { } windowFailure)
            return windowFailure;

        if (await NavigateToQqPasswordAsync(cancellationToken).ConfigureAwait(false) is { } navFailure)
            return navFailure;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (await TryTypeAndLoginAsync(account, password, cancellationToken).ConfigureAwait(false))
            {
                case AttemptResult.Ok:
                    var login = await AwaitLoginResultAsync(cancellationToken).ConfigureAwait(false);
                    return login.Outcome == CatWeGameHwLoginOutcome.Success
                               ? await StartGameAsync(cancellationToken).ConfigureAwait(false)
                               : login;

                case AttemptResult.NoWindow:
                    return CatWeGameHwLoginResult.Fallback("找不到 WeGame 登录窗口, 请在这台电脑手动登录");

                default: // Interrupted
                    Log.Warning("[CatHost] WeGame 自动输入第 {Attempt} 次被打断（前台或焦点已不在登录窗）, 重来", attempt);
                    await DelayAsync(ActionPause, cancellationToken).ConfigureAwait(false);
                    continue;
            }
        }

        return CatWeGameHwLoginResult.Fallback("自动输入多次被打断, 请在这台电脑手动登录");
    }

    /// <summary>一次输入尝试的结果</summary>
    private enum AttemptResult
    {
        /// <summary>账号密码都输完、登录也点了</summary>
        Ok,

        /// <summary>前台或焦点被抢走, 本次中止（可重来）</summary>
        Interrupted,

        /// <summary>找不到登录窗口</summary>
        NoWindow
    }

    /// <summary>
    ///     等登录窗口出现并画好: WeGame 刚拉起时窗口在但里面还是空的, 页签画出来才能点
    /// </summary>
    private async Task<CatWeGameHwLoginResult?> WaitForLoginWindowAsync(CancellationToken cancellationToken)
    {
        var deadline = Environment.TickCount64 + (long)WindowTimeout.TotalMilliseconds;

        while (Environment.TickCount64 <= deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (screen.CaptureLoginWindow() is { TabsVisible: true })
            {
                await DelayAsync(NavPause, cancellationToken).ConfigureAwait(false);
                return null;
            }

            await DelayAsync(LoginResultPoll, cancellationToken).ConfigureAwait(false);
        }

        return CatWeGameHwLoginResult.Fallback("WeGame 登录窗口没有出现, 请在这台电脑手动登录");
    }

    /// <summary>
    ///     导航到 QQ 账号密码页（密码框露出来）: 已经在就不动; 不在就点 QQ 页签、再点「QQ 账号密码登录」, 几轮后仍不在就退回手动
    /// </summary>
    private async Task<CatWeGameHwLoginResult?> NavigateToQqPasswordAsync(CancellationToken cancellationToken)
    {
        for (var round = 0; round < NavRounds; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (foreground.PasswordBoxVisible())
                return null;

            ClickOnLoginWindow(QqTab);
            await DelayAsync(NavPause, cancellationToken).ConfigureAwait(false);

            if (foreground.PasswordBoxVisible())
                return null;

            ClickOnLoginWindow(QqPasswordEntry);
            await DelayAsync(NavPause, cancellationToken).ConfigureAwait(false);
        }

        if (foreground.PasswordBoxVisible())
            return null;

        Log.Warning("[CatHost] 点了 {Rounds} 轮还没进到 WeGame 的 QQ 账号密码登录页", NavRounds);
        return CatWeGameHwLoginResult.Fallback("进不了 QQ 账号密码登录页, 请在这台电脑手动登录");
    }

    /// <summary>
    ///     一次尝试: 切前台 → 点账号框、清空、打账号 → 焦点放进密码框、清空、打密码 → 点登录。每个按键前核对前台与焦点, 不对就中止
    /// </summary>
    private async Task<AttemptResult> TryTypeAndLoginAsync(string account, CatSecret password, CancellationToken cancellationToken)
    {
        if (foreground.LoginWindow() is null)
            return AttemptResult.NoWindow;

        if (!foreground.FocusLoginWindow())
            return AttemptResult.Interrupted;

        if (!ClickOnLoginWindow(AccountBox))
            return AttemptResult.NoWindow;

        await DelayAsync(ActionPause, cancellationToken).ConfigureAwait(false);

        if (!await PressGuardedAsync(foreground.LoginWindowIsForeground, HidModifiers.Ctrl, HidKey.A, 1, cancellationToken).ConfigureAwait(false)
            || !await PressGuardedAsync(foreground.LoginWindowIsForeground, HidModifiers.None, HidKey.Delete, 1, cancellationToken).ConfigureAwait(false)
            || !await TypeGuardedAsync(foreground.LoginWindowIsForeground, account, cancellationToken).ConfigureAwait(false))
            return AttemptResult.Interrupted;

        if (!foreground.FocusPasswordBox())
        {
            Log.Warning("[CatHost] WeGame 密码框拿不到焦点");
            return AttemptResult.Interrupted;
        }

        // 点密码框时光标可能落在中间: 先到末尾再退格, 连记住的密码一起清掉
        if (!await PressGuardedAsync(foreground.PasswordBoxFocused, HidModifiers.None, HidKey.End, 1, cancellationToken).ConfigureAwait(false))
            return AttemptResult.Interrupted;

        for (var cleared = 0; cleared < PASSWORD_CLEAR_KEYS; cleared += PASSWORD_CLEAR_CHUNK)
        {
            if (!await PressGuardedAsync(foreground.PasswordBoxFocused, HidModifiers.None, HidKey.Backspace, PASSWORD_CLEAR_CHUNK, cancellationToken).ConfigureAwait(false))
                return AttemptResult.Interrupted;
        }

        if (!await TypeGuardedAsync(foreground.PasswordBoxFocused, password.Reveal(), cancellationToken).ConfigureAwait(false))
            return AttemptResult.Interrupted;

        await DelayAsync(ActionPause, cancellationToken).ConfigureAwait(false);
        Log.Information("[CatHost] WeGame 账号密码已输入, 点「登录」");
        return ClickOnLoginWindow(LoginButton) ? AttemptResult.Ok : AttemptResult.NoWindow;
    }

    /// <summary>
    ///     等登录结果: 登录窗没了 + 主界面出现 = 成功; 密码错 = 不重试; 超时 = 退回手动。
    ///     登录窗里有弹窗（图片验证码、手机 QQ 确认, 客户正在做）时把截止时间往后顺延, 外层等登录的总时限照旧兜底
    /// </summary>
    private async Task<CatWeGameHwLoginResult> AwaitLoginResultAsync(CancellationToken cancellationToken)
    {
        var deadline = Environment.TickCount64 + (long)LoginResultTimeout.TotalMilliseconds;

        while (Environment.TickCount64 <= deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (foreground.LoginWindow() is null && screen.CaptureMainWindow() is not null)
            {
                Log.Information("[CatHost] WeGame 登录成功, 去点「启动」");
                return CatWeGameHwLoginResult.Success();
            }

            if (screen.PasswordRejected())
            {
                Log.Warning("[CatHost] WeGame 提示密码错误, 不重试, 退回请员工核对");
                return CatWeGameHwLoginResult.PasswordRejected;
            }

            if (screen.CaptureLoginWindow()?.Popup != null)
                deadline = Environment.TickCount64 + (long)LoginResultTimeout.TotalMilliseconds;

            await DelayAsync(LoginResultPoll, cancellationToken).ConfigureAwait(false);
        }

        return CatWeGameHwLoginResult.Fallback("等 WeGame 登录结果超时, 请在这台电脑手动登录");
    }

    /// <summary>
    ///     登录后点开游戏: 主界面上点「启动」; 首次启动会先弹协议勾选框（勾「以上所有内容」再点「启动」）和选大区对话框（点「确定」）。
    ///     游戏的登录组件 sdologin 起来就算成功（version.dll 在它里面取登录信息, 外层随后收到）。按钮点了又变回能点时隔 <see cref="StartRetry" /> 再点
    /// </summary>
    private async Task<CatWeGameHwLoginResult> StartGameAsync(CancellationToken cancellationToken)
    {
        var since     = Now().AddSeconds(-1);
        var deadline  = Environment.TickCount64 + (long)StartTimeout.TotalMilliseconds;
        long? clicked = null;
        long? agreed  = null;

        while (Environment.TickCount64 <= deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (foreground.GameLoginStartedSince(since))
            {
                Log.Information("[CatHost] WeGame 已拉起游戏的登录组件, 等取登录信息");
                return CatWeGameHwLoginResult.Success();
            }

            if (screen.ConfirmRegionDialog())
            {
                Log.Information("[CatHost] WeGame 弹出选大区, 按默认大区点了「确定」");
                await DelayAsync(LoginResultPoll, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (screen.CaptureMainWindow() is { } main)
            {
                var sinceClick = clicked is { } at ? Environment.TickCount64 - at : long.MaxValue;

                if (main.AgreementOpen && !main.AgreeAllChecked)
                {
                    // 勾选框点一下切换一次: 上一下还没画出来时不再点, 免得又勾掉
                    if (agreed is not { } tick || Environment.TickCount64 - tick >= (long)AgreeSettle.TotalMilliseconds)
                    {
                        Log.Information("[CatHost] WeGame 首次启动要勾协议, 勾「我已阅读同意以上所有内容」");
                        ClickOnMainWindow(main, CatWeGameMainReader.AgreeAll);
                        agreed = Environment.TickCount64;
                    }
                }
                // 勾好协议后马上再点; 平时点过一次要等按钮又变回能点、且隔够 StartRetry 才再点
                else if (main.AgreementOpen && sinceClick >= (long)ActionPause.TotalMilliseconds
                         || main.StartReady && sinceClick >= (long)StartRetry.TotalMilliseconds)
                {
                    Log.Information("[CatHost] 点 WeGame 主界面的「启动」");
                    ClickOnMainWindow(main, CatWeGameMainReader.StartButton);
                    clicked = Environment.TickCount64;
                }
            }

            await DelayAsync(LoginResultPoll, cancellationToken).ConfigureAwait(false);
        }

        return CatWeGameHwLoginResult.Fallback("已登录 WeGame, 但没能自动启动游戏, 请在这台电脑的 WeGame 里点「启动」");
    }

    /// <summary>逐字符硬件输入, 每个字符前核对 <paramref name="ready" />（前台 / 焦点）, 不对就停下返回 false</summary>
    private async Task<bool> TypeGuardedAsync(Func<bool> ready, string text, CancellationToken cancellationToken)
    {
        foreach (var c in text)
        {
            if (!ready())
                return false;

            if (!await keyboard.TypeTextAsync(c.ToString(), cancellationToken).ConfigureAwait(false))
                return false;
        }

        return true;
    }

    /// <summary>核对 <paramref name="ready" /> 后硬件按一个（带修饰键的）键 <paramref name="count" /> 次; 不对就不按、返回 false</summary>
    private async Task<bool> PressGuardedAsync(Func<bool> ready, HidModifiers modifiers, HidKey key, int count, CancellationToken cancellationToken)
    {
        if (!ready())
            return false;

        await keyboard.PressAsync(modifiers, key, cancellationToken, count).ConfigureAwait(false);
        await DelayAsync(ActionPause, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>后台点登录窗口里的一个（1210×680 基准）位置</summary>
    private bool ClickOnLoginWindow((int X, int Y) point)
    {
        if (screen.CaptureLoginWindow() is not { } window)
            return false;

        var (x, y) = CatWeGameChallengeWatcher.Scale(point, window.Width, window.Height);
        return screen.ClickLoginWindow(x, y);
    }

    /// <summary>后台点主界面里的一个（1600×900 基准）位置</summary>
    private void ClickOnMainWindow(CatWeGameMainWindow main, (int X, int Y) point)
    {
        var (x, y) = CatWeGameMainReader.Scale(point, main.Width, main.Height);
        screen.ClickMainWindow(x, y);
    }

    private static async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (delay > TimeSpan.Zero)
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
    }
}
