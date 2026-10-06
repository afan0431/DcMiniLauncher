using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XIVLauncher.Common.Game.International;

/// <summary>
///     国际服登录的结果状态（goatcorp Launcher.LoginState 去掉用不到的 Unknown）
/// </summary>
public enum InternationalLoginState
{
    /// <summary>可以启动游戏</summary>
    Ok,

    /// <summary>游戏有补丁要打</summary>
    NeedsPatchGame,

    /// <summary>版本服务器不认本地的 boot 文件（被改过或损坏）, 只能用官方启动器修复或重装</summary>
    NeedsPatchBoot,

    /// <summary>账号没有游戏资格（未购买或点卡 / 月卡到期）</summary>
    NoService,

    /// <summary>账号还没接受用户协议</summary>
    NoTerms
}

/// <summary>
///     国际服登录结果。故意不是 record: 自动生成的 ToString 会把会话值打印出来。
/// </summary>
public sealed class InternationalLoginResult
{
    /// <summary>状态</summary>
    public required InternationalLoginState State { get; init; }

    /// <summary>
    ///     传给游戏 DEV.TestSID 的会话值: 版本上报返回的 X-Patch-Unique-Id（不是 login.send 返回的那个）。只有 <see cref="State" /> 为 Ok / NeedsPatchGame 时有值。
    ///     敏感, 不得写进日志
    /// </summary>
    public string? UniqueId { get; init; }

    /// <summary>账号所属区域, 传给游戏 SYS.Region</summary>
    public int Region { get; init; }

    /// <summary>账号拥有的最高资料片, 传给游戏 DEV.MaxEntitledExpansionID</summary>
    public int MaxExpansion { get; init; }

    /// <inheritdoc />
    public override string ToString() =>
        $"InternationalLoginResult {{ State = {State}, Region = {Region}, MaxExpansion = {MaxExpansion} }}";
}

/// <summary>
///     login.send 成功返回里取出的字段（goatcorp Launcher.OauthLoginResult）。故意不是 record, 理由同上。
/// </summary>
public sealed class InternationalOauthResult
{
    /// <summary>会话值, 只用于版本上报的地址。敏感, 不得写进日志</summary>
    public required string SessionId { get; init; }

    /// <summary>区域</summary>
    public required int Region { get; init; }

    /// <summary>是否已接受用户协议</summary>
    public required bool TermsAccepted { get; init; }

    /// <summary>是否有游戏资格</summary>
    public required bool Playable { get; init; }

    /// <summary>最高资料片</summary>
    public required int MaxExpansion { get; init; }

    /// <inheritdoc />
    public override string ToString() =>
        $"InternationalOauthResult {{ Region = {Region}, TermsAccepted = {TermsAccepted}, Playable = {Playable}, MaxExpansion = {MaxExpansion} }}";
}

/// <summary>
///     frontier 的 login_status.json / gate_status.json（goatcorp GateStatus）
/// </summary>
public sealed class InternationalGateStatus
{
    /// <summary>
    ///     true = 开放; false = 维护中。线上返回的是数字（<c>{"status":1}</c>, 2026-10-06 实测）, goatcorp 用的 Newtonsoft 会把数字宽松转成布尔,
    ///     System.Text.Json 不会, 所以挂了宽松转换器。是否维护只看这一项: 开放时 message 里也可能带着维护文字。
    /// </summary>
    [JsonConverter(typeof(LenientBooleanJsonConverter))]
    public bool Status { get; set; }

    /// <summary>维护说明（开放时也可能有内容, 不能据此判断是否维护）</summary>
    [JsonConverter(typeof(LenientStringListJsonConverter))]
    public List<string>? Message { get; set; }

    /// <summary>相关公告</summary>
    [JsonConverter(typeof(LenientStringListJsonConverter))]
    public List<string>? News { get; set; }
}

/// <summary>
///     布尔值的宽松读取, 与 Newtonsoft 的做法一致: 认 true / false、数字（非 0 为真）、写成字符串的布尔或数字; null 与其它形状按 false。
/// </summary>
public sealed class LenientBooleanJsonConverter : JsonConverter<bool>
{
    /// <inheritdoc />
    public override bool HandleNull => true;

