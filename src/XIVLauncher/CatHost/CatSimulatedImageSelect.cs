using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace XIVLauncher.CatHost;

/// <summary>
///     模拟模式里的选图题: 程序画一张与真实报给客户的图同样大小（342×310）的 JPEG, 上方蓝色题目条写「选择所有符合描述的图片 “红色”」,
///     下面 3×2 色块, 序号 0 与 3 是红色, 其余颜色按题号轮换（看得出换了题）
/// </summary>
public static class CatSimulatedImageSelect
{
    /// <summary>图宽</summary>
    public const int WIDTH = 342;

    /// <summary>图高</summary>
    public const int HEIGHT = 310;

    /// <summary>正确答案: 红色的两格</summary>
    public static readonly IReadOnlyList<int> Answer = [0, 3];

    /// <summary>各格的位置（千分比 [x, y, w, h]）, 与真实识别的一致</summary>
    public static IReadOnlyList<int[]> Tiles => CatWeGamePopupReader.TileRatios;

    private static readonly Color[] Others =
    [
        Color.FromRgb(0x3A, 0x7B, 0xD5), Color.FromRgb(0x2E, 0x9E, 0x5B), Color.FromRgb(0xE8, 0xB9, 0x2F),
        Color.FromRgb(0x8E, 0x5B, 0xC8), Color.FromRgb(0x7A, 0x7A, 0x7A), Color.FromRgb(0x2B, 0xB3, 0xB8)
    ];

    /// <summary>
    ///     画第 <paramref name="seq" /> 题, 返回 JPEG。WPF 画图要在 STA 线程上, 这里单开一个
    /// </summary>
    public static byte[] DrawJpeg(int seq)
    {
        byte[]?    result = null;
        Exception? error  = null;

        var thread = new Thread
        (
            () =>
            {
                try
                {
                    result = Draw(seq);
                }
                catch (Exception ex)
                {
                    error = ex;
                }
            }
        )
        {
            IsBackground = true
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        return error != null ? throw new InvalidOperationException("画模拟的选图题失败", error) : result!;
    }

    private static byte[] Draw(int seq)
    {
        var visual = new DrawingVisual();

        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(Brushes.White, null, new Rect(0, 0, WIDTH, HEIGHT));
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(0, 122, 255)), null, new Rect(0, 0, WIDTH, 79));
            context.DrawText(Text("选择所有符合描述的图片", 14, FontWeights.Normal), new Point(10, 10));
            context.DrawText(Text($"第 {seq} 组（模拟）", 12, FontWeights.Normal), new Point(WIDTH - 100, 12));
            context.DrawText(Text("“红色”", 20, FontWeights.Bold), new Point(10, 40));

            var tiles = Tiles;

            for (var i = 0; i < tiles.Count; i++)
            {
                var color = Answer.Contains(i) ? Color.FromRgb(0xDC, 0x28, 0x28) : Others[(i + seq) % Others.Length];
                var tile  = tiles[i];
                var rect  = new Rect(tile[0] * WIDTH / 1000d, tile[1] * HEIGHT / 1000d, tile[2] * WIDTH / 1000d, tile[3] * HEIGHT / 1000d);
                context.DrawRectangle(new SolidColorBrush(color), null, rect);
            }
        }

        var bitmap = new RenderTargetBitmap(WIDTH, HEIGHT, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var pixels = new byte[WIDTH * HEIGHT * 4];
        bitmap.CopyPixels(pixels, WIDTH * 4, 0);
        return CatWeGamePopupReader.EncodeJpeg(pixels, WIDTH, HEIGHT);
    }

    private static FormattedText Text(string text, double size, FontWeight weight) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(new FontFamily("Microsoft YaHei"), FontStyles.Normal, weight, FontStretches.Normal), size, Brushes.White, 1.0);
}
