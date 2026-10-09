using System.Collections.Concurrent;
using System.IO;
using XIVLauncher.CatHost;
using XIVLauncher.Minion;
using XIVLauncher.Test.International;
using Xunit;

namespace XIVLauncher.Test.CatHost;

/// <summary>
///     钉住 MinionLauncher 的命令行: 卡号、编号、论坛账号取自 launch, 其余是固定值; 国际服只在注入文件名和 -path 上不同。
///     卡号、密码都是假值。
/// </summary>
public sealed class MinionAttacherArgumentsTests
{
    private const string BOT_PATH       = @"D:\MINI\Bots\FFXIVMinion64";
    private const string KEYCODE        = "FFXIVXFAKE0123456789FAKE0123456789";
    private const string UID            = "11112222333344445555666677778888";
    private const string FORUM_ID       = "fake-forum-user";
    private const string FORUM_PASSWORD = "fake-forum-pass";

    private static CatMinionLaunch Card(string variant, string keycode = KEYCODE, string uid = UID, string forumId = FORUM_ID, string forumPassword = FORUM_PASSWORD) =>
        new("0123456789abcdef", variant, new CatSecret(keycode), uid, forumId, new CatSecret(forumPassword));

    [Fact]
    public void Cn_FullArguments_UseFixedOptions()
    {
        var datPath = Path.Combine(BOT_PATH, "MinionFiles", MinionAttacher.DatNameOf(MinionCards.VARIANT_CN));
        const string GAME_EXE = @"G:\最终幻想XIV\sdo\sdologin\Launcher.exe";

        var arguments = MinionAttacher.BuildArguments(Card(MinionCards.VARIANT_CN), BOT_PATH, datPath, GAME_EXE, 4321);

        Assert.Equal
        (
            [
                "-region=2",
                "-attachtype=0",
                "-productid=8",
                "-uid=11112222333344445555666677778888",
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
                "-streamermode=1",
                "-setwindowtitle=0"
            ],
            arguments
        );
    }

    /// <summary>与 PC3 MinionLauncherInfo.txt（2026-10-06 取证）里国际服挂载成功时的参数一致: region 仍是 2, 注入文件 FFXIVMinion_64.dat, path 是 game\ffxiv_dx11.exe</summary>
    [Fact]
    public void Global_FullArguments_DifferOnlyInDatAndPath()
    {
        var datPath = Path.Combine(BOT_PATH, "MinionFiles", MinionAttacher.DatNameOf(MinionCards.VARIANT_GLOBAL));
        const string GAME_EXE = @"D:\FINAL FANTASY XIV - A Realm Reborn\game\ffxiv_dx11.exe";

        var arguments = MinionAttacher.BuildArguments(Card(MinionCards.VARIANT_GLOBAL), BOT_PATH, datPath, GAME_EXE, 4321);

        Assert.Equal
        (
            [
                "-region=2",
                "-attachtype=0",
                "-productid=8",
                "-uid=11112222333344445555666677778888",
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
                "-streamermode=1",
                "-setwindowtitle=0"
            ],
            arguments
        );
    }

    [Theory]
    [InlineData("", UID, FORUM_ID, FORUM_PASSWORD)]
    [InlineData(KEYCODE, " ", FORUM_ID, FORUM_PASSWORD)]
    [InlineData(KEYCODE, UID, " ", FORUM_PASSWORD)]
    [InlineData(KEYCODE, UID, FORUM_ID, "")]
    public void MissingValues_ReturnNull(string keycode, string uid, string forumId, string forumPassword)
    {
        Assert.Null(MinionAttacher.BuildArguments(Card(MinionCards.VARIANT_CN, keycode, uid, forumId, forumPassword), BOT_PATH, "dat", "exe", 1));
    }

