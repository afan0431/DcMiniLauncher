using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using XIVLauncher.CatHost;
using XIVLauncher.Login.Exceptions;
using Xunit;

namespace XIVLauncher.Test.CatHost;

public sealed class CatLoginFailuresTests
{
    public static TheoryData<Exception> NetworkErrors =>
    [
        new HttpRequestException("连接失败"),
        new TaskCanceledException("超时"),
        new IOException("读流失败"),
        new SocketException(10060),
        new TimeoutException(),
        new InvalidOperationException("包装", new HttpRequestException("内层")),
        new Newtonsoft.Json.JsonReaderException("回包不是 JSON")
    ];

    [Theory]
    [MemberData(nameof(NetworkErrors))]
    public void NetworkErrors_AreNotTreatedAsRejected(Exception exception) =>
        Assert.Equal(CatLoginFailureKind.Network, CatLoginFailures.Classify(exception));

    [Theory]
    [InlineData((int)LoginExceptionCode.RiskEnvironment)]
    [InlineData((int)LoginExceptionCode.UseDaoYuApp)]
    [InlineData((int)LoginExceptionCode.SafePhoneVerificationCanceled)]
    public void RiskCodes_AreRiskControl(int code) =>
        Assert.Equal(CatLoginFailureKind.RiskControl, CatLoginFailures.Classify(new LoginException(code, "要客户验证")));

    [Theory]
    [InlineData((int)LoginExceptionCode.FirstLoginOnDevice)]
    [InlineData((int)LoginExceptionCode.OutdatedLoginInfo)]
    [InlineData((int)LoginExceptionCode.ThirdPartyVerificationFailed)]
    [InlineData(-10242301)]
    public void KnownRejectionCodes_AreRejected(int code) =>
        Assert.Equal(CatLoginFailureKind.Rejected, CatLoginFailures.Classify(new LoginException(code, "被拒")));

    [Theory]
    [InlineData(-1)]
    [InlineData(-10999999)]
    public void UnknownLoginCodes_AreUnknown_SoNoPasswordFallback(int code) =>
        Assert.Equal(CatLoginFailureKind.Unknown, CatLoginFailures.Classify(new LoginException(code, "服务器繁忙")));

    [Fact]
    public void OAuthErrors_AreUnknown() =>
        Assert.Equal(CatLoginFailureKind.Unknown, CatLoginFailures.Classify(new OAuthLoginException("getGuid 失败")));

    [Fact]
    public void UnexpectedErrors_AreUnknown() =>
        Assert.Equal(CatLoginFailureKind.Unknown, CatLoginFailures.Classify(new NullReferenceException()));

    [Fact]
    public void Describe_IncludesReturnCode() =>
        Assert.Contains("-10999999", CatLoginFailures.Describe(new InvalidOperationException("包装", new LoginException(-10999999, "繁忙"))));

    [Fact]
    public void WeGameToken_IsClearedOnlyWhenSdoAnswersNo()
    {
        // 只有盛趣明确说这枚令牌不行才清: 无界面清完没人重新取令牌
        Assert.True(CatLoginFailures.ShouldClearWeGameToken(new LoginException((int)LoginExceptionCode.ThirdPartyVerificationFailed, "第三方验证失败")));
        Assert.True(CatLoginFailures.ShouldClearWeGameToken(new LoginException(-10742165, "第三方验证失败")));

        Assert.False(CatLoginFailures.ShouldClearWeGameToken(new LoginException(-10999999, "繁忙")));
        Assert.False(CatLoginFailures.ShouldClearWeGameToken(new LoginException((int)LoginExceptionCode.RiskEnvironment, "要客户验证")));
        Assert.False(CatLoginFailures.ShouldClearWeGameToken(new LoginException((int)LoginExceptionCode.OutdatedLoginInfo, "登录过期")));
        Assert.False(CatLoginFailures.ShouldClearWeGameToken(new InvalidOperationException("包装", new LoginException(-10742165, "第三方验证失败"))));
        Assert.False(CatLoginFailures.ShouldClearWeGameToken(new HttpRequestException("连接失败")));
        Assert.False(CatLoginFailures.ShouldClearWeGameToken(new OAuthLoginException("getGuid 失败")));
        Assert.False(CatLoginFailures.ShouldClearWeGameToken(new Newtonsoft.Json.JsonReaderException("回包不是 JSON")));
    }

    [Fact]
    public void ToWeGameLoginCode_MapsFailures()
    {
        // 令牌被拒: 只能回界面版重新登录; 不认识的返回码（可能是繁忙）不算被拒, 按启动失败报
        Assert.Equal(CatCodes.AUTHORIZATION_REQUIRED, CatLoginFailures.ToWeGameLoginCode(new LoginException((int)LoginExceptionCode.ThirdPartyVerificationFailed, "第三方验证失败")));
        Assert.Equal(CatCodes.AUTHORIZATION_REQUIRED, CatLoginFailures.ToWeGameLoginCode(new LoginException((int)LoginExceptionCode.OutdatedLoginInfo, "登录过期")));
        Assert.Equal(CatCodes.LAUNCH_FAILED, CatLoginFailures.ToWeGameLoginCode(new LoginException(-10999999, "繁忙")));

        Assert.Equal(CatCodes.RISK_CONTROL, CatLoginFailures.ToWeGameLoginCode(new LoginException((int)LoginExceptionCode.RiskEnvironment, "要客户验证")));
        Assert.Equal(CatCodes.NETWORK_ERROR, CatLoginFailures.ToWeGameLoginCode(new HttpRequestException("连接失败")));
        Assert.Equal(CatCodes.NETWORK_ERROR, CatLoginFailures.ToWeGameLoginCode(new TaskCanceledException("超时")));
        Assert.Equal(CatCodes.LAUNCH_FAILED, CatLoginFailures.ToWeGameLoginCode(new OAuthLoginException("getGuid 失败")));
        Assert.Equal(CatCodes.LAUNCH_FAILED, CatLoginFailures.ToWeGameLoginCode(new NullReferenceException()));
    }

    [Fact]
    public void ToCode_MapsKinds()
    {
        Assert.Equal(CatCodes.NETWORK_ERROR, CatLoginFailures.ToCode(CatLoginFailureKind.Network, CatCodes.AUTHORIZATION_REQUIRED));
        Assert.Equal(CatCodes.RISK_CONTROL, CatLoginFailures.ToCode(CatLoginFailureKind.RiskControl, CatCodes.AUTHORIZATION_REQUIRED));
        Assert.Equal(CatCodes.AUTHORIZATION_REQUIRED, CatLoginFailures.ToCode(CatLoginFailureKind.Rejected, CatCodes.AUTHORIZATION_REQUIRED));
        Assert.Equal(CatCodes.LAUNCH_FAILED, CatLoginFailures.ToCode(CatLoginFailureKind.Unknown, CatCodes.LAUNCH_FAILED));
    }
}
