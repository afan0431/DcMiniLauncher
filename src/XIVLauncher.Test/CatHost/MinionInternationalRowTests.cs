using XIVLauncher.Minion;
using Xunit;

namespace XIVLauncher.Test.CatHost;

/// <summary>
///     给国际服的游戏选 Minion 行: 只认执行程序位于国际服游戏目录之下的行, 韩服 / 繁中服 / 别处的行不算
/// </summary>
public sealed class MinionInternationalRowTests
{
    private const string KEY   = "ABCDEF0123456789ABCDEF0123456789ABCDEF01234567";
    private const string OTHER = "ZZZZZZ9876543210ZZZZZZ9876543210ZZZZZZ98765432";

    private const string CN_ROOT   = @"G:\最终幻想XIV";
    private const string INTL_ROOT = @"D:\FINAL FANTASY XIV - A Realm Reborn";

    private static readonly string?[] CnRoots = [CN_ROOT, null];

    private static readonly string Fingerprint = MinionCards.Fingerprint(KEY);

    private static readonly MinionAccount[] Rows =
    [
        new() { Uid = "kr000000aaaaaaaaaaaaaaaaaaaaaaaa", Keycode = KEY, PathToExe   = @"E:\FFXIV_KR\game\ffxiv_dx11.exe" },
        new() { Uid = "cn000000bbbbbbbbbbbbbbbbbbbbbbbb", Keycode = KEY, PathToExe   = @"G:\最终幻想XIV\sdo\sdologin\Launcher.exe" },
        new() { Uid = "intl0001cccccccccccccccccccccccc", Keycode = KEY, PathToExe   = @"D:\FINAL FANTASY XIV - A Realm Reborn\game\ffxiv_dx11.exe" },
        new() { Uid = "intl0002dddddddddddddddddddddddd", Keycode = KEY, PathToExe   = @"d:\final fantasy xiv - a realm reborn\boot\ffxivboot.exe" },
        new() { Uid = "blank000eeeeeeeeeeeeeeeeeeeeeeee", Keycode = KEY, PathToExe   = null },
        new() { Uid = "other000ffffffffffffffffffffffff", Keycode = OTHER, PathToExe = @"D:\FINAL FANTASY XIV - A Realm Reborn\game\ffxiv_dx11.exe" }
    ];

    [Fact]
    public void PicksRowUnderInternationalGameDirectory_NotTheKoreanOne()
    {
        var selection = MinionCards.SelectInternationalRow(Rows, Fingerprint, INTL_ROOT, CnRoots, new HashSet<string>());

        Assert.Equal("intl0001cccccccccccccccccccccccc", selection.Row!.Uid);
        Assert.False(selection.AllOccupied);
        Assert.Contains(selection.Notes, x => x.Contains("国际服游戏目录: " + INTL_ROOT));
        Assert.Contains(selection.Notes, x => x.Contains("kr000000") && x.Contains("不算国际服行"));
        Assert.DoesNotContain(selection.Notes, x => x.Contains(KEY));
    }

    /// <summary>对照: 原来的「不在国服目录下就算国际服行」会先选到韩服那一行</summary>
    [Fact]
    public void OldRule_WouldHavePickedTheKoreanRow()
    {
        Assert.Equal("kr000000aaaaaaaaaaaaaaaaaaaaaaaa", MinionCards.SelectRow(Rows, Fingerprint, MinionCards.VARIANT_GLOBAL, CnRoots, new HashSet<string>()).Row!.Uid);
    }

    [Fact]
    public void SkipsOccupiedRow_ThenFallsBackToFirstWhenAllOccupied()
    {
        var one = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "INTL0001cccccccccccccccccccccccc" };
        Assert.Equal("intl0002dddddddddddddddddddddddd", MinionCards.SelectInternationalRow(Rows, Fingerprint, INTL_ROOT, CnRoots, one).Row!.Uid);

        var both = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "intl0001cccccccccccccccccccccccc", "intl0002dddddddddddddddddddddddd" };
        var selection = MinionCards.SelectInternationalRow(Rows, Fingerprint, INTL_ROOT, CnRoots, both);

        Assert.Equal("intl0001cccccccccccccccccccccccc", selection.Row!.Uid);
        Assert.True(selection.AllOccupied);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"F:\SomewhereElse")]
    [InlineData(@"D:\FINAL FANTASY XIV")] // 只是名字前缀相同, 不是上级目录
    public void NoRowUnderTheConfiguredDirectory_SelectsNothing(string? internationalRoot)
    {
        var selection = MinionCards.SelectInternationalRow(Rows, Fingerprint, internationalRoot, CnRoots, new HashSet<string>());

        Assert.Null(selection.Row);
        Assert.False(selection.AllOccupied);
    }

    [Fact]
    public void TrailingSeparatorAndSlashStyle_DoNotMatter()
    {
        Assert.NotNull(MinionCards.SelectInternationalRow(Rows, Fingerprint, @"D:/FINAL FANTASY XIV - A Realm Reborn/", CnRoots, new HashSet<string>()).Row);
    }

    [Fact]
    public void OtherCardsRows_AreNotSelected()
    {
        var selection = MinionCards.SelectInternationalRow([Rows[5]], Fingerprint, INTL_ROOT, CnRoots, new HashSet<string>());

        Assert.Null(selection.Row);
    }

    /// <summary>国服行的判定没有变: 同一批行按国服规则选, 结果与原来一样</summary>
    [Fact]
    public void DomesticSelection_IsUnchanged()
    {
        var selection = MinionCards.SelectRow(Rows, Fingerprint, MinionCards.VARIANT_CN, CnRoots, new HashSet<string>());

        Assert.Equal("cn000000bbbbbbbbbbbbbbbbbbbbbbbb", selection.Row!.Uid);
        Assert.Equal(MinionCards.VARIANT_GLOBAL, MinionCards.VariantOf(Rows[0], CnRoots));
        Assert.Equal(MinionCards.VARIANT_CN, MinionCards.VariantOf(Rows[4], CnRoots));
    }
}
