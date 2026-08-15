using System.IO;
using System.Text.Json;

namespace XIVLauncher.Minion;

/// <summary>
///     Minion 的 <c>Settings\Accounts.json</c> 里的一个账号条目
/// </summary>
public sealed record MinionAccount
{
    public string? Uid { get; init; }

    public string? Keycode { get; init; }

    public string? PathToExe { get; init; }

    public string? Group { get; init; }

    public string? CharName { get; init; }

    public string? Notes { get; init; }

    /// <summary>
    ///     <c>-productid</c>, 国服是 8
    /// </summary>
    public int? ProductId { get; init; }

    /// <summary>
    ///     <c>-datacenter</c>, Accounts.json 里都是 0
    /// </summary>
    public int? Datacenter { get; init; }

    /// <summary>
    ///     <c>-usebeta</c> —— 用户的分组里确实有勾 beta 的条目。
    ///     ⚠ 光传这个没用, 真正决定跑不跑 beta 的是 <c>-datpath</c> 指向哪份 dat, 见 <c>MinionAttacher</c>。
    /// </summary>
    public bool UseBetaFiles { get; init; }

    /// <summary>
    ///     <c>-streamermode</c>
    /// </summary>
    public bool StreamerMode { get; init; }

    /// <summary>
    ///     <c>-setwindowtitle</c>
    /// </summary>
    public bool SetWindowTitle { get; init; }

    /// <summary>
    ///     启动页下拉里显示的文字。同一分组里 beta 与非 beta 两条常常**共用同一个 Keycode**,
    ///     光显示 Keycode 根本分不出来（用户的 3/4/5/6/7 组都是这样）, 所以把 beta 标在最前面
    ///     —— 下拉窄, 尾部会被省略号吃掉。
    /// </summary>
    public string DisplayText
    {
        get
        {
            var keycode = string.IsNullOrWhiteSpace(Keycode) ? "(无 Keycode)" : Keycode;
            return UseBetaFiles ? $"[beta] {keycode}" : keycode;
        }
    }

    /// <summary>
    ///     给人看的标识 —— Accounts.json 里的条目经常没有名字
    /// </summary>
    public string Label
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(CharName))
                return CharName;

            if (!string.IsNullOrWhiteSpace(Notes))
                return Notes;

            return string.IsNullOrEmpty(Uid) ? "?" : Uid[..Math.Min(8, Uid.Length)];
        }
    }
}

/// <summary>
///     Minion 分组，启动页按分组选。一个分组下可以有多个账号。
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
///     读 Minion 安装目录 —— 启动页要分组列表, 挂 Minion 时要账号字段
/// </summary>
public static class MinionAccounts
{
    /// <summary>
    ///     MinionLauncher 靠相对路径找 MinionLauncher_64.dat, 必须以安装目录为工作目录拉起
    ///     （见 research/probe-P1-attach-standalone.md）
    /// </summary>
    public const string DEFAULT_INSTALL_PATH = @"C:\MINI";

    public const string LAUNCHER_EXE_NAME = "MinionLauncher_64.exe";

    /// <summary>
    ///     配置的安装目录, 没配就用 <see cref="DEFAULT_INSTALL_PATH" />
    /// </summary>
    public static string InstallPath =>
        string.IsNullOrWhiteSpace(App.Settings?.MinionInstallPath)
            ? DEFAULT_INSTALL_PATH
            : App.Settings.MinionInstallPath.Trim();

    public static string GetLauncherExePath(string? installPath = null) =>
        Path.Combine(installPath ?? InstallPath, LAUNCHER_EXE_NAME);

    public static string GetAccountsJsonPath(string? installPath = null) =>
        Path.Combine(installPath ?? InstallPath, "Settings", "Accounts.json");

    public static bool IsLauncherPresent(string? installPath = null) =>
        File.Exists(GetLauncherExePath(installPath));

    /// <summary>
    ///     解析 Accounts.json。文件缺失或格式错误会抛异常, 界面侧用 <see cref="TryLoadGroups" />。
    /// </summary>
    public static IReadOnlyList<MinionAccount> LoadAccounts(string? installPath = null)
    {
        var path = GetAccountsJsonPath(installPath);

        if (!File.Exists(path))
            throw new FileNotFoundException($"未找到 Minion 账号文件: {path}", path);

        // Minion 写的文件带 BOM, ReadAllText 会剥掉；直接用字节喂 JsonDocument 会失败
        using var document = JsonDocument.Parse
        (
            File.ReadAllText(path),
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
                    Uid          = ReadString(element, "UID"),
                    Keycode      = ReadString(element, "Keycode"),
                    PathToExe    = ReadString(element, "PathToExe"),
                    Group        = ReadString(element, "Group"),
                    CharName     = ReadString(element, "CharName"),
                    Notes        = ReadString(element, "Notes"),
                    ProductId      = ReadInt(element, "ProductID"),
                    Datacenter     = ReadInt(element, "Datacenter"),
                    UseBetaFiles   = ReadBool(element, "UseBetaFFXIVFiles"),
                    StreamerMode   = ReadBool(element, "StreamerMode"),
                    SetWindowTitle = ReadBool(element, "SetWindowTitle")
                }
            );
        }

        return accounts;
    }

    /// <summary>
    ///     Accounts.json 里的分组, 分组号是数字时按数字排序
    /// </summary>
    public static IReadOnlyList<MinionAccountGroup> LoadGroups(string? installPath = null) =>
        LoadAccounts(installPath)
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
    ///     按启动页选中的分组与账号 UID 取账号, 并给出它在**本分组内的序号**。
    ///     序号是 MINIONAPP 界面上那一组里的行号 —— 它的表格按分组分栏、组内按本文件顺序排,
    ///     驱动它注入时要靠这个定位（见 <see cref="MinionAppAutomation" />）。
    ///     UID 对不上（Accounts.json 改过）就退回该分组的第一个账号, 免得配置一变就挂不上。
    /// </summary>
    public static (MinionAccount? Account, int RowIndex) FindAccountInGroup(string? group, string? uid, string? installPath = null)
    {
        var accountsInGroup = LoadAccounts(installPath)
                              .Where(account => string.Equals(account.Group?.Trim(), group?.Trim(), StringComparison.OrdinalIgnoreCase))
                              .ToList();

        var index = accountsInGroup.FindIndex(account => string.Equals(account.Uid, uid, StringComparison.OrdinalIgnoreCase));

        if (index < 0)
            index = accountsInGroup.Count > 0 ? 0 : -1;

        return index < 0 ? (null, -1) : (accountsInGroup[index], index);
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

    private static int? ReadInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number)              => number,
            JsonValueKind.String when int.TryParse(value.GetString(), out var parsed) => parsed,
            _                                                                        => null
        };
    }

    private static bool ReadBool(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return false;

        return value.ValueKind switch
        {
            JsonValueKind.True   => true,
            JsonValueKind.Number => value.TryGetInt32(out var number) && number != 0,
            JsonValueKind.String => bool.TryParse(value.GetString(), out var parsed) && parsed,
            _                    => false
        };
    }
}
