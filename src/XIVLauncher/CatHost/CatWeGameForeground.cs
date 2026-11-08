using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Serilog;

namespace XIVLauncher.CatHost;

/// <summary>
///     WeGame 窗口在屏幕上的位置与客户区尺寸: 左上角的屏幕坐标 + 客户区的像素宽高
/// </summary>
public sealed record CatWeGameWindowRect(int ScreenX, int ScreenY, int ClientWidth, int ClientHeight);

/// <summary>
///     硬件自动登录要用到的前台与焦点操作: 硬件键盘的按键只会落到当前前台窗口里有焦点的那个控件, 所以输入前要把 WeGame 登录窗口切到前台、
///     把焦点放到要输入的框里, 并在每个按键前核对。全程不动鼠标: 切前台和放焦点都靠窗口消息。单独抽出来好让测试替换。
/// </summary>
public interface ICatWeGameForeground
{
    /// <summary>WeGame 登录窗口在屏幕上的位置与客户区尺寸; 没有登录窗口（没出现、已登录、最小化）时返回 null</summary>
    CatWeGameWindowRect? LoginWindow();

    /// <summary>把 WeGame 登录窗口切到前台并关掉它的输入法; 切过去了返回 true</summary>
    bool FocusLoginWindow();

    /// <summary>当前前台窗口是不是 WeGame 登录窗口（输账号时每个按键前都要核对）</summary>
    bool LoginWindowIsForeground();

    /// <summary>登录窗口上的密码框露出来了没有（露出来 = 在 QQ 账号密码页）</summary>
    bool PasswordBoxVisible();

    /// <summary>把焦点放到密码框里（登录窗口要已在前台）; 放进去了返回 true</summary>
    bool FocusPasswordBox();

    /// <summary>登录窗口在前台、并且焦点在密码框里（输密码时每个按键前都要核对）</summary>
    bool PasswordBoxFocused();

    /// <summary>
    ///     从 <paramref name="since" />（本机时间）以来 WeGame 有没有拉起游戏的登录组件 sdologin（点「启动」之后出现, version.dll 就在它里面取登录信息）
    /// </summary>
    bool GameLoginStartedSince(DateTime since) =>
        false;
}

/// <summary>
///     认 WeGame 的几个窗口。都在 wegame 进程里、类名 TWINCONTROL、标题「WeGame」的有两种, 按客户区形状分:
///     <list type="bullet">
///         <item>登录窗口: 宽高比约 16:9（StartFor 拉起 1210×680, 手动打开 960×540, 重开见过 864×486）</item>
///         <item>首次点「启动」后的「区服状态」选大区对话框: 720×540（4:3）</item>
///     </list>
///     登录后的主界面不在 wegame 进程里, 是 browser 进程里类名 CefTopWindow、标题「WeGame」的窗口（1600×900, 右下角「启动」）。
///     wegame 进程里另有一个标题「Wegame」的 1150×720 窗口, 登录后也一直隐藏, 不是主界面。
/// </summary>
internal static class CatWeGameWindows
{
    private const string WE_GAME_PROCESS   = "wegame";
    private const string BROWSER_PROCESS   = "browser";
    private const string WE_GAME_CLASS     = "TWINCONTROL";
    private const string MAIN_CLASS        = "CefTopWindow";
    private const string WINDOW_TITLE      = "WeGame";
    private const string CEF_BROWSER_CLASS = "CefBrowserWindow";
    private const string CEF_WIDGET_CLASS  = "Chrome_WidgetWin_0";
    private const string EDIT_CLASS        = "Edit";

    /// <summary>
    ///     登录窗口的客户区形状: 宽高比至少 1.5, 且够大（排除别的小提示窗）
    /// </summary>
    internal static bool IsLoginShape(int width, int height) =>
        width >= 600 && height >= 300 && width * 2 >= height * 3;

    /// <summary>
    ///     选大区对话框的客户区形状: 宽高比 1.2 到 1.5 之间（实测 720×540）
    /// </summary>
    internal static bool IsRegionDialogShape(int width, int height) =>
        width >= 500 && height >= 400 && width * 2 < height * 3 && width * 5 >= height * 6;

