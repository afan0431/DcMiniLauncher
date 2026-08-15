using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using XIVLauncher.DCTravel;
using XIVLauncher.InGame;
using XIVLauncher.Login.Models;

namespace XIVLauncher.Windows.ViewModel.Main;

public sealed partial class DCTravelViewModel : ObservableObject
{
    private readonly Action               requestBackToDashboardAction;
    private readonly Action               requestOpenHistoryAction;
    private readonly Action               requestBackToTravelAction;
    private readonly Action               requestShowProgressAction;
    private readonly Action               requestOpenReturnAction;
    private readonly Action<string>       setCurrentAreaAction;
    private readonly Action               activateAction;
    private readonly Action               onTravelCompleteAction;
    private readonly Func<DCTravelClient> getDcTravelClientFunc;

    private CancellationTokenSource? pollCts;
    private CancellationTokenSource? charactersLoadCts;

    public DCTravelViewModel
    (
        Action               requestBackToDashboardAction,
        Action               requestOpenHistoryAction,
        Action               requestBackToTravelAction,
        Action               requestShowProgressAction,
        Action               requestOpenReturnAction,
        Action<string>       setCurrentAreaAction,
        Action               activateAction,
        Action               onTravelCompleteAction,
        Func<DCTravelClient> getDcTravelClientFunc
    )
    {
        this.requestBackToDashboardAction = requestBackToDashboardAction;
        this.requestOpenHistoryAction     = requestOpenHistoryAction;
        this.requestBackToTravelAction    = requestBackToTravelAction;
        this.requestShowProgressAction    = requestShowProgressAction;
        this.requestOpenReturnAction      = requestOpenReturnAction;
        this.setCurrentAreaAction         = setCurrentAreaAction;
        this.activateAction               = activateAction;
        this.onTravelCompleteAction       = onTravelCompleteAction;
        this.getDcTravelClientFunc        = getDcTravelClientFunc;

        SourceAreas     = [];
        TargetAreas     = [];
        TargetGroups    = [];
        Characters      = [];
        MigrationOrders = [];

        MigrationOrders.CollectionChanged += (s, e) =>
        {
            OnPropertyChanged(nameof(HasMigrationOrders));
            OnPropertyChanged(nameof(HasNoMigrationOrders));
        };
    }

