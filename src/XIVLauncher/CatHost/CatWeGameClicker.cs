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
///     给登录窗口发后台鼠标消息点击（不移动鼠标、不抢前台）。接了 CH9329 的电脑也这样点: 硬件只用来打字
/// </summary>
public sealed class CatWeGameScreenClicker(ICatWeGameScreen screen) : ICatWeGameClicker
{
    /// <inheritdoc />
    public Task<bool> ClickAsync(int x, int y, CancellationToken cancellationToken) =>
        Task.FromResult(screen.ClickLoginWindow(x, y));
}
