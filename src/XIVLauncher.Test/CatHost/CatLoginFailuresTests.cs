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
    [InlineData(-10242301)]
    [InlineData(-1)]
    public void OtherLoginCodes_AreRejected(int code) =>
        Assert.Equal(CatLoginFailureKind.Rejected, CatLoginFailures.Classify(new LoginException(code, "被拒")));

    [Fact]
    public void UnexpectedErrors_AreUnknown() =>
        Assert.Equal(CatLoginFailureKind.Unknown, CatLoginFailures.Classify(new NullReferenceException()));

    [Fact]
    public void ToCode_MapsKinds()
    {
        Assert.Equal(CatCodes.NETWORK_ERROR, CatLoginFailures.ToCode(CatLoginFailureKind.Network, CatCodes.AUTHORIZATION_REQUIRED));
        Assert.Equal(CatCodes.RISK_CONTROL, CatLoginFailures.ToCode(CatLoginFailureKind.RiskControl, CatCodes.AUTHORIZATION_REQUIRED));
        Assert.Equal(CatCodes.AUTHORIZATION_REQUIRED, CatLoginFailures.ToCode(CatLoginFailureKind.Rejected, CatCodes.AUTHORIZATION_REQUIRED));
        Assert.Equal(CatCodes.LAUNCH_FAILED, CatLoginFailures.ToCode(CatLoginFailureKind.Unknown, CatCodes.LAUNCH_FAILED));
    }
}
