using System.IO;

namespace XIVLauncher.Minion;

/// <summary>
///     Minion 安装目录与 MinionLauncher_64.exe 的位置
/// </summary>
public static class MinionInstall
{
    /// <summary>
    ///     设置里没填、各个硬盘上也没找到时用的安装目录。
    ///     MinionLauncher 靠相对路径找 MinionLauncher_64.dat, 必须以安装目录为工作目录拉起
    ///     （见 research/probe-P1-attach-standalone.md）
    /// </summary>
    public const string DEFAULT_INSTALL_PATH = @"C:\MINI";

    public const string LAUNCHER_EXE_NAME = "MinionLauncher_64.exe";

    /// <summary>自动查找时在每个硬盘根目录下找的目录名</summary>
    private const string AUTO_DIRECTORY_NAME = "MINI";

    /// <summary>
    ///     本机的安装目录: 设置里填了就用设置的; 没填时按盘符顺序找第一个含 <see cref="LAUNCHER_EXE_NAME" /> 的 <c>X:\MINI</c>;
    ///     都没有时为 <see cref="DEFAULT_INSTALL_PATH" />
    /// </summary>
    public static string InstallPath => Resolve(App.Settings?.MinionInstallPath, FixedDriveRoots());

    /// <summary>
    ///     按 <see cref="InstallPath" /> 的规则定安装目录
    /// </summary>
    /// <param name="configured">设置里填的安装目录, 空表示自动查找</param>
    /// <param name="driveRoots">按顺序查找的硬盘根目录（如 <c>C:\</c>）</param>
    public static string Resolve(string? configured, IEnumerable<string> driveRoots)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            return configured.Trim();

        return FindUnderDrives(driveRoots) ?? DEFAULT_INSTALL_PATH;
    }

    /// <summary>
    ///     在各个硬盘根目录下按顺序找第一个含 <see cref="LAUNCHER_EXE_NAME" /> 的 <c>MINI</c> 目录, 找不到返回 null
    /// </summary>
    public static string? FindUnderDrives(IEnumerable<string> driveRoots) =>
        driveRoots.Select(root => Path.Combine(root, AUTO_DIRECTORY_NAME))
                  .FirstOrDefault(directory => IsLauncherPresent(directory));

    public static string GetLauncherExePath(string? installPath = null) =>
        Path.Combine(installPath ?? InstallPath, LAUNCHER_EXE_NAME);

    public static bool IsLauncherPresent(string? installPath = null) =>
        File.Exists(GetLauncherExePath(installPath));

    /// <summary>
    ///     本机就绪的固定硬盘根目录, 按盘符排序
    /// </summary>
    private static IReadOnlyList<string> FixedDriveRoots()
    {
        try
        {
            return DriveInfo.GetDrives()
                            .Where(drive => drive.DriveType == DriveType.Fixed && drive.IsReady)
                            .Select(drive => drive.RootDirectory.FullName)
                            .OrderBy(root => root, StringComparer.OrdinalIgnoreCase)
                            .ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }
}
