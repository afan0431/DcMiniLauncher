using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Serilog;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;
using ZXing.QrCode.Internal;

namespace XIVLauncher.CatHost;

/// <summary>
///     二维码的识别与生成, 单独抽出来好让测试替换
/// </summary>
public interface ICatWeGameQrCodec
{
    /// <summary>
    ///     在一张图里找二维码并读出内容; 找不到返回 null
    /// </summary>
    /// <param name="bgra">像素, 每个像素 4 字节（蓝、绿、红, 第 4 个字节不看）, 自上而下逐行</param>
    /// <param name="width">图宽</param>
    /// <param name="height">图高</param>
    string? Decode(byte[] bgra, int width, int height);

    /// <summary>
    ///     把内容画成一张带白边的二维码 PNG
    /// </summary>
    byte[] EncodePng(string content);
}

/// <summary>
///     用 ZXing.Net（纯托管）识别与生成二维码
/// </summary>
public sealed class CatZxingQrCodec : ICatWeGameQrCodec
{
    /// <summary>生成的二维码每个小格的边长（像素）</summary>
    private const int MODULE_PIXELS = 8;

    /// <summary>生成的二维码四周留白的格数</summary>
    private const int QUIET_ZONE_MODULES = 4;

    /// <inheritdoc />
    public string? Decode(byte[] bgra, int width, int height)
    {
        if (width <= 0 || height <= 0 || bgra.Length < width * height * 4)
            return null;

        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = false,
            Options    = new DecodingOptions { TryHarder = true, PossibleFormats = [BarcodeFormat.QR_CODE] }
        };

        // 按不带透明度的格式读: 截窗得到的像素第 4 个字节常常是 0, 当透明度用会把整张图算成白的
        var text = reader.Decode(new RGBLuminanceSource(bgra, width, height, RGBLuminanceSource.BitmapFormat.BGR32))?.Text;
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <inheritdoc />
    public byte[] EncodePng(string content)
    {
        var hints = new Dictionary<EncodeHintType, object>
        {
            [EncodeHintType.MARGIN]           = QUIET_ZONE_MODULES,
            [EncodeHintType.ERROR_CORRECTION] = ErrorCorrectionLevel.M,
            [EncodeHintType.CHARACTER_SET]    = "UTF-8"
        };

        // 宽高给 0 = 每格 1 像素的原始大小（已含留白）, 再按整数倍放大, 边缘不会糊
        var matrix = new QRCodeWriter().encode(content, BarcodeFormat.QR_CODE, 0, 0, hints);
        var width  = matrix.Width  * MODULE_PIXELS;
        var height = matrix.Height * MODULE_PIXELS;
        var pixels = new byte[width * height];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
                pixels[y * width + x] = matrix[x / MODULE_PIXELS, y / MODULE_PIXELS] ? (byte)0 : (byte)255;
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, pixels, width);
        bitmap.Freeze();

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}

/// <summary>
///     等 WeGame 登录期间对本机屏幕的真实操作。
///     <para>
///         登录窗口: wegame 进程里类名 TWINCONTROL、标题正好是「WeGame」的可见顶层窗口（登录后的主窗口标题是「Wegame」, 不算）。
///         截取用 PrintWindow, 窗口不在最前面也截得到; 点击用给窗口发鼠标消息的办法, 不移动鼠标、不改变前台窗口。
///     </para>
///     <para>设备验证窗口的读取和点击还没有实现（要等那个窗口出现后做过检查才知道怎么读）: 一律当作没有这个窗口。</para>
/// </summary>
public sealed class CatWeGameRealScreen(ICatWeGameQrCodec codec) : ICatWeGameScreen
{
    private const string LOGIN_PROCESS_NAME = "wegame";
    private const string LOGIN_WINDOW_CLASS = "TWINCONTROL";
    private const string LOGIN_WINDOW_TITLE = "WeGame";

    private const uint PW_RENDERFULLCONTENT = 0x00000002;
    private const uint WM_MOUSEMOVE         = 0x0200;
    private const uint WM_LBUTTONDOWN       = 0x0201;
    private const uint WM_LBUTTONUP         = 0x0202;
    private const int  MK_LBUTTON           = 0x0001;

    private const int BITMAP_INFO_HEADER_SIZE = 40;

    private string? lastLink;
    private byte[]? lastPng;

    /// <inheritdoc />
    public CatWeGameLoginWindow? CaptureLoginWindow()
    {
        var window = FindLoginWindow();

        if (window == IntPtr.Zero || IsIconic(window) || !GetClientRect(window, out var client) || client.Right <= 0 || client.Bottom <= 0)
            return null;

        string? link  = null;
        byte[]? thumb = null;
        var     tabs  = true;

        if (TryCapture(window, out var pixels, out var width, out var height))
        {
            var panel = CropLoginPanel(pixels, width, height, out var panelWidth, out var panelHeight);
            thumb = ThumbOf(panel, panelWidth, panelHeight);
            tabs  = TabsVisible(panel, panelWidth, panelHeight);
            link  = DecodePanel(panel, panelWidth, panelHeight);
        }

        if (link == null)
            return new CatWeGameLoginWindow(client.Right, client.Bottom, PanelThumb: thumb, TabsVisible: tabs);

        // 同一个二维码不重复画
        if (!string.Equals(link, lastLink, StringComparison.Ordinal) || lastPng == null)
        {
            lastPng  = codec.EncodePng(link);
            lastLink = link;
        }

        return new CatWeGameLoginWindow(client.Right, client.Bottom, link, lastPng, thumb, tabs);
    }

