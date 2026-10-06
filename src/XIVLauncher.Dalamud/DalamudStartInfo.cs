namespace XIVLauncher.Dalamud;

[Serializable]
public sealed class DalamudStartInfo
{
    public string WorkingDirectory  = string.Empty;
    public string ConfigurationPath = string.Empty;
    public string LoggingPath       = string.Empty;

    public string PluginDirectory = string.Empty;
    public string AssetDirectory  = string.Empty;
    public int    DelayInitializeMs;

    public string GameVersion             = string.Empty;
    public string TroubleshootingPackData = string.Empty;
    public string LauncherDirectory       = Environment.CurrentDirectory;

    /// <summary><c>--dalamud-client-language</c>; 缺省 4 = 国服</summary>
    public int ClientLanguage = DalamudSessionFlavor.CLIENT_LANGUAGE_CN;

    /// <summary>是否传 <c>--launcher-directory</c>; 缺省传（国服 Dalamud）</summary>
    public bool PassLauncherDirectory = true;

    /// <summary>是否传 <c>--managed-restart</c>; 缺省传（国服 Dalamud）</summary>
    public bool ManagedRestart = true;
}
