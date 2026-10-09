using CommunityToolkit.Mvvm.ComponentModel;

namespace XIVLauncher.Windows.ViewModel.Main;

/// <summary>
///     启动页的注入选择 —— 每次启动现选是否注入 Dalamud。挂 Minion 走 Cat 工作台上号, 启动页不挂。
/// </summary>
internal sealed partial class InjectionOptionsViewModel : ObservableObject
{
    private readonly SettingsWindowViewModel settings;

    /// <summary>
    ///     界面自己回填时置位, 避免 setter 反过来写配置
    /// </summary>
    private bool isReloading;

    public InjectionOptionsViewModel(SettingsWindowViewModel settings)
    {
        this.settings = settings;

        ReloadFromSettings();
    }

    /// <summary>
    ///     本次启动是否注入 Dalamud, 直接落到 <see cref="Settings.LauncherSettingsV3.DalamudEnabled" />
    /// </summary>
    public bool WithDalamud
    {
        get;
        set
        {
            if (!SetProperty(ref field, value) || isReloading)
                return;

            App.Settings.DalamudEnabled = value;

            // 设置窗口与启动页共用同一个 SettingsWindowViewModel, 保持两边一致
            settings.EnableHooks = value;

            RefreshStatus();
        }
    }

    [ObservableProperty]
    public partial string CrossDCStatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CrossDCStatusDetail { get; set; } = string.Empty;

    /// <summary>
    ///     从配置回填开关 —— 设置保存后要调一次
    /// </summary>
    public void ReloadFromSettings()
    {
        isReloading = true;

        try
        {
            WithDalamud = App.Settings.DalamudEnabled;
        }
        finally
        {
            isReloading = false;
        }

        RefreshStatus();
    }

    private void RefreshStatus() =>
        (CrossDCStatusText, CrossDCStatusDetail) = WithDalamud
                                                       ? ("超域传送: Dalamud 插件", "超域传送由 DCTravelerX 插件在游戏内完成")
                                                       : ("超域传送: 不可用", "没有任何游戏内代理, 本次启动无法游戏内超域传送");
}
