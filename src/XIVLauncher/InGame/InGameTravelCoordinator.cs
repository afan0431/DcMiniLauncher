using System.Diagnostics;
using System.Text.Json;
using Serilog;
using XIVLauncher.DCTravel;
using XIVLauncher.Login.Models;

namespace XIVLauncher.InGame;

/// <summary>
///     把「换到 XX 大区」这种人话解析成 <see cref="InGameTravelService" /> 需要的四件套
///     （源服务器 / 目标服务器 / 角色 / 目标大区主机名), 然后跑一次游戏内换大区。
///     解析步骤与启动器自己的超域传送页面一致（同样是 QueryGroupListTravelSource →
///     QueryRoleList → QueryGroupListTravelTarget), 只是这里没有界面, 全靠名字匹配。
/// </summary>
public sealed class InGameTravelCoordinator(DCTravelClient client)
{
    /// <summary>跨区订单的 <c>travelStatus</c>: 1 = 已抵达（人还在做客地）, 其余 = 已返回。
    ///     判据同启动器自己的订单列表 <c>DCTravelHistorySlide.xaml</c>。</summary>
    private const int TRAVEL_STATUS_ARRIVED = 1;

    public async Task<DCTravelListener.InGameTravelResponse> HandleAsync
    (
        DCTravelListener.InGameTravelRequest request,
        CancellationToken                    cancellationToken
    )
    {
        try
        {
            var game = RunningGameRegistry.Resolve(request.Pid, out var resolveError);

            if (game == null)
                return Failed(resolveError ?? "找不到目标客户端");

            // 「都注」也走这条路: 用户要的是两个平台共存时 mini 这套照样可用。
            // 注了 Dalamud 时 DcTraveler 插件也能换服, 两者别同时用就行（这里不去替用户拦）。
            if (!game.Agents.HasFlag(InGameAgents.Minion))
                return Failed("该客户端没有挂 Minion, 游戏内模块不会被注入");

            if (InGameTravelJobs.IsRunning(game.Process.Id))
                return Failed("这个客户端已经有一次换大区在进行中");

            var context = request.Back
                              ? await ResolveReturnContextAsync(request, cancellationToken).ConfigureAwait(false)
                              : await ResolveContextAsync(request, cancellationToken).ConfigureAwait(false);

            if (context.Error != null)
                return Failed(context.Error);

            var pid    = game.Process.Id;
            var target = $"{context.TargetArea!.AreaName}/{context.TargetGroup!.GroupName}";

            InGameTravelJobs.Begin(pid, target);

            var run = RunAsync(game.Process, context, request.Back);

            // 默认不等: 整个流程要几十秒到几分钟, 游戏内 UI 靠 /ingame-travel/status 轮询进度。
            // 想同步等结果（比如手工测试）就传 "wait": true
            if (!request.Wait)
                return new DCTravelListener.InGameTravelResponse { Ok = true, Message = $"已开始: {target}（用 /dctravel/ingame-travel/status 查进度）" };

            var result = await run.ConfigureAwait(false);
            return new DCTravelListener.InGameTravelResponse { Ok = result.Ok, Message = result.Message };
        }
        catch (OperationCanceledException)
        {
            return Failed("已取消");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[InGameTravel] 解析或执行换大区请求失败");
            return Failed(ex.Message);
        }
    }