    /// <summary>
    ///     密码框所在的那一行: 按 1210×680 量是纵向 364–388, 中心 376; 放宽到 340–410
    /// </summary>
    internal static bool InPasswordRow(double centerY, int clientHeight) =>
        clientHeight > 0 && centerY * 680d / clientHeight is > 340 and < 410;

    /// <summary>登录窗口（最小化的也算: 最小化的窗口量不出形状, 只看类名、标题和进程）</summary>
    internal static IntPtr FindLoginWindow() =>
        FindTop(WE_GAME_PROCESS, WE_GAME_CLASS, window => IsIconic(window) || ClientSize(window) is { } size && IsLoginShape(size.Width, size.Height));

    /// <summary>选大区对话框</summary>
    internal static IntPtr FindRegionDialog() =>
        FindTop(WE_GAME_PROCESS, WE_GAME_CLASS, window => !IsIconic(window) && ClientSize(window) is { } size && IsRegionDialogShape(size.Width, size.Height));

    /// <summary>登录后的主界面</summary>
    internal static IntPtr FindMainWindow() =>
        FindTop(BROWSER_PROCESS, MAIN_CLASS, window => !IsIconic(window));

    /// <summary>
    ///     主界面里真正收鼠标消息的网页窗口（CefTopWindow → CefBrowserWindow → Chrome_WidgetWin_0）; 找不到时退回主界面本身
    /// </summary>
    internal static IntPtr MainInputWindow(IntPtr main)
    {
        var browser = FindWindowExW(main, IntPtr.Zero, CEF_BROWSER_CLASS, null);
        var widget  = browser == IntPtr.Zero ? IntPtr.Zero : FindWindowExW(browser, IntPtr.Zero, CEF_WIDGET_CLASS, null);
        return widget == IntPtr.Zero ? main : widget;
    }

    /// <summary>
    ///     登录窗口上的密码框: 密码框不是网页画的, 是叠在网页上的原生 Edit 子窗口（所以发给登录窗口的鼠标消息点不进去）。
    ///     取可见、且落在密码框那一行的 Edit; 没在账号密码页时它是隐藏的
    /// </summary>
    internal static IntPtr FindPasswordEdit(IntPtr login)
    {
        if (login == IntPtr.Zero || ClientSize(login) is not { } size)
            return IntPtr.Zero;

        var clientHeight = size.Height;

        var origin = new POINT();

        if (!ClientToScreen(login, ref origin))
            return IntPtr.Zero;

        var found = IntPtr.Zero;

        EnumChildWindows
        (
            login,
            (child, _) =>
            {
                if (!IsWindowVisible(child) || ClassOf(child) != EDIT_CLASS || !GetWindowRect(child, out var rect))
                    return true;

                if (!InPasswordRow((rect.Top + rect.Bottom) / 2d - origin.Y, clientHeight))
                    return true;

                found = child;
                return false;
            },
            IntPtr.Zero
        );

        return found;
    }

    /// <summary>客户区宽高; 取不到时返回 null</summary>
    internal static (int Width, int Height)? ClientSize(IntPtr window) =>
        GetClientRect(window, out var client) && client.Right > 0 && client.Bottom > 0 ? (client.Right, client.Bottom) : null;

    /// <summary>窗口所在线程当前有焦点的窗口</summary>
    internal static IntPtr FocusedIn(IntPtr window)
    {
        var thread = GetWindowThreadProcessId(window, out _);
        var info   = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        return thread != 0 && GetGUIThreadInfo(thread, ref info) ? info.hwndFocus : IntPtr.Zero;
    }

