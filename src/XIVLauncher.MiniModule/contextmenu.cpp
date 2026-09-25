// 选角界面右键菜单: 给角色加「超域传送」, 超域中的角色再加「超域返回」
//
// 行为照抄 DCTraveler（Managers/ContextMenuManager.cs）: 只在角色选择界面、默认类型的右键菜单里出现,
// 右键的是哪个角色看 AgentLobby.SelectedCharacterIndex / SelectedCharacterContentId。
// 点击后不在这里做任何换服 —— 写一个请求文件, 由游戏内 UI（Afan）读到后打开跨区面板,
// 用户在面板里选目的地、点「前往」, 仍走启动器那条既有流程。
//
// 手法与 Framework::Tick 一样是改虚表项（本模块不引 MinHook）:
//   - RaptureAtkModule 虚表第 22 项: 菜单打开时, 把 AtkValue 数组扩一格塞进我们的项;
//   - AddonContextMenu 虚表第 74 项 OnMenuSelected: 点到我们那一项就拦下。
// 数组扩容与释放照 Dalamud 的 ContextMenu.ExpandContextMenuArray: UISpace 上 Malloc, 调完原函数再 Free。
// 只**追加**不前插, 所以游戏原有各项的下标不变, 点它们时原样放行。
//
// ⚠ 「都注」模式下 Dalamud 在同一个函数上挂了 inline hook, 两边都会追加项。那种情况请用 DCTraveler,
//   这里不做协调。
#include "MiniModule.h"
#include "offsets.h"

#include <atomic>
#include <cstdio>
#include <memory>
#include <string>

namespace
{
    using OpenAddonFn       = unsigned short (*)(void* module, const char* name, int count, void* values, void* agent, intptr_t a7, bool a8);
    using OnMenuSelectedFn  = bool (*)(void* addon, int index, unsigned char a3);
    using GetAddonByNameFn  = void* (*)(void* unitManager, const char* name, int index);
    using GetAddonByIdFn    = void* (*)(void* unitManager, unsigned short id);
    using GetUISpaceFn      = void* (*)();
    using MallocFn          = void* (*)(void* space, unsigned long long size, unsigned long long alignment);
    using FreeFn            = void (*)(void* pointer, unsigned long long size);
    using SetManagedStrFn   = void (*)(void* atkValue, const char* value);
    using FireCallbackIntFn = bool (*)(void* addon, int value);

    // 前面带 DCTraveler 同款的跨服图标（U+E05D）和颜色 34:
    //   UIForeground(34) = 02 48 02 23 03, 图标 EE 81 9D, 空格, UIForeground(0) = 02 48 02 01 03
    // 用词按国服官方: 超域传送 / 超域返回
    constexpr char MENU_LABEL_TRAVEL[] = "\x02\x48\x02\x23\x03\xEE\x81\x9D\x20\x02\x48\x02\x01\x03" "超域传送";
    constexpr char MENU_LABEL_BACK[]   = "\x02\x48\x02\x23\x03\xEE\x81\x9D\x20\x02\x48\x02\x01\x03" "超域返回";

    uintptr_t g_atkStageStatic  = 0;
    uintptr_t g_getAddonByName  = 0;
    uintptr_t g_getAddonById    = 0;
    uintptr_t g_getUISpace      = 0;
    uintptr_t g_memoryFree      = 0;
    uintptr_t g_setManagedStr   = 0;
    uintptr_t g_fireCallbackInt = 0;

    void**           g_atkModuleVTable     = nullptr;
    OpenAddonFn      g_originalOpenAddon   = nullptr;
    void**           g_contextMenuVTable   = nullptr;
    OnMenuSelectedFn g_originalOnSelected  = nullptr;

    std::atomic<int>  g_inside    {0};
    std::atomic<bool> g_installed {false};

    // 当前打开的那个菜单是不是加了我们的项、我们的项排第几（= 游戏原有项数）
    // 追加了几项: 1 =「超域传送」; 2 = 再加「超域返回」（超域中的角色）
    bool g_ours        = false;
    int  g_nativeCount = 0;
    int  g_addedCount  = 0;

    std::atomic<int> g_opens  {0};
    std::atomic<int> g_clicks {0};

