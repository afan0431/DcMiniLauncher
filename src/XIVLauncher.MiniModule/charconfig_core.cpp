// 所有角色共用一套游戏配置 · 不依赖游戏的部分（声明与目录契约见 charconfig_core.h）
#include "charconfig_core.h"

#include <bcrypt.h>
#include <shlobj.h>
#include <knownfolders.h>

#include <algorithm>
#include <array>
#include <charconv>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <map>

namespace charconfig
{
    namespace
    {
        // ---- 类别表 -------------------------------------------------------------
        const FileSpec kFiles[kFileCount] = {
            { Category::Hotbar,    "hotbar",    "HOTBAR.DAT",   L"HOTBAR.DAT" },
            { Category::Macro,     "macro",     "MACRO.DAT",    L"MACRO.DAT" },
            { Category::Keybind,   "keybind",   "KEYBIND.DAT",  L"KEYBIND.DAT" },
            { Category::LogFilter, "logFilter", "LOGFLTR.DAT",  L"LOGFLTR.DAT" },
            { Category::Character, "character", "COMMON.DAT",   L"COMMON.DAT" },
            { Category::Keyboard,  "keyboard",  "CONTROL0.DAT", L"CONTROL0.DAT" },
            { Category::Gamepad,   "gamepad",   "CONTROL1.DAT", L"CONTROL1.DAT" },
            { Category::Hud,       "hud",       "ADDON.DAT",    L"ADDON.DAT" },
            { Category::Cards,     "cards",     "GS.DAT",       L"GS.DAT" },
        };

        // ---- ADDON.DAT 常量（Cat 父任务 research/addon-format.md §1、§4）----------
        constexpr size_t kAddonSize       = 106528;
        constexpr size_t kAddonSlotStride = 0xE40;
        constexpr size_t kAddonRecordSize = 32;
        constexpr size_t kAddonMaxSlots   = 4;

        // 随角色自动变化的窗口（addon-format.md §2 实测）: 前两个只变高, 第三个整条都变
        const uint8_t kMaskHeightKeys[][4] = { { 0xd2, 0x00, 0x91, 0xa2 }, { 0xca, 0xea, 0x61, 0x46 } };
        const uint8_t kMaskAllKeys[][4]    = { { 0xbe, 0xb6, 0xd3, 0x06 } };

        constexpr size_t kMaxFileSize = 16 * 1024 * 1024;

        bool IsSeparator(wchar_t c) { return c == L'\\' || c == L'/'; }

        wchar_t AsciiUpper(wchar_t c) { return (c >= L'a' && c <= L'z') ? static_cast<wchar_t>(c - L'a' + L'A') : c; }

        bool EqualsNoCase(const wchar_t* a, size_t length, const wchar_t* b)
        {
            for (size_t i = 0; i < length; ++i)
            {
                if (b[i] == L'\0' || AsciiUpper(a[i]) != AsciiUpper(b[i]))
                    return false;
            }

            return b[length] == L'\0';
        }

        int HexValue(wchar_t c)
        {
            if (c >= L'0' && c <= L'9') return c - L'0';
            if (c >= L'a' && c <= L'f') return c - L'a' + 10;
            if (c >= L'A' && c <= L'F') return c - L'A' + 10;
            return -1;
        }

        // [begin, end) 全是十六进制且 1..maxDigits 位时解析出来
        bool ParseHex(const wchar_t* begin, const wchar_t* end, size_t maxDigits, uint64_t* value)
        {
            const size_t digits = static_cast<size_t>(end - begin);
            if (digits == 0 || digits > maxDigits)
                return false;

            uint64_t result = 0;
            for (const wchar_t* p = begin; p < end; ++p)
            {
                const int digit = HexValue(*p);
                if (digit < 0)
                    return false;
                result = (result << 4) | static_cast<uint64_t>(digit);
            }

            *value = result;
            return true;
        }

        // path[0, separator) 的最后一段是 FFXIV_CHR<16 位十六进制> 时填 cid 与目录
        bool MatchCharacterDirectory(const wchar_t* path, size_t separator, PathMatch* out)
        {
            static const wchar_t kPrefix[] = L"FFXIV_CHR";
            constexpr size_t kPrefixLength = 9;
            constexpr size_t kComponentLength = kPrefixLength + 16;

            if (separator < kComponentLength)
                return false;

            const size_t start = separator - kComponentLength;
            if (start > 0 && !IsSeparator(path[start - 1]))
                return false;

            if (!EqualsNoCase(path + start, kPrefixLength, kPrefix))
                return false;

            uint64_t cid = 0;
            if (!ParseHex(path + start + kPrefixLength, path + separator, 16, &cid))
                return false;

            wchar_t hex[17]{};
            _snwprintf_s(hex, _countof(hex), _TRUNCATE, L"%016llX", static_cast<unsigned long long>(cid));

            out->cid       = cid;
            out->cidHex    = hex;
            out->directory = std::wstring(path, separator);
            return true;
        }

        size_t LastSeparator(const wchar_t* path, size_t length)
        {
            for (size_t i = length; i > 0; --i)
            {
                if (IsSeparator(path[i - 1]))
                    return i - 1;
            }

            return std::wstring::npos;
        }

        uint32_t ReadU32(const uint8_t* b, size_t offset)
        {
            uint32_t value;
            memcpy(&value, b + offset, sizeof(value));
            return value;
        }

        uint16_t ReadU16(const uint8_t* b, size_t offset)
        {
            uint16_t value;
            memcpy(&value, b + offset, sizeof(value));
            return value;
        }

        void SetReason(std::string* reason, const char* format, ...)
        {
            if (reason == nullptr)
                return;

            char buffer[256];
            va_list args;
            va_start(args, format);
            _vsnprintf_s(buffer, sizeof(buffer), _TRUNCATE, format, args);
            va_end(args);
            *reason = buffer;
        }

        // ---- ADDON 记录 ---------------------------------------------------------
        using Record = std::array<uint8_t, kAddonRecordSize>;

        Record MaskedRecord(const uint8_t* source)
        {
            Record record;
            memcpy(record.data(), source, kAddonRecordSize);

            for (const auto& key : kMaskAllKeys)
            {
                if (memcmp(record.data(), key, 4) == 0)
                {
                    memset(record.data() + 4, 0, kAddonRecordSize - 4);
                    return record;
                }
            }

            for (const auto& key : kMaskHeightKeys)
            {
                if (memcmp(record.data(), key, 4) == 0)
                {
                    record[0x16] = 0;
                    record[0x17] = 0;
                    return record;
                }
            }

            return record;
        }

        bool IsZeroRecord(const uint8_t* p)
        {
            for (size_t i = 0; i < kAddonRecordSize; ++i)
            {
                if (p[i] != 0)
                    return false;
            }

            return true;
        }

        // 一份合法 ADDON 拆成比较用的几段
        struct AddonSlot
        {
            Bytes               head;    // 16 字节槽头
            std::vector<Record> records; // 已掩码、已排序
            Bytes               tail;    // 记录之后到槽尾
        };

        struct AddonParts
        {
            Bytes                  header;    // [0, 0x70)
            std::vector<Record>    defaults;  // 默认表, 已掩码、已排序
            Bytes                  hudHeader; // [H, H + 0x40)
            std::vector<AddonSlot> slots;
            Bytes                  trailer;   // 槽区之后到文件尾
        };

        AddonParts SplitAddon(const uint8_t* b, size_t length)
        {
            AddonParts parts;

            const size_t count = ReadU32(b, 0x60);
            const size_t hud   = 0x30 + static_cast<size_t>(ReadU32(b, 0x4c));
            const size_t slots = ReadU16(b, hud + 10);

            parts.header.assign(b, b + 0x70);

            parts.defaults.reserve(count);
            for (size_t i = 0; i < count; ++i)
                parts.defaults.push_back(MaskedRecord(b + 0x70 + i * kAddonRecordSize));
            std::sort(parts.defaults.begin(), parts.defaults.end());

            parts.hudHeader.assign(b + hud, b + hud + 0x40);

            for (size_t s = 0; s < slots; ++s)
            {
                const size_t begin = hud + 0x40 + s * kAddonSlotStride;
                const size_t end   = begin + kAddonSlotStride;

                AddonSlot slot;
                slot.head.assign(b + begin, b + begin + 0x10);

                size_t p = begin + 0x10;
                while (p + kAddonRecordSize <= end && !IsZeroRecord(b + p))
                {
                    slot.records.push_back(MaskedRecord(b + p));
                    p += kAddonRecordSize;
                }
                std::sort(slot.records.begin(), slot.records.end());

                slot.tail.assign(b + p, b + end);
                parts.slots.push_back(std::move(slot));
            }

            const size_t slotsEnd = hud + 0x40 + slots * kAddonSlotStride;
            parts.trailer.assign(b + slotsEnd, b + length);
            return parts;
        }

