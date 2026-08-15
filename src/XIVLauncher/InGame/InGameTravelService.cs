using System.Diagnostics;
using Serilog;
using XIVLauncher.DCTravel;
using XIVLauncher.Login.Models;

namespace XIVLauncher.InGame;

/// <summary>一次游戏内跨大区的结果。<see cref="Message" /> 在失败时是给用户看的原因。</summary>
public sealed record InGameTravelResult(bool Ok, string Message)
{
    public static InGameTravelResult Succeeded(string areaName) => new(true, $"已换到 {areaName}");

    public static InGameTravelResult Failed(string message) => new(false, message);
}

/// <summary>
///     游戏内跨大区（F4）的编排：**不退客户端**，让已经在跑的游戏原地回到标题、换大区、重新登录。
///     只用于「只 Minion」模式 —— 注了 Dalamud 的模式由现成的 DcTraveler 插件干这件事。
///
///     职责切分（计划定案）：在线那半（下单 / 轮询 / 换 SID）留在这里，复用
///     <see cref="DCTravelClient" />；进程内那半（回标题 / 改主机名 / 作废大厅上下文 / 写 SID / 点登录）
///     由注入的 native 模块执行，本类只按顺序下命令。
///
///     时序抄自 DcTraveler（<c>TravelSession</c> + <c>DefaultTravelInteraction</c> + <c>TravelOrderMonitor</c>）：
///     ⚠ <b>先回标题、再下单</b>—— 角色得先下线，迁移才做得了。
/// </summary>
public sealed class InGameTravelService(DCTravelClient client)
{
    /// <summary>DcTraveler 的轮询间隔就是 2 秒</summary>
    private static readonly TimeSpan POLL_INTERVAL = TimeSpan.FromSeconds(2);

    /// <summary>排队最长等这么久；超时不代表失败，订单可能还在跑，去历史记录里看</summary>
    private static readonly TimeSpan ORDER_TIMEOUT = TimeSpan.FromMinutes(30);

    /// <summary>returnToTitle 是异步的，等游戏真回到标题界面</summary>
    private static readonly TimeSpan TITLE_TIMEOUT = TimeSpan.FromMinutes(2);

    private const int PIPE_CONNECT_TIMEOUT_MS = 10_000;

    /// <summary>
    ///     角色在世界里时用哪种方式登出。<c>LOGOUT</c> = 发 <c>/logout</c> 文本命令再确认对话框
    ///     （等同玩家自己操作, 最保守）；<c>LOGOUT DIRECT</c> = 直接调 <c>AgentLobby::HandleLogout</c>
    ///     （更底层, 不弹确认框）。两条都实现了, 改这一行就能换。
    /// </summary>
    private const string LOGOUT_COMMAND = "LOGOUT";

    /// <summary>状态查询连续失败这么多次就放弃（和启动器外部传送那套一致）</summary>
    private const int MAX_CONSECUTIVE_FAILURES = 3;

    public async Task<InGameTravelResult> TravelAsync
    (
        Process           gameProcess,
        DCTravelGroup     sourceGroup,
        DCTravelGroup     targetGroup,
        DCTravelCharacter character,
        LoginArea         targetArea,
        IProgress<string>? progress,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(targetArea.AreaLobby) ||
            string.IsNullOrWhiteSpace(targetArea.AreaConfigUpload) ||
            string.IsNullOrWhiteSpace(targetArea.AreaGM))
            return InGameTravelResult.Failed($"大区 {targetArea.AreaName} 缺少主机名信息");

        var injectError = MiniModuleInjector.Inject(gameProcess);

        if (injectError != null)
            return InGameTravelResult.Failed($"注入游戏内模块失败: {injectError}");

        using var module = new MiniModuleClient(gameProcess.Id);