    /// <summary>
    ///     游戏内 UI 用: 报告<b>这个角色</b>现在的处境, 以及（在家时）它能去哪。
    ///     <paramref name="character" /> 由游戏内那侧给（Minion Lua 的 <c>Player.name</c>）——
    ///     谁在玩只有游戏进程自己知道, 问启动器的启动记录问不出来。
    ///     <c>queueTime</c>: 0=通畅, &lt;0=繁忙, &gt;0=预计排队分钟数。
    /// </summary>
    public async Task<object> QueryTargetsAsync(string? character, CancellationToken cancellationToken)
    {
        var (state, error) = await ResolveCharacterStateAsync(character, cancellationToken).ConfigureAwait(false);

        if (state == null)
            return new { ok = false, message = error ?? "拿不到角色信息" };

        // 做客中: 只能返回原大区, 给目标列表没意义 —— 空着, 游戏内 UI 靠 away 决定画什么
        if (state.Away)
            return new
            {
                ok        = true,
                character = state.Name,
                area      = state.AreaName,
                group     = state.GroupName,
                away      = true,
                homeArea  = state.Order!.SourceAreaName,
                homeGroup = state.Order.SourceGroupName,
                areas     = Array.Empty<object>()
            };

        var targets = await client.QueryGroupListTravelTarget(state.Group!.AreaID, state.Group.GroupID).ConfigureAwait(false);

        return new
        {
            ok        = true,
            character = state.Name,
            area      = state.AreaName,
            group     = state.GroupName,
            away      = false,
            homeArea  = "",
            homeGroup = "",
            areas = targets.Select(area => new
            {
                area = area.AreaName,
                // 大区级拥挤度: 0=通畅 1=热门 2=火爆, 其它=繁忙（词表抄 DcTraveler 的 WindowStyles.GetAreaStatus）
                stateCode = area.State,
                state = area.State switch
                {
                    0 => "通畅",
                    1 => "热门",
                    2 => "火爆",
                    _ => "繁忙"
                },
                groups = area.GroupList.Select(group => new
                {
                    group     = group.GroupName,
                    queueTime = group.QueueTime,
                    // 服务器级词表同样抄 DcTraveler 的 WindowStyles.GetQueueStatus,
                    // 免得游戏内 UI 再抄一遍判定规则（抄一遍就会有一天对不上）
                    state = group.QueueTime switch
                    {
                        null    => "读取中",
                        0       => "通畅",
                        < 0     => "火爆",
                        var min => $"{min} 分钟"
                    }
                })
            })
        };
    }

    /// <summary>
    ///     读/写换大区的行为设置（四项与 DcTraveler 设置页一一对应）。
    ///     <paramref name="body" /> 为 null 表示只读, 否则整份写入后返回最新值。
    /// </summary>
    public static object HandleSettings(string? body)
    {
        if (!string.IsNullOrWhiteSpace(body))
        {
            var incoming = JsonSerializer.Deserialize<InGameTravelSettings>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (incoming != null)
            {
                InGameTravelSettings.Current.Apply(incoming);
                Log.Information("[InGameTravel] 设置已更新: 自动重试={Retry} 繁忙自动换服={Switch} 最大重试={Max} 间隔={Delay}s",
                                InGameTravelSettings.Current.EnableAutoRetry,
                                InGameTravelSettings.Current.AllowSwitchToAvailableWorld,
                                InGameTravelSettings.Current.MaxRetryCount,
                                InGameTravelSettings.Current.RetryDelaySeconds);
            }
        }

        var settings = InGameTravelSettings.Current;

        return new
        {
            ok                          = true,
            enableAutoRetry             = settings.EnableAutoRetry,
            allowSwitchToAvailableWorld = settings.AllowSwitchToAvailableWorld,
            maxRetryCount               = settings.MaxRetryCount,
            retryDelaySeconds           = settings.RetryDelaySeconds
        };
    }

    /// <summary>跑一次换服, 把每一步的进度写进 <see cref="InGameTravelJobs" /> 供 UI 轮询。</summary>
    private async Task<InGameTravelResult> RunAsync(Process gameProcess, TravelContext context, bool isBack)
    {
        var pid      = gameProcess.Id;
        var progress = new Progress<string>(text => InGameTravelJobs.Report(pid, text));

        try
        {
            var service = new InGameTravelService(client);

            var result = isBack
                             ? await service.TravelBackAsync(gameProcess, context.SourceGroup!, context.ReturnOrderId!,
                                                             context.TargetArea!, progress, CancellationToken.None).ConfigureAwait(false)
                             : await service.TravelAsync(gameProcess, context.SourceGroup!, context.TargetGroup!,
                                                         context.Character!, context.TargetArea!, progress, CancellationToken.None).ConfigureAwait(false);

            InGameTravelJobs.End(pid, result.Ok, result.Message);
            return result;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[InGameTravel] 换大区任务异常");
            InGameTravelJobs.End(pid, false, ex.Message);
            return InGameTravelResult.Failed(ex.Message);
        }
    }

