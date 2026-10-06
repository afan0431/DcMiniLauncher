using System.IO;
using XIVLauncher.Minion;
using Xunit;

namespace XIVLauncher.Test.CatHost;

/// <summary>
///     钉住 MinionLauncher 的命令行: 国服行与改动前逐字相同; 国际服行只在注入文件名和 -path 上不同。
///     卡密、密码都是假值。
/// </summary>
public sealed class MinionAttacherArgumentsTests
{
    private const string MINION_ROOT = @"D:\MINI";
    private const string BOT_PATH    = @"D:\MINI\Bots\FFXIVMinion64";
    private const string KEYCODE     = "FAKEKEY0123456789FAKEKEY0123456789FAKEKEY01234";
    private const string MINION_ID   = "fake-forum-user";
    private const string MINION_PASS = "fake-forum-pass";

    private static MinionAccount Row(string? pathToExe, int? productId = 8, int? datacenter = 0) =>
        new() { Uid = "11112222-3333-4444-5555-666677778888", Keycode = KEYCODE, Group = "3", PathToExe = pathToExe, ProductId = productId, Datacenter = datacenter };

    [Fact]
    public void CnRow_FullArguments_AreUnchanged()
    {
        var datPath = Path.Combine(BOT_PATH, "MinionFiles", MinionAttacher.DatNameOf(MinionCards.VARIANT_CN));
        const string GAME_EXE = @"G:\最终幻想XIV\sdo\sdologin\Launcher.exe";

        var arguments = MinionAttacher.BuildArguments(Row(GAME_EXE), BOT_PATH, datPath, GAME_EXE, 4321, MINION_ID, MINION_PASS);

        Assert.Equal
        (
            [
                "-region=2",
                "-attachtype=0",
                "-productid=8",
                "-uid=11112222-3333-4444-5555-666677778888",
                "-minionid=fake-forum-user",
                $"-minionkey={KEYCODE}",
                "-minionpass=fake-forum-pass",
                "-attach=true",
                "-attachtopid=4321",
                @"-path=G:\最终幻想XIV\sdo\sdologin\Launcher.exe",
                @"-datpath=D:\MINI\Bots\FFXIVMinion64\MinionFiles\FFXIVMinionCN_64.dat",
                @"-botpath=D:\MINI\Bots\FFXIVMinion64",
                "-usebeta=0",
                "-datacenter=0",
                "-streamermode=0",
                "-setwindowtitle=0"
            ],
            arguments
        );
    }

    /// <summary>与 PC3 MinionLauncherInfo.txt（2026-10-06 取证）里国际服挂载成功时的参数一致: region 仍是 2, 注入文件 FFXIVMinion_64.dat, path 是 game\ffxiv_dx11.exe</summary>
    [Fact]
    public void GlobalRow_FullArguments_DifferOnlyInDatAndPath()
    {
        var datPath = Path.Combine(BOT_PATH, "MinionFiles", MinionAttacher.DatNameOf(MinionCards.VARIANT_GLOBAL));
        const string GAME_EXE = @"D:\FINAL FANTASY XIV - A Realm Reborn\game\ffxiv_dx11.exe";

        var arguments = MinionAttacher.BuildArguments(Row(GAME_EXE), BOT_PATH, datPath, GAME_EXE, 4321, MINION_ID, MINION_PASS);

        Assert.Equal
        (
            [
                "-region=2",
                "-attachtype=0",
                "-productid=8",
                "-uid=11112222-3333-4444-5555-666677778888",
                "-minionid=fake-forum-user",
                $"-minionkey={KEYCODE}",
                "-minionpass=fake-forum-pass",
                "-attach=true",
                "-attachtopid=4321",
                @"-path=D:\FINAL FANTASY XIV - A Realm Reborn\game\ffxiv_dx11.exe",
                @"-datpath=D:\MINI\Bots\FFXIVMinion64\MinionFiles\FFXIVMinion_64.dat",
                @"-botpath=D:\MINI\Bots\FFXIVMinion64",
                "-usebeta=0",
                "-datacenter=0",
                "-streamermode=0",
                "-setwindowtitle=0"
            ],
            arguments
        );
    }

    [Fact]
    public void RowValues_ProductIdDatacenterAndFlags_AreTakenFromRow()
    {
        var row = Row(null, null, 3) with { StreamerMode = true, SetWindowTitle = true };

        var arguments = MinionAttacher.BuildArguments(row, BOT_PATH, "dat", "exe", 1, MINION_ID, MINION_PASS)!;

        Assert.Contains("-productid=8", arguments); // 行里没有时兜底 8
        Assert.Contains("-datacenter=3", arguments);
        Assert.Contains("-streamermode=1", arguments);
        Assert.Contains("-setwindowtitle=1", arguments);
        Assert.Contains("-productid=12", MinionAttacher.BuildArguments(Row(null, 12), BOT_PATH, "dat", "exe", 1, MINION_ID, MINION_PASS)!);
    }

    [Theory]
    [InlineData(null, MINION_PASS)]
    [InlineData(MINION_ID, " ")]
    public void MissingMinionCredentials_ReturnsNull(string? minionId, string? minionPassword)
    {
        Assert.Null(MinionAttacher.BuildArguments(Row(null), BOT_PATH, "dat", "exe", 1, minionId, minionPassword));
    }

    [Fact]
    public void DatName_ByVariant()
    {
        Assert.Equal("FFXIVMinionCN_64.dat", MinionAttacher.DatNameOf(MinionCards.VARIANT_CN));
        Assert.Equal("FFXIVMinion_64.dat", MinionAttacher.DatNameOf(MinionCards.VARIANT_GLOBAL));
    }

    [Fact]
    public void GameExeCandidates_CnRow_OrderIsUnchanged()
    {
        var candidates = MinionAttacher.GameExeCandidates
        (
            Row(@"X:\old\Launcher.exe"),
            new DirectoryInfo(@"G:\最终幻想XIV"),
            @"G:\最终幻想XIV\game\ffxiv_dx11.exe",
            MinionCards.VARIANT_CN
        );

        Assert.Equal
        (
            [@"G:\最终幻想XIV\sdo\sdologin\Launcher.exe", @"X:\old\Launcher.exe", @"G:\最终幻想XIV\game\ffxiv_dx11.exe", @"G:\最终幻想XIV\game\ffxiv_dx11.exe"],
            candidates
        );
        Assert.Equal([null, null, null, null], MinionAttacher.GameExeCandidates(Row(null), null, null, MinionCards.VARIANT_CN));
    }

    [Fact]
    public void GameExeCandidates_GlobalRow_RowPathThenInternationalGameDirectory_NeverShengquLauncher()
    {
        var candidates = MinionAttacher.GameExeCandidates
        (
            Row(@"X:\old\game\ffxiv_dx11.exe"),
            new DirectoryInfo(@"D:\FINAL FANTASY XIV - A Realm Reborn"),
            @"D:\running\ffxiv_dx11.exe",
            MinionCards.VARIANT_GLOBAL
        );

        Assert.Equal
        (
            [@"X:\old\game\ffxiv_dx11.exe", @"D:\FINAL FANTASY XIV - A Realm Reborn\game\ffxiv_dx11.exe", @"D:\running\ffxiv_dx11.exe"],
            candidates
        );
        Assert.DoesNotContain(candidates, x => x != null && x.Contains("sdologin", StringComparison.OrdinalIgnoreCase));
    }
}
