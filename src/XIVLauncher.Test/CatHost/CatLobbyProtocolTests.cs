using XIVLauncher.CatHost;
using XIVLauncher.CatLobby.Protocol;
using XIVLauncher.InGame;
using Xunit;

namespace XIVLauncher.Test.CatHost;

/// <summary>
///     国际服 CatLobby 插件的协议层: 命令解析、回应拼装、对话框裁决、排队与提示文字判断。
///     不起游戏; 断言拼出来的回应能被 DML 里的解析函数（CatModuleReplies / CharaSelectReader / MiniModuleClient）原样解析
/// </summary>
public sealed class CatLobbyProtocolTests
{
    private static readonly DialogSnapshot NoDialog = DialogSnapshot.None;

    private static LobbySnapshot Lobby
    (
        LobbyWhere     where    = LobbyWhere.CharaSelect,
        int            queue    = 0,
        bool           locked   = false,
        DialogSnapshot? yesNo   = null,
        DialogSnapshot? ok      = null,
        DialogSnapshot? dialogue = null,
        bool           loading  = false
    ) =>
        new(where, 21, 3, 1, 4611686018427387905UL, locked, 2, 5, queue, 0, loading, 21, yesNo ?? NoDialog, ok ?? NoDialog, dialogue ?? NoDialog);

    private static DialogSnapshot Shown(uint id, string text) => new(true, id, true, true, text);

    private static CharaRow Row(ulong cid, string name, string home = "Asura", string? current = null, byte flags = 0, uint click = 0) =>
        new(cid, 0, flags, 23, 23, name, current ?? home, home, click);

    #region 版本

    [Fact]
    public void Version_ParsesAndMeetsAutoEnterMinimum()
    {
        var response = LobbyReplies.Version(4321, @"C:\Users\a b\AppData\Roaming\XIVLauncher\dalamud.log");

        Assert.True(MiniModuleClient.TryParseVersion(response, out var version));
        Assert.True(version >= MiniModuleClient.AutoEnterMinimumVersion);
        Assert.Equal(new Version(0, 11, 0), version);
        Assert.StartsWith("OK version=0.11.0 pid=4321 mode=full log=", response);
    }

    #endregion

    #region LOBBYSTATE

    [Fact]
    public void LobbyState_EmptyCharaSelect()
    {
        var parsed = CatModuleReplies.ParseLobbyState(LobbyReplies.LobbyState(Lobby()))!;

        Assert.Equal(CatModuleReplies.WHERE_CHARA_SELECT, parsed.Where);
        Assert.False(parsed.Locked);
        Assert.Equal(0, parsed.Queue);
        Assert.False(parsed.YesNo);
        Assert.False(parsed.Ok);
        Assert.False(parsed.Dialogue);
        Assert.False(parsed.Loading);
        Assert.Equal(string.Empty, parsed.YesNoText);
    }

    [Theory]
    [InlineData(LobbyWhere.Title, CatModuleReplies.WHERE_TITLE)]
    [InlineData(LobbyWhere.Busy, CatModuleReplies.WHERE_BUSY)]
    [InlineData(LobbyWhere.InGame, CatModuleReplies.WHERE_IN_GAME)]
    public void LobbyState_WhereNames(LobbyWhere where, string expected)
    {
        Assert.Equal(expected, CatModuleReplies.ParseLobbyState(LobbyReplies.LobbyState(Lobby(where)))!.Where);
    }

