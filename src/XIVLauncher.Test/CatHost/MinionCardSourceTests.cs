using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using XIVLauncher.Minion;
using XIVLauncher.Settings;
using XIVLauncher.Test.International;
using XIVLauncher.Windows.ViewModel.Main;
using Xunit;

namespace XIVLauncher.Test.CatHost;

[Collection(SerilogCaptureCollection.NAME)]
public sealed class MinionCardSourceTests : IDisposable
{
    private const string KEYCODE_A = "FFXIVX-FAKE-CARD-AAAA";
    private const string KEYCODE_B = "FFXIVX-FAKE-CARD-BBBB";
    private const string KEYCODE_C = "FFXIVX-FAKE-CARD-CCCC";
    private const string FORUM_ID = "fake-forum-user";
    private const string FORUM_PASSWORD = "fake-forum-pass";
    private const string LOCAL_FORUM_PASSWORD = "fake-local-pass";

    private const string CN_UID_A = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa01";
    private const string GLOBAL_UID_A = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa02";
    private const string CN_UID_B = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbb01";
    private const string GLOBAL_UID_B = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbb02";
    private const string ROW_UID = "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCC01";
    private const string GLOBAL_ROW_UID = "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCC02";

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("cat-minion-cards-v1");

    private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("dml-minion-cards-");

    private string CardsPath => Path.Combine(directory.FullName, "workbench", "minion-cards.bin");

    private string InstallPath => Path.Combine(directory.FullName, "MINI");

    private string CnGameRoot => Path.Combine(directory.FullName, "FF14CN");

    private string GlobalGameRoot => Path.Combine(directory.FullName, "FF14Global");

    private IReadOnlyList<string?> CnRoots => [CnGameRoot, null];

    public void Dispose()
    {
        try
        {
            directory.Delete(true);
        }
        catch
        {
            // ignored
        }
    }

    [Fact]
    public void WorkbenchFile_IsDecryptedAndParsed_CardsSortedByNumber()
    {
        WriteWorkbenchFile(WorkbenchJson());
        WriteAccountsJson();

        var choices = Load();

        Assert.True(choices.FromWorkbench);
        Assert.Equal(MinionCardSourceKind.Workbench, choices.Source);
        Assert.Empty(choices.Groups);

        var file = choices.Workbench!;
        Assert.Equal(FORUM_ID, file.ForumId);
        Assert.Equal(FORUM_PASSWORD, file.ForumPassword.Reveal());
        Assert.Equal(new DateTimeOffset(2026, 10, 10, 1, 2, 3, TimeSpan.Zero), file.UpdatedAt);

        Assert.Equal([3, 7], file.Cards.Select(card => card.Number));
        Assert.Equal(["卡 3", "卡 7"], file.Cards.Select(card => card.DisplayName));

        var card3 = file.Cards[0];
        Assert.Equal(KEYCODE_B, card3.Keycode.Reveal());
        Assert.Equal(MinionCards.Fingerprint(KEYCODE_B), card3.Fingerprint);
        Assert.Equal(CN_UID_B, card3.CnUid);
        Assert.Equal(GLOBAL_UID_B, card3.GlobalUid);

        Assert.StartsWith("来自 Cat 工作台（2 张，更新于 ", choices.SourceText);
    }

    [Theory]
    [InlineData("2026-10-10T09:02:03+08:00")]
    [InlineData("2026-10-10T01:02:03.000Z")]
    public void WorkbenchFile_UpdatedAtIsParsedAsRoundTripTime(string updatedAt)
    {
        WriteWorkbenchFile(new { version = 1, updatedAt, forumId = FORUM_ID, forumPassword = FORUM_PASSWORD, cards = Array.Empty<object>() });

        Assert.Equal(new DateTimeOffset(2026, 10, 10, 1, 2, 3, TimeSpan.Zero), MinionCardSource.TryReadWorkbenchCards(CardsPath)!.UpdatedAt);
    }

