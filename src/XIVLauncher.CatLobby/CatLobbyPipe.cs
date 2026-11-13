using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Dalamud.Plugin.Services;
using XIVLauncher.CatLobby.Game;
using XIVLauncher.CatLobby.Protocol;

namespace XIVLauncher.CatLobby;

/// <summary>
///     命名管道服务端 <c>\\.\pipe\minilauncher-&lt;pid&gt;</c>: 消息模式, 一次一个客户端, 读一条 UTF-8 命令回一条 UTF-8 回应,
///     客户端断开后等下一个。读写游戏状态的操作都放到框架线程上执行, 等待有上限。
/// </summary>
internal sealed class CatLobbyPipe : IDisposable
{
    /// <summary>读写游戏状态最多等框架线程多久（与国服小模块的主线程等待一致）</summary>
    private static readonly TimeSpan FrameworkTimeout = TimeSpan.FromSeconds(3);

    /// <summary>创建管道失败（名字被占着）后隔多久重试</summary>
    private static readonly TimeSpan CreateRetry = TimeSpan.FromSeconds(1);

    /// <summary>SKIPMOVIE 最多投几次 ESC、每次隔多久</summary>
    private const int SKIP_MOVIE_ATTEMPTS = 20;

    private static readonly TimeSpan SkipMovieInterval = TimeSpan.FromMilliseconds(500);

    private const int MAX_REQUEST_BYTES = 4096;

    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_KEYUP   = 0x0101;
    private const int  VK_ESCAPE  = 0x1B;

    private readonly IFramework              framework;
    private readonly LobbyGame               game;
    private readonly IPluginLog              log;
    private readonly string                  logPath;
    private readonly int                     pid = Environment.ProcessId;
    private readonly CancellationTokenSource stop = new();
    private          Task?                   loop;

    /// <summary>每条命令上一次的回应, 只在管道线程上读写</summary>
    private readonly Dictionary<string, string> lastReplies = new();

    public CatLobbyPipe(IFramework framework, LobbyGame game, IPluginLog log, string logPath)
    {
        this.framework = framework;
        this.game      = game;
        this.log       = log;
        this.logPath   = logPath;
    }

    public string PipeName => $"minilauncher-{pid}";

    public void Start() => loop = Task.Run(RunAsync);

