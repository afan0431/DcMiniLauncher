using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using Serilog;

namespace XIVLauncher.CatHost;

/// <summary>
///     Cat 上号自动开工的截图目录 <c>%ProgramData%\DcMiniLauncher\cat-shots\</c>: 游戏内 Afan 往里写 <c>&lt;pid&gt;-&lt;seq&gt;.png</c>,
///     工作台读了上传（只读不删）。无界面进程启动时建好目录、保证普通用户可读, 并删掉 3 天前的文件。
/// </summary>
public static class CatShots
{
    /// <summary>保留多久</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(3);

    /// <summary>
    ///     目录, 测试时可替换
    /// </summary>
    public static string Directory { get; set; } = Path.Combine
    (
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "DcMiniLauncher",
        "cat-shots"
    );

    /// <summary>
    ///     建目录、补上普通用户（BUILTIN\Users）的读取权限、删掉最后写入早于 <see cref="MaxAge" /> 的文件（只看这一层, 删不掉的跳过）。
    ///     失败只记日志
    /// </summary>
    /// <param name="nowUtc">现在（测试用）</param>
    public static void Prepare(DateTime? nowUtc = null)
    {
        DirectoryInfo directory;

        try
        {
            directory = System.IO.Directory.CreateDirectory(Directory);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[CatHost] 建截图目录 {Directory} 失败", Directory);
            return;
        }

        EnsureUsersCanRead(directory);

        var cutoff  = (nowUtc ?? DateTime.UtcNow) - MaxAge;
        var removed = 0;

        FileInfo[] files;

        try
        {
            files = directory.GetFiles();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[CatHost] 枚举截图目录失败");
            return;
        }

        foreach (var file in files)
        {
            try
            {
                if (file.LastWriteTimeUtc >= cutoff)
                    continue;

                file.Delete();
                removed++;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[CatHost] 删旧截图 {File} 失败", file.Name);
            }
        }

        if (removed > 0)
            Log.Information("[CatHost] 删掉 {Count} 张 {Days} 天前的截图", removed, MaxAge.TotalDays);
    }

    /// <summary>
    ///     目录上已有对 BUILTIN\Users 的「读取和执行」（目录与文件都继承）就不动; 没有就加一条
    /// </summary>
    private static void EnsureUsersCanRead(DirectoryInfo directory)
    {
        try
        {
            var users    = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
            var security = directory.GetAccessControl();
            const InheritanceFlags inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (rule.IdentityReference == users &&
                    rule.AccessControlType == AccessControlType.Allow &&
                    (rule.FileSystemRights & FileSystemRights.ReadAndExecute) == FileSystemRights.ReadAndExecute &&
                    (rule.InheritanceFlags & inheritance) == inheritance &&
                    rule.PropagationFlags == PropagationFlags.None)
                    return;
            }

            security.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.ReadAndExecute, inheritance, PropagationFlags.None, AccessControlType.Allow));
            directory.SetAccessControl(security);
            Log.Information("[CatHost] 截图目录已加上普通用户可读");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[CatHost] 给截图目录加普通用户读取权限失败");
        }
    }
}