    [Fact]
    public void WorkbenchFile_SkipsCardsWithoutKeycodeNumberOrCnUid_AndBlanksInvalidGlobalUid()
    {
        WriteWorkbenchFile
        (
            new
            {
                version = 1,
                forumId = FORUM_ID,
                forumPassword = FORUM_PASSWORD,
                cards = new object[]
                {
                    new { fingerprint = "0000000000000000", number = 1, keycode = "", cnUid = CN_UID_A, globalUid = GLOBAL_UID_A },
                    new { fingerprint = "0000000000000000", number = 2, keycode = KEYCODE_A, cnUid = "short", globalUid = GLOBAL_UID_A },
                    new { fingerprint = "0000000000000000", number = "3", keycode = KEYCODE_A, cnUid = CN_UID_A, globalUid = GLOBAL_UID_A },
                    new { fingerprint = "0000000000000000", number = 4, keycode = KEYCODE_A, cnUid = CN_UID_A, globalUid = GLOBAL_UID_A },
                    new { fingerprint = "0000000000000000", number = 5, keycode = KEYCODE_B, cnUid = CN_UID_B, globalUid = "not-a-uid" }
                }
            }
        );

        var file = MinionCardSource.TryReadWorkbenchCards(CardsPath)!;

        Assert.Equal([4, 5], file.Cards.Select(card => card.Number));

        // 指纹按卡号重新算, 不信文件里的
        Assert.Equal(MinionCards.Fingerprint(KEYCODE_A), file.Cards[0].Fingerprint);
        Assert.Equal(GLOBAL_UID_A, file.Cards[0].GlobalUid);

        Assert.Equal(CN_UID_B, file.Cards[1].CnUid);
        Assert.Null(file.Cards[1].GlobalUid);
    }

    [Fact]
    public async Task WorkbenchFileBrieflyLocked_IsReadAfterRetry()
    {
        WriteWorkbenchFile(WorkbenchJson());

        // 模拟工作台正在替换文件: 独占打开一小会儿
        var locked  = new FileStream(CardsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var release = Task.Run(async () =>
        {
            await Task.Delay(200);
            await locked.DisposeAsync();
        });

        var file = MinionCardSource.TryReadWorkbenchCards(CardsPath);
        await release;

        Assert.NotNull(file);
        Assert.Equal(2, file.Cards.Count);
    }

    [Fact]
    public void WorkbenchFileLockedTooLong_FallsBackToAccountsJson()
    {
        WriteWorkbenchFile(WorkbenchJson());
        WriteAccountsJson();

        using var locked = new FileStream(CardsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.False(Load().FromWorkbench);
    }

    [Fact]
    public void MissingWorkbenchFile_FallsBackToAccountsJson()
    {
        WriteAccountsJson();

        var choices = Load();

        Assert.False(choices.FromWorkbench);
        Assert.Equal(MinionCardSourceKind.AccountsJson, choices.Source);
        Assert.Null(choices.GroupLoadError);
        Assert.Equal("来自 MINIONAPP（Accounts.json）", choices.SourceText);

        var group = Assert.Single(choices.Groups);
        Assert.Equal("5", group.Id);
        Assert.Equal([ROW_UID, GLOBAL_ROW_UID], group.Accounts.Select(row => row.Uid));
    }

    [Fact]
    public void CorruptWorkbenchFile_FallsBackToAccountsJson()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CardsPath)!);
        File.WriteAllBytes(CardsPath, [1, 2, 3, 4, 5, 6, 7, 8]);
        WriteAccountsJson();

        var choices = Load();

