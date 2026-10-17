using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace XIVLauncher.CatHost;

/// <summary>WeGame 登录窗正中弹出的网页弹窗是哪一种</summary>
public enum CatWeGamePopupKind
{
    /// <summary>图片验证码的卡片出来了, 题目还没加载出来（卡片里一片白）</summary>
    CaptchaLoading,

    /// <summary>图片验证码在过渡: 提交中（格子变淡）、答错提示（题目条红字）、验证成功（绿勾）、换题时格子空白</summary>
    CaptchaBusy,

    /// <summary>图片验证码有格子打了勾、还没提交</summary>
    CaptchaSelected,

    /// <summary>图片验证码题目与图片都已就绪, 可以作答</summary>
    CaptchaReady,

    /// <summary>手机 QQ 一键认证, 等客户在手机 QQ 上确认</summary>
    QqConfirm,

    /// <summary>手机 QQ 一键认证已过期, 要点「重新验证」</summary>
    QqConfirmExpired
}

/// <summary>
///     WeGame 登录窗里的一个弹窗（图片验证码或手机 QQ 一键认证）。坐标都是登录窗口客户区坐标。
///     <para>
///         CaptchaJpeg / Tiles / GridThumb 只在 <see cref="CatWeGamePopupKind.CaptchaReady" /> 时有: 报给客户的图（题目条 + 图片格）、
///         各格在这张图里的位置（千分比 [x, y, w, h]）、网格缩成的小图（每格一个亮度, 用来判断是不是同一题）。
///         TilePoints / ConfirmPoint / RefreshPoint / SelectedTiles 在题目条已出来的验证码上都有; ReverifyPoint 只在已过期时有
///     </para>
/// </summary>
public sealed record CatWeGamePopup
(
    CatWeGamePopupKind             Kind,
    byte[]?                        CaptchaJpeg   = null,
    IReadOnlyList<int[]>?          Tiles         = null,
    IReadOnlyList<(int X, int Y)>? TilePoints    = null,
    (int X, int Y)?                ConfirmPoint  = null,
    (int X, int Y)?                RefreshPoint  = null,
    (int X, int Y)?                ReverifyPoint = null,
    byte[]?                        GridThumb     = null,
    IReadOnlyList<int>?            SelectedTiles = null
)
{
    /// <summary>两张网格小图每格亮度差在这以内算一样</summary>
    public const int GRID_TOLERANCE = 12;

    /// <summary>是不是图片验证码（含加载中与过渡态）</summary>
    public bool IsCaptcha =>
        Kind is CatWeGamePopupKind.CaptchaLoading or CatWeGamePopupKind.CaptchaBusy or CatWeGamePopupKind.CaptchaSelected or CatWeGamePopupKind.CaptchaReady;

    /// <summary>是不是手机 QQ 一键认证（含已过期）</summary>
    public bool IsQqConfirm =>
        Kind is CatWeGamePopupKind.QqConfirm or CatWeGamePopupKind.QqConfirmExpired;

    /// <summary>
    ///     两张网格小图是不是同一题; 有一边没有时返回 false
    /// </summary>
    public static bool SameGrid(byte[]? left, byte[]? right)
    {
        if (left == null || right == null || left.Length != right.Length)
            return false;

        for (var i = 0; i < left.Length; i++)
        {
            if (Math.Abs(left[i] - right[i]) > GRID_TOLERANCE)
                return false;
        }

        return true;
    }

    /// <summary>
    ///     record 自动生成的 ToString 会打印所有成员（含整张图）, 这里只留种类
    /// </summary>
    public override string ToString() =>
        $"CatWeGamePopup {{ Kind = {Kind} }}";
}

/// <summary>
///     从 WeGame 登录窗的整窗截图（PrintWindow 结果, BGRA）里认出正中的弹窗。全是纯函数, 单测直接用实机截图覆盖。
///     <para>
///         弹窗不随登录窗缩放（96 DPI 下验证码卡片 392×436、手机确认 360×481）, 总在登录窗正中: 沿中线找白色卡片的左右边、
///         再沿卡片左边内侧找上下边, 按宽高比认是哪一种, 卡片内的位置都按基准尺寸量、乘以缩放比例
///     </para>
/// </summary>
internal static class CatWeGamePopupReader
{
    /// <summary>验证码卡片的基准宽</summary>
    internal const int CAPTCHA_WIDTH = 392;

