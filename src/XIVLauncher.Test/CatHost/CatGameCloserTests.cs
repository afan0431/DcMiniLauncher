using System.Diagnostics;
using System.IO;
using XIVLauncher.CatHost;
using Xunit;

namespace XIVLauncher.Test.CatHost;

public class CatGameCloserTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task NotInWorld_KillsWithoutWaitingForTimeout()
    {
        using var process = StartSleeper();
        var exit  = new FakeExit((_, _) => Task.FromResult(CatGameExitOutcome.NotInWorld));
        var clock = Stopwatch.StartNew();

        Assert.True(await CatGameCloser.CloseAsync(process, TimeSpan.FromSeconds(30), exit).WaitAsync(Timeout));
        Assert.True(process.HasExited);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"用了 {clock.Elapsed}");
        Assert.Equal(1, exit.Calls);
    }

    [Fact]
    public async Task Exiting_WaitsForGameToExitItself()
    {
        using var process = StartSleeper();
        var exit = new FakeExit((game, token) =>
        {
            // 模拟游戏登出后自己退出
            _ = Task.Delay(500, CancellationToken.None).ContinueWith(_ => game.Kill(), TaskScheduler.Default);
            return Task.FromResult(CatGameExitOutcome.Exiting);
        });
        var clock = Stopwatch.StartNew();

        Assert.True(await CatGameCloser.CloseAsync(process, TimeSpan.FromSeconds(30), exit).WaitAsync(Timeout));
        Assert.True(process.HasExited);
        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(400), $"没等游戏自己退出就返回了: {clock.Elapsed}");
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"用了 {clock.Elapsed}");
    }

    [Fact]
    public async Task Exiting_KillsWhenGameDoesNotExitInTime()
    {
        using var process = StartSleeper();
        var exit = new FakeExit((_, _) => Task.FromResult(CatGameExitOutcome.Exiting));

        Assert.True(await CatGameCloser.CloseAsync(process, TimeSpan.FromMilliseconds(500), exit).WaitAsync(Timeout));
        Assert.True(process.HasExited);
    }

    [Fact]
    public async Task HangingExit_SharesTheTimeoutAndFallsBack()
    {
        using var process = StartSleeper();
        var exit = new FakeExit(async (_, token) =>
        {
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, token);
            return CatGameExitOutcome.Exiting;
        });
        var clock = Stopwatch.StartNew();

        Assert.True(await CatGameCloser.CloseAsync(process, TimeSpan.FromMilliseconds(500), exit).WaitAsync(Timeout));
        Assert.True(process.HasExited);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"用了 {clock.Elapsed}");
    }

    [Fact]
    public async Task FailingExit_FallsBackToClosing()
    {
        using var process = StartSleeper();
        var exit = new FakeExit((_, _) => throw new IOException("管道断了"));

        Assert.True(await CatGameCloser.CloseAsync(process, TimeSpan.FromMilliseconds(200), exit).WaitAsync(Timeout));
        Assert.True(process.HasExited);
        Assert.Equal(1, exit.Calls);
    }

    [Fact]
    public async Task ExitedGame_IsNotAsked()
    {
        using var process = StartSleeper();
        process.Kill();
        await process.WaitForExitAsync();
        var exit = new FakeExit((_, _) => Task.FromResult(CatGameExitOutcome.Exiting));

        Assert.True(await CatGameCloser.CloseAsync(process, TimeSpan.FromSeconds(5), exit).WaitAsync(Timeout));
        Assert.Equal(0, exit.Calls);
    }

    [Theory]
    [InlineData("OK exiting", CatGameExitOutcome.Exiting)]
    [InlineData("OK not-in-world\n", CatGameExitOutcome.NotInWorld)]
    [InlineData("FAIL unknown-command", CatGameExitOutcome.Unavailable)]
    [InlineData("FAIL mainthread-timeout", CatGameExitOutcome.Unavailable)]
    [InlineData(null, CatGameExitOutcome.Unavailable)]
    public void Parse_ReadsModuleReply(string? reply, CatGameExitOutcome expected) =>
        Assert.Equal(expected, CatModuleGameExit.Parse(reply));

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

        return Process.Start(startInfo)!;
    }

    private sealed class FakeExit(Func<Process, CancellationToken, Task<CatGameExitOutcome>> request) : ICatGameExit
    {
        private int calls;

        public int Calls => Volatile.Read(ref calls);

        public Task<CatGameExitOutcome> RequestAsync(Process game, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            return request(game, cancellationToken);
        }
    }
}
