namespace XIVLauncher.CatLobby.Protocol;

/// <summary>游戏现在在哪个界面（数值与国服小模块的 OpWhere 一致）</summary>
public enum LobbyWhere
{
    Unknown     = -1,
    Busy        = 0,
    Title       = 1,
    CharaSelect = 2,
    InGame      = 3
}

public static class LobbyWhereNames
{
    public static string Name(LobbyWhere where) => where switch
    {
        LobbyWhere.InGame      => "ingame",
        LobbyWhere.CharaSelect => "charaselect",
        LobbyWhere.Title       => "title",
        LobbyWhere.Busy        => "busy",
        _                      => "unknown"
    };
}

/// <summary>一个对话框 addon（SelectYesno / SelectOk / Dialogue）的现状; Present 为假表示界面上没有它</summary>
public sealed record DialogSnapshot(bool Present, uint Id, bool Ready, bool Visible, string Text)
{
    public static readonly DialogSnapshot None = new(false, 0, false, false, string.Empty);
}

/// <summary>LOBBYSTATE 的数据。Queue 是 AgentLobby 的排队名次原值, 对外报的值见 <see cref="LobbyReplies.EffectiveQueue" /></summary>
public sealed record LobbySnapshot
(
    LobbyWhere     Where,
    ushort         World,
    int            WorldIndex,
    int            SelectedIndex,
    ulong          Hovered,
    bool           Locked,
    uint           UpdateStage,
    uint           UiStage,
    int            Queue,
    uint           DialogId,
    bool           Loading,
    ushort         ListWorld,
    DialogSnapshot YesNo,
    DialogSnapshot Ok,
    DialogSnapshot Dialogue
);

/// <summary>选角列表里的一个角色。世界名是 World 表的英文名（如 Asura）</summary>
public sealed record CharaRow
(
    ulong  ContentId,
    int    Index,
    byte   LoginFlags,
    ushort CurrentWorldId,
    ushort HomeWorldId,
    string Name,
    string CurrentWorldName,
    string HomeWorldName,
    uint   ClickFlags
);

/// <summary>CHARAS 的数据: 整个大区的角色。Invalid = 没列出来、又不是空位的条目数</summary>
public sealed record CharaListSnapshot
(
    LobbyWhere          Where,
    int                 Total,
    int                 Skipped,
    int                 Invalid,
    int                 SelectedIndex,
    int                 HoveredIndex,
    ulong               SelectedContentId,
    ulong               HoveredContentId,
    IReadOnlyList<CharaRow> Rows
);

/// <summary>WHOAMI 的数据; 世界相关项只在 LoggedIn 为真时有值</summary>
public sealed record WhoAmISnapshot
(
    bool   Loaded,
    ulong  ContentId,
    ushort World,
    ushort Home,
    string WorldName,
    string HomeName,
    bool   LoggedIn,
    bool   InZone,
    ulong  LobbyContentId,
    string Name
);