    [Fact]
    public void LobbyState_LoginConfirm_KeepsCharacterNameWithSpace()
    {
        var response = LobbyReplies.LobbyState(Lobby(yesNo: Shown(77, "Log in with Miqo'te Rin?\n")));
        var parsed   = CatModuleReplies.ParseLobbyState(response)!;

        Assert.True(parsed.YesNo);
        Assert.Contains("Miqo'te Rin", parsed.YesNoText, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', parsed.YesNoText);
    }

    [Fact]
    public void LobbyState_TabsAndNewlinesInPromptNeverBreakLines()
    {
        var response = LobbyReplies.LobbyState(Lobby(ok: Shown(9, "line1\tline2\r\nline3")));

        Assert.Equal(3, response.Split('\n').Length);
        Assert.Equal("line1 line2  line3", CatModuleReplies.ParseLobbyState(response)!.OkText);
    }

    [Theory]
    [InlineData("The server is currently congested.\nPlayers in queue: 17.", 17)]
    [InlineData("現在サーバーが混み合っています。（現在23人待ちです）", 23)]
    [InlineData("Auf dieser Welt herrscht momentan hoher Andrang.\nPlatz auf der Warteliste: 5", 5)]
    [InlineData("Ce Monde est plein. \nJoueurs en attente : 9", 9)]
    public void LobbyState_QueuePromptReportsPositionFromText(string text, int expected)
    {
        var parsed = CatModuleReplies.ParseLobbyState(LobbyReplies.LobbyState(Lobby(ok: Shown(5, text))))!;

        Assert.True(parsed.Ok);
        Assert.Equal(expected, parsed.Queue);
        Assert.Equal(expected, CatAutoEnter.QueuePosition(parsed));
    }

    [Fact]
    public void LobbyState_QueuePromptWithZeroPlayersStillCountsAsQueueing()
    {
        // 排队框上写着 0 人时, 编排仍要把它当排队（名次 > 0）而不是当成需要停手的提示框
        var parsed = CatModuleReplies.ParseLobbyState(LobbyReplies.LobbyState(Lobby(ok: Shown(5, "The server is currently congested.\nPlayers in queue: 0."))))!;

        Assert.True(parsed.Queue > 0);
    }

    [Fact]
    public void LobbyState_AgentQueuePositionWinsOverText()
    {
        var parsed = CatModuleReplies.ParseLobbyState(LobbyReplies.LobbyState(Lobby(queue: 42, ok: Shown(5, "Players in queue: 7"))))!;

        Assert.Equal(42, parsed.Queue);
    }

    [Fact]
    public void LobbyState_OrdinaryNoticeIsNotQueue()
    {
        var text   = "The character selected was not properly logged out or is currently logged in on another client. Logging character out. Please wait to log in again.";
        var parsed = CatModuleReplies.ParseLobbyState(LobbyReplies.LobbyState(Lobby(ok: Shown(5, text))))!;

        Assert.Equal(0, parsed.Queue);
        Assert.Equal(text, parsed.OkText);
    }

    [Fact]
    public void LobbyState_StackedYesNoOverOk_ReadsEachDialogText()
    {
        var response = LobbyReplies.LobbyState(Lobby(yesNo: Shown(7, "Leave the queue?"), ok: Shown(5, "Players in queue: 3")));
        var parsed   = CatModuleReplies.ParseLobbyState(response)!;

        Assert.True(parsed.YesNo);
        Assert.True(parsed.Ok);
        Assert.Equal("Leave the queue?", parsed.YesNoText);
        Assert.Equal("Players in queue: 3", parsed.OkText);
    }

    [Fact]
    public void LobbyState_DialogueNotReadyIsNotReportedAsShown()
    {
        var response = LobbyReplies.LobbyState(Lobby(dialogue: new DialogSnapshot(true, 3, false, true, "Error 2002")));
        var parsed   = CatModuleReplies.ParseLobbyState(response)!;

        Assert.False(parsed.Dialogue);

        var shown = CatModuleReplies.ParseLobbyState(LobbyReplies.LobbyState(Lobby(dialogue: Shown(3, "Error 2002"))))!;

        Assert.True(shown.Dialogue);
        Assert.Equal("Error 2002", shown.DialogueText);
    }

    [Fact]
    public void LobbyState_LockedAndLoadingFlags()
    {
        var parsed = CatModuleReplies.ParseLobbyState(LobbyReplies.LobbyState(Lobby(locked: true, loading: true)))!;

        Assert.True(parsed.Locked);
        Assert.True(parsed.Loading);
    }

    [Fact]
    public void LobbyState_LargeHoveredContentIdDoesNotGoNegative()
    {
        Assert.DoesNotContain("hovered=-", LobbyReplies.LobbyState(Lobby() with { Hovered = ulong.MaxValue }), StringComparison.Ordinal);
    }

    #endregion

    #region CHARAS

    [Fact]
    public void Charas_ParsesWithCharaSelectReader()
    {
        var list = new CharaListSnapshot
        (
            LobbyWhere.CharaSelect, 3, 1, 0, 1, -1, 4611686018427387905UL, 0,
            [Row(4611686018427387905UL, "Miqo'te Rin", "Asura"), Row(4611686018427387906UL, "Alisaie Leveilleur", "Ifrit", current: "Gungnir", flags: 16)]
        );

        var snapshot = CharaSelectReader.Parse(LobbyReplies.Charas(list))!;

        Assert.Equal(CatModuleReplies.WHERE_CHARA_SELECT, snapshot.Where);
        Assert.Equal("4611686018427387905", snapshot.SelectedContentId);
        Assert.Equal(0, snapshot.Invalid);
        Assert.Equal(2, snapshot.Entries.Count);

        var first = snapshot.Entries[0];
        Assert.Equal("4611686018427387905", first.ContentId);
        Assert.Equal("Miqo'te Rin", first.Name);
        Assert.Equal("Asura", first.HomeWorldCode);
        Assert.Equal("Asura", first.CurrentWorldCode);
        Assert.Equal(23, first.HomeWorldId);

        var traveler = snapshot.Entries[1];
        Assert.Equal("Alisaie Leveilleur", traveler.Name);
        Assert.Equal("Ifrit", traveler.HomeWorldCode);
        Assert.Equal("Gungnir", traveler.CurrentWorldCode);
        Assert.True(traveler.DcTraveling);
    }

    [Fact]
    public void Charas_InvalidCountSurvivesRoundTrip()
    {
        var list = new CharaListSnapshot(LobbyWhere.CharaSelect, 2, 1, 1, 0, -1, 0, 0, [Row(1, "A B")]);

        Assert.Equal(1, CharaSelectReader.Parse(LobbyReplies.Charas(list))!.Invalid);
    }

    [Fact]
    public void Charas_OutsideCharaSelectReportsWhere()
    {
        var list = new CharaListSnapshot(LobbyWhere.Title, 0, 0, 0, 0, -1, 0, 0, []);

        Assert.Equal(CatModuleReplies.WHERE_TITLE, CharaSelectReader.Parse(LobbyReplies.Charas(list))!.Where);
    }

    [Fact]
    public void Charas_TooLargeFailsInsteadOfHalfTable()
    {
        var rows = Enumerable.Range(1, 64).Select(i => Row((ulong)i, new string('x', 60), new string('w', 60))).ToList();
        var list = new CharaListSnapshot(LobbyWhere.CharaSelect, 64, 0, 0, 0, -1, 0, 0, rows);

        Assert.Equal("FAIL too-large", LobbyReplies.Charas(list));
        Assert.Null(CharaSelectReader.Parse("FAIL too-large"));
    }

    [Fact]
    public void Charas_FullDatacenterFitsInOneResponse()
    {
        var rows = Enumerable.Range(1, 40).Select(i => Row((ulong)(4611686018427387904UL + (ulong)i), "Firstname Lastname", "Cerberus")).ToList();
        var text = LobbyReplies.Charas(new CharaListSnapshot(LobbyWhere.CharaSelect, 40, 0, 0, 0, -1, 0, 0, rows));

        Assert.True(System.Text.Encoding.UTF8.GetByteCount(text) < LobbyReplies.MAX_RESPONSE_BYTES);
        Assert.Equal(40, CharaSelectReader.Parse(text)!.Entries.Count);
    }

    #endregion

    #region WHOAMI

    [Fact]
    public void WhoAmI_LoggedIn_ParsesNameWithSpaceAndWorldNames()
    {
        var response = LobbyReplies.WhoAmI(new WhoAmISnapshot(true, 4611686018427387905UL, 23, 25, "Asura", "Cerberus", true, true, 4611686018427387905UL, "Miqo'te Rin"));
        var parsed   = CatModuleReplies.ParseWhoAmI(response)!;

        Assert.True(parsed.Loaded);
        Assert.Equal("Miqo'te Rin", parsed.Name);
        Assert.Equal("4611686018427387905", parsed.ContentId);
        Assert.Equal(23, parsed.WorldId);
        Assert.Equal(25, parsed.HomeWorldId);
        Assert.Equal("Asura", parsed.WorldCode);
        Assert.Equal("Cerberus", parsed.HomeWorldCode);
    }

    [Fact]
    public void WhoAmI_NotLoaded()
    {
        var response = LobbyReplies.WhoAmI(new WhoAmISnapshot(false, 0, 0, 0, "", "", false, false, 0, ""));

        Assert.False(CatModuleReplies.ParseWhoAmI(response)!.Loaded);
    }

    #endregion

    #region TITLEREADY / LOGIN

    [Fact]
    public void TitleReady_ReplyShape()
    {
        Assert.Contains("ready=1", LobbyReplies.TitleReady(true), StringComparison.Ordinal);
        Assert.DoesNotContain("ready=1", LobbyReplies.TitleReady(false), StringComparison.Ordinal);
        Assert.True(CatModuleReplies.IsOk(LobbyReplies.TitleReady(false)));
    }

    [Fact]
    public void Login_ReplyShape()
    {
        Assert.True(CatModuleReplies.IsOk(LobbyReplies.Login(true)));
        Assert.False(CatModuleReplies.IsOk(LobbyReplies.Login(false)));
    }

    #endregion

    #region 命令解析

    [Theory]
    [InlineData("PING", LobbyCommandKind.Ping)]
    [InlineData("VERSION", LobbyCommandKind.Version)]
    [InlineData("LOBBYSTATE", LobbyCommandKind.LobbyState)]
    [InlineData("TITLEREADY", LobbyCommandKind.TitleReady)]
    [InlineData("LOGIN", LobbyCommandKind.Login)]
    [InlineData("SKIPMOVIE", LobbyCommandKind.SkipMovie)]
    [InlineData("CHARAS", LobbyCommandKind.Charas)]
    [InlineData("WHOAMI", LobbyCommandKind.WhoAmI)]
    [InlineData("DIALOG NO", LobbyCommandKind.DialogNo)]
    [InlineData("DIALOG OK", LobbyCommandKind.DialogOk)]
    [InlineData("DIALOG NOTICE", LobbyCommandKind.DialogNotice)]
    [InlineData("BILLING", LobbyCommandKind.Unknown)]
    [InlineData("LEVELS", LobbyCommandKind.Unknown)]
    [InlineData("DUTY", LobbyCommandKind.Unknown)]
    [InlineData("EXIT", LobbyCommandKind.Unknown)]
    [InlineData("CHARCONFIG", LobbyCommandKind.Unknown)]
    [InlineData("MENUSTATUS", LobbyCommandKind.Unknown)]
    [InlineData("", LobbyCommandKind.Unknown)]
    [InlineData("ENTERCHARA DIRECT 123", LobbyCommandKind.NotImplemented)]
    public void Command_Kinds(string raw, LobbyCommandKind expected)
    {
        Assert.Equal(expected, LobbyCommand.Parse(raw).Kind);
    }

    [Fact]
    public void Command_CharaArgumentIsContentIdWhenAllDigits()
    {
        var byId = LobbyCommand.Parse("ENTERCHARA 4611686018427387905");

        Assert.Equal(LobbyCommandKind.EnterChara, byId.Kind);
        Assert.Equal(4611686018427387905UL, byId.ContentId);
    }

    [Fact]
    public void Command_CharaArgumentIsNameWithSpaces()
    {
        var byName = LobbyCommand.Parse("SELECTCHARA Miqo'te Rin");

        Assert.Equal(LobbyCommandKind.SelectChara, byName.Kind);
        Assert.Equal("Miqo'te Rin", byName.Who);
        Assert.Equal(0UL, byName.ContentId);
    }

    [Fact]
    public void Command_FocusAndTrailingNulAndNewlineAreTrimmed()
    {
        var focus = LobbyCommand.Parse("FOCUSCHARA 77\0\r\n");

        Assert.Equal(LobbyCommandKind.FocusChara, focus.Kind);
        Assert.Equal(77UL, focus.ContentId);
    }

    [Theory]
    [InlineData("DIALOG YES")]
    [InlineData("DIALOG YES abc")]
    [InlineData("DIALOG YES 0")]
    [InlineData("DIALOG NO 1")]
    [InlineData("DIALOG MAYBE")]
    public void Command_BadUsage(string raw)
    {
        var command = LobbyCommand.Parse(raw);

        Assert.Equal(LobbyCommandKind.BadUsage, command.Kind);
        Assert.StartsWith("FAIL usage:", command.Usage, StringComparison.Ordinal);
    }

    [Fact]
    public void Command_DialogYesCarriesContentId()
    {
        var yes = LobbyCommand.Parse("DIALOG YES 4611686018427387905");

        Assert.Equal(LobbyCommandKind.DialogYes, yes.Kind);
        Assert.Equal(4611686018427387905UL, yes.ContentId);
    }

    #endregion

    #region 回应格式与编排的判读

    [Fact]
    public void UnknownCommandReply_IsRecognisedByOrchestration()
    {
        Assert.Contains("unknown-command", LobbyReplies.UNKNOWN_COMMAND, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyTheIntendedRepliesLookLikeUnknownCommandOrSigscan()
    {
        // 编排见到这两个词就整个停手, 其它回应里不能带
        var others = AllFailReplies().Where(x => x != LobbyReplies.UNKNOWN_COMMAND);

        Assert.DoesNotContain(others, x => x.Contains("unknown-command", StringComparison.Ordinal) || x.Contains("sigscan-failed", StringComparison.Ordinal));
    }

    [Fact]
    public void CharaActionReplies_MatchOrchestrationBranches()
    {
        string Reply(CharaActionStatus status, bool enter = true) =>
            LobbyReplies.CharaAction(new CharaActionResult(status, enter, 2, 99, 18, 5, 1, 7, 21, 20));

        Assert.True(CatModuleReplies.IsOk(Reply(CharaActionStatus.Ok)));
        Assert.StartsWith("OK clicked", Reply(CharaActionStatus.Ok), StringComparison.Ordinal);
        Assert.StartsWith("OK selected", Reply(CharaActionStatus.Ok, false), StringComparison.Ordinal);

        // 带不可登录标志: 编排按 "flags=" 判
        Assert.Contains("flags=18", Reply(CharaActionStatus.Flags), StringComparison.Ordinal);
        // 点角色先弹别的框: 编排按 special-prompt 判, 且不能被当成 "flags="
        Assert.Contains("special-prompt", Reply(CharaActionStatus.SpecialPrompt), StringComparison.Ordinal);
        Assert.DoesNotContain("flags=", Reply(CharaActionStatus.SpecialPrompt), StringComparison.Ordinal);
        // 暂时锁定: 编排按 "locked" 判
        Assert.Contains("locked", Reply(CharaActionStatus.Locked), StringComparison.Ordinal);

        foreach (var status in Enum.GetValues<CharaActionStatus>().Where(x => x != CharaActionStatus.Locked))
            Assert.DoesNotContain("locked", Reply(status), StringComparison.Ordinal);

        foreach (var status in Enum.GetValues<CharaActionStatus>().Where(x => x is not (CharaActionStatus.Flags or CharaActionStatus.Ok)))
            Assert.DoesNotContain("flags=", Reply(status), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(CharaActionStatus.NotInList)]
    [InlineData(CharaActionStatus.ListWorldMismatch)]
    [InlineData(CharaActionStatus.BadList)]
    [InlineData(CharaActionStatus.PointersUnavailable)]
    public void CharaActionTransientFailures_AreRetriedByOrchestration(CharaActionStatus status)
    {
        Assert.True(CatModuleReplies.IsTransient(LobbyReplies.CharaAction(new CharaActionResult(status, true, World: 21, ListWorld: 20))));
    }

    [Theory]
    [InlineData(CharaActionStatus.NotApplied)]
    [InlineData(CharaActionStatus.ClickedOther)]
    [InlineData(CharaActionStatus.Ambiguous)]
    [InlineData(CharaActionStatus.DialogOpen)]
    [InlineData(CharaActionStatus.NotCharaSelect)]
    public void CharaActionHardFailures_AreNotRetried(CharaActionStatus status)
    {
        var reply = LobbyReplies.CharaAction(new CharaActionResult(status, true));

        Assert.False(CatModuleReplies.IsOk(reply));
        Assert.False(CatModuleReplies.IsTransient(reply));
    }

    [Fact]
    public void FocusReplies()
    {
        Assert.True(CatModuleReplies.IsOk(LobbyReplies.Focus(new FocusResult(FocusStatus.Switched, 23, 23, 2))));
        Assert.True(CatModuleReplies.IsOk(LobbyReplies.Focus(new FocusResult(FocusStatus.Already, 23))));
        Assert.True(CatModuleReplies.IsTransient(LobbyReplies.Focus(new FocusResult(FocusStatus.NotInList))));
        Assert.True(CatModuleReplies.IsTransient(LobbyReplies.Focus(new FocusResult(FocusStatus.NoWorldList))));
        Assert.False(CatModuleReplies.IsTransient(LobbyReplies.Focus(new FocusResult(FocusStatus.WorldNotListed, 99))));
        Assert.False(CatModuleReplies.IsTransient(LobbyReplies.Focus(new FocusResult(FocusStatus.NotApplied, 99, 21))));
    }

    [Fact]
    public void DialogReplies_OtherCharacterIsRecognisedByOrchestration()
    {
        Assert.Contains("other-character", LobbyReplies.Dialog(DialogVerdict.OtherCharacter, ""), StringComparison.Ordinal);
        Assert.True(CatModuleReplies.IsOk(LobbyReplies.Dialog(DialogVerdict.Click, "SelectYesno")));
        Assert.Equal("OK clicked addon=SelectOk", LobbyReplies.Dialog(DialogVerdict.Click, "SelectOk"));

        foreach (var verdict in Enum.GetValues<DialogVerdict>().Where(x => x != DialogVerdict.Click))
            Assert.False(CatModuleReplies.IsOk(LobbyReplies.Dialog(verdict, "")));
    }

    private static IEnumerable<string> AllFailReplies()
    {
        yield return LobbyReplies.UNKNOWN_COMMAND;
        yield return LobbyReplies.NOT_IMPLEMENTED;
        yield return LobbyReplies.EXCEPTION;
        yield return LobbyReplies.MAINTHREAD_TIMEOUT;
        yield return LobbyReplies.POINTERS_UNAVAILABLE;
        yield return LobbyReplies.Login(false);

        foreach (var verdict in Enum.GetValues<DialogVerdict>())
            yield return LobbyReplies.Dialog(verdict, "SelectOk");

        foreach (var status in Enum.GetValues<CharaActionStatus>())
            yield return LobbyReplies.CharaAction(new CharaActionResult(status, true, World: 1, ListWorld: 2));

        foreach (var status in Enum.GetValues<FocusStatus>())
            yield return LobbyReplies.Focus(new FocusResult(status, 5, 6, 7));
    }

    #endregion

    #region 对话框裁决

    private static YesGateFacts Yes(
        LobbyWhere where = LobbyWhere.CharaSelect, bool present = true, bool ready = true, uint lobbyId = 50, uint yesNoId = 50,
        bool requested = false, ulong about = 99, ulong expected = 99) =>
        new(where, present, ready, lobbyId, yesNoId, requested, about, expected);

    [Fact]
    public void Yes_ClicksOnlyWhenEveryPremiseHolds()
    {
        Assert.Equal(DialogVerdict.Click, DialogGate.DecideYes(Yes()));
    }

    [Theory]
    [InlineData(LobbyWhere.InGame, DialogVerdict.InGame)]
    [InlineData(LobbyWhere.Title, DialogVerdict.NotCharaSelect)]
    [InlineData(LobbyWhere.Busy, DialogVerdict.NotCharaSelect)]
    public void Yes_RefusesOutsideCharaSelect(LobbyWhere where, DialogVerdict expected)
    {
        Assert.Equal(expected, DialogGate.DecideYes(Yes(where)));
    }

    [Fact]
    public void Yes_RefusalReasons()
    {
        Assert.Equal(DialogVerdict.NoDialog, DialogGate.DecideYes(Yes(present: false)));
        Assert.Equal(DialogVerdict.NotReady, DialogGate.DecideYes(Yes(ready: false)));
        // 是/否框不是大厅为登录开的那个
        Assert.Equal(DialogVerdict.NotLoginConfirm, DialogGate.DecideYes(Yes(lobbyId: 51)));
        Assert.Equal(DialogVerdict.NotLoginConfirm, DialogGate.DecideYes(Yes(lobbyId: 0, yesNoId: 0)));
        // 登录请求已发出: 这时的是/否框在问要不要取消登录
        Assert.Equal(DialogVerdict.LoginRequested, DialogGate.DecideYes(Yes(requested: true)));
        // 客户端会登录的不是指定角色
        Assert.Equal(DialogVerdict.OtherCharacter, DialogGate.DecideYes(Yes(about: 100)));
        Assert.Equal(DialogVerdict.OtherCharacter, DialogGate.DecideYes(Yes(about: 0, expected: 0)));
    }

    [Fact]
    public void No_Gate()
    {
        Assert.Equal(DialogVerdict.Click, DialogGate.DecideNo(LobbyWhere.CharaSelect, true, true));
        Assert.Equal(DialogVerdict.NotReady, DialogGate.DecideNo(LobbyWhere.CharaSelect, true, false));
        Assert.Equal(DialogVerdict.NoDialog, DialogGate.DecideNo(LobbyWhere.CharaSelect, false, false));
        Assert.Equal(DialogVerdict.InGame, DialogGate.DecideNo(LobbyWhere.InGame, true, true));
        Assert.Equal(DialogVerdict.NotCharaSelect, DialogGate.DecideNo(LobbyWhere.Title, true, true));
    }

    private static OkGateFacts Ok(string text, LobbyWhere where = LobbyWhere.CharaSelect, int queue = 0, uint lobbyId = 50, uint okId = 50, bool present = true) =>
        new(where, present, lobbyId, okId, queue, text);

    [Fact]
    public void Notice_ClicksForcedLogoutNotice_EvenWhenLobbyOwned()
    {
        var text = "The character selected was not properly logged out or is currently logged in on another client. Logging character out. Please wait to log in again.";

        Assert.Equal(DialogVerdict.Click, DialogGate.DecideOk(Ok(text), true));
        Assert.True(LobbyTextRules.IsForcedLogoutPrompt(text));
    }

    [Theory]
    [InlineData("The server is currently congested.\nPlayers in queue: 12.")]
    [InlineData("現在サーバーが混み合っています。順次ログイン処理を行っていますので、そのまましばらくお待ちください。（現在0人待ちです）")]
    [InlineData("Auf dieser Welt herrscht momentan hoher Andrang. Bitte lass dieses Fenster geöffnet und habe etwas Geduld.\nPlatz auf der Warteliste: 0")]
    [InlineData("Ce Monde est plein. \nJoueurs en attente : 0")]
    public void Notice_NeverClicksQueuePrompt(string text)
    {
        Assert.Equal(DialogVerdict.Queueing, DialogGate.DecideOk(Ok(text), true));
    }

    [Fact]
    public void Notice_RefusesWhenQueuePositionIsPositiveOrTextUnreadable()
    {
        Assert.Equal(DialogVerdict.Queueing, DialogGate.DecideOk(Ok("some notice", queue: 3), true));
        Assert.Equal(DialogVerdict.Queueing, DialogGate.DecideOk(Ok(string.Empty), true));
    }

    [Fact]
    public void Notice_OnlyInCharaSelectAndNeverInGame()
    {
        Assert.Equal(DialogVerdict.NotCharaSelect, DialogGate.DecideOk(Ok("notice", LobbyWhere.Title), true));
        Assert.Equal(DialogVerdict.InGame, DialogGate.DecideOk(Ok("notice", LobbyWhere.InGame), true));
        Assert.Equal(DialogVerdict.NoDialog, DialogGate.DecideOk(Ok("notice", present: false), true));
    }

    [Fact]
    public void Ok_RefusesLobbyOwnedBox_ButClicksOthers()
    {
        Assert.Equal(DialogVerdict.Queueing, DialogGate.DecideOk(Ok("anything"), false));
        Assert.Equal(DialogVerdict.Click, DialogGate.DecideOk(Ok("anything", lobbyId: 51), false));
        Assert.Equal(DialogVerdict.Queueing, DialogGate.DecideOk(Ok("anything", lobbyId: 51, queue: 1), false));
    }

    #endregion

    #region 文字判断

    [Theory]
    [InlineData("Are you certain you wish to leave the queue?", true)]
    [InlineData("ログイン処理を中断してもよろしいですか？", true)]
    [InlineData("Den Login-Prozess abbrechen?", true)]
    [InlineData("Voulez-vous quitter la queue ?", true)]
    [InlineData("Log in with Miqo'te Rin?", false)]
    public void CancelLoginPrompt(string text, bool expected)
    {
        Assert.Equal(expected, LobbyTextRules.IsCancelLoginPrompt(text));
    }

    [Theory]
    [InlineData("Log in with UNKNOWN?\n※Cancel and access the subcommand menu to re-edit your character.", true)]
    [InlineData("「Rin」でログインします。\nよろしいですか？\n\n※キャラクターの再編集はサブコマンドから実行できます。", true)]
    [InlineData("Willst du dich mit Rin einloggen?\n※ Ruf das Untermenü auf, um das Aussehen deines Charakters umzugestalten.", true)]
    [InlineData("Souhaitez-vous vous connecter avec Rin ?\nAnnulez et accédez au sous-menu pour modifier votre personnage.", true)]
    [InlineData("Log in with Rin?", false)]
    public void FantasiaPrompt(string text, bool expected)
    {
        Assert.Equal(expected, LobbyTextRules.IsFantasiaPrompt(text));
    }

    [Theory]
    [InlineData("Logging out character from a previous play session.\nPlease wait and try logging in later.")]
    [InlineData("前回のログアウト処理が正常に行われていないか、他のクライアントからログイン中のため、対象キャラクターをログアウトしました。")]
    [InlineData("Da die letzte Sitzung nicht korrekt beendet wurde oder der Charakter noch auf einem anderen Client eingeloggt war, wurde die aktive Sitzung nun beendet.")]
    [InlineData("La précédente déconnexion ne s'était pas faite correctement ou une connexion était en cours avec un autre client de jeu.")]
    public void ForcedLogoutPrompt(string text)
    {
        Assert.True(LobbyTextRules.IsForcedLogoutPrompt(text));
        Assert.False(LobbyTextRules.IsQueuePrompt(text));
    }

    [Theory]
    [InlineData("This World is currently full. Please wait until an opening is available and try again.")]
    [InlineData("Log in with Rin?")]
    [InlineData("")]
    public void NonQueueTexts(string text)
    {
        Assert.False(LobbyTextRules.IsQueuePrompt(text));
    }

    [Fact]
    public void Sanitize_ReplacesControlCharactersAndCapsLength()
    {
        Assert.Equal("a b c", LobbyTextRules.Sanitize("a\tb\nc"));
        Assert.Equal(10, LobbyTextRules.Sanitize(new string('x', 500), 10).Length);
        Assert.Equal("?", LobbyTextRules.Sanitize("\ud800"));
        Assert.Equal("𠮷", LobbyTextRules.Sanitize("𠮷"));
    }

    [Theory]
    [InlineData("Miqo'te Rin", false, true)]
    [InlineData("", false, false)]
    [InlineData("", true, true)]
    [InlineData("bad\u0001name", true, false)]
    [InlineData("bad\ufffdname", true, false)]
    public void PlausibleName(string text, bool allowEmpty, bool expected)
    {
        Assert.Equal(expected, LobbyTextRules.IsPlausibleName(text, allowEmpty));
    }

    #endregion

    #region 选角判断

    [Fact]
    public void Pick_ByContentIdAndByName()
    {
        var entries = new[] { (0, Row(1, "A One")), (1, Row(2, "B Two")), (2, Row(3, "B Two", home: "Ifrit")) };

        Assert.Equal(1, CharaLogic.Pick(entries, "2", 2).Position);
        Assert.Equal(PickStatus.Ambiguous, CharaLogic.Pick(entries, "B Two", 0).Status);
        Assert.Equal(0, CharaLogic.Pick(entries, "A One", 0).Position);
        Assert.Equal(PickStatus.None, CharaLogic.Pick(entries, "Nobody", 0).Status);
        Assert.Equal(PickStatus.None, CharaLogic.Pick(entries, "9", 9).Status);
        // 名字区分大小写
        Assert.Equal(PickStatus.None, CharaLogic.Pick(entries, "a one", 0).Status);
    }

    [Fact]
    public void CheckEnter_Gates()
    {
        Assert.Null(CharaLogic.CheckEnter(Row(1, "A One"), false));
        Assert.Equal(CharaActionStatus.Locked, CharaLogic.CheckEnter(Row(1, "A One"), true));
        // 超域中 (16) 照常走登录确认框
        Assert.Null(CharaLogic.CheckEnter(Row(1, "A One", flags: 16), false));

        foreach (byte flag in new byte[] { 1, 2, 4, 8, 32, 18 })
            Assert.Equal(CharaActionStatus.Flags, CharaLogic.CheckEnter(Row(1, "A One", flags: flag), false));

        Assert.Equal(CharaActionStatus.SpecialPrompt, CharaLogic.CheckEnter(Row(1, "A One", click: 1), false));
        // 幻想药那个框 (bit2) 由编排按文字替人答, 这里不拦
        Assert.Null(CharaLogic.CheckEnter(Row(1, "A One", click: 4), false));
    }

    [Fact]
    public void TargetWorld_TravellingCharactersAreListedUnderCurrentWorld()
    {
        var home      = new CharaRow(1, 0, 0, 30, 23, "A", "Gungnir", "Asura", 0);
        var travelling = home with { LoginFlags = 16 };

        Assert.Equal(23, CharaLogic.TargetWorld(home));
        Assert.Equal(30, CharaLogic.TargetWorld(travelling));
    }

    #endregion
}
