using System.IO;
using System.Text.Json;

namespace XIVLauncher.Minion;

/// <summary>
///     MINIONAPP 的 <c>Settings\Accounts.json</c> 里的一个账号条目。只在本机没有 Cat 工作台的卡文件时, 给界面版启动页挂 Minion 用
/// </summary>
public sealed record MinionAccount
{
    public string? Uid { get; init; }

    public string? Keycode { get; init; }

    /// <summary>
    ///     这一行的「游戏执行程序」路径, 用来区分同一张卡的国服行与国际服行（见 <see cref="MinionAccounts.VariantOf(MinionAccount)" />）
    /// </summary>
    public string? PathToExe { get; init; }

    public string? Group { get; init; }

    public string? CharName { get; init; }

    public string? Notes { get; init; }

    /// <summary>
    ///     启动页下拉里显示的文字。同一张卡的国服行与国际服行共用同一个 Keycode,
    ///     所以国际服行在最前面标 [国际服]（下拉窄, 尾部会被省略号吃掉）。
    /// </summary>
    public string DisplayText
    {
        get
        {
            var keycode = string.IsNullOrWhiteSpace(Keycode) ? "(无 Keycode)" : Keycode;
            return MinionAccounts.VariantOf(this) == MinionCards.VARIANT_GLOBAL ? $"[国际服] {keycode}" : keycode;
        }
    }

    /// <summary>
    ///     只打印 UID 与分组, 不打印 Keycode
    /// </summary>
    public override string ToString() =>
        $"MinionAccount {{ Uid = {Uid}, Group = {Group} }}";
}

/// <summary>
///     Accounts.json 里的一个分组, 启动页按分组选。一个分组下可以有多个账号。
/// </summary>
public sealed record MinionAccountGroup
{
    public required string Id { get; init; }

    public required IReadOnlyList<MinionAccount> Accounts { get; init; }

    /// <summary>
    ///     下拉里显示的文字 —— 上方已经有「分组」标签, 这里只给分组号和账号数
    /// </summary>
    public string DisplayName => $"{Id}（{Accounts.Count} 个账号）";
}

/// <summary>
///     读 MINIONAPP 的 <c>Settings\Accounts.json</c>（位于 Minion 安装目录下, 安装目录见 <see cref="MinionInstall.InstallPath" />）
/// </summary>
public static class MinionAccounts
{
    public static string GetAccountsJsonPath(string? installPath = null) =>
        Path.Combine(installPath ?? MinionInstall.InstallPath, "Settings", "Accounts.json");

    /// <summary>
    ///     解析 Accounts.json。文件缺失或格式错误会抛异常, 界面侧用 <see cref="TryLoadGroups" />。
    /// </summary>
    public static IReadOnlyList<MinionAccount> LoadAccounts(string? installPath = null)
    {
        var path = GetAccountsJsonPath(installPath);

        if (!File.Exists(path))
            throw new FileNotFoundException($"未找到 Minion 账号文件: {path}", path);

        // Minion 写的文件带 BOM, ReadAllText 会剥掉；直接用字节喂 JsonDocument 会失败
        return Parse(File.ReadAllText(path), path);
    }

