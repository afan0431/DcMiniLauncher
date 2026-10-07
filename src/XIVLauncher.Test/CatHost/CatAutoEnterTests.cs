using XIVLauncher.CatHost;
using XIVLauncher.InGame;
using Xunit;

namespace XIVLauncher.Test.CatHost;

/// <summary>
///     自动进入角色的编排: 用假模块应答, 不起游戏
/// </summary>
public sealed class CatAutoEnterTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private const string AREA_HOME  = "陆行鸟";
    private const string AREA_OTHER = "莫古力";
    private const string AREA_THIRD = "猫小胖";

    private static CharaSelectReader.Entry Chara(string contentId, string name, string homeWorld = "LaNuoXiYa", string? currentWorld = null, byte flags = 0) =>
        new(contentId, 0, flags, WorldId(currentWorld ?? homeWorld), WorldId(homeWorld), name, currentWorld ?? homeWorld, homeWorld);

    private static int WorldId(string code) =>
        code switch
        {
            "LaNuoXiYa"   => 1042,
            "HongYuHai"   => 1167,
            "BaiYinXiang" => 1172,
            _             => 1999
        };

    private static (FakeAutoEnterGame Game, RecordingReporter Reporter) Setup(params CharaSelectReader.Entry[] homeCharacters)
    {
        var game = new FakeAutoEnterGame();
        game.Characters[AREA_HOME] = [.. homeCharacters];
        return (game, new RecordingReporter());
    }

    private static string[] Entries(RecordingReporter reporter) => [.. reporter.Entries];

    [Fact]
    public async Task NameMatchesOneCharacter_EntersWithoutAnyone()
    {
        var (game, reporter) = Setup(Chara("11", "小白"), Chara("12", "小黑", "HongYuHai"));
        game.Where = "busy";

        var flow    = new CatAutoEnter(game, reporter, new CatAutoEnterTarget("小黑", null));
        var outcome = await flow.RunAsync(CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(CatAutoEnterOutcome.InWorld, outcome);
        Assert.Equal
        (
            ["stage:enteringLobby", "characters:auto:2", "stage:enteringWorld", "character:小黑@HongYuHai", "stage:inWorld"],
            Entries(reporter)
        );
        Assert.Equal
        (
            ["VERSION", "SKIPMOVIE", "TITLEREADY", "LOGIN", "CHARAS", "FOCUSCHARA 12", "SELECTCHARA 12", "ENTERCHARA 12", "DIALOG YES 12", "WHOAMI"],
            game.Commands.Where(x => x != "LOBBYSTATE")
        );
        Assert.Equal("12", flow.EnteredContentId);
        Assert.Equal(CatStages.IN_WORLD, flow.CurrentStage);
        Assert.Equal([AREA_HOME], game.EnteredAreas);
        Assert.Empty(game.Switched);

        var entered = Assert.Single(reporter.EnteredCharacters);
        Assert.Equal("红玉海", entered.HomeWorldName);
        Assert.Equal("HongYuHai", entered.CurrentWorld);
    }

    [Fact]
    public async Task SameNameOnTwoWorlds_HomeWorldPicksTheRightOne()
    {
        var (game, reporter) = Setup(Chara("11", "小白"), Chara("12", "小白", "HongYuHai"), Chara("13", "别人"));

        var outcome = await new CatAutoEnter(game, reporter, new CatAutoEnterTarget("小白", "hongYuHai")).RunAsync(CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(CatAutoEnterOutcome.InWorld, outcome);
        Assert.Contains("ENTERCHARA 12", game.Commands);
        Assert.Contains("character:小白@HongYuHai", reporter.Entries);
    }

    [Fact]
    public async Task NoName_SingleCharacter_EntersIt()
    {
        var (game, reporter) = Setup(Chara("11", "小白"));

        var outcome = await new CatAutoEnter(game, reporter, new CatAutoEnterTarget(null, null)).RunAsync(CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(CatAutoEnterOutcome.InWorld, outcome);
        Assert.Equal(["stage:enteringLobby", "characters:auto:1", "stage:enteringWorld", "character:小白@LaNuoXiYa", "stage:inWorld"], Entries(reporter));
    }

    [Fact]
    public async Task UnknownName_SingleCharacter_WaitsForChoiceInsteadOfGuessing()
    {
        var (game, reporter) = Setup(Chara("11", "小白"));

        var flow = new CatAutoEnter(game, reporter, new CatAutoEnterTarget("写错的名字", "LaNuoXiYa"));
        var run  = flow.RunAsync(CancellationToken.None);

        await WaitUntilAsync(() => reporter.Entries.Contains("characters:choose:1"));
        Assert.False(run.IsCompleted);
        Assert.DoesNotContain(game.Commands, x => x.StartsWith("ENTERCHARA", StringComparison.Ordinal));

        Assert.True(flow.SelectCharacter("11").Accepted);
        Assert.Equal(CatAutoEnterOutcome.InWorld, await run.WaitAsync(Timeout));
        Assert.Contains("character:小白@LaNuoXiYa", reporter.Entries);
    }

    [Fact]
    public async Task NoName_SeveralCharacters_WaitsForChoice_ThenEntersTheChosenOne()
    {
        var (game, reporter) = Setup(Chara("11", "小白"), Chara("12", "小黑", "HongYuHai"));

        var flow = new CatAutoEnter(game, reporter, new CatAutoEnterTarget(null, null));

        Assert.Equal(CatCodes.NOT_RUNNING, flow.SelectCharacter("12").Code);

        var run = flow.RunAsync(CancellationToken.None);

        await WaitUntilAsync(() => reporter.Entries.Contains("characters:choose:2"));
        Assert.False(run.IsCompleted);
        Assert.Equal(CatStages.AWAITING_CHARACTER_CHOICE, flow.CurrentStage);

        // 等人选的时候不占管道, 也没有去点任何角色
        Assert.True(game.Releases > 0);
        Assert.DoesNotContain(game.Commands, x => x.StartsWith("ENTERCHARA", StringComparison.Ordinal));

        Assert.Equal(CatCodes.INVALID_PARAMS, flow.SelectCharacter("99").Code);
        Assert.True(flow.SelectCharacter("12").Accepted);

        Assert.Equal(CatAutoEnterOutcome.InWorld, await run.WaitAsync(Timeout));
        Assert.Equal
        (
            ["stage:enteringLobby", "stage:awaitingCharacterChoice", "characters:choose:2", "stage:enteringWorld", "character:小黑@HongYuHai", "stage:inWorld"],
            Entries(reporter)
        );
        Assert.Equal(CatCodes.NOT_RUNNING, flow.SelectCharacter("11").Code);
    }

    [Fact]
    public async Task WhileWaitingForChoice_SomeoneEntersInGame_StillReportsTheCharacter()
    {
        var (game, reporter) = Setup(Chara("11", "小白"), Chara("12", "小黑", "HongYuHai"));

        var flow = new CatAutoEnter(game, reporter, new CatAutoEnterTarget("不存在", null));
        var run  = flow.RunAsync(CancellationToken.None);

        await WaitUntilAsync(() => reporter.Entries.Contains("characters:choose:2"));

        // 人直接在游戏窗口里双击角色进去了
        game.EnterWorld("11");

        Assert.Equal(CatAutoEnterOutcome.InWorld, await run.WaitAsync(Timeout));
        Assert.Equal(["stage:enteringLobby", "stage:awaitingCharacterChoice", "characters:choose:2", "character:小白@LaNuoXiYa", "stage:inWorld"], Entries(reporter));
        Assert.DoesNotContain(game.Commands, x => x.StartsWith("ENTERCHARA", StringComparison.Ordinal) || x.StartsWith("DIALOG", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TargetNotInThisLobby_GoesToItsHomeAreaInsteadOfAsking()
    {
        // 账号库记的大区是上次超域留下的: 这个大厅里只有一个做客的角色, 要找的角色在它的原始大区
        var (game, reporter) = Setup(Chara("11", "访客", "BaiYinXiang", "LaNuoXiYa", 16));
        game.Characters[AREA_THIRD] = [Chara("12", "小紫", "ZiShuiZhanQiao")];

        var flow    = new CatAutoEnter(game, reporter, new CatAutoEnterTarget("小紫", "ZiShuiZhanQiao"));
        var outcome = await flow.RunAsync(CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(CatAutoEnterOutcome.InWorld, outcome);
        Assert.Equal([AREA_THIRD], game.Switched);
        Assert.Equal("12", flow.EnteredContentId);
        Assert.DoesNotContain(reporter.Entries, x => x.StartsWith("characters:choose", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TargetTravellingInAnotherArea_SwitchesLobbyOnce_ThenEnters()
    {
        var travelling = Chara("11", "小白", "LaNuoXiYa", "BaiYinXiang", 16);
        var (game, reporter) = Setup(travelling, Chara("12", "小黑"));
        game.Characters[AREA_OTHER] = [travelling];

        var flow    = new CatAutoEnter(game, reporter, new CatAutoEnterTarget("小白", "LaNuoXiYa"));
        var outcome = await flow.RunAsync(CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(CatAutoEnterOutcome.InWorld, outcome);
        Assert.Equal([AREA_OTHER], game.Switched);
        Assert.Equal([AREA_OTHER], game.EnteredAreas);
        Assert.Equal
        (
            ["stage:enteringLobby", "characters:auto:2", "stage:switchingArea", "characters:auto:1", "stage:enteringWorld", "character:小白@LaNuoXiYa", "stage:inWorld"],
            Entries(reporter)
        );

        var listed = reporter.CharacterLists.First().Characters.Single(x => x.ContentId == "11");
        Assert.True(listed.Travelling);
        Assert.Equal("白银乡", listed.CurrentWorldName);

        var entered = Assert.Single(reporter.EnteredCharacters);
        Assert.Equal("BaiYinXiang", entered.CurrentWorld);
        Assert.Equal("LaNuoXiYa", entered.HomeWorld);
    }

    [Fact]
    public async Task TargetStillElsewhereAfterOneSwitch_StopsInsteadOfSwitchingAgain()
    {
        var travelling = Chara("11", "小白", "LaNuoXiYa", "BaiYinXiang", 16);
        var (game, reporter) = Setup(travelling);

        // 换过去之后那边的列表里它仍显示在第三个大区
        game.Characters[AREA_OTHER] = [Chara("11", "小白", "LaNuoXiYa", "ZiShuiZhanQiao", 16)];

        var outcome = await new CatAutoEnter(game, reporter, new CatAutoEnterTarget("小白", null)).RunAsync(CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(CatAutoEnterOutcome.Stopped, outcome);
        Assert.Equal([AREA_OTHER], game.Switched);
        Assert.Contains("stopped:switchAreaFailed", reporter.Entries);
        Assert.Equal("stage:running", reporter.Entries.Last());
    }

    [Fact]
    public async Task SwitchingLobbyFails_StopsWithTheReason()
    {
        var (game, reporter) = Setup(Chara("11", "小白", "LaNuoXiYa", "BaiYinXiang", 16));
        game.SwitchError = "没能取到新的登录票据";

        var outcome = await new CatAutoEnter(game, reporter, new CatAutoEnterTarget("小白", null)).RunAsync(CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(CatAutoEnterOutcome.Stopped, outcome);
        Assert.Contains("stopped:switchAreaFailed", reporter.Entries);
        Assert.Contains(reporter.Messages, x => x.Contains("没能取到新的登录票据", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EmptyList_LooksThroughOtherAreas_AndEntersWhereTheCharacterIs()
    {
        var (game, reporter) = Setup();
        game.Characters[AREA_THIRD] = [Chara("21", "小白", "ZiShuiZhanQiao")];

        var outcome = await new CatAutoEnter(game, reporter, new CatAutoEnterTarget("小白", null)).RunAsync(CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(CatAutoEnterOutcome.InWorld, outcome);
        Assert.Equal([AREA_OTHER, AREA_THIRD], game.Switched);
        Assert.Equal([AREA_THIRD], game.EnteredAreas);
    }

    [Fact]
    public async Task NoCharacterInAnyArea_StopsWithNoCharacter()
    {
        var (game, reporter) = Setup();

        var outcome = await new CatAutoEnter(game, reporter, new CatAutoEnterTarget("小白", null)).RunAsync(CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(CatAutoEnterOutcome.Stopped, outcome);
        Assert.Equal([AREA_OTHER, AREA_THIRD], game.Switched);
        Assert.Equal(["stage:enteringLobby", "stage:switchingArea", "stopped:noCharacter", "stage:running"], Entries(reporter));
    }

    [Fact]
    public async Task CancelLoginPrompt_IsNeverAnswered()
    {
        var (game, reporter) = Setup(Chara("11", "小白"));

        // 点了「是」之后没有直接进游戏, 而是冒出「确定要取消登录吗」（人在排队时点了取消）, 过一会儿人自己关掉、进了游戏
        var ticks = 0;
        game.AfterConfirm = g =>
        {
            g.YesNo = true;
            g.Text  = "确定要取消登录吗？";
        };
        game.OnDelay = g =>
        {
            if (g.YesNo && g.Text.Contains(CatAutoEnter.CANCEL_LOGIN_PROMPT, StringComparison.Ordinal) && ++ticks == 20)
            {
                g.YesNo = false;
                g.Text  = string.Empty;
                g.EnterWorld("11");
            }
        };

        var outcome = await new CatAutoEnter(game, reporter, new CatAutoEnterTarget(null, null)).RunAsync(CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(CatAutoEnterOutcome.InWorld, outcome);
        Assert.Equal(1, game.Commands.Count(x => x == "DIALOG YES 11"));
        Assert.DoesNotContain("DIALOG NO", game.Commands);
        Assert.DoesNotContain("DIALOG OK", game.Commands);
    }

    [Fact]
    public async Task SomeoneClicksAnotherCharacterBeforeTheConfirm_IsNotAnswered_AndStops()
    {
        var (game, reporter) = Setup(Chara("11", "小白"), Chara("12", "小黑"));

        // 编排点了 11, 确认框出来之前有人在游戏里双击了 12: 现在这个确认框问的是 12
        game.Intercept = command =>
        {
            if (command.StartsWith("ENTERCHARA", StringComparison.Ordinal))
                game.Hovered = "12";

            return null;
        };

        var outcome = await new CatAutoEnter(game, reporter, new CatAutoEnterTarget("小白", null)).RunAsync(CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(CatAutoEnterOutcome.Stopped, outcome);
        Assert.Contains("stopped:lobbyError", reporter.Entries);
        Assert.Equal(1, game.Commands.Count(x => x.StartsWith("DIALOG", StringComparison.Ordinal)));
        Assert.True(game.YesNo);
        Assert.DoesNotContain(reporter.Entries, x => x.StartsWith("character:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SecondPromptAfterTheConfirm_IsNotAnswered_AndStops()
    {
        var (game, reporter) = Setup(Chara("11", "小白"));
        var ticks       = 0;
        var confirmedAt = -1;

        // 点了「是」, 确认框关掉, 过一会儿游戏又弹出一个不认识的是/否框
        game.AfterConfirm = _ => confirmedAt = ticks;
        game.OnDelay = g =>
        {
            if (++ticks == confirmedAt + 2 && confirmedAt >= 0)
            {
                g.Text  = "该角色正在其他服务器, 要返回原始服务器吗？";
                g.YesNo = true;
            }
        };

        var outcome = await new CatAutoEnter(game, reporter, new CatAutoEnterTarget(null, null)).RunAsync(CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(CatAutoEnterOutcome.Stopped, outcome);
        Assert.Contains("stopped:lobbyError", reporter.Entries);
        Assert.Contains(reporter.Messages, x => x.Contains("要返回原始服务器吗", StringComparison.Ordinal));
        Assert.Equal(1, game.Commands.Count(x => x.StartsWith("DIALOG", StringComparison.Ordinal)));
        Assert.True(game.YesNo);
    }

    [Fact]
    public async Task ConfirmThatDoesNotNameTheCharacter_IsNeverAnswered()
    {
        var (game, reporter) = Setup(Chara("11", "小白"));
        game.ConfirmText = "要以小黑登录吗？";

        var outcome = await new CatAutoEnter(game, reporter, new CatAutoEnterTarget(null, null)).RunAsync(CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(CatAutoEnterOutcome.Stopped, outcome);
        Assert.DoesNotContain(game.Commands, x => x.StartsWith("DIALOG YES", StringComparison.Ordinal));
        Assert.Contains(reporter.Messages, x => x.Contains("确认框上不是这个角色的名字", StringComparison.Ordinal));
    }

    [Fact]
    public void QueuePosition_FallsBackToThePromptText()
    {
        var real = CatModuleReplies.ParseLobbyState
        (
            "OK where=charaselect world=1201 worldIndex=2 selectedIndex=0 hovered=11 locked=0 stage=1 uiStage=2 queue=0 dialogId=35 yesno=0 ok=1 dialogue=0 loading=0 listWorld=1201" +
            "\nD\tSelectOk\t35\t1\t1\t当前服务器繁忙，需要排队进行登录，请耐心等待。 （当前排队人数：17人）" +
            "\nT 当前服务器繁忙，需要排队进行登录，请耐心等待。 （当前排队人数：17人）"
        )!;

        Assert.Equal(17, CatAutoEnter.QueuePosition(real));
        Assert.Equal(7, CatAutoEnter.QueuePosition(real with { Queue = 7 }));
        Assert.Equal(0, CatAutoEnter.QueuePosition(real with { OkText = "当前服务器繁忙，需要排队进行登录，请耐心等待。" }));
    }

    [Fact]
    public async Task ConfirmWhoseTextCannotBeRead_IsNeverAnswered()
    {
        var (game, reporter) = Setup(Chara("11", "小白"));
        game.ConfirmText = string.Empty;

        var outcome = await new CatAutoEnter(game, reporter, new CatAutoEnterTarget(null, null)).RunAsync(CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(CatAutoEnterOutcome.Stopped, outcome);
        Assert.Contains("stopped:timeout", reporter.Entries);
        Assert.Contains(reporter.Messages, x => x.Contains("读不到确认框的文字", StringComparison.Ordinal));
        Assert.DoesNotContain(game.Commands, x => x.StartsWith("DIALOG", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("FOCUSCHARA")]
    [InlineData("SELECTCHARA")]
    [InlineData("ENTERCHARA")]
    public async Task CallbackThatDidNotTakeEffect_IsNotSentAgain(string step)
    {
        var (game, reporter) = Setup(Chara("11", "小白"));
        game.Intercept = command => command.StartsWith(step, StringComparison.Ordinal) ? "FAIL not-applied" : null;

        var outcome = await new CatAutoEnter(game, reporter, new CatAutoEnterTarget(null, null)).RunAsync(CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(CatAutoEnterOutcome.Stopped, outcome);
        Assert.Contains("stopped:moduleUnavailable", reporter.Entries);
        Assert.Equal(1, game.Commands.Count(x => x.StartsWith(step, StringComparison.Ordinal)));
        Assert.DoesNotContain(game.Commands, x => x.StartsWith("DIALOG", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ClickLandedOnAnotherCharacter_StopsWithoutConfirming()
    {
        var (game, reporter) = Setup(Chara("11", "小白"));
        game.Intercept = command => command.StartsWith("ENTERCHARA", StringComparison.Ordinal) ? "FAIL clicked-other selectedIndex=1 hovered=99" : null;

        var outcome = await new CatAutoEnter(game, reporter, new CatAutoEnterTarget(null, null)).RunAsync(CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(CatAutoEnterOutcome.Stopped, outcome);
        Assert.Equal(1, game.Commands.Count(x => x.StartsWith("ENTERCHARA", StringComparison.Ordinal)));
        Assert.DoesNotContain(game.Commands, x => x.StartsWith("DIALOG", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ListWithUnreadableEntries_SingleCharacterIsNotAssumed()
    {
        var (game, reporter) = Setup(Chara("11", "小白"));
        game.InvalidEntries = 1;

        var flow = new CatAutoEnter(game, reporter, new CatAutoEnterTarget("写错的名字", null));
        var run  = flow.RunAsync(CancellationToken.None);

        await WaitUntilAsync(() => reporter.Entries.Contains("characters:choose:1"));
        Assert.DoesNotContain(game.Commands, x => x.StartsWith("ENTERCHARA", StringComparison.Ordinal));

        Assert.True(flow.SelectCharacter("11").Accepted);
        Assert.Equal(CatAutoEnterOutcome.InWorld, await run.WaitAsync(Timeout));
    }

    [Fact]
    public async Task ListWithOnlyUnreadableEntries_StopsInsteadOfSearchingOtherAreas()
    {
        var (game, reporter) = Setup();
        game.InvalidEntries = 2;

        var outcome = await new CatAutoEnter(game, reporter, new CatAutoEnterTarget("小白", null)).RunAsync(CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(CatAutoEnterOutcome.Stopped, outcome);
        Assert.Contains("stopped:moduleUnavailable", reporter.Entries);
        Assert.Empty(game.Switched);
    }

    [Fact]
    public async Task SomeoneEntersInGameBeforeTheLobbySwitch_IsNotLoggedOut()
    {
        var travelling = Chara("11", "小白", "LaNuoXiYa", "BaiYinXiang", 16);
        var (game, reporter) = Setup(travelling, Chara("12", "小黑"));

        // 读完角色列表之后、换大厅之前, 有人自己双击 12 进了游戏
        game.Intercept = command =>
        {
            if (command == "CHARAS")
                game.EnterAfterNextReply = "12";

            return null;
        };

        var outcome = await new CatAutoEnter(game, reporter, new CatAutoEnterTarget("小白", null)).RunAsync(CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(CatAutoEnterOutcome.InWorld, outcome);
        Assert.Empty(game.Switched);
        Assert.DoesNotContain("stage:switchingArea", reporter.Entries);
        Assert.Contains("character:小黑@LaNuoXiYa", reporter.Entries);
    }

    [Fact]
    public async Task Queue_IsNeverClicked_AndIsWaitedOutWithoutATimeLimit()
    {
        var (game, reporter) = Setup(Chara("11", "小白"));
        var timings = new CatAutoEnterTimings();

        // 排队时间远超「确认登录到进入游戏」的上限
        var queueTicks = (int)(timings.EnterTimeout * 4 / timings.QueuePoll);
        var ticks      = 0;

        game.AfterConfirm = g =>
        {
            g.Ok    = true;
            g.Queue = 12;
            g.Text  = "当前服务器繁忙，需要排队进行登录，请耐心等待。";
        };
        game.OnDelay = g =>
        {
            if (!g.Ok)
                return;

            ticks++;

            if (ticks == queueTicks / 2)
                g.Queue = 3;

            if (ticks == queueTicks)
            {
                g.Ok   = false;
                g.Text = string.Empty;
                g.EnterWorld("11");
            }
        };

        var outcome = await new CatAutoEnter(game, reporter, new CatAutoEnterTarget(null, null), timings).RunAsync(CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(CatAutoEnterOutcome.InWorld, outcome);
        Assert.True(game.Elapsed > timings.EnterTimeout * 3);
        Assert.DoesNotContain("DIALOG OK", game.Commands);
        Assert.Equal(1, game.Commands.Count(x => x == "DIALOG YES 11"));
        Assert.Equal
        (
            ["stage:enteringLobby", "characters:auto:1", "stage:enteringWorld", "queue:12", "queue:3", "character:小白@LaNuoXiYa", "stage:inWorld"],
            Entries(reporter)
        );
    }

    [Fact]
    public async Task CancelPromptDuringQueue_IsNotAnswered_EvenWhenTheTextReadIsTheQueueOne()
    {
        var (game, reporter) = Setup(Chara("11", "小白"));
        var ticks = 0;

        game.AfterConfirm = g =>
        {
            g.Ok    = true;
            g.Queue = 5;
            g.Text  = "当前服务器繁忙，需要排队进行登录，请耐心等待。";
        };
        game.OnDelay = g =>
        {
            ticks++;

            // 排队框还在, 上面又叠了一个是/否框; 模块读到的文字还是排队那句
            if (ticks == 5)
                g.YesNo = true;

            if (ticks == 15)
            {
                g.YesNo = false;
                g.Ok    = false;
                g.Text  = string.Empty;
                g.EnterWorld("11");
            }
        };

        var outcome = await new CatAutoEnter(game, reporter, new CatAutoEnterTarget(null, null)).RunAsync(CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(CatAutoEnterOutcome.InWorld, outcome);
        Assert.Equal(1, game.Commands.Count(x => x == "DIALOG YES 11"));
    }

    [Fact]
    public async Task ErrorDialog_StopsAndLeavesTheGameAlone()
    {
        var (game, reporter) = Setup(Chara("11", "小白"));

        game.AfterConfirm = g =>
        {
            g.Dialogue = true;
            g.Text     = "与服务器的连接已断开。";
        };

        var flow    = new CatAutoEnter(game, reporter, new CatAutoEnterTarget(null, null));
        var outcome = await flow.RunAsync(CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(CatAutoEnterOutcome.Stopped, outcome);
        Assert.Equal(["stage:enteringLobby", "characters:auto:1", "stage:enteringWorld", "stopped:lobbyError", "stage:running"], Entries(reporter));
        Assert.Contains(reporter.Messages, x => x.Contains("与服务器的连接已断开", StringComparison.Ordinal));
        Assert.Equal(CatStages.RUNNING, flow.CurrentStage);

        // 错误框不替人点, 也没有重新登录
        Assert.DoesNotContain("DIALOG OK", game.Commands);
        Assert.Equal(1, game.Commands.Count(x => x == "LOGIN"));
        Assert.False(game.HasExited);
    }

    [Fact]
    public async Task NonQueueNotice_Stops()
    {
        var (game, reporter) = Setup(Chara("11", "小白"));

        game.AfterConfirm = g =>
        {
            g.Ok   = true;
            g.Text = "该角色已在其他地方登录。";
        };

        var outcome = await new CatAutoEnter(game, reporter, new CatAutoEnterTarget(null, null)).RunAsync(CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(CatAutoEnterOutcome.Stopped, outcome);
        Assert.Contains("stopped:lobbyError", reporter.Entries);
        Assert.DoesNotContain("DIALOG OK", game.Commands);
    }

    [Theory]
    [InlineData("0.5.0-focus")]
    [InlineData("0.5.9")]
    public async Task OldModule_StopsBeforeTouchingTheGame(string version)
    {
        var (game, reporter) = Setup(Chara("11", "小白"));
        game.Version = version;

        var outcome = await new CatAutoEnter(game, reporter, new CatAutoEnterTarget(null, null)).RunAsync(CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(CatAutoEnterOutcome.Stopped, outcome);
        Assert.Equal(["stage:enteringLobby", "stopped:moduleOutdated", "stage:running"], Entries(reporter));
        Assert.Equal(["VERSION"], game.Commands);
    }

    [Fact]
    public async Task ModuleCannotBeInjected_StopsWithModuleUnavailable()
    {
        var (game, reporter) = Setup(Chara("11", "小白"));
        game.AttachError = "找不到模块";

        var outcome = await new CatAutoEnter(game, reporter, new CatAutoEnterTarget(null, null)).RunAsync(CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(CatAutoEnterOutcome.Stopped, outcome);
        Assert.Equal(["stage:enteringLobby", "stopped:moduleUnavailable", "stage:running"], Entries(reporter));
        Assert.Empty(game.Commands);
    }

    [Fact]
    public async Task LobbyStateUnreadableWhileBooting_IsWaitedOut()
    {
        var (game, reporter) = Setup(Chara("11", "小白"));
        var reads = 0;

        // 游戏刚起来: 界面还没建好, 模块答不上在哪个界面
        game.Intercept = command => command == "LOBBYSTATE" && ++reads <= 200 ? "FAIL unknown" : null;

        var outcome = await new CatAutoEnter(game, reporter, new CatAutoEnterTarget(null, null)).RunAsync(CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(CatAutoEnterOutcome.InWorld, outcome);
    }

    [Fact]
    public async Task LobbyStateUnreadableLater_StopsAfterAWhile()
    {
        var (game, reporter) = Setup(Chara("11", "小白"));
        var clicked = false;

        game.Intercept = command =>
        {
            if (command.StartsWith("ENTERCHARA", StringComparison.Ordinal))
                clicked = true;

            return clicked && command == "LOBBYSTATE" ? "FAIL exception" : null;
        };

        var outcome = await new CatAutoEnter(game, reporter, new CatAutoEnterTarget(null, null)).RunAsync(CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(CatAutoEnterOutcome.Stopped, outcome);
        Assert.Contains("stopped:moduleUnavailable", reporter.Entries);
    }

    [Fact]
    public async Task SignaturesBroken_StopsWithModuleUnavailable()
    {
        var (game, reporter) = Setup(Chara("11", "小白"));
        game.Intercept = command => command == "LOBBYSTATE" ? "FAIL sigscan-failed" : null;

        var outcome = await new CatAutoEnter(game, reporter, new CatAutoEnterTarget(null, null)).RunAsync(CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(CatAutoEnterOutcome.Stopped, outcome);
        Assert.Contains("stopped:moduleUnavailable", reporter.Entries);
    }

    [Fact]
    public async Task NoConfirmDialogAfterClick_StopsWithTimeout_NamingTheStep()
    {
        var (game, reporter) = Setup(Chara("11", "小白"));
        game.ShowConfirm = false;

        var outcome = await new CatAutoEnter(game, reporter, new CatAutoEnterTarget(null, null)).RunAsync(CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(CatAutoEnterOutcome.Stopped, outcome);
        Assert.Contains("stopped:timeout", reporter.Entries);
        Assert.Contains(reporter.Messages, x => x.Contains("没有出现登录确认框", StringComparison.Ordinal));
        Assert.Equal(1, game.Commands.Count(x => x.StartsWith("ENTERCHARA", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task LockedCharacter_IsNotClicked()
    {
        var (game, reporter) = Setup(Chara("11", "小白", flags: 1));

        var outcome = await new CatAutoEnter(game, reporter, new CatAutoEnterTarget(null, null)).RunAsync(CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(CatAutoEnterOutcome.Stopped, outcome);
        Assert.Contains("stopped:characterLocked", reporter.Entries);
        Assert.DoesNotContain(game.Commands, x => x.StartsWith("ENTERCHARA", StringComparison.Ordinal));
        Assert.False(Assert.Single(reporter.CharacterLists).Characters[0].Loginable);
    }

    [Fact]
    public async Task GameExitsMidway_EndsQuietly()
    {
        var (game, reporter) = Setup(Chara("11", "小白"));
        game.AfterConfirm = g => g.HasExited = true;

        var outcome = await new CatAutoEnter(game, reporter, new CatAutoEnterTarget(null, null)).RunAsync(CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(CatAutoEnterOutcome.Cancelled, outcome);
        Assert.DoesNotContain(reporter.Entries, x => x.StartsWith("stopped:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cancelled_WhileGameIsAlive_ReportsCancelled()
    {
        var (game, reporter) = Setup(Chara("11", "小白"), Chara("12", "小黑"));
        using var cancellation = new CancellationTokenSource();

        var run = new CatAutoEnter(game, reporter, new CatAutoEnterTarget(null, null)).RunAsync(cancellation.Token);

        await WaitUntilAsync(() => reporter.Entries.Contains("characters:choose:2"));
        await cancellation.CancelAsync();

        Assert.Equal(CatAutoEnterOutcome.Cancelled, await run.WaitAsync(Timeout));
        Assert.Equal("stopped:cancelled", reporter.Entries.Last());
    }

    [Fact]
    public async Task Observe_ReportsAgainWhenTheCharacterChanges_AndSkipsWhileThePipeIsBusy()
    {
        var (game, reporter) = Setup(Chara("11", "小白"), Chara("12", "小黑", "HongYuHai"));

        var flow = new CatAutoEnter(game, reporter, new CatAutoEnterTarget("小白", null));
        Assert.Equal(CatAutoEnterOutcome.InWorld, await flow.RunAsync(CancellationToken.None).WaitAsync(Timeout));

        using var cancellation = new CancellationTokenSource();
        var whoAmIBefore = game.Commands.Count(x => x == "WHOAMI");
        var ticks        = 0;

        game.OnDelay = g =>
        {
            ticks++;

            switch (ticks)
            {
                case 1:
                    g.ModuleBusy = true;
                    break;

                case 3:
                    g.ModuleBusy = false;
                    break;

                // 人退到选角界面换了个角色
                case 5:
                    g.EnterWorld("12");
                    break;

                case 8:
                    g.HasExited = true;
                    break;
            }
        };

        await flow.ObserveAsync(cancellation.Token).WaitAsync(Timeout);

        Assert.Equal(["character:小白@LaNuoXiYa", "character:小黑@HongYuHai"], reporter.Entries.Where(x => x.StartsWith("character:", StringComparison.Ordinal)));
        Assert.Equal("12", flow.EnteredContentId);

        // 第 1、2 次管道被占着没有去读; 之后每次读完都放开管道
        Assert.Equal(5, game.Commands.Count(x => x == "WHOAMI") - whoAmIBefore);
        Assert.True(game.Elapsed >= new CatAutoEnterTimings().ObserveInterval * 8);
    }

    [Fact]
    public async Task Observe_AfterAStop_ReportsTheCharacterOnceSomeoneEntersByHand()
    {
        var (game, reporter) = Setup(Chara("11", "小白"));
        game.ShowConfirm = false;

        var flow = new CatAutoEnter(game, reporter, new CatAutoEnterTarget(null, null));
        Assert.Equal(CatAutoEnterOutcome.Stopped, await flow.RunAsync(CancellationToken.None).WaitAsync(Timeout));

        var ticks = 0;
        game.OnDelay = g =>
        {
            if (++ticks == 2)
                g.EnterWorld("11");

            if (ticks == 4)
                g.HasExited = true;
        };

        await flow.ObserveAsync(CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(["stopped:timeout", "stage:running", "character:小白@LaNuoXiYa", "stage:inWorld"], Entries(reporter)[^4..]);
    }

    [Theory]
    [InlineData("LaNuoXiYa", "LaNuoXiYa", null, true)]
    [InlineData("lanuoxiya", "LaNuoXiYa", null, true)]
    [InlineData("拉诺西亚", "LaNuoXiYa", "拉诺西亚", true)]
    [InlineData("HongChaChuan", "HongChaChuan2", null, true)]
    [InlineData("HongYuHai", "LaNuoXiYa", "拉诺西亚", false)]
    public void WorldMatches_AcceptsEnumNameOrChineseName(string requested, string code, string? chineseName, bool expected) =>
        Assert.Equal(expected, CatAutoEnter.WorldMatches(requested, code, chineseName));

    [Fact]
    public void Resolve_FollowsTheRules()
    {
        var one   = Chara("11", "小白");
        var two   = Chara("12", "小白", "HongYuHai");
        var three = Chara("13", "小黑");

        Assert.Equal(((CharaSelectReader.Entry?)null, false), CatAutoEnter.Resolve([], new CatAutoEnterTarget("小白", null), []));
        Assert.Equal(((CharaSelectReader.Entry?)one, false), CatAutoEnter.Resolve([one, three], new CatAutoEnterTarget(" 小白 ", null), []));
        Assert.Equal(((CharaSelectReader.Entry?)null, true), CatAutoEnter.Resolve([one, two, three], new CatAutoEnterTarget("小白", null), []));
        Assert.Equal(((CharaSelectReader.Entry?)null, true), CatAutoEnter.Resolve([one, two, three], new CatAutoEnterTarget("小白", "MengYaChi"), []));
        Assert.Equal(((CharaSelectReader.Entry?)two, false), CatAutoEnter.Resolve([one, two, three], new CatAutoEnterTarget("小白", "HongYuHai"), []));
        Assert.Equal(((CharaSelectReader.Entry?)null, true), CatAutoEnter.Resolve([one, three], new CatAutoEnterTarget(null, null), []));
        Assert.Equal(((CharaSelectReader.Entry?)null, true), CatAutoEnter.Resolve([one, three], new CatAutoEnterTarget("不存在", null), []));
        Assert.Equal(((CharaSelectReader.Entry?)three, false), CatAutoEnter.Resolve([one, three], new CatAutoEnterTarget("不存在", null, "13"), []));

        // 接着上一次进的角色（带 contentId）时它不在列表里: 名字对得上的照登, 但不因为「只剩一个」就登别的角色
        Assert.Equal(((CharaSelectReader.Entry?)null, true), CatAutoEnter.Resolve([one], new CatAutoEnterTarget("不存在", null, "13"), []));
        Assert.Equal(((CharaSelectReader.Entry?)null, true), CatAutoEnter.Resolve([one], new CatAutoEnterTarget(null, null, "13"), []));
        Assert.Equal(((CharaSelectReader.Entry?)one, false), CatAutoEnter.Resolve([one], new CatAutoEnterTarget("小白", null, "13"), []));

        // 列表不完整时不按「只有一个」自动选; 名字对得上的照登
        Assert.Equal(((CharaSelectReader.Entry?)one, false), CatAutoEnter.Resolve([one], new CatAutoEnterTarget(null, null), []));
        Assert.Equal(((CharaSelectReader.Entry?)null, true), CatAutoEnter.Resolve([one], new CatAutoEnterTarget(null, null), [], false));
        Assert.Equal(((CharaSelectReader.Entry?)one, false), CatAutoEnter.Resolve([one], new CatAutoEnterTarget("小白", null), [], false));

        // 名字唯一时不看原始服务器（资料里的服务器可能填错）
        Assert.Equal(((CharaSelectReader.Entry?)three, false), CatAutoEnter.Resolve([one, three], new CatAutoEnterTarget("小黑", "HongYuHai"), []));
    }

    [Fact]
    public void Replies_AreParsedTolerantly()
    {
        var lobby = CatModuleReplies.ParseLobbyState
        (
            "OK where=charaselect world=1042 worldIndex=2 selectedIndex=0 hovered=11 locked=1 stage=31 uiStage=6 queue=7 dialogId=55 yesno=0 ok=1 dialogue=0 loading=0\nT 当前服务器繁忙，\n需要排队进行登录。"
        )!;

        Assert.Equal("charaselect", lobby.Where);
        Assert.True(lobby.Ok);
        Assert.True(lobby.Locked);
        Assert.False(lobby.YesNo);
        Assert.Equal(7, lobby.Queue);
        Assert.Equal("当前服务器繁忙， 需要排队进行登录。", lobby.OkText);
        Assert.Equal(string.Empty, lobby.YesNoText);

        // 模块给每个在场的对话框各一行: 排队框上叠着「取消登录」的是/否框时, 两句文字都分得清
        var stacked = CatModuleReplies.ParseLobbyState
        (
            "OK where=charaselect world=1042 worldIndex=2 selectedIndex=0 hovered=11 locked=1 stage=31 uiStage=6 queue=0 dialogId=55 yesno=1 ok=1 dialogue=0 loading=0" +
            "\nD\tSelectYesno\t60\t1\t1\t确定要取消登录吗？" +
            "\nD\tSelectOk\t55\t1\t1\t当前服务器繁忙，需要排队进行登录，请耐心等待。" +
            "\nT 确定要取消登录吗？"
        )!;

        Assert.Equal("确定要取消登录吗？", stacked.YesNoText);
        Assert.Contains(CatAutoEnter.QUEUE_PROMPT, stacked.OkText);
        Assert.Equal(string.Empty, stacked.DialogueText);

        // 没有逐框的行时, 那一句归给优先级最高的框
        var plain = CatModuleReplies.ParseLobbyState("OK where=charaselect queue=0 yesno=1 ok=1 dialogue=0 loading=0\nT 确定要取消登录吗？")!;
        Assert.Equal("确定要取消登录吗？", plain.YesNoText);
        Assert.Equal(string.Empty, plain.OkText);

        Assert.Null(CatModuleReplies.ParseLobbyState("FAIL unknown"));

        Assert.Null(CatModuleReplies.ParseLobbyState("FAIL mainthread-timeout"));
        Assert.True(CatModuleReplies.IsTransient("FAIL mainthread-timeout"));
        Assert.True(CatModuleReplies.IsTransient("FAIL not-in-list listWorld=0 world=1042"));
        Assert.False(CatModuleReplies.IsTransient("FAIL not-applied selectedIndex=0 hovered=0"));
        Assert.False(CatModuleReplies.IsTransient("FAIL clicked-other selectedIndex=1 hovered=99"));
        Assert.False(CatModuleReplies.IsTransient("FAIL dialog-open"));
        Assert.False(CatModuleReplies.IsTransient("FAIL exception"));

        // 名字带空格（空格分隔）, 以及制表符分隔的写法
        var spaced = CatModuleReplies.ParseWhoAmI("OK loaded=1 name=Xiao Bai cid=4611686018427387905 world=1172 home=1042 worldName=BaiYinXiang homeName=LaNuoXiYa loggedIn=1 inZone=1")!;
        Assert.True(spaced.Loaded);
        Assert.Equal("Xiao Bai", spaced.Name);
        Assert.Equal("4611686018427387905", spaced.ContentId);
        Assert.Equal(1172, spaced.WorldId);
        Assert.Equal("LaNuoXiYa", spaced.HomeWorldCode);

        var tabbed = CatModuleReplies.ParseWhoAmI("OK loaded=1\tcid=11\tworld=1042\thome=1042\tworldName=\thomeName=\tloggedIn=1\tinZone=1\tname=小白")!;
        Assert.Equal("小白", tabbed.Name);
        Assert.Equal(string.Empty, tabbed.WorldCode);

        Assert.False(CatModuleReplies.ParseWhoAmI("OK loaded=0 name= cid= world= home= worldName= homeName= loggedIn=0 inZone=0")!.Loaded);
        Assert.Null(CatModuleReplies.ParseWhoAmI("FAIL exception"));

        Assert.True(MiniModuleClient.TryParseVersion("OK version=0.6.0 pid=1 log=C:\\x.log", out var version));
        Assert.Equal(new Version(0, 6, 0), version);
        Assert.True(MiniModuleClient.TryParseVersion("OK version=0.5.0-focus pid=1 log=x", out var old));
        Assert.True(old < MiniModuleClient.AutoEnterMinimumVersion);
        Assert.False(MiniModuleClient.TryParseVersion("FAIL unknown-command", out _));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;

        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("没有等到预期的状态");

            await Task.Delay(5);
        }
    }

    /// <summary>
    ///     假的游戏一侧: 按模块约定的格式应答, 界面状态由测试改; 等待不真等, 只把假时钟往前拨
    /// </summary>
    private sealed class FakeAutoEnterGame : ICatAutoEnterGame
    {
        private readonly object sync = new();
        private TimeSpan elapsed;
        private CharaSelectReader.Entry? inWorld;

        public string Version { get; set; } = "0.6.0";

        public volatile string Where = "title";

        public Dictionary<string, List<CharaSelectReader.Entry>> Characters { get; } = new()
        {
            [AREA_HOME]  = [],
            [AREA_OTHER] = [],
            [AREA_THIRD] = []
        };

        public List<string> Commands { get; } = [];

        public List<string> Switched { get; } = [];

        public List<string> EnteredAreas { get; } = [];

        public string? AttachError { get; set; }

        public string? SwitchError { get; set; }

        public bool ShowConfirm { get; set; } = true;

        public volatile bool YesNo;

        public volatile bool Ok;

        public volatile bool Dialogue;

        public volatile int Queue;

        public volatile string Text = string.Empty;

        public int Releases;

        /// <summary>点了登录确认框的「是」之后发生什么; 不设就是直接进游戏</summary>
        public Action<FakeAutoEnterGame>? AfterConfirm { get; set; }

        /// <summary>客户端现在选中的角色; 不设就是编排点的那个（设了表示有人在游戏里点了别的角色）</summary>
        public string? Hovered { get; set; }

        /// <summary>点角色后弹出的确认框文字</summary>
        public string? ConfirmText { get; set; }

        /// <summary>CHARAS 首行报的读不准的条目数</summary>
        public int InvalidEntries { get; set; }

        /// <summary>每等一次调一次</summary>
        public Action<FakeAutoEnterGame>? OnDelay { get; set; }

        /// <summary>改写某条命令的回应; 返回 null 用默认回应</summary>
        public Func<string, string?>? Intercept { get; set; }

        public bool ModuleBusy { get; set; }

        public bool HasExited { get; set; }

        public string CurrentAreaName { get; private set; } = AREA_HOME;

        public IReadOnlyList<string> AreaNames => [AREA_HOME, AREA_OTHER, AREA_THIRD];

        public TimeSpan Elapsed
        {
            get
            {
                lock (sync)
                    return elapsed;
            }
        }

        public void EnterWorld(string contentId)
        {
            lock (sync)
                inWorld = Characters.Values.SelectMany(x => x).First(x => x.ContentId == contentId);

            Where = "ingame";
        }

        public Task<string?> AttachAsync(CancellationToken cancellationToken) => Task.FromResult(AttachError);

        public void Release() => Interlocked.Increment(ref Releases);

        public Task<IReadOnlyList<CatAutoEnterWorld>> LoadWorldsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CatAutoEnterWorld>>
            (
                [
                    new CatAutoEnterWorld("LaNuoXiYa", "拉诺西亚", AREA_HOME),
                    new CatAutoEnterWorld("HongYuHai", "红玉海", AREA_HOME),
                    new CatAutoEnterWorld("BaiYinXiang", "白银乡", AREA_OTHER),
                    new CatAutoEnterWorld("ZiShuiZhanQiao", "紫水栈桥", AREA_THIRD)
                ]
            );

        public Task<string?> SwitchAreaAsync(string areaName, CancellationToken cancellationToken)
        {
            Switched.Add(areaName);

            if (SwitchError != null)
                return Task.FromResult<string?>(SwitchError);

            CurrentAreaName = areaName;
            Where           = "charaselect";
            return Task.FromResult<string?>(null);
        }

        public void AreaEntered(string areaName) => EnteredAreas.Add(areaName);

        public async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            lock (sync)
                elapsed += delay;

            OnDelay?.Invoke(this);
            await Task.Delay(1, cancellationToken);
        }

        /// <summary>下一条命令答完之后, 这个角色（contentId）被人手动登进了游戏</summary>
        public string? EnterAfterNextReply { get; set; }

        public Task<string> SendAsync(string command, CancellationToken cancellationToken)
        {
            if (HasExited)
                throw new System.IO.IOException("管道已断开");

            lock (Commands)
                Commands.Add(command);

            var reply = Intercept?.Invoke(command) ?? Reply(command);

            if (EnterAfterNextReply is { } entering)
            {
                EnterAfterNextReply = null;
                EnterWorld(entering);
            }

            return Task.FromResult(reply);
        }

        private string Reply(string command)
        {
            var list = Characters[CurrentAreaName];

            switch (command)
            {
                case "VERSION":
                    return $"OK version={Version} pid=1 log=C:\\Temp\\module.log";

                case "LOBBYSTATE":
                    return $"OK where={Where} world=1042 worldIndex=0 selectedIndex=-1 hovered=0 locked=0 stage=0 uiStage=0 queue={Queue} dialogId=0 " +
                           $"yesno={(YesNo ? 1 : 0)} ok={(Ok ? 1 : 0)} dialogue={(Dialogue ? 1 : 0)} loading=0" +
                           (Text.Length > 0 ? $"\nT {Text}" : string.Empty);

                case "SKIPMOVIE":
                    Where = "title";
                    return "OK where=title";

                case "TITLEREADY":
                    return Where == "title" ? "OK ready=1" : "OK ready=0";

                case "LOGIN":
                    Where = "charaselect";
                    return "OK";

                case "CHARAS":
                    return $"OK where={Where} source=dc n={list.Count} total={list.Count} selected=0 selectedIndex=-1 hovered=0 hoveredIndex=-1 skipped={InvalidEntries} invalid={InvalidEntries}" +
                           string.Concat(list.Select(x => $"\nC\t{x.ContentId}\t{x.Index}\t{x.LoginFlags}\t{x.CurrentWorldId}\t{x.HomeWorldId}\t{x.Name}\t{x.CurrentWorldCode}\t{x.HomeWorldCode}"));

                case "WHOAMI":
                    CharaSelectReader.Entry? current;

                    lock (sync)
                        current = inWorld;

                    return Where == "ingame" && current != null
                               ? $"OK loaded=1 name={current.Name} cid={current.ContentId} world={current.CurrentWorldId} home={current.HomeWorldId} " +
                                 $"worldName={current.CurrentWorldCode} homeName={current.CurrentWorldCode} loggedIn=1 inZone=1"
                               : "OK loaded=0 name= cid= world= home= worldName= homeName= loggedIn=0 inZone=0";
            }

            var parts = command.Split(' ', 2);
            var entry = parts.Length == 2 ? list.FirstOrDefault(x => x.ContentId == parts[1]) : null;

            switch (parts[0])
            {
                case "DIALOG" when parts.Length == 2 && parts[1].StartsWith("YES ", StringComparison.Ordinal):
                    if (!YesNo)
                        return "FAIL no-dialog";

                    // 模块在点的那一刻自己核对: 客户端选中的不是指定的角色就不点
                    if (parts[1][4..] != (Hovered ?? pending))
                        return "FAIL other-character";

                    YesNo = false;
                    Text  = string.Empty;

                    if (AfterConfirm != null)
                        AfterConfirm(this);
                    else
                        EnterWorld(pending!);

                    return "OK clicked addon=SelectYesno";

                case "FOCUSCHARA":
                    return entry == null ? "FAIL not-in-list" : $"OK world={entry.CurrentWorldId}";

                case "SELECTCHARA":
                    return entry == null ? "FAIL not-in-list" : $"OK selected index=0 cid={entry.ContentId}";

                case "ENTERCHARA":
                    if (entry == null)
                        return "FAIL not-in-list";

                    pending = entry.ContentId;

                    if (ShowConfirm)
                    {
                        Text  = ConfirmText ?? $"要以{entry.Name}登录吗？";
                        YesNo = true;
                    }

                    return $"OK clicked index=0 cid={entry.ContentId}";
            }

            return "FAIL unknown-command";
        }

        private string? pending;
    }
}
