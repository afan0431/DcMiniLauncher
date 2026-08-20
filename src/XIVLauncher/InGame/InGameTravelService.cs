using System.Collections.Concurrent;
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

    /// <summary>跨大区冷却 60 秒 —— 与 DcTraveler 的 <c>TravelCooldownGate</c> 一致</summary>
    private static readonly TimeSpan COOLDOWN = TimeSpan.FromSeconds(60);

    /// <summary>重试次数/间隔/是否自动换服务器都可配, 默认值与 DcTraveler 一致（见 InGameTravelSettings）</summary>
    private static TimeSpan RetryDelay    => TimeSpan.FromSeconds(InGameTravelSettings.Current.RetryDelaySeconds);
    private static int      MaxRetryCount  => InGameTravelSettings.Current.EnableAutoRetry ? InGameTravelSettings.Current.MaxRetryCount : 0;

    /// <summary>上次下单时刻（UTC ticks）。冷却是账号侧的, 所以整个启动器共用一个。</summary>
    private static long lastOrderTicks;

    /// <summary>
    ///     同一个客户端同一时刻只允许一次换服在跑（DcTraveler 用 <c>TravelRuntime</c> 的信号量做同样的事）。
    ///     按 PID 分开 —— 多开时各客户端互不影响。
    /// </summary>
    private static readonly ConcurrentDictionary<int, SemaphoreSlim> TRAVEL_GATES = new();

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

        // 同一客户端只允许一次换服在跑; 已经有一次在跑就直接拒绝, 不排队
        var gate = TRAVEL_GATES.GetOrAdd(gameProcess.Id, _ => new SemaphoreSlim(1, 1));

        if (!await gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return InGameTravelResult.Failed("这个客户端已经有一次换大区在进行中");

        try
        {
            return await TravelCoreAsync
                       (
                           gameProcess,
                           targetArea,
                           ct => SubmitWithRetryAsync(sourceGroup, targetGroup, character, progress, ct),
                           progress,
                           cancellationToken
                       ).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    ///     返回原大区。与正向的区别只在「在线那半」：不是下新单, 而是拿当初那张跨区订单提交返回
    ///     （<c>TravelBack</c>, 要带上角色**现在所在**的服务器）。进程内那半完全一样。
    ///     DcTraveler 对返回单不做自动重试（<c>DefaultTravelRetryPolicy</c>: IsBack 时 EnableRetry=false）,
    ///     这里也不重试, 但冷却照等。
    /// </summary>
    public async Task<InGameTravelResult> TravelBackAsync
    (
        Process            gameProcess,
        DCTravelGroup      currentGroup,
        string             returnOrderId,
        LoginArea          homeArea,
        IProgress<string>? progress,
        CancellationToken  cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(homeArea.AreaLobby) ||
            string.IsNullOrWhiteSpace(homeArea.AreaConfigUpload) ||
            string.IsNullOrWhiteSpace(homeArea.AreaGM))
            return InGameTravelResult.Failed($"原大区 {homeArea.AreaName} 缺少主机名信息");

        var gate = TRAVEL_GATES.GetOrAdd(gameProcess.Id, _ => new SemaphoreSlim(1, 1));

        if (!await gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return InGameTravelResult.Failed("这个客户端已经有一次换大区在进行中");

        try
        {
            return await TravelCoreAsync
                       (
                           gameProcess,
                           homeArea,
                           ct => SubmitReturnAsync(currentGroup, returnOrderId, progress, ct),
                           progress,
                           cancellationToken
                       ).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    ///     换登录大区 —— 只换用哪个大厅登录, 不动任何角色, 不产生 SDO 订单, 没有 60 秒冷却。
    ///     和超域旅行的区别只有一处: 「在线那半」什么都不干; 进程内那半
    ///     （现取票据 → 改主机名 → 作废大厅上下文 → 写票据 → 重新登录）一模一样。
    ///     <para>
    ///         卫月版 <c>GameFunctions.SelectDCAndLogin</c> 做的就是这件事, 步骤一一对应:
    ///         <c>RefreshGameSessionId</c> → <c>ChangeToSdoArea</c>（内含改主机名 +
    ///         <c>releaseLobbyContext</c> + 清 Context/State）→ <c>ChangeDEVTestSID</c> → <c>LoginInGame</c>。
    ///     </para>
    /// </summary>
    public async Task<InGameTravelResult> SwitchLoginAreaAsync
    (
        Process            gameProcess,
        LoginArea          targetArea,
        IProgress<string>? progress,
        CancellationToken  cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(targetArea.AreaLobby) ||
            string.IsNullOrWhiteSpace(targetArea.AreaConfigUpload) ||
            string.IsNullOrWhiteSpace(targetArea.AreaGM))
            return InGameTravelResult.Failed($"大区 {targetArea.AreaName} 缺少主机名信息");

        var gate = TRAVEL_GATES.GetOrAdd(gameProcess.Id, _ => new SemaphoreSlim(1, 1));

        if (!await gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return InGameTravelResult.Failed("这个客户端已经有一次换大区在进行中");

        try
        {
            return await TravelCoreAsync
                       (
                           gameProcess,
                           targetArea,
                           // 在线那半空跑 —— 换登录大区不下单
                           _ => Task.FromResult<string?>(null),
                           progress,
                           cancellationToken
                       ).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>提交返回单并轮询到完成。返回 null 表示成功, 否则是失败原因。</summary>
    private async Task<string?> SubmitReturnAsync
    (
        DCTravelGroup      currentGroup,
        string             returnOrderId,
        IProgress<string>? progress,
        CancellationToken  cancellationToken
    )
    {
        await WaitForCooldownAsync(progress, cancellationToken).ConfigureAwait(false);

        Report(progress, "正在提交返回原大区的申请…");

        string orderId;

        try
        {
            orderId = await client.TravelBack(returnOrderId, currentGroup.GroupID, currentGroup.GroupCode, currentGroup.GroupName)
                                  .ConfigureAwait(false);
            MarkOrdered();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"提交返回申请失败: {ex.Message}";
        }

        Log.Information("[InGameTravel] 返回订单号 {OrderId}（原单 {Source}, 当前服务器 {Group}）",
                        orderId, returnOrderId, currentGroup.GroupName);

        return await WaitForOrderAsync(orderId, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     换服的进程内那半 —— 正向和返回完全一样, 区别只在传进来的 <paramref name="submitAsync" />
    ///     （下新单 / 提交返回单）和落点大区。
    /// </summary>
    private async Task<InGameTravelResult> TravelCoreAsync
    (
        Process                                            gameProcess,
        LoginArea                                          targetArea,
        Func<CancellationToken, Task<string?>>             submitAsync,
        IProgress<string>?                                 progress,
        CancellationToken                                  cancellationToken
    )
    {
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

            // 片头动画/读盘期间什么都做不了（_TitleMenu 都不存在）。模块会给游戏窗口投 ESC 把动画结束掉,
            // 不需要人工干预。客户端在标题界面闲置久了就会飘进动画, 无人值守时这是常态。
            if (where.Contains("where=busy", StringComparison.Ordinal))
            {
                Report(progress, "客户端在播片头动画, 正在跳过…");
                where = await module.SendAsync("SKIPMOVIE", cancellationToken).ConfigureAwait(false);
            }

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
                // 3. 在线那半: 下新单 / 提交返回单, 然后轮询到「完成」
                var ordered = await submitAsync(cancellationToken).ConfigureAwait(false);

                if (ordered != null)
                    return InGameTravelResult.Failed(ordered);

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
    ///     下单 + 轮询, 带冷却与重试。语义抄自 DcTraveler（<c>TravelCooldownGate</c> /
    ///     <c>DefaultTravelRetryPolicy</c> / <c>TravelContextResolver.ResolveEffectiveTargetGroup</c>）：
    ///     <list type="bullet">
    ///       <item>每次下单前等满 <b>60 秒冷却</b>（基准是上一次下单时刻, 全启动器共享）</item>
    ///       <item>下单前<b>重新拉一次目标大区的服务器状态</b>, 拿到最新的 QueueTime</item>
    ///       <item>目标服务器 <c>QueueTime &lt; 0</c>（繁忙）时, 自动换成同大区里 <c>QueueTime == 0</c> 的那个</item>
    ///       <item>只有「传送失败 / 繁忙 / 稍晚再次尝试 / 用户数量较多」这类错误才重试, 最多 20 次</item>
    ///     </list>
    ///     返回 null 表示成功, 否则是失败原因。
    /// </summary>
    private async Task<string?> SubmitWithRetryAsync
    (
        DCTravelGroup      sourceGroup,
        DCTravelGroup      requestedTargetGroup,
        DCTravelCharacter  character,
        IProgress<string>? progress,
        CancellationToken  cancellationToken
    )
    {
        string? lastFailure = null;

        for (var attempt = 0; attempt <= MaxRetryCount; ++attempt)
        {
            await WaitForCooldownAsync(progress, cancellationToken).ConfigureAwait(false);

            var targetGroup = await ResolveEffectiveTargetGroupAsync(sourceGroup, requestedTargetGroup, cancellationToken)
                                  .ConfigureAwait(false);

            Report(progress, attempt == 0
                                 ? $"正在提交超域旅行订单（目标 {targetGroup.AreaName}/{targetGroup.GroupName}）…"
                                 : $"第 {attempt}/{MaxRetryCount} 次重试（目标 {targetGroup.AreaName}/{targetGroup.GroupName}）…");

            string orderId;

            try
            {
                orderId = await client.TravelOrder(targetGroup, sourceGroup, character).ConfigureAwait(false);
                MarkOrdered();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastFailure = ex.Message;

                if (!IsRetryable(lastFailure) || attempt == MaxRetryCount)
                    return $"下单失败: {lastFailure}";

                await WaitBeforeRetryAsync(lastFailure, attempt + 1, progress, cancellationToken).ConfigureAwait(false);
                continue;
            }

            Log.Information("[InGameTravel] 订单号 {OrderId}, 目标 {Area}/{Group}", orderId, targetGroup.AreaName, targetGroup.GroupName);

            lastFailure = await WaitForOrderAsync(orderId, progress, cancellationToken).ConfigureAwait(false);

            if (lastFailure == null)
                return null; // 成功

            if (!IsRetryable(lastFailure) || attempt == MaxRetryCount)
                return lastFailure;

            await WaitBeforeRetryAsync(lastFailure, attempt + 1, progress, cancellationToken).ConfigureAwait(false);
        }

        return lastFailure ?? "传送失败";
    }

    /// <summary>
    ///     下单前重新拉一次目标大区的服务器状态。<c>QueueTime</c>：<c>0</c>=即刻完成，<c>&lt;0</c>=繁忙，
    ///     <c>&gt;0</c>=预计排队 N 分钟。繁忙时自动换成同大区里通畅的那个（DcTraveler 默认也是这么做的）。
    /// </summary>
    private async Task<DCTravelGroup> ResolveEffectiveTargetGroupAsync
    (
        DCTravelGroup     sourceGroup,
        DCTravelGroup     requestedTargetGroup,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var areas = await client.QueryGroupListTravelTarget(sourceGroup.AreaID, sourceGroup.GroupID).ConfigureAwait(false);
            var area  = areas.FirstOrDefault(x => x.AreaID == requestedTargetGroup.AreaID);

            if (area == null)
                return requestedTargetGroup;

            var latest = area.GroupList.FirstOrDefault(x => x.GroupID == requestedTargetGroup.GroupID) ?? requestedTargetGroup;

            // 只有「火爆」(<0) 才换; 排队 N 分钟(>0) 说明能排上, 照常下单
            if (latest.QueueTime is not < 0 || !InGameTravelSettings.Current.AllowSwitchToAvailableWorld)
                return latest;

            var available = area.GroupList
                                .Where(x => x.QueueTime == 0 && x.GroupID != latest.GroupID)
                                .OrderBy(x => x.GroupID)
                                .FirstOrDefault();

            if (available == null)
            {
                Log.Information("[InGameTravel] {Group} 繁忙, 但同大区没有通畅的服务器, 仍按原目标下单", latest.GroupName);
                return latest;
            }

            Log.Information("[InGameTravel] {Group} 繁忙, 自动改投同大区的 {Available}", latest.GroupName, available.GroupName);
            return available;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warning(ex, "[InGameTravel] 刷新目标服务器状态失败, 按原目标下单");
            return requestedTargetGroup;
        }
    }

    /// <summary>DcTraveler 的重试判据: 只有这几类「服务侧忙」的错误才值得再试。</summary>
    private static bool IsRetryable(string message) =>
        message.Contains("传送失败", StringComparison.Ordinal)     ||
        message.Contains("繁忙", StringComparison.Ordinal)         ||
        message.Contains("请您稍晚再次尝试", StringComparison.Ordinal) ||
        message.Contains("稍晚再次尝试", StringComparison.Ordinal)   ||
        message.Contains("用户数量较多", StringComparison.Ordinal);

    private static async Task WaitBeforeRetryAsync(string failure, int attempt, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var until = DateTimeOffset.UtcNow + RetryDelay;

        while (DateTimeOffset.UtcNow < until)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var remaining = (until - DateTimeOffset.UtcNow).TotalSeconds;
            Report(progress, $"{failure} —— 第 {attempt}/{MaxRetryCount} 次重试, {remaining:F0} 秒后继续");

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     跨大区有 60 秒冷却（DcTraveler 同样是 60 秒, 基准取「上次下单」与「上次取消」中较晚者）。
    ///     这里按启动器进程记一个全局时刻 —— 冷却是账号侧的, 与开了几个客户端无关。
    /// </summary>
    private static async Task WaitForCooldownAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var last = Volatile.Read(ref lastOrderTicks);

            if (last == 0)
                return;

            var elapsed = DateTimeOffset.UtcNow - new DateTimeOffset(last, TimeSpan.Zero);

            if (elapsed >= COOLDOWN)
                return;

            var remaining = (COOLDOWN - elapsed).TotalSeconds;
            Report(progress, $"跨大区冷却中, 还需等待 {remaining:F0} 秒…");

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }
    }

    private static void MarkOrdered() =>
        Volatile.Write(ref lastOrderTicks, DateTimeOffset.UtcNow.UtcTicks);

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

            // 等的过程中也可能飘进片头动画（返回标题后闲置)，顺手让模块把它跳掉
            await module.SendAsync("SKIPMOVIE", cancellationToken).ConfigureAwait(false);

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
