using System.Diagnostics;
using System.IO;
using System.Text.Json;
using XIVLauncher.CatHost;
using XIVLauncher.InGame;
using XIVLauncher.Minion;
using Xunit;

namespace XIVLauncher.Test.CatHost;

/// <summary>
///     Cat 上号的 Minion 客户端目录: launch 参数校验、目录编号、使用者判断, 以及组装 → 复用 → 收尾的整条流程（临时目录里的迷你安装）
/// </summary>
public sealed class CatMinionClientDirsTests : IDisposable
{
    private const string FINGERPRINT = "0123456789abcdef";
    private const string UID         = "11112222333344445555666677778888";

    private const string SETTINGS = """
                                    local tbl =
                                    {
                                    	currentProfile = "Main",
                                    	profiles =
                                    	{
                                    		EX =
                                    		{
                                    			"Profiler",
                                    			"ffxivminion",
                                    		},
                                    		Main =
                                    		{
                                    			"Profiler",
                                    			"ffxivminion",
                                    			"TensorCore",
                                    		},
                                    	},
                                    }
                                    return tbl
                                    """;

    private readonly string temp = Path.Combine(Path.GetTempPath(), "cat-minion-clientdirs-" + Guid.NewGuid().ToString("N"));
    private readonly string main;
    private readonly string clients;

    public CatMinionClientDirsTests()
    {
        main    = Path.Combine(temp, "MINI");
        clients = Path.Combine(temp, MinionClientDir.ROOT_DIR_NAME);

        Write(@"MinionLauncher_64.exe", "exe");
        Write(@"Bots\FFXIVMinion64\MinionFiles\FFXIVMinionCN_64.dat", "cn");
        Write(@"Bots\FFXIVMinion64\LuaMods\Profiler\41653DFCD0CE11EAAAD152540012300A.paf", "profiler");
        Write(@"Bots\FFXIVMinion64\LuaMods\Profiler\Settings.lua", SETTINGS);
        Write(@"Bots\FFXIVMinion64\LuaMods\ffxivminion\module.def", "[Module]\nName=ffxivminion\n");
        Write(@"Bots\FFXIVMinion64\LuaMods\TensorCore\9EA6C9F3CC7F11E98DE452540012300A.paf", "tensor");
        Write(@"Bots\FFXIVMinion64\LuaMods\Afan\module.dis", "[Module]\nName=Afan\nDependencies=ffxivminion\n");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(clients))
            {
                foreach (var directory in Directory.GetDirectories(clients))
                    MinionClientDir.Delete(directory);
            }

