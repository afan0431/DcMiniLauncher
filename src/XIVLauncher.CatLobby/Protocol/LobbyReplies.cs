using System.Globalization;
using System.Text;

namespace XIVLauncher.CatLobby.Protocol;

/// <summary>
///     各命令的回应拼装。格式与国服小模块（XIVLauncher.MiniModule）逐字一致, DML 里的解析函数
///     （CatModuleReplies / CharaSelectReader / MiniModuleClient.TryParseVersion）直接解析这里的输出。
/// </summary>
public static class LobbyReplies
{
    /// <summary>模块版本: 要不低于编排要求的最低版本（MiniModuleClient.AutoEnterMinimumVersion）</summary>
    public const string VERSION = "0.11.0";

    /// <summary>客户端一次读 8192 字节, 整条回应不能超</summary>
    public const int MAX_RESPONSE_BYTES = 8192;

    /// <summary>CHARAS 正文超过这个字节数就整条失败, 不给半张表</summary>
    private const int MAX_CHARAS_BODY_BYTES = 7600;

    public const string MAINTHREAD_TIMEOUT      = "FAIL mainthread-timeout";
    public const string POINTERS_UNAVAILABLE    = "FAIL pointers-unavailable";
    public const string EXCEPTION               = "FAIL exception";
    public const string UNKNOWN_COMMAND         = "FAIL unknown-command";
    public const string NOT_IMPLEMENTED         = "FAIL not-implemented";

