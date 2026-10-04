using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XIVLauncher.CatHost;

/// <summary>
///     无界面启动协议 dml-cat/1 的常量
/// </summary>
public static class CatProtocol
{
    /// <summary>协议版本</summary>
    public const string PROTOCOL_VERSION = "dml-cat/1";

    /// <summary>管道名前缀</summary>
    public const string PIPE_NAME_PREFIX = "cat-dml-";

    /// <summary>JSON 序列化选项: camelCase、忽略 null、中文不转义</summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
        Encoder                     = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    ///     时间格式: UTC ISO 8601, 精确到毫秒, 以 Z 结尾
    /// </summary>
    public static string FormatTimestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>
    ///     管道名是否合法: cat-dml- 加 32 位十六进制
    /// </summary>
    public static bool IsValidPipeName(string? pipeName) =>
        pipeName is { Length: 40 } &&
        pipeName.StartsWith(PIPE_NAME_PREFIX, StringComparison.Ordinal) &&
        pipeName[PIPE_NAME_PREFIX.Length..].All(Uri.IsHexDigit);

    /// <summary>
    ///     令牌长度是否合法
    /// </summary>
    public static bool IsValidToken(string? token) =>
        token is { Length: >= 16 and <= 1024 };
}

/// <summary>启动阶段</summary>
public static class CatStages
{
    /// <summary>尚未收到 launch</summary>
    public const string IDLE = "idle";

    /// <summary>准备（账号、游戏目录、登录换票据、跨区会话）</summary>
    public const string PREPARING = "preparing";

    /// <summary>Dalamud 准备与更新</summary>
    public const string UPDATING_DALAMUD = "updatingDalamud";

    /// <summary>创建游戏进程</summary>
    public const string STARTING = "starting";

    /// <summary>等 Dalamud 注入落地</summary>
    public const string INJECTING = "injecting";

    /// <summary>挂 Minion</summary>
    public const string ATTACHING_MINION = "attachingMinion";

    /// <summary>游戏运行中</summary>
    public const string RUNNING = "running";

    /// <summary>游戏已退出</summary>
    public const string EXITED = "exited";

    /// <summary>启动失败</summary>
    public const string FAILED = "failed";
}

/// <summary>失败码</summary>
public static class CatCodes
{
    /// <summary>没有可用凭证或凭证已失效, 需要重新授权</summary>
    public const string AUTHORIZATION_REQUIRED = "authorizationRequired";

    /// <summary>游戏目录无效</summary>
    public const string INVALID_GAME_PATH = "invalidGamePath";

    /// <summary>游戏需要打补丁</summary>
    public const string GAME_UPDATE_REQUIRED = "gameUpdateRequired";

    /// <summary>Dalamud 不可用</summary>
    public const string DALAMUD_UNAVAILABLE = "dalamudUnavailable";

    /// <summary>Minion 安装目录、账号或密码未配置</summary>
    public const string MINION_NOT_CONFIGURED = "minionNotConfigured";

    /// <summary>本机 Accounts.json 里找不到对应的卡</summary>
    public const string MINION_CARD_NOT_FOUND = "minionCardNotFound";

    /// <summary>其它启动失败</summary>
    public const string LAUNCH_FAILED = "launchFailed";

    /// <summary>本进程已接受过 launch</summary>
    public const string ALREADY_LAUNCHED = "alreadyLaunched";

    /// <summary>当前没有运行中的游戏</summary>
    public const string NOT_RUNNING = "notRunning";

    /// <summary>Minion 挂载失败（游戏已在运行）</summary>
    public const string ATTACH_FAILED = "attachFailed";

    /// <summary>参数无效</summary>
    public const string INVALID_PARAMS = "invalidParams";
}

/// <summary>代理类型</summary>
public static class CatAgentKinds
{
    /// <summary>Dalamud</summary>
    public const string DALAMUD = "dalamud";

    /// <summary>Minion</summary>
    public const string MINION = "minion";
}

/// <summary>stdin 第一行握手</summary>
public sealed record CatBootstrap(string PipeName, string Token);

/// <summary>hello 参数</summary>
public sealed record CatHelloParams(string? Token);

/// <summary>hello 返回</summary>
public sealed record CatHelloResult(string ProtocolVersion, string LauncherVersion);

/// <summary>launch 里的 Minion 参数</summary>
public sealed record CatMinionParams(string? CardFingerprint, string? Variant);

/// <summary>launch 参数</summary>
public sealed record CatLaunchParams(string? OperationId, string? AccountName, bool Dalamud, CatMinionParams? Minion);

/// <summary>inject 参数</summary>
public sealed record CatInjectParams(bool? Dalamud, bool? Minion);

/// <summary>launch / inject 返回</summary>
public sealed record CatAcceptResult(bool Accepted, string? Code = null, string? Message = null)
{
    /// <summary>已接受</summary>
    public static CatAcceptResult Ok() => new(true);

    /// <summary>未接受</summary>
    public static CatAcceptResult Rejected(string code, string message) => new(false, code, message);
}

/// <summary>status 返回</summary>
public sealed record CatStatusResult
(
    string          Stage,
    int?            Pid,
    string?         ProcessStartedAt,
    bool?           Dalamud,
    bool?           Minion
);

/// <summary>JSON-RPC 方法错误, 回给调用方</summary>
public sealed class CatRpcException(int code, string message) : Exception(message)
{
    /// <summary>JSON-RPC 错误码</summary>
    public int Code { get; } = code;

    /// <summary>方法不存在</summary>
    public const int METHOD_NOT_FOUND = -32601;

    /// <summary>参数无效</summary>
    public const int INVALID_PARAMS = -32602;

    /// <summary>内部错误</summary>
    public const int INTERNAL_ERROR = -32603;
}