    /// <summary>验证码卡片的基准高</summary>
    internal const int CAPTCHA_HEIGHT = 436;

    /// <summary>手机确认卡片的基准宽</summary>
    internal const int CONFIRM_WIDTH = 360;

    /// <summary>手机确认卡片的基准高</summary>
    internal const int CONFIRM_HEIGHT = 481;

    /// <summary>报给客户的 JPEG 质量</summary>
    internal const int JPEG_QUALITY = 85;

    /// <summary>算近白的亮度下限</summary>
    private const int WHITE = 248;

    /// <summary>卡片边上的白段至少多长</summary>
    private const int MIN_RUN = 8;

    /// <summary>卡片左右边到窗口中线的距离最多差多少</summary>
    private const int CENTER_SLACK = 4;

    /// <summary>卡片上下边到窗口中线的距离最多差多少</summary>
    private const int MIDDLE_SLACK = 6;

    /// <summary>沿卡片左边内侧往上下找时, 中间最多跳过几个不白的像素（标题下的分隔线）</summary>
    private const int MAX_GAP = 3;

    /// <summary>宽高比的容差</summary>
    private const double RATIO_SLACK = 0.03;

    /// <summary>缩放比例的范围（96 DPI 为 1）</summary>
    private const double MIN_SCALE = 0.75, MAX_SCALE = 3.0;

    /// <summary>验证码题目条（卡片内基准坐标, 左上含、右下不含）</summary>
    private static readonly (int X0, int Y0, int X1, int Y1) TitleBar = (24, 68, 366, 147);

    /// <summary>验证码图片网格</summary>
    private static readonly (int X0, int Y0, int X1, int Y1) Grid = (24, 151, 366, 378);

    /// <summary>报给客户的那一块: 题目条 + 图片格</summary>
    private static readonly (int X0, int Y0, int X1, int Y1) ReportArea = (24, 68, 366, 378);

    /// <summary>格子的列</summary>
    private static readonly (int X0, int X1)[] TileColumns = [(24, 136), (139, 251), (254, 366)];

    /// <summary>格子的行</summary>
    private static readonly (int Y0, int Y1)[] TileRows = [(151, 263), (266, 378)];

    /// <summary>「确定」</summary>
    private static readonly (int X, int Y) ConfirmButton = (336, 397);

    /// <summary>「换一组」</summary>
    private static readonly (int X, int Y) RefreshButton = (98, 398);

    /// <summary>至少几格同时偏平或变淡才算过渡</summary>
    private const int MIN_FADED_TILES = 3;

    /// <summary>选中后格子右上角的蓝色对勾圆（边长, 贴着格子的右上角）</summary>
    private const int CHECK_BOX = 24;

    /// <summary>手机确认的蓝色圆心</summary>
    private static readonly (int X, int Y) ConfirmCircle = (180, 237);

    /// <summary>看蓝色圆的环（半径范围）</summary>
    private const int RING_INNER = 35, RING_OUTER = 50;

    /// <summary>「请求已过期」红字所在的带</summary>
    private static readonly (int X0, int Y0, int X1, int Y1) ExpiredBand = (40, 315, 250, 345);

    /// <summary>「重新验证」蓝字所在的带</summary>
    private static readonly (int X0, int Y0, int X1, int Y1) ReverifyBand = (240, 315, 330, 345);

    /// <summary>认不出「重新验证」蓝字时点的位置</summary>
    private static readonly (int X, int Y) ReverifyFallback = (277, 329);

    /// <summary>网格小图每格的基准边长</summary>
    private const int GRID_CELL = 8;

    /// <summary>
    ///     各格在报给客户的图里的位置: 千分比 [x, y, w, h], 左上→右下逐行
    /// </summary>
    internal static readonly IReadOnlyList<int[]> TileRatios = BuildTileRatios();