        std::string KeyHex(const Record& record)
        {
            char buffer[9];
            _snprintf_s(buffer, sizeof(buffer), _TRUNCATE, "%02x%02x%02x%02x", record[0], record[1], record[2], record[3]);
            return buffer;
        }

        // 按键分组比较两组记录, 把不同的键（带前缀）追加到 out
        void DiffRecords(const std::vector<Record>& a, const std::vector<Record>& b, const std::string& prefix,
                         std::vector<std::string>* out)
        {
            std::map<std::string, std::vector<Record>> left;
            std::map<std::string, std::vector<Record>> right;

            for (const auto& record : a) left[KeyHex(record)].push_back(record);
            for (const auto& record : b) right[KeyHex(record)].push_back(record);

            for (const auto& entry : left)
            {
                const auto other = right.find(entry.first);
                if (other == right.end() || other->second != entry.second)
                    out->push_back(prefix + entry.first);
            }

            for (const auto& entry : right)
            {
                if (left.find(entry.first) == left.end())
                    out->push_back(prefix + entry.first);
            }
        }

        // ---- JSON（只够读写本机目录的几个文件）----------------------------------
        struct Json
        {
            enum class Type { Null, Bool, Number, String, Array, Object };

            Type                     type     = Type::Null;
            bool                     boolean  = false;
            bool                     integral = false;
            int64_t                  integer  = 0;
            double                   number   = 0;
            std::string              text;
            std::vector<Json>        items; // Array 的元素, 或 Object 的成员值
            std::vector<std::string> keys;  // Object 的成员名, 与 items 一一对应

            const Json* Get(const char* key) const
            {
                if (type != Type::Object)
                    return nullptr;

                for (size_t i = 0; i < keys.size(); ++i)
                {
                    if (keys[i] == key)
                        return &items[i];
                }

                return nullptr;
            }
        };

        class JsonParser
        {
        public:
            explicit JsonParser(const std::string& source) : m_source(source) {}

            bool Parse(Json* out, std::string* error)
            {
                // 容忍开头的 UTF-8 BOM
                if (m_source.size() >= 3 && memcmp(m_source.data(), "\xEF\xBB\xBF", 3) == 0)
                    m_position = 3;

                SkipSpace();
                if (!Value(out, 0))
                {
                    SetReason(error, "JSON 解析失败（位置 %zu）: %s", m_position, m_error);
                    return false;
                }

                SkipSpace();
                if (m_position != m_source.size())
                {
                    SetReason(error, "JSON 解析失败（位置 %zu）: 结尾有多余内容", m_position);
                    return false;
                }

                return true;
            }

        private:
            const std::string& m_source;
            size_t             m_position = 0;
            const char*        m_error    = "";

            bool Fail(const char* message)
            {
                m_error = message;
                return false;
            }

            bool AtEnd() const { return m_position >= m_source.size(); }
            char Peek() const { return AtEnd() ? '\0' : m_source[m_position]; }

            void SkipSpace()
            {
                while (!AtEnd())
                {
                    const char c = m_source[m_position];
                    if (c != ' ' && c != '\t' && c != '\r' && c != '\n')
                        break;
                    ++m_position;
                }
            }

            bool Literal(const char* word)
            {
                const size_t length = strlen(word);
                if (m_source.compare(m_position, length, word) != 0)
                    return Fail("无法识别的字面量");
                m_position += length;
                return true;
            }

            bool Value(Json* out, int depth)
            {
                if (depth > 32)
                    return Fail("嵌套太深");

                switch (Peek())
                {
                case '{': return Object(out, depth);
                case '[': return Array(out, depth);
                case '"': out->type = Json::Type::String; return String(&out->text);
                case 't': out->type = Json::Type::Bool; out->boolean = true; return Literal("true");
                case 'f': out->type = Json::Type::Bool; out->boolean = false; return Literal("false");
                case 'n': out->type = Json::Type::Null; return Literal("null");
                default:  return Number(out);
                }
            }

            bool Object(Json* out, int depth)
            {
                out->type = Json::Type::Object;
                ++m_position;
                SkipSpace();

                if (Peek() == '}')
                {
                    ++m_position;
                    return true;
                }

                while (true)
                {
                    SkipSpace();
                    if (Peek() != '"')
                        return Fail("成员名应为字符串");

                    std::string key;
                    if (!String(&key))
                        return false;

                    SkipSpace();
                    if (Peek() != ':')
                        return Fail("缺少冒号");
                    ++m_position;
                    SkipSpace();

                    Json member;
                    if (!Value(&member, depth + 1))
                        return false;

                    out->keys.push_back(std::move(key));
                    out->items.push_back(std::move(member));

                    SkipSpace();
                    if (Peek() == ',') { ++m_position; continue; }
                    if (Peek() == '}') { ++m_position; return true; }
                    return Fail("对象里缺少逗号或右花括号");
                }
            }

            bool Array(Json* out, int depth)
            {
                out->type = Json::Type::Array;
                ++m_position;
                SkipSpace();

                if (Peek() == ']')
                {
                    ++m_position;
                    return true;
                }

                while (true)
                {
                    SkipSpace();
                    Json item;
                    if (!Value(&item, depth + 1))
                        return false;
                    out->items.push_back(std::move(item));

                    SkipSpace();
                    if (Peek() == ',') { ++m_position; continue; }
                    if (Peek() == ']') { ++m_position; return true; }
                    return Fail("数组里缺少逗号或右方括号");
                }
            }

            static void AppendUtf8(std::string* out, uint32_t code)
            {
                if (code < 0x80)
                {
                    out->push_back(static_cast<char>(code));
                }
                else if (code < 0x800)
                {
                    out->push_back(static_cast<char>(0xC0 | (code >> 6)));
                    out->push_back(static_cast<char>(0x80 | (code & 0x3F)));
                }
                else if (code < 0x10000)
                {
                    out->push_back(static_cast<char>(0xE0 | (code >> 12)));
                    out->push_back(static_cast<char>(0x80 | ((code >> 6) & 0x3F)));
                    out->push_back(static_cast<char>(0x80 | (code & 0x3F)));
                }
                else
                {
                    out->push_back(static_cast<char>(0xF0 | (code >> 18)));
                    out->push_back(static_cast<char>(0x80 | ((code >> 12) & 0x3F)));
                    out->push_back(static_cast<char>(0x80 | ((code >> 6) & 0x3F)));
                    out->push_back(static_cast<char>(0x80 | (code & 0x3F)));
                }
            }

            bool Hex4(uint32_t* value)
            {
                if (m_position + 4 > m_source.size())
                    return Fail("\\u 转义不完整");

                uint32_t result = 0;
                for (int i = 0; i < 4; ++i)
                {
                    const int digit = HexValue(static_cast<wchar_t>(static_cast<unsigned char>(m_source[m_position++])));
                    if (digit < 0)
                        return Fail("\\u 转义不是十六进制");
                    result = (result << 4) | static_cast<uint32_t>(digit);
                }

                *value = result;
                return true;
            }

            bool String(std::string* out)
            {
                ++m_position;
                out->clear();

                while (true)
                {
                    if (AtEnd())
                        return Fail("字符串没有结束");

                    const char c = m_source[m_position++];
                    if (c == '"')
                        return true;

                    if (static_cast<unsigned char>(c) < 0x20)
                        return Fail("字符串里有控制字符");

                    if (c != '\\')
                    {
                        out->push_back(c);
                        continue;
                    }

                    if (AtEnd())
                        return Fail("转义不完整");

                    const char escape = m_source[m_position++];
                    switch (escape)
                    {
                    case '"':  out->push_back('"'); break;
                    case '\\': out->push_back('\\'); break;
                    case '/':  out->push_back('/'); break;
                    case 'b':  out->push_back('\b'); break;
                    case 'f':  out->push_back('\f'); break;
                    case 'n':  out->push_back('\n'); break;
                    case 'r':  out->push_back('\r'); break;
                    case 't':  out->push_back('\t'); break;
                    case 'u':
                    {
                        uint32_t code = 0;
                        if (!Hex4(&code))
                            return false;

                        if (code >= 0xD800 && code <= 0xDBFF)
                        {
                            uint32_t low = 0;
                            if (m_source.compare(m_position, 2, "\\u") != 0)
                                return Fail("代理对缺少低位");
                            m_position += 2;
                            if (!Hex4(&low) || low < 0xDC00 || low > 0xDFFF)
                                return Fail("代理对低位不合法");
                            code = 0x10000 + ((code - 0xD800) << 10) + (low - 0xDC00);
                        }
                        else if (code >= 0xDC00 && code <= 0xDFFF)
                        {
                            return Fail("孤立的代理对低位");
                        }

                        AppendUtf8(out, code);
                        break;
                    }
                    default:
                        return Fail("未知转义");
                    }
                }
            }

