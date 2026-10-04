using System.Diagnostics;
using System.IO;
using XIVLauncher.Minion;

namespace XIVLauncher.CatHost;

/// <summary>
///     模拟模式: 不登录、不启动真游戏, 用一个占位子进程代替游戏, 按真实时序发事件。
///     <list type="bullet">
///         <item>accountName 以 <c>fail:</c> 开头时按其后的失败码发 launch.failed（如 <c>fail:authorizationRequired</c>）</item>
///         <item>minion.cardFingerprint 为 <c>0000000000000000</c> 时发 launch.failed{minionCardNotFound}</item>
///         <item>占位进程被结束时发 game.exited, 随后本进程退出</item>
///     </list>
/// </summary>
public sealed class CatSimulatedGameRunner : ICatGameRunner
{
    /// <summary>模拟中代表"找不到卡"的指纹</summary>
    public const string MISSING_CARD_FINGERPRINT = "0000000000000000";

    private const string FAIL_PREFIX = "fail:";

    private Process? placeholder;

    /// <summary>每个阶段之间的停顿</summary>
    public TimeSpan StepDelay { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>占位进程的创建方式, 默认 <c>ping -t 127.0.0.1</c></summary>
    public Func<Process> PlaceholderFactory { get; init; } = StartDefaultPlaceholder;

    /// <inheritdoc />
    public async Task<int> RunAsync(CatLaunchRequest request, ICatLaunchReporter reporter, CancellationToken cancellationToken)
    {
        reporter.Log("information", "模拟模式: 不登录、不启动真游戏");
        reporter.Stage(CatStages.PREPARING);
        await Task.Delay(StepDelay, cancellationToken).ConfigureAwait(false);

        if (request.AccountName.StartsWith(FAIL_PREFIX, StringComparison.Ordinal))
        {
            var code = request.AccountName[FAIL_PREFIX.Length..];
            reporter.Failed(string.IsNullOrWhiteSpace(code) ? CatCodes.LAUNCH_FAILED : code, "模拟的启动失败");
            return CatLaunchHost.EXIT_LAUNCH_FAILED;
        }

        if (request.Minion && request.CardFingerprint == MISSING_CARD_FINGERPRINT)
        {
            reporter.Failed(CatCodes.MINION_CARD_NOT_FOUND, "本机 Minion Accounts.json 里找不到这张卡对应的行（模拟）");
            return CatLaunchHost.EXIT_LAUNCH_FAILED;
        }

        if (request.Dalamud)
        {
            reporter.Stage(CatStages.UPDATING_DALAMUD);
            await Task.Delay(StepDelay, cancellationToken).ConfigureAwait(false);
        }

        reporter.Stage(CatStages.STARTING);
        await Task.Delay(StepDelay, cancellationToken).ConfigureAwait(false);

        var process = PlaceholderFactory();
        placeholder = process;
        var pid = process.Id;
        reporter.Started(pid, MinionOccupancy.GetProcessStartedAt(process));

        if (request.Dalamud)
        {
            reporter.Stage(CatStages.INJECTING);
            await Task.Delay(StepDelay, cancellationToken).ConfigureAwait(false);
            reporter.Agent(CatAgentKinds.DALAMUD, true);
        }

        if (request.Minion)
        {
            reporter.Stage(CatStages.ATTACHING_MINION);
            await Task.Delay(StepDelay, cancellationToken).ConfigureAwait(false);
            reporter.Agent(CatAgentKinds.MINION, true);
        }

        reporter.Stage(CatStages.RUNNING);

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        int? exitCode;

        try
        {
            exitCode = process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            exitCode = null;
        }

        reporter.Exited(pid, exitCode);
        process.Dispose();
        return 0;
    }

    /// <inheritdoc />
    public async Task InjectAsync(bool dalamud, bool minion, ICatLaunchReporter reporter, CancellationToken cancellationToken)
    {
        if (placeholder is not { HasExited: false })
        {
            if (dalamud)
                reporter.Agent(CatAgentKinds.DALAMUD, false, CatCodes.NOT_RUNNING, "占位进程已退出");
            if (minion)
                reporter.Agent(CatAgentKinds.MINION, false, CatCodes.NOT_RUNNING, "占位进程已退出");
            return;
        }

        await Task.Delay(StepDelay, cancellationToken).ConfigureAwait(false);

        if (dalamud)
            reporter.Agent(CatAgentKinds.DALAMUD, true);
        if (minion)
            reporter.Agent(CatAgentKinds.MINION, true);
    }

    private static Process StartDefaultPlaceholder()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName               = Path.Combine(Environment.SystemDirectory, "PING.EXE"),
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            RedirectStandardInput  = true
        };
        startInfo.ArgumentList.Add("-t");
        startInfo.ArgumentList.Add("127.0.0.1");

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动占位进程");

        // 占位进程的输出不能进外壳读的标准输出, 这里读掉丢弃
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived  += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }
}
