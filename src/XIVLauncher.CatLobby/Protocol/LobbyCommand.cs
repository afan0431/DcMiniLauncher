namespace XIVLauncher.CatLobby.Protocol;

public enum LobbyCommandKind
{
    Ping,
    Version,
    LobbyState,
    TitleReady,
    Login,
    SkipMovie,
    Charas,
    FocusChara,
    SelectChara,
    EnterChara,
    DialogYes,
    DialogNo,
    DialogOk,
    DialogNotice,
    WhoAmI,
    Unknown,

    /// <summary>认得命令名, 但参数不对; <see cref="LobbyCommand.Usage" /> 是要回给对方的 FAIL 文本</summary>
    BadUsage,

    /// <summary>国服小模块里有、这里没有实现的写法（如 ENTERCHARA DIRECT）</summary>
    NotImplemented
}

/// <summary>
///     一条已解析的命令。Who 是 FOCUSCHARA / SELECTCHARA / ENTERCHARA 的参数（角色名或纯数字 ContentId）, ContentId 是
///     DIALOG YES 的参数或 Who 为纯数字时的 ContentId。
/// </summary>
public sealed record LobbyCommand(LobbyCommandKind Kind, string Who = "", ulong ContentId = 0, string Usage = "")
{
    public static LobbyCommand Parse(string? raw)
    {
        var command = (raw ?? string.Empty).Trim('\0', ' ', '\t', '\r', '\n');

        switch (command)
        {
            case "PING":       return new(LobbyCommandKind.Ping);
            case "VERSION":    return new(LobbyCommandKind.Version);
            case "LOBBYSTATE": return new(LobbyCommandKind.LobbyState);
            case "TITLEREADY": return new(LobbyCommandKind.TitleReady);
            case "LOGIN":      return new(LobbyCommandKind.Login);
            case "SKIPMOVIE":  return new(LobbyCommandKind.SkipMovie);
            case "CHARAS":     return new(LobbyCommandKind.Charas);
            case "WHOAMI":     return new(LobbyCommandKind.WhoAmI);
        }

        // 角色名可能带空格（外服的「名 姓」）, 所以前缀之后整行剩下的都算参数
        if (command.StartsWith("FOCUSCHARA ", StringComparison.Ordinal))
            return CharaCommand(LobbyCommandKind.FocusChara, "FOCUSCHARA", command["FOCUSCHARA ".Length..]);

        if (command.StartsWith("SELECTCHARA ", StringComparison.Ordinal))
            return CharaCommand(LobbyCommandKind.SelectChara, "SELECTCHARA", command["SELECTCHARA ".Length..]);

        if (command == "ENTERCHARA DIRECT" || command.StartsWith("ENTERCHARA DIRECT ", StringComparison.Ordinal))
            return new(LobbyCommandKind.NotImplemented);

        if (command.StartsWith("ENTERCHARA ", StringComparison.Ordinal))
            return CharaCommand(LobbyCommandKind.EnterChara, "ENTERCHARA", command["ENTERCHARA ".Length..]);

        if (command.StartsWith("DIALOG ", StringComparison.Ordinal))
            return ParseDialog(command["DIALOG ".Length..]);

        return new(LobbyCommandKind.Unknown);
    }

    /// <summary>纯数字当 ContentId, 否则当角色名</summary>
    public static ulong ParseContentId(string who) =>
        who.Length > 0 && who.All(char.IsAsciiDigit) && ulong.TryParse(who, out var value) ? value : 0UL;

    private static LobbyCommand CharaCommand(LobbyCommandKind kind, string name, string who)
    {
        who = who.Trim();

        return who.Length == 0
                   ? new(LobbyCommandKind.BadUsage, Usage: $"FAIL usage: {name} <角色名|contentId>")
                   : new(kind, who, ParseContentId(who));
    }

    private static LobbyCommand ParseDialog(string arguments)
    {
        const string USAGE = "FAIL usage: DIALOG <YES <contentId>|NO|OK|NOTICE>";

        var space  = arguments.IndexOf(' ');
        var button = space < 0 ? arguments : arguments[..space];
        var extra  = space < 0 ? string.Empty : arguments[(space + 1)..].Trim();

        switch (button)
        {
            case "YES":
            {
                // 「是」必须说明是替哪个角色确认
                var contentId = ParseContentId(extra);
                return contentId == 0 ? new(LobbyCommandKind.BadUsage, Usage: USAGE) : new(LobbyCommandKind.DialogYes, ContentId: contentId);
            }

            case "NO":     return extra.Length == 0 ? new(LobbyCommandKind.DialogNo) : new(LobbyCommandKind.BadUsage, Usage: USAGE);
            case "OK":     return extra.Length == 0 ? new(LobbyCommandKind.DialogOk) : new(LobbyCommandKind.BadUsage, Usage: USAGE);
            case "NOTICE": return extra.Length == 0 ? new(LobbyCommandKind.DialogNotice) : new(LobbyCommandKind.BadUsage, Usage: USAGE);
            default:       return new(LobbyCommandKind.BadUsage, Usage: USAGE);
        }
    }
}