    struct Character
    {
        unsigned long long contentId;
        unsigned char      loginFlags;
        unsigned char      entryIndex; // 条目自带的 Index（+0x10）
        int                listIndex;  // 在选角列表向量里排第几 —— Minion 的 SelectCharacter 按这个选
        unsigned short     currentWorldId;
        unsigned short     homeWorldId;
        char               name[offsets::CHARA_ENTRY_NAME_LEN + 1];
        char               currentWorld[offsets::CHARA_ENTRY_NAME_LEN + 1];
        char               homeWorld[offsets::CHARA_ENTRY_NAME_LEN + 1];
    };

    Character g_pending{};

    template <typename T>
    T ReadAt(const void* base, uintptr_t offset)
    {
        return *reinterpret_cast<const T*>(reinterpret_cast<const uint8_t*>(base) + offset);
    }

    void CopyFixed(const uint8_t* source, char* destination)
    {
        const size_t length = strnlen(reinterpret_cast<const char*>(source), offsets::CHARA_ENTRY_NAME_LEN);
        memcpy(destination, source, length);
        destination[length] = '\0';
    }

    bool PatchSlot(void** vtable, int index, void* replacement, void** previous)
    {
        DWORD oldProtect = 0;
        auto* slot       = &vtable[index];

        if (!VirtualProtect(slot, sizeof(void*), PAGE_READWRITE, &oldProtect))
        {
            LogF("[menu] VirtualProtect 失败 err=%lu", GetLastError());
            return false;
        }

        if (previous != nullptr)
            *previous = *slot;

        *slot = replacement;

        DWORD ignored = 0;
        VirtualProtect(slot, sizeof(void*), oldProtect, &ignored);
        return true;
    }

    // 还原一个 slot; 已被别人再接管就不动（强行还原会把人家那层抹掉）, 返回 false
    bool RestoreSlot(void** vtable, int index, void* ours, void* original)
    {
        if (vtable == nullptr || original == nullptr)
            return true;

        DWORD oldProtect = 0;
        auto* slot       = &vtable[index];

        if (!VirtualProtect(slot, sizeof(void*), PAGE_READWRITE, &oldProtect))
            return false;

        const bool mine = *slot == ours;

        if (mine)
            *slot = original;

        DWORD ignored = 0;
        VirtualProtect(slot, sizeof(void*), oldProtect, &ignored);
        return mine;
    }

    // ---- 游戏指针（都在主线程上调）---------------------------------------------

    struct Ui
    {
        void* uiModule;
        void* agentModule;
        void* unitManager;
    };

    bool GetUi(Ui* out)
    {
        memset(out, 0, sizeof(Ui));

        const auto framework = GameFrameworkPointer();
        if (framework == nullptr)
            return false;

        out->uiModule = ReadAt<void*>(framework, offsets::FRAMEWORK_UI_MODULE);
        if (out->uiModule == nullptr)
            return false;

        const auto vtable = ReadAt<void**>(out->uiModule, 0);
        out->agentModule  = reinterpret_cast<void* (*)(void*)>(vtable[offsets::UI_MODULE_GET_AGENT_MODULE_VF])(out->uiModule);

        const auto stage = g_atkStageStatic != 0 ? *reinterpret_cast<void**>(g_atkStageStatic) : nullptr;
        out->unitManager = stage != nullptr ? ReadAt<void*>(stage, offsets::ATK_STAGE_UNIT_MANAGER) : nullptr;

        return out->agentModule != nullptr && out->unitManager != nullptr;
    }

    void* Agent(const Ui* ui, int id)
    {
        return ReadAt<void*>(ui->agentModule, offsets::AGENT_MODULE_AGENTS + static_cast<uintptr_t>(id) * sizeof(void*));
    }

    void* AddonByName(const Ui* ui, const char* name)
    {
        return reinterpret_cast<GetAddonByNameFn>(g_getAddonByName)(ui->unitManager, name, 1);
    }

