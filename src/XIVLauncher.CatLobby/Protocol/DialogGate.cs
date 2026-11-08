namespace XIVLauncher.CatLobby.Protocol;

/// <summary>DIALOG 命令的裁决; 与国服小模块 OpDialog 的返回值一一对应</summary>
public enum DialogVerdict
{
    /// <summary>可以点</summary>
    Click,

    /// <summary>界面上没有对应的对话框</summary>
    NoDialog,

    /// <summary>那是排队提示, 不点（点了就是取消排队）</summary>
    Queueing,

    /// <summary>在游戏里, 不点</summary>
    InGame,

    /// <summary>不在选角界面</summary>
    NotCharaSelect,

    /// <summary>是/否框不是大厅为登录开的那个</summary>
    NotLoginConfirm,

    /// <summary>登录请求已经发出, 这时的是/否框是在问要不要取消登录</summary>
    LoginRequested,

    /// <summary>答「是」会登录的不是指定的角色</summary>
    OtherCharacter,

    /// <summary>框还在打开, 稍后再试</summary>
    NotReady,

    /// <summary>Dialogue 的确定按钮找不到</summary>
    NoButton
}

/// <summary>答「是」时现查的全部前提</summary>
public sealed record YesGateFacts
(
    LobbyWhere Where,
    bool       YesNoPresent,
    bool       YesNoReady,
    uint       LobbyDialogId,
    uint       YesNoId,
    bool       LoginRequested,
    ulong      AboutToLoginContentId,
    ulong      ExpectedContentId
);

/// <summary>点确定框前的现状</summary>
public sealed record OkGateFacts
(
    LobbyWhere Where,
    bool       OkPresent,
    uint       LobbyDialogId,
    uint       OkId,
    int        QueuePosition,
    string     Text
);

/// <summary>
///     点对话框前的安全裁决。「是」等于替人确认登录, 所以全部前提在同一次主线程执行里现查, 不信编排上一次读到的状态。
/// </summary>
public static class DialogGate
{
    /// <summary>
    ///     答「是」: 在选角界面; 是/否框已就绪; 它是大厅自己为登录开的那个（AgentLobby.DialogAddonId）;
    ///     登录请求还没发出（TemporaryLocked 为假）; 客户端这时会登录的角色就是指定的那个。
    /// </summary>
    public static DialogVerdict DecideYes(YesGateFacts facts)
    {
        if (facts.Where == LobbyWhere.InGame)
            return DialogVerdict.InGame;

        if (facts.Where != LobbyWhere.CharaSelect)
            return DialogVerdict.NotCharaSelect;

        if (!facts.YesNoPresent)
            return DialogVerdict.NoDialog;

        if (!facts.YesNoReady)
            return DialogVerdict.NotReady;

        if (facts.LobbyDialogId == 0 || facts.LobbyDialogId != facts.YesNoId)
            return DialogVerdict.NotLoginConfirm;

        if (facts.LoginRequested)
            return DialogVerdict.LoginRequested;

        if (facts.ExpectedContentId == 0 || facts.AboutToLoginContentId != facts.ExpectedContentId)
            return DialogVerdict.OtherCharacter;

        return DialogVerdict.Click;
    }

    /// <summary>答「否」: 只有界面条件, 没有身份核对</summary>
    public static DialogVerdict DecideNo(LobbyWhere where, bool yesNoPresent, bool yesNoReady)
    {
        if (where == LobbyWhere.InGame)
            return DialogVerdict.InGame;

        if (where != LobbyWhere.CharaSelect)
            return DialogVerdict.NotCharaSelect;

        if (!yesNoPresent)
            return DialogVerdict.NoDialog;

        return yesNoReady ? DialogVerdict.Click : DialogVerdict.NotReady;
    }

    /// <summary>
    ///     点确定框。排队提示不点: 有排队名次时不管是哪个框都不点; 文字里有排队用语的不点。
    ///     <paramref name="notice" /> 为假（DIALOG OK）时大厅自己开的确定框一律不点（名次读不到时它照样可能是排队框）;
    ///     为真（DIALOG NOTICE）时只在选角界面、读得到文字且不是排队用语时才点大厅自己开的。
    /// </summary>
    public static DialogVerdict DecideOk(OkGateFacts facts, bool notice)
    {
        if (facts.Where == LobbyWhere.InGame)
            return DialogVerdict.InGame;

        if (!facts.OkPresent)
            return DialogVerdict.NoDialog;

        if (facts.QueuePosition > 0)
            return DialogVerdict.Queueing;

        if (notice)
        {
            if (facts.Where != LobbyWhere.CharaSelect)
                return DialogVerdict.NotCharaSelect;

            return facts.Text.Length == 0 || LobbyTextRules.IsQueuePrompt(facts.Text) ? DialogVerdict.Queueing : DialogVerdict.Click;
        }

        var lobbyOwned = facts.LobbyDialogId != 0 && facts.LobbyDialogId == facts.OkId;

        return lobbyOwned ? DialogVerdict.Queueing : DialogVerdict.Click;
    }
}
