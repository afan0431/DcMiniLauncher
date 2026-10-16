using System.IO;
using XIVLauncher.CatHost;
using Xunit;

namespace XIVLauncher.Test.CatHost;

/// <summary>
///     职业等级读数: 模块 LEVELS 回应的解析、不自动进入角色的游戏的观察循环（注入后每 15 秒读一次, 管道被占跳过, 旧模块不再问）
/// </summary>
public sealed class CatLevelWatchTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private const string LEVELS_35 =
        "OK loaded=1 n=35 levels=100,100,100,100,100,100,100,100,100,100,100,100,100,100,100,100,100,100,100,100,100,100,100,100,100,80,100,100,100,100,100,100,33,100,100";

    [Fact]
    public void ParseLevels_ReadsTheWholeArray()
    {
        var levels = CatModuleReplies.ParseLevels(LEVELS_35);

        Assert.NotNull(levels);
        Assert.Equal(35, levels.Length);
        Assert.Equal(80, levels[25]);
        Assert.Equal(33, levels[32]);
    }

    [Theory]
    [InlineData("OK loaded=0 n=0 levels=")]
    [InlineData("FAIL bad-state")]
    [InlineData("FAIL unknown-command")]
    [InlineData("OK loaded=1 n=3 levels=1,2")]
    [InlineData("OK loaded=1 n=2 levels=1,-2")]
    [InlineData("OK loaded=1 n=2 levels=1,x")]
    [InlineData("OK loaded=1 n=0 levels=")]
    public void ParseLevels_NotInWorldOrMalformed_IsNull(string response) =>
        Assert.Null(CatModuleReplies.ParseLevels(response));

    [Fact]
    public void ParseLevels_WithoutCount_StillReads() =>
        Assert.Equal([1, 2, 3], CatModuleReplies.ParseLevels("OK loaded=1 levels=1,2,3"));

    [Fact]
    public async Task Watch_InjectsThenReadsEveryInterval_SkippingBusyTurns()
    {
        var game     = new FakeLevelGame { Reply = _ => LEVELS_35 };
        var reporter = new RecordingReporter();
        var ticks    = 0;

        game.OnDelay = g =>
        {
            switch (++ticks)
            {
                case 2:
                    g.ModuleBusy = true;
                    break;

                case 3:
                    g.ModuleBusy = false;
                    break;

                case 5:
                    g.HasExited = true;
                    break;
            }
        };

        await new CatLevelWatch(game, reporter).RunAsync(CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(1, game.Attaches);
        // 第 1、3、4 次读了, 第 2 次管道被占着跳过, 第 5 次游戏退出
        Assert.Equal(3, game.Commands.Count(x => x == CatLevelReader.COMMAND));
        Assert.Equal(3, reporter.LevelReports.Count);
        Assert.All(reporter.LevelReports, x => Assert.Equal(4242, x.Pid));
        Assert.Equal(game.Commands.Count, game.Releases);
        Assert.True(game.Elapsed >= new CatAutoEnterTimings().ObserveInterval * 5);
    }

    [Fact]
    public async Task Watch_NotInWorldYet_ReportsNothing()
    {
        var game     = new FakeLevelGame { Reply = _ => "OK loaded=0 n=0 levels=" };
        var reporter = new RecordingReporter();
        var ticks    = 0;

        game.OnDelay = g =>
        {
            if (++ticks == 3)
                g.HasExited = true;
        };

        await new CatLevelWatch(game, reporter).RunAsync(CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(2, game.Commands.Count);
        Assert.Empty(reporter.LevelReports);
    }

    [Fact]
    public async Task Watch_OldModule_StopsAsking()
    {
        var game     = new FakeLevelGame { Reply = _ => "FAIL unknown-command" };
        var reporter = new RecordingReporter();

        await new CatLevelWatch(game, reporter).RunAsync(CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal([CatLevelReader.COMMAND], game.Commands);
        Assert.Empty(reporter.LevelReports);
        Assert.False(game.HasExited);
    }

    [Fact]
    public async Task Watch_PipeErrors_SkipThatTurn()
    {
        var calls    = 0;
        var game     = new FakeLevelGame { Reply = _ => ++calls == 1 ? throw new IOException("管道断了") : LEVELS_35 };
        var reporter = new RecordingReporter();
        var ticks    = 0;

        game.OnDelay = g =>
        {
            if (++ticks == 3)
                g.HasExited = true;
        };

        await new CatLevelWatch(game, reporter).RunAsync(CancellationToken.None).WaitAsync(Timeout);

        Assert.Single(reporter.LevelReports);
        Assert.Equal(2, game.Releases);
    }

    [Fact]
    public async Task Watch_AttachFails_TellsTheWorkbenchOnce()
    {
        var game     = new FakeLevelGame { AttachError = "找不到模块" };
        var reporter = new RecordingReporter();

        await new CatLevelWatch(game, reporter).RunAsync(CancellationToken.None).WaitAsync(Timeout);

        Assert.Empty(game.Commands);
        Assert.Contains(reporter.Messages, x => x.Contains("找不到模块", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Watch_Cancelled_Throws()
    {
        var game = new FakeLevelGame { Reply = _ => LEVELS_35 };
        using var cancellation = new CancellationTokenSource();
        game.OnDelay = _ => cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CatLevelWatch(game, new RecordingReporter()).RunAsync(cancellation.Token));
    }

    private sealed class FakeLevelGame : ICatAutoEnterGame
    {
        private TimeSpan elapsed;

        public Func<string, string> Reply { get; set; } = _ => "FAIL unknown-command";

        public string? AttachError { get; set; }

        public Action<FakeLevelGame>? OnDelay { get; set; }

        public List<string> Commands { get; } = [];

        public int Attaches { get; private set; }

        public int Releases { get; private set; }

        public bool ModuleBusy { get; set; }

        public bool HasExited { get; set; }

        public int ProcessId => 4242;

        public string CurrentAreaName => "陆行鸟";

        public IReadOnlyList<string> AreaNames => ["陆行鸟"];

        public TimeSpan Elapsed => elapsed;

        public Task<string?> AttachAsync(CancellationToken cancellationToken)
        {
            Attaches++;
            return Task.FromResult(AttachError);
        }

        public void Release() => Releases++;

        public Task<string> SendAsync(string command, CancellationToken cancellationToken)
        {
            Commands.Add(command);
            return Task.FromResult(Reply(command));
        }

        public Task<IReadOnlyList<CatAutoEnterWorld>> LoadWorldsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CatAutoEnterWorld>>([]);

        public Task<string?> SwitchAreaAsync(string areaName, CancellationToken cancellationToken) =>
            Task.FromResult<string?>("不支持");

        public void AreaEntered(string areaName)
        {
        }

        public async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            elapsed += delay;
            OnDelay?.Invoke(this);
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }
}
