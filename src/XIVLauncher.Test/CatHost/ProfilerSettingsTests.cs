using System.IO;
using XIVLauncher.Minion;
using Xunit;

namespace XIVLauncher.Test.CatHost;

public sealed class ProfilerSettingsTests
{
    private static readonly string SamplePath = Path.Combine(AppContext.BaseDirectory, "CatHost", "Fixtures", "ProfilerSettings.sample.lua");

    [Fact]
    public void Sample_ReadsCurrentProfileAndProfilesInOrder()
    {
        var settings = ProfilerSettings.Load(SamplePath);

        Assert.Equal("Main", settings.CurrentProfile);
        Assert.Equal(["AfanTest", "EX", "Main", "Optifine", "Yukimura", "bluemage", "fight", "hus", "madao", "none"], settings.Profiles.Select(p => p.Name));
        Assert.Contains("Afan", settings.Find("AfanTest")!.Mods);
        Assert.Contains("Ace's Casual Master Pack", settings.Find("EX")!.Mods);
        Assert.Equal(["ACR", "ffxivminion", "MinionDB", "minionlib", "Questing", "Dev", "Duties", "translations", "TensorLoader", "Profiler"], settings.Find("none")!.Mods);
        Assert.Null(settings.Find("main"));
    }

    [Fact]
    public void Parse_AcceptsBareAndBracketKeys_AndSkipsOtherTables()
    {
        const string text = """
                            local tbl =
                            {
                            	currentProfile = "Name 2",
                            	favorites = { },
                            	profiles =
                            	{
                            		Plain = { "a", "b", },
                            		["Name 2"] = { "c" },
                            		['single'] = { 'd\'q' },
                            	},
                            	seenAddons = { ["x y"] = true, z = false, [1] = 2.5, n = nil, },
                            }
                            return tbl
                            """;

        var settings = ProfilerSettings.Parse(text);

        Assert.Equal("Name 2", settings.CurrentProfile);
        Assert.Equal(["Plain", "Name 2", "single"], settings.Profiles.Select(p => p.Name));
        Assert.Equal(["a", "b"], settings.Find("Plain")!.Mods);
        Assert.Equal(["c"], settings.Find("Name 2")!.Mods);
        Assert.Equal(["d'q"], settings.Find("single")!.Mods);
    }

    [Fact]
    public void Parse_EmptyProfilesTable()
    {
        var settings = ProfilerSettings.Parse("return {\n\tcurrentProfile = \"\",\n\tprofiles = \n\t{\n\t},\n}\n");

        Assert.Equal("", settings.CurrentProfile);
        Assert.True(settings.HasProfilesTable);
        Assert.Empty(settings.Profiles);
    }

    [Fact]
    public void Parse_WithoutProfilesOrCurrentProfile()
    {
        var settings = ProfilerSettings.Parse("local t = { open = false } return t");

        Assert.Null(settings.CurrentProfile);
        Assert.False(settings.HasProfilesTable);
        Assert.Empty(settings.Profiles);
    }

    [Theory]
    [InlineData("")]
    [InlineData("local tbl = { currentProfile = \"Main }\nreturn tbl")]
    [InlineData("local tbl = { profiles = 5 }\nreturn tbl")]
    [InlineData("local tbl = { profiles = { Main = { a = \"x\" } } }\nreturn tbl")]
    [InlineData("local tbl = { profiles = { \"Main\" } }\nreturn tbl")]
    [InlineData("local tbl = { currentProfile = 1 }\nreturn tbl")]
    [InlineData("local tbl = { f = function() end }\nreturn tbl")]
    [InlineData("local tbl = { open = true }\nreturn other")]
    [InlineData("local tbl = { open = true }\nreturn tbl\nextra")]
    [InlineData("local tbl = { open = true ")]
    public void TryParse_RejectsWhatItDoesNotUnderstand(string text)
    {
        Assert.False(ProfilerSettings.TryParse(text, out var settings, out var error));
        Assert.Null(settings);
        Assert.False(string.IsNullOrEmpty(error));
        Assert.Throws<FormatException>(() => ProfilerSettings.Parse(text));
    }

