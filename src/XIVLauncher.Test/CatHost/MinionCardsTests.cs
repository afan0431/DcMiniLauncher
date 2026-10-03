using System.Security.Cryptography;
using System.Text;
using XIVLauncher.Minion;
using Xunit;

namespace XIVLauncher.Test.CatHost;

public sealed class MinionCardsTests
{
    private const string KEY_A = "ABCDEF0123456789ABCDEF0123456789ABCDEF01234567";
    private const string KEY_B = "ZZZZZZ9876543210ZZZZZZ9876543210ZZZZZZ98765432";

    private static readonly MinionAccount[] Rows =
    [
        new() { Uid = "uid-a-cn", Keycode     = KEY_A, Group = "3", UseBetaFiles = false },
        new() { Uid = "uid-a-global", Keycode = KEY_A, Group = "3", UseBetaFiles = true },
        new() { Uid = "uid-b-cn", Keycode     = KEY_B, Group = "4", UseBetaFiles = false }
    ];

    [Fact]
    public void Fingerprint_IsFirst16LowerHexOfPrefixedSha256()
    {
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("cat-minion-card:" + KEY_A))).ToLowerInvariant()[..16];

        Assert.Equal(expected, MinionCards.Fingerprint(KEY_A));
        Assert.True(MinionCards.IsValidFingerprint(MinionCards.Fingerprint(KEY_A)));
    }

    [Theory]
    [InlineData("cn", "uid-a-cn")]
    [InlineData("global", "uid-a-global")]
    public void FindByCard_PicksRowByVariant(string variant, string expectedUid)
    {
        var row = MinionCards.FindByCard(Rows, MinionCards.Fingerprint(KEY_A), variant);

        Assert.Equal(expectedUid, row?.Uid);
    }

    [Fact]
    public void FindByCard_MissingVariant_ReturnsNullInsteadOfOtherRow()
    {
        // 卡 B 只有国服行, 要国际服行时不能退回国服行或分组第一行
        Assert.Null(MinionCards.FindByCard(Rows, MinionCards.Fingerprint(KEY_B), MinionCards.VARIANT_GLOBAL));
    }

    [Fact]
    public void FindByCard_UnknownCard_ReturnsNull()
    {
        Assert.Null(MinionCards.FindByCard(Rows, MinionCards.Fingerprint("not-a-real-keycode"), MinionCards.VARIANT_CN));
        Assert.Null(MinionCards.FindByCard(Rows, "not-a-fingerprint", MinionCards.VARIANT_CN));
        Assert.Null(MinionCards.FindByCard(Rows, MinionCards.Fingerprint(KEY_A), "beta"));
    }

    [Fact]
    public void FindAccount_UidNotInGroup_ReturnsNullInsteadOfFirstAccount()
    {
        Assert.Null(MinionAccounts.FindAccount(Rows, "3", "uid-that-was-deleted"));
        Assert.Null(MinionAccounts.FindAccount(Rows, "3", null));
        Assert.Null(MinionAccounts.FindAccount(Rows, "4", "uid-a-cn"));
        Assert.Equal("uid-a-global", MinionAccounts.FindAccount(Rows, "3", "UID-A-GLOBAL")?.Uid);
    }
}
