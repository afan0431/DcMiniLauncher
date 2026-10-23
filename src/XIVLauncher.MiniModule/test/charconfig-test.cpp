// 离线测试: 共用游戏配置里不依赖游戏的部分（charconfig_core）
//
//   charconfig-test.exe                                   跑全部测试, 最后一行是汇总, 有失败时退出码 1
//   charconfig-test.exe --hash <NAME.DAT> <文件>...       打印每个文件按 NAME.DAT 校验的结果、原始与可比较哈希
//   charconfig-test.exe --child-publish <根> <setId> <编号> <次数> <事件名>   并发发布测试的子进程
//
// 真实样本组读环境变量 CAT_CHARCONFIG_SAMPLES 指定的目录, 默认 %USERPROFILE%\.cat\shared-config;
// 目录不存在时跳过这一组并打印原因。样本是真实角色数据, 不进仓库。
#include "../charconfig_core.h"

#include <array>
#include <cstdio>
#include <cstring>
#include <set>
#include <string>
#include <vector>

using namespace charconfig;

namespace
{
    int g_passed  = 0;
    int g_failed  = 0;
    int g_skipped = 0;

    void Check(bool condition, const char* description, int line)
    {
        if (condition)
        {
            ++g_passed;
            return;
        }

        ++g_failed;
        printf("  [FAIL] 第 %d 行: %s\n", line, description);
        fflush(stdout);
    }

#define CHECK(condition) Check((condition), #condition, __LINE__)

    void Section(const char* name)
    {
        printf("== %s\n", name);
        fflush(stdout);
    }

    const FileSpec& Spec(Category category) { return AllFiles()[static_cast<size_t>(category)]; }

    // ---- 文件工具 -----------------------------------------------------------------
    bool WriteBytes(const std::wstring& path, const Bytes& bytes)
    {
        const HANDLE file = CreateFileW(path.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE)
            return false;

        DWORD written = 0;
        const BOOL ok = bytes.empty() || WriteFile(file, bytes.data(), static_cast<DWORD>(bytes.size()), &written, nullptr);
        CloseHandle(file);
        return ok && written == bytes.size();
    }

    bool WriteText(const std::wstring& path, const std::string& text) { return WriteBytes(path, Bytes(text.begin(), text.end())); }

    Bytes ReadBytes(const std::wstring& path)
    {
        Bytes bytes;
        ReadWholeFile(path, &bytes, nullptr, nullptr);
        return bytes;
    }

    bool Exists(const std::wstring& path) { return GetFileAttributesW(path.c_str()) != INVALID_FILE_ATTRIBUTES; }

    void RemoveTree(const std::wstring& path)
    {
        WIN32_FIND_DATAW data;
        const HANDLE find = FindFirstFileW((path + L"\\*").c_str(), &data);
        if (find != INVALID_HANDLE_VALUE)
        {
            do
            {
                const std::wstring name = data.cFileName;
                if (name == L"." || name == L"..")
                    continue;

                const std::wstring child = path + L"\\" + name;
                if (data.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY)
                    RemoveTree(child);
                else
                    DeleteFileW(child.c_str());
            } while (FindNextFileW(find, &data));
            FindClose(find);
        }

        RemoveDirectoryW(path.c_str());
    }

    // 目录树里名字带 .cat-tmp. 的文件数（原子写残留的临时文件）
    int CountTemporaries(const std::wstring& path)
    {
        int count = 0;

        WIN32_FIND_DATAW data;
        const HANDLE find = FindFirstFileW((path + L"\\*").c_str(), &data);
        if (find == INVALID_HANDLE_VALUE)
            return 0;

        do
        {
            const std::wstring name = data.cFileName;
            if (name == L"." || name == L"..")
                continue;

            if (data.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY)
                count += CountTemporaries(path + L"\\" + name);
            else if (name.find(L".cat-tmp.") != std::wstring::npos)
                ++count;
        } while (FindNextFileW(find, &data));

        FindClose(find);
        return count;
    }

    int CountFiles(const std::wstring& directory, const wchar_t* pattern)
    {
        int count = 0;

        WIN32_FIND_DATAW data;
        const HANDLE find = FindFirstFileW((directory + L"\\" + pattern).c_str(), &data);
        if (find == INVALID_HANDLE_VALUE)
            return 0;

        do { ++count; } while (FindNextFileW(find, &data));
        FindClose(find);
        return count;
    }

    std::wstring MakeTempRoot(const wchar_t* tag)
    {
        wchar_t temp[MAX_PATH]{};
        GetTempPathW(MAX_PATH, temp);

        wchar_t name[128];
        _snwprintf_s(name, _countof(name), _TRUNCATE, L"cat-charconfig-test-%lu-%s", GetCurrentProcessId(), tag);

        std::wstring root = std::wstring(temp) + name;
        RemoveTree(root);
        EnsureDirectory(root);
        return root;
    }

    std::string Hash(const Bytes& bytes) { return Sha256Hex(bytes.data(), bytes.size()); }

    // ---- 造样本 -------------------------------------------------------------------
    using Record = std::array<uint8_t, 32>;

    Record MakeRecord(uint32_t keyBigEndian, float x, float y, uint16_t width, uint16_t height)
    {
        Record record{};
        record[0] = static_cast<uint8_t>(keyBigEndian >> 24);
        record[1] = static_cast<uint8_t>(keyBigEndian >> 16);
        record[2] = static_cast<uint8_t>(keyBigEndian >> 8);
        record[3] = static_cast<uint8_t>(keyBigEndian);

        const float scale = 1.0f;
        const uint32_t flags = 0x101;
        memcpy(record.data() + 0x04, &x, 4);
        memcpy(record.data() + 0x08, &y, 4);
        memcpy(record.data() + 0x0C, &scale, 4);
        memcpy(record.data() + 0x10, &flags, 4);
        memcpy(record.data() + 0x14, &width, 2);
        memcpy(record.data() + 0x16, &height, 2);
        record[0x18] = 0x11;
        return record;
    }

    void PutU32(Bytes& b, size_t offset, uint32_t value) { memcpy(b.data() + offset, &value, 4); }
    void PutU16(Bytes& b, size_t offset, uint16_t value) { memcpy(b.data() + offset, &value, 2); }

    // 按 addon-format.md §1 拼一份合法 ADDON.DAT
    Bytes MakeAddon(const std::vector<Record>& defaults, const std::vector<std::vector<Record>>& slots, uint16_t current)
    {
        Bytes b(106528, 0);

        const uint8_t head[16] = { 0x00, 0x00, 0x69, 0x00, 0x00, 0xa0, 0x01, 0x00, 0x00, 0xa0, 0x01, 0x00, 0, 0, 0, 0 };
        memcpy(b.data(), head, sizeof(head));
        memcpy(b.data() + 0x10, "ADDN", 4);
        PutU32(b, 0x14, 1);
        PutU32(b, 0x18, 0x20);
        memcpy(b.data() + 0x30, "Default", 7);

        const size_t count = defaults.size();
        const size_t table = 0x40 + count * 32;
        PutU32(b, 0x44, 0x30);
        PutU32(b, 0x4c, static_cast<uint32_t>(table));
        PutU32(b, 0x60, static_cast<uint32_t>(count));
        PutU32(b, 0x64, 0x10);

        for (size_t i = 0; i < count; ++i)
            memcpy(b.data() + 0x70 + i * 32, defaults[i].data(), 32);

        const size_t hud = 0x30 + table;
        PutU32(b, hud, 0x10);
        PutU32(b, hud + 4, 0x70);
        PutU16(b, hud + 8, current);
        PutU16(b, hud + 10, static_cast<uint16_t>(slots.size()));

        for (size_t s = 0; s < slots.size(); ++s)
        {
            const size_t slot = hud + 0x40 + s * 0xE40;
            PutU32(b, slot + 4, 0x40);
            for (size_t i = 0; i < slots[s].size(); ++i)
                memcpy(b.data() + slot + 0x10 + i * 32, slots[s][i].data(), 32);
        }

        PutU32(b, 0x40, static_cast<uint32_t>(hud + 0x40 + slots.size() * 0xE40 - 0x60));
        return b;
    }

    // 默认表: 普通窗口 A/B/C + 三个掩码窗口; 4 套 HUD, 当前第 3 套
    struct AddonFixture
    {
        std::vector<Record>              defaults;
        std::vector<std::vector<Record>> slots;
        uint16_t                         current = 2;