    [Fact]
    public void WithCurrentProfile_ChangesOnlyThatValue()
    {
        var settings  = ProfilerSettings.Load(SamplePath);
        var rewritten = settings.WithCurrentProfile("EX");

        Assert.Equal(settings.Text.Replace("currentProfile = \"Main\"", "currentProfile = \"EX\""), rewritten);

        var reparsed = ProfilerSettings.Parse(rewritten);
        Assert.Equal("EX", reparsed.CurrentProfile);
        Assert.True(ProfilerSettings.SameProfiles(settings.Profiles, reparsed.Profiles));
    }

    [Fact]
    public void WithCurrentProfile_QuotesAndInsertsWhenMissing()
    {
        var settings = ProfilerSettings.Parse("local tbl = \n{\n\topen = false,\n}\nreturn tbl");

        var rewritten = settings.WithCurrentProfile("a \"b\" \\c");

        Assert.Equal("a \"b\" \\c", ProfilerSettings.Parse(rewritten).CurrentProfile);
        Assert.Contains("\topen = false,", rewritten);
    }

    [Fact]
    public void WithProfiles_RoundTripsTheSample_AndKeepsOtherText()
    {
        var settings  = ProfilerSettings.Load(SamplePath);
        var rewritten = settings.WithProfiles(settings.Profiles);

        // Profiler 自己写出的排版与这里一致, 原样写回不该有任何差别
        Assert.Equal(settings.Text, rewritten);
    }

    [Fact]
    public void WithProfiles_WritesQuotedKeysThatReparse()
    {
        var settings = ProfilerSettings.Load(SamplePath);
        var profiles = settings.Profiles.Append(new ProfilerProfile("1-90 Class Pack", ["Afan", "Ace's"]))
                               .Append(new ProfilerProfile("end", []))
                               .ToList();

        var reparsed = ProfilerSettings.Parse(settings.WithProfiles(profiles));

        Assert.True(ProfilerSettings.SameProfiles(profiles, reparsed.Profiles));
        Assert.Equal("Main", reparsed.CurrentProfile);
    }

    [Fact]
    public void WithProfilesTableOf_SplicesTheOtherFilesTable()
    {
        var main   = ProfilerSettings.Parse("return {\n\tcurrentProfile = \"Main\",\n\tprofiles = { Main = { \"a\" } },\n\topen = true,\n}");
        var client = ProfilerSettings.Parse("return {\n\tcurrentProfile = \"EX\",\n\tprofiles = { Main = { \"a\" }, EX = { \"b\" } },\n}");

        var merged = main.WithProfilesTableOf(client);

        Assert.Equal("return {\n\tcurrentProfile = \"Main\",\n\tprofiles = { Main = { \"a\" }, EX = { \"b\" } },\n\topen = true,\n}", merged);
    }

    [Fact]
    public void Merge_AppliesClientChangesOnTopOfCurrentMain()
    {
        IReadOnlyList<ProfilerProfile> baseline = [new("Main", ["a"]), new("EX", ["b"]), new("Gone", ["c"])];
        IReadOnlyList<ProfilerProfile> current  = [new("Main", ["a"]), new("EX", ["b"]), new("Gone", ["c"]), new("Other", ["o"])];
        IReadOnlyList<ProfilerProfile> edited   = [new("Main", ["a", "z"]), new("EX", ["b"]), new("New", ["n"])];

        var merged = ProfilerSettings.Merge(baseline, current, edited);

        Assert.NotNull(merged);
        Assert.Equal(["Main", "EX", "Other", "New"], merged!.Select(p => p.Name));
        Assert.Equal(["a", "z"], merged[0].Mods);
    }

    [Fact]
    public void Load_InvalidUtf8_Throws()
    {
        var path = Path.Combine(Path.GetTempPath(), "cat-profiler-" + Guid.NewGuid().ToString("N") + ".lua");

        try
        {
            File.WriteAllBytes(path, [.. "return { currentProfile = \"A".Select(c => (byte)c), 0xB0, 0xA1, (byte)'"', (byte)' ', (byte)'}']);
            Assert.Throws<FormatException>(() => ProfilerSettings.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Merge_NoClientChange_ReturnsNull()
    {
        IReadOnlyList<ProfilerProfile> baseline = [new("Main", ["a"])];
        IReadOnlyList<ProfilerProfile> current  = [new("Main", ["a"]), new("Other", ["o"])];

        Assert.Null(ProfilerSettings.Merge(baseline, current, [new("Main", ["a"])]));
    }
}