    /// <summary>
    ///     在窗口的客户区坐标上点一下: 鼠标消息直接发给窗口, 不移动鼠标、不改变前台。
    ///     先移过去停一下再按下抬起: 网页里的按钮要先收到「鼠标移入」才认点击
    /// </summary>
    internal static bool PostClick(IntPtr window, int x, int y, TimeSpan hover)
    {
        if (window == IntPtr.Zero)
            return false;

        var position = PackPoint(x, y);

        if (!PostMessageW(window, WM_MOUSEMOVE, IntPtr.Zero, position))
            return false;

        if (hover > TimeSpan.Zero)
            Thread.Sleep(hover);

        return PostMessageW(window, WM_LBUTTONDOWN, MK_LBUTTON, position)
               && PostMessageW(window, WM_LBUTTONUP, IntPtr.Zero, position);
    }

    /// <summary>鼠标消息的坐标参数: 低 16 位是横坐标, 高 16 位是纵坐标</summary>
    internal static IntPtr PackPoint(int x, int y) =>
        (IntPtr)((y << 16) | (x & 0xFFFF));

    private static IntPtr FindTop(string process, string className, Func<IntPtr, bool> match)
    {
        var found = IntPtr.Zero;

        EnumWindows
        (
            (window, _) =>
            {
                if (!IsWindowVisible(window) || ClassOf(window) != className || TitleOf(window) != WINDOW_TITLE || !BelongsTo(window, process) || !match(window))
                    return true;

                found = window;
                return false;
            },
            IntPtr.Zero
        );

        return found;
    }

