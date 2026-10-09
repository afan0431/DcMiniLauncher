using System.Text.Json;
using Serilog;

namespace XIVLauncher.CatHost;

/// <summary>
///     无界面启动的进程级状态: 退出码、stdin 握手解析、未处理异常处理
/// </summary>
public static class CatHostRuntime
{
    /// <summary>游戏已退出（任何方式）且已发 game.exited; 或只登录已完成且已发 launch.authorized; 或还没 launch 就收到 close</summary>
    public const int EXIT_OK = 0;

    /// <summary>stdin 握手无效或管道无法创建</summary>
    public const int EXIT_BOOTSTRAP_FAILED = 2;

    /// <summary>规定时间内没有收到 launch</summary>
    public const int EXIT_NO_LAUNCH = 4;

    /// <summary>已交接停止（handoff）: 游戏照常在跑, 守护记录留着, 等下一个进程 adopt; 已发 game.handedOff</summary>
    public const int EXIT_HANDED_OFF = 6;

    /// <summary>未处理异常</summary>
    public const int EXIT_UNHANDLED = 70;

    private static int fatalHandled;

    /// <summary>当前管道服务端</summary>
    public static CatRpcServer? Server { get; set; }

    /// <summary>当前方法分发器</summary>
    public static CatLaunchHost? Host { get; set; }

    /// <summary>
    ///     解析 stdin 第一行 <c>{"pipeName":"cat-dml-&lt;32位hex&gt;","token":"…"}</c>
    /// </summary>
    public static bool TryParseBootstrap(string? line, out CatBootstrap? bootstrap, out string? error)
    {
        bootstrap = null;

        if (string.IsNullOrWhiteSpace(line))
        {
            error = "stdin 没有收到握手";
            return false;
        }

        CatBootstrap? parsed;

        try
        {
            parsed = JsonSerializer.Deserialize<CatBootstrap>(line, CatProtocol.JsonOptions);
        }
        catch (JsonException ex)
        {
            error = $"握手不是有效的 JSON: {ex.Message}";
            return false;
        }

        if (parsed == null || !CatProtocol.IsValidPipeName(parsed.PipeName))
        {
            error = "握手里的 pipeName 无效（应为 cat-dml- 加 32 位十六进制）";
            return false;
        }

        if (!CatProtocol.IsValidToken(parsed.Token))
        {
            error = "握手里的 token 长度无效";
            return false;
        }

        bootstrap = parsed;
        error     = null;
        return true;
    }

    /// <summary>
    ///     未处理异常: 发 launcher.log（游戏还没起来时再发 launch.failed）, 尽量送达后以非零码退出, 不弹框
    /// </summary>
    public static void HandleFatal(Exception exception)
    {
        if (Interlocked.Exchange(ref fatalHandled, 1) == 1)
            return;

        Log.Fatal(exception, "[CatHost] 未处理的异常, 进程即将退出");

        try
        {
            var message = $"DcMiniLauncher 发生未处理的异常: {exception.GetType().Name}: {exception.Message}";

            var deliver = Task.Run
            (async () =>
                {
                    if (Host is { } host)
                    {
                        host.Log("error", message);

                        if (host.HasLaunch && !host.HasStarted && !host.IsAuthorized)
                            host.Failed(CatCodes.LAUNCH_FAILED, message);

                        await host.DrainEventsAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                    }

                    if (Server is { } server)
                        await server.WaitForDeliveryAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                }
            );

            deliver.Wait(TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[CatHost] 上报未处理异常失败");
        }

        Log.CloseAndFlush();
        Environment.Exit(EXIT_UNHANDLED);
    }

    /// <summary>
    ///     未被观察的任务异常: 只记日志并告知外壳, 不结束进程（游戏和跨区会话仍要靠本进程）
    /// </summary>
    public static void HandleUnobserved(Exception exception)
    {
        Log.Error(exception, "[CatHost] 未被观察的任务异常");

        try
        {
            Host?.Log("warning", $"后台任务异常: {exception.GetBaseException().GetType().Name}: {exception.GetBaseException().Message}");
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[CatHost] 上报后台任务异常失败");
        }
    }
}