    /// <summary>
    ///     认出正中的弹窗; 没有弹窗或认不出返回 null
    /// </summary>
    internal static CatWeGamePopup? Read(byte[] bgra, int width, int height)
    {
        if (FindCard(bgra, width, height) is not { } card)
            return null;

        return card.Captcha ? ReadCaptcha(bgra, width, card) : ReadConfirm(bgra, width, card);
    }

    /// <summary>
    ///     找正中的白色卡片: 返回左上角、宽高、是不是验证码卡片（否则是手机确认）与缩放比例; 找不到或比例不符返回 null
    /// </summary>
    internal static CatWeGameCard? FindCard(byte[] bgra, int width, int height)
    {
        if (width < MIN_RUN * 2 || height < MIN_RUN * 2 || bgra.Length < width * height * 4)
            return null;

        var cx   = width / 2;
        var cy   = height / 2;
        var runs = WhiteRuns(bgra, width, cy);

        foreach (var (left, _) in runs)
        {
            if (left >= cx)
                break;

            for (var i = runs.Count - 1; i >= 0; i--)
            {
                var right = runs[i].End;

                if (right <= cx || right < left || Math.Abs((cx - left) - (right - cx)) > CENTER_SLACK)
                    continue;

                if (Measure(bgra, width, height, left, right, cy) is { } card)
                    return card;
            }
        }

        return null;
    }

    /// <summary>
    ///     已知卡片左右边, 沿左边内侧往上下找上下边, 按宽高比认种类
    /// </summary>
    private static CatWeGameCard? Measure(byte[] bgra, int width, int height, int left, int right, int cy)
    {
        var x      = Math.Min(left + 4, right);
        var top    = Walk(bgra, width, height, x, cy, -1);
        var bottom = Walk(bgra, width, height, x, cy, 1);

        if (Math.Abs((cy - top) - (bottom - cy)) > MIDDLE_SLACK)
            return null;

        var cardWidth  = right - left + 1;
        var cardHeight = bottom - top + 1;
        var ratio      = (double)cardHeight / cardWidth;

        bool captcha;
        double scale;

        if (Math.Abs(ratio - (double)CAPTCHA_HEIGHT / CAPTCHA_WIDTH) <= RATIO_SLACK)
        {
            captcha = true;
            scale   = (double)cardWidth / CAPTCHA_WIDTH;
        }
        else if (Math.Abs(ratio - (double)CONFIRM_HEIGHT / CONFIRM_WIDTH) <= RATIO_SLACK)
        {
            captcha = false;
            scale   = (double)cardWidth / CONFIRM_WIDTH;
        }
        else
            return null;

        return scale is < MIN_SCALE or > MAX_SCALE ? null : new CatWeGameCard(left, top, cardWidth, cardHeight, captcha, scale);
    }

    /// <summary>
    ///     从 (x, y) 沿竖直方向走, 返回最后一个近白像素的纵坐标; 中间不超过 <see cref="MAX_GAP" /> 个不白的像素可以跳过
    /// </summary>
    private static int Walk(byte[] bgra, int width, int height, int x, int y, int step)
    {
        var last = y;
        var gap  = 0;

        for (var current = y; current >= 0 && current < height; current += step)
        {
            if (Luma(bgra, width, x, current) >= WHITE)
            {
                last = current;
                gap  = 0;
            }
            else if (++gap > MAX_GAP)
                break;
        }

        return last;
    }

    /// <summary>
    ///     一行里所有至少 <see cref="MIN_RUN" /> 像素长的近白段（含首尾）
    /// </summary>
    private static List<(int Start, int End)> WhiteRuns(byte[] bgra, int width, int y)
    {
        var runs  = new List<(int Start, int End)>();
        var start = -1;

        for (var x = 0; x <= width; x++)
        {
            var white = x < width && Luma(bgra, width, x, y) >= WHITE;

            if (white && start < 0)
                start = x;
            else if (!white && start >= 0)
            {
                if (x - start >= MIN_RUN)
                    runs.Add((start, x - 1));

                start = -1;
            }
        }

        return runs;
    }

