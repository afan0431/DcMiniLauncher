using Serilog;

namespace XIVLauncher.CatHost;

/// <summary>launch 的 weGameScan: 等 WeGame 登录时自动切到哪种扫码页</summary>
public enum CatWeGameScan
{
    /// <summary>QQ 扫码</summary>
    Qq,

    /// <summary>微信扫码</summary>
    WeChat
}

/// <summary>weGameScan 在协议里的取值</summary>
public static class CatWeGameScans
{
    /// <summary>QQ 扫码</summary>
    public const string QQ = "qq";

    /// <summary>微信扫码</summary>
    public const string WE_CHAT = "weChat";

    /// <summary>
    ///     解析 weGameScan: 没带（或为空白）= 不切换, 取值不分大小写; 不认识的取值返回 false
    /// </summary>
    public static bool TryParse(string? value, out CatWeGameScan? scan)
    {
        scan = null;

        if (string.IsNullOrWhiteSpace(value))
            return true;

        var trimmed = value.Trim();

        if (string.Equals(trimmed, QQ, StringComparison.OrdinalIgnoreCase))
            scan = CatWeGameScan.Qq;
        else if (string.Equals(trimmed, WE_CHAT, StringComparison.OrdinalIgnoreCase))
            scan = CatWeGameScan.WeChat;

        return scan != null;
    }

    /// <summary>协议里的取值</summary>
    public static string Name(CatWeGameScan scan) =>
        scan == CatWeGameScan.WeChat ? WE_CHAT : QQ;
}

/// <summary>weGame.challenge 的 kind</summary>
public static class CatWeGameChallengeKinds
{
    /// <summary>登录二维码, 要客户用另一台设备扫</summary>
    public const string QRCODE = "qrcode";

    /// <summary>设备验证, 要客户用密保手机发一条短信</summary>
    public const string SMS = "sms";
}

/// <summary>
///     等 WeGame 登录期间要客户配合的一项验证（weGame.challenge 的内容）。
///     二维码: image = 二维码 PNG 的 base64, link = 二维码内容; 短信: code = 要发的内容, phone = 发到哪个号码, text = 窗口原文
/// </summary>
public sealed record CatWeGameChallenge
(
    string  Kind,
    string  ChallengeId,
    string? Image            = null,
    string? Link             = null,
    int?    ExpiresInSeconds = null,
    string? Code             = null,
    string? Phone            = null,
    string? Text             = null
)
{
    /// <summary>
    ///     record 自动生成的 ToString 会打印所有成员, 这里只留种类和编号, 免得二维码内容、短信内容被写进日志
    /// </summary>
    public override string ToString() =>
        $"CatWeGameChallenge {{ Kind = {Kind}, ChallengeId = {ChallengeId} }}";
}

/// <summary>weGame.confirmSms 参数</summary>
public sealed record CatWeGameConfirmSmsParams(string? ChallengeId);

/// <summary>
///     WeGame 登录窗口的一次截取: 宽高是窗口客户区的实际大小; 窗口上有二维码时带二维码内容和一张可以直接给客户扫的 PNG;
///     PanelThumb 是登录栏那一块画面缩成的小图（每格一个亮度, null = 没算）, 用来看画面稳没稳、点击之后变没变;
///     TabsVisible = QQ / 微信两个页签画出来了没有（WeGame 刚启动的头两三秒窗口在但里面还是空的）;
///     QrExpired = 窗口上的二维码已经失效（变暗、中间一个刷新图标, 要点一下才出新的）, 这时不带二维码内容
/// </summary>
public sealed record CatWeGameLoginWindow(int Width, int Height, string? QrLink = null, byte[]? QrPng = null, byte[]? PanelThumb = null, bool TabsVisible = true, bool QrExpired = false)
{
    /// <summary>两格亮度差在这以内算一样（登录栏是半透明的, 后面的动态背景会让画面有很轻微的浮动）</summary>
    public const int THUMB_TOLERANCE = 10;