    public void Dispose()
    {
        stop.Cancel();

        try
        {
            loop?.Wait(TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            log.Debug(ex, "等管道线程退出时出错");
        }

        stop.Dispose();
    }

    private async Task RunAsync()
    {
        var token = stop.Token;

        while (!token.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;

            try
            {
                server = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Message, PipeOptions.Asynchronous,
                                                   LobbyReplies.MAX_RESPONSE_BYTES, LobbyReplies.MAX_RESPONSE_BYTES);

                await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                await ServeSessionAsync(server, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // 名字被上一次加载留下的实例占着等情况: 隔一会儿再建
                log.Warning(ex, "管道 {Name} 出错, 稍后重试", PipeName);

                try
                {
                    await Task.Delay(CreateRetry, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            finally
            {
                server?.Dispose();
            }
        }
    }

    private async Task ServeSessionAsync(NamedPipeServerStream server, CancellationToken token)
    {
        var buffer = new byte[MAX_REQUEST_BYTES];

        while (server.IsConnected && !token.IsCancellationRequested)
        {
            var length = 0;

            do
            {
                var read = await server.ReadAsync(buffer.AsMemory(length), token).ConfigureAwait(false);

                if (read <= 0)
                    return;

                length += read;
            }
            while (!server.IsMessageComplete && length < buffer.Length);

            var command  = Encoding.UTF8.GetString(buffer, 0, length);
            var response = await HandleAsync(command, token).ConfigureAwait(false);
            var payload  = Encoding.UTF8.GetBytes(response);

            if (payload.Length > LobbyReplies.MAX_RESPONSE_BYTES)
                payload = Encoding.UTF8.GetBytes("FAIL too-large");

            Trace(command.Trim(), response);

            await server.WriteAsync(payload, token).ConfigureAwait(false);
            await server.FlushAsync(token).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     记一条命令和它的回应。编排是轮询的, 同一条命令回应没变时只记调试级, 变了才记信息级,
    ///     这样默认日志级别下就能从 dalamud.log 看出自动进入角色每一步停在哪
    /// </summary>
    private void Trace(string command, string response)
    {
        var changed = !lastReplies.TryGetValue(command, out var last) || last != response;

        if (lastReplies.Count > 64)
            lastReplies.Clear();

        lastReplies[command] = response;

        if (changed)
            log.Information("[pipe] < {Command} | > {Response}", command, LobbyTextRules.Sanitize(response, 1500));
        else
            log.Debug("[pipe] < {Command} | > {Response}", command, response.Split('\n')[0]);
    }

    /// <summary>执行一条命令, 返回回应。任何异常都转成 FAIL exception, 不让管道线程退出</summary>
    internal async Task<string> HandleAsync(string raw, CancellationToken token)
    {
        try
        {
            var command = LobbyCommand.Parse(raw);

            switch (command.Kind)
            {
                case LobbyCommandKind.Ping:
                    return "OK PONG";

                case LobbyCommandKind.Version:
                    return LobbyReplies.Version(pid, logPath);

                case LobbyCommandKind.BadUsage:
                    return command.Usage;

                case LobbyCommandKind.NotImplemented:
                    return LobbyReplies.NOT_IMPLEMENTED;

                case LobbyCommandKind.LobbyState:
                    return await OnFrameworkAsync(() => game.ReadLobbyState() is { } lobby ? LobbyReplies.LobbyState(lobby) : LobbyReplies.POINTERS_UNAVAILABLE, token).ConfigureAwait(false);

                case LobbyCommandKind.TitleReady:
                    return await OnFrameworkAsync(() => LobbyReplies.TitleReady(game.TitleReady()), token).ConfigureAwait(false);

                case LobbyCommandKind.Login:
                    return await OnFrameworkAsync(() => LobbyReplies.Login(game.TitleLogin()), token).ConfigureAwait(false);

                case LobbyCommandKind.SkipMovie:
                    return await SkipMovieAsync(token).ConfigureAwait(false);

                case LobbyCommandKind.Charas:
                    return await OnFrameworkAsync(() => game.ReadCharas(out var failure) is { } list ? LobbyReplies.Charas(list) : failure ?? LobbyReplies.EXCEPTION, token).ConfigureAwait(false);

                case LobbyCommandKind.WhoAmI:
                    return await OnFrameworkAsync(() => game.ReadWhoAmI(out var failure) is { } who ? LobbyReplies.WhoAmI(who) : failure ?? LobbyReplies.EXCEPTION, token).ConfigureAwait(false);

                case LobbyCommandKind.FocusChara:
                    return await OnFrameworkAsync(() => LobbyReplies.Focus(game.Focus(command.Who, command.ContentId)), token).ConfigureAwait(false);

                case LobbyCommandKind.SelectChara:
                    return await OnFrameworkAsync(() => LobbyReplies.CharaAction(game.CharaAction(command.Who, command.ContentId, false)), token).ConfigureAwait(false);

                case LobbyCommandKind.EnterChara:
                    return await OnFrameworkAsync(() => LobbyReplies.CharaAction(game.CharaAction(command.Who, command.ContentId, true)), token).ConfigureAwait(false);

                case LobbyCommandKind.DialogYes:
                case LobbyCommandKind.DialogNo:
                case LobbyCommandKind.DialogOk:
                case LobbyCommandKind.DialogNotice:
                    return await OnFrameworkAsync(() => game.Dialog(command.Kind, command.ContentId), token).ConfigureAwait(false);

                // 国服专用或与进角色无关的命令（BILLING / LEVELS / DUTY / EXIT / CHARCONFIG …）一律不认
                default:
                    return LobbyReplies.UNKNOWN_COMMAND;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.Error(ex, "处理命令出错: {Command}", raw);
            return LobbyReplies.EXCEPTION;
        }
    }

    /// <summary>
    ///     在框架线程上跑 <paramref name="work" />, 最多等 <see cref="FrameworkTimeout" />; 超时回 mainthread-timeout。
    ///     超时后排着的那一份作废（<c>abandoned</c>）, 免得它过后才执行、动了游戏却没人收结果
    /// </summary>
    private async Task<string> OnFrameworkAsync(Func<string> work, CancellationToken token)
    {
        var abandoned = false;

        var task = framework.RunOnFrameworkThread(() =>
        {
            if (abandoned)
                return LobbyReplies.MAINTHREAD_TIMEOUT;

            try
            {
                return work();
            }
            catch (Exception ex)
            {
                log.Error(ex, "框架线程上的命令出错");
                return LobbyReplies.EXCEPTION;
            }
        });

        try
        {
            return await task.WaitAsync(FrameworkTimeout, token).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            abandoned = true;
            return LobbyReplies.MAINTHREAD_TIMEOUT;
        }
    }

    /// <summary>
    ///     结束片头动画: 游戏没有「跳过动画」的可调函数, 任意输入就结束, 所以隔一会儿给游戏窗口投一次 ESC（异步消息, 不占框架线程）,
    ///     直到回到标题 / 选角 / 游戏里。未实机
    /// </summary>
    private async Task<string> SkipMovieAsync(CancellationToken token)
    {
        var window = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;

        if (window == IntPtr.Zero)
            return "FAIL no-window";

        for (var attempt = 0; attempt < SKIP_MOVIE_ATTEMPTS; attempt++)
        {
            var where = LobbyWhere.Unknown;
            var reply = await OnFrameworkAsync(() =>
            {
                where = game.ReadWhere();
                return string.Empty;
            }, token).ConfigureAwait(false);

            if (reply.Length == 0 && where is LobbyWhere.Title or LobbyWhere.CharaSelect or LobbyWhere.InGame)
                return $"OK where={LobbyWhereNames.Name(where)}";

            PostMessage(window, WM_KEYDOWN, VK_ESCAPE, (nint)0x00010001);
            PostMessage(window, WM_KEYUP, VK_ESCAPE, unchecked((nint)0xC0010001u));

            await Task.Delay(SkipMovieInterval, token).ConfigureAwait(false);
        }

        return "FAIL still-busy";
    }

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, nint wParam, nint lParam);
}
