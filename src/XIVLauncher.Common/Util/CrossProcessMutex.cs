using System.Security.Cryptography;
using System.Text;
using Serilog;

namespace XIVLauncher.Common.Util;

/// <summary>
///     跨进程的命名互斥量, 可在 async 代码里持有: 由专用线程获取与释放（互斥量有线程归属）。
///     持有进程异常退出时系统会放弃它, 下一个等待者照常拿到。
/// </summary>
public sealed class CrossProcessMutex : IDisposable
{
    private readonly ManualResetEventSlim release = new(false);
    private readonly Thread               owner;

    private CrossProcessMutex(Thread owner) =>
        this.owner = owner;

    /// <summary>
    ///     由一个本地路径生成互斥量名（当前登录会话内可见）, 同一路径得到同一个名字
    /// </summary>
    public static string NameForPath(string prefix, string path)
    {
        var normalized = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant();
        var hash       = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..16];
        return $@"Local\{prefix}-{hash}";
    }

    /// <summary>
    ///     尽量拿互斥量; 超时或打不开（如被提权进程创建、无权访问）时记日志并返回 null, 调用方按不加锁继续。从不抛异常。
    /// </summary>
    public static async Task<CrossProcessMutex?> TryAcquireAsync(string name, TimeSpan timeout)
    {
        try
        {
            return await AcquireAsync(name, timeout).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "拿不到互斥量 {Name}, 不加锁继续", name);
            return null;
        }
    }

    /// <summary>
    ///     等到拿到互斥量; 超时抛 <see cref="TimeoutException" />, 打不开时抛对应异常（异常都经返回的任务抛出, 不会在后台线程里抛出）。
    ///     释放 = Dispose 返回的对象。
    /// </summary>
    public static Task<CrossProcessMutex> AcquireAsync(string name, TimeSpan timeout)
    {
        var acquired = new TaskCompletionSource<CrossProcessMutex>(TaskCreationOptions.RunContinuationsAsynchronously);
        CrossProcessMutex? holder = null;

        var thread = new Thread
        (() =>
            {
                Mutex? mutex = null;
                var    owned = false;

                try
                {
                    mutex = new Mutex(false, name);

                    try
                    {
                        owned = mutex.WaitOne(timeout);
                    }
                    catch (AbandonedMutexException)
                    {
                        owned = true;
                    }

                    if (!owned)
                    {
                        acquired.TrySetException(new TimeoutException($"等待互斥量 {name} 超过 {timeout}"));
                        return;
                    }

                    acquired.TrySetResult(holder!);
                    holder!.release.Wait();
                }
                catch (Exception ex)
                {
                    acquired.TrySetException(ex);
                }
                finally
                {
                    try
                    {
                        if (owned)
                            mutex?.ReleaseMutex();
                    }
                    catch (Exception ex)
                    {
                        Log.Debug(ex, "释放互斥量 {Name} 失败", name);
                    }

                    mutex?.Dispose();
                }
            }
        )
        {
            IsBackground = true,
            Name         = "CrossProcessMutex"
        };

        holder = new CrossProcessMutex(thread);
        thread.Start();
        return acquired.Task;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (release.IsSet)
            return;

        release.Set();
        owner.Join(TimeSpan.FromSeconds(5));
    }
}