    private static string Num(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Bool(bool value) => value ? "1" : "0";

    public static string Version(int pid, string logPath) =>
        $"OK version={VERSION} pid={Num(pid)} mode=full log={logPath}";

    /// <summary>
    ///     对外报的排队名次: AgentLobby 的名次原值优先; 名次是 0 但确定框的文字是排队用语时, 取文字里的数字, 取不到记 1。
    ///     WHY: 编排只认「名次 &gt; 0」或中文「排队」二字判断排队, 国际服的排队框文字不含中文; 名次 &gt; 0 即「在排队」, 1 是「名次未知」的占位。
    /// </summary>
    public static int EffectiveQueue(LobbySnapshot lobby)
    {
        if (lobby.Queue > 0)
            return lobby.Queue;

        if (!lobby.Ok.Present || !LobbyTextRules.IsQueuePrompt(lobby.Ok.Text))
            return 0;

        return Math.Max(1, LobbyTextRules.ParseQueueNumber(lobby.Ok.Text));
    }

    public static string LobbyState(LobbySnapshot lobby)
    {
        var queue = EffectiveQueue(lobby);

        var builder = new StringBuilder();
        builder.Append("OK where=").Append(LobbyWhereNames.Name(lobby.Where))
               .Append(" world=").Append(Num(lobby.World))
               .Append(" worldIndex=").Append(Num(lobby.WorldIndex))
               .Append(" selectedIndex=").Append(Num(lobby.SelectedIndex))
               .Append(" hovered=").Append(UNum(lobby.Hovered))
               .Append(" locked=").Append(Bool(lobby.Locked))
               .Append(" stage=").Append(Num(lobby.UpdateStage))
               .Append(" uiStage=").Append(Num(lobby.UiStage))
               .Append(" queue=").Append(Num(queue))
               .Append(" dialogId=").Append(Num(lobby.DialogId))
               .Append(" yesno=").Append(Bool(lobby.YesNo.Present))
               .Append(" ok=").Append(Bool(lobby.Ok.Present))
               .Append(" dialogue=").Append(Bool(lobby.Dialogue.Present))
               .Append(" loading=").Append(Bool(lobby.Loading))
               .Append(" listWorld=").Append(Num(lobby.ListWorld))
               .Append(" queuePrompt=").Append(Bool(lobby.Ok.Present && LobbyTextRules.IsQueuePrompt(lobby.Ok.Text)));

        AppendDialogLine(builder, "SelectYesno", lobby.YesNo);
        AppendDialogLine(builder, "SelectOk", lobby.Ok);
        AppendDialogLine(builder, "Dialogue", lobby.Dialogue);

        // 最后一行 T 是其中最该看的那个: 是/否框优先, 其次错误框, 最后确定框
        var primary = lobby.YesNo.Present ? lobby.YesNo : lobby.Dialogue.Present ? lobby.Dialogue : lobby.Ok.Present ? lobby.Ok : null;

        if (primary != null)
            builder.Append("\nT ").Append(Clean(primary.Text));

        return builder.ToString();
    }

    private static void AppendDialogLine(StringBuilder builder, string name, DialogSnapshot dialog)
    {
        if (!dialog.Present)
            return;

        builder.Append("\nD\t").Append(name)
               .Append('\t').Append(Num(dialog.Id))
               .Append('\t').Append(Bool(dialog.Ready))
               .Append('\t').Append(Bool(dialog.Visible))
               .Append('\t').Append(Clean(dialog.Text));
    }

    private static string Clean(string text) => LobbyTextRules.Sanitize(text);

    public static string TitleReady(bool ready) => ready ? "OK ready=1" : "OK ready=0";

    /// <summary>LOGIN: 点了「开始游戏」回 OK; 不在标题界面回 no-title-menu</summary>
    public static string Login(bool clicked) => clicked ? "OK" : "FAIL no-title-menu";

    public static string Charas(CharaListSnapshot list)
    {
        var body = new StringBuilder();

        foreach (var row in list.Rows)
        {
            body.Append("\nC\t").Append(UNum(row.ContentId))
                .Append('\t').Append(Num(row.Index))
                .Append('\t').Append(Num(row.LoginFlags))
                .Append('\t').Append(Num(row.CurrentWorldId))
                .Append('\t').Append(Num(row.HomeWorldId))
                .Append('\t').Append(CleanName(row.Name))
                .Append('\t').Append(CleanName(row.CurrentWorldName))
                .Append('\t').Append(CleanName(row.HomeWorldName))
                .Append('\t').Append(Num(row.ClickFlags));
        }

        // 半张表会让编排把「号里有好几个角色」看成「只有一个」, 宁可整条失败
        if (Encoding.UTF8.GetByteCount(body.ToString()) > MAX_CHARAS_BODY_BYTES)
            return "FAIL too-large";

        var header = $"OK where={LobbyWhereNames.Name(list.Where)} n={Num(list.Rows.Count)} total={Num(list.Total)} " +
                     $"selected={UNum(list.SelectedContentId)} selectedIndex={Num(list.SelectedIndex)} " +
                     $"hovered={UNum(list.HoveredContentId)} hoveredIndex={Num(list.HoveredIndex)} " +
                     $"source=dc skipped={Num(list.Skipped)} invalid={Num(list.Invalid)}";

        return header + body;
    }

    private static string UNum(ulong value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>角色名、世界名进制表符分隔的行: 去掉制表符与换行</summary>
    private static string CleanName(string text) => LobbyTextRules.Sanitize(text, 64);

    public static string WhoAmI(WhoAmISnapshot who) =>
        $"OK loaded={Bool(who.Loaded)} cid={UNum(who.ContentId)} world={Num(who.World)} home={Num(who.Home)} " +
        $"worldName={CleanName(who.WorldName)} homeName={CleanName(who.HomeName)} loggedIn={Bool(who.LoggedIn)} inZone={Bool(who.InZone)} " +
        $"lobbyCid={UNum(who.LobbyContentId)} name={CleanName(who.Name)}";

    /// <summary>
    ///     DIALOG 的回应。<paramref name="clicked" /> 是 Click 裁决时点到的 addon 名（SelectYesno / SelectOk / Dialogue）
    /// </summary>
    public static string Dialog(DialogVerdict verdict, string clicked) => verdict switch
    {
        DialogVerdict.Click           => $"OK clicked addon={clicked}",
        DialogVerdict.NoDialog        => "FAIL no-dialog",
        DialogVerdict.Queueing        => "FAIL queueing",
        DialogVerdict.InGame          => "FAIL ingame",
        DialogVerdict.NoButton        => "FAIL no-button",
        DialogVerdict.NotCharaSelect  => "FAIL not-charaselect",
        DialogVerdict.NotLoginConfirm => "FAIL not-login-confirm",
        DialogVerdict.LoginRequested  => "FAIL login-requested",
        DialogVerdict.OtherCharacter  => "FAIL other-character",
        DialogVerdict.NotReady        => "FAIL not-ready",
        _                             => EXCEPTION
    };

    public static string CharaAction(CharaActionResult result)
    {
        var command = result.Enter ? "clicked" : "selected";

        return result.Status switch
        {
            CharaActionStatus.Ok => $"OK {command} index={Num(result.Position)} cid={UNum(result.ContentId)}",
            CharaActionStatus.NotInList => "FAIL not-in-list",
            CharaActionStatus.ListWorldMismatch => $"FAIL not-in-list listWorld={Num(result.ListWorld)} world={Num(result.World)}",
            CharaActionStatus.NotCharaSelect => "FAIL not-charaselect",
            CharaActionStatus.BadList => "FAIL bad-list",
            CharaActionStatus.Ambiguous => "FAIL ambiguous",
            CharaActionStatus.NotApplied => $"FAIL not-applied selectedIndex={Num(result.SelectedIndex)} hovered={UNum(result.Hovered)}",
            CharaActionStatus.Locked => "FAIL locked",
            CharaActionStatus.Flags => $"FAIL flags={Num(result.LoginFlags)}",
            CharaActionStatus.ClickedOther => $"FAIL clicked-other selectedIndex={Num(result.SelectedIndex)} hovered={UNum(result.Hovered)}",
            CharaActionStatus.DialogOpen => "FAIL dialog-open",
            CharaActionStatus.SpecialPrompt => $"FAIL special-prompt flags74c={Num(result.ClickFlags)}",
            CharaActionStatus.PointersUnavailable => POINTERS_UNAVAILABLE,
            _ => EXCEPTION
        };
    }

    public static string Focus(FocusResult result) => result.Status switch
    {
        FocusStatus.Switched => $"OK switched world={Num(result.TargetWorld)} slot={Num(result.Slot)}",
        FocusStatus.Already => $"OK already world={Num(result.TargetWorld)}",
        FocusStatus.NotInList => "FAIL not-in-list",
        FocusStatus.NoWorldList => "FAIL no-world-list",
        FocusStatus.WorldNotListed => $"FAIL world-not-listed world={Num(result.TargetWorld)}",
        FocusStatus.NotCharaSelect => "FAIL not-charaselect",
        FocusStatus.BadList => "FAIL bad-list",
        FocusStatus.Ambiguous => "FAIL ambiguous",
        FocusStatus.NotApplied => $"FAIL not-applied world={Num(result.AfterWorld)} target={Num(result.TargetWorld)}",
        FocusStatus.DialogOpen => "FAIL dialog-open",
        FocusStatus.PointersUnavailable => POINTERS_UNAVAILABLE,
        _ => EXCEPTION
    };
}
