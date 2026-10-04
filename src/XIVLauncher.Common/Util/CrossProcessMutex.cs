using System.Security.Cryptography;
using System.Text;

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
    ///     等到拿到互斥量; 超时抛 <see cref="TimeoutException" />。释放 = Dispose 返回的对象。
    /// </summary>
    public static Task<CrossProcessMutex> AcquireAsync(string name, TimeSpan timeout)
    {
        var acquired = new TaskCompletionSource<CrossProcessMutex>(TaskCreationOptions.RunContinuationsAsynchronously);
        CrossProcessMutex? holder = null;

        var thread = new Thread
        (() =>
            {
                using var mutex = new Mutex(false, name);
                bool owned;

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

                try
                {
                    acquired.TrySetResult(holder!);
                    holder!.release.Wait();
                }
                finally
                {
                    mutex.ReleaseMutex();
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
