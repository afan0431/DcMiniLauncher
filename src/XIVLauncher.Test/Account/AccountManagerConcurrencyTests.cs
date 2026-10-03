using System.IO;
using System.Text;
using System.Text.Json;
using XIVLauncher.Account;
using XIVLauncher.Account.DeviceProfiles;
using XIVLauncher.Common.Game;
using Xunit;

namespace XIVLauncher.Test.Account;

/// <summary>
///     多个账号管理器实例（模拟多个进程）共用同一账号库与设备预设文件时的读写行为
/// </summary>
public sealed class AccountManagerConcurrencyTests : IDisposable
{
    private const string PRESET_FILE_NAME = "deviceProfilePresets.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string roamingPath = Path.Combine(Path.GetTempPath(), "dml-account-tests", Guid.NewGuid().ToString("N"));

    public AccountManagerConcurrencyTests()
    {
        Directory.CreateDirectory(roamingPath);
        WritePresetStore(CreatePreset("seed"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(roamingPath, true);
        }
        catch (Exception)
        {
            // 数据库连接由终结器释放，目录可能暂时删不掉
        }
    }

    [Fact]
    public void TwoInstances_EditingDifferentRows_DoNotOverwriteEachOther()
    {
        var first = CreateManager();
        first.AddAccount(CreateAccount("alpha"));
        first.AddAccount(CreateAccount("beta"));

        var second = CreateManager();

        Find(first, "alpha").SdoQuickLoginSecret = "alpha-from-first";
        first.Save();

        Find(second, "beta").SdoQuickLoginSecret = "beta-from-second";
        second.Save();

        var reloaded = CreateManager();
        Assert.Equal("alpha-from-first", Find(reloaded, "alpha").SdoQuickLoginSecret);
        Assert.Equal("beta-from-second", Find(reloaded, "beta").SdoQuickLoginSecret);
    }

    [Fact]
    public void SaveAfterAnotherInstanceSaved_DoesNotRollBackItsChanges()
    {
        var first = CreateManager();
        first.AddAccount(CreateAccount("alpha"));
        first.AddAccount(CreateAccount("beta"));
        Find(first, "alpha").SdoQuickLoginSecret = "old-secret";
        first.Save();

        var second = CreateManager();

        var secondPreset = second.CreateDeviceProfilePreset(CreatePreset("second").ToSnapshot(), DateTimeOffset.UtcNow.UtcTicks, null);

        var secondAlpha = Find(second, "alpha");
        secondAlpha.SdoQuickLoginSecret   = "new-secret";
        secondAlpha.DeviceProfilePresetId = secondPreset.Id;
        second.Save(secondAlpha);

        // 第一个实例保存其它行，再改同一行的其它列
        Find(first, "beta").AreaName = "beta-area";
        first.Save();

        var firstAlpha = Find(first, "alpha");
        firstAlpha.AreaName = "alpha-area";
        first.Save(firstAlpha);

        var reloaded      = CreateManager();
        var reloadedAlpha = Find(reloaded, "alpha");
        Assert.Equal("new-secret",         reloadedAlpha.SdoQuickLoginSecret);
        Assert.Equal(secondPreset.Id,      reloadedAlpha.DeviceProfilePresetId);
        Assert.Equal("alpha-area",         reloadedAlpha.AreaName);
        Assert.Equal("beta-area",          Find(reloaded, "beta").AreaName);

        // 保存时内存中的旧值也一并刷新为数据库中的新值
        Assert.Equal("new-secret", firstAlpha.SdoQuickLoginSecret);
    }

    [Fact]
    public void RefreshFromDatabase_PicksUpCredentialWrittenByAnotherInstance()
    {
        var first = CreateManager();
        first.AddAccount(CreateAccount("alpha"));

        var second      = CreateManager();
        var secondAlpha = Find(second, "alpha");
        secondAlpha.SdoQuickLoginSecret = "fresh-secret";
        second.Save(secondAlpha);

        var firstAlpha = Find(first, "alpha");
        Assert.NotEqual("fresh-secret", firstAlpha.SdoQuickLoginSecret);

        first.RefreshFromDatabase(firstAlpha);
        Assert.Equal("fresh-secret", firstAlpha.SdoQuickLoginSecret);
    }

    [Fact]
    public void RemoveAndAdd_OnlyTouchTheirOwnRows()
    {
        var first = CreateManager();
        first.AddAccount(CreateAccount("alpha"));
        first.AddAccount(CreateAccount("beta"));

        var second = CreateManager();

        first.RemoveAccount(Find(first, "beta"));

        var secondAlpha = Find(second, "alpha");
        secondAlpha.SdoQuickLoginSecret = "alpha-secret";
        second.Save(secondAlpha);

        // 第一个实例新增账号，不应把 alpha 改回旧值
        first.AddAccount(CreateAccount("gamma"));

        // 第二个实例仍持有已删除的 beta，整体保存不应把它重新插入
        second.Save();

        var reloaded = CreateManager();
        Assert.Equal(["alpha", "gamma"], reloaded.Accounts.Select(account => account.UserName).Order().ToArray());
        Assert.Equal("alpha-secret", Find(reloaded, "alpha").SdoQuickLoginSecret);
    }

    [Fact]
    public void PresetCreatedByAnotherInstance_IsKeptWhenThisInstanceWritesPresets()
    {
        var first  = CreateManager();
        var second = CreateManager();
        _ = first.GetDeviceProfilePresets();

        var fromSecond = second.CreateDeviceProfilePreset(CreatePreset("second").ToSnapshot(), DateTimeOffset.UtcNow.UtcTicks, "second");
        var fromFirst  = first.CreateDeviceProfilePreset(CreatePreset("first").ToSnapshot(),   DateTimeOffset.UtcNow.UtcTicks, "first");

        var stored = ReadPresetStore();
        Assert.Contains(stored.Presets, preset => preset.Id == fromSecond.Id);
        Assert.Contains(stored.Presets, preset => preset.Id == fromFirst.Id);
    }

    [Fact]
    public void PresetWrites_AreAtomicForConcurrentReaders()
    {
        const int PRESET_COUNT = 30;

        var writer  = CreateManager();
        var reader  = CreateManager();
        var stop    = false;
        var reads   = 0;

        var readerThread = new Thread
        (() =>
            {
                while (!Volatile.Read(ref stop))
                {
                    _ = reader.GetDeviceProfilePresets();
                    reads++;
                }
            }
        );
        readerThread.Start();

        for (var i = 0; i < PRESET_COUNT; i++)
            writer.CreateDeviceProfilePreset(CreatePreset($"atomic-{i}").ToSnapshot(), DateTimeOffset.UtcNow.UtcTicks, null);

        Volatile.Write(ref stop, true);
        readerThread.Join();

        Assert.True(reads > 0);
        Assert.Equal(PRESET_COUNT + 1, ReadPresetStore().Presets.Count);
        Assert.Equal(PRESET_COUNT + 1, reader.GetDeviceProfilePresets().Count);
        Assert.Empty(Directory.GetFiles(roamingPath, $"{PRESET_FILE_NAME}.corrupt-*"));
        Assert.Empty(Directory.GetFiles(roamingPath, $"{PRESET_FILE_NAME}.*.tmp"));
    }

    [Fact]
    public void HalfWrittenPresetFile_IsRetriedInsteadOfRegenerated()
    {
        var manager  = CreateManager();
        var complete = CreatePreset("complete");
        var fullJson = JsonSerializer.Serialize(BuildStore(CreatePreset("seed"), complete), JsonOptions);

        File.WriteAllText(PresetFilePath, fullJson[..(fullJson.Length / 2)], new UTF8Encoding(false));

        var finisher = new Thread
        (() =>
            {
                Thread.Sleep(120);
                var tempPath = $"{PresetFilePath}.finisher";
                File.WriteAllText(tempPath, fullJson, new UTF8Encoding(false));
                File.Move(tempPath, PresetFilePath, true);
            }
        );
        finisher.Start();

        var presets = manager.GetDeviceProfilePresets();
        finisher.Join();

        Assert.Contains(presets, preset => preset.Id == complete.Id);
        Assert.Empty(Directory.GetFiles(roamingPath, $"{PRESET_FILE_NAME}.corrupt-*"));
    }

    [Fact]
    public void DamagedPresetFile_IsBackedUpBeforeRegenerating()
    {
        var manager = CreateManager();
        var legacy  = CreatePreset("legacy");

        File.WriteAllText(PresetFilePath, "{ \"Version\": 1, \"Presets\": [", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(roamingPath, "sharedDeviceProfile.json"), JsonSerializer.Serialize(legacy, JsonOptions), new UTF8Encoding(false));

        var presets = manager.GetDeviceProfilePresets();

        var backup = Assert.Single(Directory.GetFiles(roamingPath, $"{PRESET_FILE_NAME}.corrupt-*"));
        Assert.Equal("{ \"Version\": 1, \"Presets\": [", File.ReadAllText(backup));

        var regenerated = Assert.Single(presets);
        Assert.True(regenerated.Matches(legacy.ToSnapshot()));
    }

    private string PresetFilePath => Path.Combine(roamingPath, PRESET_FILE_NAME);

    private AccountManager CreateManager() =>
        new(new TestAccountSettingsStore(), roamingPath);

    private static XIVAccount Find(AccountManager manager, string userName) =>
        manager.FindAccount(userName, XIVAccountType.Sdo) ?? throw new InvalidOperationException($"找不到账号 {userName}");

    private static XIVAccount CreateAccount(string userName)
    {
        var account = new XIVAccount
        {
            AccountType        = XIVAccountType.Sdo,
            SdoLoginAccount    = userName,
            WeGameLoginAccount = string.Empty,
            UserDefinedName    = string.Empty,
            AreaName           = "initial-area"
        };
        account.GenerateID();
        return account;
    }

    private static DeviceProfilePreset CreatePreset(string name) =>
        new()
        {
            Id                = Guid.NewGuid().ToString("N"),
            Remark            = name,
            DeviceId          = $"device-{name}",
            MacAddress        = $"mac-{name}",
            HostName          = $"host-{name}",
            GeneratedUtcTicks = DateTimeOffset.UtcNow.UtcTicks
        };

    private static DeviceProfilePresetStoreState BuildStore(params DeviceProfilePreset[] presets) =>
        new()
        {
            SharedPresetId = presets[0].Id,
            Presets        = presets
        };

    private void WritePresetStore(params DeviceProfilePreset[] presets) =>
        File.WriteAllText(PresetFilePath, JsonSerializer.Serialize(BuildStore(presets), JsonOptions), new UTF8Encoding(false));

    private DeviceProfilePresetStoreState ReadPresetStore() =>
        JsonSerializer.Deserialize<DeviceProfilePresetStoreState>(File.ReadAllText(PresetFilePath), JsonOptions)
        ?? throw new InvalidOperationException("设备预设文件为空");

    private sealed class TestAccountSettingsStore : IAccountSettingsStore
    {
        public string CurrentAccountID { get; set; } = string.Empty;
    }
}
