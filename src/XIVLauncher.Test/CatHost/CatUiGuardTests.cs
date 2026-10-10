using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using XIVLauncher.Account.Cred;
using XIVLauncher.CatHost;
using XIVLauncher.InGame;
using XIVLauncher.Minion;
using Xunit;

namespace XIVLauncher.Test.CatHost;

/// <summary>
///     界面版交接通道: 守护期间有记录、守护锁和描述文件; handoff 全有或全无, 接受后改写记录并以 EXIT_HANDED_OFF 退出,
///     拒绝时一切照旧（跨区占位放掉、不退出）
/// </summary>
[Collection(nameof(GameRecordsTests))]
public sealed class CatUiGuardTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private const string CARD = "0123456789abcdef";

    private readonly string originalDirectory = GameRecords.Directory;
    private readonly string tempDirectory     = Path.Combine(Path.GetTempPath(), "dml-ui-guard-" + Guid.NewGuid().ToString("N"));
    private readonly List<Process> processes  = [];
    private readonly List<CatUiGuard> guards  = [];
    private readonly List<int> travelPids     = [];

    private readonly TaskCompletionSource<int> exitCode = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private string tgt = "tgt-1";

    public CatUiGuardTests() => GameRecords.Directory = Path.Combine(tempDirectory, "games");

    private string DescriptorFolder => Path.Combine(tempDirectory, "cat-guard");

    public void Dispose()
    {
        foreach (var guard in guards)
        {
            foreach (var process in processes)
                guard.Leave(process.Id);
        }

        foreach (var pid in travelPids)
        {
            InGameTravelJobs.End(pid, false, "测试结束");
            InGameTravelJobs.Release(pid);
        }

        foreach (var process in processes)
        {
            try
            {
                process.Kill();
            }
            catch
            {
                // 已退出
            }

            process.Dispose();
        }

        GameRecords.Directory = originalDirectory;

        try
        {
            if (Directory.Exists(tempDirectory))
                Directory.Delete(tempDirectory, true);
        }
        catch (IOException)
        {
            // 交接后的实例还拿着守护锁; 临时目录, 不影响结果
        }
    }

    private CatUiGuard NewGuard(CredType credType = CredType.WindowsCredManager)
    {
        var guard = new CatUiGuard
        (
            () => DescriptorFolder,
            text => Task.FromResult(string.IsNullOrEmpty(text) ? null : "enc:" + text),
            () => credType,
            code => exitCode.TrySetResult(code)
        );

        guards.Add(guard);
        return guard;
    }

    private CatUiGuardGame NewGame(Process process, string? sndaId = "123456", bool travelListening = true) =>
        new()
        {
            Process = process,
            Record = new GameRecord
            {
                Pid                       = process.Id,
                ProcessStartedAt          = MinionOccupancy.GetProcessStartedAt(process),
                Channel                   = GameRecordChannels.SDO,
                AccountName               = "acc",
                AccountUserName           = "acc",
                AreaName                  = "陆行鸟",
                Dalamud                   = true,
                DalamudRequested          = true,
                DcTravelPort              = 51234,
                SndaId                    = sndaId,
                MinionFingerprint         = CARD,
                MinionVariant             = MinionCards.VARIANT_CN,
                CrashDialogTimeoutSeconds = CatProtocol.DEFAULT_CRASH_DIALOG_TIMEOUT_SECONDS,
                GuardPid                  = 0,
                GuardStartedAt            = DateTimeOffset.UnixEpoch,
                UpdatedAt                 = DateTimeOffset.UnixEpoch
            },
            Credentials     = () => (tgt, "guid-1"),
            TravelSession   = () => "session-1",
            TravelListening = () => travelListening
        };

    private Process StartSleeper()
    {
        var startInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "PING.EXE"))
        {
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardOutput = true
        };
        startInfo.ArgumentList.Add("-n");
        startInfo.ArgumentList.Add("60");
        startInfo.ArgumentList.Add("127.0.0.1");

        var process = Process.Start(startInfo)!;
        processes.Add(process);
        return process;
    }

    private static CatUiGuardDescriptor ReadDescriptor(CatUiGuard guard)
    {
        Assert.NotNull(guard.DescriptorPath);
        return JsonSerializer.Deserialize<CatUiGuardDescriptor>(File.ReadAllText(guard.DescriptorPath!), CatProtocol.JsonOptions)!;
    }

    private static async Task<CatTestClient> ConnectAndHelloAsync(CatUiGuard guard)
    {
        var descriptor = ReadDescriptor(guard);
        var client     = await CatTestClient.ConnectAsync(descriptor.PipeName, Timeout);
        var hello      = await client.RequestAsync("hello", new { token = descriptor.Token });

        Assert.Equal(CatProtocol.PROTOCOL_VERSION, hello["result"]!["protocolVersion"]!.GetValue<string>());
        return client;
    }

    private static async Task<JsonNode> HandOffAsync(CatUiGuard guard)
    {
        await using var client = await ConnectAndHelloAsync(guard);
        return (await client.RequestAsync(CatHandOff.METHOD, new { }))["result"]!;
    }

    [Fact]
    public async Task Enter_WritesRecordAndDescriptor_Leave_RemovesBoth()
    {
        var game  = StartSleeper();
        var guard = NewGuard();

        await guard.EnterAsync(NewGame(game));

        var record = GameRecords.Read(game.Id);
        Assert.NotNull(record);
        Assert.Null(record.OperationId);
        Assert.Equal("acc", record.AccountUserName);
        Assert.Equal("enc:tgt-1", record.Tgt);
        Assert.Equal("enc:guid-1", record.Guid);
        Assert.Null(record.DcTravelSession);
        Assert.Equal(Environment.ProcessId, record.GuardPid);
        Assert.Equal(CARD, record.MinionFingerprint);
        Assert.True(GameRecords.IsGuarded(game.Id, record.ProcessStartedAt));

        var path = guard.DescriptorPath!;
        Assert.Equal(Path.Combine(DescriptorFolder, $"{Environment.ProcessId}.json"), path);

        var json = File.ReadAllText(path);
        Assert.Contains("\"pipeName\":\"dml-ui-guard-", json);
        Assert.Contains("\"token\":", json);
        Assert.Contains($"\"launcherPid\":{Environment.ProcessId}", json);
        Assert.Contains("\"launcherStartedAt\":", json);

        var descriptor = ReadDescriptor(guard);
        Assert.True(CatProtocol.IsValidUiGuardPipeName(descriptor.PipeName));
        Assert.Equal(64, descriptor.Token.Length);

        Assert.True(guard.Leave(game.Id));

        Assert.Null(GameRecords.Read(game.Id));
        Assert.False(GameRecords.IsGuarded(game.Id, record.ProcessStartedAt));
        Assert.False(File.Exists(path));
        Assert.Null(guard.DescriptorPath);
    }

    [Fact]
    public async Task Enter_RemovesDescriptorsOfLaunchersThatAreGone()
    {
        var game  = StartSleeper();
        var other = StartSleeper();
        Directory.CreateDirectory(DescriptorFolder);

        var stale = Path.Combine(DescriptorFolder, "999999.json");
        var live  = Path.Combine(DescriptorFolder, $"{other.Id}.json");
        var broken = Path.Combine(DescriptorFolder, "888888.json");

        File.WriteAllText(stale, JsonSerializer.Serialize(new CatUiGuardDescriptor("dml-ui-guard-" + new string('a', 32), new string('b', 64), 999999, CatProtocol.FormatTimestamp(DateTimeOffset.UtcNow.AddHours(-1))), CatProtocol.JsonOptions));
        File.WriteAllText(live, JsonSerializer.Serialize(new CatUiGuardDescriptor("dml-ui-guard-" + new string('c', 32), new string('d', 64), other.Id, CatProtocol.FormatTimestamp(MinionOccupancy.GetProcessStartedAt(other))), CatProtocol.JsonOptions));
        File.WriteAllText(broken, "{ 不是 JSON");

        await NewGuard().EnterAsync(NewGame(game));

        Assert.False(File.Exists(stale));
        Assert.False(File.Exists(broken));
        Assert.True(File.Exists(live));
    }

    [Fact]
    public async Task HandOff_Accepted_RewritesRecords_RepliesGames_ThenExitsWithoutCleanup()
    {
        var game  = StartSleeper();
        var guard = NewGuard();
        await guard.EnterAsync(NewGame(game));
        var path = guard.DescriptorPath!;

        // 登录后续期过的凭证: 交接时取最新的
        tgt = "tgt-2";

        JsonNode result;

        await using (var client = await ConnectAndHelloAsync(guard))
        {
            result = (await client.RequestAsync(CatHandOff.METHOD, new { }))["result"]!;
            Assert.Equal(CatHostRuntime.EXIT_HANDED_OFF, await exitCode.Task.WaitAsync(Timeout));
        }

        travelPids.Add(game.Id);

        Assert.True(result["accepted"]!.GetValue<bool>());
        var handed = Assert.Single(result["games"]!.AsArray());
        Assert.Equal(game.Id, handed!["pid"]!.GetValue<int>());
        Assert.Equal(CatProtocol.FormatTimestamp(MinionOccupancy.GetProcessStartedAt(game)), handed["processStartedAt"]!.GetValue<string>());
        Assert.Equal("acc", handed["accountName"]!.GetValue<string>());
        Assert.Equal(CARD, handed["minionFingerprint"]!.GetValue<string>());

        var record = GameRecords.Read(game.Id);
        Assert.NotNull(record);
        Assert.Null(record.OperationId);
        Assert.Equal("enc:tgt-2", record.Tgt);
        Assert.Equal("enc:guid-1", record.Guid);
        Assert.Equal("enc:session-1", record.DcTravelSession);

        Assert.False(File.Exists(path));
        Assert.True(guard.HandedOff);

        // 交接后: 游戏退出也不做收尾（记录、守护锁留到进程退出）, 也不再接新的游戏内跨区
        Assert.False(guard.Leave(game.Id));
        Assert.NotNull(GameRecords.Read(game.Id));
        Assert.True(GameRecords.IsGuarded(game.Id, record.ProcessStartedAt));
        Assert.Null(InGameTravelJobs.TryBegin(game.Id, "陆行鸟"));
    }

    [Fact]
    public async Task HandOff_WhileAGameIsStarting_IsBusy()
    {
        var game  = StartSleeper();
        var guard = NewGuard();
        await guard.EnterAsync(NewGame(game));

        using (guard.BeginStarting())
        {
            var result = await HandOffAsync(guard);

            Assert.False(result["accepted"]!.GetValue<bool>());
            Assert.Equal(CatCodes.BUSY, result["code"]!.GetValue<string>());
        }

        Assert.False(exitCode.Task.IsCompleted);
        Assert.False(guard.HandedOff);
        Assert.NotNull(InGameTravelJobs.TryBegin(game.Id, "陆行鸟"));
        travelPids.Add(game.Id);
    }

    [Fact]
    public async Task HandOff_AfterBusy_CanBeRetriedOnTheSamePipe()
    {
        var game  = StartSleeper();
        var guard = NewGuard();
        await guard.EnterAsync(NewGame(game));
        var path = guard.DescriptorPath!;

        using (guard.BeginStarting())
            Assert.Equal(CatCodes.BUSY, (await HandOffAsync(guard))["code"]!.GetValue<string>());

        Assert.Equal(path, guard.DescriptorPath);

        var result = await HandOffAsync(guard);
        travelPids.Add(game.Id);

        Assert.True(result["accepted"]!.GetValue<bool>());
        Assert.Equal(CatHostRuntime.EXIT_HANDED_OFF, await exitCode.Task.WaitAsync(Timeout));
    }

    [Fact]
    public async Task CrashRestart_KeepsTheChannel_AndOnlyTheNewGameIsHandedOff()
    {
        var old   = StartSleeper();
        var guard = NewGuard();
        await guard.EnterAsync(NewGame(old));
        var path = guard.DescriptorPath!;

        // 重开回调: 先「正在启动」, 再让旧游戏结束守护
        var restarting = guard.BeginStarting();
        Assert.True(guard.Leave(old.Id));
        Assert.Null(GameRecords.Read(old.Id));
        Assert.Equal(path, guard.DescriptorPath);
        Assert.Equal(CatCodes.BUSY, (await HandOffAsync(guard))["code"]!.GetValue<string>());

        // 新一层第一句就「正在启动」, 回调拿到任务后结束它自己的
        var starting = guard.BeginStarting();
        restarting.Dispose();
        Assert.Equal(path, guard.DescriptorPath);

        var restarted = StartSleeper();
        await guard.EnterAsync(NewGame(restarted));
        starting.Dispose();
        starting.Dispose();

        var result = await HandOffAsync(guard);
        travelPids.Add(restarted.Id);

        Assert.True(result["accepted"]!.GetValue<bool>());
        var handed = Assert.Single(result["games"]!.AsArray());
        Assert.Equal(restarted.Id, handed!["pid"]!.GetValue<int>());
        Assert.Equal(CatHostRuntime.EXIT_HANDED_OFF, await exitCode.Task.WaitAsync(Timeout));
    }

    [Fact]
    public async Task HandOff_AfterTheGameExited_IsBusy()
    {
        var game  = StartSleeper();
        var guard = NewGuard();
        await guard.EnterAsync(NewGame(game));

        // 崩溃对话框开着 / 正在重开: 游戏已退出, 本进程还没让它结束守护
        game.Kill();
        await game.WaitForExitAsync().WaitAsync(Timeout);

        var result = await HandOffAsync(guard);

        Assert.Equal(CatCodes.BUSY, result["code"]!.GetValue<string>());
        Assert.False(exitCode.Task.IsCompleted);
        Assert.True(guard.Leave(game.Id));
    }

    [Fact]
    public async Task HandOff_WhileTravelling_IsBusy_AndReleasesEveryHold()
    {
        var first  = StartSleeper();
        var second = StartSleeper();
        var guard  = NewGuard();
        await guard.EnterAsync(NewGame(first));
        await guard.EnterAsync(NewGame(second));

        Assert.NotNull(InGameTravelJobs.TryBegin(second.Id, "猫小胖"));
        travelPids.Add(second.Id);

        var result = await HandOffAsync(guard);

        Assert.Equal(CatCodes.BUSY, result["code"]!.GetValue<string>());
        Assert.False(exitCode.Task.IsCompleted);

        // 另一个游戏照常能换大区
        Assert.NotNull(InGameTravelJobs.TryBegin(first.Id, "陆行鸟"));
        travelPids.Add(first.Id);

        // 记录没被改写
        Assert.Null(GameRecords.Read(first.Id)!.DcTravelSession);
    }

    [Fact]
    public async Task HandOff_UnencryptedAccountsWithTravelOpen_IsUnsupported()
    {
        var game  = StartSleeper();
        var guard = NewGuard(CredType.NoEncryption);
        await guard.EnterAsync(NewGame(game));

        var result = await HandOffAsync(guard);

        Assert.Equal(CatCodes.UNSUPPORTED, result["code"]!.GetValue<string>());
        Assert.False(exitCode.Task.IsCompleted);
    }

    [Fact]
    public async Task HandOff_UnencryptedAccountsWithoutTravel_IsAccepted()
    {
        var game  = StartSleeper();
        var guard = NewGuard(CredType.NoEncryption);
        await guard.EnterAsync(NewGame(game, travelListening: false));

        var result = await HandOffAsync(guard);
        travelPids.Add(game.Id);

        Assert.True(result["accepted"]!.GetValue<bool>());
        Assert.Equal(CatHostRuntime.EXIT_HANDED_OFF, await exitCode.Task.WaitAsync(Timeout));
    }

    [Fact]
    public async Task HandOff_WindowsHelloAccounts_IsUnsupported()
    {
        var game  = StartSleeper();
        var guard = NewGuard(CredType.WindowsHello);
        await guard.EnterAsync(NewGame(game, travelListening: false));

        var result = await HandOffAsync(guard);

        Assert.Equal(CatCodes.UNSUPPORTED, result["code"]!.GetValue<string>());
        Assert.False(exitCode.Task.IsCompleted);
    }

    [Fact]
    public async Task HandOff_WithoutAccountInfo_IsGameNotFound()
    {
        var game  = StartSleeper();
        var guard = NewGuard();
        await guard.EnterAsync(NewGame(game, null));

        Assert.Null(GameRecords.Read(game.Id));

        var result = await HandOffAsync(guard);

        Assert.Equal(CatCodes.GAME_NOT_FOUND, result["code"]!.GetValue<string>());
        Assert.False(exitCode.Task.IsCompleted);
    }

    [Fact]
    public async Task OtherMethods_AreMethodNotFound()
    {
        var game  = StartSleeper();
        var guard = NewGuard();
        await guard.EnterAsync(NewGame(game));

        await using var client = await ConnectAndHelloAsync(guard);
        var response = await client.RequestAsync("close", new { });

        Assert.Equal(CatRpcException.METHOD_NOT_FOUND, response["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task WrongToken_ClosesConnection()
    {
        var game  = StartSleeper();
        var guard = NewGuard();
        await guard.EnterAsync(NewGame(game));

        await using var client = await CatTestClient.ConnectAsync(ReadDescriptor(guard).PipeName, Timeout);
        await client.WriteLineAsync("""{"jsonrpc":"2.0","id":1,"method":"hello","params":{"token":"wrong-token-wrong-token"}}""");

        await client.Closed.Task.WaitAsync(Timeout);
        Assert.False(exitCode.Task.IsCompleted);
    }
}