        AddonFixture()
        {
            defaults = {
                MakeRecord(0x11111111, 10.0f, 20.0f, 300, 200),
                MakeRecord(0x22222222, 30.0f, 40.0f, 120, 80),
                MakeRecord(0xd20091a2, 50.0f, 60.0f, 400, 342),
                MakeRecord(0xcaea6146, 70.0f, 10.0f, 376, 180),
                MakeRecord(0xbeb6d306, 15.0f, 25.0f, 220, 140),
                MakeRecord(0x33333333, 80.0f, 90.0f, 64, 64),
            };

            for (int s = 0; s < 4; ++s)
            {
                slots.push_back({
                    MakeRecord(0x11111111, 11.0f + s, 21.0f, 300, 200),
                    MakeRecord(0xd20091a2, 51.0f + s, 61.0f, 400, 342),
                    MakeRecord(0x22222222, 31.0f + s, 41.0f, 120, 80),
                });
            }
        }

        Bytes Build() const { return MakeAddon(defaults, slots, current); }
    };

    void SetHeight(Record& record, uint16_t height) { memcpy(record.data() + 0x16, &height, 2); }

    Bytes Filled(size_t size, uint8_t seed)
    {
        Bytes bytes(size);
        for (size_t i = 0; i < size; ++i)
            bytes[i] = static_cast<uint8_t>(seed + i * 7);
        return bytes;
    }

    Bytes TextBytes(const char* text, size_t length) { return Bytes(text, text + length); }

    // ---- 路径匹配 -------------------------------------------------------------------
    void TestPaths()
    {
        Section("路径匹配");

        const std::wstring base = L"E:\\最终幻想XIV\\game\\My Games\\FINAL FANTASY XIV - A Realm Reborn";
        const std::wstring dir  = base + L"\\FFXIV_CHR00400012DC10855C";

        PathMatch match;
        CHECK(MatchReadPath((dir + L"\\HOTBAR.DAT").c_str(), &match));
        CHECK(match.file == &Spec(Category::Hotbar));
        CHECK(match.cid == 0x00400012DC10855CULL);
        CHECK(match.cidHex == L"00400012DC10855C");
        CHECK(match.directory == dir);
        CHECK(match.characterFile == dir + L"\\HOTBAR.DAT");

        const FileSpec* expected[] = { &Spec(Category::Hotbar), &Spec(Category::Macro), &Spec(Category::Keybind),
                                       &Spec(Category::LogFilter), &Spec(Category::Character), &Spec(Category::Keyboard),
                                       &Spec(Category::Gamepad), &Spec(Category::Hud), &Spec(Category::Cards) };
        const wchar_t* names[] = { L"HOTBAR.DAT", L"MACRO.DAT", L"KEYBIND.DAT", L"LOGFLTR.DAT", L"COMMON.DAT",
                                   L"CONTROL0.DAT", L"CONTROL1.DAT", L"ADDON.DAT", L"GS.DAT" };
        for (size_t i = 0; i < 9; ++i)
        {
            PathMatch each;
            CHECK(MatchReadPath((dir + L"\\" + names[i]).c_str(), &each) && each.file == expected[i]);
        }

        // 游戏给的路径里文件名前是 '/'
        PathMatch slash;
        CHECK(MatchReadPath((base + L"/FFXIV_CHR00400012DC10855C/ADDON.DAT").c_str(), &slash));
        CHECK(slash.file == &Spec(Category::Hud));
        CHECK(slash.directory == base + L"/FFXIV_CHR00400012DC10855C");

        // 大小写
        PathMatch lower;
        CHECK(MatchReadPath((base + L"\\ffxiv_chr00400012dc10855c\\hotbar.dat").c_str(), &lower));
        CHECK(lower.cidHex == L"00400012DC10855C");
        CHECK(lower.characterFile == base + L"\\ffxiv_chr00400012dc10855c\\hotbar.dat");

        // 不共用的文件与不像角色目录的
        const wchar_t* rejected[] = {
            L"\\ACQ.DAT", L"\\GEARSET.DAT", L"\\ITEMODR.DAT", L"\\UISAVE.DAT", L"\\HOTBAR.DAT.old", L"\\HOTBAR.DA",
            L"\\XHOTBAR.DAT", L"\\FFXIV.cfg", L"\\MACROSYS.dat",
        };
        for (const wchar_t* name : rejected)
        {
            PathMatch none;
            CHECK(!MatchReadPath((dir + name).c_str(), &none));
        }

        PathMatch none;
        CHECK(!MatchReadPath((base + L"\\FFXIV_CHR00400012DC10855\\HOTBAR.DAT").c_str(), &none));   // 15 位
        CHECK(!MatchReadPath((base + L"\\FFXIV_CHR00400012DC10855CC\\HOTBAR.DAT").c_str(), &none)); // 17 位
        CHECK(!MatchReadPath((base + L"\\FFXIV_CHR00400012DC1085GC\\HOTBAR.DAT").c_str(), &none));  // 非十六进制
        CHECK(!MatchReadPath((base + L"\\XFFXIV_CHR00400012DC10855C\\HOTBAR.DAT").c_str(), &none));
        CHECK(!MatchReadPath((base + L"\\FFXIV_CFG00400012DC10855C\\HOTBAR.DAT").c_str(), &none));
        CHECK(!MatchReadPath(L"HOTBAR.DAT", &none));
        CHECK(!MatchReadPath(L"FFXIV_CHR00400012DC10855C", &none));
        CHECK(MatchReadPath(L"FFXIV_CHR00400012DC10855C\\GS.DAT", &none)); // 相对路径也认
        CHECK(!MatchReadPath(nullptr, &none));

        // 写: <NAME>.DAT.new.<十六进制>.<进程号十六进制>
        const DWORD pid = 0x10750;
        PathMatch written;
        CHECK(MatchWritePath((dir + L"\\HOTBAR.DAT.new.1a2b3c4d.10750").c_str(), pid, &written));
        CHECK(written.file == &Spec(Category::Hotbar));
        CHECK(written.characterFile == dir + L"\\HOTBAR.DAT");
        CHECK(written.cidHex == L"00400012DC10855C");

        PathMatch upper;
        CHECK(MatchWritePath((dir + L"\\ADDON.DAT.NEW.1A2B3C4D5E6F.10750").c_str(), pid, &upper));
        CHECK(upper.characterFile == dir + L"\\ADDON.DAT");

        PathMatch padded;
        CHECK(MatchWritePath((base + L"/FFXIV_CHR00400012DC10855C/GS.DAT.new.0.00010750").c_str(), pid, &padded));
        CHECK(padded.characterFile == base + L"/FFXIV_CHR00400012DC10855C/GS.DAT");

        PathMatch other;
        CHECK(!MatchWritePath((dir + L"\\HOTBAR.DAT.new.1a2b3c4d.10751").c_str(), pid, &other)); // 别的进程
        CHECK(!MatchWritePath((dir + L"\\HOTBAR.DAT.new.1a2b3c4d").c_str(), 0x1a2b3c4d, &other)); // 缺一段
        CHECK(!MatchWritePath((dir + L"\\HOTBAR.DAT.new.10750").c_str(), pid, &other));
        CHECK(!MatchWritePath((dir + L"\\HOTBAR.DAT.tmp.1a2b3c4d.10750").c_str(), pid, &other));
        CHECK(!MatchWritePath((dir + L"\\ACQ.DAT.new.1a2b3c4d.10750").c_str(), pid, &other));
        CHECK(!MatchWritePath((dir + L"\\HOTBAR.DAT.new.xyz.10750").c_str(), pid, &other));
        CHECK(!MatchWritePath((dir + L"\\HOTBAR.DAT.new.1a2b3c4d.100010750").c_str(), pid, &other)); // 9 位
        CHECK(!MatchWritePath((dir + L"\\HOTBAR.DAT.new..10750").c_str(), pid, &other));
        CHECK(!MatchWritePath((dir + L"\\HOTBAR.DAT").c_str(), pid, &other));
        CHECK(!MatchWritePath((base + L"\\FFXIV_CHR00400012DC1085\\HOTBAR.DAT.new.1.10750").c_str(), pid, &other));
        CHECK(!MatchReadPath((dir + L"\\HOTBAR.DAT.new.1a2b3c4d.10750").c_str(), &other));
    }

