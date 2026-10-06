using System.IO;
using XIVLauncher.Common;
using XIVLauncher.Common.Game.International;
using XIVLauncher.Settings;
using Xunit;

namespace XIVLauncher.Test.International;

public sealed class InternationalSettingsTests : IDisposable
{
    private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("dml-intl-settings-");

    private string ConfigPath => Path.Combine(directory.FullName, "launcherConfigV3.json");

    public void Dispose()
    {
        try
        {
            directory.Delete(true);
        }
        catch
        {
            // ignored
        }
    }

    [Fact]
    public void OldConfigWithoutInternationalFields_LoadsWithDefaults_AndKeepsDomesticValues()
    {
        // 加这两项之前的配置文件（也是官方启动器写出来的样子）
        File.WriteAllText(ConfigPath, "{ \"GamePath\": \"D:\\\\FF14\", \"WeGamePath\": \"E:\\\\WeGame\\\\FF14\", \"FastLogin\": false, \"MinionId\": \"someone\" }");

        var settings = LauncherSettingsV3.Load(ConfigPath);

        Assert.Null(settings.InternationalGamePath);
        Assert.Equal(ClientLanguage.English, settings.InternationalLanguage);
        Assert.Equal(@"D:\FF14", settings.GamePath!.FullName);
        Assert.Equal(@"E:\WeGame\FF14", settings.WeGamePath!.FullName);
        Assert.False(settings.FastLogin);
        Assert.Equal("someone", settings.MinionId);
    }

    [Fact]
    public void InternationalFields_RoundTrip()
    {
        var settings = LauncherSettingsV3.Load(ConfigPath);
        settings.InternationalGamePath = new DirectoryInfo(@"D:\FINAL FANTASY XIV - A Realm Reborn");
        settings.InternationalLanguage = ClientLanguage.Japanese;

        var reloaded = LauncherSettingsV3.Load(ConfigPath);

        Assert.Equal(@"D:\FINAL FANTASY XIV - A Realm Reborn", reloaded.InternationalGamePath!.FullName);
        Assert.Equal(ClientLanguage.Japanese, reloaded.InternationalLanguage);
        Assert.Null(reloaded.GamePath);
    }

    [Theory]
    [InlineData("0", ClientLanguage.Japanese)]
    [InlineData("2", ClientLanguage.German)]
    [InlineData("3", ClientLanguage.French)]
    [InlineData("4", ClientLanguage.English)]  // 4 是国服在 Dalamud 里的语言编号, 不是国际服的: 按英语
    [InlineData("-1", ClientLanguage.English)]
    [InlineData("99", ClientLanguage.English)]
    public void InternationalLanguage_UnknownNumberFallsBackToEnglish(string raw, ClientLanguage expected)
    {
        File.WriteAllText(ConfigPath, $"{{ \"InternationalLanguage\": {raw} }}");

        Assert.Equal(expected, LauncherSettingsV3.Load(ConfigPath).InternationalLanguage);
    }

    [Fact]
    public void GamePathCheck_RecognizesInternationalClientLayout()
    {
        using var game = new FakeGameDirectory();
        File.WriteAllText(Path.Combine(game.Root.FullName, "game", "ffxiv_dx11.exe"), "exe");

        Assert.Equal(InternationalGamePathState.Ok, InternationalGamePath.Check(game.Root.FullName));
        Assert.Null(InternationalGamePath.Describe(InternationalGamePathState.Ok));
    }

    [Fact]
    public void GamePathCheck_RejectsMissingWrongAndChineseDirectories()
    {
        Assert.Equal(InternationalGamePathState.NotSet, InternationalGamePath.Check(null));
        Assert.Equal(InternationalGamePathState.NotSet, InternationalGamePath.Check("  "));
        Assert.Equal(InternationalGamePathState.NotFound, InternationalGamePath.Check(Path.Combine(directory.FullName, "nope")));

        // 选成了 game 那一层 / 没有可执行文件
        using var game = new FakeGameDirectory();
        Assert.Equal(InternationalGamePathState.NotGameRoot, InternationalGamePath.Check(game.Root.FullName));
        File.WriteAllText(Path.Combine(game.Root.FullName, "game", "ffxiv_dx11.exe"), "exe");
        Assert.Equal(InternationalGamePathState.NotGameRoot, InternationalGamePath.Check(Path.Combine(game.Root.FullName, "game")));

        // 国服客户端: 盛趣有 sdo, WeGame 有 rail_files
        Directory.CreateDirectory(Path.Combine(game.Root.FullName, "sdo"));
        Assert.Equal(InternationalGamePathState.LooksLikeChineseClient, InternationalGamePath.Check(game.Root.FullName));
        Directory.Delete(Path.Combine(game.Root.FullName, "sdo"));
        Directory.CreateDirectory(Path.Combine(game.Root.FullName, "rail_files"));
        Assert.Equal(InternationalGamePathState.LooksLikeChineseClient, InternationalGamePath.Check(game.Root.FullName));

        foreach (var state in Enum.GetValues<InternationalGamePathState>().Where(x => x != InternationalGamePathState.Ok))
            Assert.False(string.IsNullOrWhiteSpace(InternationalGamePath.Describe(state)));
    }
}
