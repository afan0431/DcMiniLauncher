using System.Globalization;
using System.IO;
using System.Text;

namespace XIVLauncher.Minion;

/// <summary>
///     Profiler 的一个配置: 配置名与它打开的插件目录名（顺序照文件里的）
/// </summary>
/// <param name="Name">配置名, 即 <c>profiles</c> 表里的键</param>
/// <param name="Mods">配置里列出的插件目录名</param>
public sealed record ProfilerProfile(string Name, IReadOnlyList<string> Mods)
{
    /// <summary>
    ///     两个配置名与插件列表（含顺序）都相同
    /// </summary>
    public bool SameAs(ProfilerProfile other) =>
        string.Equals(Name, other.Name, StringComparison.Ordinal) && Mods.SequenceEqual(other.Mods, StringComparer.Ordinal);
}

/// <summary>
///     Profiler 插件 <c>LuaMods\Profiler\Settings.lua</c> 的读写。
///     只认这个文件实际用到的 Lua 子集: <c>local tbl = { ... } return tbl</c> 或 <c>return { ... }</c>,
///     表里的值是字符串、数字、布尔、nil 与嵌套表, 键是 <c>Name =</c>、<c>["Name"] =</c>、<c>[数字] =</c> 或省略。
///     只取 <c>currentProfile</c> 与 <c>profiles</c> 两项, 其余各项原样保留; 读不懂就报错, 不猜。
/// </summary>
public sealed class ProfilerSettings
{
    private const string CURRENT_PROFILE_KEY = "currentProfile";

    private const string PROFILES_KEY = "profiles";

    private static readonly HashSet<string> LuaKeywords = new(StringComparer.Ordinal)
    {
        "and", "break", "do", "else", "elseif", "end", "false", "for", "function", "goto", "if", "in",
        "local", "nil", "not", "or", "repeat", "return", "then", "true", "until", "while"
    };

    private readonly Span currentProfileValue;
    private readonly Span profilesValue;
    private readonly int  topTableOpen;

    private ProfilerSettings(string text, string? currentProfile, Span currentProfileValue, IReadOnlyList<ProfilerProfile> profiles, Span profilesValue, int topTableOpen)
    {
        Text                     = text;
        CurrentProfile           = currentProfile;
        Profiles                 = profiles;
        this.currentProfileValue = currentProfileValue;
        this.profilesValue       = profilesValue;
        this.topTableOpen        = topTableOpen;
    }

    /// <summary>
    ///     文件原文
    /// </summary>
    public string Text { get; }

    /// <summary>
    ///     <c>currentProfile</c> 的值; 文件里没有这一项时为 null
    /// </summary>
    public string? CurrentProfile { get; }

    /// <summary>
    ///     <c>profiles</c> 表里的配置, 顺序照文件; 文件里没有这一项时为空
    /// </summary>
    public IReadOnlyList<ProfilerProfile> Profiles { get; }

    /// <summary>
    ///     文件里有 <c>profiles</c> 这一项
    /// </summary>
    public bool HasProfilesTable => profilesValue.Length > 0;

    /// <summary>
    ///     <c>profiles</c> 表的原文（从 <c>{</c> 到 <c>}</c>）; 没有这一项时为 null
    /// </summary>
    public string? ProfilesText => HasProfilesTable ? Text.Substring(profilesValue.Start, profilesValue.Length) : null;

