using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Serilog;
using XIVLauncher.CatHost;

namespace XIVLauncher.Minion;

/// <summary>
///     本机 Cat 工作台写下的一张 Minion 卡: 卡号, 以及这张卡在国服、国际服各自的 Minion 编号（<c>-uid</c>）。
///     <see cref="GlobalUid" /> 不合法时为 null, 这张卡只能挂国服
/// </summary>
public sealed record WorkbenchMinionCard(string Fingerprint, int Number, CatSecret Keycode, string CnUid, string? GlobalUid)
{
    /// <summary>启动页下拉里显示的名字</summary>
    public string DisplayName => $"卡 {Number}";

    /// <summary>
    ///     这张卡在 <paramref name="variant" /> 下的 Minion 编号, 没有时为 null
    /// </summary>
    public string? UidFor(string variant) =>
        variant == MinionCards.VARIANT_GLOBAL ? GlobalUid : CnUid;

    /// <summary>
    ///     只打印指纹与编号, 不打印卡号
    /// </summary>
    public override string ToString() =>
        $"WorkbenchMinionCard {{ Fingerprint = {Fingerprint}, Number = {Number} }}";
}

/// <summary>
///     本机 Cat 工作台写下的卡文件内容: 卡列表（按编号排序）与 Minion 论坛账号密码
/// </summary>
public sealed record WorkbenchMinionCardFile(DateTimeOffset? UpdatedAt, string? ForumId, CatSecret ForumPassword, IReadOnlyList<WorkbenchMinionCard> Cards)
{
    /// <summary>
    ///     只打印张数与更新时间, 不打印卡号与论坛账号密码
    /// </summary>
    public override string ToString() =>
        $"WorkbenchMinionCardFile {{ Cards = {Cards.Count}, UpdatedAt = {UpdatedAt:O} }}";
}

/// <summary>
///     界面版启动页可选的 Minion 卡: 本机有 Cat 工作台的卡文件时用 <see cref="Workbench" />, 否则用 MINIONAPP 的 Accounts.json 分组（含全部行）
/// </summary>
public sealed record MinionCardChoices(WorkbenchMinionCardFile? Workbench, IReadOnlyList<MinionAccountGroup> Groups, string? GroupLoadError)
{
    /// <summary>卡来自 Cat 工作台</summary>
    public bool FromWorkbench => Workbench != null;

    /// <summary>卡来源</summary>
    public MinionCardSourceKind Source => FromWorkbench ? MinionCardSourceKind.Workbench : MinionCardSourceKind.AccountsJson;

    /// <summary>
    ///     设置里显示的卡来源
    /// </summary>
    public string SourceText =>
        Workbench is { } file
            ? $"来自 Cat 工作台（{file.Cards.Count} 张{(file.UpdatedAt is { } updatedAt ? $"，更新于 {updatedAt.ToLocalTime():yyyy-MM-dd HH:mm}" : string.Empty)}）"
            : "来自 MINIONAPP（Accounts.json）";
}

/// <summary>
///     Minion 卡的来源
/// </summary>
public enum MinionCardSourceKind
{
    /// <summary>本机 Cat 工作台写的卡文件</summary>
    Workbench,

    /// <summary>MINIONAPP 的 Accounts.json</summary>
    AccountsJson
}

/// <summary>
///     启动页上选中的卡: <see cref="Source" /> 是选的时候界面显示的来源; 工作台卡用 <see cref="CardFingerprint" />, Accounts.json 用分组与行 UID
/// </summary>
public sealed record MinionSelection(MinionCardSourceKind Source, string? CardFingerprint, string? Group, string? AccountUid);

/// <summary>
///     界面版启动页挂 Minion 时的卡来源:
///     优先读本机 Cat 工作台写的加密卡文件（DPAPI, 当前 Windows 用户）, 读不到时退回 MINIONAPP 的 <c>Settings\Accounts.json</c>。
///     两种来源都换算成与 Cat 上号相同的 <see cref="CatMinionLaunch" />。
/// </summary>
public static class MinionCardSource
{
    /// <summary>卡文件的格式版本</summary>
    public const int FILE_VERSION = 1;

