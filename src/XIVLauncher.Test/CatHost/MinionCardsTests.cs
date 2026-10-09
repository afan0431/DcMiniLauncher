using System.IO;
using XIVLauncher.Minion;
using Xunit;

namespace XIVLauncher.Test.CatHost;

public sealed class MinionCardsTests
{
    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef", true)]
    [InlineData("0123456789ABCDEF0123456789ABCDEF", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0123456789abcdef0123456789abcde", false)]
    [InlineData("0123456789abcdef0123456789abcdef0", false)]
    [InlineData("0123456789abcdef0123456789abcdeg", false)]
    [InlineData("01234567-89ab-cdef-0123-456789abcdef", false)]
    public void IsValidUid_Is32Hex(string? uid, bool expected)
    {
        Assert.Equal(expected, MinionCards.IsValidUid(uid));
    }

    [Theory]
    [InlineData("0123456789abcdef", true)]
    [InlineData("0123456789ABCDEF", false)]
    [InlineData("0123456789abcde", false)]
    [InlineData(null, false)]
    public void IsValidFingerprint_Is16LowerHex(string? fingerprint, bool expected)
    {
        Assert.Equal(expected, MinionCards.IsValidFingerprint(fingerprint));
    }

    [Fact]
    public void InstallPath_ConfiguredValueWins()
    {
        using var drives = new FakeDrives();
        drives.AddMinion("D");

        Assert.Equal(@"E:\Bots\MINI", MinionInstall.Resolve(@"  E:\Bots\MINI ", drives.Roots));
    }

    [Fact]
    public void InstallPath_NotConfigured_FirstDriveWithLauncherExe()
    {
        using var drives = new FakeDrives();
        drives.AddEmptyMiniDirectory("C");
        drives.AddMinion("D");
        drives.AddMinion("E");

        Assert.Equal(Path.Combine(drives.Root("D"), "MINI"), MinionInstall.Resolve(null, drives.Roots));
        Assert.Equal(Path.Combine(drives.Root("D"), "MINI"), MinionInstall.Resolve(" ", drives.Roots));
    }

    [Fact]
    public void InstallPath_NotFoundAnywhere_IsCMini()
    {
        using var drives = new FakeDrives();
        drives.AddEmptyMiniDirectory("C");

        Assert.Equal(@"C:\MINI", MinionInstall.Resolve(null, drives.Roots));
        Assert.Equal(@"C:\MINI", MinionInstall.Resolve(null, []));
    }

    /// <summary>
    ///     用临时目录当作各个硬盘的根目录
    /// </summary>
    private sealed class FakeDrives : IDisposable
    {
        private readonly DirectoryInfo root = Directory.CreateTempSubdirectory("cat-minion-drives-");

        private readonly List<string> roots = [];

        public IReadOnlyList<string> Roots => roots;

        public string Root(string letter) => Path.Combine(root.FullName, letter);

        public void AddEmptyMiniDirectory(string letter)
        {
            Directory.CreateDirectory(Path.Combine(Root(letter), "MINI"));
            roots.Add(Root(letter));
        }

        public void AddMinion(string letter)
        {
            var directory = Path.Combine(Root(letter), "MINI");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, MinionInstall.LAUNCHER_EXE_NAME), "exe");
            roots.Add(Root(letter));
        }

        public void Dispose()
        {
            try
            {
                root.Delete(true);
            }
            catch
            {
                // ignored
            }
        }
    }
}
