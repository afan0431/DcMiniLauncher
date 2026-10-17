using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using XIVLauncher.CatHost;
using Xunit;

namespace XIVLauncher.Test.CatHost;

/// <summary>
///     截图目录: 启动时建好、普通用户可读、删掉 3 天前的文件, 新的留着
/// </summary>
[Collection(nameof(CatShotsTests))]
public sealed class CatShotsTests : IDisposable
{
    private readonly string originalDirectory = CatShots.Directory;
    private readonly string tempDirectory     = Path.Combine(Path.GetTempPath(), "dml-cat-shots-" + Guid.NewGuid().ToString("N"), "cat-shots");

    public CatShotsTests() => CatShots.Directory = tempDirectory;

    public void Dispose()
    {
        CatShots.Directory = originalDirectory;

        var parent = Path.GetDirectoryName(tempDirectory)!;

        if (Directory.Exists(parent))
            Directory.Delete(parent, true);
    }

    [Fact]
    public void Prepare_CreatesTheDirectory_ReadableByUsers()
    {
        CatShots.Prepare();

        Assert.True(Directory.Exists(tempDirectory));

        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var rules = new DirectoryInfo(tempDirectory).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>();

        Assert.Contains
        (
            rules,
            x => x.IdentityReference == users &&
                 x.AccessControlType == AccessControlType.Allow &&
                 (x.FileSystemRights & FileSystemRights.ReadAndExecute) == FileSystemRights.ReadAndExecute &&
                 x.InheritanceFlags.HasFlag(InheritanceFlags.ObjectInherit)
        );
    }

    [Fact]
    public void Prepare_DeletesFilesOlderThanThreeDays_KeepsTheRest()
    {
        Directory.CreateDirectory(tempDirectory);

        var now   = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
        var old   = Path.Combine(tempDirectory, "1234-1.png");
        var fresh = Path.Combine(tempDirectory, "1234-2.png");
        var edge  = Path.Combine(tempDirectory, "1234-3.png");

        foreach (var file in new[] { old, fresh, edge })
            File.WriteAllBytes(file, [1, 2, 3]);

        File.SetLastWriteTimeUtc(old, now - TimeSpan.FromDays(3) - TimeSpan.FromMinutes(1));
        File.SetLastWriteTimeUtc(edge, now - TimeSpan.FromDays(3) + TimeSpan.FromMinutes(1));
        File.SetLastWriteTimeUtc(fresh, now - TimeSpan.FromHours(1));

        CatShots.Prepare(now);

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(fresh));
        Assert.True(File.Exists(edge));
    }

    [Fact]
    public void Prepare_FileInUse_IsSkipped()
    {
        Directory.CreateDirectory(tempDirectory);

        var now  = DateTime.UtcNow;
        var busy = Path.Combine(tempDirectory, "1234-9.png");
        File.WriteAllBytes(busy, [1]);
        File.SetLastWriteTimeUtc(busy, now - TimeSpan.FromDays(10));

        using (new FileStream(busy, FileMode.Open, FileAccess.Read, FileShare.None))
            CatShots.Prepare(now);

        Assert.True(File.Exists(busy));
    }
}