    /// <summary>工作台替换卡文件时读到共享冲突, 再试的次数</summary>
    private const int READ_RETRIES = 3;

    /// <summary>两次读之间的间隔</summary>
    private static readonly TimeSpan ReadRetryDelay = TimeSpan.FromMilliseconds(150);

    /// <summary>DPAPI 的附加熵, 与工作台写入时相同</summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("cat-minion-cards-v1");

    /// <summary>
    ///     工作台卡文件的位置: <c>%LOCALAPPDATA%\cn.mzgames.cat.workbench\minion-cards.bin</c>
    /// </summary>
    public static string WorkbenchCardsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "cn.mzgames.cat.workbench", "minion-cards.bin");

    /// <summary>
    ///     读当前可选的卡: 工作台卡文件可用就只用它, 否则读 Accounts.json 的分组
    /// </summary>
    /// <param name="workbenchCardsPath">工作台卡文件路径, 空则用 <see cref="WorkbenchCardsPath" /></param>
    /// <param name="installPath">Minion 安装目录, 空则用 <see cref="MinionInstall.InstallPath" /></param>
    public static MinionCardChoices Load(string? workbenchCardsPath = null, string? installPath = null)
    {
        if (TryReadWorkbenchCards(workbenchCardsPath) is { } file)
            return new MinionCardChoices(file, [], null);

        var loaded = MinionAccounts.TryLoadGroups(out var groups, out var error, installPath);

        if (!loaded)
            Log.Warning("[Minion] 读取 Accounts.json 分组失败 ({Path}): {Error}", MinionAccounts.GetAccountsJsonPath(installPath), error);

        return new MinionCardChoices(null, groups, loaded ? null : error);
    }

    /// <summary>
    ///     读并解密工作台卡文件。文件不存在、解不开、格式不对或版本不认识时返回 null, 只记原因不记内容。
    ///     读到共享冲突（工作台正在替换文件）时隔一会儿再试
    /// </summary>
    public static WorkbenchMinionCardFile? TryReadWorkbenchCards(string? path = null)
    {
        path ??= WorkbenchCardsPath;

        for (var attempt = 0;; attempt++)
        {
            try
            {
                return ReadWorkbenchCards(path);
            }
            catch (IOException ex) when (ex is not FileNotFoundException and not DirectoryNotFoundException && attempt < READ_RETRIES)
            {
                Thread.Sleep(ReadRetryDelay);
            }
            catch (Exception ex) when (ex is CryptographicException or JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
            {
                // 异常消息里可能带出 JSON 片段, 只记类型
                Log.Warning("[Minion] 读取工作台卡文件失败（{Error}）, 改用 Accounts.json: {Path}", ex.GetType().Name, path);
                return null;
            }
        }
    }

    private static WorkbenchMinionCardFile? ReadWorkbenchCards(string path)
    {
        if (!File.Exists(path))
            return null;

        var plaintext = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);

        try
        {
            var file = ParseWorkbenchCards(plaintext);

            if (file == null)
                Log.Warning("[Minion] 工作台卡文件的版本不是 {Version}, 改用 Accounts.json: {Path}", FILE_VERSION, path);

            return file;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>
    ///     解析卡文件的明文 JSON。版本不是 <see cref="FILE_VERSION" /> 时返回 null; 格式不对时抛 <see cref="JsonException" /> 或 <see cref="InvalidDataException" />。
    ///     没有卡号、编号或国服编号不合法的卡跳过; 国际服编号不合法时置空; 指纹按卡号重新计算。
    /// </summary>
    internal static WorkbenchMinionCardFile? ParseWorkbenchCards(byte[] json)
    {
        using var document = JsonDocument.Parse(json);
        var       root     = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("卡文件根节点不是对象");

        if (!root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != FILE_VERSION)
            return null;

        DateTimeOffset? updatedAt = null;

        if (ReadString(root, "updatedAt") is { } updatedAtText &&
            DateTimeOffset.TryParse(updatedAtText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            updatedAt = parsed;

        var cards = new List<WorkbenchMinionCard>();

        if (root.TryGetProperty("cards", out var cardsElement) && cardsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in cardsElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                    continue;

                var keycode   = ReadString(element, "keycode")?.Trim();
                var cnUid     = ReadString(element, "cnUid")?.Trim();
                var globalUid = ReadString(element, "globalUid")?.Trim();

                if (string.IsNullOrEmpty(keycode)                                                 ||
                    !MinionCards.IsValidUid(cnUid)                                                 ||
                    !element.TryGetProperty("number", out var numberElement)                       ||
                    numberElement.ValueKind != JsonValueKind.Number || !numberElement.TryGetInt32(out var cardNumber))
                    continue;

                cards.Add
                (
                    new WorkbenchMinionCard
                    (
                        MinionCards.Fingerprint(keycode),
                        cardNumber,
                        new CatSecret(keycode),
                        cnUid!,
                        MinionCards.IsValidUid(globalUid) ? globalUid : null
                    )
                );
            }
        }

        return new WorkbenchMinionCardFile
        (
            updatedAt,
            ReadString(root, "forumId")?.Trim() is { Length: > 0 } forumId ? forumId : null,
            new CatSecret(ReadString(root, "forumPassword") ?? string.Empty),
            cards.OrderBy(card => card.Number).ToList()
        );
    }

    /// <summary>
    ///     用工作台的卡构造这次挂载的参数（论坛账号密码取自卡文件）。这张卡没有该 variant 的编号时 Uid 为空
    /// </summary>
    /// <param name="file">卡文件</param>
    /// <param name="card">选中的卡</param>
    /// <param name="variant">这次启动的游戏是国服还是国际服</param>
    public static CatMinionLaunch ToLaunch(WorkbenchMinionCardFile file, WorkbenchMinionCard card, string variant) =>
        new
        (
            card.Fingerprint,
            variant,
            card.Keycode,
            card.UidFor(variant) ?? string.Empty,
            file.ForumId ?? string.Empty,
            file.ForumPassword
        );

    /// <summary>
    ///     用 Accounts.json 的一行构造这次挂载的参数（编号取这一行的 UID, 论坛账号密码取自本机设置）
    /// </summary>
    /// <param name="row">选中的行</param>
    /// <param name="forumId">本机设置里的 Minion 论坛账号</param>
    /// <param name="forumPassword">本机设置里的 Minion 论坛密码</param>
    /// <param name="variant">这次启动的游戏是国服还是国际服</param>
    public static CatMinionLaunch ToLaunch(MinionAccount row, string? forumId, string? forumPassword, string variant)
    {
        var keycode = row.Keycode?.Trim() ?? string.Empty;

        return new CatMinionLaunch
        (
            MinionCards.Fingerprint(keycode),
            variant,
            new CatSecret(keycode),
            row.Uid?.Trim() ?? string.Empty,
            forumId?.Trim() ?? string.Empty,
            new CatSecret(forumPassword ?? string.Empty)
        );
    }

    /// <summary>
    ///     Accounts.json 的分组只留 <paramref name="variant" /> 的行, 没有这种行的分组去掉
    /// </summary>
    /// <param name="groups">全部分组</param>
    /// <param name="variant">要留下的 variant</param>
    /// <param name="cnGameRoots">国服游戏目录, 用来判断行的 variant</param>
    public static IReadOnlyList<MinionAccountGroup> GroupsFor(IEnumerable<MinionAccountGroup> groups, string variant, IReadOnlyList<string?> cnGameRoots) =>
        groups.Select(group => group with { Accounts = group.Accounts.Where(row => MinionAccounts.VariantOf(row, cnGameRoots) == variant).ToList() })
              .Where(group => group.Accounts.Count > 0)
              .ToList();

    /// <summary>
    ///     按启动页的选择取这次要挂的卡。
    ///     选的时候界面显示的来源与现在读到的来源不一致（工作台卡文件一时读不到、或本机新出现了卡文件）时不挂, 免得换成另一种来源里残留的选择挂错卡;
    ///     选中的卡找不到时也不退回别的卡（换一张卡挂上去可能把正在别处用的卡顶掉）。失败时返回可以直接显示的原因。
    /// </summary>
    /// <param name="choices">现在读到的卡</param>
    /// <param name="selection">启动页上选中的卡</param>
    /// <param name="forumId">本机设置里的 Minion 论坛账号（只用于 Accounts.json）</param>
    /// <param name="forumPassword">本机设置里的 Minion 论坛密码（只用于 Accounts.json）</param>
    /// <param name="variant">这次启动的游戏是国服还是国际服</param>
    /// <param name="cnGameRoots">国服游戏目录, 用来判断 Accounts.json 行的 variant</param>
    public static (CatMinionLaunch? Launch, string? Error) Resolve
    (
        MinionCardChoices       choices,
        MinionSelection         selection,
        string?                 forumId,
        string?                 forumPassword,
        string                  variant,
        IReadOnlyList<string?>  cnGameRoots
    )
    {
        if (selection.Source == MinionCardSourceKind.Workbench && choices.Workbench == null)
            return (null, "读取 Cat 工作台的卡失败, 请在启动页重选");

        if (selection.Source == MinionCardSourceKind.AccountsJson && choices.Workbench != null)
            return (null, "本机已有 Cat 工作台的卡, 请在启动页重选");

        if (choices.Workbench is { } file)
        {
            var card = file.Cards.FirstOrDefault(x => string.Equals(x.Fingerprint, selection.CardFingerprint, StringComparison.Ordinal));

            if (card == null)
                return (null, "启动页选中的 Minion 卡在 Cat 工作台的卡里找不到了, 为免挂错卡, 本次没有挂载, 请在启动页重新选择卡");

            if (!MinionCards.IsValidUid(card.UidFor(variant)))
                return (null, $"{card.DisplayName} 没有{(variant == MinionCards.VARIANT_GLOBAL ? "国际服" : "国服")}的 Minion 编号, 请在 Cat 工作台检查这张卡");

            if (string.IsNullOrWhiteSpace(file.ForumId) || string.IsNullOrEmpty(file.ForumPassword.Reveal()))
                return (null, "Cat 工作台还没有填 Minion 论坛账号或密码（在工作台「设置 → Minion」里填写）");

            return (ToLaunch(file, card, variant), null);
        }

        if (choices.GroupLoadError != null)
            return (null, $"读取 Minion 账号文件失败: {choices.GroupLoadError}");

        var group = selection.Group;
        var row   = MinionAccounts.FindAccount(choices.Groups.SelectMany(x => x.Accounts), group, selection.AccountUid);

        if (row == null)
        {
            var hasAccountsInGroup = choices.Groups.Any(x => string.Equals(x.Id, group?.Trim(), StringComparison.OrdinalIgnoreCase));

            return
            (
                null,
                hasAccountsInGroup
                    ? $"启动页选中的 Minion 账号在 Minion 分组 {group} 里找不到了（Accounts.json 可能改过）, 为免挂错卡顶掉别处的号, 本次没有挂载, 请在启动页重新选择 Keycode"
                    : $"Minion 分组 {group ?? "(未选择)"} 下没有账号, 请在启动页重新选择分组"
            );
        }

        if (MinionAccounts.VariantOf(row, cnGameRoots) != variant)
            return (null, $"选中的这一行是{(variant == MinionCards.VARIANT_GLOBAL ? "国服" : "国际服")}的配置, 和这次启动的游戏不符, 请在启动页重选");

        if (string.IsNullOrWhiteSpace(row.Keycode) || !MinionCards.IsValidUid(row.Uid?.Trim()))
            return (null, $"Minion 分组 {group} 里选中的这一行没有 Keycode 或 UID 不是 32 位十六进制, 请在 MINIONAPP 里检查");

        if (string.IsNullOrWhiteSpace(forumId) || string.IsNullOrEmpty(forumPassword))
            return (null, "未填写 Minion 论坛账号或密码（在「设置 → Minion」里配置一次）");

        return (ToLaunch(row, forumId, forumPassword, variant), null);
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
