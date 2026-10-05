using Serilog;

namespace XIVLauncher.CatHost;

/// <summary>
///     无界面启动进程在运行期间持有一个命名互斥量; 界面版据此判断「有游戏在由 Cat 运行」,
///     这时不应用启动器更新（更新会结束安装目录下的所有进程, 包括正在守护游戏的无界面进程）。
///     多个无界面进程同时运行时各自打开同一个互斥量对象, 只持有句柄、不加锁, 最后一个退出后对象自动消失。
/// </summary>
public static class CatHostPresence
{
    /// <summary>互斥量名（当前登录会话内可见）</summary>
    public const string MUTEX_NAME = @"Local\DcMiniLauncher-CatHost";

    private static Mutex? held;

    /// <summary>
    ///     本进程开始持有互斥量, 直到进程退出或 <see cref="Release" />
    /// </summary>
    public static void Hold()
    {
        if (held != null)
            return;

        try
        {
            held = new Mutex(false, MUTEX_NAME);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[CatHost] 创建运行标记互斥量失败, 界面版可能在游戏运行期间应用更新");
        }
    }

    /// <summary>
    ///     释放本进程持有的互斥量句柄
    /// </summary>
    public static void Release()
    {
        held?.Dispose();
        held = null;
    }

    /// <summary>
    ///     是否有无界面启动进程在运行（含本进程）
    /// </summary>
    public static bool IsAnyRunning()
    {
        try
        {
            if (!Mutex.TryOpenExisting(MUTEX_NAME, out var existing))
                return false;

            existing.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // 存在但没有权限打开（例如由管理员进程创建）, 同样算在运行
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[CatHost] 检查无界面启动进程失败");
            return false;
        }
    }
}
