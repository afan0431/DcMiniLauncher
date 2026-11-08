namespace XIVLauncher.CatHost;

/// <summary>
///     登录后 WeGame 主界面（最终幻想14 游戏页）上跟「启动」有关的状态:
///     StartReady = 右下角是能点的橙色「启动」（点了以后变成暗色的「正在启动」）;
///     AgreementOpen = 首次点「启动」弹出的协议勾选框开着; AgreeAllChecked = 其中「我已阅读同意以上所有内容」已勾上
/// </summary>
public sealed record CatWeGameMainWindow(int Width, int Height, bool StartReady, bool AgreementOpen, bool AgreeAllChecked);

/// <summary>
///     按截图像素认主界面。位置按 1600×900 的主界面量（11-08 开发机实测）, 按实际客户区尺寸换算:
///     <list type="bullet">
///         <item>「启动」按钮中心 (1382,848); 按钮左半边 (1290–1310, 840–856) 能点时是亮橙色（约 255,169,60）, 「正在启动」时是暗棕色（约 82,58,30）</item>
///         <item>协议勾选框在「启动」正上方, 底色是纯色（亮度 20, 一点起伏都没有）; 量三条横带 (1300–1500) × (642–646 / 758–764 / 790–798), 背景画面上这几条都有明显起伏</item>
///         <item>「以上所有内容」的勾选框中心 (1277,777): 框里 (1272–1282, 772–782) 没勾时亮度约 8, 勾上约 58</item>
///     </list>
/// </summary>
public static class CatWeGameMainReader
{
    /// <summary>量坐标用的主界面宽度</summary>
    public const int DESIGN_WIDTH = 1600;

    /// <summary>量坐标用的主界面高度</summary>
    public const int DESIGN_HEIGHT = 900;

    /// <summary>「启动」按钮</summary>
    public static readonly (int X, int Y) StartButton = (1382, 848);

    /// <summary>协议勾选框里的「我已阅读同意以上所有内容」</summary>
    public static readonly (int X, int Y) AgreeAll = (1277, 777);

    /// <summary>
    ///     认一张主界面截图（客户区, 每像素 4 字节 BGRA, 自上而下）
    /// </summary>
    public static CatWeGameMainWindow Read(byte[] bgra, int width, int height)
    {
        if (width <= 0 || height <= 0 || bgra.Length < width * height * 4)
            return new CatWeGameMainWindow(width, height, false, false, false);

        var view = new View(bgra, width, height);

        var agreement = view.Flat(1300, 642, 1500, 646) && view.Flat(1300, 758, 1500, 764) && view.Flat(1300, 790, 1500, 798);
        var checkedAll = agreement && view.AverageLuma(1272, 772, 1282, 782) > 30;
        var (r, g, b) = view.AverageColor(1290, 840, 1310, 856);
        var startReady = r > 200 && g > 110 && b < 150;

        return new CatWeGameMainWindow(width, height, startReady, agreement, checkedAll);
    }

    /// <summary>把 1600×900 上量的点换算到实际客户区尺寸</summary>
    public static (int X, int Y) Scale((int X, int Y) point, int width, int height) =>
        (
            (int)Math.Round(point.X * (double)width  / DESIGN_WIDTH, MidpointRounding.AwayFromZero),
            (int)Math.Round(point.Y * (double)height / DESIGN_HEIGHT, MidpointRounding.AwayFromZero)
        );

    /// <summary>按 1600×900 量的区域在实际截图上取样</summary>
    private sealed class View(byte[] bgra, int width, int height)
    {
        /// <summary>一条区域是不是一片纯色的暗底（协议框底色亮度约 20）</summary>
        public bool Flat(int x0, int y0, int x1, int y1)
        {
            var samples = Samples(x0, y0, x1, y1).Select(p => (double)Luma(p)).ToArray();

            if (samples.Length == 0)
                return false;

            var mean     = samples.Average();
            var variance = samples.Select(v => (v - mean) * (v - mean)).Average();
            return mean is >= 8 and <= 40 && variance <= 9;
        }

        public double AverageLuma(int x0, int y0, int x1, int y1)
        {
            var samples = Samples(x0, y0, x1, y1).Select(Luma).ToArray();
            return samples.Length == 0 ? 0 : samples.Average();
        }

        public (double R, double G, double B) AverageColor(int x0, int y0, int x1, int y1)
        {
            var samples = Samples(x0, y0, x1, y1).ToArray();

            if (samples.Length == 0)
                return (0, 0, 0);

            return (samples.Average(p => bgra[p + 2]), samples.Average(p => bgra[p + 1]), samples.Average(p => bgra[p]));
        }

        private int Luma(int offset) =>
            (bgra[offset] + bgra[offset + 1] * 2 + bgra[offset + 2]) / 4;

        /// <summary>区域里每个像素在数组里的起始下标（坐标按 1600×900 量, 换算后裁到图内）</summary>
        private IEnumerable<int> Samples(int x0, int y0, int x1, int y1)
        {
            var left   = Math.Clamp(x0 * width  / DESIGN_WIDTH, 0, width - 1);
            var right  = Math.Clamp(x1 * width  / DESIGN_WIDTH, 0, width - 1);
            var top    = Math.Clamp(y0 * height / DESIGN_HEIGHT, 0, height - 1);
            var bottom = Math.Clamp(y1 * height / DESIGN_HEIGHT, 0, height - 1);

            for (var y = top; y <= bottom; y++)
            {
                for (var x = left; x <= right; x++)
                    yield return (y * width + x) * 4;
            }
        }
    }
}
