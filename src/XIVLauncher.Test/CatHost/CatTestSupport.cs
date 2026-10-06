using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using XIVLauncher.CatHost;

namespace XIVLauncher.Test.CatHost;

/// <summary>
///     测试用的 dml-cat/1 客户端: 连管道、发请求、收事件
/// </summary>
internal sealed class CatTestClient : IAsyncDisposable
{
    private readonly NamedPipeClientStream                               pipe;
    private readonly StreamReader                                        reader;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonNode>> pending = new();
    private readonly SemaphoreSlim                                       writeLock = new(1, 1);
    private readonly Task                                                readLoop;
    private          int                                                 nextId;

    private CatTestClient(NamedPipeClientStream pipe)
    {
        this.pipe = pipe;
        reader    = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, true);
        readLoop  = Task.Run(ReadLoopAsync);
    }

    /// <summary>收到的事件（方法名, 参数）</summary>
    public BlockingCollection<(string Method, JsonNode? Params)> Events { get; } = new();

    /// <summary>对方关闭连接时完成</summary>
    public TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>底层管道</summary>
    public NamedPipeClientStream Pipe => pipe;

    public static async Task<CatTestClient> ConnectAsync(string pipeName, TimeSpan timeout)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var cts = new CancellationTokenSource(timeout);
        await pipe.ConnectAsync(cts.Token);
        return new CatTestClient(pipe);
    }

    public async Task<JsonNode> RequestAsync(string method, object? parameters, TimeSpan? timeout = null)
    {
        var id  = Interlocked.Increment(ref nextId);
        var tcs = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = tcs;

        var frame = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters ?? new { } });
        await WriteLineAsync(frame);

        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        await using (cts.Token.Register(() => tcs.TrySetCanceled()))
            return await tcs.Task;
    }

    public async Task WriteLineAsync(string line)
    {
        await writeLock.WaitAsync();

        try
        {
            var bytes = Encoding.UTF8.GetBytes(line + "\n");
            await pipe.WriteAsync(bytes);
            await pipe.FlushAsync();
        }
        finally
        {
            writeLock.Release();
        }
    }

    /// <summary>
    ///     等下一个指定方法名的事件, 之前的其它事件一并返回在 seen 里
    /// </summary>
    public (string Method, JsonNode? Params) WaitForEvent(string method, TimeSpan timeout, List<(string Method, JsonNode? Params)>? seen = null)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (true)
        {
            var remaining = deadline - DateTime.UtcNow;

            if (remaining <= TimeSpan.Zero || !Events.TryTake(out var item, remaining))
                throw new TimeoutException($"没有等到事件 {method}");

            seen?.Add(item);

            if (item.Method == method)
                return item;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await pipe.DisposeAsync();

        try
        {
            await readLoop.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // ignored
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (true)
            {
                var line = await reader.ReadLineAsync();

                if (line == null)
                    break;

                var node = JsonNode.Parse(line)!;

                if (node["id"] is { } idNode && idNode.GetValueKind() == JsonValueKind.Number && node["method"] == null)
                {
                    if (pending.TryRemove(idNode.GetValue<int>(), out var tcs))
                        tcs.TrySetResult(node);
                }
                else if (node["method"] is { } methodNode)
                    Events.Add((methodNode.GetValue<string>(), node["params"]));
            }
        }
        catch
        {
            // 连接断开
        }
        finally
        {
            Closed.TrySetResult();
        }
    }
}

/// <summary>
///     记录所有报告的假启动器
/// </summary>
internal sealed class FakeGameRunner : ICatGameRunner
{
    public TaskCompletionSource<int> Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public CatLaunchRequest? Request { get; private set; }

    public ICatLaunchReporter? Reporter { get; private set; }

    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<int> RunAsync(CatLaunchRequest request, ICatLaunchReporter reporter, CancellationToken cancellationToken)
    {
        Request  = request;
        Reporter = reporter;
        Started.TrySetResult();
        return Finish.Task;
    }

    public TimeSpan? CloseTimeout { get; private set; }

    public TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task InjectAsync(bool dalamud, bool minion, bool force, ICatLaunchReporter reporter, CancellationToken cancellationToken)
    {
        if (dalamud)
            reporter.Agent(CatAgentKinds.DALAMUD, true);
        if (minion)
            reporter.Agent(CatAgentKinds.MINION, true, force ? null : CatCodes.ALREADY_ATTACHED);
        return Task.CompletedTask;
    }

    public Task CloseAsync(TimeSpan gracefulTimeout)
    {
        CloseTimeout = gracefulTimeout;
        Closed.TrySetResult();
        return Task.CompletedTask;
    }

    /// <summary>收到的 weGame.confirmSms 的编号</summary>
    public List<string> ConfirmedSms { get; } = [];