    /// <inheritdoc />
    public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.True:
                return true;

            case JsonTokenType.False:
            case JsonTokenType.Null:
                return false;

            case JsonTokenType.Number:
                return reader.TryGetDouble(out var number) && number != 0;

            case JsonTokenType.String:
                var text = reader.GetString()?.Trim();

                if (bool.TryParse(text, out var parsed))
                    return parsed;

                return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var numeric) && numeric != 0;

            default:
                reader.Skip();
                return false;
        }
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options) =>
        writer.WriteBooleanValue(value);
}

/// <summary>
///     字符串列表的宽松读取: 认数组（元素是字符串、数字、布尔都转成文字, null 与嵌套结构跳过）, 也认单个字符串; null 与其它形状得到 null。
/// </summary>
public sealed class LenientStringListJsonConverter : JsonConverter<List<string>?>
{
    /// <inheritdoc />
    public override List<string>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;

            case JsonTokenType.String:
                return [reader.GetString() ?? string.Empty];

            case JsonTokenType.StartArray:
                var list = new List<string>();

                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    switch (reader.TokenType)
                    {
                        case JsonTokenType.String:
                            list.Add(reader.GetString() ?? string.Empty);
                            break;

                        case JsonTokenType.Number:
                            list.Add(reader.TryGetInt64(out var integer)
                                         ? integer.ToString(CultureInfo.InvariantCulture)
                                         : reader.GetDouble().ToString(CultureInfo.InvariantCulture));
                            break;

                        case JsonTokenType.True:
                            list.Add("true");
                            break;

                        case JsonTokenType.False:
                            list.Add("false");
                            break;

                        default:
                            reader.Skip();
                            break;
                    }
                }

                return list;

            default:
                reader.Skip();
                return null;
        }
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, List<string>? value, JsonSerializerOptions options)
    {
        if (value == null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartArray();

        foreach (var item in value)
            writer.WriteStringValue(item);

        writer.WriteEndArray();
    }
}

/// <summary>
///     Square Enix 拒绝了登录: 密码错、要求一次性密码、账号被锁等。SE 不给机器可读的分类, 只有一段文字。
///     不保存登录页正文（goatcorp 的 OauthLoginException 会把整页写进日志, 这里不写）。
/// </summary>
public sealed class InternationalLoginRejectedException(string? seMessage)
    : Exception(string.IsNullOrWhiteSpace(seMessage) ? "Unknown error" : seMessage)
{
    /// <summary>SE 返回的错误文字原文; 解析不出时为 null</summary>
    public string? SeMessage { get; } = string.IsNullOrWhiteSpace(seMessage) ? null : seMessage;
}

/// <summary>国际服服务器返回了不符合预期的内容, 是哪一种</summary>
public enum InternationalInvalidResponseKind
{
    /// <summary>登录页里取不到表单的隐藏值（页面改版、故障或被要求人机验证）</summary>
    StoredNotFound,

    /// <summary>登录页要求重启启动器（只有 Steam 账号才会走到, 本启动器不支持）</summary>
    RestartupRequested,

    /// <summary>登录成功但返回的字段数量或格式不对</summary>
    LoginReplyMalformed,

    /// <summary>版本服务器表示这个游戏版本已不再提供服务（410）</summary>
    GameVersionGone,

    /// <summary>版本服务器没有给出会话值</summary>
    UniqueIdMissing,

    /// <summary>boot 版本检查返回了错误状态</summary>
    BootCheckFailed
}

/// <summary>
///     国际服服务器返回了不符合预期的内容。不带响应正文（goatcorp 的 InvalidResponseException 带着整页, 界面版会写进日志）。
/// </summary>
public sealed class InternationalInvalidResponseException(InternationalInvalidResponseKind kind, string message) : Exception(message)
{
    /// <summary>是哪一种</summary>
    public InternationalInvalidResponseKind Kind { get; } = kind;
}