    // 右键的是哪个角色。DCTraveler 直接用 SelectedCharacterIndex 取条目;
    // 这里再核对一次 ContentId, 对不上就按 ContentId 在列表里找（两者理应一致, 不一致时宁可信 cid）
    bool ReadSelectedCharacter(const Ui* ui, Character* out)
    {
        memset(out, 0, sizeof(Character));

        const auto lobby = reinterpret_cast<const uint8_t*>(Agent(ui, offsets::AGENT_ID_LOBBY));
        if (lobby == nullptr)
            return false;

        const auto index = ReadAt<unsigned char>(lobby, offsets::AGENT_LOBBY_SELECTED_CHARA_INDEX);
        const auto cid   = ReadAt<unsigned long long>(lobby, offsets::AGENT_LOBBY_SELECTED_CONTENT_ID);
        const auto first = ReadAt<uint8_t**>(lobby + offsets::AGENT_LOBBY_CHARA_SELECT_ENTRIES, offsets::STD_VECTOR_FIRST);
        const auto last  = ReadAt<uint8_t**>(lobby + offsets::AGENT_LOBBY_CHARA_SELECT_ENTRIES, offsets::STD_VECTOR_LAST);

        if (first == nullptr || last == nullptr || last <= first || last - first > 1000)
            return false;

        const auto count = static_cast<size_t>(last - first);

        const uint8_t* entry = index < count ? first[index] : nullptr;
        int listIndex = entry != nullptr ? static_cast<int>(index) : -1;

        if (entry == nullptr || ReadAt<unsigned long long>(entry, offsets::CHARA_ENTRY_CONTENT_ID) != cid)
        {
            entry = nullptr;

            for (size_t i = 0; i < count && entry == nullptr; ++i)
            {
                if (first[i] != nullptr && ReadAt<unsigned long long>(first[i], offsets::CHARA_ENTRY_CONTENT_ID) == cid)
                {
                    entry     = first[i];
                    listIndex = static_cast<int>(i);
                }
            }
        }

        if (entry == nullptr)
            return false;

        out->contentId      = ReadAt<unsigned long long>(entry, offsets::CHARA_ENTRY_CONTENT_ID);
        out->loginFlags     = ReadAt<unsigned char>(entry, offsets::CHARA_ENTRY_LOGIN_FLAGS);
        out->entryIndex     = ReadAt<unsigned char>(entry, offsets::CHARA_ENTRY_INDEX);
        out->listIndex      = listIndex;
        out->currentWorldId = ReadAt<unsigned short>(entry, offsets::CHARA_ENTRY_CURRENT_WORLD);
        out->homeWorldId    = ReadAt<unsigned short>(entry, offsets::CHARA_ENTRY_HOME_WORLD);
        CopyFixed(entry + offsets::CHARA_ENTRY_NAME,               out->name);
        CopyFixed(entry + offsets::CHARA_ENTRY_CURRENT_WORLD_NAME, out->currentWorld);
        CopyFixed(entry + offsets::CHARA_ENTRY_HOME_WORLD_NAME,    out->homeWorld);
        return true;
    }

    // ---- 扩容后的 AtkValue 数组 ----------------------------------------------

    struct Expanded
    {
        uint8_t*           block; // UISpace 上分配的整块（前 8 字节是 new[] 式的元素数）
        unsigned long long size;
        uint8_t*           values;
        int                count;
    };

    uint8_t* ValueAt(uint8_t* values, int index) { return values + static_cast<size_t>(index) * offsets::ATK_VALUE_SIZE; }