    private static CatWeGamePopup ReadCaptcha(byte[] bgra, int width, CatWeGameCard card)
    {
        var s  = card.Scale;
        var s2 = s * s;

        // 题目条还不是蓝的: 卡片刚出来、里面还是一片白
        if (Fraction(bgra, width, card, TitleBar, IsTitleBlue) < 0.5)
            return new CatWeGamePopup(CatWeGamePopupKind.CaptchaLoading);

        var points   = new List<(int X, int Y)>(6);
        var selected = new List<int>();
        var faded    = 0;
        var busy     = Count(bgra, width, card, TitleBar, IsAlertRed) >= 20 * s2  // 答错提示
                       || Count(bgra, width, card, Grid, IsSuccessGreen) >= 20 * s2; // 验证成功

        foreach (var (y0, y1) in TileRows)
        {
            foreach (var (x0, x1) in TileColumns)
            {
                var index = points.Count;
                points.Add(card.Point((x0 + x1) / 2, (y0 + y1) / 2));

                if (Count(bgra, width, card, (x1 - CHECK_BOX, y0, x1, y0 + CHECK_BOX), IsCheckBlue) >= 100 * s2)
                {
                    selected.Add(index);
                    continue;
                }

                // 提交中整格变淡（白蒙层）; 换题时格子空白或只剩底色
                var (mean, deviation) = LumaStats(bgra, width, card, (x0, y0, x1, y1));

                if (mean > 210 && deviation < 30 || deviation <= 20)
                    faded++;
            }
        }

        // 过渡时是整片格子一起变淡（提交中 6 格、验证成功 5 格）; 单独一两格偏平多半是图本身（纯色、天空、白墙）, 照常算就绪
        if (faded >= MIN_FADED_TILES)
            busy = true;

        var confirm = card.Point(ConfirmButton.X, ConfirmButton.Y);
        var refresh = card.Point(RefreshButton.X, RefreshButton.Y);

        if (busy)
            return new CatWeGamePopup(CatWeGamePopupKind.CaptchaBusy, TilePoints: points, ConfirmPoint: confirm, RefreshPoint: refresh, SelectedTiles: selected);

        if (selected.Count > 0)
            return new CatWeGamePopup(CatWeGamePopupKind.CaptchaSelected, TilePoints: points, ConfirmPoint: confirm, RefreshPoint: refresh, SelectedTiles: selected);

        return new CatWeGamePopup
        (
            CatWeGamePopupKind.CaptchaReady,
            EncodeJpeg(bgra, width, card, ReportArea),
            TileRatios,
            points,
            confirm,
            refresh,
            GridThumb: GridThumbOf(bgra, width, card),
            SelectedTiles: selected
        );
    }

    private static CatWeGamePopup? ReadConfirm(byte[] bgra, int width, CatWeGameCard card)
    {
        var total = 0;
        var blue  = 0;

        // 蓝色圆的一圈: 半径 35–50 之间的像素差不多都是蓝的
        for (var dy = -RING_OUTER; dy <= RING_OUTER; dy++)
        {
            for (var dx = -RING_OUTER; dx <= RING_OUTER; dx++)
            {
                var distance = Math.Sqrt(dx * dx + dy * dy);

                if (distance < RING_INNER || distance > RING_OUTER)
                    continue;

                var (x, y) = card.Point(ConfirmCircle.X + dx, ConfirmCircle.Y + dy);
                total++;

                if (IsCircleBlue(bgra, (y * width + x) * 4))
                    blue++;
            }
        }

        if (total == 0 || blue < total * 0.6)
            return null;

        if (Count(bgra, width, card, ExpiredBand, IsExpiredRed) < 15 * card.Scale * card.Scale)
            return new CatWeGamePopup(CatWeGamePopupKind.QqConfirm);

        return new CatWeGamePopup(CatWeGamePopupKind.QqConfirmExpired, ReverifyPoint: ReverifyPointOf(bgra, width, card));
    }

