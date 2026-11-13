using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using Newtonsoft.Json.Linq;
using XIVLauncher.CatHost;
using XIVLauncher.CatLobby.Protocol;
using XIVLauncher.Dalamud;
using XIVLauncher.InGame;
using XIVLauncher.Test.CatHost;
using Xunit;

namespace XIVLauncher.Test.International;

/// <summary>
///     国际服自动进入角色: 假插件开同名管道（回应用插件自己的协议层拼装），编排与国际服游戏一侧原样跑通
/// </summary>
public sealed class CatInternationalAutoEnterTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly Process       placeholder;
    private readonly FakePlugin    plugin;
    private readonly RecordingReporter reporter = new();

    public CatInternationalAutoEnterTests()
    {
        placeholder = StartPlaceholder();
        plugin      = new FakePlugin(placeholder.Id);
    }

    public void Dispose()
    {
        plugin.Dispose();

        try
        {
            if (!placeholder.HasExited)
                placeholder.Kill();
        }
        catch
        {
            // 已退出
        }

        placeholder.Dispose();
    }

    internal static Process StartPlaceholder()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName        = Path.Combine(Environment.SystemDirectory, "PING.EXE"),
            UseShellExecute = false,
            CreateNoWindow  = true
        };
        startInfo.ArgumentList.Add("-t");
        startInfo.ArgumentList.Add("127.0.0.1");

        return Process.Start(startInfo)!;
    }

    private static CatAutoEnterTimings FastTimings() =>
        new()
        {
            Poll                 = TimeSpan.FromMilliseconds(5),
            QueuePoll            = TimeSpan.FromMilliseconds(5),
            ChoicePoll           = TimeSpan.FromMilliseconds(5),
            ObserveInterval      = TimeSpan.FromMilliseconds(10),
            ModuleReadyPoll      = TimeSpan.FromMilliseconds(20),
            ReadTimeout          = TimeSpan.FromSeconds(5),
            CharaListSettle      = TimeSpan.FromMilliseconds(50),
            RelogDelays          = [TimeSpan.FromMilliseconds(10)]
        };

    #region 游戏一侧的固定行为

    [Fact]
    public async Task HasOneAreaOnly_AndCannotSwitch_AndHasNoWorldTable()
    {
        using var game = new CatInternationalAutoEnter(placeholder);

        Assert.Equal([CatInternationalAutoEnter.AREA_NAME], game.AreaNames);
        Assert.Equal(CatInternationalAutoEnter.AREA_NAME, game.CurrentAreaName);
        Assert.Empty(await game.LoadWorldsAsync(CancellationToken.None));
        Assert.NotNull(await game.SwitchAreaAsync("别的大区", CancellationToken.None));
        Assert.Equal(placeholder.Id, game.ProcessId);
        Assert.Null(await game.AttachAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Attach_GameAlreadyExited_ReportsReason()
    {
        placeholder.Kill();
        await placeholder.WaitForExitAsync();

        using var game = new CatInternationalAutoEnter(placeholder);

        Assert.NotNull(await game.AttachAsync(CancellationToken.None));
        Assert.True(game.HasExited);
    }

    [Fact]
    public async Task Send_HoldsTheGate_UntilReleased()
    {
        plugin.Start();

        using var game = new CatInternationalAutoEnter(placeholder);

        Assert.StartsWith("OK version=", await game.SendAsync("VERSION", CancellationToken.None));
        Assert.True(InGameTravelService.IsGateHeld(placeholder.Id));

        game.Release();
        Assert.False(InGameTravelService.IsGateHeld(placeholder.Id));
    }

    #endregion

    #region 编排对着假插件

    [Fact]
    public async Task SameNameOnTwoWorlds_EnglishHomeWorldPicksTheRightOne_AndLevelReadsStopQuietly()
    {
        plugin.Characters.Add(new CharaRow(1001, 0, 0, 23, 23, "Alice Fox", "Asura", "Asura", 0));
        plugin.Characters.Add(new CharaRow(1002, 1, 0, 54, 54, "Alice Fox", "Tonberry", "Tonberry", 0));
        plugin.Start();

        using var game = new CatInternationalAutoEnter(placeholder);
        var flow       = new CatAutoEnter(game, reporter, new CatAutoEnterTarget("Alice Fox", "tonberry"), FastTimings());

        Assert.Equal(CatAutoEnterOutcome.InWorld, await flow.RunAsync(CancellationToken.None).WaitAsync(Timeout));

        Assert.Equal("1002", flow.EnteredContentId);
        Assert.Contains("DIALOG YES 1002", plugin.Commands);
        Assert.Contains("character:Alice Fox@Tonberry", reporter.Entries);
        Assert.Equal(CatStages.IN_WORLD, flow.CurrentStage);
        Assert.DoesNotContain(reporter.Entries, x => x.StartsWith("stopped:", StringComparison.Ordinal));

        // 编排结束后的看当前角色: 插件对等级、副本命令回 unknown-command, 读取器按「不支持」停读, 不停手也不刷屏
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        try
        {
            await flow.ObserveAsync(cancel.Token);
        }
        catch (OperationCanceledException)
        {
            // 到时取消
        }

        Assert.Equal(1, plugin.Commands.Count(x => x == "LEVELS"));
        Assert.Equal(1, plugin.Commands.Count(x => x == "DUTY"));
        Assert.DoesNotContain(reporter.Entries, x => x.StartsWith("stopped:", StringComparison.Ordinal));
        Assert.Empty(reporter.LevelReports);
    }

    [Fact]
    public async Task SingleCharacter_NoName_Enters_AndKeepsEnglishWorldCodes()
    {
        plugin.Characters.Add(new CharaRow(2001, 0, 0, 23, 23, "Bob Wolf", "Asura", "Asura", 0));
        plugin.Start();

        using var game = new CatInternationalAutoEnter(placeholder);
        var flow       = new CatAutoEnter(game, reporter, new CatAutoEnterTarget(null, null), FastTimings());

        Assert.Equal(CatAutoEnterOutcome.InWorld, await flow.RunAsync(CancellationToken.None).WaitAsync(Timeout));

        var list    = Assert.Single(reporter.CharacterLists);
        var info    = Assert.Single(list.Characters);
        Assert.False(list.NeedsChoice);
        Assert.Equal("Asura", info.HomeWorld);
        Assert.Null(info.HomeWorldName);
    }

    [Fact]
    public async Task NoCharacterInList_StopsInsteadOfSwitchingArea()
    {
        plugin.Start();

        using var game = new CatInternationalAutoEnter(placeholder);
        var flow       = new CatAutoEnter(game, reporter, new CatAutoEnterTarget("Nobody Here", "Asura"), FastTimings());

        Assert.Equal(CatAutoEnterOutcome.Stopped, await flow.RunAsync(CancellationToken.None).WaitAsync(Timeout));

        Assert.Contains($"stopped:{CatAutoEnterStopCodes.NO_CHARACTER}", reporter.Entries);
        Assert.DoesNotContain("ENTERCHARA", string.Join('|', plugin.Commands));
    }

    [Fact]
    public async Task PluginPipeAppearsLate_HandshakeKeepsRetrying()
    {
        plugin.Characters.Add(new CharaRow(3001, 0, 0, 23, 23, "Cara Owl", "Asura", "Asura", 0));

        using var game = new CatInternationalAutoEnter(placeholder);
        var flow       = new CatAutoEnter(game, reporter, new CatAutoEnterTarget("Cara Owl", "Asura"), FastTimings());
        var run        = flow.RunAsync(CancellationToken.None);

        await Task.Delay(300);
        Assert.False(run.IsCompleted);

        plugin.Start();

        Assert.Equal(CatAutoEnterOutcome.InWorld, await run.WaitAsync(Timeout));
    }

    #endregion

    /// <summary>
    ///     假的 CatLobby 插件: 在 <c>minilauncher-&lt;pid&gt;</c> 上按插件的协议层拼装应答, 标题 → 选角 → 点登录确认框 → 进世界
    /// </summary>
    internal sealed class FakePlugin : IDisposable
    {
        private int pid;

        public FakePlugin()
        {
        }

        public FakePlugin(int pid) => this.pid = pid;

        /// <summary>管道名按游戏进程号; 进程号在游戏起来后才知道时先建后绑</summary>
        public void Bind(int gamePid) => pid = gamePid;

        private readonly CancellationTokenSource stop   = new();
        private readonly object                  sync   = new();

        private LobbyWhere where = LobbyWhere.Title;
        private ulong      selected;
        private ulong      confirming;
        private ulong      inWorld;
        private Task?      loop;

        public List<CharaRow> Characters { get; } = [];

        public ConcurrentQueue<string> Seen { get; } = new();

        public List<string> Commands
        {
            get
            {
                lock (sync)
                    return [.. Seen];
            }
        }

        public void Start() => loop = Task.Run(ServeAsync);

        public void Dispose()
        {
            stop.Cancel();

            try
            {
                loop?.Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // 退出时的取消
            }
        }

        private async Task ServeAsync()
        {
            while (!stop.IsCancellationRequested)
            {
                await using var server = new NamedPipeServerStream
                (
                    MiniModuleClient.PipeName(pid),
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Message,
                    PipeOptions.Asynchronous
                );

                try
                {
                    await server.WaitForConnectionAsync(stop.Token);

                    var buffer = new byte[4096];

                    while (server.IsConnected && !stop.IsCancellationRequested)
                    {
                        var read = await server.ReadAsync(buffer, stop.Token);

                        if (read <= 0)
                            break;

                        var command  = Encoding.UTF8.GetString(buffer, 0, read);
                        var response = Handle(command);

                        await server.WriteAsync(Encoding.UTF8.GetBytes(response), stop.Token);
                        await server.FlushAsync(stop.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (IOException)
                {
                    // 客户端断开, 接着等下一个
                }
            }
        }

        private string Handle(string raw)
        {
            lock (sync)
            {
                Seen.Enqueue(raw.Trim());

                var parsed = LobbyCommand.Parse(raw);

                switch (parsed.Kind)
                {
                    case LobbyCommandKind.Version:
                        return LobbyReplies.Version(pid, "fake.log");

                    case LobbyCommandKind.LobbyState:
                        return LobbyReplies.LobbyState(Snapshot());

                    case LobbyCommandKind.TitleReady:
                        return LobbyReplies.TitleReady(true);

                    case LobbyCommandKind.SkipMovie:
                        return "OK";

                    case LobbyCommandKind.Login:
                        if (where != LobbyWhere.Title)
                            return LobbyReplies.Login(false);

                        where = LobbyWhere.CharaSelect;
                        return LobbyReplies.Login(true);

                    case LobbyCommandKind.Charas:
                        return LobbyReplies.Charas
                        (
                            new CharaListSnapshot(where, Characters.Count, 0, 0, -1, -1, selected, 0, [.. Characters])
                        );

                    case LobbyCommandKind.FocusChara:
                        return "OK already world=1";

                    case LobbyCommandKind.SelectChara:
                        selected = parsed.ContentId;
                        return $"OK selected index=0 cid={parsed.ContentId}";

                    case LobbyCommandKind.EnterChara:
                        confirming = parsed.ContentId;
                        return $"OK clicked index=0 cid={parsed.ContentId}";

                    case LobbyCommandKind.DialogYes:
                        if (confirming != parsed.ContentId)
                            return LobbyReplies.Dialog(DialogVerdict.OtherCharacter, "SelectYesno");

                        inWorld    = confirming;
                        confirming = 0;
                        where      = LobbyWhere.InGame;
                        return LobbyReplies.Dialog(DialogVerdict.Click, "SelectYesno");

                    case LobbyCommandKind.WhoAmI:
                    {
                        var row = Characters.FirstOrDefault(x => x.ContentId == inWorld);

                        return LobbyReplies.WhoAmI
                        (
                            row == null
                                ? new WhoAmISnapshot(false, 0, 0, 0, "", "", false, false, 0, "")
                                : new WhoAmISnapshot(true, row.ContentId, row.CurrentWorldId, row.HomeWorldId, row.CurrentWorldName, row.HomeWorldName, true, true, row.ContentId, row.Name)
                        );
                    }

                    default:
                        return LobbyReplies.UNKNOWN_COMMAND;
                }
            }
        }

        private LobbySnapshot Snapshot()
        {
            var yesNo = confirming == 0
                            ? DialogSnapshot.None
                            : new DialogSnapshot(true, 1, true, true, $"Log in as {Characters.First(x => x.ContentId == confirming).Name}?");

            return new LobbySnapshot
            (
                where, 0, 0, -1, 0, false, 0, 0, 0, 0, false, 0,
                yesNo, DialogSnapshot.None, DialogSnapshot.None
            );
        }
    }
}

/// <summary>
///     自动进入角色按提示框文字判断的关键词: 国服中文不变, 国际服四种语言都认
/// </summary>
public sealed class CatAutoEnterTextsTests
{
    [Theory]
    [InlineData("取消登录")]
    [InlineData("Do you want to leave the queue?")]
    [InlineData("ログイン処理を中断しますか？")]
    [InlineData("Login-Prozess abbrechen?")]
    [InlineData("Voulez-vous quitter la queue ?")]
    public void CancelLogin_AllLanguages(string text) => Assert.True(CatAutoEnterTexts.IsCancelLoginPrompt(text));

    [Theory]
    [InlineData("要重新编辑角色形象吗")]
    [InlineData("Would you like to re-edit your character?")]
    [InlineData("キャラクターの再編集をしますか")]
    [InlineData("Möchtest du deinen Charakter umzugestalten")]
    [InlineData("Voulez-vous modifier votre personnage ?")]
    public void Fantasia_AllLanguages(string text) => Assert.True(CatAutoEnterTexts.IsFantasiaPrompt(text));

    [Theory]
    [InlineData("上次该角色退出时系统出现异常，或正在通过其他客户端登录，已强制令该角色下线。")]
    [InlineData("This character is logged in on another client. Logging out character from a previous play session.")]
    [InlineData("他のクライアントからログイン中です")]
    [InlineData("Der Charakter ist auf einem anderen Client eingeloggt.")]
    [InlineData("Ce personnage est connecté sur un autre client de jeu.")]
    public void ForcedLogout_AllLanguages(string text) => Assert.True(CatAutoEnterTexts.IsForcedLogoutPrompt(text));

    [Theory]
    [InlineData("服务器拥挤，当前排队人数：17人", 17)]
    [InlineData("The server is busy. Players in queue: 23", 23)]
    [InlineData("（現在31人待ちです）", 31)]
    [InlineData("Platz auf der Warteliste: 5", 5)]
    [InlineData("Joueurs en attente : 8", 8)]
    public void Queue_PromptAndNumber(string text, int number)
    {
        Assert.True(CatAutoEnterTexts.IsQueuePrompt(text));
        Assert.Equal(number, CatAutoEnterTexts.ParseQueueNumber(text));
    }

    [Theory]
    [InlineData("要以 Alice 登录吗？")]
    [InlineData("Log in as Alice?")]
    [InlineData("This World is currently full. Please wait until an opening is available and try again. If you are on the free trial, you cannot log in while there is a queue.")]
    [InlineData("")]
    public void OrdinaryPrompts_AreNotSpecial(string text)
    {
        Assert.False(CatAutoEnterTexts.IsCancelLoginPrompt(text));
        Assert.False(CatAutoEnterTexts.IsFantasiaPrompt(text));
        Assert.False(CatAutoEnterTexts.IsForcedLogoutPrompt(text));
        Assert.False(CatAutoEnterTexts.IsQueuePrompt(text));
    }
}

/// <summary>
///     把 CatLobby 登记成国际服 Dalamud 的开发插件: 幂等、不动别的开发插件（如 Afan）
/// </summary>
public sealed class DalamudDevPluginRegistrarTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "cat-devplugin-" + Guid.NewGuid().ToString("N"));
    private readonly string dll;

    private const string AFAN = @"F:\Projects\Afan\dalamud\Afan\bin\Debug\Afan.dll";

    public DalamudDevPluginRegistrarTests()
    {
        Directory.CreateDirectory(Path.Combine(root, "CatLobby"));
        dll = Path.Combine(root, "CatLobby", "CatLobby.dll");
        File.WriteAllText(dll, "x");
    }

    public void Dispose() => Directory.Delete(root, true);

    private DirectoryInfo Config => new(Path.Combine(root, "config"));

    private string ConfigPath => Path.Combine(root, "config", "dalamudConfig.json");

    private void WriteConfig(params (string Path, bool Enabled)[] entries)
    {
        Directory.CreateDirectory(Config.FullName);

        var values = new JArray(entries.Select(x => new JObject
        {
            ["$type"]     = "Dalamud.Configuration.DevPluginLocationSettings, Dalamud",
            ["Path"]      = x.Path,
            ["IsEnabled"] = x.Enabled,
            ["Nickname"]  = null
        }));

        File.WriteAllText(ConfigPath, new JObject
        {
            ["LastVersion"] = "15.0.3.9",
            ["DevPluginLoadLocations"] = new JObject
            {
                ["$type"]   = "System.Collections.Generic.List`1[[Dalamud.Configuration.DevPluginLocationSettings, Dalamud]], System.Private.CoreLib",
                ["$values"] = values
            }
        }.ToString());
    }

    private JArray Locations() =>
        (JArray)JObject.Parse(File.ReadAllText(ConfigPath))["DevPluginLoadLocations"]!["$values"]!;

    [Fact]
    public async Task AddsOwnEntry_KeepsAfanUntouched()
    {
        WriteConfig((AFAN, true));

        var result = await DalamudDevPluginRegistrar.EnsureAsync(Config, dll);

        Assert.True(result.Ok);
        var locations = Locations();
        Assert.Equal(2, locations.Count);
        Assert.Equal(AFAN, locations[0].Value<string>("Path"));
        Assert.True(locations[0].Value<bool>("IsEnabled"));
        Assert.Equal(dll, locations[1].Value<string>("Path"));
        Assert.True(locations[1].Value<bool>("IsEnabled"));
        Assert.Equal("Dalamud.Configuration.DevPluginLocationSettings, Dalamud", locations[1].Value<string>("$type"));
        Assert.Equal("15.0.3.9", JObject.Parse(File.ReadAllText(ConfigPath)).Value<string>("LastVersion"));
    }

    [Fact]
    public async Task SecondRun_IsIdempotent_AndDoesNotRewriteTheFile()
    {
        WriteConfig((AFAN, true));
        await DalamudDevPluginRegistrar.EnsureAsync(Config, dll);

        var before = File.ReadAllText(ConfigPath);
        var stamp  = File.GetLastWriteTimeUtc(ConfigPath);
        await Task.Delay(30);

        var result = await DalamudDevPluginRegistrar.EnsureAsync(Config, dll);

        Assert.True(result.Ok);
        Assert.Equal(before, File.ReadAllText(ConfigPath));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(ConfigPath));
        Assert.Equal(2, Locations().Count);
    }

    [Fact]
    public async Task DisabledOrMovedEntry_IsCorrected_AndDuplicatesRemoved()
    {
        WriteConfig((AFAN, true), (@"D:\old\CatLobby\CatLobby.dll", false), (@"E:\other\CatLobby.dll", true));

        var result = await DalamudDevPluginRegistrar.EnsureAsync(Config, dll);

        Assert.True(result.Ok);
        var locations = Locations();
        Assert.Equal(2, locations.Count);
        Assert.Equal(AFAN, locations[0].Value<string>("Path"));
        Assert.Equal(dll, locations[1].Value<string>("Path"));
        Assert.True(locations[1].Value<bool>("IsEnabled"));
    }

    [Fact]
    public async Task NoConfigFile_CreatesOne()
    {
        var result = await DalamudDevPluginRegistrar.EnsureAsync(Config, dll);

        Assert.True(result.Ok);
        Assert.Equal(dll, Assert.Single(Locations()).Value<string>("Path"));
    }

    private JObject ReadConfig() => JObject.Parse(File.ReadAllText(ConfigPath));

    private static JObject ProfileEntry(JObject config, string name) =>
        Assert.Single(((JArray)config["DefaultProfile"]!["Plugins"]!["$values"]!).OfType<JObject>(), x => x.Value<string>("InternalName") == name);

    [Fact]
    public async Task NewEntry_StartsOnBoot_AndIsEnabledInDefaultProfileUnderTheSameId()
    {
        WriteConfig((AFAN, true));

        await DalamudDevPluginRegistrar.EnsureAsync(Config, dll);

        var config   = ReadConfig();
        var settings = (JObject)config["DevPluginSettings"]![dll]!;
        var id       = settings.Value<string>("WorkingPluginId");

        Assert.True(settings.Value<bool>("StartOnBoot"));
        Assert.False(settings.Value<bool>("AutomaticReloading"));
        Assert.True(Guid.TryParse(id, out var guid) && guid != Guid.Empty);

        var entry = ProfileEntry(config, "CatLobby");
        Assert.Equal(id, entry.Value<string>("WorkingPluginId"));
        Assert.True(entry.Value<bool>("IsEnabled"));
    }

    [Fact]
    public async Task MovedPlugin_KeepsItsId_DropsOldSettings_AndReenablesInProfile()
    {
        const string OLD = @"D:\old\CatLobby\CatLobby.dll";
        const string ID  = "11111111-2222-3333-4444-555555555555";

        WriteConfig((AFAN, true), (OLD, true));

        var config = ReadConfig();
        config["DevMode"] = false;
        config["DevPluginSettings"] = new JObject
        {
            [AFAN] = new JObject { ["StartOnBoot"] = true, ["WorkingPluginId"] = "772e8f7c-1486-46f3-b43e-87f7f98f30c5" },
            [OLD]  = new JObject { ["StartOnBoot"] = false, ["WorkingPluginId"] = ID },
        };
        config["DefaultProfile"] = new JObject
        {
            ["Plugins"] = new JObject
            {
                ["$values"] = new JArray
                {
                    new JObject { ["InternalName"] = "Afan", ["WorkingPluginId"] = "772e8f7c-1486-46f3-b43e-87f7f98f30c5", ["IsEnabled"] = true },
                    new JObject { ["InternalName"] = "CatLobby", ["WorkingPluginId"] = ID, ["IsEnabled"] = false },
                    new JObject { ["InternalName"] = "CatLobby", ["WorkingPluginId"] = "99999999-2222-3333-4444-555555555555", ["IsEnabled"] = false },
                }
            }
        };
        File.WriteAllText(ConfigPath, config.ToString());

        await DalamudDevPluginRegistrar.EnsureAsync(Config, dll);

        var after    = ReadConfig();
        var settings = (JObject)after["DevPluginSettings"]!;

        Assert.True(after.Value<bool>("DevMode"));
        Assert.Null(settings[OLD]);
        Assert.NotNull(settings[AFAN]);
        Assert.True(settings[dll]!.Value<bool>("StartOnBoot"));
        Assert.Equal(ID, settings[dll]!.Value<string>("WorkingPluginId"));

        var entry = ProfileEntry(after, "CatLobby");
        Assert.Equal(ID, entry.Value<string>("WorkingPluginId"));
        Assert.True(entry.Value<bool>("IsEnabled"));
        Assert.True(ProfileEntry(after, "Afan").Value<bool>("IsEnabled"));
    }

    [Fact]
    public async Task PluginFileMissing_FailsWithoutTouchingConfig()
    {
        WriteConfig((AFAN, true));
        var before = File.ReadAllText(ConfigPath);

        var result = await DalamudDevPluginRegistrar.EnsureAsync(Config, Path.Combine(root, "nowhere", "CatLobby.dll"));

        Assert.False(result.Ok);
        Assert.Equal(before, File.ReadAllText(ConfigPath));
    }

    [Fact]
    public async Task BrokenConfig_FailsWithReason_AndIsLeftAlone()
    {
        Directory.CreateDirectory(Config.FullName);
        File.WriteAllText(ConfigPath, "{ not json");

        var result = await DalamudDevPluginRegistrar.EnsureAsync(Config, dll);

        Assert.False(result.Ok);
        Assert.NotNull(result.Error);
        Assert.Equal("{ not json", File.ReadAllText(ConfigPath));
    }
}