    // 这次打开的是不是「选角界面右键角色」那个菜单; 是的话造一份多一项的数组
    bool PrepareMenu(const char* name, int count, uint8_t* values, void* agent, Expanded* out)
    {
        memset(out, 0, sizeof(Expanded));

        if (name == nullptr)
            return false;

        const bool isMenu = strcmp(name, "ContextMenu") == 0;

        // 任何一次开菜单 / 子菜单都先作废上一次的记录 —— 下标只对当时那个菜单有效
        if (isMenu || strcmp(name, "AddonContextSub") == 0)
            g_ours = false;

        if (!isMenu || values == nullptr || count < offsets::CONTEXT_MENU_HEADER_COUNT)
            return false;

        Ui ui{};
        if (!GetUi(&ui))
            return false;

        // MenuType.Default = AgentContext 打开的
        if (agent == nullptr || agent != Agent(&ui, offsets::AGENT_ID_CONTEXT))
            return false;

        // DCTraveler 的 AddonPtr == 0: 菜单没有「挡住的父窗口」
        const auto existing = AddonByName(&ui, "ContextMenu");

        if (existing != nullptr)
        {
            const auto blocked = ReadAt<unsigned short>(existing, offsets::ATK_UNIT_BASE_BLOCKED_PARENT_ID);

            if (reinterpret_cast<GetAddonByIdFn>(g_getAddonById)(ui.unitManager, blocked) != nullptr)
                return false;
        }

        // 只在角色选择界面
        if (AddonByName(&ui, "_CharaSelectListMenu") == nullptr)
            return false;

        Character character{};
        if (!ReadSelectedCharacter(&ui, &character))
            return false;

        const auto countType = ReadAt<unsigned>(values, 0);

        if (countType != offsets::ATK_VALUE_TYPE_UINT && countType != offsets::ATK_VALUE_TYPE_INT)
        {
            LogF("[menu] 菜单第 0 项类型是 %u（预期 UInt/Int），看不懂布局，不加项", countType);
            return false;
        }

        const int native = static_cast<int>(ReadAt<unsigned>(values, 8));
        const int rest   = count - offsets::CONTEXT_MENU_HEADER_COUNT - native;

        // 只认两种布局: 没有置灰表（rest == 0）/ 有一张完整置灰表（rest == native）。别的看不懂就不碰
        if (native <= 0 || native > 30 || (rest != 0 && rest != native))
            return false;

        // 超域中（LoginFlags 16/32, 与 DCTraveler 同判据）多给一项「超域返回」
        const bool traveling = character.loginFlags == offsets::LOGIN_FLAG_DC_TRAVELING ||
                               character.loginFlags == offsets::LOGIN_FLAG_UNK32;
        const int  added     = traveling ? 2 : 1;

        const bool hasDisabled = rest == native;
        const int  newCount    = (native + added) * (hasDisabled ? 2 : 1) + offsets::CONTEXT_MENU_HEADER_COUNT;
        const auto size        = static_cast<unsigned long long>(newCount) * offsets::ATK_VALUE_SIZE + 8;

        const auto space = reinterpret_cast<GetUISpaceFn>(g_getUISpace)();
        if (space == nullptr)
            return false;

        const auto allocate = reinterpret_cast<MallocFn>(ReadAt<void**>(space, 0)[offsets::MEMORY_SPACE_MALLOC_VF]);
        const auto block    = static_cast<uint8_t*>(allocate(space, size, 0));
        if (block == nullptr)
            return false;

        memset(block, 0, static_cast<size_t>(size));
        *reinterpret_cast<unsigned long long*>(block) = static_cast<unsigned long long>(newCount);

        const auto fresh = block + 8;
        const int  head  = offsets::CONTEXT_MENU_HEADER_COUNT;

        // 头 + 原有各项名字原样搬（按位拷, 与 Dalamud 一致; 托管串指针跟着走, 不重复释放）
        memcpy(fresh, values, static_cast<size_t>(head + native) * offsets::ATK_VALUE_SIZE);

        // 原有的置灰表整体后移 added 格（名字多了 added 项）, 我们的项都不置灰
        if (hasDisabled)
        {
            memcpy(ValueAt(fresh, head + native + added), ValueAt(values, head + native),
                   static_cast<size_t>(native) * offsets::ATK_VALUE_SIZE);

            for (int i = 0; i < added; ++i)
            {
                auto* flag = ValueAt(fresh, head + (native + added) + native + i);
                *reinterpret_cast<unsigned*>(flag) = offsets::ATK_VALUE_TYPE_INT;
                *reinterpret_cast<int*>(flag + 8)  = 0;
            }
        }

        // 新项的名字: 此时 Type 是 0（刚 memset）, SetManagedString 会分配并拷贝
        const auto setString = reinterpret_cast<SetManagedStrFn>(g_setManagedStr);
        setString(ValueAt(fresh, head + native), MENU_LABEL_TRAVEL);

        if (traveling)
            setString(ValueAt(fresh, head + native + 1), MENU_LABEL_BACK);

        *reinterpret_cast<unsigned*>(fresh + 8) = static_cast<unsigned>(native + added);

        out->block  = block;
        out->size   = size;
        out->values = fresh;
        out->count  = newCount;

        g_pending     = character;
        g_nativeCount = native;
        g_addedCount  = added;
        g_ours        = true;
        return true;
    }

    bool PrepareMenuGuarded(const char* name, int count, uint8_t* values, void* agent, Expanded* out)
    {
        __try
        {
            return PrepareMenu(name, count, values, agent, out);
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            LogF("[menu] 准备菜单时异常 code=0x%08X, 本次不加项", GetExceptionCode());
            g_ours = false;
            memset(out, 0, sizeof(Expanded));
            return false;
        }
    }