    private static bool BelongsTo(IntPtr window, string processName)
    {
        GetWindowThreadProcessId(window, out var pid);

        if (pid == 0)
            return false;

        try
        {
            using var process = Process.GetProcessById((int)pid);
            return string.Equals(process.ProcessName, processName, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    internal static string ClassOf(IntPtr window)
    {
        var buffer = new StringBuilder(256);
        return GetClassNameW(window, buffer, buffer.Capacity) > 0 ? buffer.ToString() : string.Empty;
    }

    private static string TitleOf(IntPtr window)
    {
        var buffer = new StringBuilder(256);
        return GetWindowTextW(window, buffer, buffer.Capacity) > 0 ? buffer.ToString() : string.Empty;
    }

    private const uint WM_MOUSEMOVE   = 0x0200;
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_LBUTTONUP   = 0x0202;
    private const int  MK_LBUTTON     = 0x0001;

    internal delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO
    {
        public int    cbSize;
        public int    flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public RECT   rcCaret;
    }

    [DllImport("user32.dll")]
    internal static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowExW(IntPtr parent, IntPtr childAfter, string? className, string? title);

    [DllImport("user32.dll")]
    internal static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    internal static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr window, StringBuilder className, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr window, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    internal static extern bool GetWindowRect(IntPtr window, out RECT rect);

    [DllImport("user32.dll")]
    internal static extern bool GetClientRect(IntPtr window, out RECT rect);

    [DllImport("user32.dll")]
    internal static extern bool ClientToScreen(IntPtr window, ref POINT point);

    [DllImport("user32.dll")]
    private static extern bool GetGUIThreadInfo(uint threadId, ref GUITHREADINFO info);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}

/// <summary>
///     前台与焦点操作的真实实现。切前台: 先注入一次 Alt 键松开（系统只允许刚收到输入的进程切前台, 注入一个按键就满足了）再 SetForegroundWindow;
///     放焦点到密码框: 先给密码框发鼠标消息, 没放进去再挂到 WeGame 的界面线程上直接 SetFocus。全程不移动鼠标（11-08 实机验证）。
/// </summary>
public sealed class CatWeGameRealForeground : ICatWeGameForeground
{
    private const byte VK_MENU          = 0x12;
    private const uint KEYEVENTF_KEYUP  = 0x0002;
    private const int  SW_RESTORE       = 9;
    private const uint WM_IME_CONTROL   = 0x0283;
    private static readonly IntPtr IMC_SETOPENSTATUS = 0x0006;
    private const uint SMTO_ABORTIFHUNG = 0x0002;

    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(300);

    /// <inheritdoc />
    public CatWeGameWindowRect? LoginWindow()
    {
        var window = CatWeGameWindows.FindLoginWindow();

        if (window == IntPtr.Zero || CatWeGameWindows.IsIconic(window) || CatWeGameWindows.ClientSize(window) is not { } size)
            return null;

        var origin = new CatWeGameWindows.POINT();
        CatWeGameWindows.ClientToScreen(window, ref origin);
        return new CatWeGameWindowRect(origin.X, origin.Y, size.Width, size.Height);
    }

    /// <inheritdoc />
    public bool FocusLoginWindow()
    {
        var window = CatWeGameWindows.FindLoginWindow();

        if (window == IntPtr.Zero)
            return false;

        if (CatWeGameWindows.IsIconic(window))
            ShowWindow(window, SW_RESTORE);

        if (GetForegroundWindow() != window)
        {
            keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
            keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            SetForegroundWindow(window);
            BringWindowToTop(window);
            Thread.Sleep(Settle);
        }

        CloseIme(window);
        return GetForegroundWindow() == window;
    }

    /// <inheritdoc />
    public bool LoginWindowIsForeground()
    {
        var window = GetForegroundWindow();
        return window != IntPtr.Zero && window == CatWeGameWindows.FindLoginWindow();
    }

    /// <inheritdoc />
    public bool PasswordBoxVisible() =>
        CatWeGameWindows.FindPasswordEdit(CatWeGameWindows.FindLoginWindow()) != IntPtr.Zero;

    /// <inheritdoc />
    public bool FocusPasswordBox()
    {
        var login = CatWeGameWindows.FindLoginWindow();
        var edit  = CatWeGameWindows.FindPasswordEdit(login);

        if (edit == IntPtr.Zero || GetForegroundWindow() != login)
            return false;

        // 点在框的最右边, 光标落在已有内容的末尾
        if (CatWeGameWindows.GetClientRect(edit, out var box))
            CatWeGameWindows.PostClick(edit, Math.Max(0, box.Right - 4), box.Bottom / 2, TimeSpan.Zero);

        Thread.Sleep(Settle);

        if (CatWeGameWindows.FocusedIn(login) == edit)
            return true;

        Log.Information("[CatHost] 点密码框没拿到焦点, 改为直接设焦点");

        var target  = CatWeGameWindows.GetWindowThreadProcessId(login, out _);
        var current = GetCurrentThreadId();

        if (target != 0 && AttachThreadInput(current, target, true))
        {
            try
            {
                SetFocus(edit);
            }
            finally
            {
                AttachThreadInput(current, target, false);
            }
        }

        Thread.Sleep(Settle);
        return CatWeGameWindows.FocusedIn(login) == edit;
    }

    /// <inheritdoc />
    public bool PasswordBoxFocused()
    {
        var login = CatWeGameWindows.FindLoginWindow();

        if (login == IntPtr.Zero || GetForegroundWindow() != login)
            return false;

        var edit = CatWeGameWindows.FindPasswordEdit(login);
        return edit != IntPtr.Zero && CatWeGameWindows.FocusedIn(login) == edit;
    }

    /// <inheritdoc />
    public bool GameLoginStartedSince(DateTime since)
    {
        foreach (var process in Process.GetProcessesByName("sdologin"))
        {
            using (process)
            {
                try
                {
                    if (process.StartTime >= since)
                        return true;
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // 进程刚退出或读不到启动时间: 不算
                }
            }
        }

        return false;
    }

    /// <summary>
    ///     关掉登录窗口的输入法: 中文输入法开着时硬件键盘打的字母会被当成拼音
    /// </summary>
    private static void CloseIme(IntPtr window)
    {
        var ime = ImmGetDefaultIMEWnd(window);

        if (ime != IntPtr.Zero)
            SendMessageTimeoutW(ime, WM_IME_CONTROL, IMC_SETOPENSTATUS, IntPtr.Zero, SMTO_ABORTIFHUNG, 500, out _);
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint attach, uint attachTo, bool doAttach);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr window);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("imm32.dll")]
    private static extern IntPtr ImmGetDefaultIMEWnd(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageTimeoutW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
}