    /// <summary>
    ///     「返回原大区」的解析。和正向不同, 它不需要选目标 —— 目标就是当初出发的那个大区,
    ///     记在跨区订单里（<c>QueryMigrationOrders</c> 的 <c>sourceAreaName</c>）。
    ///     提交时要带上角色**现在所在**的服务器（<c>TravelBack</c> 的 groupId/Code/Name）。
    /// </summary>
    private async Task<TravelContext> ResolveReturnContextAsync(DCTravelListener.InGameTravelRequest request, CancellationToken cancellationToken)
    {
        var (state, error) = await ResolveCharacterStateAsync(request.Character, cancellationToken).ConfigureAwait(false);

        if (state == null)
            return TravelContext.Failed(error!);

        if (!state.Away)
            return TravelContext.Failed($"角色 {state.Name} 没在做客 —— 它本来就在原大区");

        if (state.Group == null)
            return TravelContext.Failed($"拿不到 {state.AreaName}/{state.GroupName} 的服务器信息, 提交不了返回请求");

        var order      = state.Order!;
        var loginAreas = await LoginArea.Get().ConfigureAwait(false);
        var homeArea   = loginAreas.FirstOrDefault(x => string.Equals(x.AreaName, order.SourceAreaName, StringComparison.Ordinal));

        if (homeArea == null)
            return TravelContext.Failed($"拿不到原大区 {order.SourceAreaName} 的登录主机名");

        Log.Information("[InGameTravel] 返回原大区: {Character}@{Current} → {Home}/{HomeGroup} (订单 {OrderId})",
                        state.Name, state.GroupName, order.SourceAreaName, order.SourceGroupName, order.OrderID);

        // TargetGroup 这里只用于显示; 真正提交返回单只需要当前服务器 + 订单号
        var homeGroup = new DCTravelGroup
        {
            AreaID    = 0,
            AreaName  = order.SourceAreaName,
            GroupName = order.SourceGroupName,
            GroupCode = string.Empty
        };

        return new TravelContext(state.Group, homeGroup, state.Character, homeArea, null) { ReturnOrderId = order.OrderID };
    }

    private sealed record TravelContext
    (
        DCTravelGroup?     SourceGroup,
        DCTravelGroup?     TargetGroup,
        DCTravelCharacter? Character,
        LoginArea?         TargetArea,
        string?            Error
    )
    {
        /// <summary>「返回原大区」时要提交的那张跨区订单号</summary>
        public string? ReturnOrderId { get; init; }

        public static TravelContext Failed(string error) => new(null, null, null, null, error);
    }

    /// <summary>
    ///     角色现在的处境。<b>做客中的角色不在 <c>queryRoleList4Migration</c> 里</b> ——
    ///     SDO 不让做客的角色再发起新的超域旅行, 那份「可迁移角色」列表里根本没有它,
    ///     只能从跨区订单（<c>travelStatus == 1</c> = 已抵达）看出来。所以两个来源都查, 先查订单。
    /// </summary>
    private sealed record CharacterState
    {
        public required string Name { get; init; }

        /// <summary>现在人在哪个大区/服务器 —— 做客中就是做客地, 不是原大区</summary>
        public required string AreaName { get; init; }

        public required string GroupName { get; init; }

        /// <summary>现在所在服务器的完整对象; 提交请求要 GroupID/GroupCode, 光有名字不够</summary>
        public DCTravelGroup? Group { get; init; }

        /// <summary>在家时才有 —— 下新单要 ContentID</summary>
        public DCTravelCharacter? Character { get; init; }

