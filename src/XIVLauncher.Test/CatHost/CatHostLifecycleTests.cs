using System.Diagnostics;
using System.IO;
using XIVLauncher.CatHost;
using XIVLauncher.Common.Game;
using Xunit;

namespace XIVLauncher.Test.CatHost;

/// <summary>
///     无人值守生命周期: 运行标记、崩溃对话框超时、守护出错后仍等游戏结束、关游戏
/// </summary>
public sealed class CatHostLifecycleTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public void Presence_IsVisibleWhileHeld()
    {
        CatHostPresence.Hold();

        try
        {
            Assert.True(CatHostPresence.IsAnyRunning());
        }
        finally
        {
            CatHostPresence.Release();
        }
    }

    [Fact]
    public async Task RestartMonitor_CrashHandlerOutlivesGame_ReportsCrash_ThenGivesUpAfterTimeout()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cat-crash-handler-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        // 用改名的 ping 充当崩溃处理器: 「游戏」(cmd) 起它当子进程后自己先退出, 它还留着, 跟崩溃对话框没人点一样
        var handlerExe = Path.Combine(directory, "DalamudCrashHandler.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), handlerExe);

        var startInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow  = true,
            Arguments       = $"/c start \"\" /b \"{handlerExe}\" -n 60 127.0.0.1 >nul & ping -n 3 127.0.0.1 >nul"
        };

        using var game = new FFXIVProcess(Process.Start(startInfo)!);

        var crashedPid = 0;
        var timedOut   = false;
        var restarted  = false;

        try
        {
            await new RestartMonitor().MonitorAsync
                                      (
                                          game,
                                          RestartMonitor.RestartOptions.Normal,
                                          _ =>
                                          {
                                              restarted = true;
                                              return Task.FromResult<FFXIVProcess?>(null);
                                          },
                                          CancellationToken.None,
                                          new RestartMonitor.MonitorOptions
                                          {
                                              CrashDialogGrace         = TimeSpan.FromMilliseconds(300),
                                              CrashHandlerExitTimeout  = TimeSpan.FromSeconds(1),
                                              CrashHandlerOutlivedGame = pid => crashedPid = pid,
                                              CrashHandlerTimedOut     = () => timedOut = true
                                          }
                                      )
                                      .WaitAsync(Timeout);

            Assert.Equal(game.ProcessID, crashedPid);
            Assert.True(timedOut);
            Assert.False(restarted);
            Assert.Empty(Process.GetProcessesByName("DalamudCrashHandler").Where(x => SafePath(x) == handlerExe));
        }
        finally
        {
            foreach (var process in Process.GetProcessesByName("DalamudCrashHandler"))
            {
                if (SafePath(process) == handlerExe)
                    process.Kill();
            }

            await Task.Delay(200);

            try
            {
                Directory.Delete(directory, true);
            }
            catch
            {
                // 进程刚退出时文件可能还占着
            }
        }
    }

    [Fact]
    public async Task Host_RunnerFaultAfterStart_WaitsForGameToEnd_ThenSendsGuardError()
    {
        var events = new List<(string Method, string Json)>();
        var runner = new FakeGameRunner();
        var host   = new CatLaunchHost(runner, (method, parameters) => Record(events, method, parameters), new CatLogRedactor());

        using var game = StartSleeper();

        host.Launch(new CatLaunchParams("op-g", "acc", false, null));
        await runner.Started.Task.WaitAsync(Timeout);
        runner.Reporter!.Started(game.Id, XIVLauncher.Minion.MinionOccupancy.GetProcessStartedAt(game));

        runner.Finish.SetException(new InvalidOperationException("意外"));
        await Task.Delay(300);
        Assert.False(host.Completion.IsCompleted);

        host.Close(new CatCloseParams(0));

        Assert.Equal(CatLaunchHost.EXIT_GUARD_ERROR, await host.Completion.WaitAsync(Timeout));
        Assert.True(game.HasExited);
        await host.DrainEventsAsync(Timeout);

        lock (events)
        {
            var exited = Assert.Single(events, x => x.Method == "game.exited");
            Assert.Contains("\"reason\":\"closed\"", exited.Json);
            Assert.Contains(events, x => x.Method == "launcher.log" && x.Json.Contains("守护游戏时出错"));
        }
    }

    [Fact]
    public async Task GameCloser_KillsWindowlessProcessAfterTimeout()
    {
        using var process = StartSleeper();

        Assert.True(await CatGameCloser.CloseAsync(process, TimeSpan.FromMilliseconds(200)).WaitAsync(Timeout));
        Assert.True(process.HasExited);
    }

    private static Task Record(List<(string Method, string Json)> events, string method, object parameters)
    {
        lock (events)
            events.Add((method, System.Text.Json.JsonSerializer.Serialize(parameters, CatProtocol.JsonOptions)));

        return Task.CompletedTask;
    }

    private static string? SafePath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }

    private static Process StartSleeper()
    {
        var startInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "PING.EXE"))
        {
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardOutput = true
        };
        startInfo.ArgumentList.Add("-n");
        startInfo.ArgumentList.Add("60");
        startInfo.ArgumentList.Add("127.0.0.1");

        var process = Process.Start(startInfo)!;
        process.BeginOutputReadLine();
        return process;
    }
}