        Assert.False(choices.FromWorkbench);
        Assert.Single(choices.Groups);
    }

    [Fact]
    public void WorkbenchFileProtectedWithOtherEntropy_FallsBackToAccountsJson()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CardsPath)!);
        File.WriteAllBytes(CardsPath, ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(WorkbenchJson()), [9, 9, 9], DataProtectionScope.CurrentUser));
        WriteAccountsJson();

        Assert.False(Load().FromWorkbench);
    }

    [Fact]
    public void WorkbenchFileWithUnknownVersion_FallsBackToAccountsJson()
    {
        WriteWorkbenchFile(new { version = 2, forumId = FORUM_ID, forumPassword = FORUM_PASSWORD, cards = Array.Empty<object>() });
        WriteAccountsJson();

        Assert.False(Load().FromWorkbench);
    }

    [Fact]
    public void WorkbenchFileWithInvalidJson_FallsBackToAccountsJson()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CardsPath)!);
        File.WriteAllBytes(CardsPath, ProtectedData.Protect(Encoding.UTF8.GetBytes("{ not json"), Entropy, DataProtectionScope.CurrentUser));
        WriteAccountsJson();

        Assert.False(Load().FromWorkbench);
    }

    [Fact]
    public void NeitherSource_ReportsAccountsJsonError()
    {
        var choices = Load();

        Assert.False(choices.FromWorkbench);
        Assert.NotNull(choices.GroupLoadError);
        Assert.Empty(choices.Groups);
    }

    [Theory]
    [InlineData(MinionCards.VARIANT_CN, CN_UID_A)]
    [InlineData(MinionCards.VARIANT_GLOBAL, GLOBAL_UID_A)]
    public void Resolve_WorkbenchCard_BuildsSameLaunchAsCat(string variant, string expectedUid)
    {
        WriteWorkbenchFile(WorkbenchJson());

        var (launch, error) = Resolve(WorkbenchSelection(KEYCODE_A), "local-user-ignored", LOCAL_FORUM_PASSWORD, variant);

        Assert.Null(error);
        Assert.NotNull(launch);
        Assert.Equal(MinionCards.Fingerprint(KEYCODE_A), launch.Fingerprint);
        Assert.Equal(variant, launch.Variant);
        Assert.Equal(KEYCODE_A, launch.Keycode.Reveal());
        Assert.Equal(expectedUid, launch.Uid);
        Assert.Equal(FORUM_ID, launch.ForumId);
        Assert.Equal(FORUM_PASSWORD, launch.ForumPassword.Reveal());
    }

    [Fact]
    public void Resolve_WorkbenchCardWithoutGlobalUid_FailsOnlyForGlobal()
    {
        WriteWorkbenchFile
        (
            new
            {
                version = 1,
                forumId = FORUM_ID,
                forumPassword = FORUM_PASSWORD,
                cards = new object[] { new { number = 1, keycode = KEYCODE_A, cnUid = CN_UID_A, globalUid = "" } }
            }
        );

        Assert.Equal(CN_UID_A, Resolve(WorkbenchSelection(KEYCODE_A), null, null, MinionCards.VARIANT_CN).Launch!.Uid);

        var (launch, error) = Resolve(WorkbenchSelection(KEYCODE_A), null, null, MinionCards.VARIANT_GLOBAL);
        Assert.Null(launch);
        Assert.Contains("国际服", error);
    }

    [Fact]
    public void Resolve_WorkbenchSelectionButFileUnreadable_DoesNotFallBackToAccountsJson()
    {
        // Accounts.json 里残留的分组/行可以挂, 但界面上选的是工作台的卡
        WriteAccountsJson();
        Directory.CreateDirectory(Path.GetDirectoryName(CardsPath)!);
        File.WriteAllBytes(CardsPath, [1, 2, 3]);

        var selection = new MinionSelection(MinionCardSourceKind.Workbench, MinionCards.Fingerprint(KEYCODE_A), "5", ROW_UID);
        var (launch, error) = Resolve(selection, FORUM_ID, LOCAL_FORUM_PASSWORD, MinionCards.VARIANT_CN);

        Assert.Null(launch);
        Assert.Contains("读取 Cat 工作台的卡失败", error);
    }

    [Fact]
    public void Resolve_AccountsJsonSelectionButWorkbenchFileAppeared_Fails()
    {
        WriteAccountsJson();
        WriteWorkbenchFile(WorkbenchJson());

        var selection = new MinionSelection(MinionCardSourceKind.AccountsJson, MinionCards.Fingerprint(KEYCODE_A), "5", ROW_UID);
        var (launch, error) = Resolve(selection, FORUM_ID, LOCAL_FORUM_PASSWORD, MinionCards.VARIANT_CN);

        Assert.Null(launch);
        Assert.Contains("重选", error);
    }

    [Fact]
    public void Resolve_WorkbenchCardNoLongerThere_DoesNotPickAnotherCard()
    {
        WriteWorkbenchFile(WorkbenchJson());

        var (launch, error) = Resolve(new MinionSelection(MinionCardSourceKind.Workbench, "0123456789abcdef", null, null), null, null, MinionCards.VARIANT_CN);

        Assert.Null(launch);
        Assert.Contains("找不到", error);
    }

    [Fact]
    public void Resolve_WorkbenchWithoutForumAccount_Fails()
    {
        WriteWorkbenchFile
        (
            new
            {
                version = 1,
                forumId = (string?)null,
                forumPassword = (string?)null,
                cards = new object[] { new { number = 1, keycode = KEYCODE_A, cnUid = CN_UID_A, globalUid = GLOBAL_UID_A } }
            }
        );

        var (launch, error) = Resolve(WorkbenchSelection(KEYCODE_A), FORUM_ID, LOCAL_FORUM_PASSWORD, MinionCards.VARIANT_CN);

        Assert.Null(launch);
        Assert.Contains("论坛账号", error);
    }

    [Fact]
    public void Resolve_AccountsJsonRow_UsesRowUidAndLocalForumAccount()
    {
        WriteAccountsJson();

        var (launch, error) = Resolve(RowSelection(ROW_UID.ToLowerInvariant()), " " + FORUM_ID + " ", LOCAL_FORUM_PASSWORD, MinionCards.VARIANT_CN);

        Assert.Null(error);
        Assert.NotNull(launch);
        Assert.Equal(MinionCards.Fingerprint(KEYCODE_B), launch.Fingerprint);
        Assert.Equal(MinionCards.VARIANT_CN, launch.Variant);
        Assert.Equal(KEYCODE_B, launch.Keycode.Reveal());
        Assert.Equal(ROW_UID, launch.Uid);
        Assert.Equal(FORUM_ID, launch.ForumId);
        Assert.Equal(LOCAL_FORUM_PASSWORD, launch.ForumPassword.Reveal());
    }

    [Fact]
    public void Resolve_AccountsJsonGlobalRowForCnGame_Fails()
    {
        WriteAccountsJson();

        var (launch, error) = Resolve(RowSelection(GLOBAL_ROW_UID), FORUM_ID, LOCAL_FORUM_PASSWORD, MinionCards.VARIANT_CN);

        Assert.Null(launch);
        Assert.Contains("国际服的配置", error);
    }

    [Fact]
    public void GroupsFor_KeepsOnlyRowsOfThatVariant()
    {
        WriteAccountsJson();

        var group = Assert.Single(MinionCardSource.GroupsFor(Load().Groups, MinionCards.VARIANT_CN, CnRoots));
        Assert.Equal(ROW_UID, Assert.Single(group.Accounts).Uid);

        Assert.Equal(GLOBAL_ROW_UID, Assert.Single(Assert.Single(MinionCardSource.GroupsFor(Load().Groups, MinionCards.VARIANT_GLOBAL, CnRoots)).Accounts).Uid);
    }

    [Fact]
    public void Resolve_AccountsJsonRowNotFound_Fails()
    {
        WriteAccountsJson();

        var (launch, error) = Resolve(RowSelection("dddddddddddddddddddddddddddddddd"), FORUM_ID, LOCAL_FORUM_PASSWORD, MinionCards.VARIANT_CN);

        Assert.Null(launch);
        Assert.Contains("找不到", error);
    }

    [Fact]
    public void Resolve_AccountsJsonWithoutLocalForumAccount_Fails()
    {
        WriteAccountsJson();

        var (launch, error) = Resolve(RowSelection(ROW_UID), FORUM_ID, null, MinionCards.VARIANT_CN);

        Assert.Null(launch);
        Assert.Contains("论坛账号或密码", error);
    }

    [Fact]
    public void Fingerprint_MatchesServerAlgorithm()
    {
        var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("cat-minion-card:" + KEYCODE_A)))[..16];

        Assert.Equal(expected, MinionCards.Fingerprint(KEYCODE_A));
        Assert.True(MinionCards.IsValidFingerprint(MinionCards.Fingerprint(KEYCODE_A)));
    }

    [Fact]
    public void Picker_RefreshFillsSelectionWithoutWritingSettings()
    {
        WriteWorkbenchFile(WorkbenchJson());
        var settings = LoadSettings();
        var picker   = Picker(settings);

        picker.Refresh();

        Assert.True(picker.FromWorkbench);
        Assert.Equal(["卡 3", "卡 7"], picker.Cards.Select(card => card.DisplayName));
        Assert.Equal(3, picker.SelectedCard!.Number);
        Assert.Null(settings.MinionCardFingerprint);
        Assert.Null(settings.MinionGroup);
        Assert.Null(settings.MinionAccountUid);
        Assert.Equal(new MinionSelection(MinionCardSourceKind.Workbench, MinionCards.Fingerprint(KEYCODE_B), null, null), picker.CurrentSelection);
    }

    [Fact]
    public void Picker_UserChoiceIsSavedAndKeptAcrossRefresh()
    {
        WriteWorkbenchFile(WorkbenchJson());
        var settings = LoadSettings();
        var picker   = Picker(settings);
        picker.Refresh();

        picker.SelectedCard = picker.Cards.Single(card => card.Number == 7);
        Assert.Equal(MinionCards.Fingerprint(KEYCODE_A), settings.MinionCardFingerprint);

        // 工作台新写了一张卡, 下拉打开时重读
        WriteWorkbenchFile
        (
            new
            {
                version = 1,
                forumId = FORUM_ID,
                forumPassword = FORUM_PASSWORD,
                cards = new object[]
                {
                    new { number = 7, keycode = KEYCODE_A, cnUid = CN_UID_A, globalUid = GLOBAL_UID_A },
                    new { number = 1, keycode = KEYCODE_C, cnUid = CN_UID_B, globalUid = GLOBAL_UID_B }
                }
            }
        );
        picker.Refresh();

        Assert.Equal(["卡 1", "卡 7"], picker.Cards.Select(card => card.DisplayName));
        Assert.Equal(7, picker.SelectedCard!.Number);
        Assert.Equal(MinionCards.Fingerprint(KEYCODE_A), settings.MinionCardFingerprint);
    }

    [Fact]
    public void Picker_AccountsJsonListsOnlyCnRows_AndSavesUserChoice()
    {
        WriteAccountsJson();
        var settings = LoadSettings();
        var picker   = Picker(settings);

        picker.Refresh();

        Assert.True(picker.FromAccountsJson);
        Assert.Equal("5", picker.SelectedGroup!.Id);
        Assert.Equal(ROW_UID, Assert.Single(picker.AccountsInGroup).Uid);
        Assert.Null(settings.MinionGroup);

        picker.SelectedGroup = null;
        picker.SelectedGroup = picker.Groups.Single();

        Assert.Equal("5", settings.MinionGroup);
        Assert.Equal(ROW_UID, settings.MinionAccountUid);
        Assert.Equal(new MinionSelection(MinionCardSourceKind.AccountsJson, null, "5", ROW_UID), picker.CurrentSelection);
    }

    [Fact]
    public void Picker_RefreshAfterWorkbenchFileDisappears_SwitchesSourceButKeepsSavedCard()
    {
        WriteWorkbenchFile(WorkbenchJson());
        WriteAccountsJson();
        var settings = LoadSettings();
        var picker   = Picker(settings);
        picker.Refresh();
        picker.SelectedCard = picker.Cards.Single(card => card.Number == 7);

        File.Delete(CardsPath);
        picker.Refresh();

        Assert.True(picker.FromAccountsJson);
        Assert.Null(picker.SelectedCard);
        Assert.Equal(MinionCards.Fingerprint(KEYCODE_A), settings.MinionCardFingerprint);
    }

    [Fact]
    public void SecretsStayOutOfToStringAndLogs()
    {
        using var logs = new CapturedLogs();

        WriteWorkbenchFile(WorkbenchJson());
        WriteAccountsJson();

        var choices = Load();
        var file    = choices.Workbench!;
        var card    = file.Cards[0];
        var launch  = MinionCardSource.ToLaunch(file, card, MinionCards.VARIANT_CN);
        var row     = MinionAccounts.LoadAccounts(InstallPath)[0];

        // 损坏的卡文件只记原因
        File.WriteAllBytes(CardsPath, ProtectedData.Protect(Encoding.UTF8.GetBytes($"{{ \"version\": 1, \"forumPassword\": \"{FORUM_PASSWORD}\", \"cards\": [ {{ \"keycode\": \"{KEYCODE_A}\" "), Entropy, DataProtectionScope.CurrentUser));
        Assert.False(Load().FromWorkbench);

        var text = string.Join("\n", file.ToString(), card.ToString(), launch.ToString(), row.ToString(), choices.SourceText, logs.All);

        Assert.DoesNotContain(KEYCODE_A, text);
        Assert.DoesNotContain(KEYCODE_B, text);
        Assert.DoesNotContain(FORUM_PASSWORD, text);
        Assert.Contains("读取工作台卡文件失败", logs.All);
    }

    private MinionCardChoices Load() =>
        MinionCardSource.Load(CardsPath, InstallPath);

    private (XIVLauncher.CatHost.CatMinionLaunch? Launch, string? Error) Resolve(MinionSelection selection, string? forumId, string? forumPassword, string variant) =>
        MinionCardSource.Resolve(Load(), selection, forumId, forumPassword, variant, CnRoots);

    private static MinionSelection WorkbenchSelection(string keycode) =>
        new(MinionCardSourceKind.Workbench, MinionCards.Fingerprint(keycode), null, null);

    private static MinionSelection RowSelection(string uid) =>
        new(MinionCardSourceKind.AccountsJson, null, "5", uid);

    private LauncherSettingsV3 LoadSettings() =>
        LauncherSettingsV3.Load(Path.Combine(directory.FullName, "launcherConfigV3.json"));

    private MinionPickerViewModel Picker(LauncherSettingsV3 settings) =>
        new(settings, Load, () => CnRoots);

    private static object WorkbenchJson() =>
        new
        {
            version = 1,
            updatedAt = "2026-10-10T01:02:03Z",
            forumId = FORUM_ID,
            forumPassword = FORUM_PASSWORD,
            cards = new object[]
            {
                new { fingerprint = MinionCards.Fingerprint(KEYCODE_A), number = 7, keycode = KEYCODE_A, cnUid = CN_UID_A, globalUid = GLOBAL_UID_A },
                new { fingerprint = MinionCards.Fingerprint(KEYCODE_B), number = 3, keycode = KEYCODE_B, cnUid = CN_UID_B, globalUid = GLOBAL_UID_B }
            }
        };

    private void WriteWorkbenchFile(object json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CardsPath)!);
        File.WriteAllBytes(CardsPath, ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(json), Entropy, DataProtectionScope.CurrentUser));
    }

    private void WriteAccountsJson()
    {
        var settings = Path.Combine(InstallPath, "Settings");
        Directory.CreateDirectory(settings);

        var rows = new object[]
        {
            new { UID = ROW_UID, Keycode = KEYCODE_B, Group = "5", PathToExe = Path.Combine(CnGameRoot, "sdo", "sdologin", "Launcher.exe") },
            new { UID = GLOBAL_ROW_UID, Keycode = KEYCODE_B, Group = "5", PathToExe = Path.Combine(GlobalGameRoot, "game", "ffxiv_dx11.exe") }
        };

        // MINIONAPP 写的文件带 BOM
        File.WriteAllText(Path.Combine(settings, "Accounts.json"), JsonSerializer.Serialize(rows), new UTF8Encoding(true));
    }
}