        /// <summary>做客中才有 —— 返回原大区要 OrderID 和订单里记的原大区</summary>
        public DCTravelMigrationOrder? Order { get; init; }

        /// <summary>做客中: 只能返回原大区, 不能直接再跨去别处</summary>
        public bool Away => Order != null;
    }

    /// <summary>
    ///     查清楚角色现在什么处境。<paramref name="wantedName" /> 由游戏内那侧给（Minion Lua 的
    ///     <c>Player.name</c>）—— 它比启动器的任何记录都准: 启动器只知道「这个客户端是我起的、
    ///     登录的哪个大区」, 而角色一旦做客, 登录大区和它实际所在的大区就是两回事了。
    /// </summary>
    private async Task<(CharacterState? State, string? Error)> ResolveCharacterStateAsync
    (
        string?           wantedName,
        CancellationToken cancellationToken
    )
    {
        var sourceAreas = await client.QueryGroupListTravelSource().ConfigureAwait(false);

        var (order, orderError) = await FindAwayOrderAsync(wantedName, cancellationToken).ConfigureAwait(false);

        if (orderError != null)
            return (null, orderError);

        if (order != null)
        {
            // 订单里的 target 才是它现在待的地方, source 是家
            var current = FindGroup(sourceAreas, order.TargetAreaName, order.TargetGroupName);

            return (new CharacterState
            {
                Name      = order.RoleName,
                AreaName  = order.TargetAreaName,
                GroupName = order.TargetGroupName,
                Group     = current,
                Order     = order
            }, null);
        }

        // 不在做客 —— 挨个源服务器问「可迁移角色」
        var found = new List<(DCTravelCharacter Role, DCTravelGroup Group)>();

        foreach (var area in sourceAreas)
        {
            foreach (var group in area.GroupList)
            {
                cancellationToken.ThrowIfCancellationRequested();

                List<DCTravelCharacter> roles;

                try
                {
                    roles = await client.QueryRoleList(area.AreaID, group.GroupID).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "[InGameTravel] 查角色失败 A={AreaID} G={GroupID}, 跳过", area.AreaID, group.GroupID);
                    continue;
                }

                foreach (var role in roles)
                    found.Add((role, group));
            }
        }

        var listed = string.Join(", ", found.Select(x => $"{x.Role.Name}@{x.Group.GroupName}"));

        var matched = string.IsNullOrWhiteSpace(wantedName)
                          ? found
                          : found.Where(x => string.Equals(x.Role.Name, wantedName, StringComparison.Ordinal)).ToList();

        if (matched.Count == 0)
            return (null, string.IsNullOrWhiteSpace(wantedName)
                              ? "没查到任何可传送的角色"
                              : $"没找到角色 {wantedName}（它可能正在做客, 也可能不在可超域的服务器上）, 可选: {listed}");

        if (matched.Count > 1)
            return (null, $"匹配到多个角色, 请指定 character: {listed}");

        var (hit, hitGroup) = matched[0];

