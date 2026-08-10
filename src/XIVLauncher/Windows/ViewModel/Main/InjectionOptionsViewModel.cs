using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using XIVLauncher.Minion;
using XIVLauncher.Windows.Services;

namespace XIVLauncher.Windows.ViewModel.Main;

/// <summary>
///     启动页的注入选择 —— 每次启动现选注 Dalamud / 注 Minion / 都注 / 都不注, 以及本次用哪个 Minion 分组和账号。
///     分组和账号都是每次现读 Minion 的 <c>Settings\Accounts.json</c>, 不落任何写死的值；
///     Minion 的密码、安装目录、账号名是一次性配置, 留在设置里。
/// </summary>
internal sealed partial class InjectionOptionsViewModel : ObservableObject
{
    private readonly SettingsWindowViewModel settings;
    private readonly IDialogService          dialogService;

    /// <summary>
    ///     界面自己回填时置位, 避免 setter 反过来写配置
    /// </summary>
    private bool isReloading;

    private string? groupLoadError;

    public InjectionOptionsViewModel(SettingsWindowViewModel settings, IDialogService dialogService)
    {
        this.settings      = settings;
        this.dialogService = dialogService;

        MinionGroups          = [];
        MinionAccountsInGroup = [];

        ReloadFromSettings();
    }

    /// <summary>
    ///     Accounts.json 里的分组, 每次刷新都重读
    /// </summary>
    public ObservableCollection<MinionAccountGroup> MinionGroups { get; }

    /// <summary>
    ///     当前分组下的账号, 下拉里显示各自的 Keycode
    /// </summary>
    public ObservableCollection<MinionAccount> MinionAccountsInGroup { get; }

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

    public MinionAccountGroup? SelectedMinionGroup
    {
        get;
        set
        {
            if (!SetProperty(ref field, value))
                return;

            // 分组换了, 账号列表跟着换（回填期间也要换, 否则下面的账号还是上一组的）
            ReloadAccountsInGroup();

            if (isReloading || value == null)
                return;

            App.Settings.MinionGroup = value.Id;
            RefreshStatus();
        }
    }

    public MinionAccount? SelectedMinionAccount
    {
        get;
        set
        {
            if (!SetProperty(ref field, value) || isReloading || value == null)
                return;

            App.Settings.MinionAccountUid = value.Uid;
            RefreshStatus();
        }
    }

    [ObservableProperty]
    public partial string CrossDCStatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CrossDCStatusDetail { get; set; } = string.Empty;

    /// <summary>
    ///     从配置回填开关、分组和账号 —— 设置保存后要调一次
    /// </summary>
    public void ReloadFromSettings()
    {
        isReloading = true;

        try
        {
            WithDalamud = App.Settings.DalamudEnabled;
            WithMinion  = App.Settings.MinionAttachEnabled;
            ReloadMinionGroups();
        }
        finally
        {
            isReloading = false;
        }

        RefreshStatus();
    }

    [RelayCommand]
    private void RefreshMinionGroups()
    {
        isReloading = true;

        try
        {
            ReloadMinionGroups();
        }
        finally
        {
            isReloading = false;
        }

        RefreshStatus();
    }

    private void ReloadMinionGroups()
    {
        var loaded = MinionAccounts.TryLoadGroups(out var groups, out var error);
        groupLoadError = loaded ? null : error;

        if (!loaded)
            Log.Warning("[Minion] 读取分组失败 ({Path}): {Error}", MinionAccounts.GetAccountsJsonPath(), error);

        MinionGroups.Clear();
        foreach (var group in groups)
            MinionGroups.Add(group);

        var savedGroup = App.Settings.MinionGroup?.Trim();
        SelectedMinionGroup = MinionGroups.FirstOrDefault(group => string.Equals(group.Id, savedGroup, StringComparison.OrdinalIgnoreCase))
                              ?? MinionGroups.FirstOrDefault();

        // 让配置跟界面显示一致, 免得启动时用上一个已经不存在的分组/账号。
        // 只在真读到分组时写回 —— Minion 目录一时不可用不该清掉用户的选择。
        if (SelectedMinionGroup == null)
            return;

        App.Settings.MinionGroup = SelectedMinionGroup.Id;

        if (SelectedMinionAccount != null)
            App.Settings.MinionAccountUid = SelectedMinionAccount.Uid;
    }

    private void ReloadAccountsInGroup()
    {
        var wasReloading = isReloading;
        isReloading = true;

        try
        {
            MinionAccountsInGroup.Clear();

            foreach (var account in SelectedMinionGroup?.Accounts ?? [])
                MinionAccountsInGroup.Add(account);

            var savedUid = App.Settings.MinionAccountUid;
            SelectedMinionAccount = MinionAccountsInGroup.FirstOrDefault(account => string.Equals(account.Uid, savedUid, StringComparison.OrdinalIgnoreCase))
                                    ?? MinionAccountsInGroup.FirstOrDefault();
        }
        finally
        {
            isReloading = wasReloading;
        }

        if (!wasReloading && SelectedMinionAccount != null)
            App.Settings.MinionAccountUid = SelectedMinionAccount.Uid;
    }

    /// <summary>
    ///     打开「注入 Minion」前的配置检查, 缺东西就提示并把开关弹回去
    /// </summary>
    private bool TryEnableMinion()
    {
        // 先重读一遍, 用户刚补好的配置应该算数, 而不是拿旧状态报错
        RefreshMinionGroups();

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

             请在「设置 → Minion」中补齐后再打开此开关。
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

        if (!MinionAccounts.IsLauncherPresent())
            problems.Add($"未找到 {MinionAccounts.GetLauncherExePath()}（在设置里指定 Minion 安装目录）");

        if (groupLoadError != null)
            problems.Add(groupLoadError);
        else if (MinionGroups.Count == 0)
            problems.Add($"{MinionAccounts.GetAccountsJsonPath()} 里没有任何分组");
        else if (SelectedMinionAccount == null)
            problems.Add($"分组 {SelectedMinionGroup?.Id} 下没有可用账号");

        if (string.IsNullOrWhiteSpace(App.Settings.MinionPassword))
            problems.Add("未填写 Minion 账号密码（MinionLauncher 只接受明文, 在设置里配置一次）");

        if (string.IsNullOrWhiteSpace(App.Settings.MinionId))
            problems.Add("未填写 Minion 账号（-minionid）");

        return problems;
    }

    private void RefreshStatus()
    {
        if (WithMinion && groupLoadError != null)
        {
            CrossDCStatusText   = "Minion 分组读取失败";
            CrossDCStatusDetail = groupLoadError;
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