    // ---- 合法性 -------------------------------------------------------------------
    void TestValidation()
    {
        Section("文件合法性");

        struct Fixed { Category category; size_t minimum; size_t maximum; };
        const Fixed fixed[] = {
            { Category::Hotbar, 204832, 204832 }, { Category::Macro, 286752, 286752 }, { Category::Keybind, 24608, 24608 },
            { Category::LogFilter, 2080, 2080 },  { Category::Keyboard, 4201, 4204 },  { Category::Gamepad, 4201, 4204 },
            { Category::Cards, 681, 682 },
        };

        for (const auto& entry : fixed)
        {
            const FileSpec& spec = Spec(entry.category);
            std::string reason;

            for (size_t size = entry.minimum; size <= entry.maximum; ++size)
            {
                const Bytes ok = Filled(size, 3);
                CHECK(ValidateFile(spec, ok.data(), ok.size(), &reason));
            }

            const Bytes small = Filled(entry.minimum - 1, 3);
            const Bytes large = Filled(entry.maximum + 1, 3);
            CHECK(!ValidateFile(spec, small.data(), small.size(), &reason) && !reason.empty());
            CHECK(!ValidateFile(spec, large.data(), large.size(), &reason));
            CHECK(!ValidateFile(spec, nullptr, 0, &reason));
        }

        const FileSpec& common = Spec(Category::Character);
        std::string reason;

        const char good[] = "BattleEffectSelf\t0\r\n\r\n<GamePad Settings>\r\nPadMode\t0\r\n\r\n<UI Settings>\r\n名字\t鱼鱼\r\nEmpty\t\r\n";
        const Bytes goodNul = TextBytes(good, sizeof(good)); // 带结尾 0, 与实测一致
        const Bytes goodRaw = TextBytes(good, sizeof(good) - 1);
        CHECK(ValidateFile(common, goodNul.data(), goodNul.size(), &reason));
        CHECK(ValidateFile(common, goodRaw.data(), goodRaw.size(), &reason));

        const char* bad[] = {
            "PadMode\t0",                // 没有 \r\n 结尾
            "PadMode\t0\r\nabc\r\n",     // 没有 tab
            "\tvalue\r\n",               // 键为空
            "a\t1\nb\t2\r\n",            // 单独的 \n
            "a\t1\rb\t2\r\n",            // 单独的 \r
            "\xff\xfe\t1\r\n",           // 不是 UTF-8
            "<Section\r\n",              // 分节没闭合也没有 tab
            "\r",
        };
        for (const char* text : bad)
        {
            const Bytes bytes = TextBytes(text, strlen(text));
            CHECK(!ValidateFile(common, bytes.data(), bytes.size(), &reason));
        }

        const char twoNul[] = "a\t1\r\n\0";
        const Bytes twoNulBytes = TextBytes(twoNul, sizeof(twoNul)); // 结尾两个 0
        CHECK(!ValidateFile(common, twoNulBytes.data(), twoNulBytes.size(), &reason));

        const char innerNul[] = "a\t1\r\n\0b\t2\r\n";
        const Bytes innerNulBytes = TextBytes(innerNul, sizeof(innerNul) - 1);
        CHECK(!ValidateFile(common, innerNulBytes.data(), innerNulBytes.size(), &reason));

        // ADDON 结构
        const FileSpec& addon = Spec(Category::Hud);
        AddonFixture fixture;
        const Bytes valid = fixture.Build();
        CHECK(AddonValid(valid.data(), valid.size()));
        CHECK(ValidateFile(addon, valid.data(), valid.size(), &reason));

        const Bytes empty = MakeAddon({}, { {} }, 0); // 从没挪过窗口的号: 记录数 0、只有 1 套
        CHECK(AddonValid(empty.data(), empty.size()));

        Bytes broken = valid;
        memcpy(broken.data() + 0x10, "ADDX", 4);
        CHECK(!AddonValid(broken.data(), broken.size(), &reason) && !reason.empty());

        broken = valid;
        PutU32(broken, 0x60, static_cast<uint32_t>(fixture.defaults.size() + 1)); // 记录数与段长不符
        CHECK(!AddonValid(broken.data(), broken.size()));

        broken = valid;
        PutU32(broken, 0x60, 0xFFFFFFFF);
        PutU32(broken, 0x4c, 0x40 + 0xFFFFFFE0); // 段长回绕
        CHECK(!AddonValid(broken.data(), broken.size()));

        const size_t hud = 0x30 + 0x40 + fixture.defaults.size() * 32;
        broken = valid;
        PutU32(broken, hud, 0x11);
        CHECK(!AddonValid(broken.data(), broken.size()));

        broken = valid;
        PutU16(broken, hud + 10, 0);
        CHECK(!AddonValid(broken.data(), broken.size()));

        broken = valid;
        PutU16(broken, hud + 10, 5);
        CHECK(!AddonValid(broken.data(), broken.size()));

        broken = valid;
        PutU16(broken, hud + 10, 3); // 套数改了, 0x40 处的长度没跟着改
        CHECK(!AddonValid(broken.data(), broken.size()));

        broken = valid;
        broken.push_back(0);
        CHECK(!AddonValid(broken.data(), broken.size()));
        CHECK(!ValidateFile(addon, broken.data(), broken.size(), &reason));

        // 记录多到 HUD 区超出文件
        std::vector<Record> many(3000, MakeRecord(0x44444444, 1, 1, 1, 1));
        const size_t manyTable = 0x40 + many.size() * 32;
        Bytes overflow(106528, 0);
        memcpy(overflow.data() + 0x10, "ADDN", 4);
        PutU32(overflow, 0x4c, static_cast<uint32_t>(manyTable));
        PutU32(overflow, 0x60, static_cast<uint32_t>(many.size()));
        CHECK(!AddonValid(overflow.data(), overflow.size()));
    }