        return (new CharacterState
        {
            Name      = hit.Name,
            AreaName  = hitGroup.AreaName,
            GroupName = hitGroup.GroupName,
            Group     = hitGroup,
            Character = hit
        }, null);
    }

    /// <summary>
    ///     翻跨区订单, 找「还在做客」的那一张。<c>travelStatus == 1</c> = 已抵达（人在做客地）,
    ///     其余是已返回的历史单 —— 不筛这个就会抓到早就返回过的旧单去提交返回请求。
    /// </summary>
    private async Task<(DCTravelMigrationOrder? Order, string? Error)> FindAwayOrderAsync
    (
        string?           wantedName,
        CancellationToken cancellationToken
    )
    {
        var away = new List<DCTravelMigrationOrder>();

        for (var page = 1; page <= 20; ++page)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var orders = await client.QueryMigrationOrders(page).ConfigureAwait(false);

            away.AddRange(orders.Orders.Where(x => x.TravelStatus == TRAVEL_STATUS_ARRIVED));

            if (page >= orders.TotalPageNum)
                break;
        }

        if (!string.IsNullOrWhiteSpace(wantedName))
            return (away.FirstOrDefault(x => string.Equals(x.RoleName, wantedName, StringComparison.Ordinal)), null);

        return away.Count switch
        {
            0 => (null, null),
            1 => (away[0], null),
            _ => (null, $"有多个做客中的角色, 请指定 character: {string.Join(", ", away.Select(x => x.RoleName))}")
        };
    }

    private static DCTravelGroup? FindGroup(List<DCTravelArea> areas, string areaName, string groupName) =>
        areas.FirstOrDefault(x => string.Equals(x.AreaName, areaName, StringComparison.Ordinal))
            ?.GroupList.FirstOrDefault(x => string.Equals(x.GroupName, groupName, StringComparison.Ordinal));

    private async Task<TravelContext> ResolveContextAsync(DCTravelListener.InGameTravelRequest request, CancellationToken cancellationToken)
    {
        // 1. 找角色 —— 顺带就确定了它现在在哪个大区哪个服务器（= 源服务器）
        var (state, error) = await ResolveCharacterStateAsync(request.Character, cancellationToken).ConfigureAwait(false);

        if (state == null)
            return TravelContext.Failed(error!);

        // 做客中不能再直接跨去别处, 得先回家 —— 这是 SDO 的规则, 不是我们加的限制
        if (state.Away)
            return TravelContext.Failed($"角色 {state.Name} 正在 {state.AreaName}/{state.GroupName} 做客, "
                                        + $"要去别的大区得先返回原大区 {state.Order!.SourceAreaName}/{state.Order.SourceGroupName}");

        var character   = state.Character!;
        var sourceGroup = state.Group!;
        // 2. 目标大区/服务器
        var targetAreas = await client.QueryGroupListTravelTarget(sourceGroup.AreaID, sourceGroup.GroupID).ConfigureAwait(false);

        var targetArea = targetAreas.FirstOrDefault(x => string.Equals(x.AreaName, request.Area, StringComparison.Ordinal));

        if (targetArea == null)
            return TravelContext.Failed($"目标大区 {request.Area} 不在可选列表里, 可选: {string.Join(", ", targetAreas.Select(x => x.AreaName))}");

        var targetGroup = string.IsNullOrWhiteSpace(request.Group)
                              ? targetArea.GroupList.FirstOrDefault()
                              : targetArea.GroupList.FirstOrDefault(x => string.Equals(x.GroupName, request.Group, StringComparison.Ordinal));

        if (targetGroup == null)
            return TravelContext.Failed(string.IsNullOrWhiteSpace(request.Group)
                                            ? $"大区 {request.Area} 下没有可选服务器"
                                            : $"服务器 {request.Group} 不在 {request.Area} 的可选列表里, 可选: {string.Join(", ", targetArea.GroupList.Select(x => x.GroupName))}");

        // 3. 目标大区的登录主机名 —— 和启动器塞进 XL.LobbyHosts 给插件用的是同一份数据
        var loginAreas = await LoginArea.Get().ConfigureAwait(false);
        var loginArea  = loginAreas.FirstOrDefault(x => string.Equals(x.AreaName, targetArea.AreaName, StringComparison.Ordinal));

        if (loginArea == null)
            return TravelContext.Failed($"拿不到大区 {targetArea.AreaName} 的登录主机名");

        Log.Information("[InGameTravel] 解析完成: {Character}@{SourceGroup} → {TargetArea}/{TargetGroup} ({Lobby})",
                        character.Name, sourceGroup.GroupName, targetArea.AreaName, targetGroup.GroupName, loginArea.AreaLobby);

        return new TravelContext(sourceGroup, targetGroup, character, loginArea, null);
    }

    private static DCTravelListener.InGameTravelResponse Failed(string message)
    {
        Log.Warning("[InGameTravel] {Message}", message);
        return new DCTravelListener.InGameTravelResponse { Ok = false, Message = message };
    }
}
