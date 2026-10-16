using System.IO;
using XIVLauncher.CatHost;
using Xunit;

namespace XIVLauncher.Test.CatHost;

/// <summary>
///     职业等级与副本状态读数: 模块 LEVELS / DUTY 回应的解析、不自动进入角色的游戏的观察循环
///     （注入失败隔一段时间重试, 之后每 15 秒先 WHOAMI 再 LEVELS 再 DUTY, 角色变了报 game.character, 管道被占跳过, 旧模块不再问, 读超时跳过这一轮）
/// </summary>
public sealed class CatLevelWatchTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>开发机实机采样的形状: 当前职业机工士（ClassJob 31 → 下标 20）100 级</summary>
    private const string LEVELS_35 =
        "OK loaded=1 job=31 level=100 synced=0 n=35 levels=100,100,100,100,100,100,100,100,100,100,100,100,100,100,100,100,100,100,100,100,100,100,100,100,100,80,100,100,100,100,100,100,33,100,100";

    private const string WHO_SEVEN = "OK loaded=1 cid=40000000000000017 world=1167 home=1167 worldName=MengYaChi homeName=MengYaChi loggedIn=1 inZone=1 lobbyCid=40000000000000017 name=七色花";

    private const string WHO_OTHER = "OK loaded=1 cid=40000000000000099 world=1042 home=1167 worldName=LaNuoXiYa homeName=LaNuoXiYa loggedIn=1 inZone=1 lobbyCid=40000000000000099 name=别的 角色";

    private const string WHO_NOBODY = "OK loaded=0 cid=0 world=0 home=0 worldName= homeName= loggedIn=0 inZone=0 lobbyCid=0 name=";

    private const string DUTY_OUT = "OK loaded=1 inDuty=0 flags=";

    private const string DUTY_IN = "OK loaded=1 inDuty=1 flags=34,56";

    [Fact]
    public void ParseLevels_ReadsTheWholeArray_AndTheCurrentJob()
    {
        var reading = CatModuleReplies.ParseLevels(LEVELS_35);

        Assert.NotNull(reading);
        Assert.Equal(35, reading.Levels.Length);
        Assert.Equal(80, reading.Levels[25]);
        Assert.Equal(33, reading.Levels[32]);
        Assert.Equal(31, reading.ClassJob);
        Assert.Equal(100, reading.Level);
        Assert.False(reading.Synced);
        Assert.True(reading.Consistent);
    }

    [Theory]
    // 当前职业那一格对不上当前等级: 偏移漂移读出的错位数据
    [InlineData("OK loaded=1 job=3 level=90 synced=0 n=3 levels=1,2,3", false)]
    [InlineData("OK loaded=1 job=3 level=3 synced=0 n=3 levels=1,2,3", true)]
    // 等级同步中不比
    [InlineData("OK loaded=1 job=31 level=11 synced=1 n=3 levels=1,2,3", true)]
    // 冒险者、表里没有的职业、下标超出数组、旧回应没带当前职业: 不比
    [InlineData("OK loaded=1 job=0 level=1 synced=0 n=3 levels=1,2,3", true)]
    [InlineData("OK loaded=1 job=99 level=1 synced=0 n=3 levels=1,2,3", true)]
    [InlineData("OK loaded=1 job=43 level=5 synced=0 n=3 levels=1,2,3", true)]
    [InlineData("OK loaded=1 n=3 levels=1,2,3", true)]
    public void ParseLevels_ChecksTheCurrentJobAgainstItsSlot(string response, bool consistent) =>
        Assert.Equal(consistent, CatModuleReplies.ParseLevels(response)!.Consistent);

    [Theory]
    [InlineData("OK loaded=0 n=0 levels=")]
    [InlineData("FAIL bad-state")]
    [InlineData("FAIL unknown-command")]
    [InlineData("OK loaded=1 n=3 levels=1,2")]
    [InlineData("OK loaded=1 n=2 levels=1,-2")]
    [InlineData("OK loaded=1 n=2 levels=1,x")]
    [InlineData("OK loaded=1 n=0 levels=")]
    [InlineData("OK loaded=1 n=3 levels=0,0,0")]
    public void ParseLevels_NotInWorldAllZeroOrMalformed_IsNull(string response) =>
        Assert.Null(CatModuleReplies.ParseLevels(response));

    [Fact]
    public void ParseLevels_WithoutCount_StillReads() =>
        Assert.Equal([1, 2, 3], CatModuleReplies.ParseLevels("OK loaded=1 levels=1,2,3")!.Levels);

    [Theory]
    [InlineData("OK loaded=1 inDuty=1 flags=34", true)]
    [InlineData("OK loaded=1 inDuty=1 flags=34,56,95", true)]
    [InlineData("OK loaded=1 inDuty=0 flags=", false)]
    [InlineData("OK loaded=1 inDuty=0", false)]
    public void ParseDuty_InWorld_ReadsTheFlag(string response, bool inDuty) =>
        Assert.Equal(inDuty, CatModuleReplies.ParseDuty(response));

    [Theory]
    [InlineData("OK loaded=0")]
    [InlineData("OK loaded=0 inDuty=1 flags=34")]
    [InlineData("OK loaded=1")]
    [InlineData("OK loaded=1 inDuty=2 flags=")]
    [InlineData("OK loaded=1 inDuty= flags=")]
    [InlineData("FAIL bad-state")]
    [InlineData("FAIL sigscan-failed:Conditions")]
    [InlineData("FAIL unknown-command")]
    public void ParseDuty_NotInWorldOrMalformed_IsUnknown(string response) =>
        Assert.Null(CatModuleReplies.ParseDuty(response));

    [Fact]
    public async Task Watch_InconsistentReading_IsNotReported()
    {
        var game     = new FakeLevelGame { Levels = "OK loaded=1 job=31 level=90 synced=0 n=35 levels=" + string.Join(',', Enumerable.Repeat(100, 35)) };
        var reporter = new RecordingReporter();
        var ticks    = 0;

        game.OnDelay = g =>
        {
            if (++ticks == 3)
                g.HasExited = true;
        };

        await new CatLevelWatch(game, reporter).RunAsync(CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(2, game.Commands.Count(x => x == "LEVELS"));
        Assert.Empty(reporter.LevelReports);
    }

    [Fact]
    public async Task Watch_ReadsWhoAmIThenLevels_EveryInterval_SkippingBusyTurns()
    {
        var game     = new FakeLevelGame();
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
        // 第 1、3、4 次读了（先 WHOAMI 再 LEVELS 再 DUTY）, 第 2 次管道被占着跳过, 第 5 次游戏退出
        Assert.Equal(["WHOAMI", "LEVELS", "DUTY", "WHOAMI", "LEVELS", "DUTY", "WHOAMI", "LEVELS", "DUTY"], game.Commands);
        Assert.Equal(3, reporter.LevelReports.Count);
        Assert.Equal([(4242, false), (4242, false), (4242, false)], reporter.DutyReports);
        Assert.All(reporter.LevelReports, x => Assert.Equal(4242, x.Pid));
        Assert.All(reporter.LevelReports, x => Assert.Equal(new CatLevelsCharacter("七色花", "40000000000000017", 1167), x.Character));
        Assert.Equal(3, game.Releases);
        Assert.True(game.Elapsed >= new CatAutoEnterTimings().ObserveInterval * 5);

        // 角色只在第一次读到时报一次
        var entered = Assert.Single(reporter.EnteredCharacters);
        Assert.Equal("40000000000000017", entered.ContentId);
        Assert.Equal("MengYaChi", entered.HomeWorld);
        Assert.Equal("萌芽池", entered.HomeWorldName);
        Assert.Equal("萌芽池", entered.CurrentWorldName);
        Assert.DoesNotContain(reporter.Entries, x => x.StartsWith("stage:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Watch_CharacterChanges_ReportsItAgain_WithoutTrustingTheHomeName()
    {
        var game     = new FakeLevelGame();
        var reporter = new RecordingReporter();
        var ticks    = 0;

        game.OnDelay = g =>
        {
            switch (++ticks)
            {
                case 3:
                    g.WhoAmI = WHO_OTHER;
                    break;

                case 5:
                    g.HasExited = true;
                    break;
            }
        };

        await new CatLevelWatch(game, reporter).RunAsync(CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(["40000000000000017", "40000000000000099"], reporter.EnteredCharacters.Select(x => x.ContentId));

        // 超域中: 原始服务器 Id 与当前不同, 模块读到的 homeName 其实是当前服务器, 不用
        var other = reporter.EnteredCharacters.Last();
        Assert.Equal("别的 角色", other.Name);
        Assert.Equal("LaNuoXiYa", other.CurrentWorld);
        Assert.Equal(string.Empty, other.HomeWorld);
        Assert.Null(other.HomeWorldName);
        Assert.Equal(1167, reporter.LevelReports.Last().Character.HomeWorldId);
    }

    [Fact]
    public async Task Watch_NotInWorldYet_ReportsNothing()
    {
        var game     = new FakeLevelGame { WhoAmI = WHO_NOBODY };
        var reporter = new RecordingReporter();
        var ticks    = 0;

        game.OnDelay = g =>
        {
            if (++ticks == 3)
                g.HasExited = true;
        };

        await new CatLevelWatch(game, reporter).RunAsync(CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(["WHOAMI", "WHOAMI"], game.Commands);
        Assert.Empty(reporter.LevelReports);
        Assert.Empty(reporter.EnteredCharacters);
    }

    [Fact]
    public async Task Watch_OldModule_StopsAskingForLevels_ButKeepsWatchingTheCharacter()
    {
        var game     = new FakeLevelGame { Levels = "FAIL unknown-command", Duty = "FAIL unknown-command" };
        var reporter = new RecordingReporter();
        var ticks    = 0;

        game.OnDelay = g =>
        {
            if (++ticks == 4)
                g.HasExited = true;
        };

        await new CatLevelWatch(game, reporter).RunAsync(CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(["WHOAMI", "LEVELS", "DUTY", "WHOAMI", "WHOAMI"], game.Commands);
        Assert.Empty(reporter.LevelReports);
        Assert.Empty(reporter.DutyReports);
        Assert.Single(reporter.EnteredCharacters);
    }

    [Fact]
    public async Task Watch_ModuleWithoutDuty_StopsAskingForDuty_ButKeepsReadingLevels()
    {
        // 0.8.0 的模块: 认 LEVELS, 不认 DUTY
        var game     = new FakeLevelGame { Duty = "FAIL unknown-command" };
        var reporter = new RecordingReporter();
        var ticks    = 0;

        game.OnDelay = g =>
        {
            if (++ticks == 4)
                g.HasExited = true;
        };

        await new CatLevelWatch(game, reporter).RunAsync(CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(["WHOAMI", "LEVELS", "DUTY", "WHOAMI", "LEVELS", "WHOAMI", "LEVELS"], game.Commands);
        Assert.Equal(3, reporter.LevelReports.Count);
        Assert.Empty(reporter.DutyReports);
    }

    [Fact]
    public async Task Watch_ReportsEveryDutyReading_AndSkipsUnknownOnes()
    {
        var game     = new FakeLevelGame { Duty = DUTY_OUT };
        var reporter = new RecordingReporter();
        var ticks    = 0;

        // 第 1 轮不在副本, 第 2 轮在, 第 3 轮读歪了, 第 4 轮还没载入, 第 5 轮出来了
        game.OnDelay = g =>
        {
            switch (++ticks)
            {
                case 2:
                    g.Duty = DUTY_IN;
                    break;

                case 3:
                    g.Duty = "FAIL bad-state";
                    break;

                case 4:
                    g.Duty = "OK loaded=0";
                    break;

                case 5:
                    g.Duty = DUTY_OUT;
                    break;

                case 6:
                    g.HasExited = true;
                    break;
            }
        };

        await new CatLevelWatch(game, reporter).RunAsync(CancellationToken.None).WaitAsync(Timeout);

        // 去重是任务单通道的事: 这里每读到一次就报一次, 读不到的不报
        Assert.Equal([(4242, false), (4242, true), (4242, false)], reporter.DutyReports);
        Assert.Equal(5, game.Commands.Count(x => x == "DUTY"));
    }

    [Fact]
    public async Task Watch_SlowDuty_TimesOutAndIsAskedAgainNextTurn()
    {
        var game     = new FakeLevelGame { Hang = command => command == "DUTY" };
        var reporter = new RecordingReporter();
        var ticks    = 0;
        var timings  = new CatAutoEnterTimings { ReadTimeout = TimeSpan.FromMilliseconds(100) };

        game.OnDelay = g =>
        {
            switch (++ticks)
            {
                case 2:
                    g.Hang = null;
                    g.Duty = DUTY_IN;
                    break;

                case 3:
                    g.HasExited = true;
                    break;
            }
        };

        await new CatLevelWatch(game, reporter, timings).RunAsync(CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(["WHOAMI", "LEVELS", "DUTY", "WHOAMI", "LEVELS", "DUTY"], game.Commands);
        Assert.Equal([(4242, true)], reporter.DutyReports);
        Assert.Equal(2, game.Releases);
    }

    [Fact]
    public async Task Watch_ModuleWithoutWhoAmI_Stops()
    {
        var game = new FakeLevelGame { WhoAmI = "FAIL unknown-command" };

        await new CatLevelWatch(game, new RecordingReporter()).RunAsync(CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(["WHOAMI"], game.Commands);
        Assert.False(game.HasExited);
    }

    [Fact]
    public async Task Watch_PipeErrors_SkipThatTurn()
    {
        var calls    = 0;
        var game     = new FakeLevelGame();
        var reporter = new RecordingReporter();
        var ticks    = 0;

        game.Intercept = command => command == "WHOAMI" && ++calls == 1 ? throw new IOException("管道断了") : null;
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
    public async Task Watch_SlowModule_TimesOutAndSkipsThatTurn()
    {
        var game     = new FakeLevelGame { Hang = command => command == "LEVELS" };
        var reporter = new RecordingReporter();
        var ticks    = 0;
        var timings  = new CatAutoEnterTimings { ReadTimeout = TimeSpan.FromMilliseconds(100) };

        game.OnDelay = g =>
        {
            switch (++ticks)
            {
                case 2:
                    g.Hang = null;
                    break;

                case 3:
                    g.HasExited = true;
                    break;
            }
        };

        await new CatLevelWatch(game, reporter, timings).RunAsync(CancellationToken.None).WaitAsync(Timeout);

        // 第一次 LEVELS 卡住超时, 这一轮跳过、不再读 DUTY（管道照样放开）; 第二次读到
        Assert.Equal(["WHOAMI", "LEVELS", "WHOAMI", "LEVELS", "DUTY"], game.Commands);
        Assert.Single(reporter.LevelReports);
        Assert.Equal(2, game.Releases);
    }

    [Fact]
    public async Task Watch_AttachFails_RetriesAfterAMinute_ThenReads()
    {
        var game     = new FakeLevelGame { AttachErrors = new Queue<string?>(["窗口还没出来", "注入超时", null]) };
        var reporter = new RecordingReporter();
        var ticks    = 0;

        // 第 1、2 次等待是重试注入的间隔, 第 3 次是第一轮观察, 第 4 次时游戏退出
        game.OnDelay = g =>
        {
            if (++ticks == 4)
                g.HasExited = true;
        };

        await new CatLevelWatch(game, reporter).RunAsync(CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(3, game.Attaches);
        Assert.Equal(TimeSpan.FromSeconds(60), game.Delays[0]);
        Assert.Equal(TimeSpan.FromSeconds(60), game.Delays[1]);
        Assert.Empty(reporter.Messages);
        Assert.Equal(["WHOAMI", "LEVELS", "DUTY"], game.Commands);
    }

    [Fact]
    public async Task Watch_AttachKeepsFailing_TellsTheWorkbenchOnce()
    {
        var game     = new FakeLevelGame { AttachErrors = new Queue<string?>(Enumerable.Repeat<string?>("找不到模块", 10)) };
        var reporter = new RecordingReporter();

        await new CatLevelWatch(game, reporter).RunAsync(CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(new CatAutoEnterTimings().AttachAttempts, game.Attaches);
        Assert.Empty(game.Commands);
        Assert.Single(reporter.Messages, x => x.Contains("找不到模块", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Watch_Cancelled_Throws()
    {
        var game = new FakeLevelGame();
        using var cancellation = new CancellationTokenSource();
        game.OnDelay = _ => cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CatLevelWatch(game, new RecordingReporter()).RunAsync(cancellation.Token));
    }

    private sealed class FakeLevelGame : ICatAutoEnterGame
    {
        private TimeSpan elapsed;

        public string WhoAmI { get; set; } = WHO_SEVEN;

        public string Levels { get; set; } = LEVELS_35;

        public string Duty { get; set; } = DUTY_OUT;

        /// <summary>改写某条命令的回应（可抛异常）; 返回 null 用默认回应</summary>
        public Func<string, string?>? Intercept { get; set; }

        /// <summary>这条命令一直不回应（直到被取消）</summary>
        public Func<string, bool>? Hang { get; set; }

        public Queue<string?> AttachErrors { get; init; } = new();

        public Action<FakeLevelGame>? OnDelay { get; set; }

        public List<string> Commands { get; } = [];

        public List<TimeSpan> Delays { get; } = [];

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
            return Task.FromResult(AttachErrors.Count > 0 ? AttachErrors.Dequeue() : null);
        }

        public void Release() => Releases++;

        public async Task<string> SendAsync(string command, CancellationToken cancellationToken)
        {
            Commands.Add(command);

            if (Hang?.Invoke(command) == true)
                await Task.Delay(System.Threading.Timeout.Infinite, cancellationToken);

            return Intercept?.Invoke(command) ?? command switch
            {
                "WHOAMI" => WhoAmI,
                "LEVELS" => Levels,
                "DUTY"   => Duty,
                _        => "FAIL unknown-command"
            };
        }

        public Task<IReadOnlyList<CatAutoEnterWorld>> LoadWorldsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CatAutoEnterWorld>>
            (
                [
                    new CatAutoEnterWorld("MengYaChi", "萌芽池", "陆行鸟"),
                    new CatAutoEnterWorld("LaNuoXiYa", "拉诺西亚", "陆行鸟")
                ]
            );

        public Task<string?> SwitchAreaAsync(string areaName, CancellationToken cancellationToken) =>
            Task.FromResult<string?>("不支持");

        public void AreaEntered(string areaName)
        {
        }

        public async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            elapsed += delay;
            Delays.Add(delay);
            OnDelay?.Invoke(this);
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }
}
