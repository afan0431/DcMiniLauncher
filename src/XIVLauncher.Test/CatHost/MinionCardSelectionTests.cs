using XIVLauncher.Minion;
using Xunit;

namespace XIVLauncher.Test.CatHost;

public sealed class MinionCardSelectionTests
{
    private const string KEY = "ABCDEF0123456789ABCDEF0123456789ABCDEF01234567";

    private const string CN_ROOT = @"G:\最终幻想XIV";

    private static readonly string?[] CnRoots = [CN_ROOT];

    private static readonly MinionAccount[] Rows =
    [
        new() { Uid = "11111111aaaaaaaaaaaaaaaaaaaaaaaa", Keycode = KEY, PathToExe = @"D:\SquareEnix\boot\ffxivboot.exe" },
        new() { Uid = "22222222bbbbbbbbbbbbbbbbbbbbbbbb", Keycode = KEY, PathToExe = @"G:\最终幻想XIV\sdo\sdologin\Launcher.exe" },
        new() { Uid = "33333333cccccccccccccccccccccccc", Keycode = KEY, PathToExe = null }
    ];

    private static readonly string Fingerprint = MinionCards.Fingerprint(KEY);

    [Fact]
    public void SelectRow_PicksFirstFreeRowOfVariant()
    {
        var selection = MinionCards.SelectRow(Rows, Fingerprint, MinionCards.VARIANT_CN, CnRoots, new HashSet<string>());

        Assert.Equal("22222222bbbbbbbbbbbbbbbbbbbbbbbb", selection.Row!.Uid);
        Assert.False(selection.AllOccupied);
    }

    [Fact]
    public void SelectRow_SkipsOccupiedRow_CaseInsensitive()
    {
        var occupied  = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "22222222BBBBBBBBBBBBBBBBBBBBBBBB" };
        var selection = MinionCards.SelectRow(Rows, Fingerprint, MinionCards.VARIANT_CN, CnRoots, occupied);

        Assert.Equal("33333333cccccccccccccccccccccccc", selection.Row!.Uid);
        Assert.Contains(selection.Notes, note => note.Contains("22222222") && note.Contains("已挂在别的游戏上"));
    }

    [Fact]
    public void SelectRow_AllOccupied_StillPicksFirst_AndSaysSo()
    {
        var occupied = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "22222222bbbbbbbbbbbbbbbbbbbbbbbb",
            "33333333cccccccccccccccccccccccc"
        };

        var selection = MinionCards.SelectRow(Rows, Fingerprint, MinionCards.VARIANT_CN, CnRoots, occupied);

        Assert.Equal("22222222bbbbbbbbbbbbbbbbbbbbbbbb", selection.Row!.Uid);
        Assert.True(selection.AllOccupied);
    }

    [Fact]
    public void SelectRow_NotesExplainVariant_WithoutKeycode()
    {
        var selection = MinionCards.SelectRow(Rows, Fingerprint, MinionCards.VARIANT_GLOBAL, CnRoots, new HashSet<string>());

        Assert.Equal("11111111aaaaaaaaaaaaaaaaaaaaaaaa", selection.Row!.Uid);
        Assert.Contains(selection.Notes, note => note.Contains(CN_ROOT));
        Assert.Contains(selection.Notes, note => note.Contains("(空, 按国服)"));
        Assert.DoesNotContain(selection.Notes, note => note.Contains(KEY));
    }

    [Fact]
    public void SelectRow_UnknownCard_ReturnsNull()
    {
        var selection = MinionCards.SelectRow(Rows, "0123456789abcdef", MinionCards.VARIANT_CN, CnRoots, new HashSet<string>());

        Assert.Null(selection.Row);
    }
}
