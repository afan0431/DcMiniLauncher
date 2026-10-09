using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XIVLauncher.Minion;
using XIVLauncher.Settings;

namespace XIVLauncher.Windows.ViewModel.Main;

/// <summary>
///     启动页「本次注入」里的 Minion 卡选择。卡每次现读（<see cref="MinionCardSource" />）:
///     本机有 Cat 工作台写的卡文件时选「卡 N」, 否则选 MINIONAPP 的 Accounts.json 里的分组和国服行（界面版只起国服）。
///     只有用户亲手改选时才写进设置; 重读只回填界面, 不改设置。
/// </summary>
internal sealed partial class MinionPickerViewModel : ObservableObject
{
    private readonly LauncherSettingsV3                     settings;
    private readonly Func<MinionCardChoices>                load;
    private readonly Func<IReadOnlyList<string?>>           cnGameRoots;

    /// <summary>
    ///     回填期间置位, 避免 setter 反过来写设置
    /// </summary>
    private bool isReloading;

    /// <param name="settings">保存所选卡的设置</param>
    /// <param name="load">读当前可选的卡</param>
    /// <param name="cnGameRoots">国服游戏目录, 用来挑出 Accounts.json 的国服行</param>
    public MinionPickerViewModel(LauncherSettingsV3 settings, Func<MinionCardChoices> load, Func<IReadOnlyList<string?>> cnGameRoots)
    {
        this.settings    = settings;
        this.load        = load;
        this.cnGameRoots = cnGameRoots;
    }

    /// <summary>
    ///     重读完成后触发
    /// </summary>
    public event Action? Reloaded;

    /// <summary>
    ///     最近一次读到的卡
    /// </summary>
    public MinionCardChoices Choices { get; private set; } = new(null, [], null);

    /// <summary>
    ///     本机 Cat 工作台写下的卡, 按编号排序
    /// </summary>
    public ObservableCollection<WorkbenchMinionCard> Cards { get; } = [];

    /// <summary>
    ///     Accounts.json 里含国服行的分组
    /// </summary>
    public ObservableCollection<MinionAccountGroup> Groups { get; } = [];

    /// <summary>
    ///     当前分组下的国服行, 下拉里显示各自的 Keycode
    /// </summary>
    public ObservableCollection<MinionAccount> AccountsInGroup { get; } = [];

    /// <summary>
    ///     卡来自本机 Cat 工作台的卡文件
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FromAccountsJson))]
    public partial bool FromWorkbench { get; private set; }

    /// <summary>
    ///     卡来自 MINIONAPP 的 Accounts.json
    /// </summary>
    public bool FromAccountsJson => !FromWorkbench;

    public WorkbenchMinionCard? SelectedCard
    {
        get;
        set
        {
            if (!SetProperty(ref field, value) || isReloading || value == null)
                return;

            settings.MinionCardFingerprint = value.Fingerprint;
        }
    }

    public MinionAccountGroup? SelectedGroup
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

            settings.MinionGroup = value.Id;

            if (SelectedAccount != null)
                settings.MinionAccountUid = SelectedAccount.Uid;
        }
    }

    public MinionAccount? SelectedAccount
    {
        get;
        set
        {
            if (!SetProperty(ref field, value) || isReloading || value == null)
                return;

            settings.MinionAccountUid = value.Uid;
        }
    }

    /// <summary>
    ///     界面上当前选中的卡（连同当时显示的来源）
    /// </summary>
    public MinionSelection CurrentSelection =>
        new(FromWorkbench ? MinionCardSourceKind.Workbench : MinionCardSourceKind.AccountsJson, SelectedCard?.Fingerprint, SelectedGroup?.Id, SelectedAccount?.Uid);

    /// <summary>
    ///     重读卡并回填选择: 优先设置里记住的, 否则第一项
    /// </summary>
    [RelayCommand]
    public void Refresh()
    {
        var wasReloading = isReloading;
        isReloading = true;

        try
        {
            Choices       = load();
            FromWorkbench = Choices.FromWorkbench;

            var savedFingerprint = settings.MinionCardFingerprint;
            Cards.Clear();
            foreach (var card in Choices.Workbench?.Cards ?? [])
                Cards.Add(card);

            SelectedCard = Cards.FirstOrDefault(card => string.Equals(card.Fingerprint, savedFingerprint, StringComparison.Ordinal))
                           ?? Cards.FirstOrDefault();

            var savedGroup = settings.MinionGroup?.Trim();
            Groups.Clear();
            foreach (var group in MinionCardSource.GroupsFor(Choices.Groups, MinionCards.VARIANT_CN, cnGameRoots()))
                Groups.Add(group);

            SelectedGroup = Groups.FirstOrDefault(group => string.Equals(group.Id, savedGroup, StringComparison.OrdinalIgnoreCase))
                            ?? Groups.FirstOrDefault();
        }
        finally
        {
            isReloading = wasReloading;
        }

        Reloaded?.Invoke();
    }

    private void ReloadAccountsInGroup()
    {
        var wasReloading = isReloading;
        isReloading = true;

        try
        {
            // 先读出记住的行: 清空列表时下拉会把选中项置空
            var savedUid = settings.MinionAccountUid;

            AccountsInGroup.Clear();

            foreach (var account in SelectedGroup?.Accounts ?? [])
                AccountsInGroup.Add(account);

            SelectedAccount = AccountsInGroup.FirstOrDefault(account => string.Equals(account.Uid, savedUid, StringComparison.OrdinalIgnoreCase))
                              ?? AccountsInGroup.FirstOrDefault();
        }
        finally
        {
            isReloading = wasReloading;
        }
    }
}
