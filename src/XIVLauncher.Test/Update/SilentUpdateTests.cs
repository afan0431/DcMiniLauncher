using XIVLauncher.CatHost;
using XIVLauncher.Update;
using Xunit;

namespace XIVLauncher.Test.Update;

/// <summary>
///     无界面更新（<c>--cat-self-update</c>）: 会结束别的启动器进程时不装; 命令行开关的判定
/// </summary>
public sealed class SilentUpdateTests : IDisposable
{
    private readonly Func<bool> originalCatHost   = UpdateOrchestrator.IsCatHostRunning;
    private readonly Func<bool> originalGuarded   = UpdateOrchestrator.IsGameGuarded;
    private readonly Func<bool> originalInstallUse = UpdateOrchestrator.IsInstallInUse;

    public void Dispose()
    {
        UpdateOrchestrator.IsCatHostRunning = originalCatHost;
        UpdateOrchestrator.IsGameGuarded    = originalGuarded;
        UpdateOrchestrator.IsInstallInUse   = originalInstallUse;
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task RunSilent_DoesNothing_WhileALauncherGuardsAGame(bool catHost, bool uiGuard)
    {
        UpdateOrchestrator.IsCatHostRunning = () => catHost;
        UpdateOrchestrator.IsGameGuarded    = () => uiGuard;
        UpdateOrchestrator.IsInstallInUse   = () => false;

        Assert.Equal(UpdateOrchestrator.SILENT_GUARDED, await UpdateOrchestrator.RunSilentAsync());
    }

    [Fact]
    public async Task RunSilent_DoesNothing_WhileAnotherLauncherRunsFromTheInstall()
    {
        UpdateOrchestrator.IsCatHostRunning = () => false;
        UpdateOrchestrator.IsGameGuarded    = () => false;
        UpdateOrchestrator.IsInstallInUse   = () => true;

        Assert.Equal(UpdateOrchestrator.SILENT_IN_USE, await UpdateOrchestrator.RunSilentAsync());
    }

    [Fact]
    public void Switches_AreRecognisedByName()
    {
        string[] detached = ["XIVLauncherCN.exe", "--cat-launch", "--CAT-DETACHED"];
        string[] update   = ["XIVLauncherCN.exe", "--cat-self-update"];

        Assert.True(CatHostMode.HasSwitch(detached, CatHostMode.DETACHED_SWITCH));
        Assert.False(CatHostMode.HasSwitch(update, CatHostMode.DETACHED_SWITCH));
        Assert.True(CatHostMode.HasSwitch(update, CatHostMode.SELF_UPDATE_SWITCH));
        Assert.False(CatHostMode.HasSwitch(detached, CatHostMode.SELF_UPDATE_SWITCH));
    }
}
