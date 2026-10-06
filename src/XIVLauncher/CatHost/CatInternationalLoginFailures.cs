using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using XIVLauncher.Common.Game.Exceptions;
using XIVLauncher.Common.Game.International;

namespace XIVLauncher.CatHost;

/// <summary>
///     国际服无界面启动: 登录各种结果 → 协议失败码 + 给员工看的话。
///     Square Enix 不给机器可读的失败分类（goatcorp 也分不出密码错、要求一次性密码、账号被锁, 见它的 MainWindowViewModel.cs:513-536）,
///     所以被拒绝一律报 authorizationRequired 并带上 SE 的原文。消息用大白话, 不出现内部词。
/// </summary>
public static class CatInternationalLoginFailures
{
    /// <summary>有更新时的处理办法: 本启动器打不了国际服的补丁</summary>
    public const string UPDATE_HINT = "请在这台电脑上用官方启动器更新国际服客户端";

    /// <summary>SE 原文最多带多少个字（外壳把整条消息截到 200 字）</summary>
    private const int MAX_SE_MESSAGE_LENGTH = 120;

    /// <summary>
    ///     登录返回的状态 → 失败码与消息; <see cref="InternationalLoginState.Ok" /> 返回 null
    /// </summary>
    public static (string Code, string Message)? FromState(InternationalLoginState state) =>
        state switch
        {
            InternationalLoginState.Ok => null,

            InternationalLoginState.NoService =>
                (CatCodes.LAUNCH_FAILED, "这个国际服账号现在不能玩: 没有游戏资格（没买游戏, 或者游戏时间到期了）"),

            InternationalLoginState.NoTerms =>
                (CatCodes.LAUNCH_FAILED, "这个国际服账号还没有同意用户协议, 需要先用官方启动器登录一次并同意"),

            InternationalLoginState.NeedsPatchGame =>
                (CatCodes.GAME_UPDATE_REQUIRED, $"国际服客户端有更新, {UPDATE_HINT}"),

            InternationalLoginState.NeedsPatchBoot =>
                (CatCodes.GAME_UPDATE_REQUIRED, $"国际服客户端的启动文件和官方的不一样（被改过或损坏）, {UPDATE_HINT}; 更新不了就要重装"),

            _ => (CatCodes.LAUNCH_FAILED, $"国际服登录返回了不认识的状态: {state}")
        };

    /// <summary>
    ///     登录、版本检查过程中的异常 → 失败码与消息（调用方自己排除主动取消）
    /// </summary>
    /// <param name="exception">异常</param>
    /// <param name="redact">脱敏; SE 的原文和其它异常文字都要先过它</param>
    public static (string Code, string Message) FromException(Exception exception, Func<string, string> redact)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(redact);

        for (var current = exception; current != null; current = current.InnerException)
        {
            switch (current)
            {
                case InternationalLoginRejectedException rejected:
                    return (CatCodes.AUTHORIZATION_REQUIRED, DescribeRejection(rejected.SeMessage, redact));

                case InternationalInvalidResponseException invalid:
                    return FromInvalidResponse(invalid.Kind);

                case InvalidVersionFilesException:
                    return (CatCodes.INVALID_GAME_PATH, "国际服客户端的版本文件损坏, 请在这台电脑上用官方启动器修复国际服客户端");

                case HttpRequestException or SocketException or TimeoutException or OperationCanceledException:
                    return (CatCodes.NETWORK_ERROR, "连不上国际服的登录服务器, 稍后重试即可");

                // 回包不是预期的内容（网关报错页等）, 跟网络问题一样稍后重试
                case JsonException:
                    return (CatCodes.NETWORK_ERROR, "国际服的服务器返回了读不懂的内容, 稍后重试即可");

                // 读游戏目录里的文件失败（HTTP 的读写错误都包在 HttpRequestException 里, 走不到这里）
                case IOException or UnauthorizedAccessException:
                    return (CatCodes.INVALID_GAME_PATH, "读不到国际服客户端的文件（boot 文件夹不完整或目录打不开）, 请检查 DcMiniLauncher 设置里的国际服游戏目录");
            }
        }

        return (CatCodes.LAUNCH_FAILED, $"国际服登录出错: {exception.GetType().Name}: {redact(exception.Message)}");
    }

    /// <summary>
    ///     被 SE 拒绝登录时给员工看的话
    /// </summary>
    public static string DescribeRejection(string? seMessage, Func<string, string> redact)
    {
        const string HINT = "（账号或密码不对、账号开了一次性密码、账号被锁都会这样; 开了一次性密码的号要客户先关掉）";

        if (string.IsNullOrWhiteSpace(seMessage))
            return $"国际服登录被拒绝, 服务器没有说明原因{HINT}";

        // SE 的文字里换行是字面的 \r\n（goatcorp MainWindowViewModel.cs:521-523 也这样处理）
        var text = redact(seMessage).Replace("\\r\\n", " ").Replace("\\n", " ").Replace("\r", " ").Replace("\n", " ").Trim();

        if (text.Length > MAX_SE_MESSAGE_LENGTH)
            text = text[..MAX_SE_MESSAGE_LENGTH] + "…";

        return $"国际服登录被拒绝: {text} {HINT}";
    }

    private static (string Code, string Message) FromInvalidResponse(InternationalInvalidResponseKind kind) =>
        kind switch
        {
            InternationalInvalidResponseKind.StoredNotFound =>
                (CatCodes.LAUNCH_FAILED, "国际服的登录页打不开或内容不对（服务器故障、太忙, 或者要求人工验证）, 现在没法自动登录, 请稍后再试"),

            InternationalInvalidResponseKind.RestartupRequested =>
                (CatCodes.LAUNCH_FAILED, "国际服的登录页要求用官方启动器处理（Steam 版账号等情况）, 这个号不能自动登录"),

            InternationalInvalidResponseKind.LoginReplyMalformed =>
                (CatCodes.LAUNCH_FAILED, "国际服登录返回的内容和预期不一样（登录方式可能改了）, 这个版本的 DcMiniLauncher 暂时没法自动登录"),

            // 版本服务器说这个版本已不再提供服务: 客户端太旧
            InternationalInvalidResponseKind.GameVersionGone =>
                (CatCodes.GAME_UPDATE_REQUIRED, $"国际服客户端的版本太旧, 服务器已经不认了, {UPDATE_HINT}"),

            InternationalInvalidResponseKind.UniqueIdMissing =>
                (CatCodes.LAUNCH_FAILED, "国际服的版本服务器返回异常（可能正在维护）, 请稍后再试"),

            InternationalInvalidResponseKind.BootCheckFailed =>
                (CatCodes.LAUNCH_FAILED, "检查国际服客户端版本时服务器返回异常（可能正在维护）, 请稍后再试"),

            _ => (CatCodes.LAUNCH_FAILED, "国际服的服务器返回异常, 请稍后再试")
        };
}