            bool Number(Json* out)
            {
                const size_t start = m_position;
                bool         fraction = false;

                if (Peek() == '-')
                    ++m_position;

                if (!(Peek() >= '0' && Peek() <= '9'))
                    return Fail("无法识别的值");

                while (Peek() >= '0' && Peek() <= '9')
                    ++m_position;

                if (Peek() == '.')
                {
                    fraction = true;
                    ++m_position;
                    if (!(Peek() >= '0' && Peek() <= '9'))
                        return Fail("小数点后没有数字");
                    while (Peek() >= '0' && Peek() <= '9')
                        ++m_position;
                }

                if (Peek() == 'e' || Peek() == 'E')
                {
                    fraction = true;
                    ++m_position;
                    if (Peek() == '+' || Peek() == '-')
                        ++m_position;
                    if (!(Peek() >= '0' && Peek() <= '9'))
                        return Fail("指数没有数字");
                    while (Peek() >= '0' && Peek() <= '9')
                        ++m_position;
                }

                const char* begin = m_source.data() + start;
                const char* end   = m_source.data() + m_position;

                out->type = Json::Type::Number;

                if (!fraction)
                {
                    int64_t value = 0;
                    const auto parsed = std::from_chars(begin, end, value);
                    if (parsed.ec == std::errc() && parsed.ptr == end)
                    {
                        out->integral = true;
                        out->integer  = value;
                        out->number   = static_cast<double>(value);
                        return true;
                    }
                }

                double value = 0;
                const auto parsed = std::from_chars(begin, end, value);
                if (parsed.ec != std::errc() || parsed.ptr != end)
                    return Fail("数字超出范围");

                out->number = value;
                return true;
            }
        };

        // 合法 UTF-8 原样输出, 不合法的字节换成 U+FFFD: 外壳按严格 UTF-8 读
        std::string JsonQuote(const std::string& text)
        {
            std::string result = "\"";
            const auto* bytes  = reinterpret_cast<const unsigned char*>(text.data());
            const size_t length = text.size();

            for (size_t i = 0; i < length;)
            {
                const unsigned char c = bytes[i];

                if (c < 0x80)
                {
                    switch (c)
                    {
                    case '"':  result += "\\\""; break;
                    case '\\': result += "\\\\"; break;
                    case '\n': result += "\\n"; break;
                    case '\r': result += "\\r"; break;
                    case '\t': result += "\\t"; break;
                    default:
                        if (c < 0x20)
                        {
                            char escaped[8];
                            _snprintf_s(escaped, sizeof(escaped), _TRUNCATE, "\\u%04x", c);
                            result += escaped;
                        }
                        else
                        {
                            result.push_back(static_cast<char>(c));
                        }
                    }
                    ++i;
                    continue;
                }

                size_t   need = 0;
                uint32_t code = 0;
                if (c >= 0xC2 && c <= 0xDF)      { need = 1; code = c & 0x1F; }
                else if (c >= 0xE0 && c <= 0xEF) { need = 2; code = c & 0x0F; }
                else if (c >= 0xF0 && c <= 0xF4) { need = 3; code = c & 0x07; }

                bool ok = need > 0 && i + need < length;

                for (size_t k = 1; ok && k <= need; ++k)
                {
                    if (i + k >= length || (bytes[i + k] & 0xC0) != 0x80)
                        ok = false;
                    else
                        code = (code << 6) | (bytes[i + k] & 0x3F);
                }

                if (ok)
                {
                    // 过长编码、代理区、超出 U+10FFFF 都不算合法
                    if ((need == 2 && code < 0x800) || (need == 3 && code < 0x10000) || code > 0x10FFFF ||
                        (code >= 0xD800 && code <= 0xDFFF))
                        ok = false;
                }

                if (ok)
                {
                    result.append(text, i, need + 1);
                    i += need + 1;
                }
                else
                {
                    result += "\xEF\xBF\xBD";
                    ++i;
                }
            }

            result += "\"";
            return result;
        }

        // 空串写 null
        std::string JsonQuoteOrNull(const std::string& text) { return text.empty() ? "null" : JsonQuote(text); }

        std::string JsonSerialize(const Json& value)
        {
            switch (value.type)
            {
            case Json::Type::Null:   return "null";
            case Json::Type::Bool:   return value.boolean ? "true" : "false";
            case Json::Type::String: return JsonQuote(value.text);
            case Json::Type::Number:
            {
                char buffer[64];
                const auto result = value.integral
                    ? std::to_chars(buffer, buffer + sizeof(buffer), value.integer)
                    : std::to_chars(buffer, buffer + sizeof(buffer), value.number);
                return std::string(buffer, result.ptr);
            }
            case Json::Type::Array:
            {
                std::string out = "[";
                for (size_t i = 0; i < value.items.size(); ++i)
                {
                    if (i > 0) out += ",";
                    out += JsonSerialize(value.items[i]);
                }
                return out + "]";
            }
            case Json::Type::Object:
            {
                std::string out = "{";
                for (size_t i = 0; i < value.items.size(); ++i)
                {
                    if (i > 0) out += ",";
                    out += JsonQuote(value.keys[i]) + ":" + JsonSerialize(value.items[i]);
                }
                return out + "}";
            }
            }

            return "null";
        }

        std::string JsonString(const Json* value)
        {
            return (value != nullptr && value->type == Json::Type::String) ? value->text : std::string();
        }

        int64_t JsonInteger(const Json* value)
        {
            return (value != nullptr && value->type == Json::Type::Number && value->integral) ? value->integer : 0;
        }

        bool JsonBool(const Json* value)
        {
            return value != nullptr && value->type == Json::Type::Bool && value->boolean;
        }

        // 把 object 里不在 known 之列的成员拼成「"k":v,"k2":v2」
        std::string ExtraMembers(const Json& object, std::initializer_list<const char*> known)
        {
            std::string out;
            for (size_t i = 0; i < object.keys.size(); ++i)
            {
                bool isKnown = false;
                for (const char* name : known)
                {
                    if (object.keys[i] == name)
                    {
                        isKnown = true;
                        break;
                    }
                }

                if (isKnown)
                    continue;

                if (!out.empty()) out += ",";
                out += JsonQuote(object.keys[i]) + ":" + JsonSerialize(object.items[i]);
            }

            return out;
        }

        std::string Win32Error(const char* what, DWORD code)
        {
            char buffer[160];
            _snprintf_s(buffer, sizeof(buffer), _TRUNCATE, "%s err=%lu", what, code);
            return buffer;
        }

        std::wstring Utf8Path(const std::string& text) { return FromUtf8(text); }

        std::wstring StateFilePath(const std::wstring& root, const std::string& setId)
        {
            return SetDirectory(root, setId) + L"\\state.json";
        }

        std::wstring OutboxFilePath(const std::wstring& root, const std::string& setId, uint64_t seq, const wchar_t* extension)
        {
            wchar_t name[48];
            _snwprintf_s(name, _countof(name), _TRUNCATE, L"\\%020llu%s", static_cast<unsigned long long>(seq), extension);
            return OutboxDirectory(root, setId) + name;
        }

        // 文件名是 20 位十进制 + 扩展名时解析出 seq
        bool ParseOutboxName(const wchar_t* name, const wchar_t* extension, uint64_t* seq)
        {
            const size_t length    = wcslen(name);
            const size_t extLength = wcslen(extension);
            if (length != 20 + extLength || !EqualsNoCase(name + 20, extLength, extension))
                return false;

            uint64_t value = 0;
            for (size_t i = 0; i < 20; ++i)
            {
                if (name[i] < L'0' || name[i] > L'9')
                    return false;
                value = value * 10 + static_cast<uint64_t>(name[i] - L'0');
            }

            *seq = value;
            return true;
        }

        uint64_t MaxOutboxSeq(const std::wstring& directory)
        {
            uint64_t maximum = 0;

            WIN32_FIND_DATAW data;
            const HANDLE find = FindFirstFileW((directory + L"\\*").c_str(), &data);
            if (find == INVALID_HANDLE_VALUE)
                return 0;

            do
            {
                uint64_t seq = 0;
                if (ParseOutboxName(data.cFileName, L".json", &seq) || ParseOutboxName(data.cFileName, L".dat", &seq))
                {
                    if (seq > maximum)
                        maximum = seq;
                }
            } while (FindNextFileW(find, &data));

            FindClose(find);
            return maximum;
        }

