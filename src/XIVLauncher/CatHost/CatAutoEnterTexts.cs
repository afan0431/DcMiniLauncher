using System.Text.RegularExpressions;

namespace XIVLauncher.CatHost;

/// <summary>
///     自动进入角色里按提示框文字做判断的关键词: 国服用中文原文, 国际服（游戏内模块读回来的提示框文字）用英 / 日 / 德 / 法四种客户端语言的原文。
///     国际服关键词出自游戏数据表 Error / Lobby 各语言原文; 比较时不分大小写。
/// </summary>
internal static partial class CatAutoEnterTexts
{
    private static readonly string[] QueueKeywords =
    [
        CatAutoEnter.QUEUE_PROMPT,
        "Players in queue", "人待ち", "混み合って", "Warteliste", "Joueurs en attente"
    ];

    private static readonly string[] ForcedLogoutKeywords =
    [
        CatAutoEnter.FORCED_LOGOUT_PROMPT,
        "logged in on another client", "Logging out character from a previous play session",
        "他のクライアントからログイン中", "ログアウト処理が完了していません",
        "auf einem anderen Client eingeloggt", "Der Charakter wird gerade ausgeloggt",
        "autre client de jeu", "Processus de déconnexion en cours"
    ];

    private static readonly string[] FantasiaKeywords =
    [
        CatAutoEnter.FANTASIA_PROMPT,
        "re-edit your character", "キャラクターの再編集", "umzugestalten", "modifier votre personnage"
    ];

    private static readonly string[] CancelLoginKeywords =
    [
        CatAutoEnter.CANCEL_LOGIN_PROMPT,
        "leave the queue", "ログイン処理を中断", "Login-Prozess abbrechen", "quitter la queue"
    ];

    /// <summary>排队提示框（SelectOk）的文字</summary>
    public static bool IsQueuePrompt(string text) => ContainsAny(text, QueueKeywords);

    /// <summary>「角色还没下线, 稍等再登」的确定框</summary>
    public static bool IsForcedLogoutPrompt(string text) => ContainsAny(text, ForcedLogoutKeywords);

    /// <summary>喝了幻想药还没改外貌的是/否框</summary>
    public static bool IsFantasiaPrompt(string text) => ContainsAny(text, FantasiaKeywords);

    /// <summary>排队中想取消登录的确认框: 这种框不替人点</summary>
    public static bool IsCancelLoginPrompt(string text) => ContainsAny(text, CancelLoginKeywords);

    /// <summary>从排队提示里取名次; 国服「排队人数：17」, 国际服「Players in queue: 17」「現在17人待ち」。取不到返回 0</summary>
    public static int ParseQueueNumber(string text)
    {
        foreach (var pattern in new[] { ChineseQueueNumber(), QueueAfterColon(), JapaneseQueueNumber() })
        {
            var match = pattern.Match(text);

            if (match.Success && int.TryParse(match.Groups[1].Value, out var count))
                return count;
        }

        return 0;
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

    [GeneratedRegex(@"排队人数\s*[:：]\s*(\d+)")]
    private static partial Regex ChineseQueueNumber();

    [GeneratedRegex(@"(?:queue|Warteliste|attente)\s*[:：]\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex QueueAfterColon();

    [GeneratedRegex(@"現在\s*(\d+)\s*人待ち")]
    private static partial Regex JapaneseQueueNumber();
}
