using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using XIVLauncher.Minion;
using XIVLauncher.Windows.Services;

namespace XIVLauncher.Windows.ViewModel.Main;

/// <summary>
///     启动页的注入选择 —— 每次启动现选注 Dalamud / 注 Minion / 都注 / 都不注, 以及本次挂哪张 Minion 卡（见 <see cref="MinionPickerViewModel" />）。
///     Minion 的安装目录、论坛账号密码（只在用 Accounts.json 时需要）是一次性配置, 留在设置里。
/// </summary>
internal sealed partial class InjectionOptionsViewModel : ObservableObject
{
    private readonly SettingsWindowViewModel settings;
    private readonly IDialogService          dialogService;

    /// <summary>
    ///     界面自己回填时置位, 避免 setter 反过来写配置
    /// </summary>
    private bool isReloading;

    public InjectionOptionsViewModel(SettingsWindowViewModel settings, IDialogService dialogService)
    {
        this.settings      = settings;
        this.dialogService = dialogService;

        Minion          =  new MinionPickerViewModel(App.Settings, () => MinionCardSource.Load(), MinionAccounts.ConfiguredCnGameRoots);
        Minion.Reloaded += RefreshStatus;

        ReloadFromSettings();
    }

    /// <summary>
    ///     本次挂哪张 Minion 卡
    /// </summary>
    public MinionPickerViewModel Minion { get; }

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

    /// <summary>
    ///     本次启动是否挂 Minion
    /// </summary>
    public bool WithMinion
    {
        get;
        set
        {
            if (!SetProperty(ref field, value) || isReloading)
                return;

            if (value && !TryEnableMinion())
                return;

            App.Settings.MinionAttachEnabled = value;
            RefreshStatus();
        }
    }

    [ObservableProperty]
    public partial string CrossDCStatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CrossDCStatusDetail { get; set; } = string.Empty;

    /// <summary>
    ///     从配置回填开关和卡 —— 设置保存后要调一次
    /// </summary>
    public void ReloadFromSettings()
    {
        isReloading = true;

        try
        {
            WithDalamud = App.Settings.DalamudEnabled;
            WithMinion  = App.Settings.MinionAttachEnabled;
        }
        finally
        {
            isReloading = false;
        }

        Minion.Refresh();
    }

    /// <summary>
    ///     打开「注入 Minion」前的配置检查, 缺东西就提示并把开关弹回去
    /// </summary>
    private bool TryEnableMinion()
    {
        // 先重读一遍, 用户刚补好的配置应该算数, 而不是拿旧状态报错
        Minion.Refresh();

        var problems = CollectMinionConfigProblems();
        if (problems.Count == 0)
            return true;

        isReloading = true;

        try
        {
            WithMinion = false;
        }
        finally
        {
            isReloading = false;
        }

        dialogService.ShowMessage
        (
            $"""
             Minion 注入还缺少配置：

             {string.Join(Environment.NewLine, problems.Select(problem => $"· {problem}"))}

             请补齐后再打开此开关。
             """,
            "Minion 注入",
            MessageBoxButton.OK,
            MessageBoxImage.Exclamation,
            false,
            false
        );

        RefreshStatus();
        return false;
    }

    /// <summary>
    ///     会让启动后挂 Minion 失败的配置缺口, 用人话列出来
    /// </summary>
    private List<string> CollectMinionConfigProblems()
    {
        var problems = new List<string>();

        if (!MinionInstall.IsLauncherPresent())
            problems.Add($"未找到 {MinionInstall.GetLauncherExePath()}（在「设置 → Minion」里指定 Minion 安装目录）");

        if (Minion.Choices.Workbench is { } file)
        {
            if (file.Cards.Count == 0)
                problems.Add("Cat 工作台还没有录入可用的 Minion 卡（在工作台「设置 → Minion」里录入）");
            else if (Minion.SelectedCard == null)
                problems.Add("没有选择 Minion 卡");

            if (string.IsNullOrWhiteSpace(file.ForumId) || string.IsNullOrEmpty(file.ForumPassword.Reveal()))
                problems.Add("Cat 工作台还没有填 Minion 论坛账号或密码（在工作台「设置 → Minion」里填写）");

            return problems;
        }

        if (Minion.Choices.GroupLoadError != null)
            problems.Add(Minion.Choices.GroupLoadError);
        else if (Minion.Groups.Count == 0)
            problems.Add($"{MinionAccounts.GetAccountsJsonPath()} 里没有国服的账号行");
        else if (Minion.SelectedAccount == null)
            problems.Add($"分组 {Minion.SelectedGroup?.Id} 下没有可用账号");

        if (string.IsNullOrEmpty(App.Settings.MinionPassword))
            problems.Add("未填写 Minion 账号密码（在「设置 → Minion」里配置一次）");

        if (string.IsNullOrWhiteSpace(App.Settings.MinionId))
            problems.Add("未填写 Minion 账号（在「设置 → Minion」里配置一次）");

        return problems;
    }

    private void RefreshStatus()
    {
        if (WithMinion && !Minion.FromWorkbench && Minion.Choices.GroupLoadError != null)
        {
            CrossDCStatusText   = "Minion 分组读取失败";
            CrossDCStatusDetail = Minion.Choices.GroupLoadError;
            return;
        }

        (CrossDCStatusText, CrossDCStatusDetail) = (WithDalamud, WithMinion) switch
        {
            (true, _)      => ("超域传送: Dalamud 插件", "超域传送由 DCTravelerX 插件在游戏内完成"),
            (false, true)  => ("超域传送: Minion 模式", "超域传送由启动器注入的模块在游戏内完成（只 Minion 模式）"),
            (false, false) => ("超域传送: 不可用", "没有任何游戏内代理, 本次启动无法游戏内超域传送")
        };
    }
}
