using Serilog;

namespace XIVLauncher.CatHost;

/// <summary>
///     WeGame 登录后自动点开游戏: 主界面上点「启动」; 首次启动会先弹协议勾选框（勾「以上所有内容」再点「启动」）和选大区对话框（点「确定」）。
///     游戏的登录组件 sdologin 起来就算成功（version.dll 在它里面取登录信息）。全程是发给窗口的后台鼠标消息, 不用硬件、不动鼠标,
///     所以扫码、员工手动、硬件自动输入哪种方式登录都用它。
///     <para>主界面按 1600×900 量, 按实际客户区尺寸换算（11-08 开发机实测）。</para>
/// </summary>
public sealed class CatWeGameAutoStart(ICatWeGameScreen screen, ICatWeGameForeground foreground)
{
    /// <summary>没能点开游戏时给工作室员工看的话</summary>
    public const string START_FAILED = "已登录 WeGame, 但没能自动启动游戏, 请在这台电脑的 WeGame 里点「启动」";

    /// <summary>勾好协议后再点「启动」前的停顿</summary>
    public TimeSpan ActionPause { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>等登录完成、等游戏启动时多久看一次</summary>
    public TimeSpan Poll { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>主界面出现后点开游戏的上限</summary>
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>点了「启动」后按钮又变回能点的样子, 隔多久再点一次</summary>
    public TimeSpan StartRetry { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>勾了协议后等多久再看勾上没有（没勾上才再点）</summary>
    public TimeSpan AgreeSettle { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>本机时间, 测试可替换（判断游戏登录组件是不是点了「启动」之后才起来的）</summary>
    public Func<DateTime> Now { get; init; } = () => DateTime.Now;

    /// <summary>
    ///     等 WeGame 登录完成（登录窗没了、主界面出现）后点开游戏。一直等到点开或取消; 点开了返回 true, 主界面出现后 <see cref="StartTimeout" /> 内没点开返回 false
    /// </summary>
    public async Task<bool> RunAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (LoggedIn())
            {
                Log.Information("[CatHost] WeGame 已登录, 去点「启动」");
                return await StartGameAsync(cancellationToken).ConfigureAwait(false);
            }

            await DelayAsync(Poll, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>登录窗没了、主界面出现 = 已登录</summary>
    public bool LoggedIn() =>
        foreground.LoginWindow() is null && screen.CaptureMainWindow() is not null;

    /// <summary>
    ///     在主界面上点开游戏, 直到游戏的登录组件起来（返回 true）或超过 <see cref="StartTimeout" />（返回 false）。按钮点了又变回能点时隔 <see cref="StartRetry" /> 再点
    /// </summary>
    public async Task<bool> StartGameAsync(CancellationToken cancellationToken)
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
                return true;
            }

            if (screen.ConfirmRegionDialog())
            {
                Log.Information("[CatHost] WeGame 弹出选大区, 按默认大区点了「确定」");
                await DelayAsync(Poll, cancellationToken).ConfigureAwait(false);
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

            await DelayAsync(Poll, cancellationToken).ConfigureAwait(false);
        }

        Log.Warning("[CatHost] 已登录 WeGame, 但 {Timeout} 内没能自动启动游戏", StartTimeout);
        return false;
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
