using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;
using XIVLauncher.Common.Util;

namespace XIVLauncher.Minion;

/// <summary>
///     组装或清理客户端目录失败的原因
/// </summary>
public enum MinionClientDirError
{
    /// <summary>主目录不存在或没有 <c>Bots\FFXIVMinion64\LuaMods</c></summary>
    MainInstallMissing,

    /// <summary>Profiler 的 <c>Settings.lua</c> 读不出</summary>
    SettingsUnreadable,

    /// <summary>Profiler 里没有指定的配置</summary>
    ProfileNotFound,

    /// <summary>客户端目录已存在且不为空</summary>
    AlreadyExists,

    /// <summary>路径不在 <c>MINI-clients</c> 目录下, 拒绝操作</summary>
    NotAClientDir
}

/// <summary>
///     组装或清理客户端目录失败
/// </summary>
public sealed class MinionClientDirException : Exception
{
    /// <summary>
    ///     新建异常
    /// </summary>
    public MinionClientDirException(MinionClientDirError error, string message, Exception? inner = null)
        : base(message, inner) =>
        Error = error;

    /// <summary>
    ///     失败原因
    /// </summary>
    public MinionClientDirError Error { get; }
}

/// <summary>
///     组装时记下、写回与清理时要用的信息, 以 JSON 存在客户端目录里, 交接或残留清理时别的进程也能读到
/// </summary>
public sealed record MinionClientDirSnapshot
{
    /// <summary>格式版本</summary>
    public int Version { get; init; } = 1;

    /// <summary>主目录（完整路径）</summary>
    public required string MainInstall { get; init; }

    /// <summary>组装开始的时间（UTC）, 写回时只认在这之后改过的文件</summary>
    public required DateTime AssembledAtUtc { get; init; }

    /// <summary>指定的 Profiler 配置名; 没指定为 null</summary>
    public string? Profile { get; init; }

    /// <summary>强制打开的插件（含依赖, 用的是磁盘上的目录名）</summary>
    public IReadOnlyList<string> ForcedMods { get; init; } = [];

    /// <summary>
    ///     Profiler <c>Settings.lua</c> 相对 <c>LuaMods</c> 的路径; 主目录没有这个文件时为 null
    /// </summary>
    public string? ProfilerSettingsPath { get; init; }

    /// <summary>
    ///     组装时主目录的 Profiler 配置定义; 主目录没有 <c>Settings.lua</c> 时为 null
    /// </summary>
    public IReadOnlyList<ProfilerProfile>? Profiles { get; init; }

    /// <summary>
    ///     从主目录硬链接（跨盘时为复制）过来、可能需要写回的文件, 路径相对 <c>LuaMods</c>
    ///     （<c>LuaMods</c> 根下的散文件与各插件根目录下的普通文件, 不含开关文件和 Profiler 的 <c>Settings.lua</c>）
    /// </summary>
    public IReadOnlyList<string> WriteBackFiles { get; init; } = [];
}

/// <summary>
///     写回并删除客户端目录的结果
/// </summary>
/// <param name="ProfilesWrittenBack">Profiler 配置定义写回了主目录</param>
/// <param name="FilesWrittenBack">写回主目录的插件文件数</param>
/// <param name="Deleted">客户端目录已删除</param>
/// <param name="Warnings">没做成但不影响删除的事</param>
public sealed record MinionClientDirRetireResult(bool ProfilesWrittenBack, int FilesWrittenBack, bool Deleted, IReadOnlyList<string> Warnings);

/// <summary>
///     每个游戏客户端一份的 Minion 安装目录 <c>&lt;主目录所在盘&gt;:\MINI-clients\&lt;id&gt;</c>:
///     大部分内容用硬链接与目录联接指回主目录, 只有插件开关文件和 Profiler 的 <c>Settings.lua</c> 是自己的,
///     于是游戏里用 Profiler 切配置只改这个客户端。挂载时 <c>-botpath</c> 指向这里的 <c>Bots\FFXIVMinion64</c>。
/// </summary>
public sealed class MinionClientDir
{
    /// <summary>客户端目录的上级目录名; 不放进 <c>MINI</c> 里, 免得被当成安装目录找到</summary>
    public const string ROOT_DIR_NAME = "MINI-clients";

    /// <summary>客户端目录里记组装信息的文件名</summary>
    public const string SNAPSHOT_FILE_NAME = "cat-client-dir.json";

    private const string BOTS_DIR_NAME         = "Bots";
    private const string BOT_DIR_NAME          = "FFXIVMinion64";
    private const string LUA_MODS_DIR_NAME     = "LuaMods";
    private const string LUA_MODS_ARCHIVE_NAME = "LuaMods.7z";
    private const string PROFILER_DIR_NAME     = "Profiler";
    private const string PROFILER_SETTINGS     = "Settings.lua";
    private const string LAUNCHER_INFO_NAME    = "MinionLauncherInfo.txt";
    private const string MODULE_ON             = "module.def";
    private const string MODULE_OFF            = "module.dis";
    private const string PAF_ON                = ".paf";
    private const string PAF_OFF               = ".dis";