    void FreeExpandedGuarded(Expanded* expanded)
    {
        __try
        {
            reinterpret_cast<FreeFn>(g_memoryFree)(expanded->block, expanded->size);
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            LogF("[menu] 释放菜单数组时异常 code=0x%08X", GetExceptionCode());
        }
    }

    bool OnMenuSelectedHooked(void* addon, int index, unsigned char a3);

    // ContextMenu 这个 addon 第一次出现后才拿得到它的虚表; 虚表是静态的, 挂一次就一直在
    void PatchContextMenuOnce()
    {
        if (g_contextMenuVTable != nullptr)
            return;

        __try
        {
            Ui ui{};
            if (!GetUi(&ui))
                return;

            const auto addon = AddonByName(&ui, "ContextMenu");
            if (addon == nullptr)
                return;

            const auto vtable = ReadAt<void**>(addon, 0);

            if (PatchSlot(vtable, offsets::ADDON_CONTEXT_MENU_ON_MENU_SELECTED_VF,
                          reinterpret_cast<void*>(&OnMenuSelectedHooked), reinterpret_cast<void**>(&g_originalOnSelected)))
            {
                g_contextMenuVTable = vtable;
                LogF("[menu] 已挂 AddonContextMenu::OnMenuSelected (vtable=0x%p 原函数=0x%p)",
                     vtable, reinterpret_cast<void*>(g_originalOnSelected));
            }
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            LogF("[menu] 挂 OnMenuSelected 时异常 code=0x%08X", GetExceptionCode());
        }
    }

    unsigned short OpenAddonHooked(void* module, const char* name, int count, void* values, void* agent, intptr_t a7, bool a8)
    {
        // 计数包住对原函数的调用 —— 理由同 HookedTick（卸载时要等所有调用真正离开本模块）
        g_inside.fetch_add(1);

        Expanded   expanded{};
        const bool ours = PrepareMenuGuarded(name, count, static_cast<uint8_t*>(values), agent, &expanded);

        const auto original = g_originalOpenAddon;
        unsigned short result = 0;

        if (original != nullptr)
        {
            result = ours ? original(module, name, expanded.count, expanded.values, agent, a7, a8)
                          : original(module, name, count, values, agent, a7, a8);
        }

        if (ours)
        {
            FreeExpandedGuarded(&expanded);
            PatchContextMenuOnce();

            ++g_opens;
            LogF("[menu] 选角右键菜单: 从第 %d 项起追加 %d 项（超域传送%s）, 角色=%s cid=%llu",
                 g_nativeCount, g_addedCount, g_addedCount > 1 ? " + 超域返回" : "", g_pending.name, g_pending.contentId);
        }

        g_inside.fetch_sub(1);
        return result;
    }

    // ---- 点击 → 请求文件 -------------------------------------------------------

    std::string JsonEscape(const char* text)
    {
        std::string result;

        for (const char* p = text; *p != '\0'; ++p)
        {
            const auto c = static_cast<unsigned char>(*p);

            if (c == '"' || c == '\\')
            {
                result += '\\';
                result += static_cast<char>(c);
            }
            else if (c < 0x20)
            {
                char escaped[8];
                _snprintf_s(escaped, sizeof(escaped), _TRUNCATE, "\\u%04x", c);
                result += escaped;
            }
            else
            {
                result += static_cast<char>(c);
            }
        }

        return result;
    }

    std::wstring RequestDirectory()
    {
        wchar_t programData[MAX_PATH]{};
        const DWORD length = GetEnvironmentVariableW(L"ProgramData", programData, MAX_PATH);

        std::wstring directory = (length > 0 && length < MAX_PATH) ? programData : L"C:\\ProgramData";
        directory += L"\\DcMiniLauncher";
        return directory;
    }