        std::string OutboxJson(const OutboxItem& item)
        {
            char head[160];
            _snprintf_s(head, sizeof(head), _TRUNCATE, "{\"schema\":1,\"seq\":%llu,", static_cast<unsigned long long>(item.seq));

            char numbers[160];
            _snprintf_s(numbers, sizeof(numbers), _TRUNCATE, "\"size\":%llu,\"baseVersion\":%lld,",
                        static_cast<unsigned long long>(item.size), static_cast<long long>(item.baseVersion));

            char pid[48];
            _snprintf_s(pid, sizeof(pid), _TRUNCATE, ",\"gamePid\":%lu}", item.gamePid);

            std::string json = head;
            json += "\"name\":" + JsonQuote(item.name) + ",";
            json += "\"sha256\":" + JsonQuote(item.sha256) + ",";
            json += numbers;
            json += "\"cid\":" + JsonQuoteOrNull(item.cid) + ",";
            json += "\"charName\":" + JsonQuoteOrNull(item.charName) + ",";
            json += "\"at\":" + JsonQuoteOrNull(item.at);
            json += pid;
            return json;
        }

        bool ParseOutboxJson(const std::string& text, OutboxItem* out, std::string* error)
        {
            Json root;
            if (!JsonParser(text).Parse(&root, error))
                return false;

            if (root.type != Json::Type::Object)
            {
                SetReason(error, "outbox 项不是对象");
                return false;
            }

            out->seq         = static_cast<uint64_t>(JsonInteger(root.Get("seq")));
            out->name        = JsonString(root.Get("name"));
            out->sha256      = JsonString(root.Get("sha256"));
            out->size        = static_cast<uint64_t>(JsonInteger(root.Get("size")));
            out->baseVersion = JsonInteger(root.Get("baseVersion"));
            out->cid         = JsonString(root.Get("cid"));
            out->charName    = JsonString(root.Get("charName"));
            out->at          = JsonString(root.Get("at"));
            out->gamePid     = static_cast<DWORD>(JsonInteger(root.Get("gamePid")));
            return true;
        }

        std::wstring ParentDirectory(const std::wstring& path)
        {
            const size_t separator = LastSeparator(path.c_str(), path.size());
            return separator == std::wstring::npos ? std::wstring() : path.substr(0, separator);
        }

        // 基线 = 这份内容的可比较哈希; 没有内容（文件不存在）时基线为空串
        void SetBaseline(PrepareOutcome* outcome, const FileSpec& spec, const Bytes* content)
        {
            outcome->hasBaseline = true;
            if (content == nullptr)
            {
                outcome->baseline.clear();
                outcome->baselineBytes.clear();
                return;
            }

            outcome->baseline      = ComparableHash(spec, content->data(), content->size());
            outcome->baselineBytes = *content;
        }
    }

    // ---- 类别与文件 -------------------------------------------------------------
    const FileSpec* AllFiles() { return kFiles; }

    const FileSpec* FindFile(const wchar_t* name, size_t length)
    {
        for (const auto& spec : kFiles)
        {
            if (EqualsNoCase(name, length, spec.fileNameW))
                return &spec;
        }

        return nullptr;
    }

    const FileSpec* FindFile(const char* name)
    {
        for (const auto& spec : kFiles)
        {
            if (_stricmp(name, spec.fileName) == 0)
                return &spec;
        }

        return nullptr;
    }

    const FileSpec* FindCategory(const char* key)
    {
        for (const auto& spec : kFiles)
        {
            if (strcmp(key, spec.key) == 0)
                return &spec;
        }

        return nullptr;
    }

    // ---- 路径匹配 ---------------------------------------------------------------
    bool MatchReadPath(const wchar_t* path, PathMatch* out)
    {
        if (path == nullptr || out == nullptr)
            return false;

        const size_t length    = wcslen(path);
        const size_t separator = LastSeparator(path, length);
        if (separator == std::wstring::npos)
            return false;

        const FileSpec* file = FindFile(path + separator + 1, length - separator - 1);
        if (file == nullptr)
            return false;

        PathMatch match;
        if (!MatchCharacterDirectory(path, separator, &match))
            return false;

        match.file          = file;
        match.characterFile = path;
        *out = std::move(match);
        return true;
    }

    bool MatchWritePath(const wchar_t* path, DWORD pid, PathMatch* out)
    {
        if (path == nullptr || out == nullptr)
            return false;

        const size_t length    = wcslen(path);
        const size_t separator = LastSeparator(path, length);
        if (separator == std::wstring::npos)
            return false;

        const wchar_t* name       = path + separator + 1;
        const size_t   nameLength = length - separator - 1;

        // <NAME>.DAT.new.<十六进制>.<进程号十六进制>: 从尾部往前拆
        size_t pidDot = std::wstring::npos;
        for (size_t i = nameLength; i > 0; --i)
        {
            if (name[i - 1] == L'.') { pidDot = i - 1; break; }
        }
        if (pidDot == std::wstring::npos)
            return false;

        size_t stampDot = std::wstring::npos;
        for (size_t i = pidDot; i > 0; --i)
        {
            if (name[i - 1] == L'.') { stampDot = i - 1; break; }
        }
        if (stampDot == std::wstring::npos || stampDot < 4)
            return false;

        uint64_t writerPid = 0;
        uint64_t stamp     = 0;
        if (!ParseHex(name + pidDot + 1, name + nameLength, 8, &writerPid) ||
            !ParseHex(name + stampDot + 1, name + pidDot, 16, &stamp))
            return false;

        if (!EqualsNoCase(name + stampDot - 4, 4, L".new"))
            return false;

        const size_t    baseLength = stampDot - 4;
        const FileSpec* file       = FindFile(name, baseLength);
        if (file == nullptr || writerPid != pid)
            return false;

        PathMatch match;
        if (!MatchCharacterDirectory(path, separator, &match))
            return false;

        match.file          = file;
        match.characterFile = std::wstring(path, separator + 1) + std::wstring(name, baseLength);
        *out = std::move(match);
        return true;
    }

    // ---- 合法性 -----------------------------------------------------------------
    bool AddonValid(const uint8_t* b, size_t length, std::string* reason)
    {
        if (b == nullptr || length != kAddonSize)
        {
            SetReason(reason, "大小 %zu 字节, 应为 %zu", length, kAddonSize);
            return false;
        }

        if (memcmp(b + 0x10, "ADDN", 4) != 0)
        {
            SetReason(reason, "0x10 处不是 ADDN");
            return false;
        }

        const uint64_t count    = ReadU32(b, 0x60);
        const uint64_t tableLen = ReadU32(b, 0x4c);
        if (tableLen != 0x40 + count * kAddonRecordSize)
        {
            SetReason(reason, "默认表段长 0x%llx 与记录数 %llu 不符", static_cast<unsigned long long>(tableLen),
                      static_cast<unsigned long long>(count));
            return false;
        }

        const uint64_t hud = 0x30 + tableLen;
        if (hud + 0x40 + kAddonMaxSlots * kAddonSlotStride > length)
        {
            SetReason(reason, "HUD 区超出文件（记录数 %llu）", static_cast<unsigned long long>(count));
            return false;
        }

        if (ReadU32(b, static_cast<size_t>(hud)) != 0x10)
        {
            SetReason(reason, "HUD 头不是 0x10");
            return false;
        }

        const uint64_t slots = ReadU16(b, static_cast<size_t>(hud) + 10);
        if (slots < 1 || slots > kAddonMaxSlots)
        {
            SetReason(reason, "HUD 套数 %llu 不在 1..4", static_cast<unsigned long long>(slots));
            return false;
        }

        if (ReadU32(b, 0x40) != hud + 0x40 + slots * kAddonSlotStride - 0x60)
        {
            SetReason(reason, "0x40 处的槽区长度与 HUD 套数不符");
            return false;
        }

        return true;
    }

    bool ValidateFile(const FileSpec& spec, const uint8_t* data, size_t length, std::string* reason)
    {
        if (data == nullptr && length > 0)
        {
            SetReason(reason, "没有内容");
            return false;
        }

        auto sizeIn = [&](size_t minimum, size_t maximum) -> bool
        {
            if (length >= minimum && length <= maximum)
                return true;

            if (minimum == maximum)
                SetReason(reason, "%s 大小 %zu 字节, 应为 %zu", spec.fileName, length, minimum);
            else
                SetReason(reason, "%s 大小 %zu 字节, 应在 %zu–%zu", spec.fileName, length, minimum, maximum);
            return false;
        };

        switch (spec.category)
        {
        case Category::Hotbar:    return sizeIn(204832, 204832);
        case Category::Macro:     return sizeIn(286752, 286752);
        case Category::Keybind:   return sizeIn(24608, 24608);
        case Category::LogFilter: return sizeIn(2080, 2080);
        case Category::Keyboard:
        case Category::Gamepad:   return sizeIn(4201, 4204);
        case Category::Cards:     return sizeIn(681, 682);
        case Category::Hud:
        {
            std::string detail;
            if (AddonValid(data, length, &detail))
                return true;
            SetReason(reason, "ADDON.DAT 结构不对: %s", detail.c_str());
            return false;
        }
        case Category::Character:
            break;
        }

        // COMMON.DAT
        size_t textLength = length;
        if (textLength > 0 && data[textLength - 1] == 0)
            --textLength;

        if (textLength < 2 || textLength > 1024 * 1024)
        {
            SetReason(reason, "COMMON.DAT 大小 %zu 字节不合理", length);
            return false;
        }

        if (memchr(data, 0, textLength) != nullptr)
        {
            SetReason(reason, "COMMON.DAT 中间有 0 字节");
            return false;
        }

        if (MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, reinterpret_cast<const char*>(data),
                                static_cast<int>(textLength), nullptr, 0) == 0)
        {
            SetReason(reason, "COMMON.DAT 不是 UTF-8");
            return false;
        }

