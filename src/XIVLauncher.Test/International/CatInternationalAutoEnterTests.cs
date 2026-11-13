using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using XIVLauncher.CatHost;
using XIVLauncher.InGame;
using XIVLauncher.Test.CatHost;
using Xunit;

namespace XIVLauncher.Test.International;

/// <summary>
///     国际服自动进入角色: 假的游戏内模块开同名管道（回应与小模块逐字一致），编排与国际服游戏一侧原样跑通
/// </summary>
public sealed class CatInternationalAutoEnterTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly Process       placeholder;
    private readonly FakeModule    module;
    private readonly RecordingReporter reporter = new();

    public CatInternationalAutoEnterTests()
    {
        placeholder = StartPlaceholder();
        module      = new FakeModule(placeholder.Id);
    }

    public void Dispose()
    {
        module.Dispose();

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
        module.Start();

        using var game = new CatInternationalAutoEnter(placeholder);

        Assert.StartsWith("OK version=", await game.SendAsync("VERSION", CancellationToken.None));
        Assert.True(InGameTravelService.IsGateHeld(placeholder.Id));

        game.Release();
        Assert.False(InGameTravelService.IsGateHeld(placeholder.Id));
    }

    #endregion

    #region 编排对着假模块

    [Fact]
    public async Task SameNameOnTwoWorlds_EnglishHomeWorldPicksTheRightOne_AndLevelReadsStopQuietly()
    {
        module.Characters.Add(new CharaRow(1001, 0, 0, 23, 23, "Alice Fox", "Asura", "Asura", 0));
        module.Characters.Add(new CharaRow(1002, 1, 0, 54, 54, "Alice Fox", "Tonberry", "Tonberry", 0));
        module.Start();

        using var game = new CatInternationalAutoEnter(placeholder);
        var flow       = new CatAutoEnter(game, reporter, new CatAutoEnterTarget("Alice Fox", "tonberry"), FastTimings());

        Assert.Equal(CatAutoEnterOutcome.InWorld, await flow.RunAsync(CancellationToken.None).WaitAsync(Timeout));

        Assert.Equal("1002", flow.EnteredContentId);
        Assert.Contains("DIALOG YES 1002", module.Commands);
        Assert.Contains("character:Alice Fox@Tonberry", reporter.Entries);
        Assert.Equal(CatStages.IN_WORLD, flow.CurrentStage);
        Assert.DoesNotContain(reporter.Entries, x => x.StartsWith("stopped:", StringComparison.Ordinal));

        // 编排结束后的看当前角色: 模块不认的命令回 unknown-command, 读取器按「不支持」停读, 不停手也不刷屏
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        try
        {
            await flow.ObserveAsync(cancel.Token);
        }
        catch (OperationCanceledException)
        {
            // 到时取消
        }

        Assert.Equal(1, module.Commands.Count(x => x == "LEVELS"));
        Assert.Equal(1, module.Commands.Count(x => x == "DUTY"));
        Assert.DoesNotContain(reporter.Entries, x => x.StartsWith("stopped:", StringComparison.Ordinal));
        Assert.Empty(reporter.LevelReports);
    }

    [Fact]
    public async Task SingleCharacter_NoName_Enters_AndKeepsEnglishWorldCodes()
    {
        module.Characters.Add(new CharaRow(2001, 0, 0, 23, 23, "Bob Wolf", "Asura", "Asura", 0));
        module.Start();

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
        module.Start();

        using var game = new CatInternationalAutoEnter(placeholder);
        var flow       = new CatAutoEnter(game, reporter, new CatAutoEnterTarget("Nobody Here", "Asura"), FastTimings());

        Assert.Equal(CatAutoEnterOutcome.Stopped, await flow.RunAsync(CancellationToken.None).WaitAsync(Timeout));

        Assert.Contains($"stopped:{CatAutoEnterStopCodes.NO_CHARACTER}", reporter.Entries);
        Assert.DoesNotContain("ENTERCHARA", string.Join('|', module.Commands));
    }

    [Fact]
    public async Task ModulePipeAppearsLate_HandshakeKeepsRetrying()
    {
        module.Characters.Add(new CharaRow(3001, 0, 0, 23, 23, "Cara Owl", "Asura", "Asura", 0));

        using var game = new CatInternationalAutoEnter(placeholder);
        var flow       = new CatAutoEnter(game, reporter, new CatAutoEnterTarget("Cara Owl", "Asura"), FastTimings());
        var run        = flow.RunAsync(CancellationToken.None);

        await Task.Delay(300);
        Assert.False(run.IsCompleted);

        module.Start();

        Assert.Equal(CatAutoEnterOutcome.InWorld, await run.WaitAsync(Timeout));
    }

    #endregion

    /// <summary>
    ///     假的游戏内模块: 在 <c>minilauncher-&lt;pid&gt;</c> 上按小模块的回应格式拼装应答, 标题 → 选角 → 点登录确认框 → 进世界
    /// </summary>
    internal sealed class FakeModule : IDisposable
    {
        private int pid;

        public FakeModule()
        {
        }

        public FakeModule(int pid) => this.pid = pid;

        /// <summary>管道名按游戏进程号; 进程号在游戏起来后才知道时先建后绑</summary>
        public void Bind(int gamePid) => pid = gamePid;

        private readonly CancellationTokenSource stop = new();
        private readonly object                  sync = new();

        private string where = "title";
        private ulong  selected;
        private ulong  confirming;
        private ulong  inWorld;
        private Task?  loop;

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

        private static ulong ContentIdOf(string command, string prefix) =>
            ulong.TryParse(command[prefix.Length..].Trim(), out var value) ? value : 0UL;

        private string Handle(string raw)
        {
            lock (sync)
            {
                Seen.Enqueue(raw.Trim());

                var command = raw.Trim('\0', ' ', '\t', '\r', '\n');

                switch (command)
                {
                    case "VERSION":
                        return $"OK version=0.11.0 pid={pid} mode=readonly log=fake.log";

                    case "LOBBYSTATE":
                        return LobbyState();

                    case "TITLEREADY":
                        return "OK ready=1";

                    case "SKIPMOVIE":
                        return "OK";

                    case "LOGIN":
                        if (where != "title")
                            return "FAIL no-title-menu";

                        where = "charaselect";
                        return "OK";

                    case "CHARAS":
                        return Charas();

                    case "WHOAMI":
                        return WhoAmI();
                }

                if (command.StartsWith("FOCUSCHARA ", StringComparison.Ordinal))
                    return "OK already world=1";

                if (command.StartsWith("SELECTCHARA ", StringComparison.Ordinal))
                {
                    selected = ContentIdOf(command, "SELECTCHARA ");
                    return $"OK selected index=0 cid={selected}";
                }

                if (command.StartsWith("ENTERCHARA ", StringComparison.Ordinal))
                {
                    confirming = ContentIdOf(command, "ENTERCHARA ");
                    return $"OK clicked index=0 cid={confirming}";
                }

                if (command.StartsWith("DIALOG YES ", StringComparison.Ordinal))
                {
                    if (confirming != ContentIdOf(command, "DIALOG YES "))
                        return "FAIL other-character";

                    inWorld    = confirming;
                    confirming = 0;
                    where      = "ingame";
                    return "OK clicked addon=SelectYesno";
                }

                return "FAIL unknown-command";
            }
        }

        private string LobbyState()
        {
            var yesNo = confirming != 0;
            var text  = yesNo ? $"Log in as {Characters.First(x => x.ContentId == confirming).Name}?" : "";

            var reply = $"OK where={where} world=0 worldIndex=0 selectedIndex=-1 hovered=0 locked=0 stage=0 uiStage=0 queue=0 dialogId=0 " +
                        $"yesno={(yesNo ? 1 : 0)} ok=0 dialogue=0 loading=0 listWorld=0 queuePrompt=0";

            return yesNo ? $"{reply}\nD\tSelectYesno\t1\t1\t1\t{text}\nT {text}" : reply;
        }

        private string Charas()
        {
            var header = $"OK where={where} n={Characters.Count} total={Characters.Count} selected={selected} selectedIndex=-1 " +
                         "hovered=0 hoveredIndex=-1 source=dc skipped=0 invalid=0";

            var body = new StringBuilder();

            foreach (var row in Characters)
            {
                body.Append("\nC\t").Append(row.ContentId).Append('\t').Append(row.Index).Append('\t').Append(row.LoginFlags)
                    .Append('\t').Append(row.CurrentWorldId).Append('\t').Append(row.HomeWorldId)
                    .Append('\t').Append(row.Name).Append('\t').Append(row.CurrentWorldName).Append('\t').Append(row.HomeWorldName)
                    .Append('\t').Append(row.ClickFlags);
            }

            return header + body;
        }

        private string WhoAmI()
        {
            var row = Characters.FirstOrDefault(x => x.ContentId == inWorld);

            return row == null
                       ? "OK loaded=0 cid=0 world=0 home=0 worldName= homeName= loggedIn=0 inZone=0 lobbyCid=0 name="
                       : $"OK loaded=1 cid={row.ContentId} world={row.CurrentWorldId} home={row.HomeWorldId} worldName={row.CurrentWorldName} " +
                         $"homeName={row.HomeWorldName} loggedIn=1 inZone=1 lobbyCid={row.ContentId} name={row.Name}";
        }
    }
}

/// <summary>选角列表里的一个角色（世界名是英文名, 如 Asura）</summary>
internal sealed record CharaRow
(
    ulong  ContentId,
    int    Index,
    byte   LoginFlags,
    ushort CurrentWorldId,
    ushort HomeWorldId,
    string Name,
    string CurrentWorldName,
    string HomeWorldName,
    uint   ClickFlags
);

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
