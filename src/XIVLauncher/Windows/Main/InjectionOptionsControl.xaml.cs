namespace XIVLauncher.Windows.Main;

/// <summary>
///     启动页的注入选择块 —— 注 Dalamud / 注 Minion / 都注 / 都不注, 以及本次用的 Minion 分组。
///     绑定 MainWindowViewModel.InjectionOptions, 单独成控件是为了少动上游的启动页文件。
/// </summary>
public partial class InjectionOptionsControl
{
    public InjectionOptionsControl() =>
        InitializeComponent();
}
