// 特征码扫描 —— 复刻 Dalamud SigScanner 的语义, 好让 DCTraveler / ClientStructs 里
// 的特征码原样拿来就能用:
//   - 只扫主模块 (ffxiv_dx11.exe) 的 .text 节
//   - 命中处若是 E8/E9 (call/jmp rel32), 自动跟进目标地址 —— 这正是 ScanText 的行为,
//     CS 里那些以 E8 打头的 MemberFunction 特征码都依赖它
//   - StaticAddress 语义: 取 命中+offset 处的 rel32, 目标 = 命中+offset+4+rel32 (RIP 相对)
#include "MiniModule.h"

#include <cstdint>
#include <vector>

namespace
{
    struct Pattern
    {
        std::vector<uint8_t> bytes;
        std::vector<bool>    mask; // true = 该位需要匹配
    };

    int HexValue(char c)
    {
        if (c >= '0' && c <= '9') return c - '0';
        if (c >= 'a' && c <= 'f') return c - 'a' + 10;
        if (c >= 'A' && c <= 'F') return c - 'A' + 10;
        return -1;
    }

    bool ParsePattern(const char* text, Pattern& out)
    {
        for (const char* cursor = text; *cursor != '\0';)
        {
            if (*cursor == ' ')
            {
                ++cursor;
                continue;
            }

            if (*cursor == '?')
            {
                out.bytes.push_back(0);
                out.mask.push_back(false);

                ++cursor;
                if (*cursor == '?') ++cursor; // "??" 和 "?" 都当通配

                continue;
            }

            const int high = HexValue(cursor[0]);
            const int low  = cursor[1] == '\0' ? -1 : HexValue(cursor[1]);

            if (high < 0 || low < 0)
                return false;

            out.bytes.push_back(static_cast<uint8_t>((high << 4) | low));
            out.mask.push_back(true);
            cursor += 2;
        }

        return !out.bytes.empty();
    }

    // 主模块的 .text 节 —— 只扫这里, 既快又避免踩到不可读的页
    bool TextSection(uint8_t*& begin, size_t& size)
    {
        const auto base = reinterpret_cast<uint8_t*>(GetModuleHandleW(nullptr));
        if (base == nullptr)
            return false;

        const auto dos = reinterpret_cast<IMAGE_DOS_HEADER*>(base);
        if (dos->e_magic != IMAGE_DOS_SIGNATURE)
            return false;

        const auto nt = reinterpret_cast<IMAGE_NT_HEADERS64*>(base + dos->e_lfanew);
        if (nt->Signature != IMAGE_NT_SIGNATURE)
            return false;

        auto* section = IMAGE_FIRST_SECTION(nt);

        for (int i = 0; i < nt->FileHeader.NumberOfSections; ++i, ++section)
        {
            if (memcmp(section->Name, ".text", 5) == 0)
            {
                begin = base + section->VirtualAddress;
                size  = section->Misc.VirtualSize;
                return true;
            }
        }

        return false;
    }

    // 从 .text 节开头扫; limit = 最多数到几个命中就停（1 = 取第一个）。返回第一个命中, count 带回数到的命中个数
    uint8_t* ScanRaw(const Pattern& pattern, size_t limit, size_t& count)
    {
        count = 0;

        uint8_t* begin = nullptr;
        size_t   size  = 0;

        if (!TextSection(begin, size))
            return nullptr;

        const size_t length = pattern.bytes.size();
        if (length == 0 || length > size)
            return nullptr;

        uint8_t* first = nullptr;

        for (size_t i = 0; i + length <= size; ++i)
        {
            bool hit = true;

            for (size_t j = 0; j < length; ++j)
            {
                if (pattern.mask[j] && begin[i + j] != pattern.bytes[j])
                {
                    hit = false;
                    break;
                }
            }

            if (!hit)
                continue;

            if (first == nullptr)
                first = begin + i;

            if (++count >= limit)
                break;
        }

        return first;
    }

    uint8_t* ScanRaw(const Pattern& pattern)
    {
        size_t count = 0;
        return ScanRaw(pattern, 1, count);
    }
}

uintptr_t ModuleBase()
{
    return reinterpret_cast<uintptr_t>(GetModuleHandleW(nullptr));
}

