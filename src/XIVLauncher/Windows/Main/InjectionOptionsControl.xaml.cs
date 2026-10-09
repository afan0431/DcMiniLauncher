using System.Windows.Controls;
using XIVLauncher.Windows.ViewModel.Main;

namespace XIVLauncher.Windows.Main;

/// <summary>
///     启动页的注入选择块 —— 注 Dalamud / 注 Minion / 都注 / 都不注, 以及本次用的 Minion 卡。
///     绑定 MainWindowViewModel.InjectionOptions, 单独成控件是为了少动上游的启动页文件。
/// </summary>
public partial class InjectionOptionsControl
{
    public InjectionOptionsControl() =>
        InitializeComponent();

    /// <summary>
    ///     打开 Minion 卡的下拉前重读一次, 让工作台刚写下的卡或 Accounts.json 的改动立刻可选
    /// </summary>
    private void MinionComboBox_OnDropDownOpened(object? sender, EventArgs e)
    {
        if (sender is ComboBox { DataContext: MainWindowViewModel viewModel })
            viewModel.InjectionOptions.Minion.Refresh();
    }
}