        try
        {
            await module.ConnectAsync(PIPE_CONNECT_TIMEOUT_MS, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return InGameTravelResult.Failed($"连不上游戏内模块: {ex.Message}");
        }

        try
        {
            // 0. 先看角色在哪。
            //    ⚠ 硬约束（2026-08-15 两次实测, 两次都把客户端搞崩）: returnToTitle 属于大厅上下文,
            //      **在世界里调用必崩**（C0000005, 崩在主线程 tick 里; 换执行点没用)。
            //      DcTraveler 的判定与此一致: 只有 _CharaSelectListMenu 存在时它才 ReturnToTitle。
            //      所以角色还在游戏内时, 这里直接拒绝, 由调用方（bot / 用户）先用正常途径登出。
            var where = await module.SendAsync("WHERE", cancellationToken).ConfigureAwait(false);

            if (where.Contains("where=ingame", StringComparison.Ordinal))
            {
                // 角色还在世界里: 走游戏自己的登出流程回到角色选择界面, 再往下走。
                // 绝不能在这里直接 RETURNTITLE —— 那个在世界里调必崩。
                Report(progress, "角色在游戏内, 正在登出…");
                await CommandAsync(module, LOGOUT_COMMAND, cancellationToken).ConfigureAwait(false);

                where = await module.SendAsync("WHERE", cancellationToken).ConfigureAwait(false);

                if (where.Contains("where=ingame", StringComparison.Ordinal))
                    return InGameTravelResult.Failed("登出没成功, 角色仍在游戏内");
            }

            if (!where.Contains("where=title", StringComparison.Ordinal))
            {
                // 在角色选择界面 —— 退回标题, 换服那几步要在标题界面做
                Report(progress, "正在返回标题界面…");
                await CommandAsync(module, "RETURNTITLE", cancellationToken).ConfigureAwait(false);
            }

            if (!await WaitForTitleAsync(module, progress, cancellationToken).ConfigureAwait(false))
                return InGameTravelResult.Failed("等不到标题界面");

            // 2. 挂在标题界面会被踢, 全程保活
            await CommandAsync(module, "KEEPALIVE ON", cancellationToken).ConfigureAwait(false);

            try
            {
                // 3. 下单 + 轮询到「完成」
                Report(progress, "正在提交超域旅行订单…");
                var orderId = await client.TravelOrder(targetGroup, sourceGroup, character).ConfigureAwait(false);
                Log.Information("[InGameTravel] 订单号 {OrderId}, 目标 {Area}/{Group}", orderId, targetGroup.AreaName, targetGroup.GroupName);

                var orderFailure = await WaitForOrderAsync(orderId, progress, cancellationToken).ConfigureAwait(false);

                if (orderFailure != null)
                    return InGameTravelResult.Failed(orderFailure);

                // 4. 现取一张新票据 —— 旧的和原大区绑定, 换服后必然失效
                Report(progress, "正在换取新的登录票据…");
                var sid = await client.RefreshGameSessionId().ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(sid))
                    return InGameTravelResult.Failed("没能取到新的登录票据");

                // 5. 改主机名 → 作废大厅上下文 → 写新票据
                //    ⚠ 顺序不能动: 不做 RELEASE 的话 title→login 会复用缓存的大厅会话,
                //      改过的主机名根本不会被重读（P3 实测, 见 research/probe-P3）
                Report(progress, "正在切换大区…");
                await CommandAsync(module, $"SETHOSTS {targetArea.AreaLobby} {targetArea.AreaConfigUpload} {targetArea.AreaGM}", cancellationToken).ConfigureAwait(false);
                await CommandAsync(module, "RELEASE", cancellationToken).ConfigureAwait(false);
                await CommandAsync(module, $"SETSID {sid}", cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await TryCommandAsync(module, "KEEPALIVE OFF", cancellationToken).ConfigureAwait(false);
            }

            // 6. 点「开始游戏」
            Report(progress, "正在重新登录…");
            await CommandAsync(module, "LOGIN", cancellationToken).ConfigureAwait(false);

            Log.Information("[InGameTravel] 完成: {Area} (PID={Pid} 全程未变)", targetArea.AreaName, gameProcess.Id);
            return InGameTravelResult.Succeeded(targetArea.AreaName);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[InGameTravel] 游戏内跨大区失败");
            return InGameTravelResult.Failed(ex.Message);
        }
    }