    public CatAcceptResult ConfirmWeGameSms(string challengeId)
    {
        ConfirmedSms.Add(challengeId);
        return challengeId == "s-1" ? CatAcceptResult.Ok() : CatAcceptResult.Rejected(CatCodes.NOT_RUNNING, "这条短信验证已经不在了");
    }

    /// <summary>收到的 selectCharacter 的 contentId</summary>
    public List<string> SelectedCharacters { get; } = [];

    public CatAcceptResult SelectCharacter(string contentId)
    {
        SelectedCharacters.Add(contentId);
        return CatAcceptResult.Ok();
    }
}

/// <summary>
///     按顺序记录报告内容的 reporter
/// </summary>
internal sealed class RecordingReporter : ICatLaunchReporter
{
    public ConcurrentQueue<string> Entries { get; } = new();

    public int? Pid { get; private set; }

    public TaskCompletionSource Running { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Stage(string stage)
    {
        Entries.Enqueue($"stage:{stage}");

        if (stage == CatStages.RUNNING)
            Running.TrySetResult();
    }

    public void Started(int pid, DateTimeOffset processStartedAt)
    {
        Pid = pid;
        Entries.Enqueue("started");
    }

    public void Restarted(int oldPid, int pid, DateTimeOffset processStartedAt)
    {
        Pid = pid;
        Entries.Enqueue("restarted");
        Restart.TrySetResult();
    }

    public TaskCompletionSource Restart { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Crashed(int pid) =>
        Entries.Enqueue("crashed");

    public void Agent(string kind, bool ok, string? code = null, string? message = null)
    {
        Entries.Enqueue($"agent:{kind}:{(ok ? "ok" : code)}");

        if (message != null)
            Messages.Enqueue(message);
    }

    public void Exited(int pid, int? exitCode, string? reason = null, string? code = null, string? message = null)
    {
        Entries.Enqueue(reason == null ? "exited" : $"exited:{reason}");

        if (message != null)
            Messages.Enqueue(message);
    }

    public void Failed(string code, string message)
    {
        Entries.Enqueue($"failed:{code}");
        Messages.Enqueue(message);
    }

    /// <summary>所有带文字的报告（日志、失败、代理、退出）里的文字, 查敏感值有没有漏出来用</summary>
    public ConcurrentQueue<string> Messages { get; } = new();

    public void Log(string level, string message) =>
        Messages.Enqueue(message);

    /// <summary>报出来的验证, 按顺序</summary>
    public ConcurrentQueue<CatWeGameChallenge> Challenges { get; } = new();

    public void WeGameChallenge(CatWeGameChallenge challenge)
    {
        Challenges.Enqueue(challenge);
        Entries.Enqueue($"challenge:{challenge.Kind}:{challenge.ChallengeId}");
    }

    public void WeGameChallengeCleared(string challengeId) =>
        Entries.Enqueue($"cleared:{challengeId}");

    public void WeGameScanSwitchFailed(string scan) =>
        Entries.Enqueue($"scanSwitchFailed:{scan}");

    public void WeGameSmsResult(string challengeId, bool passed) =>
        Entries.Enqueue($"smsResult:{challengeId}:{(passed ? "passed" : "notPassed")}");

    /// <summary>报出来的角色列表, 按顺序</summary>
    public ConcurrentQueue<(bool NeedsChoice, IReadOnlyList<CatCharacterInfo> Characters)> CharacterLists { get; } = new();

    /// <summary>报出来的已进入游戏的角色, 按顺序</summary>
    public ConcurrentQueue<CatCharacterInfo> EnteredCharacters { get; } = new();

    public void Queueing(int? queuePosition) =>
        Entries.Enqueue($"queue:{queuePosition?.ToString() ?? "?"}");

    public void Characters(bool needsChoice, IReadOnlyList<CatCharacterInfo> characters)
    {
        CharacterLists.Enqueue((needsChoice, characters));
        Entries.Enqueue($"characters:{(needsChoice ? "choose" : "auto")}:{characters.Count}");
    }

    public void Character(CatCharacterInfo character)
    {
        EnteredCharacters.Enqueue(character);
        Entries.Enqueue($"character:{character.Name}@{character.HomeWorld}");
    }

    public void AutoEnterStopped(string code, string message)
    {
        Entries.Enqueue($"stopped:{code}");
        Messages.Enqueue(message);
    }
}

internal static class CatTestNames
{
    public static string NewPipeName() => CatProtocol.PIPE_NAME_PREFIX + Guid.NewGuid().ToString("N");

    public static string NewToken() => Convert.ToHexString(Guid.NewGuid().ToByteArray()) + Convert.ToHexString(Guid.NewGuid().ToByteArray());
}