    /// <summary>
    ///     「重新验证」蓝字的外框中心; 没找到蓝字时用固定位置
    /// </summary>
    private static (int X, int Y) ReverifyPointOf(byte[] bgra, int width, CatWeGameCard card)
    {
        var (left, top, right, bottom) = card.Rect(ReverifyBand);
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;

        for (var y = top; y < bottom; y++)
        {
            for (var x = left; x < right; x++)
            {
                if (!IsLinkBlue(bgra, (y * width + x) * 4))
                    continue;

                minX = Math.Min(minX, x);
                maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y);
                maxY = Math.Max(maxY, y);
            }
        }

        return maxX < 0 ? card.Point(ReverifyFallback.X, ReverifyFallback.Y) : ((minX + maxX + 1) / 2, (minY + maxY + 1) / 2);
    }

    /// <summary>
    ///     网格那一块缩成小图: 每格存平均亮度
    /// </summary>
    private static byte[] GridThumbOf(byte[] bgra, int width, CatWeGameCard card)
    {
        var (left, top, right, bottom) = card.Rect(Grid);
        var cell    = Math.Max(4, (int)Math.Round(GRID_CELL * card.Scale));
        var columns = Math.Max(1, (right - left) / cell);
        var rows    = Math.Max(1, (bottom - top) / cell);
        var thumb   = new byte[columns * rows];

        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                var sum = 0;

                for (var y = top + row * cell; y < top + (row + 1) * cell; y++)
                {
                    for (var x = left + column * cell; x < left + (column + 1) * cell; x++)
                        sum += Luma(bgra, width, x, y);
                }

                thumb[row * columns + column] = (byte)(sum / (cell * cell));
            }
        }

        return thumb;
    }

    /// <summary>
    ///     把卡片里的一块编码成 JPEG
    /// </summary>
    private static byte[] EncodeJpeg(byte[] bgra, int width, CatWeGameCard card, (int X0, int Y0, int X1, int Y1) area)
    {
        var (left, top, right, bottom) = card.Rect(area);
        var cropWidth  = right - left;
        var cropHeight = bottom - top;
        var pixels     = new byte[cropWidth * cropHeight * 4];

        for (var y = 0; y < cropHeight; y++)
            Buffer.BlockCopy(bgra, ((top + y) * width + left) * 4, pixels, y * cropWidth * 4, cropWidth * 4);

        return EncodeJpeg(pixels, cropWidth, cropHeight);
    }

    /// <summary>
    ///     把 BGRA 像素编码成 JPEG（第 4 个字节不看）
    /// </summary>
    internal static byte[] EncodeJpeg(byte[] bgra, int width, int height)
    {
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, bgra, width * 4);
        bitmap.Freeze();

        var encoder = new JpegBitmapEncoder { QualityLevel = JPEG_QUALITY };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static IReadOnlyList<int[]> BuildTileRatios()
    {
        var width  = (double)(ReportArea.X1 - ReportArea.X0);
        var height = (double)(ReportArea.Y1 - ReportArea.Y0);
        var tiles  = new List<int[]>(6);

        foreach (var (y0, y1) in TileRows)
        {
            foreach (var (x0, x1) in TileColumns)
            {
                tiles.Add
                (
                    [
                        (int)Math.Round((x0 - ReportArea.X0) * 1000 / width, MidpointRounding.AwayFromZero),
                        (int)Math.Round((y0 - ReportArea.Y0) * 1000 / height, MidpointRounding.AwayFromZero),
                        (int)Math.Round((x1 - x0) * 1000 / width, MidpointRounding.AwayFromZero),
                        (int)Math.Round((y1 - y0) * 1000 / height, MidpointRounding.AwayFromZero)
                    ]
                );
            }
        }

        return tiles;
    }

    private static int Luma(byte[] bgra, int width, int x, int y)
    {
        var offset = (y * width + x) * 4;
        return (bgra[offset] + bgra[offset + 1] * 2 + bgra[offset + 2]) / 4;
    }

    private static (double Mean, double Deviation) LumaStats(byte[] bgra, int width, CatWeGameCard card, (int X0, int Y0, int X1, int Y1) area)
    {
        var (left, top, right, bottom) = card.Rect(area);
        long sum    = 0;
        long square = 0;
        var  count  = 0;

        for (var y = top; y < bottom; y++)
        {
            for (var x = left; x < right; x++)
            {
                var luma = Luma(bgra, width, x, y);
                sum    += luma;
                square += luma * luma;
                count++;
            }
        }

        if (count == 0)
            return (0, 0);

        var mean = (double)sum / count;
        return (mean, Math.Sqrt(Math.Max(0, (double)square / count - mean * mean)));
    }

    private static int Count(byte[] bgra, int width, CatWeGameCard card, (int X0, int Y0, int X1, int Y1) area, Func<byte[], int, bool> match)
    {
        var (left, top, right, bottom) = card.Rect(area);
        var count = 0;

        for (var y = top; y < bottom; y++)
        {
            for (var x = left; x < right; x++)
            {
                if (match(bgra, (y * width + x) * 4))
                    count++;
            }
        }

        return count;
    }

    private static double Fraction(byte[] bgra, int width, CatWeGameCard card, (int X0, int Y0, int X1, int Y1) area, Func<byte[], int, bool> match)
    {
        var (left, top, right, bottom) = card.Rect(area);
        var total = (right - left) * (bottom - top);
        return total <= 0 ? 0 : (double)Count(bgra, width, card, area, match) / total;
    }

    /// <summary>题目条的蓝 (0,122,255)</summary>
    private static bool IsTitleBlue(byte[] p, int o) =>
        p[o + 2] <= 30 && Math.Abs(p[o + 1] - 122) <= 30 && p[o] >= 225;

    /// <summary>答错提示的红字</summary>
    private static bool IsAlertRed(byte[] p, int o) =>
        p[o + 2] > 200 && p[o + 1] < 80 && p[o] < 80;

    /// <summary>验证成功的绿勾</summary>
    private static bool IsSuccessGreen(byte[] p, int o) =>
        p[o + 1] > 180 && p[o + 2] < 80 && p[o] < 80;

    /// <summary>选中格子右上角对勾圆的深蓝 (0,82,217)</summary>
    private static bool IsCheckBlue(byte[] p, int o) =>
        p[o + 2] <= 40 && Math.Abs(p[o + 1] - 82) <= 25 && Math.Abs(p[o] - 217) <= 25;

    /// <summary>手机确认圆的蓝 (0,153,255)</summary>
    private static bool IsCircleBlue(byte[] p, int o) =>
        p[o + 2] <= 30 && Math.Abs(p[o + 1] - 153) <= 30 && p[o] >= 225;

    /// <summary>「请求已过期」的红字</summary>
    private static bool IsExpiredRed(byte[] p, int o) =>
        p[o + 2] > 200 && p[o + 1] < 110 && p[o] < 110;

    /// <summary>「重新验证」的蓝字</summary>
    private static bool IsLinkBlue(byte[] p, int o) =>
        p[o] > 180 && p[o] - p[o + 2] > 60;
}

/// <summary>
///     认出的弹窗卡片: 左上角（窗口客户区坐标）、宽高、是不是验证码卡片（否则是手机确认）、相对基准尺寸的缩放比例
/// </summary>
internal readonly record struct CatWeGameCard(int Left, int Top, int Width, int Height, bool Captcha, double Scale)
{
    /// <summary>卡片内按基准尺寸量的点换成窗口坐标</summary>
    public (int X, int Y) Point(int x, int y) =>
        (Left + (int)Math.Round(x * Scale, MidpointRounding.AwayFromZero), Top + (int)Math.Round(y * Scale, MidpointRounding.AwayFromZero));

    /// <summary>卡片内按基准尺寸量的一块换成窗口坐标（左上含、右下不含）, 不超出卡片</summary>
    public (int Left, int Top, int Right, int Bottom) Rect((int X0, int Y0, int X1, int Y1) area)
    {
        var (left, top)     = Point(area.X0, area.Y0);
        var (right, bottom) = Point(area.X1, area.Y1);
        return (Math.Max(Left, left), Math.Max(Top, top), Math.Min(Left + Width, right), Math.Min(Top + Height, bottom));
    }
}