    /// <summary>
    ///     按配置名找配置（区分大小写, 与 Lua 的键一致）; 找不到返回 null
    /// </summary>
    public ProfilerProfile? Find(string name) =>
        Profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));

    /// <summary>
    ///     读文件并解析; 读不懂时抛 <see cref="FormatException" />, 文件不存在时抛对应的 IO 异常
    /// </summary>
    public static ProfilerSettings Load(string path) =>
        Parse(File.ReadAllText(path, new UTF8Encoding(false)));

    /// <summary>
    ///     解析文本; 读不懂时抛 <see cref="FormatException" />
    /// </summary>
    public static ProfilerSettings Parse(string text)
    {
        if (TryParse(text, out var settings, out var error))
            return settings!;

        throw new FormatException(error);
    }

    /// <summary>
    ///     解析文本; 读不懂时返回 false, <paramref name="error" /> 说明哪里不对
    /// </summary>
    public static bool TryParse(string text, out ProfilerSettings? settings, out string? error)
    {
        settings = null;

        try
        {
            settings = new Parser(text).ParseFile();
            error    = null;
            return true;
        }
        catch (FormatException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    ///     只把 <c>currentProfile</c> 的值换成 <paramref name="name" /> 后的全文, 其余字符不动。
    ///     文件里没有这一项时加在最外层表的开头
    /// </summary>
    public string WithCurrentProfile(string name)
    {
        var quoted = Quote(name);

        if (currentProfileValue.Length > 0)
            return string.Concat(Text.AsSpan(0, currentProfileValue.Start), quoted, Text.AsSpan(currentProfileValue.End));

        var newline = NewlineOf(Text);
        var indent  = LineIndentAt(Text, topTableOpen) + "\t";
        var insert  = $"{newline}{indent}{CURRENT_PROFILE_KEY} = {quoted},";
        return Text.Insert(topTableOpen + 1, insert);
    }

    /// <summary>
    ///     把 <c>profiles</c> 表换成 <paramref name="profiles" /> 后的全文, 排版照 Profiler 自己写出的样子; 其余字符不动。
    ///     文件里没有这一项时加在最外层表的开头
    /// </summary>
    public string WithProfiles(IReadOnlyList<ProfilerProfile> profiles)
    {
        var newline = NewlineOf(Text);

        if (profilesValue.Length > 0)
        {
            var indent = LineIndentAt(Text, profilesValue.Start);
            return string.Concat(Text.AsSpan(0, profilesValue.Start), FormatProfiles(profiles, indent, newline), Text.AsSpan(profilesValue.End));
        }

        var topIndent = LineIndentAt(Text, topTableOpen) + "\t";
        var insert    = $"{newline}{topIndent}{PROFILES_KEY} = {newline}{topIndent}{FormatProfiles(profiles, topIndent, newline)},";
        return Text.Insert(topTableOpen + 1, insert);
    }

    /// <summary>
    ///     把 <c>profiles</c> 表原文换成 <paramref name="other" /> 文件里的 <c>profiles</c> 表原文后的全文; 其余字符不动
    /// </summary>
    public string WithProfilesTableOf(ProfilerSettings other)
    {
        if (!other.HasProfilesTable)
            return WithProfiles(other.Profiles);

        if (!HasProfilesTable)
            return WithProfiles(other.Profiles);

        return string.Concat(Text.AsSpan(0, profilesValue.Start), other.ProfilesText, Text.AsSpan(profilesValue.End));
    }

    /// <summary>
    ///     三方合并配置定义: 以 <paramref name="current" />（主目录现在的）为底,
    ///     套上 <paramref name="edited" /> 相对 <paramref name="baseline" />（组装时的快照）的增、改、删。
    ///     没有任何改动时返回 null
    /// </summary>
    public static IReadOnlyList<ProfilerProfile>? Merge(IReadOnlyList<ProfilerProfile> baseline, IReadOnlyList<ProfilerProfile> current, IReadOnlyList<ProfilerProfile> edited)
    {
        var baseByName = ByName(baseline);
        var editByName = ByName(edited);
        var result     = current.ToList();
        var changed    = false;

        // 客户端新增或改过的配置: 覆盖主目录同名的, 没有就加在后面
        foreach (var profile in edited)
        {
            if (baseByName.TryGetValue(profile.Name, out var before) && before.SameAs(profile))
                continue;

            var index = result.FindIndex(p => string.Equals(p.Name, profile.Name, StringComparison.Ordinal));

            if (index >= 0)
            {
                if (result[index].SameAs(profile))
                    continue;

                result[index] = profile;
            }
            else
            {
                result.Add(profile);
            }

            changed = true;
        }

        // 客户端删掉的配置: 主目录里也删掉
        foreach (var profile in baseline)
        {
            if (editByName.ContainsKey(profile.Name))
                continue;

            changed |= result.RemoveAll(p => string.Equals(p.Name, profile.Name, StringComparison.Ordinal)) > 0;
        }

        return changed ? result : null;
    }

    /// <summary>
    ///     两组配置定义完全相同（含顺序）
    /// </summary>
    public static bool SameProfiles(IReadOnlyList<ProfilerProfile> a, IReadOnlyList<ProfilerProfile> b) =>
        a.Count == b.Count && a.Zip(b).All(pair => pair.First.SameAs(pair.Second));

    private static Dictionary<string, ProfilerProfile> ByName(IReadOnlyList<ProfilerProfile> profiles)
    {
        var map = new Dictionary<string, ProfilerProfile>(StringComparer.Ordinal);

        foreach (var profile in profiles)
            map[profile.Name] = profile;

        return map;
    }

    private static string FormatProfiles(IReadOnlyList<ProfilerProfile> profiles, string indent, string newline)
    {
        var inner = indent + "\t";
        var item  = inner + "\t";
        var text  = new StringBuilder();
        text.Append('{').Append(newline);

        foreach (var profile in profiles)
        {
            text.Append(inner).Append(FormatKey(profile.Name)).Append(" = ").Append(newline);
            text.Append(inner).Append('{').Append(newline);

            foreach (var mod in profile.Mods)
                text.Append(item).Append(Quote(mod)).Append(',').Append(newline);

            text.Append(inner).Append("},").Append(newline);
        }

        text.Append(indent).Append('}');
        return text.ToString();
    }

    private static string FormatKey(string name)
    {
        var bare = name.Length > 0
                   && (char.IsAsciiLetter(name[0]) || name[0] == '_')
                   && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')
                   && !LuaKeywords.Contains(name);

        return bare ? name : $"[{Quote(name)}]";
    }

    /// <summary>
    ///     写成 Lua 双引号字符串
    /// </summary>
    internal static string Quote(string value)
    {
        var text = new StringBuilder(value.Length + 2);
        text.Append('"');

        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': text.Append(@"\\"); break;
                case '"':  text.Append("\\\""); break;
                case '\n': text.Append(@"\n"); break;
                case '\r': text.Append(@"\r"); break;
                case '\t': text.Append(@"\t"); break;
                default:
                    if (c < 0x20)
                        text.Append('\\').Append(((int)c).ToString(CultureInfo.InvariantCulture));
                    else
                        text.Append(c);
                    break;
            }
        }

        text.Append('"');
        return text.ToString();
    }

    private static string NewlineOf(string text) =>
        text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    /// <summary>
    ///     某个位置所在行开头的空白
    /// </summary>
    private static string LineIndentAt(string text, int position)
    {
        var lineStart = text.LastIndexOf('\n', Math.Max(0, position - 1)) + 1;
        var end       = lineStart;

        while (end < position && (text[end] == ' ' || text[end] == '\t'))
            end++;

        return text[lineStart..end];
    }

    private readonly record struct Span(int Start, int End)
    {
        public int Length => End - Start;
    }

    /// <summary>
    ///     一个表项: 键（字符串键或标识符键; 其余键为 null）与值的范围
    /// </summary>
    private sealed record Field(string? Key, Span Value, object? Parsed);

    /// <summary>
    ///     表构造式的解析结果: 各表项
    /// </summary>
    private sealed record Table(List<Field> Fields);

    /// <summary>
    ///     手写的递归下降解析器, 只认上面说的子集
    /// </summary>
    private sealed class Parser(string text)
    {
        private int pos;

        public ProfilerSettings ParseFile()
        {
            SkipTrivia();
            int    open;
            Table  table;

            if (TryKeyword("local"))
            {
                var name = ReadIdentifier();
                Expect('=');
                SkipTrivia();
                open  = pos;
                table = ReadTable();
                SkipTrivia();

                if (!TryKeyword("return"))
                    throw Error("表之后应为 return");

                var returned = ReadIdentifier();

                if (returned != name)
                    throw Error($"return 的是 {returned}, 不是 {name}");
            }
            else if (TryKeyword("return"))
            {
                SkipTrivia();
                open  = pos;
                table = ReadTable();
            }
            else
            {
                throw Error("文件应以 local 或 return 开头");
            }

            SkipTrivia();

            if (pos < text.Length)
                throw Error("文件末尾有多余内容");

            string? current      = null;
            var     currentSpan  = default(Span);
            var     profiles     = new List<ProfilerProfile>();
            var     profilesSpan = default(Span);

            foreach (var field in table.Fields)
            {
                if (field.Key == CURRENT_PROFILE_KEY)
                {
                    current     = field.Parsed as string ?? throw new FormatException("currentProfile 不是字符串");
                    currentSpan = field.Value;
                }
                else if (field.Key == PROFILES_KEY)
                {
                    profiles     = ReadProfiles(field.Parsed);
                    profilesSpan = field.Value;
                }
            }

            return new ProfilerSettings(text, current, currentSpan, profiles, profilesSpan, open);
        }

        private static List<ProfilerProfile> ReadProfiles(object? value)
        {
            if (value is not Table table)
                throw new FormatException("profiles 不是表");

            var profiles = new List<ProfilerProfile>();

            foreach (var field in table.Fields)
            {
                if (field.Key is null)
                    throw new FormatException("profiles 里有不带名字的项");

                if (field.Parsed is not Table list)
                    throw new FormatException($"配置 {field.Key} 不是表");

                var mods = new List<string>();

                foreach (var item in list.Fields)
                {
                    if (item.Key is not null || item.Parsed is not string mod)
                        throw new FormatException($"配置 {field.Key} 里有不是插件名的项");

                    mods.Add(mod);
                }

                var existing = profiles.FindIndex(p => p.Name == field.Key);
                var profile  = new ProfilerProfile(field.Key, mods);

                if (existing >= 0)
                    profiles[existing] = profile;
                else
                    profiles.Add(profile);
            }

            return profiles;
        }

        private Table ReadTable()
        {
            Expect('{');
            var fields = new List<Field>();

            while (true)
            {
                SkipTrivia();

                if (Peek() == '}')
                {
                    pos++;
                    return new Table(fields);
                }

                fields.Add(ReadField());
                SkipTrivia();

                var c = Peek();

                if (c is ',' or ';')
                {
                    pos++;
                    continue;
                }

                if (c == '}')
                {
                    pos++;
                    return new Table(fields);
                }

                throw Error("表项之后应为 , 或 }");
            }
        }

        private Field ReadField()
        {
            var c = Peek();

            if (c == '[' && PeekAt(1) != '[' && PeekAt(1) != '=')
            {
                pos++;
                SkipTrivia();
                var keyStart = pos;
                var key      = ReadValue();
                SkipTrivia();
                Expect(']');
                Expect('=');
                SkipTrivia();
                var (value, span) = ReadValueWithSpan();

                if (key is not string && key is not double)
                    throw Error("不认识的键", keyStart);

                return new Field(key as string, span, value);
            }

            if (IsIdentifierStart(c))
            {
                var save = pos;
                var name = ReadIdentifier();
                SkipTrivia();

                if (Peek() == '=' && PeekAt(1) != '=')
                {
                    if (LuaKeywords.Contains(name))
                        throw Error($"关键字 {name} 不能作键", save);

                    pos++;
                    SkipTrivia();
                    var (value, span) = ReadValueWithSpan();
                    return new Field(name, span, value);
                }

                pos = save;
            }

            var (positional, positionalSpan) = ReadValueWithSpan();
            return new Field(null, positionalSpan, positional);
        }

        private (object? Value, Span Span) ReadValueWithSpan()
        {
            var start = pos;
            var value = ReadValue();
            return (value, new Span(start, pos));
        }

        private object? ReadValue()
        {
            var c = Peek();

            if (c == '{')
                return ReadTable();

            if (c is '"' or '\'')
                return ReadString();

            if (c == '-' || char.IsAsciiDigit(c) || (c == '.' && char.IsAsciiDigit(PeekAt(1))))
                return ReadNumber();

            if (IsIdentifierStart(c))
            {
                var start = pos;

                return ReadIdentifier() switch
                {
                    "true"  => true,
                    "false" => false,
                    "nil"   => null,
                    var other => throw Error($"不认识的值 {other}", start)
                };
            }

            throw Error("不认识的值");
        }

        private double ReadNumber()
        {
            var start = pos;

            var negative = Peek() == '-';

            if (negative)
                pos++;

            if (Peek() == '0' && (PeekAt(1) is 'x' or 'X'))
            {
                pos += 2;
                var digitsStart = pos;

                while (char.IsAsciiHexDigit(Peek()))
                    pos++;

                if (!long.TryParse(text.AsSpan(digitsStart, pos - digitsStart), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var hex))
                    throw Error("数字格式不对", start);

                return negative ? -hex : hex;
            }

            while (char.IsAsciiDigit(Peek()) || Peek() is '.' or 'e' or 'E' || ((Peek() is '+' or '-') && PeekAt(-1) is 'e' or 'E'))
                pos++;

            if (!double.TryParse(text.AsSpan(start, pos - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                throw Error("数字格式不对", start);

            return number;
        }

        private string ReadString()
        {
            var start = pos;
            var quote = text[pos++];
            var value = new StringBuilder();

            while (true)
            {
                if (pos >= text.Length)
                    throw Error("字符串没有结束", start);

                var c = text[pos++];

                if (c == quote)
                    return value.ToString();

                if (c is '\n' or '\r')
                    throw Error("字符串里有换行", start);

                if (c != '\\')
                {
                    value.Append(c);
                    continue;
                }

                if (pos >= text.Length)
                    throw Error("字符串没有结束", start);

                var e = text[pos++];

                switch (e)
                {
                    case 'n':  value.Append('\n'); break;
                    case 't':  value.Append('\t'); break;
                    case 'r':  value.Append('\r'); break;
                    case 'a':  value.Append('\a'); break;
                    case 'b':  value.Append('\b'); break;
                    case 'f':  value.Append('\f'); break;
                    case 'v':  value.Append('\v'); break;
                    case '\\': value.Append('\\'); break;
                    case '"':  value.Append('"'); break;
                    case '\'': value.Append('\''); break;
                    case '\n': value.Append('\n'); break;
                    case 'x':
                        if (pos + 2 > text.Length || !int.TryParse(text.AsSpan(pos, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var hex))
                            throw Error("字符串转义不对", pos);

                        value.Append((char)hex);
                        pos += 2;
                        break;
                    default:
                        if (!char.IsAsciiDigit(e))
                            throw Error("字符串转义不对", pos - 1);

                        var digits = 1;
                        var code   = e - '0';

                        while (digits < 3 && char.IsAsciiDigit(Peek()))
                        {
                            code = code * 10 + (text[pos++] - '0');
                            digits++;
                        }

                        if (code > 255)
                            throw Error("字符串转义不对", pos);

                        value.Append((char)code);
                        break;
                }
            }
        }

        private string ReadIdentifier()
        {
            SkipTrivia();
            var start = pos;

            if (!IsIdentifierStart(Peek()))
                throw Error("应为名字");

            while (char.IsAsciiLetterOrDigit(Peek()) || Peek() == '_')
                pos++;

            return text[start..pos];
        }

        private bool TryKeyword(string keyword)
        {
            if (string.CompareOrdinal(text, pos, keyword, 0, keyword.Length) != 0)
                return false;

            var after = PeekAt(keyword.Length);

            if (char.IsAsciiLetterOrDigit(after) || after == '_')
                return false;

            pos += keyword.Length;
            return true;
        }

        private void Expect(char c)
        {
            SkipTrivia();

            if (Peek() != c)
                throw Error($"应为 {c}");

            pos++;
        }

        /// <summary>
        ///     跳过空白与注释（<c>--</c> 行注释、<c>--[[ ]]</c> 块注释）
        /// </summary>
        private void SkipTrivia()
        {
            while (pos < text.Length)
            {
                var c = text[pos];

                if (char.IsWhiteSpace(c) || c == '﻿')
                {
                    pos++;
                    continue;
                }

                if (c == '-' && PeekAt(1) == '-')
                {
                    if (PeekAt(2) == '[' && PeekAt(3) == '[')
                    {
                        var end = text.IndexOf("]]", pos + 4, StringComparison.Ordinal);

                        if (end < 0)
                            throw Error("块注释没有结束");

                        pos = end + 2;
                    }
                    else
                    {
                        var end = text.IndexOf('\n', pos);
                        pos = end < 0 ? text.Length : end + 1;
                    }

                    continue;
                }

                break;
            }
        }

        private static bool IsIdentifierStart(char c) => char.IsAsciiLetter(c) || c == '_';

        private char Peek() => pos < text.Length ? text[pos] : '\0';

        private char PeekAt(int offset) => pos + offset >= 0 && pos + offset < text.Length ? text[pos + offset] : '\0';

        private FormatException Error(string message, int? at = null)
        {
            var position = Math.Min(at ?? pos, text.Length);
            var line     = 1 + text.AsSpan(0, position).Count('\n');
            return new FormatException($"Settings.lua 第 {line} 行: {message}");
        }
    }
}