    // ---- 可比较字节与哈希 -------------------------------------------------------------
    void TestComparable()
    {
        Section("可比较字节与哈希");

        const Bytes nothing;
        CHECK(Sha256Hex(nothing.data(), 0) == "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
        const Bytes abc = TextBytes("abc", 3);
        CHECK(Hash(abc) == "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");

        const FileSpec& addon = Spec(Category::Hud);
        AddonFixture fixture;
        const Bytes original = fixture.Build();
        const Bytes comparable = AddonComparable(original.data(), original.size());
        CHECK(comparable.size() == original.size());

        // 默认表顺序变了、HUD 槽内顺序变了
        {
            AddonFixture changed;
            std::swap(changed.defaults[0], changed.defaults[4]);
            std::swap(changed.defaults[1], changed.defaults[5]);
            std::swap(changed.slots[1][0], changed.slots[1][2]);
            const Bytes bytes = changed.Build();
            CHECK(bytes != original);
            CHECK(AddonComparable(bytes.data(), bytes.size()) == comparable);
            CHECK(ComparableHash(addon, bytes.data(), bytes.size()) == ComparableHash(addon, original.data(), original.size()));
            CHECK(AddonDiffKeys(original.data(), original.size(), bytes.data(), bytes.size()).empty());
        }

        // 两个伸缩窗口的高（默认表与当前那套）、自动弹出的窗口整条
        {
            AddonFixture changed;
            SetHeight(changed.defaults[2], 156);
            SetHeight(changed.defaults[3], 204);
            SetHeight(changed.slots[2][1], 103);
            changed.defaults[4] = MakeRecord(0xbeb6d306, 99.0f, 1.0f, 10, 10);
            const Bytes bytes = changed.Build();
            CHECK(bytes != original);
            CHECK(AddonComparable(bytes.data(), bytes.size()) == comparable);
        }

        // 伸缩窗口的宽变了不算自动变化
        {
            AddonFixture changed;
            changed.defaults[2] = MakeRecord(0xd20091a2, 50.0f, 60.0f, 401, 342);
            const Bytes bytes = changed.Build();
            CHECK(AddonComparable(bytes.data(), bytes.size()) != comparable);
        }

        // 挪了普通窗口
        {
            AddonFixture changed;
            changed.defaults[1] = MakeRecord(0x22222222, 31.0f, 40.0f, 120, 80);
            const Bytes bytes = changed.Build();
            CHECK(AddonComparable(bytes.data(), bytes.size()) != comparable);

            const auto keys = AddonDiffKeys(original.data(), original.size(), bytes.data(), bytes.size());
            CHECK(keys.size() == 1 && keys[0] == "22222222");
        }

        // 挪了第 2 套 HUD 里的窗口
        {
            AddonFixture changed;
            changed.slots[1][2] = MakeRecord(0x22222222, 0.0f, 0.0f, 120, 80);
            const Bytes bytes = changed.Build();
            CHECK(AddonComparable(bytes.data(), bytes.size()) != comparable);

            const auto keys = AddonDiffKeys(original.data(), original.size(), bytes.data(), bytes.size());
            CHECK(keys.size() == 1 && keys[0] == "hud1:22222222");
        }

        // 换了当前 HUD 套
        {
            AddonFixture changed;
            changed.current = 0;
            const Bytes bytes = changed.Build();
            CHECK(AddonComparable(bytes.data(), bytes.size()) != comparable);

            const auto keys = AddonDiffKeys(original.data(), original.size(), bytes.data(), bytes.size());
            CHECK(keys.size() == 1 && keys[0] == "hudHeader");
        }

        // 多了一个窗口
        {
            AddonFixture changed;
            changed.defaults.push_back(MakeRecord(0x55555555, 1.0f, 2.0f, 3, 4));
            const Bytes bytes = changed.Build();
            CHECK(AddonValid(bytes.data(), bytes.size()));
            CHECK(AddonComparable(bytes.data(), bytes.size()) != comparable);

            const auto keys = AddonDiffKeys(original.data(), original.size(), bytes.data(), bytes.size());
            bool hasKey = false;
            for (const auto& key : keys) hasKey = hasKey || key == "55555555";
            CHECK(hasKey);
        }

        // 不合法时 AddonComparable 为空, ComparableBytes 退回原字节
        Bytes broken = original;
        memcpy(broken.data() + 0x10, "XXXX", 4);
        CHECK(AddonComparable(broken.data(), broken.size()).empty());
        CHECK(ComparableBytes(addon, broken.data(), broken.size()) == broken);

        // GS 去掉末尾的 0
        const FileSpec& cards = Spec(Category::Cards);
        Bytes gs = Filled(681, 9);
        gs[680] = 0;
        Bytes gsLonger = gs;
        gsLonger.push_back(0);
        CHECK(GsComparable(gs.data(), gs.size()) == GsComparable(gsLonger.data(), gsLonger.size()));
        CHECK(ComparableHash(cards, gs.data(), gs.size()) == ComparableHash(cards, gsLonger.data(), gsLonger.size()));
        Bytes gsChanged = gs;
        gsChanged[100] ^= 1;
        CHECK(ComparableHash(cards, gs.data(), gs.size()) != ComparableHash(cards, gsChanged.data(), gsChanged.size()));

        // 其余类别哈希原字节
        const FileSpec& hotbar = Spec(Category::Hotbar);
        const Bytes bar = Filled(204832, 1);
        CHECK(ComparableHash(hotbar, bar.data(), bar.size()) == Hash(bar));
    }

    // ---- machine.json -------------------------------------------------------------
    void TestMachineConfig()
    {
        Section("machine.json");

        const std::string json =
            "\xEF\xBB\xBF{ \"schema\": 1, \"enabled\": true, \"blockedReason\": null,\r\n"
            "  \"setId\": \"3f2a9c1e-0b7d-4e5a-9c3f-123456789abc\", \"setName\": \"\\u6302\\u673a统一 \\\"A\\\" \\ud83d\\ude00\",\n"
            "  \"categories\": [\"hotbar\",\"macro\",\"keybind\",\"logFilter\",\"character\",\"keyboard\",\"hud\",\"cards\", 3],\n"
            "  \"updatedAt\": \"2026-10-24T03:31:00Z\", \"future\": {\"x\": [1, 2.5e3, -7, false]} }";

        MachineConfig config;
        std::string error;
        CHECK(ParseMachineConfig(json, &config, &error));
        CHECK(config.schema == 1);
        CHECK(config.enabled);
        CHECK(config.blockedReason.empty());
        CHECK(config.setId == "3f2a9c1e-0b7d-4e5a-9c3f-123456789abc");
        CHECK(config.setName == "挂机统一 \"A\" \xF0\x9F\x98\x80");
        CHECK(config.categories.size() == 8);
        CHECK(config.updatedAt == "2026-10-24T03:31:00Z");
        CHECK(config.FileEnabled(Spec(Category::Hotbar)));
        CHECK(config.FileEnabled(Spec(Category::Hud)));
        CHECK(!config.FileEnabled(Spec(Category::Gamepad))); // 没列 gamepad

        MachineConfig off = config;
        off.enabled = false;
        CHECK(!off.FileEnabled(Spec(Category::Hotbar)));

        MachineConfig evil = config;
        evil.setId = "..\\..\\x";
        CHECK(!evil.FileEnabled(Spec(Category::Hotbar)));

        CHECK(ValidSetId("abc_DEF-123"));
        CHECK(!ValidSetId(""));
        CHECK(!ValidSetId("a/b"));
        CHECK(!ValidSetId("a.b"));
        CHECK(!ValidSetId(std::string(65, 'a')));

        MachineConfig blocked;
        CHECK(ParseMachineConfig("{\"schema\":1,\"enabled\":false,\"blockedReason\":\"CharacterSync 开着\",\"setId\":null,\"categories\":[]}", &blocked, &error));
        CHECK(!blocked.enabled && blocked.blockedReason == "CharacterSync 开着" && blocked.setId.empty());

        const char* invalid[] = {
            "", "[]x", "{\"a\":1,}", "{\"a\":\"unterminated}", "{\"a\":01x}", "{\"a\":tru}", "{\"a\":\"\\ud83d\"}",
            "{\"a\":\"\\q\"}", "{\"a\" 1}", "{\"a\":\"line\nbreak\"}", "1",
            "{\"a\":[1,2}", "{\"a\":1e}", "{\"a\":-}",
        };
        for (const char* text : invalid)
        {
            MachineConfig ignored;
            CHECK(!ParseMachineConfig(text, &ignored, &error));
        }
        MachineConfig number;
        CHECK(!ParseMachineConfig("1", &number, &error)); // 不是对象

        // 缓存: 最多每秒查一次修改时间
        const std::wstring root = MakeTempRoot(L"machine");
        MachineConfigCache cache(root, 1000);

        MachineConfig got = cache.Get(10000, &error);
        CHECK(!got.enabled && !error.empty());

        CHECK(WriteText(root + L"\\machine.json", "{\"schema\":1,\"enabled\":true,\"setId\":\"set1\",\"categories\":[\"hotbar\"]}"));
        got = cache.Get(10500, &error);
        CHECK(!got.enabled); // 不到 1 秒, 还没去看
        got = cache.Get(11000, &error);
        CHECK(got.enabled && got.setId == "set1" && error.empty());

        CHECK(WriteText(root + L"\\machine.json", "{\"schema\":1,\"enabled\":true,\"setId\":\"set-two\",\"categories\":[\"hotbar\",\"macro\"]}"));
        got = cache.Get(11999, &error);
        CHECK(got.setId == "set1");
        got = cache.Get(12000, &error);
        CHECK(got.setId == "set-two" && got.categories.size() == 2);

        CHECK(WriteText(root + L"\\machine.json", "{\"schema\":1,\"enabled\":tr"));
        got = cache.Get(13000, &error);
        CHECK(!got.enabled && !error.empty());
        got = cache.Get(14000, &error);
        CHECK(!got.enabled && !error.empty());

        DeleteFileW((root + L"\\machine.json").c_str());
        got = cache.Get(15000, &error);
        CHECK(!got.enabled && error == "没有 machine.json");

        RemoveTree(root);
    }

    // ---- state.json 与 outbox ------------------------------------------------------
    void TestStateAndOutbox()
    {
        Section("state.json 与 outbox 读写");

        const std::wstring root  = MakeTempRoot(L"state");
        const std::string  setId = "set-a";
        std::string        error;

        SetState state;
        CHECK(ReadSetState(root, setId, &state, &error)); // 不存在 = 空
        CHECK(state.files.empty());

        // 往返, 且保留不认识的字段
        const std::string withExtras =
            "{\"schema\":1,\"lastSync\":123,\"files\":{\"HOTBAR.DAT\":{\"version\":7,\"sha256\":\"ab\",\"size\":204832,"
            "\"pending\":false,\"uploadedAt\":\"x\",\"source\":{\"workstation\":\"ws-1\",\"cid\":\"00400012DC10855C\","
            "\"name\":\"鱼鱼无敌啦\",\"at\":\"2026-10-24T03:31:00Z\"}},\"MACRO.DAT\":{\"version\":0,\"sha256\":\"cd\","
            "\"size\":286752,\"pending\":true,\"source\":{\"workstation\":null,\"cid\":null,\"name\":null,\"at\":null}}},"
            "\"note\":[1,{\"a\":null}]}";
        CHECK(ParseSetState(withExtras, &state, &error));
        CHECK(state.files.size() == 2);

        const FileState* hotbar = state.Find("HOTBAR.DAT");
        CHECK(hotbar != nullptr && hotbar->version == 7 && hotbar->sha256 == "ab" && hotbar->size == 204832 && !hotbar->pending);
        CHECK(hotbar != nullptr && hotbar->source.workstation == "ws-1" && hotbar->source.name == "鱼鱼无敌啦");
        CHECK(state.Find("hotbar.dat") == hotbar);
        const FileState* macro = state.Find("MACRO.DAT");
        CHECK(macro != nullptr && macro->pending && macro->source.cid.empty());

        const std::string serialized = SerializeSetState(state);
        CHECK(serialized.find("\"lastSync\":123") != std::string::npos);
        CHECK(serialized.find("\"note\":[1,{\"a\":null}]") != std::string::npos);
        CHECK(serialized.find("\"uploadedAt\":\"x\"") != std::string::npos);

        SetState again;
        CHECK(ParseSetState(serialized, &again, &error));
        CHECK(SerializeSetState(again) == serialized);

        // 特殊字符与坏的 UTF-8
        state.files[0].source.name = std::string("引号\"反斜杠\\换行\n控制\x01坏\xff结尾");
        CHECK(WriteSetState(root, setId, state, &error));
        CHECK(ReadSetState(root, setId, &again, &error));
        CHECK(again.Find("HOTBAR.DAT") != nullptr &&
              again.Find("HOTBAR.DAT")->source.name == std::string("引号\"反斜杠\\换行\n控制\x01坏\xEF\xBF\xBD结尾"));

        // 坏的 state.json 读失败, 不当成空
        CHECK(WriteText(SetDirectory(root, setId) + L"\\state.json", "{\"files\":"));
        CHECK(!ReadSetState(root, setId, &again, &error) && !error.empty());
        CHECK(WriteText(SetDirectory(root, setId) + L"\\state.json", "{\"files\":[]}"));
        CHECK(!ReadSetState(root, setId, &again, &error));
        DeleteFileW((SetDirectory(root, setId) + L"\\state.json").c_str());

        // 发布 → outbox、current、state
        const FileSpec& hotbarSpec = Spec(Category::Hotbar);
        const Bytes first = Filled(204832, 1);
        PublishSource source{ "00400012DC10855C", "鱼鱼无敌啦", 67408 };

        uint64_t seq1 = 0;
        CHECK(PublishFile(root, setId, hotbarSpec, first.data(), first.size(), source, &seq1, &error));

        std::vector<OutboxItem> items;
        CHECK(ListOutbox(root, setId, &items, &error));
        CHECK(items.size() == 1);
        if (items.size() == 1)
        {
            const OutboxItem& item = items[0];
            CHECK(item.seq == seq1 && item.name == "HOTBAR.DAT" && item.sha256 == Hash(first) && item.size == first.size());
            CHECK(item.baseVersion == 0 && item.cid == "00400012DC10855C" && item.charName == "鱼鱼无敌啦" && item.gamePid == 67408);
            CHECK(item.at.size() == 20 && item.at.back() == 'Z');

            Bytes content;
            CHECK(ReadOutboxContent(root, setId, seq1, &content, &error) && content == first);
        }

        CHECK(ReadBytes(CurrentFilePath(root, setId, hotbarSpec)) == first);
        CHECK(ReadSetState(root, setId, &state, &error));
        hotbar = state.Find("HOTBAR.DAT");
        CHECK(hotbar != nullptr && hotbar->pending && hotbar->sha256 == Hash(first) && hotbar->size == first.size() && hotbar->version == 0);
        CHECK(hotbar != nullptr && hotbar->source.cid == "00400012DC10855C" && hotbar->source.name == "鱼鱼无敌啦");

        // 外壳上传后: version 改为服务器给的, 删掉 outbox 项; 再发布时 baseVersion 带上它, 序号仍递增
        state.Find("HOTBAR.DAT")->version = 5;
        state.Find("HOTBAR.DAT")->pending = false;
        CHECK(WriteSetState(root, setId, state, &error));
        {
            wchar_t name[48];
            _snwprintf_s(name, _countof(name), _TRUNCATE, L"\\%020llu", static_cast<unsigned long long>(seq1));
            DeleteFileW((OutboxDirectory(root, setId) + name + L".json").c_str());
            DeleteFileW((OutboxDirectory(root, setId) + name + L".dat").c_str());
        }

        const Bytes second = Filled(204832, 2);
        uint64_t seq2 = 0;
        CHECK(PublishFile(root, setId, hotbarSpec, second.data(), second.size(), PublishSource{ "", "", 1 }, &seq2, &error));
        CHECK(seq2 > seq1);
        CHECK(ListOutbox(root, setId, &items, &error) && items.size() == 1 && items[0].baseVersion == 5 && items[0].cid.empty());
        CHECK(ReadSetState(root, setId, &state, &error) && state.Find("HOTBAR.DAT")->version == 5 && state.Find("HOTBAR.DAT")->pending);

        // 同一毫秒内连发, 序号也不重复
        uint64_t seq3 = 0, seq4 = 0;
        CHECK(PublishFile(root, setId, hotbarSpec, first.data(), first.size(), source, &seq3, &error));
        CHECK(PublishFile(root, setId, hotbarSpec, second.data(), second.size(), source, &seq4, &error));
        CHECK(seq3 > seq2 && seq4 > seq3);

        CHECK(!PublishFile(root, "bad/id", hotbarSpec, first.data(), first.size(), source, &seq4, &error));

        // 心跳
        ModuleHeartbeat heartbeat{ "0.9.1", true, "00400012DC10855C", "鱼鱼无敌啦", "" };
        CHECK(WriteHeartbeat(root, 4242, heartbeat, &error));
        const Bytes beat = ReadBytes(root + L"\\modules\\4242.json");
        const std::string beatText(beat.begin(), beat.end());
        CHECK(beatText.find("\"pid\":4242") != std::string::npos);
        CHECK(beatText.find("\"hooked\":true") != std::string::npos);
        CHECK(beatText.find("\"lastCid\":\"00400012DC10855C\"") != std::string::npos);
        CHECK(beatText.find("\"lastName\":\"鱼鱼无敌啦\"") != std::string::npos);
        CHECK(beatText.find("\"lastError\":null") != std::string::npos);
        CHECK(beatText.find("\"moduleVersion\":\"0.9.1\"") != std::string::npos);

        // 查不到角色名: lastName 写 null
        ModuleHeartbeat nameless{ "0.9.1", true, "00400012DC10855C", "", "" };
        CHECK(WriteHeartbeat(root, 4243, nameless, &error));
        const Bytes namelessBeat = ReadBytes(root + L"\\modules\\4243.json");
        const std::string namelessText(namelessBeat.begin(), namelessBeat.end());
        CHECK(namelessText.find("\"lastName\":null") != std::string::npos);

        CHECK(CountTemporaries(root) == 0);
        RemoveTree(root);
    }

    // ---- 读之前 / 写之后 -------------------------------------------------------------
    void TestPrepareAndWritten()
    {
        Section("读之前放入与写之后发布");

        const std::wstring root  = MakeTempRoot(L"flow");
        const std::wstring game  = MakeTempRoot(L"flow-game");
        const std::string  setId = "set-flow";
        const std::wstring dir   = game + L"\\FFXIV_CHR00400012DC164D70"; // 本机从没登录过的号: 目录不存在
        std::string        error;
        PublishSource      source{ "00400012DC10855C", "鱼鱼无敌啦", 1234 };

        const FileSpec& hotbar = Spec(Category::Hotbar);
        const FileSpec& macro  = Spec(Category::Macro);
        const FileSpec& addon  = Spec(Category::Hud);
        const FileSpec& cards  = Spec(Category::Cards);
        const FileSpec& keybind = Spec(Category::Keybind);

        // 这一套还没有这个文件: 不放入, 基线 = 角色目录现有（没有 = 空）
        PrepareOutcome prepared = PrepareRead(root, setId, macro, dir + L"\\MACRO.DAT");
        CHECK(prepared.result == PrepareResult::NoLatest && prepared.hasBaseline && prepared.baseline.empty());
        CHECK(!Exists(dir));

        // 有最新版: 建目录放入
        const Bytes latest = Filled(204832, 4);
        CHECK(PublishFile(root, setId, hotbar, latest.data(), latest.size(), source, nullptr, &error));

        prepared = PrepareRead(root, setId, hotbar, dir + L"\\HOTBAR.DAT");
        CHECK(prepared.result == PrepareResult::Placed && prepared.hasBaseline);
        CHECK(prepared.baseline == Hash(latest) && prepared.pending && prepared.baselineBytes == latest);
        CHECK(ReadBytes(dir + L"\\HOTBAR.DAT") == latest);

        prepared = PrepareRead(root, setId, hotbar, dir + L"\\HOTBAR.DAT");
        CHECK(prepared.result == PrepareResult::AlreadyLatest && prepared.baseline == Hash(latest));

        const Bytes own = Filled(204832, 5);
        CHECK(WriteBytes(dir + L"\\HOTBAR.DAT", own));
        prepared = PrepareRead(root, setId, hotbar, dir + L"\\HOTBAR.DAT");
        CHECK(prepared.result == PrepareResult::Placed && ReadBytes(dir + L"\\HOTBAR.DAT") == latest);

        // current 与 state.json 对不上: 当作没有, 不动角色目录
        const std::wstring currentPath = CurrentFilePath(root, setId, hotbar);
        CHECK(WriteBytes(currentPath, own));
        CHECK(WriteBytes(dir + L"\\HOTBAR.DAT", Filled(204832, 6)));
        prepared = PrepareRead(root, setId, hotbar, dir + L"\\HOTBAR.DAT");
        CHECK(prepared.result == PrepareResult::NoLatest && prepared.hasBaseline && prepared.baseline == Hash(Filled(204832, 6)));
        CHECK(ReadBytes(dir + L"\\HOTBAR.DAT") == Filled(204832, 6));
        CHECK(WriteBytes(currentPath, latest));

        // 最新版不合法: 不放入
        const Bytes badKeybind = Filled(100, 1);
        CHECK(PublishFile(root, setId, keybind, badKeybind.data(), badKeybind.size(), source, nullptr, &error));
        prepared = PrepareRead(root, setId, keybind, dir + L"\\KEYBIND.DAT");
        CHECK(prepared.result == PrepareResult::LatestInvalid && prepared.hasBaseline && prepared.baseline.empty());
        CHECK(!Exists(dir + L"\\KEYBIND.DAT"));

        // 写之后
        std::vector<OutboxItem> items;
        CHECK(ListOutbox(root, setId, &items, &error));
        const size_t before = items.size();

        prepared = PrepareRead(root, setId, hotbar, dir + L"\\HOTBAR.DAT");
        CHECK(prepared.result == PrepareResult::Placed);

        WrittenOutcome written = HandleWritten(root, setId, hotbar, dir + L"\\HOTBAR.DAT", true, prepared.baseline, source);
        CHECK(written.result == WrittenResult::Unchanged);
        CHECK(ListOutbox(root, setId, &items, &error) && items.size() == before);

        const Bytes edited = Filled(204832, 7);
        CHECK(WriteBytes(dir + L"\\HOTBAR.DAT", edited));
        written = HandleWritten(root, setId, hotbar, dir + L"\\HOTBAR.DAT", false, prepared.baseline, source);
        CHECK(written.result == WrittenResult::NoBaseline);
        CHECK(ListOutbox(root, setId, &items, &error) && items.size() == before);

        written = HandleWritten(root, setId, hotbar, dir + L"\\HOTBAR.DAT", true, prepared.baseline, source);
        CHECK(written.result == WrittenResult::Published && written.hash == Hash(edited) && written.seq > 0);
        CHECK(ListOutbox(root, setId, &items, &error) && items.size() == before + 1 && items.back().seq == written.seq);
        CHECK(ReadBytes(currentPath) == edited);

        // 同一次在线再保存一次, 以新基线比较
        written = HandleWritten(root, setId, hotbar, dir + L"\\HOTBAR.DAT", true, written.hash, source);
        CHECK(written.result == WrittenResult::Unchanged);

        CHECK(WriteBytes(dir + L"\\HOTBAR.DAT", Filled(204831, 7)));
        written = HandleWritten(root, setId, hotbar, dir + L"\\HOTBAR.DAT", true, prepared.baseline, source);
        CHECK(written.result == WrittenResult::Invalid && !written.reason.empty());

        written = HandleWritten(root, setId, macro, dir + L"\\MACRO.DAT", true, "", source);
        CHECK(written.result == WrittenResult::ReadFailed);

        // 角色目录本来没有、写出了第一份: 基线为空, 发布
        const Bytes firstMacro = Filled(286752, 8);
        CHECK(WriteBytes(dir + L"\\MACRO.DAT", firstMacro));
        written = HandleWritten(root, setId, macro, dir + L"\\MACRO.DAT", true, "", source);
        CHECK(written.result == WrittenResult::Published);

        // ADDON: 只有自动变化不发布, 真挪了窗口才发布
        AddonFixture fixture;
        const Bytes layout = fixture.Build();
        CHECK(PublishFile(root, setId, addon, layout.data(), layout.size(), source, nullptr, &error));
        prepared = PrepareRead(root, setId, addon, dir + L"\\ADDON.DAT");
        CHECK(prepared.result == PrepareResult::Placed && prepared.baseline == ComparableHash(addon, layout.data(), layout.size()));

        AddonFixture automatic;
        std::swap(automatic.defaults[0], automatic.defaults[3]);
        SetHeight(automatic.defaults[2], 176);
        SetHeight(automatic.slots[2][1], 176);
        const Bytes automaticBytes = automatic.Build();
        CHECK(WriteBytes(dir + L"\\ADDON.DAT", automaticBytes));
        written = HandleWritten(root, setId, addon, dir + L"\\ADDON.DAT", true, prepared.baseline, source);
        CHECK(written.result == WrittenResult::Unchanged);

        AddonFixture moved = automatic;
        moved.defaults[5] = MakeRecord(0x33333333, 1.0f, 1.0f, 64, 64);
        const Bytes movedBytes = moved.Build();
        CHECK(WriteBytes(dir + L"\\ADDON.DAT", movedBytes));
        written = HandleWritten(root, setId, addon, dir + L"\\ADDON.DAT", true, prepared.baseline, source);
        CHECK(written.result == WrittenResult::Published && written.content == movedBytes);
        CHECK(ReadBytes(CurrentFilePath(root, setId, addon)) == movedBytes); // 整文件发布
        const auto keys = AddonDiffKeys(prepared.baselineBytes.data(), prepared.baselineBytes.size(),
                                        written.content.data(), written.content.size());
        CHECK(keys.size() == 1 && keys[0] == "33333333");

        // GS: 末尾多一个 0 不发布
        Bytes gs = Filled(681, 3);
        gs[680] = 0;
        CHECK(PublishFile(root, setId, cards, gs.data(), gs.size(), source, nullptr, &error));
        prepared = PrepareRead(root, setId, cards, dir + L"\\GS.DAT");
        CHECK(prepared.result == PrepareResult::Placed);
        Bytes gsLonger = gs;
        gsLonger.push_back(0);
        CHECK(WriteBytes(dir + L"\\GS.DAT", gsLonger));
        written = HandleWritten(root, setId, cards, dir + L"\\GS.DAT", true, prepared.baseline, source);
        CHECK(written.result == WrittenResult::Unchanged);

        // 互斥体被别人长期占着: 放入失败但基线照取角色目录现有
        {
            SetLock held(setId, 1000);
            CHECK(held.Acquired());

            HANDLE done = CreateEventW(nullptr, TRUE, FALSE, nullptr);
            struct Context { const std::wstring* root; const std::string* setId; const FileSpec* spec; std::wstring file; PrepareOutcome outcome; HANDLE done; };
            Context context{ &root, &setId, &hotbar, dir + L"\\HOTBAR.DAT", {}, done };

            // 互斥体可重入, 必须从别的线程等
            const HANDLE thread = CreateThread(nullptr, 0, [](LPVOID parameter) -> DWORD
            {
                auto* c = static_cast<Context*>(parameter);
                c->outcome = PrepareRead(*c->root, *c->setId, *c->spec, c->file, 200);
                SetEvent(c->done);
                return 0;
            }, &context, 0, nullptr);

            CHECK(thread != nullptr && WaitForSingleObject(done, 10000) == WAIT_OBJECT_0);
            if (thread != nullptr)
            {
                WaitForSingleObject(thread, 10000);
                CloseHandle(thread);
            }
            CloseHandle(done);

            CHECK(context.outcome.result == PrepareResult::Failed && context.outcome.hasBaseline);
            CHECK(context.outcome.reason == "等互斥体超时");
        }

        CHECK(CountTemporaries(root) == 0 && CountTemporaries(game) == 0);
        RemoveTree(root);
        RemoveTree(game);
    }

    // ---- 两个进程同时发布 -----------------------------------------------------------
    Bytes ChildContent(const FileSpec& spec, int child, int index)
    {
        const size_t size = spec.category == Category::Hotbar ? 204832 : 286752;
        Bytes bytes = Filled(size, static_cast<uint8_t>(child * 31 + index));

        char tag[32];
        const int length = _snprintf_s(tag, sizeof(tag), _TRUNCATE, "child%d-%04d|", child, index);
        memcpy(bytes.data(), tag, static_cast<size_t>(length));
        return bytes;
    }

    const FileSpec& ChildSpec(int index) { return Spec(index % 2 == 0 ? Category::Hotbar : Category::Macro); }

    int ChildPublish(const std::wstring& root, const std::string& setId, int child, int count, const std::wstring& eventName)
    {
        const HANDLE go = OpenEventW(SYNCHRONIZE, FALSE, eventName.c_str());
        if (go == nullptr || WaitForSingleObject(go, 30000) != WAIT_OBJECT_0)
        {
            printf("child%d: 等开始信号失败\n", child);
            return 3;
        }
        CloseHandle(go);

        for (int i = 0; i < count; ++i)
        {
            const FileSpec& spec    = ChildSpec(i);
            const Bytes     content = ChildContent(spec, child, i);

            char cid[17];
            _snprintf_s(cid, sizeof(cid), _TRUNCATE, "%016X", child + 1);

            std::string error;
            if (!PublishFile(root, setId, spec, content.data(), content.size(), PublishSource{ cid, "并发", GetCurrentProcessId() }, nullptr, &error))
            {
                printf("child%d: 第 %d 次发布失败: %s\n", child, i, error.c_str());
                return 2;
            }
        }

        return 0;
    }

    void TestConcurrentPublish()
    {
        Section("两个进程同时发布到同一套");

        const std::wstring root   = MakeTempRoot(L"concurrent");
        const std::string  setId  = "set-concurrent";
        const int          count  = 30;
        const int          children = 2;

        wchar_t eventName[96];
        _snwprintf_s(eventName, _countof(eventName), _TRUNCATE, L"Local\\Cat-CharConfig-Test-Go-%lu", GetCurrentProcessId());
        const HANDLE go = CreateEventW(nullptr, TRUE, FALSE, eventName);
        CHECK(go != nullptr);

        wchar_t self[MAX_PATH]{};
        GetModuleFileNameW(nullptr, self, MAX_PATH);

        std::vector<PROCESS_INFORMATION> processes;
        for (int c = 0; c < children; ++c)
        {
            std::wstring command = L"\"" + std::wstring(self) + L"\" --child-publish \"" + root + L"\" " + FromUtf8(setId) +
                                   L" " + std::to_wstring(c) + L" " + std::to_wstring(count) + L" " + eventName;

            STARTUPINFOW startup{};
            startup.cb = sizeof(startup);
            PROCESS_INFORMATION process{};
            const BOOL started = CreateProcessW(nullptr, command.data(), nullptr, nullptr, FALSE, 0, nullptr, nullptr, &startup, &process);
            CHECK(started);
            if (started)
                processes.push_back(process);
        }

        Sleep(300);
        SetEvent(go);

        for (auto& process : processes)
        {
            CHECK(WaitForSingleObject(process.hProcess, 120000) == WAIT_OBJECT_0);
            DWORD code = 99;
            GetExitCodeProcess(process.hProcess, &code);
            CHECK(code == 0);
            CloseHandle(process.hThread);
            CloseHandle(process.hProcess);
        }
        CloseHandle(go);

        std::string error;
        std::vector<OutboxItem> items;
        CHECK(ListOutbox(root, setId, &items, &error));
        CHECK(items.size() == static_cast<size_t>(children * count));
        CHECK(CountFiles(OutboxDirectory(root, setId), L"*.dat") == children * count);
        CHECK(CountFiles(OutboxDirectory(root, setId), L"*.json") == children * count);

        std::set<std::string> seen;
        bool seqIncreasing = true, contentOk = true;
        uint64_t lastHotbarSeq = 0, lastMacroSeq = 0;
        Bytes lastHotbar, lastMacro;

        for (size_t i = 0; i < items.size(); ++i)
        {
            const OutboxItem& item = items[i];
            if (i > 0 && item.seq <= items[i - 1].seq)
                seqIncreasing = false;

            Bytes content;
            const FileSpec* spec = FindFile(item.name.c_str());
            if (spec == nullptr || !ReadOutboxContent(root, setId, item.seq, &content, &error) ||
                Hash(content) != item.sha256 || content.size() != item.size || !ValidateFile(*spec, content.data(), content.size(), nullptr))
            {
                contentOk = false;
                continue;
            }

            seen.insert(std::string(reinterpret_cast<const char*>(content.data()), 11) + item.name);

            if (spec->category == Category::Hotbar) { lastHotbarSeq = item.seq; lastHotbar = content; }
            else                                     { lastMacroSeq = item.seq; lastMacro = content; }
        }

        CHECK(seqIncreasing);
        CHECK(contentOk);
        CHECK(seen.size() == static_cast<size_t>(children * count));

        std::set<std::string> expected;
        for (int c = 0; c < children; ++c)
        {
            for (int i = 0; i < count; ++i)
            {
                const Bytes content = ChildContent(ChildSpec(i), c, i);
                expected.insert(std::string(reinterpret_cast<const char*>(content.data()), 11) + ChildSpec(i).fileName);
            }
        }
        CHECK(seen == expected);

        // current 与 state.json 都是序号最大的那次
        SetState state;
        CHECK(ReadSetState(root, setId, &state, &error));
        const FileState* hotbar = state.Find("HOTBAR.DAT");
        const FileState* macro  = state.Find("MACRO.DAT");
        CHECK(hotbar != nullptr && macro != nullptr && state.files.size() == 2);
        CHECK(lastHotbarSeq > 0 && lastMacroSeq > 0);
        if (hotbar != nullptr && macro != nullptr)
        {
            const Bytes currentHotbar = ReadBytes(CurrentFilePath(root, setId, Spec(Category::Hotbar)));
            const Bytes currentMacro  = ReadBytes(CurrentFilePath(root, setId, Spec(Category::Macro)));
            CHECK(hotbar->pending && hotbar->sha256 == Hash(currentHotbar) && currentHotbar == lastHotbar);
            CHECK(macro->pending && macro->sha256 == Hash(currentMacro) && currentMacro == lastMacro);
        }

        CHECK(CountTemporaries(root) == 0);
        RemoveTree(root);
    }

    // ---- 真实样本（addon-format.md §5）------------------------------------------------
    std::wstring SampleDirectory()
    {
        wchar_t buffer[MAX_PATH]{};
        DWORD length = GetEnvironmentVariableW(L"CAT_CHARCONFIG_SAMPLES", buffer, MAX_PATH);
        if (length > 0 && length < MAX_PATH)
            return buffer;

        length = GetEnvironmentVariableW(L"USERPROFILE", buffer, MAX_PATH);
        if (length > 0 && length < MAX_PATH)
            return std::wstring(buffer) + L"\\.cat\\shared-config";

        return {};
    }

    bool LoadSample(const std::wstring& base, const wchar_t* relative, Bytes* out)
    {
        bool missing = false;
        std::string error;
        if (ReadWholeFile(base + L"\\" + relative, out, &missing, &error))
            return true;

        printf("  [FAIL] 缺样本文件 %s（%s）\n", ToUtf8(relative).c_str(), error.c_str());
        ++g_failed;
        return false;
    }

    void TestSamples()
    {
        Section("真实样本（addon-format.md §5 的 7 组）");

        const std::wstring base = SampleDirectory();
        const DWORD attributes = base.empty() ? INVALID_FILE_ATTRIBUTES : GetFileAttributesW(base.c_str());
        if (attributes == INVALID_FILE_ATTRIBUTES || !(attributes & FILE_ATTRIBUTE_DIRECTORY))
        {
            printf("  跳过: 样本目录不存在（%s）。设 CAT_CHARCONFIG_SAMPLES 指向样本目录\n", ToUtf8(base).c_str());
            ++g_skipped;
            return;
        }

        printf("  样本目录 %s\n", ToUtf8(base).c_str());
        const FileSpec& addon = Spec(Category::Hud);

        struct Pair { const wchar_t* input; const wchar_t* output; };
        const Pair pairs[] = {
            { L"addon-samples\\in-yuyu\\ADDON.DAT", L"addon-samples\\out-xiaoxiao\\ADDON.DAT" },
            { L"addon-samples\\in-yuyu\\ADDON.DAT", L"addon-samples\\out-kio\\ADDON.DAT" },
            { L"addon-samples\\put-xinqing\\ADDON.DAT", L"addon-samples\\out-xinqing\\ADDON.DAT" },
            { L"bak-before\\ADDON.DAT", L"addon-after-kio.DAT" },
        };

        int group = 0;
        for (const auto& pair : pairs)
        {
            ++group;
            Bytes input, output;
            if (!LoadSample(base, pair.input, &input) || !LoadSample(base, pair.output, &output))
                continue;

            std::string reason;
            const bool valid = AddonValid(input.data(), input.size(), &reason) && AddonValid(output.data(), output.size(), &reason);
            const bool equal = valid && AddonComparable(input.data(), input.size()) == AddonComparable(output.data(), output.size());
            printf("  组 %d: %s → %s: %s\n", group, ToUtf8(pair.input).c_str(), ToUtf8(pair.output).c_str(),
                   !valid ? reason.c_str() : (equal ? "可比较字节相等" : "可比较字节不同"));
            CHECK(valid);
            CHECK(input != output);
            CHECK(equal);
        }

        Bytes yuyu;
        if (LoadSample(base, L"addon-samples\\in-yuyu\\ADDON.DAT", &yuyu) && AddonValid(yuyu.data(), yuyu.size()))
        {
            const Bytes comparable = AddonComparable(yuyu.data(), yuyu.size());

            // 组 5: 任一非掩码记录的 x 加 1.0
            const uint32_t count = [&] { uint32_t v; memcpy(&v, yuyu.data() + 0x60, 4); return v; }();
            Bytes moved = yuyu;
            bool  found = false;
            for (uint32_t i = 0; i < count && !found; ++i)
            {
                const size_t offset = 0x70 + static_cast<size_t>(i) * 32;
                const uint8_t* key  = moved.data() + offset;
                const bool masked = (key[0] == 0xd2 && key[1] == 0x00 && key[2] == 0x91 && key[3] == 0xa2) ||
                                    (key[0] == 0xca && key[1] == 0xea && key[2] == 0x61 && key[3] == 0x46) ||
                                    (key[0] == 0xbe && key[1] == 0xb6 && key[2] == 0xd3 && key[3] == 0x06);
                if (masked)
                    continue;

                float x;
                memcpy(&x, moved.data() + offset + 4, 4);
                x += 1.0f;
                memcpy(moved.data() + offset + 4, &x, 4);
                found = true;
            }
            const bool movedDiffers = found && AddonComparable(moved.data(), moved.size()) != comparable;
            printf("  组 5: 挪动一个普通窗口: %s\n", movedDiffers ? "可比较字节不同" : "可比较字节相等");
            CHECK(movedDiffers);
            CHECK(ComparableHash(addon, moved.data(), moved.size()) != ComparableHash(addon, yuyu.data(), yuyu.size()));

            // 组 6: 改 HUD 当前套号
            uint32_t table;
            memcpy(&table, yuyu.data() + 0x4c, 4);
            const size_t hud = 0x30 + table;
            Bytes switched = yuyu;
            uint16_t current;
            memcpy(&current, switched.data() + hud + 8, 2);
            const uint16_t other = current == 0 ? 1 : 0;
            memcpy(switched.data() + hud + 8, &other, 2);
            const bool switchedDiffers = AddonComparable(switched.data(), switched.size()) != comparable;
            printf("  组 6: 改 HUD 当前套号 %u → %u: %s\n", current, other, switchedDiffers ? "可比较字节不同" : "可比较字节相等");
            CHECK(switchedDiffers);
        }

        // 组 7: GS 681 → 682
        Bytes gsIn, gsOut;
        if (LoadSample(base, L"addon-samples\\put-xinqing\\GS.DAT", &gsIn) && LoadSample(base, L"addon-samples\\out-xinqing\\GS.DAT", &gsOut))
        {
            const FileSpec& cards = Spec(Category::Cards);
            const bool equal = GsComparable(gsIn.data(), gsIn.size()) == GsComparable(gsOut.data(), gsOut.size());
            printf("  组 7: GS %zu → %zu 字节: %s\n", gsIn.size(), gsOut.size(), equal ? "可比较字节相等" : "可比较字节不同");
            CHECK(gsIn.size() == 681 && gsOut.size() == 682);
            CHECK(equal);
            CHECK(ValidateFile(cards, gsIn.data(), gsIn.size(), nullptr) && ValidateFile(cards, gsOut.data(), gsOut.size(), nullptr));
        }

        // 样本目录里 9 类文件都合法
        const wchar_t* directories[] = { L"addon-samples\\in-yuyu", L"addon-samples\\out-xiaoxiao", L"addon-samples\\out-kio",
                                         L"addon-samples\\put-xinqing", L"addon-samples\\out-xinqing", L"bak-before" };
        int checked = 0;
        for (const wchar_t* directory : directories)
        {
            for (size_t i = 0; i < kFileCount; ++i)
            {
                const FileSpec& spec = AllFiles()[i];
                Bytes content;
                if (!ReadWholeFile(base + L"\\" + directory + L"\\" + spec.fileNameW, &content, nullptr, nullptr))
                    continue;

                std::string reason;
                const bool valid = ValidateFile(spec, content.data(), content.size(), &reason);
                if (!valid)
                    printf("  %s\\%s 不合法: %s\n", ToUtf8(directory).c_str(), spec.fileName, reason.c_str());
                CHECK(valid);
                ++checked;
            }
        }
        printf("  样本目录里共校验 %d 个文件\n", checked);
    }

    // --hash <NAME.DAT> <文件>...: 每行「合法 原始哈希 可比较哈希 路径」, 不合法时附原因
    int HashFiles(int argc, wchar_t** argv)
    {
        const FileSpec* spec = FindFile(argv[2], wcslen(argv[2]));
        if (spec == nullptr)
        {
            printf("不认识的文件名: %s\n", ToUtf8(argv[2]).c_str());
            return 2;
        }

        for (int i = 3; i < argc; ++i)
        {
            Bytes content;
            std::string error;
            if (!ReadWholeFile(argv[i], &content, nullptr, &error))
            {
                printf("ERR %s %s\n", error.c_str(), ToUtf8(argv[i]).c_str());
                continue;
            }

            std::string reason;
            const bool valid = ValidateFile(*spec, content.data(), content.size(), &reason);
            printf("%d %s %s %s%s%s\n", valid ? 1 : 0, Hash(content).c_str(),
                   ComparableHash(*spec, content.data(), content.size()).c_str(), ToUtf8(argv[i]).c_str(),
                   valid ? "" : " ", valid ? "" : reason.c_str());
        }

        return 0;
    }
}

int wmain(int argc, wchar_t** argv)
{
    SetConsoleOutputCP(CP_UTF8);

    if (argc >= 7 && wcscmp(argv[1], L"--child-publish") == 0)
        return ChildPublish(argv[2], ToUtf8(argv[3]), _wtoi(argv[4]), _wtoi(argv[5]), argv[6]);

    if (argc >= 3 && wcscmp(argv[1], L"--hash") == 0)
        return HashFiles(argc, argv);

    TestPaths();
    TestValidation();
    TestComparable();
    TestMachineConfig();
    TestStateAndOutbox();
    TestPrepareAndWritten();
    TestConcurrentPublish();
    TestSamples();

    printf("charconfig-test: 通过 %d 项, 失败 %d 项, 跳过 %d 组\n", g_passed, g_failed, g_skipped);
    return g_failed > 0 ? 1 : 0;
}