    // <ProgramData>\DcMiniLauncher\menu-<pid>.json —— 和启动器的端口文件同一个目录,
    // 游戏内 UI 已经知道在哪个盘上找它。先写临时文件再改名, 读的一方不会读到半截。
    //
    // contentId 用字符串: 17 位, 超出 Lua 数字（double）的精确范围。
    // seq 用当前时间（毫秒）, 模块重载后也单调递增, 读的一方按 seq 去重。
    // action: "travel" = 超域传送（打开面板选目的地）; "back" = 超域返回（回原始大区, 不用选）
    bool WriteRequest(const Character& character, const char* action)
    {
        FILETIME now{};
        GetSystemTimeAsFileTime(&now);
        const auto seq = ((static_cast<unsigned long long>(now.dwHighDateTime) << 32) | now.dwLowDateTime) / 10000ULL;

        char head[256];
        _snprintf_s(head, sizeof(head), _TRUNCATE,
                    "{\"seq\":%llu,\"action\":\"%s\",\"pid\":%lu,\"contentId\":\"%llu\",\"loginFlags\":%u,"
                    "\"currentWorldId\":%u,\"homeWorldId\":%u,\"listIndex\":%d,\"entryIndex\":%u,",
                    seq, action, GetCurrentProcessId(), character.contentId, character.loginFlags,
                    character.currentWorldId, character.homeWorldId, character.listIndex, character.entryIndex);

        std::string json = head;
        json += "\"name\":\"" + JsonEscape(character.name) + "\",";
        json += "\"currentWorld\":\"" + JsonEscape(character.currentWorld) + "\",";
        json += "\"homeWorld\":\"" + JsonEscape(character.homeWorld) + "\"}";

        const auto directory = RequestDirectory();
        CreateDirectoryW(directory.c_str(), nullptr);

        wchar_t fileName[64];
        _snwprintf_s(fileName, _countof(fileName), _TRUNCATE, L"\\menu-%lu.json", GetCurrentProcessId());

        const auto path      = directory + fileName;
        const auto temporary = path + L".tmp";

        const HANDLE file = CreateFileW(temporary.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);

        if (file == INVALID_HANDLE_VALUE)
        {
            LogF("[menu] 写请求文件失败 err=%lu", GetLastError());
            return false;
        }

        DWORD written = 0;
        const BOOL ok = WriteFile(file, json.data(), static_cast<DWORD>(json.size()), &written, nullptr);
        CloseHandle(file);

        if (!ok || !MoveFileExW(temporary.c_str(), path.c_str(), MOVEFILE_REPLACE_EXISTING))
        {
            LogF("[menu] 写请求文件失败 err=%lu", GetLastError());
            return false;
        }

        LogF("[menu] 已写请求: %s", json.c_str());
        return true;
    }

    void CloseMenuGuarded(void* addon)
    {
        __try
        {
            // -2 = 带点击音效关掉菜单（Dalamud 对自定义项的处理）
            reinterpret_cast<FireCallbackIntFn>(g_fireCallbackInt)(addon, -2);
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            LogF("[menu] 关菜单时异常 code=0x%08X", GetExceptionCode());
        }
    }

    bool OnMenuSelectedHooked(void* addon, int index, unsigned char a3)
    {
        g_inside.fetch_add(1);

        bool result;

        if (g_ours && index >= g_nativeCount && index < g_nativeCount + g_addedCount)
        {
            g_ours = false;
            ++g_clicks;

            const bool back = index == g_nativeCount + 1;

            LogF("[menu] 点了「%s」: %s (当前=%s 原始=%s flags=%u)", back ? "超域返回" : "超域传送",
                 g_pending.name, g_pending.currentWorld, g_pending.homeWorld, g_pending.loginFlags);

            WriteRequest(g_pending, back ? "back" : "travel");
            CloseMenuGuarded(addon);
            result = false;
        }
        else
        {
            const auto original = g_originalOnSelected;
            result = original != nullptr ? original(addon, index, a3) : false;
        }

        g_inside.fetch_sub(1);
        return result;
    }

    bool Resolve()
    {
        g_atkStageStatic  = ScanStaticAddress(offsets::ATK_STAGE_SIG, offsets::ATK_STAGE_SIG_OFFSET);
        g_getAddonByName  = ScanText(offsets::GET_ADDON_BY_NAME_SIG);
        g_getAddonById    = ScanText(offsets::GET_ADDON_BY_ID_SIG);
        g_getUISpace      = ScanText(offsets::GET_UI_SPACE_SIG);
        g_memoryFree      = ScanText(offsets::MEMORY_SPACE_FREE_SIG);
        g_setManagedStr   = ScanText(offsets::ATK_VALUE_SET_MANAGED_STRING_SIG);
        g_fireCallbackInt = ScanText(offsets::FIRE_CALLBACK_INT_SIG);

        const auto base = ModuleBase();
        const auto rva  = [base](uintptr_t address) { return static_cast<unsigned long long>(address ? address - base : 0); };

        LogF("[menu] 特征码: AtkStage=0x%llX GetAddonByName=0x%llX GetAddonById=0x%llX GetUISpace=0x%llX "
             "Free=0x%llX SetManagedString=0x%llX FireCallbackInt=0x%llX",
             rva(g_atkStageStatic), rva(g_getAddonByName), rva(g_getAddonById), rva(g_getUISpace),
             rva(g_memoryFree), rva(g_setManagedStr), rva(g_fireCallbackInt));

        return g_atkStageStatic && g_getAddonByName && g_getAddonById && g_getUISpace &&
               g_memoryFree && g_setManagedStr && g_fireCallbackInt;
    }

