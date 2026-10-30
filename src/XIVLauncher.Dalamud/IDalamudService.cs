namespace XIVLauncher.Dalamud;

public interface IDalamudService
{
    DalamudUpdater Updater { get; }

    /// <summary>dalamudConfig.json 与 installedPlugins 所在的目录</summary>
    DirectoryInfo ConfigDirectory { get; }

    event Action<DalamudStatusSnapshot>? StatusChanged;

    void RunUpdater(bool refreshVersionInfo = false);

    void EnsureCompatibility();

    DalamudSession CreateLauncher(DirectoryInfo gamePath, DalamudLaunchOptions options);

    DalamudStatusSnapshot GetStatusSnapshot();
}