    private const string SETTINGS_MUTEX_PREFIX = "DcMiniLauncher-ProfilerSettings";

    private static readonly TimeSpan SettingsLockTimeout = TimeSpan.FromSeconds(30);

    /// <summary>枚举时不跳过隐藏、系统文件</summary>
    private static readonly EnumerationOptions AllEntries = new()
    {
        AttributesToSkip      = 0,
        IgnoreInaccessible    = false,
        RecurseSubdirectories = false
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy   = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented          = true
    };

    private MinionClientDir(string directoryPath, MinionClientDirSnapshot snapshot, IReadOnlyList<string> missingMods)
    {
        DirectoryPath = directoryPath;
        Snapshot      = snapshot;
        MissingMods   = missingMods;
    }

    /// <summary>客户端目录（完整路径）, 挂载时用作 MinionLauncher 的工作目录</summary>
    public string DirectoryPath { get; }

    /// <summary>组装信息</summary>
    public MinionClientDirSnapshot Snapshot { get; }

    /// <summary>要求强制打开、但主目录里没有的插件名（组装时才有, <see cref="Open" /> 读回的为空）</summary>
    public IReadOnlyList<string> MissingMods { get; }

    /// <summary><c>-botpath</c> 用的目录</summary>
    public string BotPath => Path.Combine(DirectoryPath, BOTS_DIR_NAME, BOT_DIR_NAME);

    /// <summary>客户端目录里的 MinionLauncher_64.exe（主目录那份的硬链接）</summary>
    public string LauncherExe => Path.Combine(DirectoryPath, MinionInstall.LAUNCHER_EXE_NAME);

