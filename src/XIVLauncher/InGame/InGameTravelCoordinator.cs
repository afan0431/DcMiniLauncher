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
    /// <summary>
    ///     跨区订单的 <c>travelStatus</c>: 1 = 这一单送到过。
    ///     ⚠ <b>它不等于「角色现在还在做客地」</b> —— 送到之后这个值再也不会变, 而官方对超过一天
    ///     没登录的角色会自动遣返, 订单状态不跟着改。所以判断角色此刻在哪只能看
    ///     <c>queryRoleList4Migration</c>（人在哪个服务器就出现在哪个服务器下）, 订单只用来取返回票。
    /// </summary>
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

            var (legs, planError) = await PlanAsync(request, cancellationToken).ConfigureAwait(false);

            if (planError != null)
                return Failed(planError);

            var pid    = game.Process.Id;
            var last   = legs[^1].Context;
            var target = $"{last.TargetArea!.AreaName}/{last.TargetGroup!.GroupName}";

            InGameTravelJobs.Begin(pid, target);

            var run = RunLegsAsync(game.Process, legs);

            // 默认不等: 一段就要几十秒到几分钟(含排队与 60 秒冷却), 两段更久,
            // 游戏内 UI 靠 /ingame-travel/status 轮询进度。想同步等结果就传 "wait": true
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
    ///     游戏内 UI 用: 报告<b>这个角色</b>现在的处境, 以及（在原始大区时）它能去哪。
    ///     三个参数都由游戏内那侧给（<c>Player.name</c> + <c>FFXIVLib.API.World.GetWorldById</c>
    ///     解出的当前世界名 / 原始世界名）—— 谁在玩、人在哪, 只有游戏进程自己知道。
    ///     <c>queueTime</c>: 0=通畅, &lt;0=繁忙, &gt;0=预计排队分钟数。
    /// </summary>
    public async Task<object> QueryTargetsAsync
    (
        string?           character,
        string?           world,
        string?           homeWorld,
        CancellationToken cancellationToken
    )
    {
        var (state, error) = await ResolveCharacterStateAsync(character, world, homeWorld, cancellationToken).ConfigureAwait(false);

        if (state == null)
            return new { ok = false, message = error ?? "拿不到角色信息" };

        // 超域中: 只能超域返回, 给目标列表没意义 —— 空着, 游戏内 UI 靠 away 决定画什么
        if (state.Away)
            return new
            {
                ok        = true,
                character = state.Name,
                area      = state.CurrentGroup.AreaName,
                group     = state.CurrentGroup.GroupName,
                away      = true,
                visiting  = false,
                homeArea  = state.HomeGroup.AreaName,
                homeGroup = state.HomeGroup.GroupName,
                areas     = Array.Empty<object>()
            };

        // 跨界传送中: 也去不了 —— 得先在游戏内返回原始世界。同样不给目标列表。
        if (state.Visiting)
            return new
            {
                ok        = true,
                character = state.Name,
                area      = state.CurrentGroup.AreaName,
                group     = state.CurrentGroup.GroupName,
                away      = false,
                visiting  = true,
                homeArea  = state.HomeGroup.AreaName,
                homeGroup = state.HomeGroup.GroupName,
                areas     = Array.Empty<object>()
            };

        // SDO 的超域业务以**原始服务器**为源 —— 角色跨界传送到同大区别的世界时也一样
        var targets = await client.QueryGroupListTravelTarget(state.HomeGroup.AreaID, state.HomeGroup.GroupID).ConfigureAwait(false);

        return new
        {
            ok        = true,
            character = state.Name,
            area      = state.CurrentGroup.AreaName,
            group     = state.CurrentGroup.GroupName,
            away      = false,
            visiting  = false,
            homeArea  = state.HomeGroup.AreaName,
            homeGroup = state.HomeGroup.GroupName,
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
    ///     换登录大区（标题界面用）。<b>和超域旅行是两回事</b>: 只换用哪个大厅登录,
    ///     不动任何角色、不产生 SDO 订单、没有 60 秒冷却。所以这里不需要知道角色是谁,
    ///     也不需要查任何服务器 —— 只要目标大区的名字。
    /// </summary>
    public async Task<DCTravelListener.InGameTravelResponse> HandleSwitchAreaAsync
    (
        DCTravelListener.SwitchAreaRequest request,
        CancellationToken                  cancellationToken
    )
    {
        try
        {
            var game = RunningGameRegistry.Resolve(request.Pid, out var resolveError);

            if (game == null)
                return Failed(resolveError ?? "找不到目标客户端");

            if (!game.Agents.HasFlag(InGameAgents.Minion))
                return Failed("该客户端没有挂 Minion, 游戏内模块不会被注入");

            if (InGameTravelJobs.IsRunning(game.Process.Id))
                return Failed("这个客户端已经有一次换大区在进行中");

            if (string.IsNullOrWhiteSpace(request.Area))
                return Failed("没给目标大区");

            var loginAreas = await LoginArea.Get().ConfigureAwait(false);
            var targetArea = loginAreas.FirstOrDefault(x => string.Equals(x.AreaName, request.Area, StringComparison.Ordinal));

            if (targetArea == null)
                return Failed($"没有大区 {request.Area}, 可选: {string.Join(", ", loginAreas.Select(x => x.AreaName))}");

            var pid = game.Process.Id;
            InGameTravelJobs.Begin(pid, targetArea.AreaName);

            var run = RunSwitchAsync(game.Process, targetArea);

            // 默认不等: 整个流程十几秒, 游戏内 UI 靠 /ingame-travel/status 轮询进度
            if (!request.Wait)
                return new DCTravelListener.InGameTravelResponse { Ok = true, Message = $"已开始: 切换到 {targetArea.AreaName}" };

            var result = await run.ConfigureAwait(false);
            return new DCTravelListener.InGameTravelResponse { Ok = result.Ok, Message = result.Message };
        }
        catch (OperationCanceledException)
        {
            return Failed("已取消");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[InGameTravel] 换登录大区失败");
            return Failed(ex.Message);
        }
    }

    /// <summary>可切换的登录大区列表。纯本地数据, 不联网、不涉及角色。</summary>
    public static async Task<object> QueryLoginAreasAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;

        var areas = await LoginArea.Get().ConfigureAwait(false);

        return new
        {
            ok    = true,
            areas = areas.Select(x => new { area = x.AreaName })
        };
    }

    /// <summary>跑一次换登录大区, 进度同样写进 <see cref="InGameTravelJobs" /> 供 UI 轮询。</summary>
    private async Task<InGameTravelResult> RunSwitchAsync(Process gameProcess, LoginArea targetArea)
    {
        var pid      = gameProcess.Id;
        var progress = new Progress<string>(text => InGameTravelJobs.Report(pid, text));

        try
        {
            var service = new InGameTravelService(client);
            var result  = await service.SwitchLoginAreaAsync(gameProcess, targetArea, progress, CancellationToken.None).ConfigureAwait(false);

            InGameTravelJobs.End(pid, result.Ok, result.Message);
            return result;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[InGameTravel] 换登录大区任务异常");
            InGameTravelJobs.End(pid, false, ex.Message);
            return InGameTravelResult.Failed(ex.Message);
        }
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

    /// <summary>一段行程。超域中要去别的大区时是两段: 先超域返回, 再超域旅行。</summary>
    private sealed record TravelLeg(bool IsBack, TravelContext Context, string Describe);

    /// <summary>
    ///     按顺序跑完所有段, 每一步的进度写进 <see cref="InGameTravelJobs" /> 供 UI 轮询。
    ///     任何一段失败就整体停在那里 —— 不假装成功, 也不继续往下跑。
    /// </summary>
    private async Task<InGameTravelResult> RunLegsAsync(Process gameProcess, IReadOnlyList<TravelLeg> legs)
    {
        var pid = gameProcess.Id;

        try
        {
            var service = new InGameTravelService(client);
            var result  = InGameTravelResult.Failed("没有任何行程");

            for (var i = 0; i < legs.Count; ++i)
            {
                var leg = legs[i];

                // 多段时把「第几段」缀在每条进度前面, 单段就不啰嗦
                var prefix   = legs.Count > 1 ? $"[{i + 1}/{legs.Count} {leg.Describe}] " : string.Empty;
                var progress = new Progress<string>(text => InGameTravelJobs.Report(pid, prefix + text));
                var context  = leg.Context;

                result = leg.IsBack
                             ? await service.TravelBackAsync(gameProcess, context.SourceGroup!, context.ReturnOrderId!,
                                                             context.TargetArea!, progress, CancellationToken.None).ConfigureAwait(false)
                             : await service.TravelAsync(gameProcess, context.SourceGroup!, context.TargetGroup!,
                                                         context.Character!, context.TargetArea!, progress, CancellationToken.None).ConfigureAwait(false);

                if (!result.Ok)
                {
                    var message = legs.Count > 1 ? $"{prefix}{result.Message}" : result.Message;
                    InGameTravelJobs.End(pid, false, message);
                    return InGameTravelResult.Failed(message);
                }
            }

            InGameTravelJobs.End(pid, true, result.Message);
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
    ///     把一次请求拆成要跑的几段。
    ///     <list type="bullet">
    ///       <item>在原始大区 → 1 段: 超域旅行</item>
    ///       <item>超域中 + 请求返回 → 1 段: 超域返回</item>
    ///       <item>超域中 + 要去别的大区 → 2 段: 超域返回 → （冷却 60 秒）→ 超域旅行</item>
    ///       <item>跨界传送中 → 拒绝: SDO 不接受从做客世界发起超域, 得先在游戏内返回原始世界</item>
    ///     </list>
    ///     两段的目标都能**提前**定下来: SDO 的超域业务一律以原始服务器为源, 而
    ///     <c>queryRoleList4Migration</c> 按原始服务器列角色 —— 角色人还在做客地时,
    ///     它的 roleId 照样查得到。所以不必等第一段跑完再解析第二段。
    /// </summary>
    private async Task<(IReadOnlyList<TravelLeg> Legs, string? Error)> PlanAsync
    (
        DCTravelListener.InGameTravelRequest request,
        CancellationToken                    cancellationToken
    )
    {
        var (state, stateError) = await ResolveCharacterStateAsync(request.Character, request.World, request.HomeWorld, cancellationToken)
                                      .ConfigureAwait(false);

        if (state == null)
            return ([], stateError);

        // 跨界传送中: 人在同大区的别的世界。SDO 不接受从做客世界发起超域,
        // 必须先在游戏内(主城大水晶)返回原始世界 —— 那一步这里做不了。
        if (state.Visiting)
            return ([], $"角色 {state.Name} 正跨界传送在 {state.CurrentGroup.GroupName}, "
                        + $"要超域得先在游戏内返回原始世界 {state.HomeGroup.GroupName}（主城大水晶）");

        var legs = new List<TravelLeg>();

        if (state.Away)
        {
            var back = await ResolveReturnContextAsync(state, cancellationToken).ConfigureAwait(false);

            if (back.Error != null)
                return ([], back.Error);

            legs.Add(new TravelLeg(true, back, "超域返回"));

            // 只想返回就到此为止
            if (request.Back)
                return (legs, null);
        }
        else if (request.Back)
        {
            return ([], $"角色 {state.Name} 没在超域 —— 它本来就在原始大区");
        }

        var forward = await ResolveForwardContextAsync(request, state, cancellationToken).ConfigureAwait(false);

        if (forward.Error != null)
            return ([], forward.Error);

        legs.Add(new TravelLeg(false, forward, "超域旅行"));
        return (legs, null);
    }

    /// <summary>
    ///     「返回原大区」的解析。和正向不同, 它不需要选目标 —— 目标就是当初出发的那个大区,
    ///     记在跨区订单里（<c>QueryMigrationOrders</c> 的 <c>sourceAreaName</c>）。
    ///     提交时要带上角色**现在所在**的服务器（<c>TravelBack</c> 的 groupId/Code/Name）。
    /// </summary>
    private async Task<TravelContext> ResolveReturnContextAsync(CharacterState state, CancellationToken cancellationToken)
    {
        var order = await FindReturnTicketAsync(state.Name, state.CurrentGroup, cancellationToken).ConfigureAwait(false);

        if (order == null)
            return TravelContext.Failed($"没找到角色 {state.Name} 在 {state.CurrentGroup.AreaName}/{state.CurrentGroup.GroupName} 的超域订单");

        var loginAreas = await LoginArea.Get().ConfigureAwait(false);
        var homeArea   = loginAreas.FirstOrDefault(x => string.Equals(x.AreaName, order.SourceAreaName, StringComparison.Ordinal));

        if (homeArea == null)
            return TravelContext.Failed($"拿不到原始大区 {order.SourceAreaName} 的登录主机名");

        Log.Information("[InGameTravel] 超域返回: {Character}@{Current} → {Home}/{HomeGroup} (订单 {OrderId})",
                        state.Name, state.CurrentGroup.GroupName, order.SourceAreaName, order.SourceGroupName, order.OrderID);

        // TargetGroup 这里只用于显示; 真正提交返回单只需要当前服务器 + 订单号
        var homeGroup = new DCTravelGroup
        {
            AreaID    = 0,
            AreaName  = order.SourceAreaName,
            GroupName = order.SourceGroupName,
            GroupCode = string.Empty
        };

        return new TravelContext(state.CurrentGroup, homeGroup, null, homeArea, null) { ReturnOrderId = order.OrderID };
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
    ///     角色现在的处境。<b>位置只能来自游戏内存</b> ——
    ///     游戏内是 <c>Player.currentworld</c> / <c>homeworld</c>，选角界面是
    ///     <c>CharaSelectCharacterEntry</c>。SDO 那边给不了：
    ///     <c>queryRoleList4Migration</c> 是按**原始服务器**列角色的，
    ///     角色超域出去之后它照样把角色列在原始服务器下（2026-08-20 实测）。
    /// </summary>
    private sealed record CharacterState
    {
        public required string Name { get; init; }

        /// <summary>角色**现在**所在的服务器（超域中就是做客地）</summary>
        public required DCTravelGroup CurrentGroup { get; init; }

        /// <summary>角色的原始服务器。SDO 的超域业务全部以它为源。</summary>
        public required DCTravelGroup HomeGroup { get; init; }

        /// <summary>超域中 = 现在所在的**大区**不是原始大区。只能超域返回。</summary>
        public bool Away => !string.Equals(CurrentGroup.AreaName, HomeGroup.AreaName, StringComparison.Ordinal);

        /// <summary>
        ///     跨界传送中 = 同一个大区内做客别的服务器。这不是超域,
        ///     但 SDO 同样不接受从做客世界发起超域 —— 必须先在游戏内(主城大水晶)返回原始世界。
        ///     卫月版的处理是把「超域旅行」菜单项置灰（<c>IsEnabled = currentWorldId == homeWorldId</c>）。
        /// </summary>
        public bool Visiting => !Away && !string.Equals(CurrentGroup.GroupName, HomeGroup.GroupName, StringComparison.Ordinal);
    }

    /// <summary>
    ///     按游戏内报上来的世界名定位角色。<paramref name="world" /> / <paramref name="homeWorld" />
    ///     由游戏内那侧算好（<c>FFXIVLib.API.World.GetWorldById</c>），
    ///     这里只把名字映射成 SDO 的服务器对象（要 GroupID / GroupCode 才能提交请求）。
    ///     <para>
    ///         只发一次 <c>queryGroupListTravelSource</c>。原先那套「挨个服务器问可迁移角色」
    ///         的扫描（4 大区 28 个服务器、串行十几秒）已删除 —— 它不但慢，查的还是另一回事。
    ///     </para>
    /// </summary>
    private async Task<(CharacterState? State, string? Error)> ResolveCharacterStateAsync
    (
        string?           name,
        string?           world,
        string?           homeWorld,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(name))
            return (null, "拿不到角色名（游戏内那侧没报上来）");

        if (string.IsNullOrWhiteSpace(world) || string.IsNullOrWhiteSpace(homeWorld))
            return (null, "拿不到角色所在的世界（进游戏后再试）");

        var sourceAreas = await client.QueryGroupListTravelSource().ConfigureAwait(false);

        var homeGroup = FindGroupByName(sourceAreas, homeWorld);

        if (homeGroup == null)
            return (null, $"SDO 的服务器列表里没有 {homeWorld}");

        // 做客世界一定也在源列表里（那份列表含全部四个大区的全部服务器）；
        // 万一没有就退回原始服务器，至少不会把状态判反。
        var currentGroup = FindGroupByName(sourceAreas, world) ?? homeGroup;

        return (new CharacterState
        {
            Name         = name,
            CurrentGroup = currentGroup,
            HomeGroup    = homeGroup
        }, null);
    }

    /// <summary>
    ///     找「角色此刻正超域所凭的那张单」—— 提交超域返回要它的 OrderID。
    ///     判据是<b>位置对得上</b>：这一单的目的地就是角色现在所在的服务器。
    ///     <para>
    ///         为什么不能只看 <c>travelStatus == 1</c>: 那个值送达后永不改变，而官方对超过一天
    ///         没登录的角色会自动遣返 —— 角色早回去了，订单还写着「已抵达」。
    ///     </para>
    ///     订单列表新单在前，取第一条对得上的。
    /// </summary>
    private async Task<DCTravelMigrationOrder?> FindReturnTicketAsync
    (
        string            roleName,
        DCTravelGroup     current,
        CancellationToken cancellationToken
    )
    {
        for (var page = 1; page <= 20; ++page)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var orders = await client.QueryMigrationOrders(page).ConfigureAwait(false);

            foreach (var order in orders.Orders)
            {
                if (order.TravelStatus != TRAVEL_STATUS_ARRIVED)
                    continue;

                if (!string.Equals(order.RoleName, roleName, StringComparison.Ordinal))
                    continue;

                if (!string.Equals(order.TargetAreaName,  current.AreaName,  StringComparison.Ordinal) ||
                    !string.Equals(order.TargetGroupName, current.GroupName, StringComparison.Ordinal))
                    continue;

                return order;
            }

            if (page >= orders.TotalPageNum)
                break;
        }

        return null;
    }

    /// <summary>在原始服务器上把角色查出来 —— 下新单要 <c>roleId</c>。只问这一个服务器。</summary>
    private async Task<DCTravelCharacter?> FindCharacterAsync(string roleName, DCTravelGroup homeGroup)
    {
        List<DCTravelCharacter> roles;

        try
        {
            roles = await client.QueryRoleList(homeGroup.AreaID, homeGroup.GroupID).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[InGameTravel] 查角色失败 A={AreaID} G={GroupID}", homeGroup.AreaID, homeGroup.GroupID);
            return null;
        }

        return roles.FirstOrDefault(x => string.Equals(x.Name, roleName, StringComparison.Ordinal));
    }

    private static DCTravelGroup? FindGroupByName(List<DCTravelArea> areas, string groupName) =>
        areas.SelectMany(x => x.GroupList)
             .FirstOrDefault(x => string.Equals(x.GroupName, groupName, StringComparison.Ordinal));

    private async Task<TravelContext> ResolveForwardContextAsync
    (
        DCTravelListener.InGameTravelRequest request,
        CharacterState                       state,
        CancellationToken                    cancellationToken
    )
    {
        // SDO 的超域业务一律以原始服务器为源 —— 角色此刻在不在原始大区都一样。
        // 超域中时这一段是「第二段」, 跑之前第一段已经把角色送回原始大区了。
        var sourceGroup = state.HomeGroup;
        var character   = await FindCharacterAsync(state.Name, sourceGroup).ConfigureAwait(false);

        if (character == null)
            return TravelContext.Failed($"在 {sourceGroup.AreaName}/{sourceGroup.GroupName} 上没查到角色 {state.Name}");
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