        if (data[textLength - 2] != '\r' || data[textLength - 1] != '\n')
        {
            SetReason(reason, "COMMON.DAT 不以 \\r\\n 结尾");
            return false;
        }

        size_t lineNumber = 0;
        for (size_t start = 0; start < textLength;)
        {
            ++lineNumber;

            size_t end = start;
            while (!(data[end] == '\r' && data[end + 1] == '\n'))
            {
                if (data[end] == '\r' || data[end] == '\n')
                {
                    SetReason(reason, "COMMON.DAT 第 %zu 行有单独的换行符", lineNumber);
                    return false;
                }
                ++end;
            }

            const size_t lineLength = end - start;
            if (lineLength > 0)
            {
                const uint8_t* line      = data + start;
                const bool     isSection = line[0] == '<' && line[lineLength - 1] == '>';
                const void*    tab       = memchr(line, '\t', lineLength);

                if (!isSection && (tab == nullptr || tab == line))
                {
                    SetReason(reason, "COMMON.DAT 第 %zu 行不是「键\\t值」或「<分节>」", lineNumber);
                    return false;
                }
            }

            start = end + 2;
        }

        return true;
    }

    // ---- 可比较字节与哈希 -------------------------------------------------------
    Bytes AddonComparable(const uint8_t* b, size_t length)
    {
        if (!AddonValid(b, length))
            return {};

        const AddonParts parts = SplitAddon(b, length);

        Bytes out;
        out.reserve(length);

        auto append       = [&](const Bytes& bytes) { out.insert(out.end(), bytes.begin(), bytes.end()); };
        auto appendRecords = [&](const std::vector<Record>& records)
        {
            for (const auto& record : records)
                out.insert(out.end(), record.begin(), record.end());
        };

        append(parts.header);
        appendRecords(parts.defaults);
        append(parts.hudHeader);
        for (const auto& slot : parts.slots)
        {
            append(slot.head);
            appendRecords(slot.records);
            append(slot.tail);
        }
        append(parts.trailer);
        return out;
    }

    Bytes GsComparable(const uint8_t* data, size_t length)
    {
        size_t end = length;
        while (end > 0 && data[end - 1] == 0)
            --end;
        return Bytes(data, data + end);
    }

    Bytes ComparableBytes(const FileSpec& spec, const uint8_t* data, size_t length)
    {
        if (spec.category == Category::Hud && AddonValid(data, length))
            return AddonComparable(data, length);

        if (spec.category == Category::Cards)
            return GsComparable(data, length);

        return Bytes(data, data + length);
    }

    std::string Sha256Hex(const uint8_t* data, size_t length)
    {
        // 算法句柄只开一次、不关: 模块卸载时在 DllMain 里关它不安全
        static const BCRYPT_ALG_HANDLE algorithm = []
        {
            BCRYPT_ALG_HANDLE handle = nullptr;
            if (!BCRYPT_SUCCESS(BCryptOpenAlgorithmProvider(&handle, BCRYPT_SHA256_ALGORITHM, nullptr, 0)))
                return static_cast<BCRYPT_ALG_HANDLE>(nullptr);
            return handle;
        }();

        if (algorithm == nullptr)
            return {};

        BCRYPT_HASH_HANDLE hash = nullptr;
        if (!BCRYPT_SUCCESS(BCryptCreateHash(algorithm, &hash, nullptr, 0, nullptr, 0, 0)))
            return {};

        bool ok = true;
        for (size_t offset = 0; ok && offset < length;)
        {
            const ULONG chunk = static_cast<ULONG>((length - offset) > 0x10000000 ? 0x10000000 : (length - offset));
            ok = BCRYPT_SUCCESS(BCryptHashData(hash, const_cast<PUCHAR>(data + offset), chunk, 0));
            offset += chunk;
        }

        UCHAR digest[32]{};
        if (ok)
            ok = BCRYPT_SUCCESS(BCryptFinishHash(hash, digest, sizeof(digest), 0));
        BCryptDestroyHash(hash);

        if (!ok)
            return {};

        static const char kDigits[] = "0123456789abcdef";
        std::string hex(64, '0');
        for (size_t i = 0; i < 32; ++i)
        {
            hex[i * 2]     = kDigits[digest[i] >> 4];
            hex[i * 2 + 1] = kDigits[digest[i] & 0x0F];
        }

        return hex;
    }

    std::string ComparableHash(const FileSpec& spec, const uint8_t* data, size_t length)
    {
        const Bytes comparable = ComparableBytes(spec, data, length);
        return Sha256Hex(comparable.data(), comparable.size());
    }

    std::vector<std::string> AddonDiffKeys(const uint8_t* a, size_t aLength, const uint8_t* b, size_t bLength)
    {
        std::vector<std::string> out;
        if (!AddonValid(a, aLength) || !AddonValid(b, bLength))
        {
            out.push_back("invalid");
            return out;
        }

        const AddonParts left  = SplitAddon(a, aLength);
        const AddonParts right = SplitAddon(b, bLength);

        if (left.header != right.header)
            out.push_back("header");

        DiffRecords(left.defaults, right.defaults, "", &out);

        if (left.hudHeader != right.hudHeader)
            out.push_back("hudHeader");

        const size_t slots = left.slots.size() > right.slots.size() ? left.slots.size() : right.slots.size();
        static const AddonSlot kEmpty;
        for (size_t s = 0; s < slots; ++s)
        {
            const AddonSlot& l = s < left.slots.size() ? left.slots[s] : kEmpty;
            const AddonSlot& r = s < right.slots.size() ? right.slots[s] : kEmpty;

            char prefix[16];
            _snprintf_s(prefix, sizeof(prefix), _TRUNCATE, "hud%zu:", s);

            if (l.head != r.head)
                out.push_back(std::string(prefix) + "head");
            DiffRecords(l.records, r.records, prefix, &out);
            if (l.tail != r.tail)
                out.push_back(std::string(prefix) + "tail");
        }

        if (left.trailer != right.trailer)
            out.push_back("tail");

        return out;
    }

    // ---- 文件与目录 -------------------------------------------------------------
    std::string ToUtf8(const std::wstring& text)
    {
        if (text.empty())
            return {};

        const int size = WideCharToMultiByte(CP_UTF8, 0, text.data(), static_cast<int>(text.size()), nullptr, 0, nullptr, nullptr);
        std::string result(static_cast<size_t>(size > 0 ? size : 0), '\0');
        if (size > 0)
            WideCharToMultiByte(CP_UTF8, 0, text.data(), static_cast<int>(text.size()), result.data(), size, nullptr, nullptr);
        return result;
    }

    std::wstring FromUtf8(const std::string& text)
    {
        if (text.empty())
            return {};

        const int size = MultiByteToWideChar(CP_UTF8, 0, text.data(), static_cast<int>(text.size()), nullptr, 0);
        std::wstring result(static_cast<size_t>(size > 0 ? size : 0), L'\0');
        if (size > 0)
            MultiByteToWideChar(CP_UTF8, 0, text.data(), static_cast<int>(text.size()), result.data(), size);
        return result;
    }

    std::string UtcNowIso()
    {
        SYSTEMTIME now;
        GetSystemTime(&now);

        char buffer[32];
        _snprintf_s(buffer, sizeof(buffer), _TRUNCATE, "%04u-%02u-%02uT%02u:%02u:%02uZ",
                    now.wYear, now.wMonth, now.wDay, now.wHour, now.wMinute, now.wSecond);
        return buffer;
    }

    ULONGLONG UnixNowMs()
    {
        FILETIME now;
        GetSystemTimeAsFileTime(&now);

        const ULONGLONG ticks = (static_cast<ULONGLONG>(now.dwHighDateTime) << 32) | now.dwLowDateTime;
        return (ticks - 116444736000000000ULL) / 10000ULL;
    }

    std::wstring DefaultRoot()
    {
        std::wstring base;

        PWSTR known = nullptr;
        if (SUCCEEDED(SHGetKnownFolderPath(FOLDERID_LocalAppData, 0, nullptr, &known)) && known != nullptr)
            base = known;
        if (known != nullptr)
            CoTaskMemFree(known);

        if (base.empty())
        {
            wchar_t buffer[MAX_PATH]{};
            const DWORD length = GetEnvironmentVariableW(L"LOCALAPPDATA", buffer, MAX_PATH);
            if (length > 0 && length < MAX_PATH)
                base = buffer;
        }

        return base + L"\\Cat\\char-config";
    }

    bool ValidSetId(const std::string& setId)
    {
        if (setId.empty() || setId.size() > 64)
            return false;

        for (const char c : setId)
        {
            const bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '-' || c == '_';
            if (!ok)
                return false;
        }

        return true;
    }

    std::wstring SetDirectory(const std::wstring& root, const std::string& setId)
    {
        return root + L"\\sets\\" + Utf8Path(setId);
    }

    std::wstring CurrentFilePath(const std::wstring& root, const std::string& setId, const FileSpec& spec)
    {
        return SetDirectory(root, setId) + L"\\current\\" + spec.fileNameW;
    }

    std::wstring OutboxDirectory(const std::wstring& root, const std::string& setId)
    {
        return SetDirectory(root, setId) + L"\\outbox";
    }

    bool ReadWholeFile(const std::wstring& path, Bytes* out, bool* missing, std::string* error)
    {
        if (missing != nullptr)
            *missing = false;
        out->clear();

        const HANDLE file = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                                        nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE)
        {
            const DWORD code = GetLastError();
            if (missing != nullptr && (code == ERROR_FILE_NOT_FOUND || code == ERROR_PATH_NOT_FOUND))
                *missing = true;
            if (error != nullptr)
                *error = Win32Error("打开失败", code);
            return false;
        }

        LARGE_INTEGER size{};
        if (!GetFileSizeEx(file, &size) || size.QuadPart < 0 || static_cast<ULONGLONG>(size.QuadPart) > kMaxFileSize)
        {
            const DWORD code = GetLastError();
            CloseHandle(file);
            if (error != nullptr)
                *error = size.QuadPart > 0 ? std::string("文件太大") : Win32Error("取大小失败", code);
            return false;
        }

        out->resize(static_cast<size_t>(size.QuadPart));

        size_t total = 0;
        while (total < out->size())
        {
            DWORD read = 0;
            if (!ReadFile(file, out->data() + total, static_cast<DWORD>(out->size() - total), &read, nullptr))
            {
                const DWORD code = GetLastError();
                CloseHandle(file);
                out->clear();
                if (error != nullptr)
                    *error = Win32Error("读取失败", code);
                return false;
            }

            if (read == 0)
                break;
            total += read;
        }

        CloseHandle(file);
        out->resize(total);
        return true;
    }

    bool EnsureDirectory(const std::wstring& path)
    {
        if (path.empty())
            return false;

        const DWORD existing = GetFileAttributesW(path.c_str());
        if (existing != INVALID_FILE_ATTRIBUTES)
            return (existing & FILE_ATTRIBUTE_DIRECTORY) != 0;

        // 逐级建; 盘符、UNC 前缀这类建不了的段失败无妨, 最后看目录在不在
        for (size_t i = 1; i < path.size(); ++i)
        {
            if (IsSeparator(path[i]))
                CreateDirectoryW(path.substr(0, i).c_str(), nullptr);
        }

        CreateDirectoryW(path.c_str(), nullptr);

        const DWORD created = GetFileAttributesW(path.c_str());
        return created != INVALID_FILE_ATTRIBUTES && (created & FILE_ATTRIBUTE_DIRECTORY) != 0;
    }

    bool AtomicWriteFile(const std::wstring& path, const void* data, size_t length, std::string* error)
    {
        static volatile LONG counter = 0;

        wchar_t suffix[64];
        _snwprintf_s(suffix, _countof(suffix), _TRUNCATE, L".cat-tmp.%lu.%ld", GetCurrentProcessId(),
                     InterlockedIncrement(&counter));
        const std::wstring temporary = path + suffix;

        const HANDLE file = CreateFileW(temporary.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE)
        {
            if (error != nullptr)
                *error = Win32Error("建临时文件失败", GetLastError());
            return false;
        }

        const auto* bytes = static_cast<const uint8_t*>(data);
        bool        ok    = true;
        DWORD       code  = 0;

        for (size_t offset = 0; ok && offset < length;)
        {
            DWORD written = 0;
            const DWORD chunk = static_cast<DWORD>((length - offset) > 0x1000000 ? 0x1000000 : (length - offset));
            if (!WriteFile(file, bytes + offset, chunk, &written, nullptr) || written == 0)
            {
                code = GetLastError();
                ok = false;
            }
            offset += written;
        }

        if (ok && !FlushFileBuffers(file))
        {
            code = GetLastError();
            ok = false;
        }

        CloseHandle(file);

        if (!ok)
        {
            DeleteFileW(temporary.c_str());
            if (error != nullptr)
                *error = Win32Error("写临时文件失败", code);
            return false;
        }

        // 目标正被别人以不带 FILE_SHARE_DELETE 的方式打开时, 改名会短暂失败, 稍等重试
        for (int attempt = 0; attempt < 25; ++attempt)
        {
            if (MoveFileExW(temporary.c_str(), path.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH))
                return true;

            code = GetLastError();
            if (code != ERROR_ACCESS_DENIED && code != ERROR_SHARING_VIOLATION && code != ERROR_LOCK_VIOLATION)
                break;
            Sleep(20);
        }

        DeleteFileW(temporary.c_str());
        if (error != nullptr)
            *error = Win32Error("替换文件失败", code);
        return false;
    }

    // ---- 命名互斥体 -------------------------------------------------------------
    SetLock::SetLock(const std::string& setId, DWORD timeoutMs)
    {
        if (!ValidSetId(setId))
        {
            m_error = "配置套编号不合法";
            return;
        }

        const std::wstring name = L"Local\\Cat-CharConfig-" + Utf8Path(setId);
        m_handle = CreateMutexW(nullptr, FALSE, name.c_str());
        if (m_handle == nullptr)
        {
            m_error = Win32Error("建互斥体失败", GetLastError());
            return;
        }

        const DWORD wait = WaitForSingleObject(m_handle, timeoutMs);
        if (wait == WAIT_OBJECT_0 || wait == WAIT_ABANDONED)
        {
            m_acquired = true;
            return;
        }

        m_error = wait == WAIT_TIMEOUT ? std::string("等互斥体超时") : Win32Error("等互斥体失败", GetLastError());
    }

    SetLock::~SetLock()
    {
        if (m_acquired)
            ReleaseMutex(m_handle);
        if (m_handle != nullptr)
            CloseHandle(m_handle);
    }

    // ---- machine.json -----------------------------------------------------------
    bool MachineConfig::FileEnabled(const FileSpec& spec) const
    {
        if (!enabled || !ValidSetId(setId))
            return false;

        for (const auto& key : categories)
        {
            if (key == spec.key)
                return true;
        }

        return false;
    }

    bool ParseMachineConfig(const std::string& json, MachineConfig* out, std::string* error)
    {
        Json root;
        if (!JsonParser(json).Parse(&root, error))
            return false;

        if (root.type != Json::Type::Object)
        {
            SetReason(error, "machine.json 不是对象");
            return false;
        }

        MachineConfig config;
        config.schema        = static_cast<int>(JsonInteger(root.Get("schema")));
        config.enabled       = JsonBool(root.Get("enabled"));
        config.blockedReason = JsonString(root.Get("blockedReason"));
        config.setId         = JsonString(root.Get("setId"));
        config.setName       = JsonString(root.Get("setName"));
        config.updatedAt     = JsonString(root.Get("updatedAt"));

        if (const Json* categories = root.Get("categories"); categories != nullptr && categories->type == Json::Type::Array)
        {
            for (const auto& item : categories->items)
            {
                if (item.type == Json::Type::String)
                    config.categories.push_back(item.text);
            }
        }

        *out = std::move(config);
        return true;
    }

    bool ReadMachineConfig(const std::wstring& root, MachineConfig* out, bool* missing, std::string* error)
    {
        Bytes bytes;
        std::string readError;
        if (!ReadWholeFile(root + L"\\machine.json", &bytes, missing, &readError))
        {
            if (error != nullptr)
                *error = "读 machine.json 失败: " + readError;
            return false;
        }

        std::string parseError;
        if (!ParseMachineConfig(std::string(bytes.begin(), bytes.end()), out, &parseError))
        {
            if (error != nullptr)
                *error = "machine.json " + parseError;
            return false;
        }

        return true;
    }

    MachineConfigCache::MachineConfigCache(std::wstring root, ULONGLONG intervalMs)
        : m_root(std::move(root)), m_intervalMs(intervalMs)
    {
    }

    MachineConfig MachineConfigCache::Get(ULONGLONG nowMs, std::string* error)
    {
        std::lock_guard<std::mutex> lock(m_mutex);

        if (!m_checked || nowMs - m_lastCheck >= m_intervalMs)
        {
            m_checked   = true;
            m_lastCheck = nowMs;

            WIN32_FILE_ATTRIBUTE_DATA attributes{};
            const std::wstring path = m_root + L"\\machine.json";

            if (!GetFileAttributesExW(path.c_str(), GetFileExInfoStandard, &attributes))
            {
                m_present = false;
                m_config  = MachineConfig();
                m_error   = "没有 machine.json";
            }
            else
            {
                const ULONGLONG size = (static_cast<ULONGLONG>(attributes.nFileSizeHigh) << 32) | attributes.nFileSizeLow;
                const bool changed = !m_present || CompareFileTime(&attributes.ftLastWriteTime, &m_writeTime) != 0 || size != m_size;

                if (changed)
                {
                    MachineConfig config;
                    bool          missing = false;
                    std::string   readError;

                    if (ReadMachineConfig(m_root, &config, &missing, &readError))
                    {
                        m_config = std::move(config);
                        m_error.clear();
                        m_present = true;
                    }
                    else
                    {
                        m_config = MachineConfig();
                        m_error  = readError;
                        // 内容解析失败就记住这个版本, 等它再变; 读不到（正在被替换等）下次再试
                        m_present = !missing && readError.find("读 machine.json 失败") != 0;
                    }

                    m_writeTime = attributes.ftLastWriteTime;
                    m_size      = size;
                }
            }
        }

        if (error != nullptr)
            *error = m_error;
        return m_config;
    }

    // ---- state.json -------------------------------------------------------------
    FileState* SetState::Find(const char* name)
    {
        for (auto& file : files)
        {
            if (_stricmp(file.name.c_str(), name) == 0)
                return &file;
        }

        return nullptr;
    }

    const FileState* SetState::Find(const char* name) const
    {
        for (const auto& file : files)
        {
            if (_stricmp(file.name.c_str(), name) == 0)
                return &file;
        }

        return nullptr;
    }

    bool ParseSetState(const std::string& json, SetState* out, std::string* error)
    {
        Json root;
        if (!JsonParser(json).Parse(&root, error))
            return false;

        if (root.type != Json::Type::Object)
        {
            SetReason(error, "state.json 不是对象");
            return false;
        }

        SetState state;
        state.schema    = static_cast<int>(JsonInteger(root.Get("schema")));
        state.extraJson = ExtraMembers(root, { "schema", "files" });

        if (const Json* files = root.Get("files"); files != nullptr)
        {
            if (files->type != Json::Type::Object)
            {
                SetReason(error, "state.json 的 files 不是对象");
                return false;
            }

            for (size_t i = 0; i < files->keys.size(); ++i)
            {
                const Json& entry = files->items[i];
                if (entry.type != Json::Type::Object)
                {
                    SetReason(error, "state.json 的 %s 不是对象", files->keys[i].c_str());
                    return false;
                }

                FileState file;
                file.name      = files->keys[i];
                file.version   = JsonInteger(entry.Get("version"));
                file.sha256    = JsonString(entry.Get("sha256"));
                file.size      = static_cast<uint64_t>(JsonInteger(entry.Get("size")));
                file.pending   = JsonBool(entry.Get("pending"));
                file.extraJson = ExtraMembers(entry, { "version", "sha256", "size", "pending", "source" });

                if (const Json* source = entry.Get("source"); source != nullptr && source->type == Json::Type::Object)
                {
                    file.source.workstation = JsonString(source->Get("workstation"));
                    file.source.cid         = JsonString(source->Get("cid"));
                    file.source.name        = JsonString(source->Get("name"));
                    file.source.at          = JsonString(source->Get("at"));
                }

                state.files.push_back(std::move(file));
            }
        }

        *out = std::move(state);
        return true;
    }

    std::string SerializeSetState(const SetState& state)
    {
        char head[48];
        _snprintf_s(head, sizeof(head), _TRUNCATE, "{\"schema\":%d,\"files\":{", state.schema > 0 ? state.schema : 1);

        std::string json = head;
        for (size_t i = 0; i < state.files.size(); ++i)
        {
            const FileState& file = state.files[i];

            char numbers[128];
            _snprintf_s(numbers, sizeof(numbers), _TRUNCATE, "{\"version\":%lld,", static_cast<long long>(file.version));

            char size[64];
            _snprintf_s(size, sizeof(size), _TRUNCATE, ",\"size\":%llu,\"pending\":%s,",
                        static_cast<unsigned long long>(file.size), file.pending ? "true" : "false");

            if (i > 0) json += ",";
            json += JsonQuote(file.name) + ":" + numbers;
            json += "\"sha256\":" + JsonQuote(file.sha256) + size;
            json += "\"source\":{\"workstation\":" + JsonQuoteOrNull(file.source.workstation) +
                    ",\"cid\":" + JsonQuoteOrNull(file.source.cid) +
                    ",\"name\":" + JsonQuoteOrNull(file.source.name) +
                    ",\"at\":" + JsonQuoteOrNull(file.source.at) + "}";
            if (!file.extraJson.empty())
                json += "," + file.extraJson;
            json += "}";
        }

        json += "}";
        if (!state.extraJson.empty())
            json += "," + state.extraJson;
        json += "}";
        return json;
    }

    bool ReadSetState(const std::wstring& root, const std::string& setId, SetState* out, std::string* error)
    {
        Bytes       bytes;
        bool        missing = false;
        std::string readError;

        if (!ReadWholeFile(StateFilePath(root, setId), &bytes, &missing, &readError))
        {
            if (missing)
            {
                *out = SetState();
                return true;
            }

            if (error != nullptr)
                *error = "读 state.json 失败: " + readError;
            return false;
        }

        std::string parseError;
        if (!ParseSetState(std::string(bytes.begin(), bytes.end()), out, &parseError))
        {
            if (error != nullptr)
                *error = "state.json " + parseError;
            return false;
        }

        return true;
    }

    bool WriteSetState(const std::wstring& root, const std::string& setId, const SetState& state, std::string* error)
    {
        if (!EnsureDirectory(SetDirectory(root, setId)))
        {
            if (error != nullptr)
                *error = Win32Error("建配置套目录失败", GetLastError());
            return false;
        }

        const std::string json = SerializeSetState(state);
        return AtomicWriteFile(StateFilePath(root, setId), json.data(), json.size(), error);
    }

    // ---- outbox -----------------------------------------------------------------
    bool ListOutbox(const std::wstring& root, const std::string& setId, std::vector<OutboxItem>* out, std::string* error)
    {
        out->clear();

        const std::wstring directory = OutboxDirectory(root, setId);

        WIN32_FIND_DATAW data;
        const HANDLE find = FindFirstFileW((directory + L"\\*.json").c_str(), &data);
        if (find == INVALID_HANDLE_VALUE)
        {
            const DWORD code = GetLastError();
            if (code == ERROR_FILE_NOT_FOUND || code == ERROR_PATH_NOT_FOUND)
                return true;
            if (error != nullptr)
                *error = Win32Error("列 outbox 失败", code);
            return false;
        }

        do
        {
            uint64_t seq = 0;
            if (!ParseOutboxName(data.cFileName, L".json", &seq))
                continue;

            Bytes bytes;
            if (!ReadWholeFile(directory + L"\\" + data.cFileName, &bytes, nullptr, nullptr))
                continue;

            OutboxItem item;
            if (!ParseOutboxJson(std::string(bytes.begin(), bytes.end()), &item, nullptr))
                continue;

            item.seq = seq;
            out->push_back(std::move(item));
        } while (FindNextFileW(find, &data));

        FindClose(find);

        std::sort(out->begin(), out->end(), [](const OutboxItem& a, const OutboxItem& b) { return a.seq < b.seq; });
        return true;
    }

    bool ReadOutboxContent(const std::wstring& root, const std::string& setId, uint64_t seq, Bytes* out, std::string* error)
    {
        return ReadWholeFile(OutboxFilePath(root, setId, seq, L".dat"), out, nullptr, error);
    }

    // ---- 发布与放入 -------------------------------------------------------------
    bool PublishFile(const std::wstring& root, const std::string& setId, const FileSpec& spec,
                     const uint8_t* data, size_t length, const PublishSource& source,
                     uint64_t* seq, std::string* error)
    {
        auto fail = [&](const std::string& message)
        {
            if (error != nullptr)
                *error = message;
            return false;
        };

        if (!ValidSetId(setId))
            return fail("配置套编号不合法");

        const std::string sha256 = Sha256Hex(data, length);
        if (sha256.empty())
            return fail("算哈希失败");

        SetLock lock(setId, 10000);
        if (!lock.Acquired())
            return fail(lock.Error());

        SetState    state;
        std::string stepError;
        if (!ReadSetState(root, setId, &state, &stepError))
            return fail(stepError);

        const std::wstring setDirectory    = SetDirectory(root, setId);
        const std::wstring outboxDirectory = OutboxDirectory(root, setId);
        if (!EnsureDirectory(setDirectory + L"\\current") || !EnsureDirectory(outboxDirectory))
            return fail(Win32Error("建配置套目录失败", GetLastError()));

        if (!AtomicWriteFile(CurrentFilePath(root, setId, spec), data, length, &stepError))
            return fail("写 current 失败: " + stepError);

        FileState* file = state.Find(spec.fileName);
        if (file == nullptr)
        {
            FileState created;
            created.name = spec.fileName;
            state.files.push_back(std::move(created));
            file = &state.files.back();
        }

        // 序号按时间, 且严格大于已有项: 外壳按序号上传
        OutboxItem item;
        const uint64_t now  = UnixNowMs();
        const uint64_t next = MaxOutboxSeq(outboxDirectory) + 1;
        item.seq         = now > next ? now : next;
        item.name        = spec.fileName;
        item.sha256      = sha256;
        item.size        = length;
        item.baseVersion = file->version;
        item.cid         = source.cid;
        item.charName    = source.charName;
        item.at          = UtcNowIso();
        item.gamePid     = source.gamePid;

        if (!AtomicWriteFile(OutboxFilePath(root, setId, item.seq, L".dat"), data, length, &stepError))
            return fail("写 outbox 内容失败: " + stepError);

        const std::string itemJson = OutboxJson(item);
        if (!AtomicWriteFile(OutboxFilePath(root, setId, item.seq, L".json"), itemJson.data(), itemJson.size(), &stepError))
        {
            DeleteFileW(OutboxFilePath(root, setId, item.seq, L".dat").c_str());
            return fail("写 outbox 说明失败: " + stepError);
        }

        file->sha256             = sha256;
        file->size               = length;
        file->pending            = true;
        file->source.workstation = std::string();
        file->source.cid         = source.cid;
        file->source.name        = source.charName;
        file->source.at          = item.at;

        if (!WriteSetState(root, setId, state, &stepError))
            return fail("写 state.json 失败: " + stepError);

        if (seq != nullptr)
            *seq = item.seq;
        return true;
    }

    PrepareOutcome PrepareRead(const std::wstring& root, const std::string& setId, const FileSpec& spec,
                               const std::wstring& characterFile, DWORD lockTimeoutMs)
    {
        PrepareOutcome outcome;
        Bytes          latest;
        std::string    latestSha;
        bool           haveLatest = false;

        if (!ValidSetId(setId))
        {
            outcome.result = PrepareResult::Failed;
            outcome.reason = "配置套编号不合法";
        }
        else
        {
            SetLock lock(setId, lockTimeoutMs);
            SetState state;
            std::string stepError;

            if (!lock.Acquired())
            {
                outcome.result = PrepareResult::Failed;
                outcome.reason = lock.Error();
            }
            else if (!ReadSetState(root, setId, &state, &stepError))
            {
                outcome.result = PrepareResult::Failed;
                outcome.reason = stepError;
            }
            else if (const FileState* file = state.Find(spec.fileName); file == nullptr)
            {
                outcome.result = PrepareResult::NoLatest;
                outcome.reason = "这一套还没有这个文件";
            }
            else if (!ReadWholeFile(CurrentFilePath(root, setId, spec), &latest, nullptr, &stepError))
            {
                outcome.result = PrepareResult::NoLatest;
                outcome.reason = "读 current 失败: " + stepError;
            }
            else if (Sha256Hex(latest.data(), latest.size()) != file->sha256)
            {
                outcome.result = PrepareResult::NoLatest;
                outcome.reason = "current 与 state.json 的哈希对不上";
            }
            else if (!ValidateFile(spec, latest.data(), latest.size(), &stepError))
            {
                outcome.result = PrepareResult::LatestInvalid;
                outcome.reason = "最新版不合法: " + stepError;
            }
            else
            {
                haveLatest      = true;
                latestSha       = file->sha256;
                outcome.version = file->version;
                outcome.pending = file->pending;
            }
        }

        Bytes       existing;
        bool        missing = false;
        std::string existingError;
        const bool  haveExisting = ReadWholeFile(characterFile, &existing, &missing, &existingError);

        if (haveLatest)
        {
            if (haveExisting && Sha256Hex(existing.data(), existing.size()) == latestSha)
            {
                outcome.result = PrepareResult::AlreadyLatest;
                SetBaseline(&outcome, spec, &latest);
                return outcome;
            }

            std::string writeError;
            const std::wstring directory = ParentDirectory(characterFile);
            if (!directory.empty() && !EnsureDirectory(directory))
            {
                writeError = Win32Error("建角色目录失败", GetLastError());
            }
            else if (AtomicWriteFile(characterFile, latest.data(), latest.size(), &writeError))
            {
                outcome.result = PrepareResult::Placed;
                SetBaseline(&outcome, spec, &latest);
                return outcome;
            }

            outcome.result = PrepareResult::Failed;
            outcome.reason = "放入失败: " + writeError;
        }

        // 没放入: 基线取角色目录现有的文件; 读不出来（不是不存在）就没有基线, 这次登录不发布
        if (haveExisting)
            SetBaseline(&outcome, spec, &existing);
        else if (missing)
            SetBaseline(&outcome, spec, nullptr);
        else
            outcome.reason += (outcome.reason.empty() ? "" : "; ") + std::string("读角色目录文件失败: ") + existingError;

        return outcome;
    }

    WrittenOutcome HandleWritten(const std::wstring& root, const std::string& setId, const FileSpec& spec,
                                 const std::wstring& characterFile, bool hasBaseline, const std::string& baseline,
                                 const PublishSource& source)
    {
        WrittenOutcome outcome;
        std::string    stepError;
        bool           missing = false;

        if (!ReadWholeFile(characterFile, &outcome.content, &missing, &stepError))
        {
            outcome.result = WrittenResult::ReadFailed;
            outcome.reason = missing ? std::string("写回的文件不存在") : "读写回的文件失败: " + stepError;
            return outcome;
        }

        if (!ValidateFile(spec, outcome.content.data(), outcome.content.size(), &stepError))
        {
            outcome.result = WrittenResult::Invalid;
            outcome.reason = stepError;
            return outcome;
        }

        outcome.hash = ComparableHash(spec, outcome.content.data(), outcome.content.size());

        if (!hasBaseline)
        {
            outcome.result = WrittenResult::NoBaseline;
            outcome.reason = "这次登录没有基线（模块晚于登录接入）";
            return outcome;
        }

        if (outcome.hash == baseline)
        {
            outcome.result = WrittenResult::Unchanged;
            return outcome;
        }

        if (!PublishFile(root, setId, spec, outcome.content.data(), outcome.content.size(), source, &outcome.seq, &stepError))
        {
            outcome.result = WrittenResult::Failed;
            outcome.reason = stepError;
            return outcome;
        }

        outcome.result = WrittenResult::Published;
        return outcome;
    }

    // ---- 心跳 -------------------------------------------------------------------
    bool WriteHeartbeat(const std::wstring& root, DWORD pid, const ModuleHeartbeat& heartbeat, std::string* error)
    {
        const std::wstring directory = root + L"\\modules";
        if (!EnsureDirectory(directory))
        {
            if (error != nullptr)
                *error = Win32Error("建 modules 目录失败", GetLastError());
            return false;
        }

        char head[64];
        _snprintf_s(head, sizeof(head), _TRUNCATE, "{\"schema\":1,\"pid\":%lu,", pid);

        std::string json = head;
        json += "\"moduleVersion\":" + JsonQuoteOrNull(heartbeat.moduleVersion) + ",";
        json += std::string("\"hooked\":") + (heartbeat.hooked ? "true" : "false") + ",";
        json += "\"lastCid\":" + JsonQuoteOrNull(heartbeat.lastCid) + ",";
        json += "\"lastError\":" + JsonQuoteOrNull(heartbeat.lastError) + ",";
        json += "\"updatedAt\":" + JsonQuote(UtcNowIso()) + "}";

        wchar_t name[32];
        _snwprintf_s(name, _countof(name), _TRUNCATE, L"\\%lu.json", pid);
        return AtomicWriteFile(directory + name, json.data(), json.size(), error);
    }
}
