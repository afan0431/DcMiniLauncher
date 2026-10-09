using System.Diagnostics;
using System.IO;
using XIVLauncher.Common.Game;
using Xunit;

namespace XIVLauncher.Test.CatHost;

/// <summary>
///     接管时的崩溃重开: 开始监视时游戏已经退出、只剩它的崩溃处理器, 也要读到重开决定
/// </summary>
public sealed class RestartMonitorAdoptTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>托管重启「正常重启」的退出码 0x12345671</summary>
    private const uint RESTART_NORMAL = 0x12345671;

    [Fact]
    public async Task GameAlreadyExited_OrphanCrashHandler_RestartDecisionIsHonoured()
    {
        var directory = Path.Combine(Path.GetTempPath(), "dml-orphan-handler-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        // 改名的 cmd 充当崩溃处理器: 等 2 秒后以「正常重启」的退出码退出
        var handlerExe = Path.Combine(directory, "DalamudCrashHandler.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), handlerExe);

        // 「游戏」起它当子进程后立刻退出
        var startInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow  = true,
            Arguments       = $"/c start \"\" /b \"{handlerExe}\" /c \"ping -n 3 127.0.0.1 >nul & exit {RESTART_NORMAL}\" & exit 3"
        };

        using var game = new FFXIVProcess(Process.Start(startInfo)!);
        await game.UnderlyingProcess.WaitForExitAsync().WaitAsync(Timeout);

        RestartMonitor.RestartOptions? decided = null;

        try
        {
            await new RestartMonitor().MonitorAsync
                                      (
                                          game,
                                          new RestartMonitor.RestartOptions(false, true, false),
                                          options =>
                                          {
                                              decided = options;
                                              return Task.FromResult<FFXIVProcess?>(null);
                                          },
                                          CancellationToken.None,
                                          new RestartMonitor.MonitorOptions
                                          {
                                              CrashHandlerDiscoveryTimeout = TimeSpan.Zero,
                                              CrashDialogGrace             = TimeSpan.FromSeconds(10),
                                              CrashHandlerExitTimeout      = TimeSpan.FromSeconds(10)
                                          }
                                      )
                                      .WaitAsync(Timeout);

            Assert.Equal(RestartMonitor.RestartOptions.Normal, decided);
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
}
