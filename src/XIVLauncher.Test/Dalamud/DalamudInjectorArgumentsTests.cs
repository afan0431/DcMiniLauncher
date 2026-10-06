using System.IO;
using System.Text;
using XIVLauncher.Dalamud;
using Xunit;

namespace XIVLauncher.Test.Dalamud;

/// <summary>
///     钉住国服 Dalamud 注入器的命令行: 语言、--launcher-directory、--managed-restart 做成选项之后,
///     缺省值下拼出来的参数必须与参数化之前逐字相同。期望值是照参数化之前的代码手写的字面量, 不经被测代码生成。
/// </summary>
public sealed class DalamudInjectorArgumentsTests
{
    private const string TS_DATA = "{\"When\":\"now\"}";

    private static readonly string TsB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(TS_DATA));

    private static DalamudStartInfo NewStartInfo() =>
        new()
        {
            WorkingDirectory        = @"C:\XL\addon\Hooks\1.0.0",
            ConfigurationPath       = @"C:\XL\dalamudConfig.json",
            LoggingPath             = @"C:\XL",
            PluginDirectory         = @"C:\XL\installedPlugins",
            AssetDirectory          = @"C:\XL\dalamudAssets\42",
            DelayInitializeMs       = 1500,
            GameVersion             = "2026.01.01.0000.0000",
            TroubleshootingPackData = TS_DATA,
            LauncherDirectory       = @"C:\Program Files\DcMiniLauncher"
        };

    [Fact]
    public void Launch_Defaults_MatchCommandLineBeforeParameterization()
    {
        var arguments = DalamudInjector.BuildLaunchArguments
        (
            DalamudLoadMethod.EntryPoint,
            new FileInfo(@"D:\FF14\game\ffxiv_dx11.exe"),
            NewStartInfo(),
            1234,
            false,
            false,
            false,
            "//**sqex0003abc**//"
        );

        Assert.Equal
        (
            "launch --mode=entrypoint --game=\"D:\\FF14\\game\\ffxiv_dx11.exe\" " +
            "--dalamud-working-directory=\"C:\\XL\\addon\\Hooks\\1.0.0\" " +
            "--dalamud-configuration-path=\"C:\\XL\\dalamudConfig.json\" " +
            "--logpath=\"C:\\XL\" " +
            "--dalamud-plugin-directory=\"C:\\XL\\installedPlugins\" " +
            "--dalamud-asset-directory=\"C:\\XL\\dalamudAssets\\42\" " +
            "--dalamud-client-language=4 " +
            "--dalamud-delay-initialize=1500 " +
            $"--dalamud-tspack-b64={TsB64} " +
            "--launcher-directory=\"C:\\Program Files\\DcMiniLauncher\" " +
            "--handle-owner=1234 " +
            "--managed-restart " +
            "-- //**sqex0003abc**//",
            string.Join(" ", arguments)
        );
    }

    [Fact]
    public void Launch_Defaults_AllSwitches_KeepOriginalOrder()
    {
        var arguments = DalamudInjector.BuildLaunchArguments
        (
            DalamudLoadMethod.ACLonly,
            new FileInfo(@"D:\FF14\game\ffxiv_dx11.exe"),
            NewStartInfo(),
            null,
            true,
            true,
            true,
            "args"
        );

        Assert.Equal
        (
            [
                "launch",
                "--mode=inject",
                "--game=\"D:\\FF14\\game\\ffxiv_dx11.exe\"",
                "--dalamud-working-directory=\"C:\\XL\\addon\\Hooks\\1.0.0\"",
                "--dalamud-configuration-path=\"C:\\XL\\dalamudConfig.json\"",
                "--logpath=\"C:\\XL\"",
                "--dalamud-plugin-directory=\"C:\\XL\\installedPlugins\"",
                "--dalamud-asset-directory=\"C:\\XL\\dalamudAssets\\42\"",
                "--dalamud-client-language=4",
                "--dalamud-delay-initialize=1500",
                $"--dalamud-tspack-b64={TsB64}",
                "--launcher-directory=\"C:\\Program Files\\DcMiniLauncher\"",
                "--without-dalamud",
                "--fake-arguments",
                "--no-plugin",
                "--no-3rd-plugin",
                "--managed-restart",
                "--",
                "args"
            ],
            arguments
        );
    }

    [Theory]
    [InlineData(false, false, "")]
    [InlineData(true, false, " --no-plugin")]
    [InlineData(false, true, " --no-3rd-plugin")]
    [InlineData(true, true, " --no-plugin --no-3rd-plugin")]
    public void Inject_Defaults_MatchCommandLineBeforeParameterization(bool safeMode, bool noThirdPlugins, string switches)
    {
        var arguments = DalamudInjector.BuildInjectArguments(4321, NewStartInfo(), safeMode, noThirdPlugins);

        Assert.Equal
        (
            "inject -v 4321 " +
            "--dalamud-working-directory=\"C:\\XL\\addon\\Hooks\\1.0.0\" " +
            "--dalamud-configuration-path=\"C:\\XL\\dalamudConfig.json\" " +
            "--logpath=\"C:\\XL\" " +
            "--dalamud-plugin-directory=\"C:\\XL\\installedPlugins\" " +
            "--dalamud-asset-directory=\"C:\\XL\\dalamudAssets\\42\" " +
            "--dalamud-client-language=4 " +
            "--dalamud-delay-initialize=1500 " +
            $"--dalamud-tspack-b64={TsB64} " +
            "--launcher-directory=\"C:\\Program Files\\DcMiniLauncher\"" +
            switches +
            " --managed-restart",
            string.Join(" ", arguments)
        );
    }

    [Fact]
    public void StartInfo_And_Flavor_DefaultsAreDomestic()
    {
        var startInfo = new DalamudStartInfo();
        Assert.Equal(4, startInfo.ClientLanguage);
        Assert.True(startInfo.PassLauncherDirectory);
        Assert.True(startInfo.ManagedRestart);

        var flavor = DalamudSessionFlavor.Default;
        Assert.Equal(4, flavor.ClientLanguage);
        Assert.True(flavor.PassLauncherDirectory);
        Assert.True(flavor.ManagedRestart);
        Assert.Null(flavor.IsGameVersionSupported);
    }

    /// <summary>
    ///     国际服（goatcorp 原版）: 与 goatcorp WindowsDalamudRunner.Run 的参数表一致 —— 语言照传, 没有两个国服专有参数
    /// </summary>
    [Fact]
    public void Launch_Upstream_MatchesGoatcorpArgumentList()
    {
        var startInfo = NewStartInfo();
        startInfo.ClientLanguage        = 1;
        startInfo.PassLauncherDirectory = false;
        startInfo.ManagedRestart        = false;

        var arguments = DalamudInjector.BuildLaunchArguments
        (
            DalamudLoadMethod.EntryPoint,
            new FileInfo(@"D:\SquareEnix\FFXIV\game\ffxiv_dx11.exe"),
            startInfo,
            77,
            false,
            false,
            false,
            "//**sqex0003xyz**//"
        );

        Assert.Equal
        (
            [
                "launch",
                "--mode=entrypoint",
                "--game=\"D:\\SquareEnix\\FFXIV\\game\\ffxiv_dx11.exe\"",
                "--dalamud-working-directory=\"C:\\XL\\addon\\Hooks\\1.0.0\"",
                "--dalamud-configuration-path=\"C:\\XL\\dalamudConfig.json\"",
                "--logpath=\"C:\\XL\"",
                "--dalamud-plugin-directory=\"C:\\XL\\installedPlugins\"",
                "--dalamud-asset-directory=\"C:\\XL\\dalamudAssets\\42\"",
                "--dalamud-client-language=1",
                "--dalamud-delay-initialize=1500",
                $"--dalamud-tspack-b64={TsB64}",
                "--handle-owner=77",
                "--",
                "//**sqex0003xyz**//"
            ],
            arguments
        );
        Assert.DoesNotContain(arguments, x => x.StartsWith("--launcher-directory", StringComparison.Ordinal));
        Assert.DoesNotContain("--managed-restart", arguments);
    }

    [Fact]
    public void Inject_Upstream_OmitsDomesticOnlyArguments()
    {
        var startInfo = NewStartInfo();
        startInfo.ClientLanguage        = 0;
        startInfo.PassLauncherDirectory = false;
        startInfo.ManagedRestart        = false;

        var arguments = DalamudInjector.BuildInjectArguments(99, startInfo, false, false);

        Assert.Contains("--dalamud-client-language=0", arguments);
        Assert.DoesNotContain(arguments, x => x.StartsWith("--launcher-directory", StringComparison.Ordinal));
        Assert.DoesNotContain("--managed-restart", arguments);
    }
}
