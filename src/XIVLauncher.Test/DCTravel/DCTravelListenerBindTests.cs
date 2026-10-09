using System.Net;
using System.Net.Sockets;
using XIVLauncher.DCTravel;
using Xunit;

namespace XIVLauncher.Test.DCTravel;

/// <summary>
///     跨区监听器的端口: 游戏命令行里的端口改不了, 接管时必须绑回同一个, 绑不上要能知道
/// </summary>
public sealed class DCTravelListenerBindTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    [Fact]
    public async Task FreePort_ReportsListening()
    {
        using var client   = new DCTravelClient(string.Empty);
        using var listener = new DCTravelListener(client, FreePort(), false);

        _ = listener.StartAsync();

        Assert.True(await listener.WaitListeningAsync(Timeout));
        listener.StopListening();
    }

    [Fact]
    public async Task OccupiedPort_ReportsNotListening()
    {
        var blocker = new TcpListener(IPAddress.Loopback, 0);
        blocker.Start();

        try
        {
            var port = ((IPEndPoint)blocker.LocalEndpoint).Port;

            using var client   = new DCTravelClient(string.Empty);
            using var listener = new DCTravelListener(client, port, false);

            _ = listener.StartAsync();

            Assert.False(await listener.WaitListeningAsync(Timeout));
        }
        finally
        {
            blocker.Stop();
        }
    }

    [Fact]
    public async Task SamePort_CanBeBoundAgainRightAfterThePreviousOwnerStops()
    {
        var port = FreePort();

        using var client = new DCTravelClient(string.Empty);

        using (var first = new DCTravelListener(client, port, false))
        {
            _ = first.StartAsync();
            Assert.True(await first.WaitListeningAsync(Timeout));
            first.StopListening();
        }

        using var second = new DCTravelListener(client, port, false);
        _ = second.StartAsync();

        Assert.True(await second.WaitListeningAsync(Timeout), "前一个监听停掉后, 同一端口必须马上能再绑（接管靠它）");
        second.StopListening();
    }

    /// <summary>
    ///     守护进程被强杀（不是正常关闭）, 而且还有一条连着的连接: 新进程要能马上绑回同一端口
    /// </summary>
    [Fact]
    public async Task SamePort_CanBeBoundRightAfterTheOwningProcessIsKilled()
    {
        var port = FreePort();

        var script = $"$l=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,{port}); $l.Start(); " +
                     "[Console]::Out.WriteLine('ready'); [Console]::Out.Flush(); $c=$l.AcceptTcpClient(); Start-Sleep 60";

        var startInfo = new System.Diagnostics.ProcessStartInfo("powershell.exe")
        {
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardOutput = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(script);

        using var owner = System.Diagnostics.Process.Start(startInfo)!;

        try
        {
            Assert.Equal("ready", await owner.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)));

            // 留一条已建立的连接, 进程被杀后它会进 TIME_WAIT / 被重置
            using var connection = new TcpClient();
            await connection.ConnectAsync(IPAddress.Loopback, port);
            await Task.Delay(200);
        }
        finally
        {
            owner.Kill(true);
            await owner.WaitForExitAsync();
        }

        using var client   = new DCTravelClient(string.Empty);
        using var listener = new DCTravelListener(client, port, false);
        _ = listener.StartAsync();

        Assert.True(await listener.WaitListeningAsync(Timeout), "占端口的进程被杀后, 同一端口必须马上能再绑");
        listener.StopListening();
    }
}
