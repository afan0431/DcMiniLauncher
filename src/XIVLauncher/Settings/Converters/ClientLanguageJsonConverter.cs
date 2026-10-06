using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using XIVLauncher.Common;

namespace XIVLauncher.Settings.Converters;

/// <summary>
///     国际服客户端语言的容错读取: 认 0–3 的数字、写成字符串的数字、语言名（Japanese / English / German / French, 不分大小写）;
///     null、别的数字、别的文字、别的形状一律回缺省的英语, 不抛异常 —— 这份配置是国服也在用的, 一个设置项手改写错不能让整份配置被判成损坏。
///     写出时是数字。
/// </summary>
public sealed class ClientLanguageJsonConverter : JsonConverter<ClientLanguage>
{
    /// <summary>缺省语言</summary>
    public const ClientLanguage DEFAULT = ClientLanguage.English;

    /// <inheritdoc />
    public override bool HandleNull => true;

    /// <inheritdoc />
    public override ClientLanguage Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                return reader.TryGetInt32(out var number) ? FromNumber(number) : DEFAULT;

            case JsonTokenType.String:
                var text = reader.GetString()?.Trim();

                if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric))
                    return FromNumber(numeric);

                foreach (var language in Enum.GetValues<ClientLanguage>())
                {
                    if (string.Equals(language.ToString(), text, StringComparison.OrdinalIgnoreCase))
                        return language;
                }

                return DEFAULT;

            case JsonTokenType.Null:
            case JsonTokenType.True:
            case JsonTokenType.False:
                return DEFAULT;

            default:
                reader.Skip();
                return DEFAULT;
        }
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, ClientLanguage value, JsonSerializerOptions options) =>
        writer.WriteNumberValue((int)(Enum.IsDefined(value) ? value : DEFAULT));

    private static ClientLanguage FromNumber(int number) =>
        Enum.IsDefined((ClientLanguage)number) ? (ClientLanguage)number : DEFAULT;
}
