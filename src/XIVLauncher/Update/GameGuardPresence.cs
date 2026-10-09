using Serilog;

namespace XIVLauncher.Update;

/// <summary>
///     界面版启动器守着游戏（等它退出, 好清占用记录、处理崩溃重启）期间持有一个命名互斥量;
///     别的启动器据此不应用更新 —— 更新会结束安装目录下的所有进程, 守护一断, 崩溃就没人重启。
///     无界面启动的同类标记见 <see cref="XIVLauncher.CatHost.CatHostPresence" />。
///     多个启动器各自打开同一个互斥量对象, 只持有句柄、不加锁, 最后一个放手后对象自动消失。
/// </summary>
public static class GameGuardPresence
{
    /// <summary>互斥量名（当前登录会话内可见）</summary>
    public const string MUTEX_NAME = @"Local\DcMiniLauncher-GameGuard";

    private static readonly object Gate = new();

    private static Mutex? held;

    private static int holders;

    /// <summary>
    ///     本进程开始守一个游戏; 返回值释放时结束。同一进程可同时守多个, 全部结束后才放手
    /// </summary>
    public static IDisposable Hold()
    {
        lock (Gate)
        {
            if (holders++ == 0)
            {
                try
                {
                    held = new Mutex(false, MUTEX_NAME);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "[GameGuard] 创建守护标记互斥量失败, 别的启动器可能在游戏运行期间应用更新");
                }
            }
        }

        return new Holder();
    }

    /// <summary>
    ///     是否有界面版启动器正守着游戏（含本进程）
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
            // 存在但没有权限打开（例如由管理员进程创建）, 同样算在守护
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[GameGuard] 检查守护标记失败");
            return false;
        }
    }

    private static void Release()
    {
        lock (Gate)
        {
            if (--holders > 0)
                return;

            held?.Dispose();
            held = null;
        }
    }

    private sealed class Holder : IDisposable
    {
        private int released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
                Release();
        }
    }
}