            Directory.Delete(temp, true);
        }
        catch (IOException)
        {
            // 临时目录删不掉不影响结果
        }
    }

    private void Write(string relative, string text)
    {
        var path = Path.Combine(main, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private CatMinionClientDirs NewDirs(bool enabled = true) =>
        new(() => enabled, () => main, _ => clients);

    private static CatMinionLaunch Card(string? profile = null, IReadOnlyList<string>? ensureMods = null) =>
        new(FINGERPRINT, MinionCards.VARIANT_CN, new CatSecret("KEY"), UID, "forum", new CatSecret("pass"), profile, ensureMods);

    private static Process Self => Process.GetCurrentProcess();

    private static DateTimeOffset SelfStartedAt => MinionOccupancy.GetProcessStartedAt(Process.GetCurrentProcess());

    #region launch 参数

    private static CatMinionParams Params(string? profile = null, string[]? ensureMods = null) =>
        new(FINGERPRINT, "cn", "KEY", UID, "forum", "pass", profile, ensureMods);

    [Fact]
    public void Params_ProfileAndEnsureMods_DeserializeFromCamelCase()
    {
        var parsed = JsonSerializer.Deserialize<CatMinionParams>
        (
            """{"cardFingerprint":"0123456789abcdef","variant":"cn","profile":"AfanTest","ensureMods":["Afan"]}""",
            CatProtocol.JsonOptions
        )!;

        Assert.Equal("AfanTest", parsed.Profile);
        Assert.Equal(["Afan"], parsed.EnsureMods);
        Assert.DoesNotContain("pass", Params("AfanTest").ToString());
        Assert.Contains("AfanTest", Params("AfanTest").ToString());
    }

    [Fact]
    public void Validate_TrimsProfile_DropsBlankMods_AndDeduplicates()
    {
        Assert.Null(CatLaunchHost.ValidateMinion(Params("  EX  ", ["Afan", " ", "afan", "TensorCore"]), out var card));

        Assert.Equal("EX", card!.Profile);
        Assert.Equal(["Afan", "TensorCore"], card.EnsureMods);
        Assert.DoesNotContain("KEY", card.ToString());
    }

    [Fact]
    public void Validate_BlankProfileAndNoMods_AreNull()
    {
        Assert.Null(CatLaunchHost.ValidateMinion(Params("   ", []), out var card));

        Assert.Null(card!.Profile);
        Assert.Null(card.EnsureMods);
    }

    [Fact]
    public void Validate_RejectsTooLongProfile_TooManyMods_AndBadModNames()
    {
        Assert.Equal(CatCodes.INVALID_PARAMS, CatLaunchHost.ValidateMinion(Params(new string('a', CatLaunchHost.MAX_MINION_PROFILE_LENGTH + 1)), out _)?.Code);
        Assert.Null(CatLaunchHost.ValidateMinion(Params(new string('a', CatLaunchHost.MAX_MINION_PROFILE_LENGTH)), out _));

        var many = Enumerable.Range(0, CatLaunchHost.MAX_MINION_ENSURE_MODS + 1).Select(x => $"Mod{x}").ToArray();
        Assert.Equal(CatCodes.INVALID_PARAMS, CatLaunchHost.ValidateMinion(Params(ensureMods: many), out _)?.Code);

        Assert.Equal(CatCodes.INVALID_PARAMS, CatLaunchHost.ValidateMinion(Params(ensureMods: [@"..\Afan"]), out _)?.Code);
        Assert.Equal(CatCodes.INVALID_PARAMS, CatLaunchHost.ValidateMinion(Params(ensureMods: [".."]), out _)?.Code);
    }

    #endregion

    #region 编号与使用者

    [Fact]
    public void BuildId_PidThenSanitizedOperationId()
    {
        Assert.Equal("4321-op-1_a", CatMinionClientDirs.BuildId("op-1_a", 4321));
        Assert.Equal("4321-abc", CatMinionClientDirs.BuildId(@"a/b\c:", 4321));
        Assert.Equal("4321", CatMinionClientDirs.BuildId(null, 4321));
        Assert.Equal("4321", CatMinionClientDirs.BuildId("上号", 4321));
        Assert.Equal(4 + 1 + 32, CatMinionClientDirs.BuildId(new string('x', 80), 4321).Length);
    }

    [Fact]
    public void IsInUse_OwnerOrGameAlive()
    {
        var owner = new CatMinionClientDirOwner
        {
            OwnerPid       = 10,
            OwnerStartedAt = DateTimeOffset.UnixEpoch,
            GamePid        = 20,
            GameStartedAt  = DateTimeOffset.UnixEpoch.AddDays(1)
        };

        Assert.False(CatMinionClientDirs.IsInUse(null, (_, _) => true));
        Assert.False(CatMinionClientDirs.IsInUse(owner, (_, _) => false));
        Assert.True(CatMinionClientDirs.IsInUse(owner, (pid, startedAt) => pid == 10 && startedAt == DateTimeOffset.UnixEpoch));
        Assert.True(CatMinionClientDirs.IsInUse(owner, (pid, startedAt) => pid == 20 && startedAt == owner.GameStartedAt));

        // 游戏的创建时间对不上（进程号被复用）不算
        Assert.False(CatMinionClientDirs.IsInUse(owner, (pid, startedAt) => pid == 20 && startedAt == DateTimeOffset.UnixEpoch));
        Assert.False(CatMinionClientDirs.IsInUse(owner with { GamePid = null, GameStartedAt = null }, (pid, _) => pid == 20));
    }

    [Fact]
    public void Owner_RoundTrips()
    {
        Directory.CreateDirectory(temp);
        var owner = new CatMinionClientDirOwner { OwnerPid = 1, OwnerStartedAt = SelfStartedAt, GamePid = 2, GameStartedAt = SelfStartedAt, UpdatedAt = SelfStartedAt };

        CatMinionClientDirs.WriteOwner(temp, owner);

        Assert.Equal(owner, CatMinionClientDirs.ReadOwner(temp));
        Assert.Null(CatMinionClientDirs.ReadOwner(Path.Combine(temp, "nope")));
    }

    #endregion

    #region 启动前检查配置

    [Fact]
    public void CheckProfile_KnownUnknownAndUnreadable()
    {
        Assert.Null(MinionClientDir.CheckProfile(main, null));
        Assert.Null(MinionClientDir.CheckProfile(main, "EX"));
        Assert.Equal(CatCodes.MINION_PROFILE_NOT_FOUND, CatMinionReservations.ProfileError(main, "ex")?.Code); // 配置名区分大小写, 与 Lua 的键一致

        Assert.Equal((CatCodes.MINION_PROFILE_NOT_FOUND, "本机 Minion 没有配置 Nope"), CatMinionReservations.ProfileError(main, "Nope"));

        Write(@"Bots\FFXIVMinion64\LuaMods\Profiler\Settings.lua", "local tbl = { profiles = ");
        Assert.Equal((CatCodes.MINION_SETTINGS_UNREADABLE, "Minion 配置文件读不出"), CatMinionReservations.ProfileError(main, "EX"));
        Assert.Null(CatMinionReservations.ProfileError(main, null));
    }

    #endregion

    #region 组装、复用、收尾

    [Fact]
    public async Task Prepare_AssemblesClientDir_WritesOwner_ReusesIt_ThenRetireDeletes()
    {
        var dirs     = NewDirs();
        var prepared = await dirs.PrepareAsync(Card("EX", ["Afan"]), "op-1", Self, SelfStartedAt, CancellationToken.None);

        Assert.False(prepared.Failed);
        var path = Path.Combine(clients, $"{Environment.ProcessId}-op-1");
        Assert.Equal(path, prepared.InstallPath);
        Assert.Equal(path, dirs.CurrentPath);

        // EX 不含 TensorCore; Afan 强制打开
        Assert.False(prepared.TensorCoreEnabled);
        var clientDir = MinionClientDir.Open(path)!;
        Assert.True(clientDir.IsModEnabled("Afan"));
        Assert.True(clientDir.IsModEnabled("ffxivminion"));
        Assert.False(clientDir.IsModEnabled("TensorCore"));
        Assert.True(File.Exists(Path.Combine(path, "MinionLauncher_64.exe")));

        var owner = CatMinionClientDirs.ReadOwner(path)!;
        Assert.Equal(Environment.ProcessId, owner.OwnerPid);
        Assert.Equal(Environment.ProcessId, owner.GamePid);

        // 崩溃重开或补挂: 同一个目录, 不重新组装（配置变了也不管）
        var again = await dirs.PrepareAsync(Card("Main"), "op-2", Self, SelfStartedAt, CancellationToken.None);
        Assert.Equal(path, again.InstallPath);
        Assert.False(again.TensorCoreEnabled);
        Assert.Single(Directory.GetDirectories(clients));

        // 主目录开关没被动过
        Assert.True(File.Exists(Path.Combine(main, @"Bots\FFXIVMinion64\LuaMods\Afan\module.dis")));
        Assert.False(File.Exists(Path.Combine(main, @"Bots\FFXIVMinion64\LuaMods\Afan\module.def")));

        await dirs.RetireAsync();

        Assert.False(Directory.Exists(path));
        Assert.Null(dirs.CurrentPath);
        Assert.True(File.Exists(Path.Combine(main, "MinionLauncher_64.exe")));
        Assert.True(File.Exists(Path.Combine(main, @"Bots\FFXIVMinion64\LuaMods\TensorCore\9EA6C9F3CC7F11E98DE452540012300A.paf")));

        // 没有目录时收尾什么都不做
        await dirs.RetireAsync();
    }

    [Fact]
    public async Task Prepare_NoProfile_FollowsMainSwitches()
    {
        var prepared = await NewDirs().PrepareAsync(Card(), "op", Self, SelfStartedAt, CancellationToken.None);

        Assert.True(prepared.TensorCoreEnabled);
        Assert.False(MinionClientDir.Open(prepared.InstallPath!)!.IsModEnabled("Afan"));
    }

    [Fact]
    public async Task Prepare_UnknownProfile_IsProfileNotFound_AndLeavesNothing()
    {
        var dirs     = NewDirs();
        var prepared = await dirs.PrepareAsync(Card("Nope"), "op", Self, SelfStartedAt, CancellationToken.None);

        Assert.Equal(CatCodes.MINION_PROFILE_NOT_FOUND, prepared.ErrorCode);
        Assert.Equal("本机 Minion 没有配置 Nope", prepared.ErrorMessage);
        Assert.Null(dirs.CurrentPath);
        Assert.Empty(Directory.Exists(clients) ? Directory.GetDirectories(clients) : []);
    }

    [Fact]
    public async Task Prepare_UnreadableSettings_IsSettingsUnreadable()
    {
        Write(@"Bots\FFXIVMinion64\LuaMods\Profiler\Settings.lua", "local tbl = { profiles = ");

        var prepared = await NewDirs().PrepareAsync(Card("EX"), "op", Self, SelfStartedAt, CancellationToken.None);

        Assert.Equal(CatCodes.MINION_SETTINGS_UNREADABLE, prepared.ErrorCode);
        Assert.Equal("Minion 配置文件读不出", prepared.ErrorMessage);
    }

    [Fact]
    public async Task Prepare_Disabled_UsesMainInstall()
    {
        var dirs     = NewDirs(false);
        var prepared = await dirs.PrepareAsync(Card("EX", ["Afan"]), "op", Self, SelfStartedAt, CancellationToken.None);

        Assert.Same(CatMinionClientDirPrepared.MainInstall, prepared);
        Assert.Null(dirs.CurrentPath);
        Assert.False(Directory.Exists(clients));
    }

    [Fact]
    public async Task Prepare_CleansStaleDirs_KeepsDirsInUse()
    {
        // 残留: 守护进程与游戏都已不在（这里用创建时间对不上表示）
        var stale = await NewDirs().PrepareAsync(Card("EX"), "stale", Self, SelfStartedAt, CancellationToken.None);
        CatMinionClientDirs.WriteOwner(stale.InstallPath!, new CatMinionClientDirOwner { OwnerPid = Environment.ProcessId, OwnerStartedAt = DateTimeOffset.UnixEpoch });

        // 在用: 守护进程就是本进程
        var live = await NewDirs().PrepareAsync(Card("EX"), "live", Self, SelfStartedAt, CancellationToken.None);

        var fresh = await NewDirs().PrepareAsync(Card("Main"), "fresh", Self, SelfStartedAt, CancellationToken.None);

        Assert.False(Directory.Exists(stale.InstallPath));
        Assert.True(Directory.Exists(live.InstallPath));
        Assert.True(Directory.Exists(fresh.InstallPath));
        Assert.True(File.Exists(Path.Combine(main, "MinionLauncher_64.exe")));
    }

    [Fact]
    public async Task Adopt_TakesOverDir_RewritesOwner_AndRetires()
    {
        var original = await NewDirs().PrepareAsync(Card("EX"), "op", Self, SelfStartedAt, CancellationToken.None);
        CatMinionClientDirs.WriteOwner(original.InstallPath!, new CatMinionClientDirOwner { OwnerPid = 1, OwnerStartedAt = DateTimeOffset.UnixEpoch });

        var adopter = NewDirs();
        adopter.Adopt(original.InstallPath, Environment.ProcessId, SelfStartedAt);

        Assert.Equal(original.InstallPath, adopter.CurrentPath);
        Assert.Equal(Environment.ProcessId, CatMinionClientDirs.ReadOwner(original.InstallPath!)!.OwnerPid);
        Assert.Equal(GameRecordWriter.SelfStartedAt, CatMinionClientDirs.ReadOwner(original.InstallPath!)!.OwnerStartedAt);

        // 接管后崩溃重开: 复用同一个目录
        Assert.Equal(original.InstallPath, (await adopter.PrepareAsync(Card(), "op2", Self, SelfStartedAt, CancellationToken.None)).InstallPath);

        await adopter.RetireAsync();
        Assert.False(Directory.Exists(original.InstallPath));

        // 记录里没有目录或目录已不在: 什么都不接
        var empty = NewDirs();
        empty.Adopt(null, 1, SelfStartedAt);
        empty.Adopt(original.InstallPath, 1, SelfStartedAt);
        Assert.Null(empty.CurrentPath);
    }

    #endregion
}