    /// <summary>
    ///     解析 Accounts.json 的内容, <paramref name="path" /> 只用于报错
    /// </summary>
    internal static IReadOnlyList<MinionAccount> Parse(string json, string path)
    {
        using var document = JsonDocument.Parse
        (
            json,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling     = JsonCommentHandling.Skip
            }
        );

        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"Minion 账号文件格式异常（根节点不是数组）: {path}");

        var accounts = new List<MinionAccount>();

        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
                continue;

            accounts.Add
            (
                new MinionAccount
                {
                    Uid       = ReadString(element, "UID"),
                    Keycode   = ReadString(element, "Keycode"),
                    PathToExe = ReadString(element, "PathToExe"),
                    Group     = ReadString(element, "Group"),
                    CharName  = ReadString(element, "CharName"),
                    Notes     = ReadString(element, "Notes")
                }
            );
        }

        return accounts;
    }

    /// <summary>
    ///     把账号按分组归并, 分组号是数字时按数字排序
    /// </summary>
    public static IReadOnlyList<MinionAccountGroup> GroupAccounts(IEnumerable<MinionAccount> accounts) =>
        accounts
            .Where(account => !string.IsNullOrWhiteSpace(account.Group))
            .GroupBy(account => account.Group!.Trim())
            .Select
            (group => new MinionAccountGroup
                {
                    Id       = group.Key,
                    Accounts = group.ToList()
                }
            )
            .OrderBy(group => int.TryParse(group.Id, out var number) ? number : int.MaxValue)
            .ThenBy(group => group.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    ///     Accounts.json 里的分组
    /// </summary>
    public static IReadOnlyList<MinionAccountGroup> LoadGroups(string? installPath = null) =>
        GroupAccounts(LoadAccounts(installPath));

    /// <summary>
    ///     不抛异常版的 <see cref="LoadGroups" />, <paramref name="error" /> 是可以直接显示的提示
    /// </summary>
    public static bool TryLoadGroups(out IReadOnlyList<MinionAccountGroup> groups, out string? error, string? installPath = null)
    {
        try
        {
            groups = LoadGroups(installPath);
            error  = null;
            return true;
        }
        catch (Exception ex)
        {
            groups = [];
            error  = ex.Message;
            return false;
        }
    }

    /// <summary>
    ///     在给定的账号列表里按分组与 UID 精确查找, 找不到返回 null。
    ///     不退回分组里的其它账号: 换一张卡挂上去可能把正在别处用的卡顶掉。
    /// </summary>
    public static MinionAccount? FindAccount(IEnumerable<MinionAccount> accounts, string? group, string? uid)
    {
        if (string.IsNullOrWhiteSpace(uid))
            return null;

        return accounts.FirstOrDefault
        (account => string.Equals(account.Group?.Trim(), group?.Trim(), StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(account.Uid?.Trim(), uid.Trim(), StringComparison.OrdinalIgnoreCase)
        );
    }

    /// <summary>
    ///     本启动器设置里的国服游戏目录（盛趣与 WeGame）, 未配置的项为 null
    /// </summary>
    public static IReadOnlyList<string?> ConfiguredCnGameRoots()
    {
        var settings = App.Settings;

        if (settings == null)
            return [];

        return [settings.GamePath?.FullName, settings.WeGamePath?.FullName];
    }

    /// <summary>
    ///     按本启动器当前设置的国服游戏目录判断行对应的 variant
    /// </summary>
    public static string VariantOf(MinionAccount account) =>
        VariantOf(account, ConfiguredCnGameRoots());

    /// <summary>
    ///     行对应的 variant: 执行程序路径为空或位于任一国服游戏目录之下 → <see cref="MinionCards.VARIANT_CN" />, 否则 → <see cref="MinionCards.VARIANT_GLOBAL" />
    /// </summary>
    /// <param name="account">Accounts.json 的一行</param>
    /// <param name="cnGameRoots">国服游戏目录, 空项忽略</param>
    public static string VariantOf(MinionAccount account, IEnumerable<string?> cnGameRoots)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(cnGameRoots);

        if (string.IsNullOrWhiteSpace(account.PathToExe))
            return MinionCards.VARIANT_CN;

        return cnGameRoots.Any(root => IsUnderDirectory(account.PathToExe, root)) ? MinionCards.VARIANT_CN : MinionCards.VARIANT_GLOBAL;
    }

    /// <summary>
    ///     规范化完整路径后按目录前缀比较（大小写不敏感）, 判断 <paramref name="path" /> 是否位于 <paramref name="directory" /> 之下
    /// </summary>
    private static bool IsUnderDirectory(string path, string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return false;

        string fullPath;
        string fullDirectory;

        try
        {
            fullPath      = Path.GetFullPath(path.Trim());
            fullDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory.Trim()));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        // 盘符根目录（如 G:\）去不掉末尾分隔符, 不再重复追加
        var prefix = Path.EndsInDirectorySeparator(fullDirectory) ? fullDirectory : fullDirectory + Path.DirectorySeparatorChar;

        return fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            // Group 目前是字符串, 但容忍 Minion 写成数字
            JsonValueKind.Number => value.ToString(),
            _                    => null
        };
    }
}