    /// <summary>
    ///     两次截取的登录栏画面是不是一样; 有一边没算小图时返回 null（分不清）
    /// </summary>
    public static bool? SamePanel(CatWeGameLoginWindow? left, CatWeGameLoginWindow? right)
    {
        if (left?.PanelThumb is not { } a || right?.PanelThumb is not { } b)
            return null;

        if (a.Length != b.Length)
            return false;

        for (var i = 0; i < a.Length; i++)
        {
            if (Math.Abs(a[i] - b[i]) > THUMB_TOLERANCE)
                return false;
        }

        return true;
    }
}

/// <summary>
///     设备验证窗口的内容: 要发的短信内容、发到哪个号码、窗口原文
/// </summary>
public sealed record CatWeGameSmsPrompt(string Code, string Phone, string Text);

/// <summary>
///     等 WeGame 登录期间对本机屏幕的操作（找窗口、截窗识别二维码、发点击、读设备验证窗口）, 单独抽出来好让测试替换
/// </summary>
public interface ICatWeGameScreen
{
    /// <summary>
    ///     登录窗口被最小化了就把它还原到所有窗口的最下面, 不给焦点（最小化的窗口截不到画面, 二维码就看不到、刷新不了）;
    ///     返回这次有没有还原。只在要了自动切到扫码页的上号里调用
    /// </summary>
    bool RestoreMinimizedLoginWindow() =>
        false;

    /// <summary>
    ///     截取 WeGame 登录窗口并识别上面的二维码; 没有登录窗口（没出现、已登录、最小化）时返回 null
    /// </summary>
    CatWeGameLoginWindow? CaptureLoginWindow();

    /// <summary>
    ///     在登录窗口的客户区坐标上点一下（不改变前台窗口）; 登录窗口不在时返回 false
    /// </summary>
    bool ClickLoginWindow(int x, int y);

    /// <summary>
    ///     当前的设备验证窗口; 没有时返回 null
    /// </summary>
    CatWeGameSmsPrompt? FindSmsPrompt();

    /// <summary>
    ///     点设备验证窗口上的「确定」; 窗口不在时返回 false
    /// </summary>
    bool ConfirmSmsPrompt();
}

/// <summary>
///     等 WeGame 登录期间（拉起 WeGame 之后, 到取到登录信息、取消或超时）定时看 WeGame 的窗口,
///     把要客户配合的验证报给工作台: 登录二维码（weGame.challenge kind=qrcode）、设备验证短信（kind=sms）。
///     <para>
///         launch 带了 weGameScan 时, 登录窗口出现后先自动切到对应的扫码页: 给窗口发鼠标消息点页签, 每点一下等一会儿再用识别结果核对;
///         几轮都没有二维码就报 weGame.scanSwitchFailed, 之后只看不点。没带 weGameScan 时从头到尾只看不点。
///     </para>
///     <para>工作台外壳靠程序集里有没有这个类型名判断启动器是否支持把验证转给客户, 名字和命名空间不能改。</para>
/// </summary>
public sealed class CatWeGameChallengeWatcher(ICatWeGameScreen screen, ICatLaunchReporter reporter, CatLogRedactor redactor, CatWeGameScan? scan)
{
    /// <summary>下面几个点击位置对应的登录窗口宽度, 实际点击时按窗口实际大小换算</summary>
    public const int DESIGN_WIDTH = 1210;

    /// <summary>下面几个点击位置对应的登录窗口高度</summary>
    public const int DESIGN_HEIGHT = 680;

    /// <summary>自动切扫码页最多试几轮</summary>
    public const int MAX_SWITCH_ROUNDS = 3;

    /// <summary>切好后窗口自己换页时最多再切几次</summary>
    public const int MAX_RESETTLES = 2;

    /// <summary>连续几轮看不到才算验证已经不在了（窗口重绘的瞬间会识别不到）</summary>
    public const int MISSES_TO_CLEAR = 2;

