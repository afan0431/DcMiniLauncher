using XIVLauncher.CatHost;
using Xunit;

namespace XIVLauncher.Test.CatHost;

/// <summary>
///     游戏内模块的注入条件: 各启动方式用哪种模式, 等窗口出现后注入, 已注入不重复, 接管来的游戏受环境变量开关控制、经跨进程锁错开
/// </summary>
public sealed class CatModuleInjectionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(false, false, true)] // 工作台上号, 不自动进入、不挂 Minion: 只读（读等级的后台任务注入）
    [InlineData(true, false, false)] // 自动进入角色: 完整（编排一开始注入）
    [InlineData(false, true, false)] // 挂 Minion、不自动进入: 完整（挂 Minion 前后台注入）; 界面版挂 Minion 同
    [InlineData(true, true, false)]  // 自动进入并挂 Minion: 完整
    public void ReadOnlyForLaunch_FullModeOnlyForAutoEnterOrMinion(bool autoEnter, bool minion, bool readOnly) =>
        Assert.Equal(readOnly, CatModuleInjection.ReadOnlyForLaunch(autoEnter, minion));

    [Theory]
    [InlineData(true, false)] // 接管自动进入角色的游戏: 与原来那次一致, 完整
    [InlineData(false, true)] // 其它接管来的游戏（含界面版交接来的）: 只读
    public void ReadOnlyForAdopt_FullModeOnlyForAutoEnter(bool autoEnter, bool readOnly) =>
        Assert.Equal(readOnly, CatModuleInjection.ReadOnlyForAdopt(autoEnter));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Launch_WaitsForTheWindow_ThenInjectsOnceInTheRequestedMode(bool readOnly)
    {
        var game = new FakeTarget { WindowAfterChecks = 3 };

        var error = await CatModuleInjection.InjectWhenReadyAsync(game, readOnly, false, Fast(), CancellationToken.None).WaitAsync(Timeout);

        Assert.Null(error);
        Assert.Equal([readOnly], game.Injections);
        Assert.True(game.WindowChecks >= 3);
    }

    [Fact]
    public async Task Launch_AlreadyInjected_DoesNotInjectAgain()
    {
        var game = new FakeTarget { Injected = true };

        var error = await CatModuleInjection.InjectWhenReadyAsync(game, true, false, Fast(), CancellationToken.None).WaitAsync(Timeout);

        Assert.Null(error);
        Assert.Empty(game.Injections);
    }

    [Fact]
    public async Task SeveralCallersAtOnce_InjectOnlyOnce()
    {
        var game = new FakeTarget { InjectDelay = TimeSpan.FromMilliseconds(200) };

        var calls = Enumerable.Range(0, 4)
                              .Select(_ => Task.Run(() => CatModuleInjection.InjectWhenReadyAsync(game, true, false, Fast(), CancellationToken.None)))
                              .ToArray();

        var errors = await Task.WhenAll(calls).WaitAsync(Timeout);

        Assert.All(errors, Assert.Null);
        Assert.Single(game.Injections);
    }

    [Fact]
    public async Task InjectionFailure_IsReturned()
    {
        var game = new FakeTarget { InjectError = "OpenProcess 失败 (err=5)" };

        var error = await CatModuleInjection.InjectWhenReadyAsync(game, true, false, Fast(), CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal("OpenProcess 失败 (err=5)", error);
    }

    [Fact]
    public async Task GameExitsBeforeTheWindow_DoesNotInject()
    {
        var game = new FakeTarget { WindowAfterChecks = int.MaxValue, ExitAfterChecks = 2 };

        var error = await CatModuleInjection.InjectWhenReadyAsync(game, true, false, Fast(), CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal("游戏进程已经退出", error);
        Assert.Empty(game.Injections);
    }

    [Fact]
    public async Task WindowNeverAppears_GivesUpWithoutInjecting()
    {
        var game    = new FakeTarget { WindowAfterChecks = int.MaxValue };
        var options = Fast() with { WindowTimeout = TimeSpan.FromMilliseconds(100) };

        var error = await CatModuleInjection.InjectWhenReadyAsync(game, true, false, options, CancellationToken.None).WaitAsync(Timeout);

        Assert.NotNull(error);
        Assert.Contains("游戏窗口没有出现", error);
        Assert.Empty(game.Injections);
    }

    [Fact]
    public async Task Adopted_WithoutModule_InjectsUnderTheCrossProcessLock()
    {
        var game = new FakeTarget();

        var error = await CatModuleInjection.InjectWhenReadyAsync(game, false, true, Fast(), CancellationToken.None).WaitAsync(Timeout);

        Assert.Null(error);
        Assert.Equal([false], game.Injections);
    }

    [Fact]
    public async Task Adopted_AlreadyInjected_DoesNotInjectEvenWhenTheSwitchIsOff()
    {
        var game    = new FakeTarget { Injected = true };
        var options = Fast() with { InjectOnAdopt = () => false };

        var error = await CatModuleInjection.InjectWhenReadyAsync(game, true, true, options, CancellationToken.None).WaitAsync(Timeout);

        Assert.Null(error);
        Assert.Empty(game.Injections);
    }

    [Fact]
    public async Task Adopted_SwitchOff_DoesNotInject()
    {
        var game    = new FakeTarget();
        var options = Fast() with { InjectOnAdopt = () => false };

        var error = await CatModuleInjection.InjectWhenReadyAsync(game, true, true, options, CancellationToken.None).WaitAsync(Timeout);

        Assert.NotNull(error);
        Assert.Contains(CatModuleInjection.ADOPT_INJECT_VARIABLE, error);
        Assert.Empty(game.Injections);
    }

    [Fact]
    public async Task Adopted_SeveralCallersAtOnce_InjectOnlyOnce()
    {
        var game    = new FakeTarget { InjectDelay = TimeSpan.FromMilliseconds(100) };
        var options = Fast() with { AdoptSpacing = TimeSpan.FromMilliseconds(50) };

        var calls = Enumerable.Range(0, 3)
                              .Select(_ => Task.Run(() => CatModuleInjection.InjectWhenReadyAsync(game, true, true, options, CancellationToken.None)))
                              .ToArray();

        var errors = await Task.WhenAll(calls).WaitAsync(Timeout);

        Assert.All(errors, Assert.Null);
        Assert.Single(game.Injections);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData(" False ", false)]
    [InlineData("off", false)]
    public void InjectOnAdopt_ReadsTheEnvironmentVariable(string? value, bool expected)
    {
        // 同一个变量只在这里改: 其它用例都经 CatModuleInjectOptions.InjectOnAdopt 指定
        var saved = Environment.GetEnvironmentVariable(CatModuleInjection.ADOPT_INJECT_VARIABLE);

        try
        {
            Environment.SetEnvironmentVariable(CatModuleInjection.ADOPT_INJECT_VARIABLE, value);
            Assert.Equal(expected, CatModuleInjection.InjectOnAdopt);
        }
        finally
        {
            Environment.SetEnvironmentVariable(CatModuleInjection.ADOPT_INJECT_VARIABLE, saved);
        }
    }

    /// <summary>窗口检查间隔与接管间隔都压短; 跨进程锁用本用例独有的名字</summary>
    private static CatModuleInjectOptions Fast() => new()
    {
        PollInterval  = TimeSpan.FromMilliseconds(10),
        AdoptMutex    = $@"Local\CatModuleInjectionTests-{Guid.NewGuid():N}",
        AdoptWait     = TimeSpan.FromSeconds(5),
        AdoptSpacing  = TimeSpan.Zero,
        InjectOnAdopt = () => true
    };

    private sealed class FakeTarget : ICatModuleTarget
    {
        private readonly object sync = new();
        private int             windowChecks;

        public int       WindowAfterChecks { get; init; }
        public int       ExitAfterChecks   { get; init; } = int.MaxValue;
        public TimeSpan  InjectDelay       { get; init; }
        public string?   InjectError       { get; init; }
        public bool      Injected          { get; set; }
        public List<bool> Injections       { get; } = [];

        public int WindowChecks
        {
            get
            {
                lock (sync)
                    return windowChecks;
            }
        }

        public bool HasExited
        {
            get
            {
                lock (sync)
                    return windowChecks >= ExitAfterChecks;
            }
        }

        public bool HasWindow
        {
            get
            {
                lock (sync)
                    return ++windowChecks > WindowAfterChecks;
            }
        }

        public bool IsInjected
        {
            get
            {
                lock (sync)
                    return Injected;
            }
        }

        public string? Inject(bool readOnly)
        {
            Thread.Sleep(InjectDelay);

            lock (sync)
            {
                Injections.Add(readOnly);

                if (InjectError == null)
                    Injected = true;
            }

            return InjectError;
        }
    }
}
