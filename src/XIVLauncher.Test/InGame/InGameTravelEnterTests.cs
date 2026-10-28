using System.Diagnostics;
using XIVLauncher.CatHost;
using XIVLauncher.DCTravel;
using XIVLauncher.InGame;
using XIVLauncher.Test.CatHost;
using Xunit;

namespace XIVLauncher.Test.InGame;

/// <summary>
///     游戏内跨区带 enter: 跨区落定后接着自动进入角色, 进了世界才报成功。跨区那几段用假的, 游戏一侧用自动进入角色测试的假游戏
/// </summary>
public sealed class InGameTravelEnterTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private const string AREA_HOME  = "陆行鸟";
    private const string AREA_OTHER = "莫古力";

    /// <summary>超域中的登录标记</summary>
    private const byte DC_TRAVELING = 16;

    // 状态是进程内静态的, 每个用例用自己的进程号
    private static int NextPid() => Random.Shared.Next(3_000_000, 4_000_000);

    private static CharaSelectReader.Entry Chara(string contentId, string name, string homeWorld, string currentWorld, byte flags = 0) =>
        new(contentId, 0, flags, currentWorld == "BaiYinXiang" ? 1172 : 1042, homeWorld == "BaiYinXiang" ? 1172 : 1042, name, currentWorld, homeWorld);

    /// <summary>跨完区停在落点大区选角界面的假游戏</summary>
    private static CatAutoEnterTests.FakeAutoEnterGame LandedAt(string area, params CharaSelectReader.Entry[] characters)
    {
        var game = new CatAutoEnterTests.FakeAutoEnterGame { CurrentAreaName = area, Where = "charaselect" };
        game.Characters[area] = [.. characters];
        return game;
    }

    private static InGameTravelEnterPlan Plan(ICatAutoEnterGame game, string? name, string? homeWorld = "LaNuoXiYa") =>
        new(() => Task.FromResult(game), new CatAutoEnterTarget(name, homeWorld));

    private static (bool Running, bool Ok, string Step) Snapshot(int pid)
    {
        var status = InGameTravelJobs.Get(pid)!;
        return (status.Running, status.Ok, status.Step);
    }

    [Fact]
    public async Task Forward_WithEnter_ReportsSuccessOnlyAfterInWorld()
    {
        var pid  = NextPid();
        var game = LandedAt(AREA_OTHER, Chara("12", "小黑", "LaNuoXiYa", "BaiYinXiang", DC_TRAVELING));

        (bool Running, bool Ok, string Step) duringTravel = default, duringEnter = default;

        game.Intercept = command =>
        {
            if (command.StartsWith("ENTERCHARA", StringComparison.Ordinal))
                duringEnter = Snapshot(pid);

            return null;
        };

        Assert.NotNull(InGameTravelJobs.TryBegin(pid, $"{AREA_OTHER}/白银乡", true));

        var result = await InGameTravelEnter.RunAsync
        (
            pid,
            () =>
            {
                duringTravel = Snapshot(pid);
                return Task.FromResult(InGameTravelResult.Succeeded(AREA_OTHER));
            },
            Plan(game, "小黑")
        ).WaitAsync(Timeout);

        Assert.Equal((true, false, InGameTravelSteps.TRAVEL), duringTravel);
        Assert.Equal((true, false, InGameTravelSteps.ENTER), duringEnter);

        Assert.True(result.Ok);
        var status = InGameTravelJobs.Get(pid)!;
        Assert.False(status.Running);
        Assert.True(status.Ok);
        Assert.True(status.Enter);
        Assert.Equal(InGameTravelSteps.ENTER, status.Step);
        Assert.Equal("已完成", status.Phase);
        Assert.Equal(string.Empty, status.StopCode);
        Assert.Equal($"已换到 {AREA_OTHER}, 小黑 已进入游戏", status.Message);

        // 角色就在落点大区: 不换大厅, 直接登录
        Assert.Empty(game.Switched);
        Assert.Contains("ENTERCHARA 12", game.Commands);
    }

    [Fact]
    public async Task Back_WithEnter_EntersTheCharacterAtHome()
    {
        var pid  = NextPid();
        var game = LandedAt(AREA_HOME, Chara("11", "小白", "LaNuoXiYa", "LaNuoXiYa"), Chara("12", "小黑", "LaNuoXiYa", "LaNuoXiYa"));

        Assert.NotNull(InGameTravelJobs.TryBegin(pid, $"{AREA_HOME}/拉诺西亚", true));

        var result = await InGameTravelEnter.RunAsync(pid, () => Task.FromResult(InGameTravelResult.Succeeded(AREA_HOME)), Plan(game, "小黑")).WaitAsync(Timeout);

        Assert.True(result.Ok);
        var status = InGameTravelJobs.Get(pid)!;
        Assert.True(status.Ok);
        Assert.False(status.Running);
        Assert.Equal(InGameTravelSteps.ENTER, status.Step);
        Assert.Contains("ENTERCHARA 12", game.Commands);
        Assert.DoesNotContain("ENTERCHARA 11", game.Commands);
        Assert.Empty(game.Switched);
    }

    [Fact]
    public async Task TravelFails_DoesNotEnter()
    {
        var pid     = NextPid();
        var created = false;

        Assert.NotNull(InGameTravelJobs.TryBegin(pid, $"{AREA_OTHER}/白银乡", true));

        var plan = new InGameTravelEnterPlan
        (
            () =>
            {
                created = true;
                return Task.FromResult<ICatAutoEnterGame>(LandedAt(AREA_OTHER));
            },
            new CatAutoEnterTarget("小黑", "LaNuoXiYa")
        );

        var result = await InGameTravelEnter.RunAsync(pid, () => Task.FromResult(InGameTravelResult.Failed("下单失败: 繁忙")), plan).WaitAsync(Timeout);

        Assert.False(result.Ok);
        Assert.False(created);
        var status = InGameTravelJobs.Get(pid)!;
        Assert.False(status.Running);
        Assert.False(status.Ok);
        Assert.Equal(InGameTravelSteps.TRAVEL, status.Step);
        Assert.Equal(string.Empty, status.StopCode);
        Assert.Equal("下单失败: 繁忙", status.Message);
    }

    [Fact]
    public async Task TravelThrows_FailsTheJob()
    {
        var pid = NextPid();

        Assert.NotNull(InGameTravelJobs.TryBegin(pid, $"{AREA_OTHER}/白银乡", true));

        var result = await InGameTravelEnter.RunAsync(pid, () => throw new InvalidOperationException("模块命令 LOGIN 失败"), Plan(LandedAt(AREA_OTHER), "小黑"))
                                            .WaitAsync(Timeout);

        Assert.False(result.Ok);
        Assert.False(InGameTravelJobs.IsRunning(pid));
        Assert.Equal("模块命令 LOGIN 失败", InGameTravelJobs.Get(pid)!.Message);
    }

    [Fact]
    public async Task EnterStops_ReportsTheStopReasonAsIs()
    {
        var pid  = NextPid();
        var game = LandedAt(AREA_OTHER, Chara("12", "小黑", "LaNuoXiYa", "BaiYinXiang", DC_TRAVELING));
        game.Dialogue = true;
        game.Text     = "与服务器的连接中断了。（2002）";

        Assert.NotNull(InGameTravelJobs.TryBegin(pid, $"{AREA_OTHER}/白银乡", true));

        var result = await InGameTravelEnter.RunAsync(pid, () => Task.FromResult(InGameTravelResult.Succeeded(AREA_OTHER)), Plan(game, "小黑")).WaitAsync(Timeout);

        Assert.False(result.Ok);
        var status = InGameTravelJobs.Get(pid)!;
        Assert.False(status.Running);
        Assert.False(status.Ok);
        Assert.Equal(InGameTravelSteps.ENTER, status.Step);
        Assert.Equal(CatAutoEnterStopCodes.LOBBY_ERROR, status.StopCode);
        Assert.Contains("与服务器的连接中断了。（2002）", status.Message, StringComparison.Ordinal);
        Assert.Equal(status.Message, result.Message);
    }

    [Fact]
    public async Task CharacterNotInAnyList_FailsInsteadOfWaitingForChoice()
    {
        var pid  = NextPid();
        var game = LandedAt(AREA_OTHER, Chara("13", "别人", "BaiYinXiang", "BaiYinXiang"));
        game.Characters[AREA_HOME] = [Chara("11", "小白", "LaNuoXiYa", "LaNuoXiYa")];

        Assert.NotNull(InGameTravelJobs.TryBegin(pid, $"{AREA_OTHER}/白银乡", true));

        var result = await InGameTravelEnter.RunAsync(pid, () => Task.FromResult(InGameTravelResult.Succeeded(AREA_OTHER)), Plan(game, "小黑")).WaitAsync(Timeout);

        Assert.False(result.Ok);
        var status = InGameTravelJobs.Get(pid)!;
        Assert.Equal(CatAutoEnterStopCodes.CHARACTER_NOT_FOUND, status.StopCode);
        Assert.Contains("小黑", status.Message);

        // 先去它原始服务器所在的大区找过一次, 那里也没有就停
        Assert.Equal([AREA_HOME], game.Switched);
        Assert.DoesNotContain(game.Commands, x => x.StartsWith("ENTERCHARA", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GameExitsWhileEntering_ReportsGameExited()
    {
        var pid  = NextPid();
        var game = LandedAt(AREA_OTHER, Chara("12", "小黑", "LaNuoXiYa", "BaiYinXiang", DC_TRAVELING));
        game.Where   = "title";
        game.OnDelay = g => g.HasExited = true;

        // 不让它在标题界面点「开始游戏」, 好停在等待里
        game.Intercept = command => command == "TITLEREADY" ? "OK ready=0" : null;

        Assert.NotNull(InGameTravelJobs.TryBegin(pid, $"{AREA_OTHER}/白银乡", true));

        var result = await InGameTravelEnter.RunAsync(pid, () => Task.FromResult(InGameTravelResult.Succeeded(AREA_OTHER)), Plan(game, "小黑")).WaitAsync(Timeout);

        Assert.False(result.Ok);
        var status = InGameTravelJobs.Get(pid)!;
        Assert.Equal(InGameTravelEnter.GAME_EXITED, status.StopCode);
        Assert.Equal(InGameTravelSteps.ENTER, status.Step);
    }

    [Fact]
    public async Task WithoutEnter_SucceedsRightAfterTravel()
    {
        var pid = NextPid();

        Assert.NotNull(InGameTravelJobs.TryBegin(pid, $"{AREA_OTHER}/白银乡"));

        var result = await InGameTravelEnter.RunAsync(pid, () => Task.FromResult(InGameTravelResult.Succeeded(AREA_OTHER)), null).WaitAsync(Timeout);

        Assert.True(result.Ok);
        var status = InGameTravelJobs.Get(pid)!;
        Assert.False(status.Running);
        Assert.True(status.Ok);
        Assert.False(status.Enter);
        Assert.Equal(InGameTravelSteps.TRAVEL, status.Step);
        Assert.Equal($"已换到 {AREA_OTHER}", status.Message);
    }

    [Fact]
    public void RealGame_OwnedByTravelJob_IsNotBlockedByItsOwnJob()
    {
        using var process = Process.GetCurrentProcess();
        using var client  = new DCTravelClient(string.Empty);

        var pid = process.Id;

        using var plain = new CatAutoEnterRealGame(process, client, [], AREA_HOME, _ => { });
        using var owned = new CatAutoEnterRealGame(process, client, [], AREA_HOME, _ => { }) { OwnedByTravelJob = true };

        Assert.NotNull(InGameTravelJobs.TryBegin(pid, AREA_OTHER, true));

        try
        {
            // 换区单在跑: Cat 的观察循环、读等级不碰模块; 这一单自己的自动进入角色照用
            Assert.True(plain.ModuleBusy);
            Assert.False(owned.ModuleBusy);

            // 管道闸被别的操作占着时, 两个都要等
            using (InGameTravelService.TryEnterGate(pid))
                Assert.True(owned.ModuleBusy);

            Assert.False(owned.ModuleBusy);
        }
        finally
        {
            InGameTravelJobs.End(pid, true, "完成");
        }

        Assert.False(plain.ModuleBusy);
    }
}