    bool InstallOnMainThread()
    {
        __try
        {
            Ui ui{};
            if (!GetUi(&ui))
                return false;

            const auto vtable    = ReadAt<void**>(ui.uiModule, 0);
            const auto atkModule = reinterpret_cast<void* (*)(void*)>(vtable[offsets::UI_MODULE_GET_RAPTURE_ATK_MODULE_VF])(ui.uiModule);

            if (atkModule == nullptr)
                return false;

            g_atkModuleVTable = ReadAt<void**>(atkModule, 0);

            return PatchSlot(g_atkModuleVTable, offsets::RAPTURE_ATK_MODULE_OPEN_ADDON_BY_AGENT_VF,
                             reinterpret_cast<void*>(&OpenAddonHooked), reinterpret_cast<void**>(&g_originalOpenAddon));
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            LogF("[menu] 安装时异常 code=0x%08X", GetExceptionCode());
            return false;
        }
    }
}

bool ContextMenuInstall()
{
    if (g_installed.load())
        return true;

    if (!Resolve())
    {
        LogF("[menu] 特征码不全, 不加右键菜单（其它功能不受影响）");
        return false;
    }

    // 虚表项在哪个线程改都行, 但取 RaptureAtkModule 要调虚函数 —— 放主线程上做
    auto ok = std::make_shared<bool>(false);

    if (!MainThreadRun([ok] { *ok = InstallOnMainThread(); }, 5000) || !*ok)
    {
        LogF("[menu] 没挂上 RaptureAtkModule vf22, 不加右键菜单");
        return false;
    }

    g_installed.store(true);
    LogF("[menu] 已挂 RaptureAtkModule vf%d (vtable=0x%p 原函数=0x%p)", offsets::RAPTURE_ATK_MODULE_OPEN_ADDON_BY_AGENT_VF,
         g_atkModuleVTable, reinterpret_cast<void*>(g_originalOpenAddon));
    return true;
}

bool ContextMenuUninstall()
{
    if (!g_installed.load())
        return true;

    const bool menu     = RestoreSlot(g_atkModuleVTable, offsets::RAPTURE_ATK_MODULE_OPEN_ADDON_BY_AGENT_VF,
                                      reinterpret_cast<void*>(&OpenAddonHooked), reinterpret_cast<void*>(g_originalOpenAddon));
    const bool selected = RestoreSlot(g_contextMenuVTable, offsets::ADDON_CONTEXT_MENU_ON_MENU_SELECTED_VF,
                                      reinterpret_cast<void*>(&OnMenuSelectedHooked), reinterpret_cast<void*>(g_originalOnSelected));

    if (!menu || !selected)
    {
        LogF("[menu] 虚表项已被别人接管, 不还原, 模块只能留在进程里");
        return false;
    }

    for (int i = 0; i < 200 && g_inside.load() > 0; ++i)
        Sleep(10);

    if (g_inside.load() > 0)
    {
        LogF("[menu] ⚠ 仍有调用停在右键菜单 hook 里, 卸载不安全");
        return false;
    }

    Sleep(200);

    g_installed.store(false);
    g_originalOpenAddon  = nullptr;
    g_originalOnSelected = nullptr;

    LogF("[menu] 已还原右键菜单 hook");
    return true;
}

std::string ContextMenuStatus()
{
    char response[256];
    _snprintf_s(response, sizeof(response), _TRUNCATE, "OK installed=%d selectedHooked=%d opens=%d clicks=%d",
                g_installed.load() ? 1 : 0, g_contextMenuVTable != nullptr ? 1 : 0, g_opens.load(), g_clicks.load());
    return response;
}
