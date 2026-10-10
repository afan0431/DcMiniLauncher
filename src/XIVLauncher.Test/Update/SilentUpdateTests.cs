using XIVLauncher.CatHost;
using XIVLauncher.Update;
using Xunit;

namespace XIVLauncher.Test.Update;

/// <summary>
///     无界面更新（<c>--cat-self-update</c>）: 没有新版本就不看别的; 有新版本但会结束别的启动器进程时不装; 命令行开关的判定
/// </summary>
public sealed class SilentUpdateTests : IDisposable
{
    private readonly Func<bool> originalCatHost   = UpdateOrchestrator.IsCatHostRunning;
    private readonly Func<bool> originalGuarded   = UpdateOrchestrator.IsGameGuarded;
    private readonly Func<bool> originalInstallUse = UpdateOrchestrator.IsInstallInUse;
    private readonly Func<UpdateOrchestrator.ISilentUpdater> originalUpdater = UpdateOrchestrator.CreateSilentUpdater;

    public void Dispose()
    {
        UpdateOrchestrator.IsCatHostRunning    = originalCatHost;
        UpdateOrchestrator.IsGameGuarded       = originalGuarded;
        UpdateOrchestrator.IsInstallInUse      = originalInstallUse;
        UpdateOrchestrator.CreateSilentUpdater = originalUpdater;
    }

    private sealed class FakeUpdater(bool installed, string? newVersion) : UpdateOrchestrator.ISilentUpdater
    {
        public Action? OnDownload { get; init; }

        public bool Downloaded { get; private set; }

        public bool Applied { get; private set; }

        public bool IsInstalled => installed;

        public Task<string?> CheckAsync() => Task.FromResult(newVersion);

        public Task DownloadAsync()
        {
            Downloaded = true;
            OnDownload?.Invoke();
            return Task.CompletedTask;
        }

        public void ApplyAfterExit() => Applied = true;
    }

    private FakeUpdater UseUpdater(bool installed, string? newVersion)
    {
        var updater = new FakeUpdater(installed, newVersion);
        UpdateOrchestrator.CreateSilentUpdater = () => updater;
        return updater;
    }

    private static void SetGuards(bool catHost, bool uiGuard, bool installInUse)
    {
        UpdateOrchestrator.IsCatHostRunning = () => catHost;
        UpdateOrchestrator.IsGameGuarded    = () => uiGuard;
        UpdateOrchestrator.IsInstallInUse   = () => installInUse;
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task RunSilent_DoesNothing_WhileALauncherGuardsAGame(bool catHost, bool uiGuard)
    {
        SetGuards(catHost, uiGuard, false);
        var updater = UseUpdater(true, "2.5.0-mini.30");

        Assert.Equal(UpdateOrchestrator.SILENT_GUARDED, await UpdateOrchestrator.RunSilentAsync());
        Assert.False(updater.Downloaded);
        Assert.False(updater.Applied);
    }

    [Fact]
    public async Task RunSilent_DoesNothing_WhileAnotherLauncherRunsFromTheInstall()
    {
        SetGuards(false, false, true);
        var updater = UseUpdater(true, "2.5.0-mini.30");

        Assert.Equal(UpdateOrchestrator.SILENT_IN_USE, await UpdateOrchestrator.RunSilentAsync());
        Assert.False(updater.Downloaded);
        Assert.False(updater.Applied);
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, false, false)]
    public async Task RunSilent_IsUpToDate_WithoutANewVersion_EvenWhileGuardedOrInUse(bool catHost, bool uiGuard, bool installInUse)
    {
        SetGuards(catHost, uiGuard, installInUse);
        var updater = UseUpdater(true, null);

        Assert.Equal(UpdateOrchestrator.SILENT_UP_TO_DATE, await UpdateOrchestrator.RunSilentAsync());
        Assert.False(updater.Downloaded);
    }

    [Fact]
    public async Task RunSilent_IsNotInstalled_BeforeAnythingElse()
    {
        SetGuards(true, true, true);
        UseUpdater(false, "2.5.0-mini.30");

        Assert.Equal(UpdateOrchestrator.SILENT_NOT_INSTALLED, await UpdateOrchestrator.RunSilentAsync());
    }

    [Fact]
    public async Task RunSilent_DoesNotApply_WhenAGameStartsBeingGuardedDuringTheDownload()
    {
        SetGuards(false, false, false);
        var updater = new FakeUpdater(true, "2.5.0-mini.30") { OnDownload = () => UpdateOrchestrator.IsGameGuarded = () => true };
        UpdateOrchestrator.CreateSilentUpdater = () => updater;

        Assert.Equal(UpdateOrchestrator.SILENT_GUARDED, await UpdateOrchestrator.RunSilentAsync());
        Assert.True(updater.Downloaded);
        Assert.False(updater.Applied);
    }

    [Fact]
    public async Task RunSilent_DownloadsAndApplies_WhenNothingIsInTheWay()
    {
        SetGuards(false, false, false);
        var updater = UseUpdater(true, "2.5.0-mini.30");

        Assert.Equal(UpdateOrchestrator.SILENT_APPLYING, await UpdateOrchestrator.RunSilentAsync());
        Assert.True(updater.Downloaded);
        Assert.True(updater.Applied);
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

    [Theory]
    [InlineData(UpdateOrchestrator.SILENT_UP_TO_DATE, CatSelfUpdateExitCodes.UP_TO_DATE)]
    [InlineData(UpdateOrchestrator.SILENT_APPLYING, CatSelfUpdateExitCodes.APPLYING)]
    [InlineData(UpdateOrchestrator.SILENT_GUARDED, CatSelfUpdateExitCodes.GUARDED)]
    [InlineData(UpdateOrchestrator.SILENT_IN_USE, CatSelfUpdateExitCodes.IN_USE)]
    [InlineData(UpdateOrchestrator.SILENT_NOT_INSTALLED, CatSelfUpdateExitCodes.NOT_INSTALLED)]
    [InlineData("whatever", CatSelfUpdateExitCodes.FAILED)]
    public void Outcome_MapsToDistinctExitCode(string outcome, int exitCode) =>
        Assert.Equal(exitCode, CatSelfUpdateExitCodes.From(outcome));
}