    /// <summary>QQ 页签</summary>
    internal static readonly (int X, int Y) QqTab = (125, 277);

    /// <summary>微信页签</summary>
    internal static readonly (int X, int Y) WeChatTab = (175, 277);

    /// <summary>QQ 账号密码页底部右侧的「QQ 扫码登录」</summary>
    internal static readonly (int X, int Y) QqScanEntry = (208, 630);

    /// <summary>
    ///     QQ 页底部正中的「QQ 账号密码登录」: 快捷安全登录页和扫码页上都有, 点了到账号密码页; 账号密码页上这个位置是空的。
    ///     QQ 页会停在快捷安全登录、账号密码、扫码三种之一, 只有账号密码页上有「QQ 扫码登录」, 所以先点它再点扫码入口, 从哪一页出发都到扫码页
    /// </summary>
    internal static readonly (int X, int Y) QqPasswordEntry = (150, 630);

    /// <summary>QQ 二维码失效后中间的刷新图标（二维码的正中）</summary>
    internal static readonly (int X, int Y) QqQrRefresh = (150, 379);

    /// <summary>微信二维码失效后中间的刷新图标（二维码的正中, 实机量到）</summary>
    internal static readonly (int X, int Y) WeChatQrRefresh = (150, 381);

    /// <summary>二维码失效后最多自动刷新几次（等登录总共 10 分钟, 一张约 2 分钟）</summary>
    public const int MAX_QR_REFRESHES = 10;

    /// <summary>微信页停在快捷登录时的「使用其他头像、昵称或账号」</summary>
    internal static readonly (int X, int Y) WeChatOtherAccount = (150, 483);

    private readonly object gate = new();

    private string? qrId;
    private string? qrLink;
    private int     qrSeq;
    private int     qrMisses;

    private string?             smsId;
    private CatWeGameSmsPrompt? smsPrompt;
    private int                 smsSeq;
    private int                 smsMisses;
    private int                 smsConfirmTicks;

    private SwitchState switchState = scan == null ? SwitchState.Off : SwitchState.Pending;
    private int         switchRounds;
    private long        switchDoneAt;
    private long        switchStartedAt;
    private CatWeGameLoginWindow? pendingWindow;
    private int         qrRefreshes;
    private int         resettles;
    private int         windowSeen;

    private enum SwitchState
    {
        /// <summary>launch 没带 weGameScan, 不点</summary>
        Off,

        /// <summary>还没切到扫码页</summary>
        Pending,

        /// <summary>已经看到二维码</summary>
        Done,

        /// <summary>试够了轮数, 不再点</summary>
        GaveUp
    }