    [Fact]
    public void Redact_HidesKeycodeAndForumPassword()
    {
        var arguments = MinionAttacher.BuildArguments(Card(MinionCards.VARIANT_CN), BOT_PATH, "dat", "exe", 1)!;

        var text = MinionAttacher.Redact(@"D:\MINI\MinionLauncher_64.exe", arguments);

        Assert.DoesNotContain(KEYCODE, text);
        Assert.DoesNotContain(FORUM_PASSWORD, text);
        Assert.Contains("-minionkey=***", text);
        Assert.Contains("-minionpass=***", text);
        Assert.Contains($"-uid={UID}", text);
    }

    [Fact]
    public void DatName_ByVariant()
    {
        Assert.Equal("FFXIVMinionCN_64.dat", MinionAttacher.DatNameOf(MinionCards.VARIANT_CN));
        Assert.Equal("FFXIVMinion_64.dat", MinionAttacher.DatNameOf(MinionCards.VARIANT_GLOBAL));
    }

    [Fact]
    public void GameExeCandidates_Cn_ShengquLauncherThenGameExeThenProcess()
    {
        var candidates = MinionAttacher.GameExeCandidates(new DirectoryInfo(@"G:\最终幻想XIV"), @"G:\running\ffxiv_dx11.exe", MinionCards.VARIANT_CN);

        Assert.Equal
        (
            [@"G:\最终幻想XIV\sdo\sdologin\Launcher.exe", @"G:\最终幻想XIV\game\ffxiv_dx11.exe", @"G:\running\ffxiv_dx11.exe"],
            candidates
        );
        Assert.Equal([null, null, null], MinionAttacher.GameExeCandidates(null, null, MinionCards.VARIANT_CN));
    }

    [Fact]
    public void GameExeCandidates_Global_GameExeThenProcess_NeverShengquLauncher()
    {
        var candidates = MinionAttacher.GameExeCandidates
        (
            new DirectoryInfo(@"D:\FINAL FANTASY XIV - A Realm Reborn"),
            @"D:\running\ffxiv_dx11.exe",
            MinionCards.VARIANT_GLOBAL
        );

        Assert.Equal([@"D:\FINAL FANTASY XIV - A Realm Reborn\game\ffxiv_dx11.exe", @"D:\running\ffxiv_dx11.exe"], candidates);
        Assert.DoesNotContain(candidates, x => x != null && x.Contains("sdologin", StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
///     MinionLauncher 的输出写进本地日志前遮住本次的卡号与论坛密码（卡号、密码都是假值）
/// </summary>
[Collection(SerilogCaptureCollection.NAME)]
public sealed class MinionAttacherLauncherOutputTests
{
    private const string KEYCODE        = "FFXIVXFAKE0123456789FAKE0123456789";
    private const string FORUM_PASSWORD = "fake-forum-pass";

    [Fact]
    public void LauncherOutput_WithKeycodeAndPassword_IsRedactedInLogsAndErrors()
    {
        var minion   = new CatMinionLaunch("0123456789abcdef", MinionCards.VARIANT_CN, new CatSecret(KEYCODE), "11112222333344445555666677778888", "fake-forum-user", new CatSecret(FORUM_PASSWORD));
        var redactor = MinionAttacher.RedactorFor(minion);
        var errors   = new ConcurrentQueue<string>();

        using var logs = new CapturedLogs();
        MinionAttacher.OnLauncherOutput($"Parsed: -minionkey={KEYCODE} -minionpass={FORUM_PASSWORD}", redactor, errors);
        MinionAttacher.OnLauncherOutput($"ERROR: invalid key {KEYCODE}", redactor, errors);
        MinionAttacher.OnLauncherError($"login failed for pass {FORUM_PASSWORD}", redactor, errors);

        var all = logs.All;
        Assert.Contains("-minionkey=***", all);
        Assert.Contains("-minionpass=***", all);
        Assert.DoesNotContain(KEYCODE, all);
        Assert.DoesNotContain(FORUM_PASSWORD, all);
        Assert.Equal(2, errors.Count);
        Assert.All(errors, line => Assert.DoesNotContain(KEYCODE, line));
        Assert.All(errors, line => Assert.DoesNotContain(FORUM_PASSWORD, line));
    }
}
