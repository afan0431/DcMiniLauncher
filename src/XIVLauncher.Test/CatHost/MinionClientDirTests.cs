using System.IO;
using System.Security.Cryptography;
using XIVLauncher.Minion;
using Xunit;

namespace XIVLauncher.Test.CatHost;

public sealed class MinionClientDirTests : IDisposable
{
    private const string SETTINGS = """
                                    local tbl =
                                    {
                                    	currentProfile = "Main",
                                    	open = false,
                                    	profiles =
                                    	{
                                    		EX =
                                    		{
                                    			"Profiler",
                                    			"RikuDRK",
                                    			"minionlib",
                                    		},
                                    		Main =
                                    		{
                                    			"Profiler",
                                    			"ffxivminion",
                                    			"TensorCore",
                                    			"MinionDB",
                                    		},
                                    	},
                                    	visible = true,
                                    }
                                    return tbl
                                    """;

    private readonly string temp = Path.Combine(Path.GetTempPath(), "cat-minion-clientdir-" + Guid.NewGuid().ToString("N"));
    private readonly string main;
    private readonly string clients;
    private readonly string external;
    private readonly bool   symlinks;

    public MinionClientDirTests()
    {
        main     = Path.Combine(temp, "MINI");
        clients  = Path.Combine(temp, MinionClientDir.ROOT_DIR_NAME);
        external = Path.Combine(temp, "external");

        Write(@"MinionLauncher_64.exe", "exe");
        Write(@"MinionLauncher_64.dat", "dat");
        Write(@"MinionLauncherInfo.txt", "info");
        Write(@"settings.bin", "bin");
        Write(@"Settings\Accounts.json", "{}");
        Write(@"Logs\a.log", "log");
        Write(@"Bots\FFXIVMinion64\gui.ini", "gui");
        Write(@"Bots\FFXIVMinion64\LuaMods.7z", "7z");
        Write(@"Bots\FFXIVMinion64\MinionFiles\FFXIVMinionCN_64.dat", "cn");
        Write(@"Bots\FFXIVMinion64\Navigation\a.nav", "nav");
        Write(@"Bots\FFXIVMinion64\LuaMods\AfanSAM_Settings.txt", "sam");
        Write(@"Bots\FFXIVMinion64\LuaMods\Profiler\41653DFCD0CE11EAAAD152540012300A.paf", "profiler");
        Write(@"Bots\FFXIVMinion64\LuaMods\Profiler\Settings.lua", SETTINGS);
        Write(@"Bots\FFXIVMinion64\LuaMods\Profiler\images\p.png", "png");
        Write(@"Bots\FFXIVMinion64\LuaMods\Afan\module.dis", "[Module]\nName=Afan\nDependencies=FFXIVMINION, minionlib\nEnabled=1\n");
        Write(@"Bots\FFXIVMinion64\LuaMods\Afan\src\main.lua", "main");
        Write(@"Bots\FFXIVMinion64\LuaMods\Afan\settings.lua", "afan settings");
        Write(@"Bots\FFXIVMinion64\LuaMods\ffxivminion\module.def", "[Module]\nName=ffxivminion\n");
        Write(@"Bots\FFXIVMinion64\LuaMods\ffxivminion\ffxiv.lua", "ffxiv");
        Write(@"Bots\FFXIVMinion64\LuaMods\ffxivminion\Behavior\b.lua", "b");
        Write(@"Bots\FFXIVMinion64\LuaMods\minionlib\module.dis", "[Module]\nName=minionlib\n");
        Write(@"Bots\FFXIVMinion64\LuaMods\TensorCore\9EA6C9F3CC7F11E98DE452540012300A.paf", "tensor");
        Write(@"Bots\FFXIVMinion64\LuaMods\TensorCore\settings.lua", "tensor settings");
        Write(@"Bots\FFXIVMinion64\LuaMods\TensorCore\Defs\d.lua", "d");
        Write(@"Bots\FFXIVMinion64\LuaMods\RikuDRK\ADADD894FD6A11EAA37352540012300A.dis", "riku");
        Write(@"Bots\FFXIVMinion64\LuaMods\Questing\QuestProfiles\q.lua", "q");

        var readOnly = Path.Combine(main, @"Bots\FFXIVMinion64\LuaMods\ffxivminion\ro.lua");
        File.WriteAllText(readOnly, "ro");
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);