    /// <summary>多久看一次</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>每点一下之后等多久再核对</summary>
    public TimeSpan ClickSettle { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>点了设备验证的「确定」之后, 窗口过多久还在就算没通过</summary>
    public TimeSpan SmsConfirmWait { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>还没切到扫码页时每隔多久看一轮（比平时勤, 好让窗口一出现就开始切）</summary>
    public TimeSpan SwitchInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>切好扫码页后多久之内二维码没了算"窗口自己换了页", 要再切一次</summary>
    public TimeSpan ResettleWindow { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>
    ///     页签出来后连着几轮画面一样才开始点。启动时上次那一页会闪过约半秒, 按每轮半秒看, 要三轮才不会把它当成稳了
    /// </summary>
    public int SettleTicks { get; init; } = 3;

    /// <summary>登录窗口点了没反应时, 从第一轮算起最多这样重来多久; 过后照常计轮数</summary>
    public TimeSpan SwitchPatience { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>点击后每隔多久看一次有没有二维码</summary>
    public TimeSpan ClickPoll { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>报给工作台的二维码有效期（WeGame 没有给出确切时间; 二维码刷新后会作为新的验证再报）</summary>
    public int QrExpiresInSeconds { get; init; } = 120;

    /// <summary>
    ///     一直看到被取消为止; 结束时把还没清掉的验证报成已清除
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                try
                {
                    await TickAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // 看窗口出错不影响上号, 员工照旧可以自己在 WeGame 窗口里处理
                    Log.Warning("[CatHost] 看 WeGame 窗口时出错: {Type}: {Message}", ex.GetType().Name, redactor.Redact(ex.Message));
                }

                // 还在等登录窗口出现、还没切到扫码页时看得勤一些, 切好之后按正常间隔看
                await Task.Delay(switchState == SwitchState.Pending ? SwitchInterval : Interval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常结束
        }
        finally
        {
            ClearAll();
        }
    }

    /// <summary>
    ///     看一轮: 设备验证窗口、登录窗口上的二维码; 该切扫码页时切一轮
    /// </summary>
    internal async Task TickAsync(CancellationToken cancellationToken)
    {
        ObserveSms();

        // 员工把挡事的 WeGame 窗口最小化了: 垫到别的窗口后面去, 这样还能接着看二维码、失效了接着刷新
        if (scan != null && screen.RestoreMinimizedLoginWindow())
            Log.Information("[CatHost] WeGame 登录窗口被最小化了, 已还原到其它窗口后面");

        var window = CaptureWanted();

        if (switchState != SwitchState.Pending)
        {
            // 二维码约 2 分钟失效, 失效后要点中间的刷新图标才出新的: 要了自动切换的就替员工点, 新码作为新的一条报给客户
            if (window is { QrExpired: true } && scan != null && qrRefreshes < MAX_QR_REFRESHES)
            {
                qrRefreshes++;
                Log.Information("[CatHost] WeGame 登录窗口上的二维码失效了, 点刷新（第 {Count} 次）", qrRefreshes);
                window = await ClickAndCaptureAsync(window, scan == CatWeGameScan.WeChat ? WeChatQrRefresh : QqQrRefresh, false, cancellationToken).ConfigureAwait(false);
            }

            ObserveQr(window);

            // WeGame 刚启动时登录窗口会自己再跳一次页（比如跳到本机微信的快捷登录）, 把刚切好的扫码页换掉:
            // 切好后不久二维码就没了而登录窗口还在, 再切一次。隔得久的不管, 那多半是员工自己换了登录方式
            if (switchState == SwitchState.Done && window != null && qrId == null && resettles < MAX_RESETTLES
                && Environment.TickCount64 - switchDoneAt <= (long)ResettleWindow.TotalMilliseconds)
            {
                resettles++;
                switchState  = SwitchState.Pending;
                switchRounds = 0;
                windowSeen   = 0;
                Log.Information("[CatHost] WeGame 登录窗口切好后又自己换了页, 再切一次");
            }

            return;
        }

        // 还没切到要的扫码页: 这时窗口上的二维码可能是另一种扫码方式的, 先不报
        if (window == null)
        {
            windowSeen = 0;
            return;
        }

        // WeGame 刚启动的头几秒: 窗口在但里面是空的, 接着页签出来、内容还要再跳一两次（会先闪一下上次那一页）。
        // 这时点了不起作用。等页签画出来、且连着两轮画面一样了才开始点
        var settled = window.TabsVisible && CatWeGameLoginWindow.SamePanel(pendingWindow, window) != false;
        pendingWindow = window;

        if (!window.TabsVisible)
        {
            windowSeen = 0;
            return;
        }

        if (!settled)
        {
            windowSeen = 1;
            return;
        }

        if (++windowSeen < SettleTicks)
        {
            if (windowSeen == 1)
                Log.Information("[CatHost] WeGame 登录窗口的页签出来了, 等画面稳下来");

            return;
        }

        Log.Information("[CatHost] 开始切{Scan}扫码页（第 {Round} 轮）", scan == CatWeGameScan.WeChat ? "微信" : " QQ ", switchRounds + 1);

        if (switchStartedAt == 0)
            switchStartedAt = Environment.TickCount64;

        await SwitchRoundAsync(window, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     客户说短信已经发了: 点设备验证窗口的「确定」。立即返回是否点了; 通过与否之后看窗口还在不在
    ///     （不在了报 weGame.challengeCleared, 还在报 weGame.smsResult passed=false）
    /// </summary>
    public CatAcceptResult ConfirmSms(string? challengeId)
    {
        lock (gate)
        {
            if (smsId == null || !string.Equals(smsId, challengeId, StringComparison.Ordinal))
                return CatAcceptResult.Rejected(CatCodes.NOT_RUNNING, "这条短信验证已经不在了");

            // 上一次点完还在等结果: 不重复点
            if (smsConfirmTicks > 0)
                return CatAcceptResult.Ok();

            bool clicked;

            try
            {
                clicked = screen.ConfirmSmsPrompt();
            }
            catch (Exception ex)
            {
                Log.Warning("[CatHost] 点设备验证窗口的确定时出错: {Type}: {Message}", ex.GetType().Name, redactor.Redact(ex.Message));
                clicked = false;
            }

            if (!clicked)
                return CatAcceptResult.Rejected(CatCodes.NOT_RUNNING, "设备验证窗口已经不在了");

            smsConfirmTicks = Math.Max(1, (int)Math.Ceiling(SmsConfirmWait.TotalMilliseconds / Math.Max(1, Interval.TotalMilliseconds)));
            Log.Information("[CatHost] 已点设备验证窗口的确定 ({ChallengeId})", smsId);
            return CatAcceptResult.Ok();
        }
    }

    /// <summary>
    ///     把按 1210×680 量的位置换算到窗口的实际大小
    /// </summary>
    internal static (int X, int Y) Scale((int X, int Y) point, int width, int height) =>
        (
            (int)Math.Round(point.X * (double)width  / DESIGN_WIDTH,  MidpointRounding.AwayFromZero),
            (int)Math.Round(point.Y * (double)height / DESIGN_HEIGHT, MidpointRounding.AwayFromZero)
        );

    /// <summary>
    ///     切一轮: 先点页签; 没有二维码再点进扫码的那个入口。有二维码就算切好了, 不再点。
    /// </summary>
    private async Task SwitchRoundAsync(CatWeGameLoginWindow window, CancellationToken cancellationToken)
    {
        var isWeChat = scan == CatWeGameScan.WeChat;
        CatWeGameLoginWindow? after;

        if (isWeChat)
        {
            // 点完页签稍等一下: 已经是扫码页且二维码出来了就停; 否则点「使用其他头像、昵称或账号」。
            // 这个位置在扫码页上是二维码说明和「快捷登录」之间的空白, 点了无效果, 所以不用先等够时间确认没有二维码
            after = await ClickAndCaptureAsync(window, WeChatTab, true, cancellationToken).ConfigureAwait(false);

            if (after != null && !HasQr(after))
                after = await ClickAndCaptureAsync(after, WeChatOtherAccount, false, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // 点完页签稍等一下: 已经是扫码页且二维码出来了就停; 否则先到账号密码页, 再点扫码入口
            after = await ClickAndCaptureAsync(window, QqTab, true, cancellationToken).ConfigureAwait(false);

            if (after != null && !HasQr(after))
                after = await ClickAndCaptureAsync(after, QqPasswordEntry, true, cancellationToken).ConfigureAwait(false);

            if (after != null && !HasQr(after))
                after = await ClickAndCaptureAsync(after, QqScanEntry, false, cancellationToken).ConfigureAwait(false);
        }

        // 点的时候登录窗口没了（员工自己登录了、窗口被关了）: 这一轮不算
        if (after == null)
        {
            windowSeen = 0;
            return;
        }

        if (HasQr(after))
        {
            switchState  = SwitchState.Done;
            switchDoneAt = Environment.TickCount64;
            Log.Information("[CatHost] WeGame 登录窗口已切到{Scan}扫码页（第 {Round} 轮）", isWeChat ? "微信" : " QQ ", switchRounds + 1);
            ObserveQr(after);
            return;
        }

        // WeGame 刚启动时窗口已经在了却还不接收点击: 这一轮点完画面一点没变就不算数, 马上再来;
        // 但总共只这样等 SwitchPatience 这么久, 之后照常计轮数
        if (CatWeGameLoginWindow.SamePanel(window, after) == true
            && Environment.TickCount64 - switchStartedAt <= (long)SwitchPatience.TotalMilliseconds)
        {
            Log.Information("[CatHost] 点了之后登录窗口没有变化, 多半还没启动完, 再来一轮");
            return;
        }

        if (++switchRounds < MAX_SWITCH_ROUNDS)
            return;

        switchState = SwitchState.GaveUp;
        Log.Warning("[CatHost] 试了 {Rounds} 轮, WeGame 登录窗口没有切到{Scan}扫码页, 不再自动点", switchRounds, isWeChat ? "微信" : " QQ ");
        reporter.WeGameScanSwitchFailed(CatWeGameScans.Name(scan!.Value));
    }

    private async Task<CatWeGameLoginWindow?> ClickAndCaptureAsync(CatWeGameLoginWindow window, (int X, int Y) point, bool brief, CancellationToken cancellationToken)
    {
        var (x, y) = Scale(point, window.Width, window.Height);

        if (!screen.ClickLoginWindow(x, y))
            return null;

        Log.Information("[CatHost] 点了登录窗口 ({X}, {Y})", x, y);

        // 二维码要联网取, 可能过一会儿才画出来: 最多等两个 ClickSettle, 期间一看到二维码就返回;
        // 等够了仍没有才算这一页没有二维码, 免得把刚切好的扫码页又点走
        // brief: 只是换页的一步, 不指望这一下就出二维码, 看两次就继续
        var polls = brief ? 2 : Math.Max(1, (int)Math.Ceiling(ClickSettle.TotalMilliseconds * 2 / Math.Max(1, ClickPoll.TotalMilliseconds)));
        CatWeGameLoginWindow? after = null;

        for (var i = 0; i < polls; i++)
        {
            await Task.Delay(ClickPoll, cancellationToken).ConfigureAwait(false);
            after = CaptureWanted();

            if (after == null || HasQr(after))
                return after;

            // 看了三次画面还和点之前一模一样: 这一下没点动, 不用等二维码了
            if (!brief && i >= 2 && CatWeGameLoginWindow.SamePanel(window, after) == true)
                return after;
        }

        return after;
    }

    /// <summary>
    ///     截一次登录窗口。要切到某种扫码页时, 窗口上若是另一种的二维码（WeGame 刚启动会先闪一下上次用的那一页）,
    ///     当作没有二维码: 不拿它当"切好了", 也不报给客户。放弃自动切换后不再区分。
    /// </summary>
    private CatWeGameLoginWindow? CaptureWanted()
    {
        var window = screen.CaptureLoginWindow();

        if (window == null || scan == null || switchState == SwitchState.GaveUp || !HasQr(window))
            return window;

        return QrAppOf(window.QrLink!) is { } app && app != scan ? window with { QrLink = null, QrPng = null } : window;
    }

    /// <summary>
    ///     二维码是给哪个 App 扫的: 按链接的主机名认, 认不出返回 null
    /// </summary>
    internal static CatWeGameScan? QrAppOf(string link)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri))
            return null;

        var host = uri.Host;

        if (host.Equals("weixin.qq.com", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".weixin.qq.com", StringComparison.OrdinalIgnoreCase))
            return CatWeGameScan.WeChat;

        return host.Equals("qq.com", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".qq.com", StringComparison.OrdinalIgnoreCase) ? CatWeGameScan.Qq : null;
    }

    private static bool HasQr(CatWeGameLoginWindow? window) =>
        window is { QrLink.Length: > 0, QrPng.Length: > 0 };

    /// <summary>
    ///     二维码内容与上次不同才报（算新的验证, 不单独清旧的）; 连续几轮看不到才报已清除
    /// </summary>
    private void ObserveQr(CatWeGameLoginWindow? window)
    {
        if (HasQr(window))
        {
            qrMisses = 0;

            if (string.Equals(window!.QrLink, qrLink, StringComparison.Ordinal))
                return;

            qrLink = window.QrLink;
            qrId   = $"q-{++qrSeq}";

            // 二维码内容就是一次性的登录凭据, 不能进日志
            redactor.RegisterSecret(qrLink);
            Log.Information("[CatHost] WeGame 登录窗口上有新的二维码 ({ChallengeId})", qrId);
            reporter.WeGameChallenge(new CatWeGameChallenge(CatWeGameChallengeKinds.QRCODE, qrId, Convert.ToBase64String(window.QrPng!), qrLink, QrExpiresInSeconds));
            return;
        }

        if (qrId == null || ++qrMisses < MISSES_TO_CLEAR)
            return;

        Log.Information("[CatHost] WeGame 登录窗口上的二维码不在了 ({ChallengeId})", qrId);
        reporter.WeGameChallengeCleared(qrId);
        qrId     = null;
        qrLink   = null;
        qrMisses = 0;
    }

    /// <summary>
    ///     设备验证窗口: 出现或换了内容就报; 点过「确定」后窗口不在了算通过, 等够时间还在或换了内容算没通过
    /// </summary>
    private void ObserveSms()
    {
        lock (gate)
        {
            var prompt = screen.FindSmsPrompt();

            if (prompt != null)
            {
                smsMisses = 0;

                if (smsId != null && IsSamePrompt(prompt, smsPrompt))
                {
                    if (smsConfirmTicks > 0 && --smsConfirmTicks == 0)
                    {
                        Log.Information("[CatHost] 点了确定后设备验证窗口还在, 没有通过 ({ChallengeId})", smsId);
                        reporter.WeGameSmsResult(smsId, false);
                    }

                    return;
                }

                // 点了确定后换了一串新内容: 上一条没通过
                if (smsId != null && smsConfirmTicks > 0)
                    reporter.WeGameSmsResult(smsId, false);

                smsConfirmTicks = 0;
                smsPrompt       = prompt;
                smsId           = $"s-{++smsSeq}";

                redactor.RegisterSecret(prompt.Code);
                Log.Information("[CatHost] 出现了设备验证窗口 ({ChallengeId})", smsId);
                reporter.WeGameChallenge(new CatWeGameChallenge(CatWeGameChallengeKinds.SMS, smsId, Code: prompt.Code, Phone: prompt.Phone, Text: prompt.Text));
                return;
            }

            if (smsId == null)
                return;

            // 点过确定后窗口不在了就是通过; 没点过的（员工自己处理的）多看一轮, 免得把重绘当成消失
            if (smsConfirmTicks == 0 && ++smsMisses < MISSES_TO_CLEAR)
                return;

            Log.Information("[CatHost] 设备验证窗口不在了 ({ChallengeId})", smsId);
            reporter.WeGameChallengeCleared(smsId);
            ResetSms();
        }
    }

    private static bool IsSamePrompt(CatWeGameSmsPrompt current, CatWeGameSmsPrompt? previous) =>
        previous != null                                                 &&
        string.Equals(current.Code, previous.Code, StringComparison.Ordinal) &&
        string.Equals(current.Phone, previous.Phone, StringComparison.Ordinal);

    private void ResetSms()
    {
        smsId           = null;
        smsPrompt       = null;
        smsMisses       = 0;
        smsConfirmTicks = 0;
    }

    private void ClearAll()
    {
        if (qrId != null)
        {
            reporter.WeGameChallengeCleared(qrId);
            qrId   = null;
            qrLink = null;
        }

        lock (gate)
        {
            if (smsId == null)
                return;

            reporter.WeGameChallengeCleared(smsId);
            ResetSms();
        }
    }
}
