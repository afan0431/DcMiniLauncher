using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Xunit.Abstractions;

namespace XIVLauncher.Test.CatHost;

/// <summary>
///     端到端: 以子进程方式启动 XIVLauncherCN.exe --cat-launch --cat-simulate, 走 stdin 握手 → 连管道 → hello → launch
///     → 收到 started/agent → 杀占位进程 → 收到 exited 且进程退出。
/// </summary>
public sealed class CatLaunchProcessTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task SimulatedLaunch_FullLifecycle_OverStdinAndPipe()
    {
        var exe = FindLauncherExe();
        Assert.True(exe != null, "找不到 XIVLauncherCN.exe, 先编译 XIVLauncher 项目");

        var roamingPath = Path.Combine(Path.GetTempPath(), "cat-dml-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(roamingPath);

        var pipeName = CatTestNames.NewPipeName();
        var token    = CatTestNames.NewToken();

        var startInfo = new ProcessStartInfo(exe!)
        {
            UseShellExecute        = false,
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            WorkingDirectory       = Path.GetDirectoryName(exe)!
        };
        startInfo.ArgumentList.Add("--cat-launch");
        startInfo.ArgumentList.Add("--cat-simulate");
        startInfo.ArgumentList.Add($"--roamingPath={roamingPath}");

        using var launcher = Process.Start(startInfo)!;
        launcher.BeginOutputReadLine();
        launcher.BeginErrorReadLine();

        try
        {
            await launcher.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { pipeName, token }));
            await launcher.StandardInput.FlushAsync();

            await using var client = await ConnectWithRetryAsync(pipeName);

            Assert.True(GetNamedPipeServerProcessId(client.Pipe.SafePipeHandle.DangerousGetHandle(), out var serverPid));
            Assert.Equal((uint)launcher.Id, serverPid);

            var hello = await client.RequestAsync("hello", new { token });
            output.WriteLine($"<= hello {hello["result"]!.ToJsonString()}");
            Assert.Equal("dml-cat/1", hello["result"]!["protocolVersion"]!.GetValue<string>());

            var launch = await client.RequestAsync
            (
                "launch",
                new { operationId = "op-e2e", accountName = "sim-account", dalamud = true, minion = new { cardFingerprint = "0123456789abcdef", variant = "cn", keycode = "FFXIVXFAKE3333333333", uid = "0123456789abcdef0123456789abcdef", forumId = "fake-forum-user", forumPassword = "fake-forum-pass" } }
            );
            output.WriteLine($"<= launch {launch["result"]!.ToJsonString()}");
            Assert.True(launch["result"]!["accepted"]!.GetValue<bool>());

            var seen    = new List<(string Method, JsonNode? Params)>();
            var started = client.WaitForEvent("game.started", Timeout, seen);
            var gamePid = started.Params!["pid"]!.GetValue<int>();

            while (true)
            {
                var stage = client.WaitForEvent("game.stage", Timeout, seen);

                if (stage.Params!["stage"]!.GetValue<string>() == "running")
                    break;
            }

            var status = await client.RequestAsync("status", new { });
            output.WriteLine($"<= status {status["result"]!.ToJsonString()}");
            Assert.Equal("running", status["result"]!["stage"]!.GetValue<string>());

            using (var placeholder = Process.GetProcessById(gamePid))
                placeholder.Kill();

            client.WaitForEvent("game.exited", Timeout, seen);

            foreach (var (method, parameters) in seen)
                output.WriteLine($"<- {method} {parameters?.ToJsonString()}");

            Assert.Contains(seen, x => x.Method == "game.agent" && x.Params!["kind"]!.GetValue<string>() == "dalamud" && x.Params["ok"]!.GetValue<bool>());
            Assert.Contains(seen, x => x.Method == "game.agent" && x.Params!["kind"]!.GetValue<string>() == "minion" && x.Params["ok"]!.GetValue<bool>());
            Assert.All(seen.Where(x => x.Method.StartsWith("game.")), x => Assert.Equal("op-e2e", x.Params!["operationId"]!.GetValue<string>()));

            Assert.True(launcher.WaitForExit(Timeout));
            output.WriteLine($"DcMiniLauncher 进程退出码: {launcher.ExitCode}");
            Assert.Equal(0, launcher.ExitCode);
        }
        finally
        {
            if (!launcher.HasExited)
                launcher.Kill(true);

            try
            {
                Directory.Delete(roamingPath, true);
            }
            catch
            {
                // 日志文件可能还被占用
            }
        }
    }

    [Fact]
    public async Task SimulatedLaunch_Close_ClosesGame_SendsExitedClosed_AndHoldsPresenceWhileRunning()
    {
        var exe = FindLauncherExe();
        Assert.True(exe != null, "找不到 XIVLauncherCN.exe, 先编译 XIVLauncher 项目");

        var roamingPath = Path.Combine(Path.GetTempPath(), "cat-dml-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(roamingPath);

        var pipeName = CatTestNames.NewPipeName();
        var token    = CatTestNames.NewToken();

        var startInfo = new ProcessStartInfo(exe!)
        {
            UseShellExecute        = false,
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            WorkingDirectory       = Path.GetDirectoryName(exe)!
        };
        startInfo.ArgumentList.Add("--cat-launch");
        startInfo.ArgumentList.Add("--cat-simulate");
        startInfo.ArgumentList.Add($"--roamingPath={roamingPath}");

        using var launcher = Process.Start(startInfo)!;
        launcher.BeginOutputReadLine();
        launcher.BeginErrorReadLine();

        try
        {
            await launcher.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { pipeName, token }));
            await launcher.StandardInput.FlushAsync();

            await using var client = await ConnectWithRetryAsync(pipeName);
            await client.RequestAsync("hello", new { token });

            Assert.True(XIVLauncher.CatHost.CatHostPresence.IsAnyRunning());

            await client.RequestAsync("launch", new { operationId = "op-close", accountName = "sim-account", dalamud = false });

            var seen    = new List<(string Method, JsonNode? Params)>();
            var started = client.WaitForEvent("game.started", Timeout, seen);
            var gamePid = started.Params!["pid"]!.GetValue<int>();

            var close = await client.RequestAsync("close", new { timeoutSeconds = 1 });
            Assert.True(close["result"]!["accepted"]!.GetValue<bool>());

            var exited = client.WaitForEvent("game.exited", Timeout, seen);
            Assert.Equal("closed", exited.Params!["reason"]!.GetValue<string>());
            Assert.Equal(gamePid, exited.Params["pid"]!.GetValue<int>());

            Assert.True(launcher.WaitForExit(Timeout));
            Assert.Equal(0, launcher.ExitCode);
            Assert.Throws<ArgumentException>(() => Process.GetProcessById(gamePid));
        }
        finally
        {
            if (!launcher.HasExited)
                launcher.Kill(true);

            try
            {
                Directory.Delete(roamingPath, true);
            }
            catch
            {
                // 日志文件可能还被占用
            }
        }
    }

    [Fact]
    public async Task SimulatedLaunch_CloseBeforeLaunch_RepliesThenExitsWithCode0()
    {
        var exe = FindLauncherExe();
        Assert.True(exe != null, "找不到 XIVLauncherCN.exe, 先编译 XIVLauncher 项目");

        var roamingPath = Path.Combine(Path.GetTempPath(), "cat-dml-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(roamingPath);

        var pipeName = CatTestNames.NewPipeName();
        var token    = CatTestNames.NewToken();

        var startInfo = new ProcessStartInfo(exe!)
        {
            UseShellExecute        = false,
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            WorkingDirectory       = Path.GetDirectoryName(exe)!
        };
        startInfo.ArgumentList.Add("--cat-launch");
        startInfo.ArgumentList.Add("--cat-simulate");
        startInfo.ArgumentList.Add($"--roamingPath={roamingPath}");

        using var launcher = Process.Start(startInfo)!;
        launcher.BeginOutputReadLine();
        launcher.BeginErrorReadLine();

        try
        {
            await launcher.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { pipeName, token }));
            await launcher.StandardInput.FlushAsync();

            await using var client = await ConnectWithRetryAsync(pipeName);
            await client.RequestAsync("hello", new { token });

            var close = await client.RequestAsync("close", new { });
            Assert.True(close["result"]!["accepted"]!.GetValue<bool>());

            Assert.True(launcher.WaitForExit(Timeout));
            Assert.Equal(0, launcher.ExitCode);
        }
        finally
        {
            if (!launcher.HasExited)
                launcher.Kill(true);

            try
            {
                Directory.Delete(roamingPath, true);
            }
            catch
            {
                // 日志文件可能还被占用
            }
        }
    }

    [Fact]
    public async Task InvalidBootstrap_ExitsWithCode2()
    {
        var exe = FindLauncherExe();
        Assert.True(exe != null, "找不到 XIVLauncherCN.exe, 先编译 XIVLauncher 项目");

        var roamingPath = Path.Combine(Path.GetTempPath(), "cat-dml-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(roamingPath);

        var startInfo = new ProcessStartInfo(exe!) { UseShellExecute = false, RedirectStandardInput = true };
        startInfo.ArgumentList.Add("--cat-launch");
        startInfo.ArgumentList.Add("--cat-simulate");
        startInfo.ArgumentList.Add($"--roamingPath={roamingPath}");

        using var launcher = Process.Start(startInfo)!;
        await launcher.StandardInput.WriteLineAsync("""{"pipeName":"not-valid","token":"x"}""");
        await launcher.StandardInput.FlushAsync();

        Assert.True(launcher.WaitForExit(Timeout));
        Assert.Equal(2, launcher.ExitCode);
    }

    private static async Task<CatTestClient> ConnectWithRetryAsync(string pipeName)
    {
        var deadline = DateTime.UtcNow + Timeout;

        while (true)
        {
            try
            {
                return await CatTestClient.ConnectAsync(pipeName, TimeSpan.FromSeconds(2));
            }
            catch (Exception) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(200);
            }
        }
    }

    private static string? FindLauncherExe()
    {
        var local = Path.Combine(AppContext.BaseDirectory, "XIVLauncherCN.exe");

        if (File.Exists(local) && File.Exists(Path.Combine(AppContext.BaseDirectory, "XIVLauncherCN.runtimeconfig.json")))
            return local;

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "bin", "win-x64", "XIVLauncherCN.exe");

            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint serverProcessId);
}