    /// <summary>
    ///     轮询订单直到「完成」。语义与启动器外部传送那套一致（需要确认就确认, 连续异常三次放弃）。
    ///     返回 null 表示成功, 否则是失败原因。
    /// </summary>
    private async Task<string?> WaitForOrderAsync(string orderId, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var confirmationSent    = false;
        var consecutiveFailures = 0;
        var deadline            = DateTimeOffset.UtcNow + ORDER_TIMEOUT;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var status = await client.QueryOrderStatus(orderId).ConfigureAwait(false);
                consecutiveFailures = 0;

                Report(progress, status.Status switch
                {
                    DCTravelStatusType.Checking or DCTravelStatusType.CheckingAlt     => "检查目标大区角色信息中…",
                    DCTravelStatusType.NeedConfirmation                               => "等待确认传送…",
                    DCTravelStatusType.Processing or DCTravelStatusType.ProcessingAlt => "超域传送排队中…",
                    DCTravelStatusType.Success                                        => "超域传送完成",
                    _                                                                 => "超域传送中…"
                });

                switch (status.Status)
                {
                    case DCTravelStatusType.Success:
                        return null;

                    case DCTravelStatusType.TravelFailed:
                    case DCTravelStatusType.PreCheckFailed:
                        return $"传送失败: {status.CheckMessage} {status.MigrationMessage}".Trim();

                    case DCTravelStatusType.NeedConfirmation when !confirmationSent:
                        await client.MigrationConfirmOrder(orderId, true).ConfigureAwait(false);
                        confirmationSent = true;
                        break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                consecutiveFailures++;
                Log.Warning(ex, "[InGameTravel] 查询订单状态失败 ({Count}/{Max})", consecutiveFailures, MAX_CONSECUTIVE_FAILURES);

                if (consecutiveFailures >= MAX_CONSECUTIVE_FAILURES)
                    return $"状态查询连续失败: {ex.Message}";
            }

            await Task.Delay(POLL_INTERVAL, cancellationToken).ConfigureAwait(false);
        }

        return "等待传送结果超时, 订单可能仍在处理, 可稍后在历史记录中确认";
    }

    private static async Task<bool> WaitForTitleAsync(MiniModuleClient module, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TITLE_TIMEOUT;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var response = await module.SendAsync("TITLEREADY", cancellationToken).ConfigureAwait(false);

            if (response.Contains("ready=1", StringComparison.Ordinal))
                return true;

            Report(progress, "等待回到标题界面…");
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>发一条模块命令, 回应不是 OK 就抛 —— 换服这几步任何一步没成都不能继续往下走。</summary>
    private static async Task CommandAsync(MiniModuleClient module, string command, CancellationToken cancellationToken)
    {
        var response = await module.SendAsync(command, cancellationToken).ConfigureAwait(false);

        if (!response.StartsWith("OK", StringComparison.Ordinal))
        {
            // ⚠ SETSID 带着登录票据, 报错里只能出现命令名
            var safeName = command.Split(' ')[0];
            throw new InvalidOperationException($"模块命令 {safeName} 失败: {response}");
        }
    }

    private static async Task TryCommandAsync(MiniModuleClient module, string command, CancellationToken cancellationToken)
    {
        try
        {
            await module.SendAsync(command, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[InGameTravel] 收尾命令 {Command} 失败, 忽略", command);
        }
    }

    private static void Report(IProgress<string>? progress, string message)
    {
        Log.Information("[InGameTravel] {Message}", message);
        progress?.Report(message);
    }
}