        // 插件目录本身是联接（如本机的 Afan 指向部署目录）
        Directory.CreateDirectory(Path.Combine(external, "Linked"));
        File.WriteAllText(Path.Combine(external, "Linked", "module.def"), "[Module]\nName=Linked\n");
        File.WriteAllText(Path.Combine(external, "Linked", "linked.lua"), "linked");
        File.WriteAllText(Path.Combine(external, "bootstrap.lua"), "bootstrap");
        MinionLinks.CreateJunction(Path.Combine(main, @"Bots\FFXIVMinion64\LuaMods\Linked"), Path.Combine(external, "Linked"));

        // 插件根下的文件符号链接（相对目标）; 没有建符号链接的权限时跳过相关断言
        try
        {
            File.CreateSymbolicLink(Path.Combine(main, @"Bots\FFXIVMinion64\LuaMods\ffxivminion\bootstrap.lua"), @"..\..\..\..\..\external\bootstrap.lua");
            symlinks = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            symlinks = false;
        }
    }

    private string LuaMods => Path.Combine(main, @"Bots\FFXIVMinion64\LuaMods");

    public void Dispose()
    {
        if (!Directory.Exists(temp))
            return;

        // 本机 .NET 递归删除遇到目录联接会报「参数错误」, 先逐个删掉链接本身
        RemoveLinksAndReadOnly(new DirectoryInfo(temp));
        Directory.Delete(temp, true);
    }

    private static void RemoveLinksAndReadOnly(DirectoryInfo directory)
    {
        foreach (var entry in directory.EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0 }))
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                if (entry is DirectoryInfo link)
                    link.Delete(false);
                else
                    entry.Delete();
            }
            else if (entry is DirectoryInfo child)
            {
                RemoveLinksAndReadOnly(child);
            }
            else
            {
                entry.Attributes = FileAttributes.Normal;
            }
        }
    }

    [Fact]
    public void PathFor_UsesClientsRootOnMainDrive()
    {
        Assert.Equal(@"E:\MINI-clients", MinionClientDir.RootFor(@"E:\MINI"));
        Assert.Equal(@"E:\MINI-clients\1234", MinionClientDir.PathFor(@"E:\MINI\", "1234"));
        Assert.True(MinionClientDir.IsClientDirPath(@"E:\MINI-clients\1234"));
        Assert.False(MinionClientDir.IsClientDirPath(@"E:\MINI"));
        Assert.False(MinionClientDir.IsClientDirPath(@"E:\MINI-clients"));
        Assert.Throws<ArgumentException>(() => MinionClientDir.PathFor(@"E:\MINI", @"..\x"));
    }

    [Fact]
    public void Assemble_WithProfile_LinksBackToMain_AndSwitchesFollowProfile()
    {
        var before = Hashes(main);
        var dir    = MinionClientDir.Assemble(main, ClientPath("1"), "EX", []);
        var client = dir.DirectoryPath;
        var mods   = Path.Combine(client, @"Bots\FFXIVMinion64\LuaMods");

        // 安装根目录: 文件硬链接, 目录联接, MinionLauncherInfo.txt 复制
        AssertHardLink(Path.Combine(client, "MinionLauncher_64.exe"));
        AssertHardLink(Path.Combine(client, "settings.bin"));
        AssertCopy(Path.Combine(client, "MinionLauncherInfo.txt"), "info");
        AssertJunction(Path.Combine(client, "Settings"));
        AssertJunction(Path.Combine(client, "Logs"));
        AssertRealDir(Path.Combine(client, "Bots"));
        AssertRealDir(Path.Combine(client, @"Bots\FFXIVMinion64"));
        Assert.Equal(Path.Combine(client, @"Bots\FFXIVMinion64"), dir.BotPath);
        Assert.Equal(Path.Combine(client, "MinionLauncher_64.exe"), dir.LauncherExe);

        // Bots\FFXIVMinion64: 压缩包不带, 其余链接回去
        Assert.False(File.Exists(Path.Combine(client, @"Bots\FFXIVMinion64\LuaMods.7z")));
        AssertHardLink(Path.Combine(client, @"Bots\FFXIVMinion64\gui.ini"));
        AssertJunction(Path.Combine(client, @"Bots\FFXIVMinion64\MinionFiles"));
        Assert.Equal("cn", File.ReadAllText(Path.Combine(client, @"Bots\FFXIVMinion64\MinionFiles\FFXIVMinionCN_64.dat")));
        AssertRealDir(mods);

        // 插件: 真实目录, 子目录联接, 根文件硬链接
        foreach (var plugin in new[] { "Profiler", "Afan", "ffxivminion", "minionlib", "TensorCore", "RikuDRK", "Questing", "Linked" })
            AssertRealDir(Path.Combine(mods, plugin));

        AssertHardLink(Path.Combine(mods, "AfanSAM_Settings.txt"));
        AssertJunction(Path.Combine(mods, @"ffxivminion\Behavior"));
        AssertJunction(Path.Combine(mods, @"Questing\QuestProfiles"));
        AssertHardLink(Path.Combine(mods, @"ffxivminion\ffxiv.lua"));
        AssertHardLink(Path.Combine(mods, @"TensorCore\settings.lua"));

        // 开关: 只开 EX 里的 Profiler、RikuDRK、minionlib
        AssertSwitch(mods, "Profiler", "41653DFCD0CE11EAAAD152540012300A.paf");
        AssertSwitch(mods, "RikuDRK", "ADADD894FD6A11EAA37352540012300A.paf");
        AssertSwitch(mods, "minionlib", "module.def");
        AssertSwitch(mods, "TensorCore", "9EA6C9F3CC7F11E98DE452540012300A.dis");
        AssertSwitch(mods, "ffxivminion", "module.dis");
        AssertSwitch(mods, "Afan", "module.dis");
        AssertSwitch(mods, "Linked", "module.dis");
        AssertHardLink(Path.Combine(mods, @"RikuDRK\ADADD894FD6A11EAA37352540012300A.paf"));
        AssertCopy(Path.Combine(mods, @"minionlib\module.def"), "[Module]\nName=minionlib\n");

        // 插件目录本是联接: 客户端里是真实目录, 跨不了盘的文件复制, 同盘的硬链接
        Assert.Equal("linked", File.ReadAllText(Path.Combine(mods, @"Linked\linked.lua")));

        // Profiler 的 Settings.lua: 自己一份, 只改 currentProfile
        var settingsPath = Path.Combine(mods, @"Profiler\Settings.lua");
        AssertCopy(settingsPath, SETTINGS.Replace("currentProfile = \"Main\"", "currentProfile = \"EX\""));

        // 组装信息
        Assert.True(File.Exists(Path.Combine(client, MinionClientDir.SNAPSHOT_FILE_NAME)));
        var opened = MinionClientDir.Open(client)!;
        Assert.Equal("EX", opened.Snapshot.Profile);
        Assert.Equal(main, opened.Snapshot.MainInstall);
        Assert.Equal(@"Profiler\Settings.lua", opened.Snapshot.ProfilerSettingsPath);
        Assert.Equal(["EX", "Main"], opened.Snapshot.Profiles!.Select(p => p.Name));
        Assert.Contains("AfanSAM_Settings.txt", opened.Snapshot.WriteBackFiles);
        Assert.Contains(@"TensorCore\settings.lua", opened.Snapshot.WriteBackFiles);
        Assert.DoesNotContain(opened.Snapshot.WriteBackFiles, f => f.EndsWith(".paf") || f.EndsWith(".dis") || f.EndsWith("module.def") || f.EndsWith("Settings.lua"));

        // 在客户端里按 Profiler 的方式切开关, 主目录不动
        File.Move(Path.Combine(mods, @"TensorCore\9EA6C9F3CC7F11E98DE452540012300A.dis"), Path.Combine(mods, @"TensorCore\9EA6C9F3CC7F11E98DE452540012300A.paf"));
        File.Move(Path.Combine(mods, @"minionlib\module.def"), Path.Combine(mods, @"minionlib\module.dis"));

        Assert.Equal(before, Hashes(main));
        Assert.True(File.GetAttributes(Path.Combine(LuaMods, @"ffxivminion\ro.lua")).HasFlag(FileAttributes.ReadOnly));
    }

    [Fact]
    public void Assemble_WithSymlinkInPlugin_RecreatesItWithAbsoluteTarget()
    {
        if (!symlinks)
            return;

        var client = MinionClientDir.Assemble(main, ClientPath("1"), null, []).DirectoryPath;
        var link   = new FileInfo(Path.Combine(client, @"Bots\FFXIVMinion64\LuaMods\ffxivminion\bootstrap.lua"));

        Assert.True(link.Attributes.HasFlag(FileAttributes.ReparsePoint));
        Assert.Equal(Path.Combine(external, "bootstrap.lua"), link.LinkTarget);
        Assert.Equal("bootstrap", File.ReadAllText(link.FullName));
    }

    [Fact]
    public void Assemble_WithoutProfile_MirrorsMainSwitches()
    {
        var before = Hashes(main);
        var client = MinionClientDir.Assemble(main, ClientPath("1"), null, null).DirectoryPath;
        var mods   = Path.Combine(client, @"Bots\FFXIVMinion64\LuaMods");

        AssertSwitch(mods, "TensorCore", "9EA6C9F3CC7F11E98DE452540012300A.paf");
        AssertSwitch(mods, "RikuDRK", "ADADD894FD6A11EAA37352540012300A.dis");
        AssertSwitch(mods, "ffxivminion", "module.def");
        AssertSwitch(mods, "minionlib", "module.dis");
        AssertSwitch(mods, "Afan", "module.dis");
        AssertSwitch(mods, "Linked", "module.def");
        Assert.Equal(SETTINGS, File.ReadAllText(Path.Combine(mods, @"Profiler\Settings.lua")));
        Assert.Null(MinionClientDir.Open(client)!.Snapshot.Profile);
        Assert.Equal(before, Hashes(main));
    }

    [Fact]
    public void Assemble_EnsureMods_ForcesPluginAndDependencies_CaseInsensitive()
    {
        var dir  = MinionClientDir.Assemble(main, ClientPath("1"), "EX", ["afan", "NoSuchMod"]);
        var mods = Path.Combine(dir.DirectoryPath, @"Bots\FFXIVMinion64\LuaMods");

        AssertSwitch(mods, "Afan", "module.def");
        AssertSwitch(mods, "ffxivminion", "module.def");
        AssertSwitch(mods, "minionlib", "module.def");
        AssertSwitch(mods, "TensorCore", "9EA6C9F3CC7F11E98DE452540012300A.dis");
        Assert.Equal(["Afan", "ffxivminion", "minionlib"], dir.Snapshot.ForcedMods);
        Assert.Equal(["NoSuchMod"], dir.MissingMods);
    }

    [Fact]
    public void Assemble_UnknownProfile_FailsAndLeavesNothing()
    {
        var error = Assert.Throws<MinionClientDirException>(() => MinionClientDir.Assemble(main, ClientPath("1"), "main", []));

        Assert.Equal(MinionClientDirError.ProfileNotFound, error.Error);
        Assert.Equal("本机 Minion 没有配置 main", error.Message);
        Assert.False(Directory.Exists(ClientPath("1")));
    }

    [Fact]
    public void Assemble_UnreadableSettings_Fails()
    {
        File.WriteAllText(Path.Combine(LuaMods, @"Profiler\Settings.lua"), "local tbl = { profiles = ");

        var error = Assert.Throws<MinionClientDirException>(() => MinionClientDir.Assemble(main, ClientPath("1"), null, []));

        Assert.Equal(MinionClientDirError.SettingsUnreadable, error.Error);
        Assert.Equal("Minion 配置文件读不出", error.Message);
    }

    [Fact]
    public void Assemble_RefusesPathOutsideClientsRoot_AndExistingDir()
    {
        var outside = Assert.Throws<MinionClientDirException>(() => MinionClientDir.Assemble(main, Path.Combine(temp, "other", "1"), null, []));
        Assert.Equal(MinionClientDirError.NotAClientDir, outside.Error);

        MinionClientDir.Assemble(main, ClientPath("1"), null, []);
        var exists = Assert.Throws<MinionClientDirException>(() => MinionClientDir.Assemble(main, ClientPath("1"), null, []));
        Assert.Equal(MinionClientDirError.AlreadyExists, exists.Error);
    }

    [Fact]
    public async Task Retire_WithoutChanges_DeletesClientDir_MainUntouched()
    {
        var before = Hashes(main);
        var files  = Directory.GetFileSystemEntries(main, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }).Length;
        var linked = Hashes(external);
        var client = MinionClientDir.Assemble(main, ClientPath("1"), "EX", ["Afan"]).DirectoryPath;

        var result = await MinionClientDir.RetireAsync(client, main);

        Assert.True(result.Deleted);
        Assert.False(result.ProfilesWrittenBack);
        Assert.Equal(0, result.FilesWrittenBack);
        Assert.Empty(result.Warnings);
        Assert.False(Directory.Exists(client));
        Assert.Equal(before, Hashes(main));
        Assert.Equal(files, Directory.GetFileSystemEntries(main, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }).Length);
        Assert.Equal(linked, Hashes(external));
        Assert.True(File.GetAttributes(Path.Combine(LuaMods, @"ffxivminion\ro.lua")).HasFlag(FileAttributes.ReadOnly));
    }

    [Fact]
    public async Task Retire_WritesBackEditedProfiles_KeepsMainCurrentProfile()
    {
        var client   = MinionClientDir.Assemble(main, ClientPath("1"), "EX", []).DirectoryPath;
        var path     = Path.Combine(client, @"Bots\FFXIVMinion64\LuaMods\Profiler\Settings.lua");
        var settings = ProfilerSettings.Load(path);
        File.WriteAllText(path, ProfilerSettings.Parse(settings.WithProfiles([.. settings.Profiles, new ProfilerProfile("New", ["Afan"])])).WithCurrentProfile("New"));

        var result = await MinionClientDir.RetireAsync(client, main);

        Assert.True(result.ProfilesWrittenBack);
        var written = ProfilerSettings.Load(Path.Combine(LuaMods, @"Profiler\Settings.lua"));
        Assert.Equal("Main", written.CurrentProfile);
        Assert.Equal(["EX", "Main", "New"], written.Profiles.Select(p => p.Name));
        Assert.Equal(["Afan"], written.Find("New")!.Mods);
        Assert.Contains("\tvisible = true,", written.Text);
        Assert.False(Directory.Exists(client));
    }

    [Fact]
    public async Task Retire_MainEditedMeanwhile_MergesBothChanges()
    {
        var client     = MinionClientDir.Assemble(main, ClientPath("1"), "EX", []).DirectoryPath;
        var clientPath = Path.Combine(client, @"Bots\FFXIVMinion64\LuaMods\Profiler\Settings.lua");
        var mainPath   = Path.Combine(LuaMods, @"Profiler\Settings.lua");

        var mainSettings = ProfilerSettings.Load(mainPath);
        File.WriteAllText(mainPath, mainSettings.WithProfiles([.. mainSettings.Profiles, new ProfilerProfile("Other", ["o"])]));

        var clientSettings = ProfilerSettings.Load(clientPath);
        File.WriteAllText(clientPath, clientSettings.WithProfiles([.. clientSettings.Profiles.Where(p => p.Name != "EX"), new ProfilerProfile("New", ["n"])]));

        var result = await MinionClientDir.RetireAsync(client, main);

        Assert.True(result.ProfilesWrittenBack);
        var written = ProfilerSettings.Load(mainPath);
        Assert.Equal(["Main", "Other", "New"], written.Profiles.Select(p => p.Name));
        Assert.Equal("Main", written.CurrentProfile);
    }

    [Fact]
    public async Task Retire_CopiesBackReplacedSettingsFiles_AndNewFiles()
    {
        var client = MinionClientDir.Assemble(main, ClientPath("1"), null, []).DirectoryPath;
        var mods   = Path.Combine(client, @"Bots\FFXIVMinion64\LuaMods");

        // 插件「新建再替换」写设置: 客户端的硬链接断开
        var replaced = Path.Combine(mods, @"TensorCore\settings.lua");
        File.WriteAllText(replaced + ".new", "tensor settings v2");
        File.Move(replaced + ".new", replaced, true);
        File.WriteAllText(Path.Combine(mods, @"TensorCore\new.lua"), "new");
        File.WriteAllText(Path.Combine(mods, "Loose.txt"), "loose");

        // 原地写经硬链接本来就落到主目录, 不算写回
        File.WriteAllText(Path.Combine(mods, @"ffxivminion\ffxiv.lua"), "ffxiv v2");

        var result = await MinionClientDir.RetireAsync(client, main);

        Assert.Equal(3, result.FilesWrittenBack);
        Assert.Equal("tensor settings v2", File.ReadAllText(Path.Combine(LuaMods, @"TensorCore\settings.lua")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(LuaMods, @"TensorCore\new.lua")));
        Assert.Equal("loose", File.ReadAllText(Path.Combine(LuaMods, "Loose.txt")));
        Assert.Equal("ffxiv v2", File.ReadAllText(Path.Combine(LuaMods, @"ffxivminion\ffxiv.lua")));
        Assert.True(File.Exists(Path.Combine(LuaMods, @"TensorCore\9EA6C9F3CC7F11E98DE452540012300A.paf")));
        Assert.False(Directory.Exists(client));
    }

    [Fact]
    public async Task Retire_AndDelete_RefusePathsOutsideClientsRoot()
    {
        var retire = await Assert.ThrowsAsync<MinionClientDirException>(() => MinionClientDir.RetireAsync(main, main));
        Assert.Equal(MinionClientDirError.NotAClientDir, retire.Error);

        var delete = Assert.Throws<MinionClientDirException>(() => MinionClientDir.Delete(LuaMods));
        Assert.Equal(MinionClientDirError.NotAClientDir, delete.Error);

        Assert.True(Directory.Exists(LuaMods));
    }

    [Fact]
    public void Delete_ClientDirThatIsItselfALink_RemovesOnlyTheLink()
    {
        var before = Hashes(main);
        Directory.CreateDirectory(clients);
        MinionLinks.CreateJunction(ClientPath("9"), main);

        MinionClientDir.Delete(ClientPath("9"));

        Assert.False(Directory.Exists(ClientPath("9")));
        Assert.Equal(before, Hashes(main));
    }

    [Fact]
    public async Task CleanupStale_RetiresDeadClients_KeepsLiveOnes()
    {
        var before = Hashes(external);
        var live   = MinionClientDir.Assemble(main, ClientPath("111"), "EX", []).DirectoryPath;
        var dead   = MinionClientDir.Assemble(main, ClientPath("222"), "EX", []).DirectoryPath;
        var broken = ClientPath("333");
        Directory.CreateDirectory(Path.Combine(broken, "half"));
        MinionLinks.CreateJunction(Path.Combine(broken, "half", "Logs"), Path.Combine(main, "Logs"));

        var path     = Path.Combine(dead, @"Bots\FFXIVMinion64\LuaMods\Profiler\Settings.lua");
        var settings = ProfilerSettings.Load(path);
        File.WriteAllText(path, settings.WithProfiles([.. settings.Profiles, new ProfilerProfile("FromDead", ["x"])]));

        var removed = await MinionClientDir.CleanupStaleAsync(clients, id => id == "111");

        Assert.Equal(2, removed);
        Assert.True(Directory.Exists(live));
        Assert.False(Directory.Exists(dead));
        Assert.False(Directory.Exists(broken));
        Assert.True(File.Exists(Path.Combine(main, @"Logs\a.log")));
        Assert.NotNull(ProfilerSettings.Load(Path.Combine(LuaMods, @"Profiler\Settings.lua")).Find("FromDead"));
        Assert.Equal(before, Hashes(external));
    }

    private string ClientPath(string id) => Path.Combine(clients, id);

    private void Write(string relative, string content)
    {
        var path = Path.Combine(main, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    /// <summary>
    ///     目录下所有项（不顺着链接进去）的相对路径、类型与内容哈希
    /// </summary>
    private static string Hashes(string root)
    {
        var lines = new List<string>();

        void Walk(DirectoryInfo directory)
        {
            foreach (var entry in directory.EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0 }).OrderBy(e => e.Name, StringComparer.Ordinal))
            {
                var relative = Path.GetRelativePath(root, entry.FullName);

                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    lines.Add($"L {relative} -> {entry.LinkTarget}");
                else if (entry is DirectoryInfo child)
                {
                    lines.Add($"D {relative}");
                    Walk(child);
                }
                else
                    lines.Add($"F {relative} {Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(entry.FullName)))} {entry.Attributes & FileAttributes.ReadOnly}");
            }
        }

        Walk(new DirectoryInfo(root));
        return string.Join("\n", lines);
    }

    private static void AssertHardLink(string path)
    {
        var info = new FileInfo(path);
        Assert.True(info.Exists, path);
        Assert.False(info.Attributes.HasFlag(FileAttributes.ReparsePoint), path);
        Assert.True(MinionLinks.HardLinkCount(path) >= 2, path);
    }

    private static void AssertCopy(string path, string content)
    {
        Assert.True(File.Exists(path), path);
        Assert.Equal(1, MinionLinks.HardLinkCount(path));
        Assert.Equal(content, File.ReadAllText(path));
    }

    private static void AssertJunction(string path)
    {
        var info = new DirectoryInfo(path);
        Assert.True(info.Exists, path);
        Assert.True(info.Attributes.HasFlag(FileAttributes.ReparsePoint), path);
    }

    private static void AssertRealDir(string path)
    {
        var info = new DirectoryInfo(path);
        Assert.True(info.Exists, path);
        Assert.False(info.Attributes.HasFlag(FileAttributes.ReparsePoint), path);
    }

    private static void AssertSwitch(string mods, string plugin, string expected)
    {
        var switches = Directory.GetFiles(Path.Combine(mods, plugin))
                                .Select(Path.GetFileName)
                                .Where(n => n!.EndsWith(".def") || n.EndsWith(".dis") || n.EndsWith(".paf"))
                                .ToList();

        Assert.Equal([expected], switches);
    }
}