    public ObservableCollection<DCTravelArea> SourceAreas { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCharacterVisible))]
    [NotifyPropertyChangedFor(nameof(IsCharacterEnabled))]
    [NotifyPropertyChangedFor(nameof(CharacterHint))]
    [NotifyPropertyChangedFor(nameof(IsTargetAreaVisible))]
    [NotifyPropertyChangedFor(nameof(IsTargetGroupVisible))]
    [NotifyPropertyChangedFor(nameof(CanTravelOrder))]
    [NotifyCanExecuteChangedFor(nameof(TravelOrderCommand))]
    public partial DCTravelArea? SelectedSourceArea { get; set; }

    partial void OnSelectedSourceAreaChanged(DCTravelArea? value)
    {
        charactersLoadCts?.Cancel();
        charactersLoadCts = new CancellationTokenSource();

        SelectedCharacter   = null;
        SelectedTargetArea  = null;
        SelectedTargetGroup = null;
        TargetAreas.Clear();
        TargetGroups.Clear();
        Characters.Clear();

        if (value != null)
            _ = LoadCharactersAsync(value, charactersLoadCts.Token);
        else
            IsLoading = false;
    }

    public ObservableCollection<DCTravelCharacter> Characters { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTargetAreaVisible))]
    [NotifyPropertyChangedFor(nameof(IsTargetGroupVisible))]
    [NotifyPropertyChangedFor(nameof(CanTravelOrder))]
    [NotifyCanExecuteChangedFor(nameof(TravelOrderCommand))]
    public partial DCTravelCharacter? SelectedCharacter { get; set; }

    partial void OnSelectedCharacterChanged(DCTravelCharacter? value)
    {
        SelectedTargetArea  = null;
        SelectedTargetGroup = null;
        TargetAreas.Clear();
        TargetGroups.Clear();

        if (value != null && SelectedSourceArea != null)
            _ = LoadTargetAreasAsync(SelectedSourceArea, value);

        TravelOrderCommand.NotifyCanExecuteChanged();
    }

    public ObservableCollection<DCTravelArea> TargetAreas { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTargetGroupVisible))]
    [NotifyPropertyChangedFor(nameof(CanTravelOrder))]
    public partial DCTravelArea? SelectedTargetArea { get; set; }

    partial void OnSelectedTargetAreaChanged(DCTravelArea? value)
    {
        SelectedTargetGroup = null;
        TargetGroups.Clear();
        if (value != null)
        {
            foreach (var group in value.GroupList)
                TargetGroups.Add(group);
        }
    }

    public ObservableCollection<DCTravelGroup> TargetGroups { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanTravelOrder))]
    [NotifyCanExecuteChangedFor(nameof(TravelOrderCommand))]
    public partial DCTravelGroup? SelectedTargetGroup { get; set; }

    public ObservableCollection<DCTravelMigrationOrder> MigrationOrders { get; }

    public bool HasMigrationOrders   => MigrationOrders.Count > 0;
    public bool HasNoMigrationOrders => MigrationOrders.Count == 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TravelBackCommand))]
    public partial DCTravelMigrationOrder? SelectedOrder { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotLoading))]
    [NotifyPropertyChangedFor(nameof(IsCharacterEnabled))]
    [NotifyPropertyChangedFor(nameof(CharacterHint))]
    [NotifyPropertyChangedFor(nameof(CanTravelOrder))]
    [NotifyCanExecuteChangedFor(nameof(TravelOrderCommand))]
    [NotifyCanExecuteChangedFor(nameof(TravelBackCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmTravelBackCommand))]
    public partial bool IsLoading { get; set; }

    public bool IsNotLoading => !IsLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotUnderMaintenance))]
    [NotifyPropertyChangedFor(nameof(CanTravelOrder))]
    [NotifyCanExecuteChangedFor(nameof(TravelOrderCommand))]
    [NotifyCanExecuteChangedFor(nameof(TravelBackCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmTravelBackCommand))]
    public partial bool IsUnderMaintenance { get; set; }

    public bool IsNotUnderMaintenance => !IsUnderMaintenance;

    [ObservableProperty]
    public partial string MaintenanceMessage { get; set; } = string.Empty;

    public bool IsCharacterVisible   => SelectedSourceArea != null;
    public bool IsCharacterEnabled   => SelectedSourceArea != null && !IsLoading;
    public bool IsTargetAreaVisible  => SelectedCharacter  != null;
    public bool IsTargetGroupVisible => SelectedTargetArea != null;

    public string CharacterHint => IsLoading && SelectedSourceArea != null ? "(获取角色信息中)" : "选择角色";

    public bool CanTravelOrder =>
        SelectedSourceArea != null && SelectedCharacter != null && SelectedTargetArea != null && SelectedTargetGroup != null && !IsLoading && !IsUnderMaintenance;

    [ObservableProperty]
    public partial bool AutoStartGameOnComplete { get; set; } = true;

    // 超域返回页属性
    public ObservableCollection<DCTravelArea> ReturnSourceAreas { get; } = [];

    [ObservableProperty]
    public partial DCTravelArea? ReturnSelectedSourceArea { get; set; }

    partial void OnReturnSelectedSourceAreaChanged(DCTravelArea? value)
    {
        ReturnSelectedCurrentGroup = null;
        ReturnCurrentGroups.Clear();
        if (value == null)
            return;

        foreach (var group in value.GroupList)
            ReturnCurrentGroups.Add(group);

        if (!string.IsNullOrWhiteSpace(pendingTargetGroupName))
        {
            ReturnSelectedCurrentGroup = ReturnCurrentGroups.FirstOrDefault
                (group => string.Equals(group.GroupName, pendingTargetGroupName, StringComparison.Ordinal));
            pendingTargetGroupName = null;
        }
    }

    public ObservableCollection<DCTravelGroup> ReturnCurrentGroups { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmTravelBackCommand))]
    public partial DCTravelGroup? ReturnSelectedCurrentGroup { get; set; }

    [ObservableProperty]
    public partial string ReturnOrderInfo { get; set; } = string.Empty;

    private DCTravelMigrationOrder? pendingReturnOrder;
    private string?                 pendingTargetGroupName;

    // 进度页属性
    [ObservableProperty]
    public partial string TravelProgressText { get; set; } = "正在提交传送请求…";

    [ObservableProperty]
    public partial bool IsTravelInProgress { get; set; } = true;

    [ObservableProperty]
    public partial bool IsTravelSuccessful { get; set; }

    [RelayCommand(CanExecute = nameof(CanTravelOrderExecute))]
    private async Task TravelOrder() =>
        await StartTravelAsync();

    private bool CanTravelOrderExecute() =>
        SelectedTargetGroup != null &&
        SelectedCharacter   != null &&
        !IsLoading                  &&
        !IsUnderMaintenance;

    [RelayCommand(CanExecute = nameof(CanTravelBack))]
    private void TravelBack() =>
        OpenReturnPage();

    private bool CanTravelBack() =>
        SelectedOrder != null && !IsLoading && !IsUnderMaintenance;

    [RelayCommand]
    private async Task RefreshOrders() =>
        await RefreshOrdersAsync();

    [RelayCommand(CanExecute = nameof(CanConfirmTravelBack))]
    private async Task ConfirmTravelBack() =>
        await ConfirmTravelBackAsync();

    private bool CanConfirmTravelBack() =>
        ReturnSelectedCurrentGroup != null && !IsLoading && !IsUnderMaintenance;

    [RelayCommand]
    private void BackToDashboard() =>
        requestBackToDashboardAction();

    [RelayCommand]
    private void OpenHistory() =>
        requestOpenHistoryAction();

    [RelayCommand]
    private void BackToTravel() =>
        requestBackToTravelAction();

    [RelayCommand]
    private void ReturnFromProgress() =>
        CancelPollAndReturn();

    [RelayCommand]
    private void CancelReturn() =>
        requestOpenHistoryAction();

    public async Task InitializeAsync(string? currentAreaName = null)
    {
        SelectedSourceArea = null;
        SourceAreas.Clear();
        MigrationOrders.Clear();
        SelectedOrder = null;

        await RefreshTravelDataAsync();

        // 首次打开时预填充当前账号所在大区，触发后续角色与目标列表加载
        if (SelectedSourceArea == null && !string.IsNullOrWhiteSpace(currentAreaName))
            SelectedSourceArea = SourceAreas.FirstOrDefault(a => string.Equals(a.AreaName, currentAreaName, StringComparison.Ordinal));

        await RefreshOrdersAsync();
    }

    private async Task StartTravelAsync()
    {
        if (SelectedSourceArea == null || SelectedTargetGroup == null || SelectedCharacter == null)
            return;

        pollCts?.Cancel();
        pollCts = new CancellationTokenSource();

        IsTravelInProgress = true;
        IsTravelSuccessful = false;
        TravelProgressText = "正在提交传送请求…";
        requestShowProgressAction();

        try
        {
            var            client      = getDcTravelClientFunc();
            DCTravelGroup? sourceGroup = null;

            foreach (var g in SelectedSourceArea.GroupList)
            {
                if (g.AreaID == SelectedCharacter.AreaID && g.GroupID == SelectedCharacter.GroupID)
                {
                    sourceGroup = g;
                    break;
                }
            }

            sourceGroup ??= SelectedSourceArea.GroupList.FirstOrDefault();

            if (sourceGroup == null)
            {
                TravelProgressText = "无法确定源服务器";
                IsTravelInProgress = false;
                return;
            }

            var targetGroup = SelectedTargetGroup;

            // F4: 游戏已经开着（且是「只 Minion」模式）就原地换服, 不必退客户端。
            // 选人选服的界面完全复用上面这套, 只是执行路径不同。
            var inGameTarget = TryGetInGameTarget();

            if (inGameTarget != null)
            {
                var travelled = await TravelInGameAsync(client, inGameTarget, sourceGroup, targetGroup, pollCts.Token);

                if (!travelled)
                    return;
            }
            else
            {
                var orderId   = await client.TravelOrder(targetGroup, sourceGroup, SelectedCharacter);
                var completed = await PollOrderStatusAsync(orderId, pollCts.Token);

                if (!completed)
                    return;
            }

            if (SelectedTargetArea != null)
                UpdateCurrentArea(SelectedTargetArea.AreaName);

            // 游戏内换服的客户端已经在目标大区跑着了, 这时候再「传送完成后自动启动游戏」
            // 就会多开出第二个客户端 —— 所以那条只走外部传送
            if (AutoStartGameOnComplete && inGameTarget == null)
                onTravelCompleteAction();
            else
                activateAction();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error(ex, "[DCTravelVM] 传送失败");
            TravelProgressText = $"传送失败: {ex.Message}";
            IsTravelInProgress = false;
        }
    }

    /// <summary>
    ///     本次传送能不能走「游戏内换服」（F4）。条件是本启动器正好起着一个客户端, 且它是
    ///     「只 Minion」模式 —— 注了 Dalamud 的由 DcTraveler 插件负责, 什么都没注的没有执行者。
    ///     不满足就返回 null, 走原来的外部传送（传送完再启动游戏）。
    /// </summary>
    private static RunningGameRegistry.Entry? TryGetInGameTarget()
    {
        var game = RunningGameRegistry.Resolve(null, out _);

        if (game == null)
            return null;

        if (game.Agents != InGameAgents.Minion)
            return null;

        return MiniModuleInjector.ModulePath.Exists ? game : null;
    }

    private async Task<bool> TravelInGameAsync
    (
        DCTravelClient              client,
        RunningGameRegistry.Entry   game,
        DCTravelGroup               sourceGroup,
        DCTravelGroup               targetGroup,
        CancellationToken           ct
    )
    {
        if (SelectedTargetArea == null || SelectedCharacter == null)
            return false;

        LoginArea? loginArea = null;

        try
        {
            var loginAreas = await LoginArea.Get();
            loginArea = loginAreas.FirstOrDefault(x => string.Equals(x.AreaName, SelectedTargetArea.AreaName, StringComparison.Ordinal));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[DCTravelVM] 取大区登录主机名失败");
        }

        if (loginArea == null)
        {
            TravelProgressText = $"拿不到大区 {SelectedTargetArea.AreaName} 的登录主机名, 无法游戏内换服";
            IsTravelInProgress = false;
            return false;
        }

        var progress = new Progress<string>(text => TravelProgressText = text);
        var service  = new InGameTravelService(client);

        var result = await service.TravelAsync(game.Process, sourceGroup, targetGroup, SelectedCharacter, loginArea, progress, ct);

        if (!result.Ok)
        {
            TravelProgressText = $"游戏内换服失败: {result.Message}";
            IsTravelInProgress = false;
            return false;
        }

        TravelProgressText = $"已在游戏内换到 {SelectedTargetArea.AreaName}（客户端未重启）";
        IsTravelInProgress = false;
        IsTravelSuccessful = true;

        await RefreshOrdersAsync();
        return true;
    }

    private async Task<bool> PollOrderStatusAsync(string orderId, CancellationToken ct)
    {
        var client              = getDcTravelClientFunc();
        var confirmationSent    = false;
        var consecutiveFailures = 0;
        var deadline            = DateTimeOffset.UtcNow.AddMinutes(30);

        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var status = await client.QueryOrderStatus(orderId);
                consecutiveFailures = 0;

                TravelProgressText = status.Status switch
                {
                    DCTravelStatusType.Checking or DCTravelStatusType.CheckingAlt     => "检查目标大区角色信息中…",
                    DCTravelStatusType.NeedConfirmation                               => "等待确认传送…",
                    DCTravelStatusType.Processing or DCTravelStatusType.ProcessingAlt => "超域传送排队中…",
                    DCTravelStatusType.Success                                        => "超域传送完成",
                    DCTravelStatusType.TravelFailed or DCTravelStatusType.PreCheckFailed =>
                        $"传送失败: {status.CheckMessage} {status.MigrationMessage}".Trim(),
                    _ => "未知状态"
                };

                if (status.Status is DCTravelStatusType.TravelFailed or DCTravelStatusType.PreCheckFailed)
                {
                    IsTravelInProgress = false;
                    return false;
                }

                if (status.Status == DCTravelStatusType.Success)
                {
                    IsTravelInProgress = false;
                    IsTravelSuccessful = true;
                    await RefreshOrdersAsync();
                    return true;
                }

                if (status.Status == DCTravelStatusType.NeedConfirmation && !confirmationSent)
                {
                    TravelProgressText = "正在确认传送…";
                    await client.MigrationConfirmOrder(orderId, true);
                    confirmationSent = true;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                consecutiveFailures++;
                Log.Warning(ex, "[DCTravelVM] 查询订单状态失败");
                TravelProgressText = $"状态查询异常: {ex.Message}";

                if (consecutiveFailures >= 3)
                {
                    IsTravelInProgress = false;
                    return false;
                }
            }

            await Task.Delay(1000, ct);
        }

        TravelProgressText = "等待传送结果超时, 可稍后在历史记录中确认状态";
        IsTravelInProgress = false;
        return false;
    }

    private void UpdateCurrentArea(string areaName) =>
        setCurrentAreaAction(areaName);

    private void CancelPollAndReturn()
    {
        pollCts?.Cancel();
        requestBackToDashboardAction();
    }

    private async Task RefreshTravelDataAsync()
    {
        if (IsLoading) return;
        IsLoading = true;

        try
        {
            var client = getDcTravelClientFunc();
            var areas  = await client.QueryGroupListTravelSource();

            SourceAreas.Clear();
            foreach (var a in areas)
                SourceAreas.Add(a);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DCTravelVM] 加载源大区失败");
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task LoadCharactersAsync(DCTravelArea sourceArea, CancellationToken ct)
    {
        IsLoading = true;

        try
        {
            var client = getDcTravelClientFunc();
            Characters.Clear();

            foreach (var g in sourceArea.GroupList)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var chars = await client.QueryRoleList(sourceArea.AreaID, g.GroupID);

                    ct.ThrowIfCancellationRequested();

                    foreach (var c in chars)
                    {
                        c.ServerName = g.GroupName;
                        Characters.Add(c);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (DCTravelAPIException ex) when (ex.IsServiceMaintenance)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "[DCTravelVM] 加载角色失败 A={AreaID} G={GroupID}", sourceArea.AreaID, g.GroupID);
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(SelectedSourceArea, sourceArea))
                IsLoading = false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DCTravelVM] 加载角色失败");
        }
        finally
        {
            if (!ct.IsCancellationRequested && ReferenceEquals(SelectedSourceArea, sourceArea))
                IsLoading = false;
        }
    }

    private async Task LoadTargetAreasAsync(DCTravelArea sourceArea, DCTravelCharacter character)
    {
        if (IsLoading)
            return;
        IsLoading = true;

        try
        {
            var client = getDcTravelClientFunc();
            TargetAreas.Clear();

            foreach (var g in sourceArea.GroupList)
            {
                if (g.AreaID != character.AreaID || g.GroupID != character.GroupID)
                    continue;

                try
                {
                    var targets = await client.QueryGroupListTravelTarget(sourceArea.AreaID, g.GroupID);
                    if (!ReferenceEquals(SelectedSourceArea, sourceArea) || !ReferenceEquals(SelectedCharacter, character))
                        return;

                    foreach (var area in targets)
                        TargetAreas.Add(area);
                }
                catch (DCTravelAPIException ex) when (ex.IsServiceMaintenance)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "[DCTravelVM] 加载目标大区失败");
                }

                break;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DCTravelVM] 加载目标大区失败");
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void OpenReturnPage()
    {
        if (SelectedOrder == null || IsLoading)
            return;

        pendingReturnOrder     = SelectedOrder;
        pendingTargetGroupName = pendingReturnOrder.TargetGroupName;

        ReturnOrderInfo = $"{pendingReturnOrder.RoleName}  |  {pendingReturnOrder.TargetAreaName} - {pendingReturnOrder.TargetGroupName}";

        ReturnSourceAreas.Clear();
        foreach (var a in SourceAreas)
            ReturnSourceAreas.Add(a);

        var targetArea = ReturnSourceAreas.FirstOrDefault(a => a.AreaName == pendingReturnOrder.TargetAreaName);
        ReturnSelectedSourceArea = targetArea;

        requestOpenReturnAction();
    }

    private async Task ConfirmTravelBackAsync()
    {
        if (pendingReturnOrder == null || ReturnSelectedCurrentGroup == null || IsLoading) return;

        var group = ReturnSelectedCurrentGroup;

        pollCts?.Cancel();
        pollCts = new CancellationTokenSource();

        IsTravelInProgress = true;
        IsTravelSuccessful = false;
        TravelProgressText = "正在提交超域返回请求…";
        requestShowProgressAction();

        try
        {
            var client  = getDcTravelClientFunc();
            var orderId = await client.TravelBack(pendingReturnOrder.OrderID, group.GroupID, group.GroupCode, group.GroupName);

            var completed = await PollOrderStatusAsync(orderId, pollCts.Token);
            if (!completed)
                return;

            if (pendingReturnOrder != null && !string.IsNullOrWhiteSpace(pendingReturnOrder.SourceAreaName))
                UpdateCurrentArea(pendingReturnOrder.SourceAreaName);

            if (AutoStartGameOnComplete)
                onTravelCompleteAction();
            else
                activateAction();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error(ex, "[DCTravelVM] 超域返回失败");
            TravelProgressText = $"超域返回失败: {ex.Message}";
            IsTravelInProgress = false;
        }
    }

    private async Task RefreshOrdersAsync()
    {
        try
        {
            var client = getDcTravelClientFunc();

            MigrationOrders.Clear();
            var addedRoles = new HashSet<string>();
            var pageIndex  = 1;
            var totalPages = 1;

            while (pageIndex <= totalPages)
            {
                var result = await client.QueryMigrationOrders(pageIndex);
                totalPages = Math.Max(1, result.TotalPageNum);

                foreach (var order in result.Orders)
                {
                    if (!string.IsNullOrEmpty(order.ContentID) && addedRoles.Add(order.ContentID))
                        MigrationOrders.Add(order);
                }

                pageIndex++;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[DCTravelVM] 加载历史订单失败");
        }
    }
}
