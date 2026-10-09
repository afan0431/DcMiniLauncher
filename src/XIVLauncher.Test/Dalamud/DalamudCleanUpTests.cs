using System.IO;
using XIVLauncher.Dalamud;
using Xunit;

namespace XIVLauncher.Test.Dalamud;

/// <summary>
///     更新 Dalamud 后清理旧版本: 还有游戏在用的版本（文件被占着）整目录保留, 不能删掉一半
/// </summary>
public sealed class DalamudCleanUpTests : IDisposable
{
    private readonly DirectoryInfo addon = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"dml-addon-{Guid.NewGuid():N}"));

    public void Dispose()
    {
        try
        {
            addon.Delete(true);
        }
        catch
        {
            // 临时目录, 删不掉不影响结果
        }
    }

    private DirectoryInfo MakeVersion(string name)
    {
        var dir = addon.CreateSubdirectory(name);
        File.WriteAllText(Path.Combine(dir.FullName, "Dalamud.json"), "{}");
        File.WriteAllText(Path.Combine(dir.FullName, "Dalamud.dll"), "dll");
        dir.CreateSubdirectory("runtimes");
        File.WriteAllText(Path.Combine(dir.FullName, "runtimes", "native.dll"), "native");
        return dir;
    }

    [Fact]
    public void InUseVersionIsKeptWhole_UnusedVersionIsDeleted()
    {
        var inUse   = MakeVersion("26-10-01-01");
        var unused  = MakeVersion("26-10-05-01");
        var current = MakeVersion("26-10-08-02");

        // 模拟游戏映射着旧版的 DLL: 别人打不开独占读写
        using (new FileStream(Path.Combine(inUse.FullName, "Dalamud.dll"), FileMode.Open, FileAccess.Read, FileShare.Read))
            DalamudUpdater.CleanUpOld(addon, current.Name);

        Assert.True(File.Exists(Path.Combine(inUse.FullName, "Dalamud.json")), "在用版本里没被锁的文件也必须留着");
        Assert.True(File.Exists(Path.Combine(inUse.FullName, "runtimes", "native.dll")));
        Assert.False(Directory.Exists(unused.FullName));
        Assert.True(Directory.Exists(current.FullName));
    }

    [Fact]
    public void DevDirectoryIsNeverTouched()
    {
        var dev = MakeVersion("dev");

        DalamudUpdater.CleanUpOld(addon, "26-10-08-02");

        Assert.True(Directory.Exists(dev.FullName));
    }

    [Fact]
    public void FirstFileInUse_NullWhenNothingIsOpen()
    {
        var dir = MakeVersion("26-10-01-01");

        Assert.Null(DalamudUpdater.FirstFileInUse(dir));
    }
}