    /// <inheritdoc />
    public bool ClickLoginWindow(int x, int y)
    {
        var window = FindLoginWindow();

        if (window == IntPtr.Zero)
            return false;

        var position = PackPoint(x, y);

        // 三条消息连着发, 中间不能停: 真正的鼠标不在窗口上, 一停窗口就收到"鼠标离开",
        // 页签还能点动, 但底部"QQ 扫码登录"这类文字链接的按下状态会被取消, 点了没反应（实机验证过）
        return PostMessageW(window, WM_MOUSEMOVE, IntPtr.Zero, position)
               && PostMessageW(window, WM_LBUTTONDOWN, MK_LBUTTON, position)
               && PostMessageW(window, WM_LBUTTONUP, IntPtr.Zero, position);
    }

    /// <inheritdoc />
    public CatWeGameSmsPrompt? FindSmsPrompt() =>
        null;

    /// <inheritdoc />
    public bool ConfirmSmsPrompt() =>
        false;

    /// <summary>
    ///     鼠标消息的坐标参数: 低 16 位是横坐标, 高 16 位是纵坐标
    /// </summary>
    internal static IntPtr PackPoint(int x, int y) =>
        (IntPtr)((y << 16) | (x & 0xFFFF));

    /// <summary>
    ///     把图按整数倍放大（每个像素原样重复, 不做平滑）
    /// </summary>
    internal static byte[] Upscale(byte[] bgra, int width, int height, int factor)
    {
        var scaledWidth = width * factor;
        var scaled      = new byte[scaledWidth * height * factor * 4];

        for (var y = 0; y < height * factor; y++)
        {
            var sourceRow = y / factor * width * 4;
            var targetRow = y * scaledWidth * 4;

            for (var x = 0; x < scaledWidth; x++)
                Buffer.BlockCopy(bgra, sourceRow + x / factor * 4, scaled, targetRow + x * 4, 4);
        }

        return scaled;
    }

    /// <summary>
    ///     只识别左侧登录栏的下半截（二维码只会出现在那里, 约占整窗的六分之一）; 识别不出时放大一倍再试一次
    ///     （二维码只有一百多像素宽）。每一轮、每次点击后都要识别, 对整窗做会让切换明显变慢
    /// </summary>
    private string? DecodePanel(byte[] panel, int panelWidth, int panelHeight) =>
        codec.Decode(panel, panelWidth, panelHeight) ?? codec.Decode(Upscale(panel, panelWidth, panelHeight, 2), panelWidth * 2, panelHeight * 2);

    /// <summary>缩成小图时每格的边长（像素）</summary>
    private const int THUMB_CELL = 10;