    /// <summary>
    ///     客户端目录里这个插件是否开着: 插件目录下有 <c>module.def</c> 或 <c>.paf</c> 文件（不区分大小写）
    /// </summary>
    public bool IsModEnabled(string mod)
    {
        var directory = new DirectoryInfo(Path.Combine(BotPath, LUA_MODS_DIR_NAME));

        if (!directory.Exists)
            return false;

        var plugin = directory.EnumerateDirectories("*", AllEntries).FirstOrDefault(x => Is(x, mod));

        return plugin != null
               && plugin.EnumerateFiles("*", AllEntries)
                        .Any(x => Is(x, MODULE_ON) || x.Name.EndsWith(PAF_ON, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     某个主目录对应的客户端目录根: 同一个盘下的 <c>MINI-clients</c>（硬链接不能跨盘）
    /// </summary>
    public static string RootFor(string mainInstall) =>
        Path.Combine(Path.GetPathRoot(Path.GetFullPath(mainInstall))!, ROOT_DIR_NAME);

    /// <summary>
    ///     某个主目录下编号为 <paramref name="id" />（游戏 PID 或操作编号）的客户端目录
    /// </summary>
    public static string PathFor(string mainInstall, string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || id is "." or "..")
            throw new ArgumentException($"客户端目录编号不合法: {id}", nameof(id));

        return Path.Combine(RootFor(mainInstall), id);
    }

    /// <summary>
    ///     路径的上一级目录叫 <c>MINI-clients</c>; 不是的一律不写回、不删除
    /// </summary>
    public static bool IsClientDirPath(string path)
    {
        var full   = Normalize(path);
        var parent = Path.GetDirectoryName(full);
        return parent != null
               && Path.GetFileName(full).Length > 0
               && string.Equals(Path.GetFileName(parent), ROOT_DIR_NAME, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     由主目录组装一个客户端目录。
    ///     <paramref name="profile" /> 为 null 时插件开关照主目录当前的摆; 否则只开配置里列出的插件。
    ///     <paramref name="ensureMods" /> 里的插件及其 <c>Dependencies</c> 无论如何都打开（不区分大小写）。
    ///     失败时删掉已建的部分并抛 <see cref="MinionClientDirException" /> 或 IO 异常; 主目录的文件内容不会被改动
    /// </summary>
    /// <param name="mainInstall">主目录, 如 <c>E:\MINI</c></param>
    /// <param name="clientDir">客户端目录, 必须在 <c>MINI-clients</c> 下, 见 <see cref="PathFor" /></param>
    /// <param name="profile">Profiler 配置名; null 表示不指定</param>
    /// <param name="ensureMods">必须打开的插件名, 如 <c>Afan</c></param>
    public static MinionClientDir Assemble(string mainInstall, string clientDir, string? profile, IReadOnlyCollection<string>? ensureMods)
    {
        var main   = Normalize(mainInstall);
        var client = Normalize(clientDir);
        EnsureClientDirPath(client);

        var mainLuaMods = Path.Combine(main, BOTS_DIR_NAME, BOT_DIR_NAME, LUA_MODS_DIR_NAME);

        if (!Directory.Exists(mainLuaMods))
            throw new MinionClientDirException(MinionClientDirError.MainInstallMissing, $"Minion 主目录不完整: 找不到 {mainLuaMods}");

        if (Directory.Exists(client) && Directory.EnumerateFileSystemEntries(client, "*", AllEntries).Any())
            throw new MinionClientDirException(MinionClientDirError.AlreadyExists, $"客户端目录已存在: {client}");

        if (File.Exists(client))
            throw new MinionClientDirException(MinionClientDirError.AlreadyExists, $"客户端目录位置上已有同名文件: {client}");

        var plugins = new Dictionary<string, DirectoryInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in new DirectoryInfo(mainLuaMods).EnumerateDirectories("*", AllEntries))
            plugins.TryAdd(directory.Name, directory);

        // Profiler 的配置定义
        string?           settingsRelative = null;
        ProfilerSettings? settings         = null;

        if (plugins.TryGetValue(PROFILER_DIR_NAME, out var profilerDir) && File.Exists(Path.Combine(profilerDir.FullName, PROFILER_SETTINGS)))
        {
            settingsRelative = Path.Combine(profilerDir.Name, PROFILER_SETTINGS);
            var settingsPath = Path.Combine(mainLuaMods, settingsRelative);

            try
            {
                settings = ProfilerSettings.Load(settingsPath);
            }
            catch (FormatException ex)
            {
                throw new MinionClientDirException(MinionClientDirError.SettingsUnreadable, "Minion 配置文件读不出", ex);
            }
        }

        ProfilerProfile? chosen = null;

        if (profile != null)
        {
            chosen = settings?.Find(profile)
                     ?? throw new MinionClientDirException(MinionClientDirError.ProfileNotFound, $"本机 Minion 没有配置 {profile}");
        }

        var missing = new List<string>();
        var forced  = ForcedMods(plugins, ensureMods ?? [], missing);

        foreach (var name in missing)
            Log.Warning("[Minion] 要求打开的插件 {Mod} 不在主目录里", name);

        var enabled = chosen == null ? null : new HashSet<string>(chosen.Mods, StringComparer.OrdinalIgnoreCase);

        bool? Desired(string plugin)
        {
            if (forced.Contains(plugin))
                return true;

            return enabled?.Contains(plugin);
        }

        var assembledAt = DateTime.UtcNow;
        var writeBack   = new List<string>();

        try
        {
            Directory.CreateDirectory(client);

            foreach (var entry in new DirectoryInfo(main).EnumerateFileSystemInfos("*", AllEntries))
            {
                var target = Path.Combine(client, entry.Name);

                if (entry is DirectoryInfo bots && Is(entry, BOTS_DIR_NAME))
                    AssembleBots(bots, target, Desired, settingsRelative, writeBack);
                else if (entry is FileInfo && Is(entry, LAUNCHER_INFO_NAME))
                    File.Copy(entry.FullName, target); // MinionLauncher 会重建这个文件, 硬链接本来就会断
                else
                    Mirror(entry, target);
            }

            if (settings != null)
            {
                var text = chosen == null ? settings.Text : settings.WithCurrentProfile(chosen.Name);
                var path = Path.Combine(client, BOTS_DIR_NAME, BOT_DIR_NAME, LUA_MODS_DIR_NAME, settingsRelative!);
                File.WriteAllText(path, text, new UTF8Encoding(false));
            }

            var snapshot = new MinionClientDirSnapshot
            {
                MainInstall          = main,
                AssembledAtUtc       = assembledAt,
                Profile              = chosen?.Name,
                ForcedMods           = forced.Order(StringComparer.OrdinalIgnoreCase).ToList(),
                ProfilerSettingsPath = settingsRelative,
                Profiles             = settings?.Profiles,
                WriteBackFiles       = writeBack
            };

            WriteSnapshot(client, snapshot);

            Log.Information
            (
                "[Minion] 已组装客户端目录 {Dir}: 配置={Profile}, 强制打开={Forced}",
                client,
                chosen?.Name ?? "(照主目录)",
                string.Join(",", snapshot.ForcedMods)
            );

            return new MinionClientDir(client, snapshot, missing);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Minion] 组装客户端目录 {Dir} 失败, 删除已建的部分", client);

            try
            {
                Delete(client);
            }
            catch (Exception cleanup)
            {
                Log.Warning(cleanup, "[Minion] 删除组装了一半的客户端目录 {Dir} 失败", client);
            }

            throw;
        }
    }

    /// <summary>
    ///     起游戏前先看主目录的 Profiler 里有没有这个配置: 有（或 <paramref name="profile" /> 为 null、主目录没有 Profiler 配置文件时不查）返回 null,
    ///     否则返回与 <see cref="Assemble" /> 相同的 <see cref="MinionClientDirError.SettingsUnreadable" /> / <see cref="MinionClientDirError.ProfileNotFound" /> 异常（不抛出）
    /// </summary>
    public static MinionClientDirException? CheckProfile(string mainInstall, string? profile)
    {
        if (profile == null)
            return null;

        var path = Path.Combine(Normalize(mainInstall), BOTS_DIR_NAME, BOT_DIR_NAME, LUA_MODS_DIR_NAME, PROFILER_DIR_NAME, PROFILER_SETTINGS);

        if (!File.Exists(path))
            return new MinionClientDirException(MinionClientDirError.ProfileNotFound, $"本机 Minion 没有配置 {profile}");

        try
        {
            return ProfilerSettings.Load(path).Find(profile) == null
                       ? new MinionClientDirException(MinionClientDirError.ProfileNotFound, $"本机 Minion 没有配置 {profile}")
                       : null;
        }
        catch (FormatException ex)
        {
            return new MinionClientDirException(MinionClientDirError.SettingsUnreadable, "Minion 配置文件读不出", ex);
        }
    }

    /// <summary>
    ///     读回已组装的客户端目录（如崩溃重开时复用、交接后接管）; 不是客户端目录或没有组装信息时返回 null
    /// </summary>
    public static MinionClientDir? Open(string clientDir)
    {
        var client = Normalize(clientDir);

        if (!IsClientDirPath(client))
            return null;

        var snapshot = ReadSnapshot(client);
        return snapshot == null ? null : new MinionClientDir(client, snapshot, []);
    }

    /// <summary>
    ///     游戏退出后收尾: 把游戏里改过的 Profiler 配置定义与插件设置写回主目录（主目录的 <c>currentProfile</c> 不变）, 再删掉客户端目录。
    ///     写回失败只记进结果, 照样删除; 删除失败抛异常。拒绝处理不在 <c>MINI-clients</c> 下的路径
    /// </summary>
    /// <param name="clientDir">客户端目录</param>
    /// <param name="mainInstall">主目录; 与组装时记下的不同时不写回</param>
    /// <param name="cancellationToken">取消</param>
    public static async Task<MinionClientDirRetireResult> RetireAsync(string clientDir, string mainInstall, CancellationToken cancellationToken = default)
    {
        var client = Normalize(clientDir);
        EnsureClientDirPath(client);

        var warnings = new List<string>();
        var info     = new DirectoryInfo(client);

        if (!info.Exists)
            return new MinionClientDirRetireResult(false, 0, false, warnings);

        var profilesWritten = false;
        var filesWritten    = 0;
        var snapshot        = MinionLinks.IsReparsePoint(info) ? null : ReadSnapshot(client);

        if (snapshot == null)
        {
            warnings.Add("没有组装信息, 不写回");
        }
        else if (!string.Equals(snapshot.MainInstall, Normalize(mainInstall), StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add($"组装时的主目录是 {snapshot.MainInstall}, 不是 {mainInstall}, 不写回");
        }
        else
        {
            var mainLuaMods   = Path.Combine(snapshot.MainInstall, BOTS_DIR_NAME, BOT_DIR_NAME, LUA_MODS_DIR_NAME);
            var clientLuaMods = Path.Combine(client, BOTS_DIR_NAME, BOT_DIR_NAME, LUA_MODS_DIR_NAME);

            try
            {
                filesWritten = WriteBackFiles(clientLuaMods, mainLuaMods, snapshot, warnings);
            }
            catch (Exception ex)
            {
                warnings.Add($"写回插件文件失败: {ex.Message}");
            }

            try
            {
                profilesWritten = await WriteBackProfilesAsync(clientLuaMods, mainLuaMods, snapshot, warnings).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                warnings.Add($"写回 Profiler 配置失败: {ex.Message}");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        foreach (var warning in warnings)
            Log.Warning("[Minion] 收尾客户端目录 {Dir}: {Warning}", client, warning);

        Delete(client);
        Log.Information("[Minion] 已删除客户端目录 {Dir}: 写回配置={Profiles}, 写回文件={Files}", client, profilesWritten, filesWritten);
        return new MinionClientDirRetireResult(profilesWritten, filesWritten, true, warnings);
    }

    /// <summary>
    ///     清理残留: <paramref name="clientsRoot" />（<see cref="RootFor" />）下 <paramref name="isAlive" /> 判为已不在用的客户端目录,
    ///     先写回再删除。<paramref name="isAlive" /> 收到的是目录名（组装时的编号）, 抛异常按仍在用处理。返回清掉的个数
    /// </summary>
    public static async Task<int> CleanupStaleAsync(string clientsRoot, Func<string, bool> isAlive, CancellationToken cancellationToken = default)
    {
        var root = new DirectoryInfo(Normalize(clientsRoot));

        if (!root.Exists || !string.Equals(root.Name, ROOT_DIR_NAME, StringComparison.OrdinalIgnoreCase))
            return 0;

        var removed = 0;

        foreach (var directory in root.EnumerateDirectories("*", AllEntries).ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();

            bool alive;

            try
            {
                alive = isAlive(directory.Name);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[Minion] 判断客户端目录 {Dir} 是否在用失败, 保留", directory.FullName);
                alive = true;
            }

            if (alive)
                continue;

            try
            {
                var snapshot = MinionLinks.IsReparsePoint(directory) ? null : ReadSnapshot(directory.FullName);

                if (snapshot != null)
                    await RetireAsync(directory.FullName, snapshot.MainInstall, cancellationToken).ConfigureAwait(false);
                else
                    Delete(directory.FullName);

                removed++;
                Log.Information("[Minion] 已清理残留客户端目录 {Dir}", directory.FullName);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[Minion] 清理残留客户端目录 {Dir} 失败", directory.FullName);
            }
        }

        return removed;
    }

    /// <summary>
    ///     删除客户端目录, 不写回。先逐个删掉联接与符号链接本身, 确认目录里已没有任何重解析点后才删其余文件与目录;
    ///     硬链接只删客户端这一个名字。拒绝处理不在 <c>MINI-clients</c> 下的路径
    /// </summary>
    public static void Delete(string clientDir)
    {
        var client = Normalize(clientDir);
        EnsureClientDirPath(client);

        var info = new DirectoryInfo(client);

        if (!info.Exists)
            return;

        if (MinionLinks.IsReparsePoint(info))
        {
            // 客户端目录本身是个链接: 只删链接
            info.Delete(false);
            return;
        }

        RemoveLinks(info);

        var left = FirstLink(info);

        if (left != null)
            throw new IOException($"客户端目录里仍有链接 {left}, 不删除");

        DeleteTree(info);
    }

    private static void AssembleBots(DirectoryInfo source, string target, Func<string, bool?> desired, string? settingsRelative, List<string> writeBack)
    {
        Directory.CreateDirectory(target);

        foreach (var entry in source.EnumerateFileSystemInfos("*", AllEntries))
        {
            var path = Path.Combine(target, entry.Name);

            if (entry is DirectoryInfo bot && Is(entry, BOT_DIR_NAME))
                AssembleBot(bot, path, desired, settingsRelative, writeBack);
            else
                Mirror(entry, path);
        }
    }

    private static void AssembleBot(DirectoryInfo source, string target, Func<string, bool?> desired, string? settingsRelative, List<string> writeBack)
    {
        Directory.CreateDirectory(target);

        foreach (var entry in source.EnumerateFileSystemInfos("*", AllEntries))
        {
            var path = Path.Combine(target, entry.Name);

            // 压缩包不放进客户端目录, 免得 Minion 解压时经链接写进主目录
            if (entry is FileInfo && Is(entry, LUA_MODS_ARCHIVE_NAME))
                continue;

            if (entry is DirectoryInfo luaMods && Is(entry, LUA_MODS_DIR_NAME))
                AssembleLuaMods(luaMods, path, desired, settingsRelative, writeBack);
            else
                Mirror(entry, path);
        }
    }

    private static void AssembleLuaMods(DirectoryInfo source, string target, Func<string, bool?> desired, string? settingsRelative, List<string> writeBack)
    {
        Directory.CreateDirectory(target);

        foreach (var entry in source.EnumerateFileSystemInfos("*", AllEntries))
        {
            var path = Path.Combine(target, entry.Name);

            if (entry is DirectoryInfo plugin)
            {
                AssemblePlugin(plugin, path, desired(plugin.Name), settingsRelative, writeBack);
            }
            else if (Mirror(entry, path) != MirrorKind.Link)
            {
                writeBack.Add(entry.Name);
            }
        }
    }

    /// <summary>
    ///     插件建成真实目录: 子目录联接回主目录, 根文件硬链接, 符号链接照原目标重建, 开关文件按 <paramref name="on" /> 摆
    ///     （null 表示照主目录当前的）
    /// </summary>
    private static void AssemblePlugin(DirectoryInfo source, string target, bool? on, string? settingsRelative, List<string> writeBack)
    {
        Directory.CreateDirectory(target);

        foreach (var entry in source.EnumerateFileSystemInfos("*", AllEntries))
        {
            var path     = Path.Combine(target, entry.Name);
            var relative = Path.Combine(source.Name, entry.Name);

            if (entry is DirectoryInfo)
            {
                Mirror(entry, path);
                continue;
            }

            // Profiler 的 Settings.lua 每个客户端一份, 组装末尾单独写
            if (settingsRelative != null && string.Equals(relative, settingsRelative, StringComparison.OrdinalIgnoreCase))
                continue;

            var kind = Classify(entry.Name);

            if (kind == SwitchKind.None)
            {
                if (Mirror(entry, path) != MirrorKind.Link)
                    writeBack.Add(relative);

                continue;
            }

            var enable = on ?? kind is SwitchKind.ModuleOn or SwitchKind.PafOn;

            if (kind is SwitchKind.ModuleOn or SwitchKind.ModuleOff)
            {
                var switchPath = Path.Combine(target, enable ? MODULE_ON : MODULE_OFF);

                if (!File.Exists(switchPath))
                    File.Copy(entry.FullName, switchPath);
            }
            else
            {
                var stem       = Path.GetFileNameWithoutExtension(entry.Name);
                var switchPath = Path.Combine(target, stem + (enable ? PAF_ON : PAF_OFF));

                if (!File.Exists(switchPath))
                    LinkOrCopy(FinalPath(entry), switchPath);
            }
        }
    }

    private enum MirrorKind
    {
        /// <summary>联接或符号链接, 写入直接落到主目录</summary>
        Link,

        /// <summary>硬链接</summary>
        HardLink,

        /// <summary>复制（跨盘建不了硬链接）</summary>
        Copy
    }

    /// <summary>
    ///     镜像一项: 目录建联接, 文件建硬链接（跨盘时复制）, 符号链接照原目标重建
    /// </summary>
    private static MirrorKind Mirror(FileSystemInfo entry, string path)
    {
        if (MinionLinks.IsReparsePoint(entry) && entry.LinkTarget is { } linkTarget)
            return RecreateLink(entry, linkTarget, path);

        if (entry is DirectoryInfo)
        {
            MinionLinks.CreateJunction(path, entry.FullName);
            return MirrorKind.Link;
        }

        return LinkOrCopy(entry.FullName, path);
    }

    /// <summary>
    ///     重建链接。相对目标按原链接所在目录换成绝对路径, 否则搬到客户端目录后指向就错了。
    ///     目录: 一律建联接指向同一目标（联接不需要提权, 本机路径上与目录符号链接效果相同）; 目标是网络路径时建目录符号链接。
    ///     文件: 建符号链接, 没有权限时对最终目标建硬链接, 跨盘再退为复制
    /// </summary>
    private static MirrorKind RecreateLink(FileSystemInfo entry, string linkTarget, string path)
    {
        var target = Path.IsPathRooted(linkTarget)
                         ? linkTarget
                         : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(entry.FullName)!, linkTarget));

        if (entry is DirectoryInfo)
        {
            if (target.StartsWith(@"\\", StringComparison.Ordinal))
                Directory.CreateSymbolicLink(path, target);
            else
                MinionLinks.CreateJunction(path, target);

            return MirrorKind.Link;
        }

        try
        {
            File.CreateSymbolicLink(path, target);
            return MirrorKind.Link;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Debug(ex, "[Minion] 建文件符号链接 {Path} 失败, 改为硬链接或复制", path);
            return LinkOrCopy(FinalPath(entry), path);
        }
    }

    private static MirrorKind LinkOrCopy(string source, string path)
    {
        if (MinionLinks.TryCreateHardLink(path, source))
            return MirrorKind.HardLink;

        File.Copy(source, path);
        return MirrorKind.Copy;
    }

    /// <summary>
    ///     一项沿着链接一路找到的真实路径; 不是链接时为它自己
    /// </summary>
    private static string FinalPath(FileSystemInfo entry)
    {
        if (entry.LinkTarget == null)
            return entry.FullName;

        return entry.ResolveLinkTarget(true)?.FullName ?? entry.FullName;
    }

    private enum SwitchKind
    {
        None,
        ModuleOn,
        ModuleOff,
        PafOn,
        PafOff
    }

    private static SwitchKind Classify(string name)
    {
        if (string.Equals(name, MODULE_ON, StringComparison.OrdinalIgnoreCase))
            return SwitchKind.ModuleOn;

        if (string.Equals(name, MODULE_OFF, StringComparison.OrdinalIgnoreCase))
            return SwitchKind.ModuleOff;

        if (name.EndsWith(PAF_ON, StringComparison.OrdinalIgnoreCase))
            return SwitchKind.PafOn;

        if (name.EndsWith(PAF_OFF, StringComparison.OrdinalIgnoreCase))
            return SwitchKind.PafOff;

        return SwitchKind.None;
    }

    /// <summary>
    ///     要强制打开的插件（磁盘上的目录名）: 要求的插件, 加上它们 module.def / module.dis 里 Dependencies 列出的, 一直追下去
    /// </summary>
    private static HashSet<string> ForcedMods(Dictionary<string, DirectoryInfo> plugins, IEnumerable<string> ensureMods, List<string> missing)
    {
        var forced  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<(string Name, bool Requested)>(ensureMods.Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => (m.Trim(), true)));

        while (pending.TryDequeue(out var item))
        {
            if (!plugins.TryGetValue(item.Name, out var plugin))
            {
                if (item.Requested)
                    missing.Add(item.Name);

                continue;
            }

            if (!forced.Add(plugin.Name))
                continue;

            foreach (var dependency in Dependencies(plugin))
                pending.Enqueue((dependency, false));
        }

        return forced;
    }

    private static IEnumerable<string> Dependencies(DirectoryInfo plugin)
    {
        foreach (var name in new[] { MODULE_ON, MODULE_OFF })
        {
            var path = Path.Combine(plugin.FullName, name);

            if (!File.Exists(path))
                continue;

            foreach (var line in File.ReadLines(path))
            {
                var trimmed = line.Trim();

                if (!trimmed.StartsWith("Dependencies", StringComparison.OrdinalIgnoreCase))
                    continue;

                var equals = trimmed.IndexOf('=');

                if (equals < 0 || !string.IsNullOrWhiteSpace(trimmed["Dependencies".Length..equals]))
                    continue;

                foreach (var dependency in trimmed[(equals + 1)..].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                    yield return dependency;
            }

            yield break;
        }
    }

    /// <summary>
    ///     插件写设置用的是「新建再替换」时客户端的硬链接会断开: 链接数为 1、修改时间晚于组装时间的文件复制回主目录;
    ///     插件在自己目录（或 <c>LuaMods</c> 根下）新建的普通文件也复制过去。返回复制的个数
    /// </summary>
    private static int WriteBackFiles(string clientLuaMods, string mainLuaMods, MinionClientDirSnapshot snapshot, List<string> warnings)
    {
        if (!Directory.Exists(clientLuaMods))
            return 0;

        var tracked = new HashSet<string>(snapshot.WriteBackFiles, StringComparer.OrdinalIgnoreCase);
        var copied  = 0;

        void CopyBack(string relative)
        {
            var source = Path.Combine(clientLuaMods, relative);
            var target = Path.Combine(mainLuaMods, relative);

            try
            {
                // 主目录那份比客户端的新: 期间别的客户端或人改过, 不拿旧的盖掉它
                if (File.Exists(target) && File.GetLastWriteTimeUtc(target) > File.GetLastWriteTimeUtc(source))
                {
                    warnings.Add($"主目录的 {relative} 比客户端的新, 不写回");
                    return;
                }

                File.Copy(source, target, true);
                copied++;
            }
            catch (Exception ex)
            {
                warnings.Add($"写回 {relative} 失败: {ex.Message}");
            }
        }

        bool ChangedSinceAssemble(FileInfo file) =>
            file.LastWriteTimeUtc > snapshot.AssembledAtUtc && MinionLinks.HardLinkCount(file.FullName) == 1;

        foreach (var relative in snapshot.WriteBackFiles)
        {
            var file = new FileInfo(Path.Combine(clientLuaMods, relative));

            if (file.Exists && !MinionLinks.IsReparsePoint(file) && ChangedSinceAssemble(file))
                CopyBack(relative);
        }

        // 新建的文件: 只看真实目录里的普通文件, 不碰开关文件
        var luaMods = new DirectoryInfo(clientLuaMods);

        foreach (var file in luaMods.EnumerateFiles("*", AllEntries))
        {
            if (!MinionLinks.IsReparsePoint(file) && !tracked.Contains(file.Name) && !File.Exists(Path.Combine(mainLuaMods, file.Name)) && ChangedSinceAssemble(file))
                CopyBack(file.Name);
        }

        foreach (var plugin in luaMods.EnumerateDirectories("*", AllEntries))
        {
            if (MinionLinks.IsReparsePoint(plugin) || !Directory.Exists(Path.Combine(mainLuaMods, plugin.Name)))
                continue;

            foreach (var file in plugin.EnumerateFiles("*", AllEntries))
            {
                var relative = Path.Combine(plugin.Name, file.Name);

                if (MinionLinks.IsReparsePoint(file)
                    || tracked.Contains(relative)
                    || Classify(file.Name) != SwitchKind.None
                    || string.Equals(relative, snapshot.ProfilerSettingsPath, StringComparison.OrdinalIgnoreCase)
                    || File.Exists(Path.Combine(mainLuaMods, relative))
                    || !ChangedSinceAssemble(file))
                    continue;

                CopyBack(relative);
            }
        }

        return copied;
    }

    /// <summary>
    ///     游戏里改过 Profiler 配置定义（增删配置、改插件列表）就写回主目录: 只换 <c>profiles</c> 表, 其余各项（含 <c>currentProfile</c>）照主目录的。
    ///     主目录在这期间被别的客户端改过时, 按组装时的快照三方合并, 不互相覆盖
    /// </summary>
    private static async Task<bool> WriteBackProfilesAsync(string clientLuaMods, string mainLuaMods, MinionClientDirSnapshot snapshot, List<string> warnings)
    {
        if (snapshot.Profiles == null || snapshot.ProfilerSettingsPath == null)
            return false;

        var clientPath = Path.Combine(clientLuaMods, snapshot.ProfilerSettingsPath);

        if (!File.Exists(clientPath))
            return false;

        ProfilerSettings edited;

        try
        {
            edited = ProfilerSettings.Load(clientPath);
        }
        catch (FormatException ex)
        {
            warnings.Add($"客户端的 Profiler 配置读不出, 不写回: {ex.Message}");
            return false;
        }

        // 客户端文件里没有 profiles 表（被写坏或改了结构）: 不当成「删光了所有配置」写回
        if (!edited.HasProfilesTable)
        {
            warnings.Add("客户端的 Profiler 配置里没有 profiles 表, 不写回");
            return false;
        }

        if (ProfilerSettings.SameProfiles(edited.Profiles, snapshot.Profiles))
            return false;

        var mainPath = Path.Combine(mainLuaMods, snapshot.ProfilerSettingsPath);

        CrossProcessMutex mutex;

        try
        {
            mutex = await CrossProcessMutex.AcquireAsync(CrossProcessMutex.NameForPath(SETTINGS_MUTEX_PREFIX, mainPath), SettingsLockTimeout).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            warnings.Add($"拿不到 Profiler 配置文件锁, 不写回: {ex.Message}");
            return false;
        }

        using (mutex)
        {
            ProfilerSettings current;

            try
            {
                current = ProfilerSettings.Load(mainPath);
            }
            catch (Exception ex) when (ex is FormatException or IOException)
            {
                warnings.Add($"主目录的 Profiler 配置读不出, 不写回: {ex.Message}");
                return false;
            }

            string text;

            if (ProfilerSettings.SameProfiles(current.Profiles, snapshot.Profiles))
            {
                // 主目录没被别人改过: 原样搬客户端的 profiles 表
                text = current.WithProfilesTableOf(edited);
            }
            else
            {
                var merged = ProfilerSettings.Merge(snapshot.Profiles, current.Profiles, edited.Profiles);

                if (merged == null)
                    return false;

                text = current.WithProfiles(merged);
            }

            if (text == current.Text)
                return false;

            ReplaceFile(mainPath, text);
            return true;
        }
    }

    /// <summary>
    ///     写临时文件再替换; 文件被短暂占用时重试几次
    /// </summary>
    private static void ReplaceFile(string path, string text)
    {
        var temp = path + ".cat-tmp";
        File.WriteAllText(temp, text, new UTF8Encoding(false));

        for (var attempt = 1;; attempt++)
        {
            try
            {
                File.Move(temp, path, true);
                return;
            }
            catch (Exception ex) when (attempt < 5 && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(200);
            }
            catch
            {
                try
                {
                    File.Delete(temp);
                }
                catch
                {
                    // 临时文件留着不影响使用
                }

                throw;
            }
        }
    }

    private static void RemoveLinks(DirectoryInfo directory)
    {
        foreach (var entry in directory.EnumerateFileSystemInfos("*", AllEntries))
        {
            if (MinionLinks.IsReparsePoint(entry))
                MinionLinks.DeleteLinkOnly(entry);
            else if (entry is DirectoryInfo child)
                RemoveLinks(child);
        }
    }

    private static string? FirstLink(DirectoryInfo directory)
    {
        foreach (var entry in directory.EnumerateFileSystemInfos("*", AllEntries))
        {
            if (MinionLinks.IsReparsePoint(entry))
                return entry.FullName;

            if (entry is DirectoryInfo child && FirstLink(child) is { } found)
                return found;
        }

        return null;
    }

    /// <summary>
    ///     删除已确认不含链接的目录树
    /// </summary>
    private static void DeleteTree(DirectoryInfo directory)
    {
        foreach (var entry in directory.EnumerateFileSystemInfos("*", AllEntries))
        {
            if (entry is DirectoryInfo child && !MinionLinks.IsReparsePoint(child))
                DeleteTree(child);
            else if (entry is DirectoryInfo link)
                link.Delete(false);
            else
                MinionLinks.DeleteFile(entry.FullName);
        }

        directory.Delete(false);
    }

    private static void EnsureClientDirPath(string path)
    {
        if (!IsClientDirPath(path))
            throw new MinionClientDirException(MinionClientDirError.NotAClientDir, $"{path} 不在 {ROOT_DIR_NAME} 目录下, 拒绝操作");
    }

    private static void WriteSnapshot(string client, MinionClientDirSnapshot snapshot)
    {
        var path = Path.Combine(client, SNAPSHOT_FILE_NAME);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(snapshot, JsonOptions), new UTF8Encoding(false));
        File.Move(temp, path, true);
    }

    private static MinionClientDirSnapshot? ReadSnapshot(string client)
    {
        var path = Path.Combine(client, SNAPSHOT_FILE_NAME);

        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<MinionClientDirSnapshot>(File.ReadAllText(path), JsonOptions) : null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Minion] 读客户端目录组装信息 {Path} 失败", path);
            return null;
        }
    }

    private static bool Is(FileSystemInfo entry, string name) =>
        string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        return full.Length > 3 ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : full;
    }
}
