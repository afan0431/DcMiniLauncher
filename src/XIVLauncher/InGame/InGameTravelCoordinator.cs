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

            // 注了 Dalamud 的客户端由现成的 DcTraveler 插件换服, 我们不去抢
            if (game.Agents.HasFlag(InGameAgents.Dalamud))
                return Failed("该客户端注了 Dalamud, 请用 DcTraveler 插件换大区");

            var context = await ResolveContextAsync(request, cancellationToken).ConfigureAwait(false);

            if (context.Error != null)
                return Failed(context.Error);

            var service = new InGameTravelService(client);

            var result = await service.TravelAsync
                               (
                                   game.Process,
                                   context.SourceGroup!,
                                   context.TargetGroup!,
                                   context.Character!,
                                   context.TargetArea!,
                                   null,
                                   cancellationToken
                               ).ConfigureAwait(false);

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

    private sealed record TravelContext
    (
        DCTravelGroup?     SourceGroup,
        DCTravelGroup?     TargetGroup,
        DCTravelCharacter? Character,
        LoginArea?         TargetArea,
        string?            Error
    )
    {
        public static TravelContext Failed(string error) => new(null, null, null, null, error);
    }

    private async Task<TravelContext> ResolveContextAsync(DCTravelListener.InGameTravelRequest request, CancellationToken cancellationToken)
    {
        var sourceAreas = await client.QueryGroupListTravelSource().ConfigureAwait(false);

        // 1. 找角色 —— 顺带就确定了它在哪个大区哪个服务器（= 源服务器）
        DCTravelCharacter? character   = null;
        DCTravelGroup?     sourceGroup = null;
        var                candidates  = new List<string>();

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
                {
                    candidates.Add($"{role.Name}@{group.GroupName}");

                    if (!string.IsNullOrWhiteSpace(request.Character) &&
                        !string.Equals(role.Name, request.Character, StringComparison.Ordinal))
                        continue;

                    // 没指定角色名时只允许有一个候选, 免得挑错人把别的号换走
                    if (character != null)
                        return TravelContext.Failed($"这个账号下有多个角色, 请指定 character: {string.Join(", ", candidates)}");

                    character   = role;
                    sourceGroup = group;
                }
            }
        }

        if (character == null || sourceGroup == null)
            return TravelContext.Failed(candidates.Count == 0
                                            ? "没查到任何可传送的角色"
                                            : $"没找到角色 {request.Character}, 可选: {string.Join(", ", candidates)}");

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
