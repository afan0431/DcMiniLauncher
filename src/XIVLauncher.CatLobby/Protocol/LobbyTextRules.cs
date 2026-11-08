using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace XIVLauncher.CatLobby.Protocol;

/// <summary>
///     对话框提示文字的判断与清洗。国际服客户端是英文 / 日文 / 德文 / 法文, 关键词出自游戏数据表 Error / Lobby 的各语言原文。
///     未实机: 以下关键词只对照过数据表原文, 没有在真实的排队界面上核对过。
/// </summary>
public static partial class LobbyTextRules
{
    /// <summary>
    ///     排队提示（Error 表第 13206 行「服务器拥挤, 排队人数: n」）里的词: 英 queue / 日 人待ち 混み合って / 德 Warteliste / 法 en attente。
    ///     「是否退出排队」那个是/否框（Lobby 表第 1800 行）带 queue，但它是 SelectYesno, 不走确定框的判断。
    /// </summary>
    private static readonly string[] QueueKeywords =
    [
        "queue", "人待ち", "混み合って", "Warteliste", "Joueurs en attente"
    ];

    /// <summary>
    ///     「角色上次没登出完 / 在别处登录中, 已被强制下线, 稍等再登」的确定框（Error 表第 13214 / 13003 行）里的词。
    ///     编排按这个决定点掉提示后等一会儿重登; 国服用的是「强制令该角色下线」。
    /// </summary>
    private static readonly string[] ForcedLogoutKeywords =
    [
        "logged in on another client", "Logging out character from a previous play session",
        "他のクライアントからログイン中", "ログアウト処理が完了していません",
        "auf einem anderen Client eingeloggt", "Der Charakter wird gerade ausgeloggt",
        "autre client de jeu", "Processus de déconnexion en cours"
    ];

    /// <summary>喝了幻想药还没改外貌时先弹的是/否框（Lobby 表第 629 行）里的词, 答「是」= 不改外貌直接登录</summary>
    private static readonly string[] FantasiaKeywords =
    [
        "re-edit your character", "キャラクターの再編集", "umzugestalten", "modifier votre personnage"
    ];

    /// <summary>排队中想取消时的确认框（Lobby 表第 1800 行）里的词: 这种框不替人点</summary>
    private static readonly string[] CancelLoginKeywords =
    [
        "leave the queue", "ログイン処理を中断", "Login-Prozess abbrechen", "quitter la queue"
    ];

    public static bool IsQueuePrompt(string text) => ContainsAny(text, QueueKeywords);

    public static bool IsForcedLogoutPrompt(string text) => ContainsAny(text, ForcedLogoutKeywords);

    public static bool IsFantasiaPrompt(string text) => ContainsAny(text, FantasiaKeywords);

    public static bool IsCancelLoginPrompt(string text) => ContainsAny(text, CancelLoginKeywords);

    /// <summary>
    ///     从排队提示里取名次: 英「Players in queue: 17」/ 日「（現在17人待ちです）」/ 德「Platz auf der Warteliste: 17」/ 法「Joueurs en attente : 17」。取不到返回 0
    /// </summary>
    public static int ParseQueueNumber(string text)
    {
        foreach (var pattern in new[] { QueueAfterColon(), QueueJapanese() })
        {
            var match = pattern.Match(text);

            if (match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                return number;
        }

        return 0;
    }

    /// <summary>
    ///     提示文字进行式协议前的清洗: 控制字符（含换行、制表符）换成空格, 不合法的代理项换成 '?', 并裁到 <paramref name="maxChars" /> 个字符内
    /// </summary>
    public static string Sanitize(string? text, int maxChars = 300)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var builder = new StringBuilder(Math.Min(text.Length, maxChars));

        for (var i = 0; i < text.Length && builder.Length < maxChars; i++)
        {
            var c = text[i];

            if (char.IsControl(c))
                builder.Append(' ');
            else if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                builder.Append(c).Append(text[++i]);
            }
            else if (char.IsSurrogate(c))
                builder.Append('?');
            else
                builder.Append(c);
        }

        return builder.ToString().Trim();
    }

    /// <summary>
    ///     名字字段读出来是不是个合法名字: 非空（<paramref name="allowEmpty" /> 为真时可空）、没有控制字符、没有解码失败的替换字符。
    ///     读歪了的内存多半过不了这一关
    /// </summary>
    public static bool IsPlausibleName(string? text, bool allowEmpty)
    {
        if (string.IsNullOrEmpty(text))
            return allowEmpty;

        foreach (var c in text)
        {
            if (char.IsControl(c) || c == '\uFFFD')
                return false;
        }

        return true;
    }

    private static bool ContainsAny(string text, string[] keywords)
    {
        if (string.IsNullOrEmpty(text))
            return false;

        foreach (var keyword in keywords)
        {
            if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    [GeneratedRegex(@"(?:queue|Warteliste|attente)\s*[:：]\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex QueueAfterColon();

    [GeneratedRegex(@"現在\s*(\d+)\s*人待ち")]
    private static partial Regex QueueJapanese();
}
