using System.IO;
using System.Security.Cryptography;
using System.Text;
using XIVLauncher.Minion;
using Xunit;

namespace XIVLauncher.Test.CatHost;

public sealed class MinionCardsTests
{
    private const string KEY_A = "ABCDEF0123456789ABCDEF0123456789ABCDEF01234567";
    private const string KEY_B = "ZZZZZZ9876543210ZZZZZZ9876543210ZZZZZZ98765432";

    private const string CN_ROOT = @"G:\最终幻想XIV";

    private static readonly string?[] CnRoots = [CN_ROOT];

    private static readonly MinionAccount[] Rows =
    [
        new() { Uid = "uid-a-global", Keycode = KEY_A, Group = "3", PathToExe = @"D:\SquareEnix\FINAL FANTASY XIV - A Realm Reborn\boot\ffxivboot.exe" },
        new() { Uid = "uid-a-cn", Keycode     = KEY_A, Group = "3", PathToExe = @"G:\最终幻想XIV\sdo\sdologin\Launcher.exe" },
        new() { Uid = "uid-b-cn", Keycode     = KEY_B, Group = "4", PathToExe = @"G:\最终幻想XIV\sdo\sdologin\Launcher.exe" }
    ];

    [Fact]
    public void Fingerprint_IsFirst16LowerHexOfPrefixedSha256()
    {
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("cat-minion-card:" + KEY_A))).ToLowerInvariant()[..16];

        Assert.Equal(expected, MinionCards.Fingerprint(KEY_A));
        Assert.True(MinionCards.IsValidFingerprint(MinionCards.Fingerprint(KEY_A)));
    }

    [Theory]
    [InlineData(@"G:\最终幻想XIV\sdo\sdologin\Launcher.exe", CN_ROOT)]
    [InlineData(@"g:\最终幻想xiv\SDO\sdologin\launcher.EXE", CN_ROOT)]
    [InlineData(@"G:\最终幻想XIV\sdo\sdologin\Launcher.exe", @"G:\最终幻想XIV\")]
    [InlineData(@"G:\最终幻想XIV\sdo\sdologin\Launcher.exe", @"g:/最终幻想xiv/")]
    [InlineData(@"G:/最终幻想XIV/game/ffxiv_dx11.exe", CN_ROOT)]
    [InlineData(@"G:\最终幻想XIV\sdo\..\game\ffxiv_dx11.exe", CN_ROOT)]
    [InlineData(@"G:\Game\Launcher.exe", @"G:\")]
    public void VariantOf_ExeUnderCnRoot_IsCn(string pathToExe, string cnRoot)
    {
        var row = new MinionAccount { PathToExe = pathToExe };

        Assert.Equal(MinionCards.VARIANT_CN, MinionCards.VariantOf(row, [cnRoot]));
    }

    [Theory]
    [InlineData(@"D:\SquareEnix\FINAL FANTASY XIV - A Realm Reborn\boot\ffxivboot.exe")]
    [InlineData(@"G:\最终幻想XIV-国际服\boot\ffxivboot.exe")]
    [InlineData(@"G:\最终幻想XIV\..\Other\Launcher.exe")]
    public void VariantOf_ExeOutsideCnRoot_IsGlobal(string pathToExe)
    {
        var row = new MinionAccount { PathToExe = pathToExe };

        Assert.Equal(MinionCards.VARIANT_GLOBAL, MinionCards.VariantOf(row, CnRoots));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void VariantOf_EmptyExePath_IsCn(string? pathToExe)
    {
        var row = new MinionAccount { PathToExe = pathToExe };

        Assert.Equal(MinionCards.VARIANT_CN, MinionCards.VariantOf(row, CnRoots));
        Assert.Equal(MinionCards.VARIANT_CN, MinionCards.VariantOf(row, []));
    }

    [Fact]
    public void VariantOf_AnyConfiguredCnRootMatches_IsCnAndNullRootsIgnored()
    {
        var weGameRow = new MinionAccount { PathToExe = @"E:\WeGameApps\最终幻想XIV\sdo\sdologin\Launcher.exe" };

        Assert.Equal(MinionCards.VARIANT_CN, MinionCards.VariantOf(weGameRow, [null, CN_ROOT, @"E:\WeGameApps\最终幻想XIV"]));
        Assert.Equal(MinionCards.VARIANT_GLOBAL, MinionCards.VariantOf(weGameRow, [null, CN_ROOT]));
        Assert.Equal(MinionCards.VARIANT_GLOBAL, MinionCards.VariantOf(weGameRow, []));
    }

    [Theory]
    [InlineData("cn", "uid-a-cn")]
    [InlineData("global", "uid-a-global")]
    public void FindByCard_SameKeycodeTwoRows_PicksRowByVariant(string variant, string expectedUid)
    {
        var row = MinionCards.FindByCard(Rows, MinionCards.Fingerprint(KEY_A), variant, CnRoots);

        Assert.Equal(expectedUid, row?.Uid);
    }

    [Fact]
    public void FindByCard_MissingVariant_ReturnsNullInsteadOfOtherRow()
    {
        // 卡 B 只有国服行, 要国际服行时不能退回国服行或分组第一行
        Assert.Null(MinionCards.FindByCard(Rows, MinionCards.Fingerprint(KEY_B), MinionCards.VARIANT_GLOBAL, CnRoots));
    }

    [Fact]
    public void FindByCard_UnknownCard_ReturnsNull()
    {
        Assert.Null(MinionCards.FindByCard(Rows, MinionCards.Fingerprint("not-a-real-keycode"), MinionCards.VARIANT_CN, CnRoots));
        Assert.Null(MinionCards.FindByCard(Rows, "not-a-fingerprint", MinionCards.VARIANT_CN, CnRoots));
        Assert.Null(MinionCards.FindByCard(Rows, MinionCards.Fingerprint(KEY_A), "beta", CnRoots));
    }

    [Fact]
    public void FindAccount_UidNotInGroup_ReturnsNullInsteadOfFirstAccount()
    {
        Assert.Null(MinionAccounts.FindAccount(Rows, "3", "uid-that-was-deleted"));
        Assert.Null(MinionAccounts.FindAccount(Rows, "3", null));
        Assert.Null(MinionAccounts.FindAccount(Rows, "4", "uid-a-cn"));
        Assert.Equal("uid-a-global", MinionAccounts.FindAccount(Rows, "3", "UID-A-GLOBAL")?.Uid);
    }

    [Fact]
    public void LoadAccounts_ReadsPathToExeAndToleratesUseBetaField()
    {
        var installPath = Directory.CreateTempSubdirectory("cat-minion-accounts-").FullName;

        try
        {
            Directory.CreateDirectory(Path.Combine(installPath, "Settings"));
            File.WriteAllText
            (
                MinionAccounts.GetAccountsJsonPath(installPath),
                """
                [
                  { "UID": "uid-1", "Keycode": "K1", "Group": "3", "PathToExe": "G:\\最终幻想XIV\\sdo\\sdologin\\Launcher.exe", "UseBetaFFXIVFiles": true },
                  { "UID": "uid-2", "Keycode": "K1", "Group": "3", "PathToExe": "", "UseBetaFFXIVFiles": false }
                ]
                """,
                new UTF8Encoding(true)
            );

            var accounts = MinionAccounts.LoadAccounts(installPath);

            Assert.Equal(2, accounts.Count);
            Assert.Equal(@"G:\最终幻想XIV\sdo\sdologin\Launcher.exe", accounts[0].PathToExe);
            Assert.Equal(string.Empty, accounts[1].PathToExe);
        }
        finally
        {
            Directory.Delete(installPath, true);
        }
    }
}
