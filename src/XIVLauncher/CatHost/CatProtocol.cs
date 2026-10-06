using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using XIVLauncher.Common.Game;

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

    /// <summary>崩溃对话框默认等待秒数: 游戏已退出而崩溃处理器还开着时, 等这么久没人选择就按不重启处理</summary>
    public const int DEFAULT_CRASH_DIALOG_TIMEOUT_SECONDS = 120;

    /// <summary>崩溃对话框等待秒数上限</summary>
    public const int MAX_CRASH_DIALOG_TIMEOUT_SECONDS = 3600;

    /// <summary>close 默认等游戏自己退出的秒数, 超时后结束进程</summary>
    public const int DEFAULT_CLOSE_TIMEOUT_SECONDS = 15;

    /// <summary>close 等待秒数上限</summary>
    public const int MAX_CLOSE_TIMEOUT_SECONDS = 300;

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

    /// <summary>连不上盛趣登录服务器或网络出错, 可直接重试, 不需要客户</summary>
    public const string NETWORK_ERROR = "networkError";

    /// <summary>盛趣要求客户验证（安全手机短信 / 叨鱼扫码等风控）</summary>
    public const string RISK_CONTROL = "riskControl";

    /// <summary>启动途中收到 close, 没有起游戏</summary>
    public const string CANCELLED = "cancelled";

    /// <summary>上一次补注入还没结束</summary>
    public const string BUSY = "busy";

    /// <summary>这张卡已挂在当前游戏上, 没有重复挂</summary>
    public const string ALREADY_ATTACHED = "alreadyAttached";

    /// <summary>已收到 close, 不再接受 launch</summary>
    public const string CLOSING = "closing";
}

/// <summary>game.exited 的 reason（不带 reason = 游戏自己退出或被外部结束）</summary>
public static class CatExitReasons
{
    /// <summary>按 close 请求关闭</summary>
    public const string CLOSED = "closed";

    /// <summary>崩溃对话框超时没人选择, 按不重启处理</summary>
    public const string CRASH_DIALOG_TIMEOUT = "crashDialogTimeout";

    /// <summary>崩溃后要求重启, 但重启失败（带 code / message）</summary>
    public const string RESTART_FAILED = "restartFailed";

    /// <summary>游戏起来后本进程内部出错, 不再守护（崩溃重启、跨区失效）, 等到游戏结束才发</summary>
    public const string GUARD_ERROR = "guardError";
}

/// <summary>代理类型</summary>
public static class CatAgentKinds
{
    /// <summary>Dalamud</summary>
    public const string DALAMUD = "dalamud";

    /// <summary>Minion</summary>
    public const string MINION = "minion";
}

/// <summary>launch 的 platform: 号是哪个渠道的（决定用账号库里哪种行、哪个游戏目录、怎么登录）</summary>
public static class CatPlatforms
{
    /// <summary>盛趣官服（缺省）</summary>
    public const string SHENGQU = "shengqu";

    /// <summary>WeGame 版国服: 只能启动已在本机界面版登录过一次的号</summary>
    public const string WE_GAME = "weGame";

    /// <summary>国际服（Square Enix 账号, Windows 版）: 用 launch 带来的账号名和密码登录, 不用账号库</summary>
    public const string INTERNATIONAL = "international";

    /// <summary>
    ///     解析 platform: 没带（或为空白）按盛趣, 取值不分大小写; 不认识的取值返回 false
    /// </summary>
    public static bool TryParse(string? platform, out XIVAccountType accountType)
    {
        accountType = XIVAccountType.Sdo;

        if (string.IsNullOrWhiteSpace(platform))
            return true;

        var value = platform.Trim();

        if (string.Equals(value, SHENGQU, StringComparison.OrdinalIgnoreCase))
            return true;

        if (string.Equals(value, WE_GAME, StringComparison.OrdinalIgnoreCase))
        {
            accountType = XIVAccountType.WeGame;
            return true;
        }

        return false;
    }

    /// <summary>
    ///     渠道在日志和消息里的叫法
    /// </summary>
    public static string DisplayName(XIVAccountType accountType) =>
        accountType == XIVAccountType.WeGame ? "WeGame" : "盛趣";