    /// <summary>
    ///     把登录栏那一块缩成小图: 每 10×10 像素一格, 存这一格的平均亮度。比较两张小图就知道画面变没变,
    ///     又不会被半透明登录栏后面动态背景的轻微浮动干扰
    /// </summary>
    internal static byte[] ThumbOf(byte[] panel, int panelWidth, int panelHeight)
    {
        var columns = Math.Max(1, panelWidth / THUMB_CELL);
        var rows    = Math.Max(1, panelHeight / THUMB_CELL);
        var thumb   = new byte[columns * rows];

        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                var sum   = 0;
                var count = 0;

                for (var y = row * THUMB_CELL; y < Math.Min(panelHeight, (row + 1) * THUMB_CELL); y++)
                {
                    var offset = (y * panelWidth + column * THUMB_CELL) * 4;

                    for (var x = 0; x < THUMB_CELL && column * THUMB_CELL + x < panelWidth; x++, offset += 4)
                    {
                        sum += (panel[offset] + panel[offset + 1] * 2 + panel[offset + 2]) / 4;
                        count++;
                    }
                }

                thumb[row * columns + column] = (byte)(count == 0 ? 0 : sum / count);
            }
        }

        return thumb;
    }

    /// <summary>
    ///     QQ / 微信两个页签画出来了没有: 页签在登录栏最上面那一条（按 1210×680 量是横向 98–202、纵向 264–290,
    ///     裁出的登录栏从纵向 230 起算）, 画出来后里面有浅色的图标; 还没画出来时这一条是一片暗色
    /// </summary>
    internal static bool TabsVisible(byte[] panel, int panelWidth, int panelHeight)
    {
        var left   = panelWidth * 98 / 300;
        var right  = Math.Min(panelWidth, panelWidth * 202 / 300);
        var top    = panelHeight * 34 / 450;
        var bottom = Math.Min(panelHeight, panelHeight * 60 / 450);
        var bright = 0;

        for (var y = top; y < bottom; y++)
        {
            var offset = (y * panelWidth + left) * 4;

            for (var x = left; x < right; x++, offset += 4)
            {
                if ((panel[offset] + panel[offset + 1] * 2 + panel[offset + 2]) / 4 > 110)
                    bright++;
            }
        }

        return bright >= Math.Max(12, (right - left) * (bottom - top) / 60);
    }

    /// <summary>
    ///     裁出左侧登录栏从页签往下的部分: 按 1210×680 量是横向 0–300、纵向 230–680, 按窗口实际大小换算
    /// </summary>
    internal static byte[] CropLoginPanel(byte[] bgra, int width, int height, out int panelWidth, out int panelHeight)
    {
        panelWidth = Math.Clamp((int)Math.Round(width * 300d / 1210), 1, width);
        var top    = Math.Clamp((int)Math.Round(height * 230d / 680), 0, height - 1);
        panelHeight = height - top;

        var panel = new byte[panelWidth * panelHeight * 4];

        for (var y = 0; y < panelHeight; y++)
            Buffer.BlockCopy(bgra, ((top + y) * width) * 4, panel, y * panelWidth * 4, panelWidth * 4);

        return panel;
    }

    private static IntPtr FindLoginWindow()
    {
        var found = IntPtr.Zero;

        EnumWindows
        (
            (window, _) =>
            {
                if (!IsWindowVisible(window) || GetClassNameOf(window) != LOGIN_WINDOW_CLASS || GetTitleOf(window) != LOGIN_WINDOW_TITLE || !BelongsToWeGame(window))
                    return true;

                found = window;
                return false;
            },
            IntPtr.Zero
        );

        return found;
    }

    private static bool BelongsToWeGame(IntPtr window)
    {
        GetWindowThreadProcessId(window, out var pid);

        if (pid == 0)
            return false;

        try
        {
            using var process = Process.GetProcessById((int)pid);
            return string.Equals(process.ProcessName, LOGIN_PROCESS_NAME, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static string GetClassNameOf(IntPtr window)
    {
        var buffer = new StringBuilder(256);
        return GetClassNameW(window, buffer, buffer.Capacity) > 0 ? buffer.ToString() : string.Empty;
    }

    private static string GetTitleOf(IntPtr window)
    {
        var buffer = new StringBuilder(256);
        return GetWindowTextW(window, buffer, buffer.Capacity) > 0 ? buffer.ToString() : string.Empty;
    }

    /// <summary>
    ///     把整个窗口画到内存里的位图上, 取出像素
    /// </summary>
    private static bool TryCapture(IntPtr window, out byte[] pixels, out int width, out int height)
    {
        pixels = [];
        width  = 0;
        height = 0;

        if (!GetWindowRect(window, out var rect))
            return false;

        width  = rect.Right  - rect.Left;
        height = rect.Bottom - rect.Top;

        if (width <= 0 || height <= 0)
            return false;

        var info = new BITMAPINFO
        {
            biSize     = BITMAP_INFO_HEADER_SIZE,
            biWidth    = width,
            biHeight   = -height, // 负数 = 自上而下
            biPlanes   = 1,
            biBitCount = 32
        };

        var memoryDc = CreateCompatibleDC(IntPtr.Zero);

        if (memoryDc == IntPtr.Zero)
            return false;

        var bitmap = IntPtr.Zero;

        try
        {
            bitmap = CreateDIBSection(memoryDc, ref info, 0, out var bits, IntPtr.Zero, 0);

            if (bitmap == IntPtr.Zero || bits == IntPtr.Zero)
                return false;

            var previous = SelectObject(memoryDc, bitmap);
            var printed  = PrintWindow(window, memoryDc, PW_RENDERFULLCONTENT);

            if (printed)
            {
                pixels = new byte[width * height * 4];
                Marshal.Copy(bits, pixels, 0, pixels.Length);
            }
            else
                Log.Debug("[CatHost] 截取 WeGame 登录窗口失败, 错误码 {Error}", Marshal.GetLastWin32Error());

            SelectObject(memoryDc, previous);
            return printed;
        }
        finally
        {
            if (bitmap != IntPtr.Zero)
                DeleteObject(bitmap);

            DeleteDC(memoryDc);
        }
    }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    /// <summary>BITMAPINFOHEADER 加上 32 位色用不到的调色板位置</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public int   biSize;
        public int   biWidth;
        public int   biHeight;
        public short biPlanes;
        public short biBitCount;
        public int   biCompression;
        public int   biSizeImage;
        public int   biXPelsPerMeter;
        public int   biYPelsPerMeter;
        public int   biClrUsed;
        public int   biClrImportant;
        public int   bmiColors;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr window, StringBuilder className, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr window, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr window, out RECT rect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFO info, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr gdiObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr gdiObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr dc);
}