// Dalamud ScanText: 命中处是 E8/E9 就跟进调用/跳转目标
uintptr_t ScanText(const char* signature)
{
    Pattern pattern;
    if (!ParsePattern(signature, pattern))
    {
        LogF("[sigscan] 特征码语法错误: %s", signature);
        return 0;
    }

    uint8_t* hit = ScanRaw(pattern);

    if (hit == nullptr)
    {
        LogF("[sigscan] 未命中: %s", signature);
        return 0;
    }

    if (hit[0] == 0xE8 || hit[0] == 0xE9)
    {
        const int32_t relative = *reinterpret_cast<int32_t*>(hit + 1);
        return reinterpret_cast<uintptr_t>(hit + 5 + relative);
    }

    return reinterpret_cast<uintptr_t>(hit);
}

// 不跟进 E8/E9 的 ScanText: 特征码以通配符开头（函数头被别人改写过）时用, 命中处就是结果
uintptr_t ScanTextRaw(const char* signature)
{
    Pattern pattern;
    if (!ParsePattern(signature, pattern))
    {
        LogF("[sigscan] 特征码语法错误: %s", signature);
        return 0;
    }

    uint8_t* hit = ScanRaw(pattern);

    if (hit == nullptr)
        LogF("[sigscan] 未命中: %s", signature);

    return reinterpret_cast<uintptr_t>(hit);
}

// CS 的 StaticAddress: 命中+offset 处是一条 RIP 相对寻址的 rel32
uintptr_t ScanStaticAddress(const char* signature, int offset)
{
    Pattern pattern;
    if (!ParsePattern(signature, pattern))
    {
        LogF("[sigscan] 特征码语法错误: %s", signature);
        return 0;
    }

    uint8_t* hit = ScanRaw(pattern);

    if (hit == nullptr)
    {
        LogF("[sigscan] 未命中: %s", signature);
        return 0;
    }

    const int32_t relative = *reinterpret_cast<int32_t*>(hit + offset);
    return reinterpret_cast<uintptr_t>(hit + offset + 4 + relative);
}

// 同 ScanStaticAddress, 但扫完整个 .text 节、要求只有一个命中: 多于一个时不取第一个, 当作没命中
uintptr_t ScanStaticAddressUnique(const char* signature, int offset)
{
    Pattern pattern;
    if (!ParsePattern(signature, pattern))
    {
        LogF("[sigscan] 特征码语法错误: %s", signature);
        return 0;
    }

    size_t   count = 0;
    uint8_t* hit   = ScanRaw(pattern, SIZE_MAX, count);

    if (hit == nullptr)
    {
        LogF("[sigscan] 未命中: %s", signature);
        return 0;
    }

    if (count > 1)
    {
        LogF("[sigscan] 命中 %llu 处, 要求唯一, 不用: %s", static_cast<unsigned long long>(count), signature);
        return 0;
    }

    const int32_t relative = *reinterpret_cast<int32_t*>(hit + offset);
    return reinterpret_cast<uintptr_t>(hit + offset + 4 + relative);
}

// 同 ScanText, 但扫完整个 .text 节: count 带回命中个数, 返回第一个命中（E8/E9 跟进后）的地址; 没命中返回 0
uintptr_t ScanTextCount(const char* signature, size_t* count)
{
    if (count != nullptr)
        *count = 0;

    Pattern pattern;
    if (!ParsePattern(signature, pattern))
    {
        LogF("[sigscan] 特征码语法错误: %s", signature);
        return 0;
    }

    size_t   hits = 0;
    uint8_t* hit  = ScanRaw(pattern, SIZE_MAX, hits);

    if (count != nullptr)
        *count = hits;

    if (hit == nullptr)
    {
        LogF("[sigscan] 未命中: %s", signature);
        return 0;
    }

    if (hit[0] == 0xE8 || hit[0] == 0xE9)
    {
        const int32_t relative = *reinterpret_cast<int32_t*>(hit + 1);
        return reinterpret_cast<uintptr_t>(hit + 5 + relative);
    }

    return reinterpret_cast<uintptr_t>(hit);
}

// 地址是否落在主模块的 .text 节里
bool TextSectionContains(uintptr_t address)
{
    uint8_t* begin = nullptr;
    size_t   size  = 0;

    if (!TextSection(begin, size))
        return false;

    const auto start = reinterpret_cast<uintptr_t>(begin);
    return address >= start && address < start + size;
}
