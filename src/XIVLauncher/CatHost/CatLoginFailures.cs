using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using XIVLauncher.Login.Exceptions;

namespace XIVLauncher.CatHost;

/// <summary>登录失败的种类, 决定要不要换密码登录、报哪个失败码</summary>
public enum CatLoginFailureKind
{
    /// <summary>连不上、超时、回包读不出: 可直接重试, 不换密码（换了也是多登录一次）</summary>
    Network,

    /// <summary>凭证被盛趣拒绝（失效、设备不认等）: 有密码才用密码登录一次</summary>
    Rejected,

    /// <summary>盛趣要求客户验证（安全手机短信、叨鱼扫码）: 不再尝试</summary>
    RiskControl,

    /// <summary>其它意外错误: 不再尝试, 按启动失败报</summary>
    Unknown
}

/// <summary>
///     无界面登录失败分类
/// </summary>
public static class CatLoginFailures
{
    /// <summary>
    ///     按异常判断失败种类（调用方自己排除主动取消）
    /// </summary>
    public static CatLoginFailureKind Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        for (var current = exception; current != null; current = current.InnerException)
        {
            switch (current)
            {
                case LoginException login:
                    return IsRiskControl(login.ErrorCode) ? CatLoginFailureKind.RiskControl : CatLoginFailureKind.Rejected;

                case OAuthLoginException:
                    return CatLoginFailureKind.Rejected;

                case HttpRequestException or IOException or SocketException or TimeoutException or OperationCanceledException:
                    return CatLoginFailureKind.Network;

                // 回包不是预期的 JSON（被劫持的网页、网关报错页）, 跟网络问题一样重试即可
                case JsonException or InvalidDataException:
                    return CatLoginFailureKind.Network;
            }

            if (current.GetType().FullName?.StartsWith("Newtonsoft.Json.", StringComparison.Ordinal) == true)
                return CatLoginFailureKind.Network;
        }

        return CatLoginFailureKind.Unknown;
    }

    /// <summary>
    ///     盛趣返回码是否表示要客户验证
    /// </summary>
    public static bool IsRiskControl(int errorCode) =>
        errorCode is (int)LoginExceptionCode.RiskEnvironment
                  or (int)LoginExceptionCode.UseDaoYuApp
                  or (int)LoginExceptionCode.SafePhoneVerificationCanceled;

    /// <summary>
    ///     失败种类对应的协议失败码; <paramref name="otherwise" /> 用于凭证被拒与意外错误
    /// </summary>
    public static string ToCode(CatLoginFailureKind kind, string otherwise) =>
        kind switch
        {
            CatLoginFailureKind.Network     => CatCodes.NETWORK_ERROR,
            CatLoginFailureKind.RiskControl => CatCodes.RISK_CONTROL,
            _                               => otherwise
        };
}
