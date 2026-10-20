using Serilog;

namespace XIVLauncher.CatHost;

/// <summary>
///     在 WeGame 登录窗口里点一下（坐标是登录窗口客户区坐标）, 单独抽出来好让测试替换
/// </summary>
public interface ICatWeGameClicker
{
    /// <summary>
    ///     点一下; 没点出去（登录窗口不在、前台核对不对）返回 false
    /// </summary>
    Task<bool> ClickAsync(int x, int y, CancellationToken cancellationToken);
}

/// <summary>
///     给登录窗口发后台鼠标消息点击（不移动鼠标、不抢前台）
/// </summary>
public sealed class CatWeGameScreenClicker(ICatWeGameScreen screen) : ICatWeGameClicker
{
    /// <inheritdoc />
    public Task<bool> ClickAsync(int x, int y, CancellationToken cancellationToken) =>
        Task.FromResult(screen.ClickLoginWindow(x, y));
}

/// <summary>
///     本机接了硬件键鼠（CH9329）时用硬件鼠标点: 先把登录窗口切到前台、核对前台确实是它, 再换成屏幕坐标点（同硬件自动登录）;
///     没接、或没给前台窗口操作时退回后台鼠标消息。有没有硬件只在第一次点击时探测一次
/// </summary>
public sealed class CatWeGameClicker(ICatWeGameScreen screen, ICatHidKeyboard? keyboard, ICatWeGameForeground? foreground) : ICatWeGameClicker
{
    private readonly object sync = new();

    private bool? hardware;

    /// <inheritdoc />
    public async Task<bool> ClickAsync(int x, int y, CancellationToken cancellationToken)
    {
        if (!HasHardware() || keyboard is not { } hid || foreground is not { } windows)
            return screen.ClickLoginWindow(x, y);

        windows.FocusLoginWindow();

        if (!windows.LoginWindowIsForeground() || windows.LoginWindow() is not { } rect)
        {
            Log.Warning("[CatHost] 要用硬件鼠标点 WeGame 登录窗口, 但前台不是登录窗口, 这一下没点");
            return false;
        }

        var size = windows.PrimaryScreen();
        await hid.ClickAsync(rect.ScreenX + x, rect.ScreenY + y, size.Width, size.Height, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private bool HasHardware()
    {
        if (keyboard == null || foreground == null)
            return false;

        lock (sync)
            return hardware ??= keyboard.TryOpen();
    }
}