    /// <summary>
    ///     解析 platform（含国际服）: 国服两个渠道的判定沿用 <see cref="TryParse(string?, out XIVAccountType)" />, 另认 international;
    ///     不认识的取值返回 false
    /// </summary>
    public static bool TryParsePlatform(string? platform, out CatPlatform channel)
    {
        channel = CatPlatform.Shengqu;

        if (TryParse(platform, out var accountType))
        {
            channel = accountType == XIVAccountType.WeGame ? CatPlatform.WeGame : CatPlatform.Shengqu;
            return true;
        }

        if (string.Equals(platform!.Trim(), INTERNATIONAL, StringComparison.OrdinalIgnoreCase))
        {
            channel = CatPlatform.International;
            return true;
        }

        return false;
    }

    /// <summary>
    ///     渠道在日志和消息里的叫法（国服两个渠道与 <see cref="DisplayName(XIVAccountType)" /> 相同）
    /// </summary>
    public static string DisplayName(CatPlatform channel) =>
        channel switch
        {
            CatPlatform.International => "国际服",
            CatPlatform.WeGame        => DisplayName(XIVAccountType.WeGame),
            _                         => DisplayName(XIVAccountType.Sdo)
        };
}

/// <summary>
///     launch 的渠道。国际服不进 <see cref="XIVAccountType" />: 那个枚举是国服账号库、登录方式、设置共用的, 加成员会波及界面版
/// </summary>
public enum CatPlatform
{
    /// <summary>盛趣官服</summary>
    Shengqu,

    /// <summary>WeGame 版国服</summary>
    WeGame,

    /// <summary>国际服</summary>
    International
}

/// <summary>stdin 第一行握手</summary>
public sealed record CatBootstrap(string PipeName, string Token);

/// <summary>hello 参数</summary>
public sealed record CatHelloParams(string? Token);

/// <summary>hello 返回</summary>
public sealed record CatHelloResult(string ProtocolVersion, string LauncherVersion);

/// <summary>launch 里的 Minion 参数</summary>
public sealed record CatMinionParams(string? CardFingerprint, string? Variant);

/// <summary>
///     launch 参数; areaName = 资料里的大区名（如 豆豆柴）, 账号库没记这个号的大区时用它;
///     platform = 渠道, 见 <see cref="CatPlatforms" />, 不带按盛趣;
///     password = 国际服的 Square Enix 账号密码（国际服必填, 其它渠道带了也不用）
/// </summary>
public sealed record CatLaunchParams
(
    string?          OperationId,
    string?          AccountName,
    bool             Dalamud,
    CatMinionParams? Minion,
    int?             CrashDialogTimeoutSeconds = null,
    string?          AreaName                  = null,
    string?          Platform                  = null,
    string?          Password                  = null
)
{
    /// <summary>
    ///     record 自动生成的 ToString 会打印所有成员, 这里改成不带密码, 免得哪天被写进日志
    /// </summary>
    public override string ToString() =>
        $"CatLaunchParams {{ OperationId = {OperationId}, AccountName = {AccountName}, Dalamud = {Dalamud}, Minion = {Minion}, " +
        $"CrashDialogTimeoutSeconds = {CrashDialogTimeoutSeconds}, AreaName = {AreaName}, Platform = {Platform}, Password = {(Password == null ? "(无)" : "***")} }}";
}

/// <summary>
///     不该出现在日志里的值（国际服密码）: ToString 恒为 ***, 取原文必须显式调 <see cref="Reveal" />
/// </summary>
public sealed class CatSecret(string value) : IEquatable<CatSecret>
{
    private readonly string value = value ?? throw new ArgumentNullException(nameof(value));

    /// <summary>取原文; 只在真正要用的地方调, 不要把结果写进日志、事件或文件</summary>
    public string Reveal() => value;

    /// <inheritdoc />
    public bool Equals(CatSecret? other) =>
        other != null && string.Equals(value, other.value, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as CatSecret);

    /// <inheritdoc />
    public override int GetHashCode() => 0;

    /// <inheritdoc />
    public override string ToString() => "***";
}

/// <summary>inject 参数; force = 已挂着也重新挂 Minion</summary>
public sealed record CatInjectParams(bool? Dalamud, bool? Minion, bool? Force = null);

/// <summary>close 参数; timeoutSeconds = 请求游戏自己退出后等待的秒数, 超时结束进程</summary>
public sealed record CatCloseParams(int? TimeoutSeconds);

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
