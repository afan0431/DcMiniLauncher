namespace XIVLauncher.CatLobby.Protocol;

public enum CharaActionStatus
{
    /// <summary>成功</summary>
    Ok,

    /// <summary>当前服务器的列表里没有这个角色（或列表还没载入）</summary>
    NotInList,

    /// <summary>存着的角色列表不是当前服务器的那份, 列表里的位置对回调没有意义</summary>
    ListWorldMismatch,

    NotCharaSelect,
    BadList,
    Ambiguous,

    /// <summary>发了选中回调但客户端选中的不是它</summary>
    NotApplied,

    /// <summary>客户端暂时锁定（提示稍后再登录）</summary>
    Locked,

    /// <summary>角色带着不能直接登录的标志</summary>
    Flags,

    /// <summary>已经点了, 但客户端选中的不是要找的角色</summary>
    ClickedOther,

    /// <summary>界面上开着对话框</summary>
    DialogOpen,

    /// <summary>点这个角色会先弹别的是/否框</summary>
    SpecialPrompt,

    PointersUnavailable,
    Exception
}

/// <summary>SELECTCHARA / ENTERCHARA 的结果</summary>
public sealed record CharaActionResult
(
    CharaActionStatus Status,
    bool              Enter,
    int               Position      = -1,
    ulong             ContentId     = 0,
    uint              LoginFlags    = 0,
    uint              ClickFlags    = 0,
    int               SelectedIndex = 0,
    ulong             Hovered       = 0,
    ushort            World         = 0,
    ushort            ListWorld     = 0
);

public enum FocusStatus
{
    /// <summary>已切换到角色所在的服务器</summary>
    Switched,

    /// <summary>本来就在</summary>
    Already,

    /// <summary>大区里没这个角色（或列表还没载入）</summary>
    NotInList,

    /// <summary>服务器列表还没出来</summary>
    NoWorldList,

    /// <summary>目标服务器不在本大区</summary>
    WorldNotListed,

    NotCharaSelect,
    BadList,
    Ambiguous,
    NotApplied,
    DialogOpen,
    PointersUnavailable,
    Exception
}

public sealed record FocusResult(FocusStatus Status, ushort TargetWorld = 0, ushort AfterWorld = 0, int Slot = -1);

public enum PickStatus
{
    Found,
    None,
    Ambiguous
}

public sealed record PickResult(PickStatus Status, int Position, CharaRow? Row);

/// <summary>选角相关的纯判断</summary>
public static class CharaLogic
{
    /// <summary>LoginFlags 里的「超域中」位</summary>
    public const byte LOGIN_FLAG_DC_TRAVELING = 16;

    /// <summary>除「超域中」之外的任何一位都意味着点下去弹出来的不是登录确认框</summary>
    public const byte LOGIN_FLAG_BLOCKING_MASK = 0xFF & unchecked((byte)~LOGIN_FLAG_DC_TRAVELING);

    /// <summary>条目 +0x74C 的标志: 第 0 位置位时点角色先弹 1.0 继承角色要重设职业与外貌的是/否框</summary>
    public const uint CLICK_PROMPT_MASK = 0x1;

    /// <summary>在列表里按 ContentId（contentId 不为 0 时）或角色名找角色; 不止一个算同名</summary>
    public static PickResult Pick(IEnumerable<(int Position, CharaRow Row)> entries, string who, ulong contentId)
    {
        var found = new List<(int Position, CharaRow Row)>(1);

        foreach (var entry in entries)
        {
            var matches = contentId != 0 ? entry.Row.ContentId == contentId : string.Equals(entry.Row.Name, who, StringComparison.Ordinal);

            if (matches)
                found.Add(entry);
        }

        return found.Count switch
        {
            0 => new PickResult(PickStatus.None, -1, null),
            1 => new PickResult(PickStatus.Found, found[0].Position, found[0].Row),
            _ => new PickResult(PickStatus.Ambiguous, -1, null)
        };
    }

    /// <summary>
    ///     点角色（左键）前的检查: 客户端暂时锁定、角色带不可登录的标志、点下去先弹别的是/否框的, 都不点。返回 null 表示可以点
    /// </summary>
    public static CharaActionStatus? CheckEnter(CharaRow row, bool temporaryLocked)
    {
        if (temporaryLocked)
            return CharaActionStatus.Locked;

        if ((row.LoginFlags & LOGIN_FLAG_BLOCKING_MASK) != 0)
            return CharaActionStatus.Flags;

        if ((row.ClickFlags & CLICK_PROMPT_MASK) != 0)
            return CharaActionStatus.SpecialPrompt;

        return null;
    }

    /// <summary>角色列在哪个服务器下: 超域中的列在它当前所在的服务器下, 其余（包括同大区跨服中的）列在原始服务器下</summary>
    public static ushort TargetWorld(CharaRow row) => row.LoginFlags == LOGIN_FLAG_DC_TRAVELING ? row.CurrentWorldId : row.HomeWorldId;
}
