using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace XIVLauncher.CatHost;

/// <summary>
///     WeGame 窗口在屏幕上的位置与客户区尺寸: 左上角的屏幕坐标 + 客户区的像素宽高。
///     硬件鼠标用绝对坐标点击, 要把登录窗口里按 1210×680 量的点换算成「窗口左上 + 客户区内偏移」的屏幕坐标。
/// </summary>
public sealed record CatWeGameWindowRect(int ScreenX, int ScreenY, int ClientWidth, int ClientHeight);

/// <summary>一块屏幕的像素尺寸（硬件鼠标按 0..4095 的比例定位, 要知道满量程对应多少像素）</summary>
public sealed record CatScreenSize(int Width, int Height);

/// <summary>
///     硬件自动登录要用到的前台窗口操作: 硬件键鼠的按键只会落到当前前台窗口, 所以输入前必须把 WeGame 登录窗口切到前台、
///     并在每次输入前核对前台窗口仍是它。这一套跟 <see cref="CatWeGameRealScreen" /> 的后台截图 / 发消息点击是两回事, 单独抽出来好让测试替换。
/// </summary>
public interface ICatWeGameForeground
{
    /// <summary>WeGame 登录窗口在屏幕上的位置与客户区尺寸; 没有登录窗口（没出现、已登录、最小化）时返回 null</summary>
    CatWeGameWindowRect? LoginWindow();

    /// <summary>把 WeGame 登录窗口切到前台; 登录窗口不在时返回 false</summary>
    bool FocusLoginWindow();

    /// <summary>当前前台窗口是不是 WeGame 登录窗口（每次硬件输入前都要核对）</summary>
    bool LoginWindowIsForeground();

    /// <summary>主屏的像素尺寸（硬件鼠标绝对定位的满量程）</summary>
    CatScreenSize PrimaryScreen();

    /// <summary>
    ///     登录成功后的 WeGame 主窗口（标题 Wegame, 右下角有「启动」）在屏幕上的位置与客户区尺寸; 还没登录成功时返回 null。
    ///     配合 <see cref="LoginWindow" /> 返回 null 一起判断「登录窗没了 + 主窗口出现 = 登录成功」
    /// </summary>
    CatWeGameWindowRect? MainWindow() =>
        null;
}

/// <summary>
///     前台窗口操作的真实实现。登录窗口与 <see cref="CatWeGameRealScreen" /> 同口径: wegame 进程里类名 TWINCONTROL、标题正好是「WeGame」的可见顶层窗口;
///     登录后的主窗口标题是「Wegame」（小写 g）。窗口里「启动」按钮的精确位置、主窗口归属进程留实机核对（见 window-geometry.md 待实测项）。
/// </summary>
public sealed class CatWeGameRealForeground : ICatWeGameForeground
{
    private const string LOGIN_PROCESS_NAME  = "wegame";
    private const string LOGIN_WINDOW_CLASS  = "TWINCONTROL";
    private const string LOGIN_WINDOW_TITLE  = "WeGame";
    private const string MAIN_WINDOW_TITLE   = "Wegame";

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    /// <inheritdoc />
    public CatWeGameWindowRect? LoginWindow() =>
        RectOf(FindWindow(LOGIN_WINDOW_TITLE));

    /// <inheritdoc />
    public CatWeGameWindowRect? MainWindow() =>
        RectOf(FindWindow(MAIN_WINDOW_TITLE));

    /// <inheritdoc />
    public bool FocusLoginWindow()
    {
        var window = FindWindow(LOGIN_WINDOW_TITLE);

        if (window == IntPtr.Zero)
            return false;

        SetForegroundWindow(window);
        return true;
    }

    /// <inheritdoc />
    public bool LoginWindowIsForeground()
    {
        var window = FindWindow(LOGIN_WINDOW_TITLE);
        return window != IntPtr.Zero && GetForegroundWindow() == window;
    }

    /// <inheritdoc />
    public CatScreenSize PrimaryScreen() =>
        new(Math.Max(1, GetSystemMetrics(SM_CXSCREEN)), Math.Max(1, GetSystemMetrics(SM_CYSCREEN)));

    private static CatWeGameWindowRect? RectOf(IntPtr window)
    {
        if (window == IntPtr.Zero || IsIconic(window) || !GetWindowRect(window, out var rect) || !GetClientRect(window, out var client))
            return null;

        var width  = client.Right - client.Left;
        var height = client.Bottom - client.Top;

        if (width <= 0 || height <= 0)
            return null;

        // 客户区左上角在屏幕上的坐标（窗口矩形是含边框的, 要用 ClientToScreen 换算客户区原点）
        var origin = new POINT { X = 0, Y = 0 };

        if (!ClientToScreen(window, ref origin))
            origin = new POINT { X = rect.Left, Y = rect.Top };

        return new CatWeGameWindowRect(origin.X, origin.Y, width, height);
    }

    private static IntPtr FindWindow(string title)
    {
        var found = IntPtr.Zero;

        EnumWindows
        (
            (window, _) =>
            {
                if (!IsWindowVisible(window) || GetClassNameOf(window) != LOGIN_WINDOW_CLASS || GetTitleOf(window) != title || !BelongsToWeGame(window))
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

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

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

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr window, ref POINT point);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
