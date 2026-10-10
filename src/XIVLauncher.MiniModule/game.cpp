// 游戏结构体访问 —— 本轮只读
//
// 目的: 在动任何一个字节之前, 先证明 offsets.h 里那堆偏移在这台机器的这个客户端上是对的。
// 判据是「读出来的东西能和外部已知事实对上」: 当前大厅主机名应当形如 ffxivlobby0N.ff14.sdo.com,
// 且与启动时所连大区一致。对不上就说明偏移错了 —— 那种情况下绝不能进入写入/调用阶段。
#include "MiniModule.h"
#include "offsets.h"

#include <atomic>
#include <cstdio>
#include <memory>
#include <utility>

namespace
{
    uintptr_t g_frameworkStatic     = 0;
    uintptr_t g_atkStageStatic      = 0;
    uintptr_t g_returnToTitle       = 0;
    uintptr_t g_releaseLobbyContext = 0;
    uintptr_t g_setString           = 0;
    bool      g_resolved            = false;

    // ⚠ 带 __try 的函数里不能有需要展开的 C++ 对象 (MSVC C2712), 所以这里全是 POD
    struct ProbeData
    {
        void*     framework;
        void*     uiModule;
        void*     agentModule;
        void*     agentLobby;
        void*     networkModuleProxy;
        void*     networkModule;
        long long idleTime;
        void*     lobbyUiContext;
        unsigned char lobbyUiState;
        char      activeLobbyHost[160];
        char      lobbyHost0[160];
        char      saveDataBankHost[160];
        char      gameSession[160];
        int       devConfigCount;
    };

    template <typename T>
    T ReadAt(void* base, uintptr_t offset)
    {
        return *reinterpret_cast<T*>(reinterpret_cast<uint8_t*>(base) + offset);
    }

    // Utf8String: +0 是 char*, +0x18 是长度。空串时 StringPtr 也可能有效, 按长度截断即可
    void CopyUtf8String(void* utf8String, char* destination, size_t capacity)
    {
        destination[0] = '\0';

        if (utf8String == nullptr)
            return;

        const auto text = ReadAt<char*>(utf8String, offsets::UTF8_STRING_PTR);
        if (text == nullptr)
            return;

        // ⚠ 2026-08-15 实测: CS 标的 StringLength(+0x18) 在国服客户端上恒为 0（活着的串也一样）,
        //   真正有效的是 BufUsed(+0x10) = 字符数 + 1。所以长度一律按 BufUsed 算, 再用 strnlen 兜底。
        const auto bufUsed = ReadAt<long long>(utf8String, offsets::UTF8_STRING_BUF_USED);
        const auto bufSize = ReadAt<long long>(utf8String, offsets::UTF8_STRING_BUF_SIZE);

        long long length = bufUsed > 0 ? bufUsed - 1 : 0;

        if (length <= 0 || length > bufSize)
            length = static_cast<long long>(strnlen(text, capacity - 1));

        if (length <= 0)
            return;

        size_t copy = static_cast<size_t>(length);
        if (copy >= capacity)
            copy = capacity - 1;

        memcpy(destination, text, copy);
        destination[copy] = '\0';
    }

    bool ProbeRaw(ProbeData* out)
    {
        __try
        {
            memset(out, 0, sizeof(ProbeData));

            out->framework = *reinterpret_cast<void**>(g_frameworkStatic);
            if (out->framework == nullptr)
                return false;

            out->uiModule = ReadAt<void*>(out->framework, offsets::FRAMEWORK_UI_MODULE);

            if (out->uiModule != nullptr)
            {
                // UIModule 的第 37 号虚函数 = GetAgentModule()
                const auto vtable = ReadAt<void**>(out->uiModule, 0);
                const auto getAgentModule =
                    reinterpret_cast<void* (*)(void*)>(vtable[offsets::UI_MODULE_GET_AGENT_MODULE_VF]);

                out->agentModule = getAgentModule(out->uiModule);
            }

            if (out->agentModule != nullptr)
            {
                out->agentLobby = ReadAt<void*>(out->agentModule,
                                                offsets::AGENT_MODULE_AGENTS +
                                                offsets::AGENT_ID_LOBBY * sizeof(void*));
            }

            if (out->agentLobby != nullptr)
            {
                out->idleTime       = ReadAt<long long>(out->agentLobby, offsets::AGENT_LOBBY_IDLE_TIME);
                out->lobbyUiContext = ReadAt<void*>(out->agentLobby, offsets::AGENT_LOBBY_UI_CLIENT_CONTEXT);
                out->lobbyUiState   = ReadAt<unsigned char>(out->agentLobby, offsets::AGENT_LOBBY_UI_CLIENT_STATE);

                CopyUtf8String(reinterpret_cast<uint8_t*>(out->agentLobby) + offsets::AGENT_LOBBY_GAME_SESSION,
                               out->gameSession, sizeof(out->gameSession));
            }

            out->networkModuleProxy = ReadAt<void*>(out->framework, offsets::FRAMEWORK_NETWORK_MODULE_PROXY);

            if (out->networkModuleProxy != nullptr)
                out->networkModule = ReadAt<void*>(out->networkModuleProxy, offsets::NETWORK_MODULE_PROXY_MODULE);

            if (out->networkModule != nullptr)
            {
                const auto module = reinterpret_cast<uint8_t*>(out->networkModule);

                CopyUtf8String(module + offsets::NETWORK_MODULE_ACTIVE_LOBBY, out->activeLobbyHost, sizeof(out->activeLobbyHost));
                CopyUtf8String(module + offsets::NETWORK_MODULE_LOBBY_HOSTS,  out->lobbyHost0,      sizeof(out->lobbyHost0));
                CopyUtf8String(module + offsets::NETWORK_MODULE_SAVE_DATA,    out->saveDataBankHost, sizeof(out->saveDataBankHost));
            }

            const auto devConfig = reinterpret_cast<uint8_t*>(out->framework) + offsets::FRAMEWORK_DEV_CONFIG;
            out->devConfigCount  = static_cast<int>(ReadAt<unsigned int>(devConfig, offsets::CONFIG_BASE_COUNT));

            return true;
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            LogF("[game] 探针触发异常 code=0x%08X —— 偏移多半不对, 绝不能继续往下写", GetExceptionCode());
            return false;
        }
    }
}

namespace
{
    // 诊断用: 把一个 Utf8String 的头部原样摊开。读出来是空串时, 用它区分
    // 「偏移错了(全是垃圾)」和「字段确实是空的(结构合法, 长度为 0)」
    // previewText=false 用于 GameSession —— 那是登录票据, 绝不能进日志
    void DescribeUtf8String(char* destination, size_t capacity, const char* label, void* utf8String, bool previewText)
    {
        __try
        {
            const auto text     = ReadAt<char*>(utf8String, offsets::UTF8_STRING_PTR);
            const auto bufSize  = ReadAt<long long>(utf8String, 0x08);
            const auto bufUsed  = ReadAt<long long>(utf8String, 0x10);
            const auto length   = ReadAt<long long>(utf8String, offsets::UTF8_STRING_LENGTH);
            const auto inlineAt = reinterpret_cast<char*>(reinterpret_cast<uint8_t*>(utf8String) + 0x22);

            char preview[48]{};
            if (text != nullptr && previewText)
            {
                for (int i = 0; i < 40; ++i)
                {
                    const char c = text[i];
                    if (c == '\0') break;
                    preview[i] = (c >= 0x20 && c < 0x7F) ? c : '.';
                }
            }

            _snprintf_s(destination, capacity, _TRUNCATE,
                        "%s{ptr=0x%p bufSize=%lld bufUsed=%lld len=%lld inlineAt=0x%p preview=%s}",
                        label, text, bufSize, bufUsed, length, inlineAt, preview);
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            _snprintf_s(destination, capacity, _TRUNCATE, "%s{读取异常}", label);
        }
    }

    // 读出 DevConfig 里那三项当前值。幂等写自检要拿它们原样写回去, 所以必须能先读到
    void CopyDevConfigHosts(void* framework, char* gm, char* saveData, char* lobby01, size_t capacity)
    {
        gm[0] = saveData[0] = lobby01[0] = '\0';

        __try
        {
            const auto devConfig = reinterpret_cast<uint8_t*>(framework) + offsets::FRAMEWORK_DEV_CONFIG;
            const auto count     = ReadAt<unsigned int>(devConfig, offsets::CONFIG_BASE_COUNT);
            const auto entries   = ReadAt<uint8_t*>(devConfig, offsets::CONFIG_BASE_ENTRIES);

            if (entries == nullptr)
                return;

            for (unsigned int i = 0; i < count; ++i)
            {
                auto* entry = entries + static_cast<size_t>(i) * offsets::CONFIG_ENTRY_SIZE;

                const auto name  = ReadAt<const char*>(entry, offsets::CONFIG_ENTRY_NAME);
                const auto value = ReadAt<void*>(entry, offsets::CONFIG_ENTRY_VALUE);

                if (name == nullptr || value == nullptr)
                    continue;

                if (strcmp(name, "GMServerHost") == 0)
                    CopyUtf8String(value, gm, capacity);
                else if (strcmp(name, "SaveDataBankHost") == 0)
                    CopyUtf8String(value, saveData, capacity);
                else if (strcmp(name, "LobbyHost01") == 0)
                    CopyUtf8String(value, lobby01, capacity);
            }
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            LogF("[game] 读 DevConfig 主机项异常 code=0x%08X", GetExceptionCode());
        }
    }

    // 在一段内存里找 ASCII 子串, 用来在偏移存疑时反查字段真正在哪
    int FindAscii(uint8_t* begin, size_t size, const char* needle, uintptr_t* hits, int maxHits)
    {
        int          found  = 0;
        const size_t length = strlen(needle);

        __try
        {
            for (size_t i = 0; i + length <= size && found < maxHits; ++i)
            {
                if (memcmp(begin + i, needle, length) == 0)
                    hits[found++] = i;
            }
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            // 读到不可读的页就停在已经找到的那些上
        }

        return found;
    }
}

std::string GameDump()
{
    if (!GameResolve())
        return "FAIL sigscan-failed";

    auto shared = std::make_shared<std::pair<ProbeData, bool>>();

    if (!MainThreadRun([shared] { shared->second = ProbeRaw(&shared->first); }, 3000))
        return "FAIL mainthread-timeout";

    const ProbeData& data = shared->first;

    if (!shared->second || data.networkModule == nullptr || data.agentLobby == nullptr)
        return "FAIL probe-failed";

    char active[256]{}, lobby0[256]{}, saveData[256]{}, session[256]{};

    const auto network = reinterpret_cast<uint8_t*>(data.networkModule);
    const auto lobby   = reinterpret_cast<uint8_t*>(data.agentLobby);

    DescribeUtf8String(active,   sizeof(active),   "ActiveLobbyHost",  network + offsets::NETWORK_MODULE_ACTIVE_LOBBY, true);
    DescribeUtf8String(lobby0,   sizeof(lobby0),   "LobbyHosts[0]",    network + offsets::NETWORK_MODULE_LOBBY_HOSTS,  true);
    DescribeUtf8String(saveData, sizeof(saveData), "SaveDataBankHost", network + offsets::NETWORK_MODULE_SAVE_DATA,    true);
    DescribeUtf8String(session,  sizeof(session),  "GameSession",      lobby   + offsets::AGENT_LOBBY_GAME_SESSION,    false);

    // 反查: NetworkModule 整块里还有没有 "ffxiv" 开头的主机名
    uintptr_t hits[8]{};
    const int hitCount = FindAscii(network, 0xC50, "ffxiv", hits, 8);

    char hitText[256] = "none";
    if (hitCount > 0)
    {
        int written = _snprintf_s(hitText, sizeof(hitText), _TRUNCATE, "%d 处:", hitCount);

        for (int i = 0; i < hitCount && written > 0 && written < static_cast<int>(sizeof(hitText)) - 12; ++i)
            written += _snprintf_s(hitText + written, sizeof(hitText) - written, _TRUNCATE, " +0x%llX",
                                   static_cast<unsigned long long>(hits[i]));
    }

    struct DevHosts { char gm[160]; char saveData[160]; char lobby01[160]; void* framework; };

    auto dev = std::make_shared<DevHosts>();
    dev->gm[0] = dev->saveData[0] = dev->lobby01[0] = '\0';
    dev->framework = data.framework;

    MainThreadRun([dev] { CopyDevConfigHosts(dev->framework, dev->gm, dev->saveData, dev->lobby01, sizeof(dev->gm)); }, 3000);

    const char* devGm        = dev->gm;
    const char* devSaveData  = dev->saveData;
    const char* devLobby01   = dev->lobby01;

    char response[1600];
    _snprintf_s(response, sizeof(response), _TRUNCATE,
                "OK %s %s %s %s ffxivInNetworkModule=%s "
                "devConfig{GMServerHost=%s SaveDataBankHost=%s LobbyHost01=%s}",
                active, lobby0, saveData, session, hitText,
                devGm[0]        ? devGm        : "(空)",
                devSaveData[0]  ? devSaveData  : "(空)",
                devLobby01[0]   ? devLobby01   : "(空)");

    LogF("[game] DUMP → %s", response);
    return response;
}

// =============================================================================
// 换服原语 —— 时序规格 = DCTraveler 的 GameFunctions.cs, 逐条搬成 C++。
// 每条都是独立命令, 编排（什么时候返回标题、等多久、什么时候点登录）留给启动器,
// 这样每一步都能单独观察, 也符合「在线那半留在 C#, native 只被命令」的分工。
// =============================================================================

namespace
{
    using SetStringFn          = void  (*)(void* utf8String, const char* value);
    using ReturnToTitleFn      = void  (*)(void* agentLobby);
    using ReleaseLobbyFn       = void  (*)(void* networkModule);
    using GetAddonByNameFn     = void* (*)(void* unitManager, const char* name, int index);
    using GetComponentButtonFn = void* (*)(void* addon, unsigned int nodeId);
    using ReceiveEventFn       = void  (*)(void* addon, int eventType, int eventParam, void* atkEvent, void* eventData);

    uintptr_t g_getAddonByName      = 0;
    uintptr_t g_getComponentButton  = 0;
    uintptr_t g_processChatBoxEntry = 0;
    uintptr_t g_utf8Ctor            = 0;
    uintptr_t g_utf8Dtor            = 0;
    uintptr_t g_fireCallbackInt     = 0;
    uintptr_t g_handleLogout        = 0;

    struct Pointers
    {
        void* framework;
        void* uiModule;
        void* agentLobby;
        void* networkModule;
        void* unitManager; // AtkStage → RaptureAtkUnitManager, 找 addon 用
    };

    bool GetPointers(Pointers* out)
    {
        __try
        {
            memset(out, 0, sizeof(Pointers));

            out->framework = *reinterpret_cast<void**>(g_frameworkStatic);
            if (out->framework == nullptr)
                return false;

            out->uiModule = ReadAt<void*>(out->framework, offsets::FRAMEWORK_UI_MODULE);
            if (out->uiModule == nullptr)
                return false;

            const auto vtable = ReadAt<void**>(out->uiModule, 0);
            const auto getAgentModule =
                reinterpret_cast<void* (*)(void*)>(vtable[offsets::UI_MODULE_GET_AGENT_MODULE_VF]);

            const auto agentModule = getAgentModule(out->uiModule);
            if (agentModule == nullptr)
                return false;

            out->agentLobby = ReadAt<void*>(agentModule,
                                            offsets::AGENT_MODULE_AGENTS + offsets::AGENT_ID_LOBBY * sizeof(void*));

            const auto proxy = ReadAt<void*>(out->framework, offsets::FRAMEWORK_NETWORK_MODULE_PROXY);
            if (proxy != nullptr)
                out->networkModule = ReadAt<void*>(proxy, offsets::NETWORK_MODULE_PROXY_MODULE);

            // addon 相关的走 AtkStage, 取不到不算失败（换服那几步用不着它）
            if (g_atkStageStatic != 0)
            {
                const auto stage = *reinterpret_cast<void**>(g_atkStageStatic);

                if (stage != nullptr)
                    out->unitManager = ReadAt<void*>(stage, offsets::ATK_STAGE_UNIT_MANAGER);
            }

            return out->agentLobby != nullptr && out->networkModule != nullptr;
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            return false;
        }
    }

    // GameFunctions.ChangeGameServer: 三个 NetworkModule 字段 + DevConfig 里的三项
    int OpSetHosts(const Pointers* p, const char* lobbyHost, const char* saveDataHost, const char* gmHost)
    {
        __try
        {
            const auto setString = reinterpret_cast<SetStringFn>(g_setString);
            const auto network   = reinterpret_cast<uint8_t*>(p->networkModule);

            setString(network + offsets::NETWORK_MODULE_ACTIVE_LOBBY, lobbyHost);
            setString(network + offsets::NETWORK_MODULE_LOBBY_HOSTS,  lobbyHost);
            setString(network + offsets::NETWORK_MODULE_SAVE_DATA,    saveDataHost);

            int written = 3;

            const auto devConfig = reinterpret_cast<uint8_t*>(p->framework) + offsets::FRAMEWORK_DEV_CONFIG;
            const auto count     = ReadAt<unsigned int>(devConfig, offsets::CONFIG_BASE_COUNT);
            const auto entries   = ReadAt<uint8_t*>(devConfig, offsets::CONFIG_BASE_ENTRIES);

            if (entries == nullptr)
                return written;

            for (unsigned int i = 0; i < count; ++i)
            {
                auto* entry = entries + static_cast<size_t>(i) * offsets::CONFIG_ENTRY_SIZE;

                const auto name  = ReadAt<const char*>(entry, offsets::CONFIG_ENTRY_NAME);
                const auto value = ReadAt<void*>(entry, offsets::CONFIG_ENTRY_VALUE);

                if (name == nullptr || value == nullptr)
                    continue;

                if (strcmp(name, "GMServerHost") == 0)
                    setString(value, gmHost);
                else if (strcmp(name, "SaveDataBankHost") == 0)
                    setString(value, saveDataHost);
                else if (strcmp(name, "LobbyHost01") == 0)
                    setString(value, lobbyHost);
                else
                    continue;

                ++written;
            }

            return written;
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            LogF("[game] SETHOSTS 异常 code=0x%08X", GetExceptionCode());
            return -1;
        }
    }

    // GameFunctions.RefreshGameServer: 作废缓存的大厅上下文 —— P3 实测证明这步是必需的,
    // 光改主机名不做这步, title→login 会复用旧连接, 大区根本不变
    int OpRelease(const Pointers* p)
    {
        __try
        {
            reinterpret_cast<ReleaseLobbyFn>(g_releaseLobbyContext)(p->networkModule);

            const auto lobby = reinterpret_cast<uint8_t*>(p->agentLobby);
            *reinterpret_cast<void**>(lobby + offsets::AGENT_LOBBY_UI_CLIENT_CONTEXT)       = nullptr;
            *reinterpret_cast<unsigned char*>(lobby + offsets::AGENT_LOBBY_UI_CLIENT_STATE) = 0;

            return 1;
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            LogF("[game] RELEASE 异常 code=0x%08X", GetExceptionCode());
            return -1;
        }
    }

    // 当前处在哪: 3=在游戏里, 2=角色选择, 1=标题菜单, 0=都不是(片头动画/读盘/过场), -1=读不到
    //
    // ⚠ 「在游戏里」必须读 AgentLobby.IsLoggedIn, 不能靠 addon 反推。2026-08-15 实测:
    //   客户端闲置后会飘进片头动画, 那时 _TitleMenu / _CharaSelectListMenu 都不在,
    //   旧写法（两个都没有就算 ingame）会把动画误判成在游戏里 —— 编排就会在动画里去调登出。
    int OpWhere(const Pointers* p)
    {
        __try
        {
            if (p->agentLobby != nullptr)
            {
                const auto loggedIn = ReadAt<unsigned char>(p->agentLobby, offsets::AGENT_LOBBY_IS_LOGGED_IN);
                const auto inZone   = ReadAt<unsigned char>(p->agentLobby, offsets::AGENT_LOBBY_IS_LOGGED_INTO_ZONE);

                if (loggedIn != 0 || inZone != 0)
                    return 3;
            }

            if (p->unitManager == nullptr)
                return -1;

            const auto find = reinterpret_cast<GetAddonByNameFn>(g_getAddonByName);

            if (find(p->unitManager, "_CharaSelectListMenu", 1) != nullptr)
                return 2;

            if (find(p->unitManager, "_TitleMenu", 1) != nullptr)
                return 1;

            return 0;
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            return -1;
        }
    }

    int OpReturnToTitle(const Pointers* p)
    {
        __try
        {
            // ⚠ 2026-08-15 两次实测: **在世界里**调这个函数必崩 (C0000005, 崩在主线程 tick 里),
            //   换成 Tick hook 也一样 —— 它不是执行点的问题, 而是 returnToTitle 属于大厅上下文,
            //   在世界里根本不该被调。DCTraveler 的判定与此一致 (TravelContextResolver.cs:66):
            //   只有 _CharaSelectListMenu 存在时它才 ReturnToTitle。这里照抄那条闸。
            const int where = OpWhere(p);

            if (where != 2 && where != 1)
                return 0; // 不在角色选择/标题界面 -> 拒绝执行

            reinterpret_cast<ReturnToTitleFn>(g_returnToTitle)(p->agentLobby);
            return 1;
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            LogF("[game] RETURNTITLE 异常 code=0x%08X", GetExceptionCode());
            return -1;
        }
    }

    // GameFunctions.ChangeDEVTestSID
    int OpSetSid(const Pointers* p, const char* sid)
    {
        __try
        {
            reinterpret_cast<SetStringFn>(g_setString)(
                reinterpret_cast<uint8_t*>(p->agentLobby) + offsets::AGENT_LOBBY_GAME_SESSION, sid);
            return 1;
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            LogF("[game] SETSID 异常 code=0x%08X", GetExceptionCode());
            return -1;
        }
    }

    int OpResetIdleTime(const Pointers* p)
    {
        __try
        {
            *reinterpret_cast<long long*>(reinterpret_cast<uint8_t*>(p->agentLobby) + offsets::AGENT_LOBBY_IDLE_TIME) = 0;
            return 1;
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            return -1;
        }
    }

    // returnToTitle 是异步的（游戏要花几秒回到标题）, 编排侧靠这个判断什么时候能往下走
    int OpTitleReady(const Pointers* p)
    {
        __try
        {
            if (p->unitManager == nullptr)
                return -1;

            return reinterpret_cast<GetAddonByNameFn>(g_getAddonByName)(p->unitManager, "_TitleMenu", 1) != nullptr ? 1 : 0;
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            return -1;
        }
    }

    // GameFunctions.LoginInGame: 给 _TitleMenu 的 4 号按钮发一次 ButtonClick
    int OpLogin(const Pointers* p)
    {
        __try
        {
            if (p->unitManager == nullptr)
                return -1;

            const auto addon = reinterpret_cast<GetAddonByNameFn>(g_getAddonByName)(p->unitManager, "_TitleMenu", 1);
            if (addon == nullptr)
                return 0; // 不在标题界面

            const auto button = reinterpret_cast<GetComponentButtonFn>(g_getComponentButton)(
                addon, offsets::TITLE_MENU_LOGIN_BUTTON_ID);
            if (button == nullptr)
                return 0;

            const auto resNode = ReadAt<void*>(button, offsets::ATK_COMPONENT_BASE_RES_NODE);
            if (resNode == nullptr)
                return 0;

            const auto atkEvent = ReadAt<void*>(resNode, offsets::ATK_RES_NODE_EVENT_MANAGER);

            const auto vtable      = ReadAt<void**>(addon, 0);
            const auto receiveEvent = reinterpret_cast<ReceiveEventFn>(vtable[offsets::ATK_UNIT_BASE_RECEIVE_EVENT_VF]);

            receiveEvent(addon, offsets::ATK_EVENT_TYPE_BUTTON_CLICK, 1, atkEvent, nullptr);
            return 1;
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            LogF("[game] LOGIN 异常 code=0x%08X", GetExceptionCode());
            return -1;
        }
    }

    // ---- 游戏内登出 --------------------------------------------------------
    // returnToTitle 在世界里调必崩, 所以这里走游戏自己的路: 发 /logout 文本命令,
    // 确认那个 Yes/No, 然后等游戏倒数完自己回到角色选择界面。
    using ProcessChatBoxEntryFn = void (*)(void* uiModule, void* message, void* a3, bool saveToHistory);
    using Utf8CtorFn            = void* (*)(void* self);
    using Utf8DtorFn            = void  (*)(void* self);
    using FireCallbackIntFn     = bool  (*)(void* addon, int value);

    int OpSendLogoutCommand(const Pointers* p)
    {
        __try
        {
            if (OpWhere(p) != 3)
                return 0; // 不在世界里, 不用登出

            // Utf8String 放栈上: "/logout" 只有 7 字节, 走的是它自带的内联缓冲, 不会另外分配堆内存
            unsigned char message[offsets::UTF8_STRING_SIZE]{};

            reinterpret_cast<Utf8CtorFn>(g_utf8Ctor)(message);
            reinterpret_cast<SetStringFn>(g_setString)(message, "/logout");
            reinterpret_cast<ProcessChatBoxEntryFn>(g_processChatBoxEntry)(p->uiModule, message, nullptr, false);
            reinterpret_cast<Utf8DtorFn>(g_utf8Dtor)(message);

            return 1;
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            LogF("[game] 发送 /logout 异常 code=0x%08X", GetExceptionCode());
            return -1;
        }
    }

    // 更底层的一条: 直接调游戏自己的登出处理函数, 不发文本命令也不弹确认框
    using HandleLogoutFn = void (*)(void* agentLobby, bool isExiting, unsigned char countdown);

    int OpDirectLogout(const Pointers* p)
    {
        __try
        {
            if (OpWhere(p) != 3)
                return 0; // 不在世界里, 不用登出

            // isExiting=false: 登出到角色选择, 而不是退出游戏
            reinterpret_cast<HandleLogoutFn>(g_handleLogout)(p->agentLobby, false, 0);
            return 1;
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            LogF("[game] HandleLogout 异常 code=0x%08X", GetExceptionCode());
            return -1;
        }
    }

    // 退出游戏: 同一个处理函数, isExiting=true —— 游戏在「确定要结束游戏吗？」点了确定之后走的就是它,
    // 先向服务器登出再退出进程。不在世界里时返回 0, 由调用方直接结束进程;
    // 读不到界面位置返回 -2, 登出函数没解析到返回 -3。
    int OpExitGame(const Pointers* p)
    {
        __try
        {
            const int where = OpWhere(p);

            if (where < 0)
                return -2;

            if (where != 3)
                return 0;

            if (g_handleLogout == 0 || p->agentLobby == nullptr)
                return -3;

            reinterpret_cast<HandleLogoutFn>(g_handleLogout)(p->agentLobby, true, 0);
            return 1;
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            LogF("[game] HandleLogout(退出) 异常 code=0x%08X", GetExceptionCode());
            return -1;
        }
    }

    // 是不是正在放片头动画。判据是 MovieStaffList 这个 addon 在不在 ——
    // 实测动画期间它在、标题菜单时不在。
    // ⚠ 不能用「where==busy」当判据: 登录读盘、过场也都是 busy, 那时候投 ESC 是误伤。
    int OpIsTitleMovie(const Pointers* p)
    {
        __try
        {
            if (p->unitManager == nullptr)
                return -1;

            return reinterpret_cast<GetAddonByNameFn>(g_getAddonByName)(p->unitManager, "MovieStaffList", 1) != nullptr ? 1 : 0;
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            return -1;
        }
    }

    // 确认登出对话框。FireCallbackInt(0) = 「是」, 比按节点 id 找按钮稳
    int OpConfirmYesNo(const Pointers* p)
    {
        __try
        {
            if (p->unitManager == nullptr)
                return -1;

            const auto addon = reinterpret_cast<GetAddonByNameFn>(g_getAddonByName)(p->unitManager, "SelectYesno", 1);

            if (addon == nullptr)
                return 0; // 对话框还没出来

            reinterpret_cast<FireCallbackIntFn>(g_fireCallbackInt)(addon, offsets::SELECT_YESNO_YES);
            return 1;
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            LogF("[game] 确认对话框异常 code=0x%08X", GetExceptionCode());
            return -1;
        }
    }


    // 派到主线程去跑的 job, 捕获的东西必须自己活着 —— 超时之后调用方就返回了, 而 job 可能
    // 下一帧才被 tick 跑到; 早期版本按引用捕栈变量, 那就是往野指针上写。统一放堆上传值捕获。
    struct CallState
    {
        Pointers  pointers{};
        ProbeData probe{};
        int       result = -1;
        bool      ok     = false;

        std::string lobbyHost;
        std::string saveDataHost;
        std::string gmHost;

        char  text[1400]{};
        void* unitManager = nullptr;
    };

    using CallStatePtr = std::shared_ptr<CallState>;

    // 所有换服命令共用的前置: 特征码解析 + 指针取全, 缺一不可
    bool PrepareCall(CallStatePtr& state, std::string& failure)
    {
        if (!GameResolve())
        {
            failure = "FAIL sigscan-failed";
            return false;
        }

        state = std::make_shared<CallState>();
        auto captured = state;

        if (!MainThreadRun([captured] { captured->ok = GetPointers(&captured->pointers); }, 3000))
        {
            failure = "FAIL mainthread-timeout";
            return false;
        }

        if (!state->ok)
        {
            failure = "FAIL pointers-unavailable";
            return false;
        }

        return true;
    }
}

std::string GameSetHosts(const std::string& lobbyHost, const std::string& saveDataHost, const std::string& gmHost)
{
    CallStatePtr state;
    std::string  failure;

    if (!PrepareCall(state, failure))
        return failure;

    state->lobbyHost    = lobbyHost;
    state->saveDataHost = saveDataHost;
    state->gmHost       = gmHost;

    auto captured = state;

    if (!MainThreadRun([captured]
        {
            captured->result = OpSetHosts(&captured->pointers,
                                          captured->lobbyHost.c_str(),
                                          captured->saveDataHost.c_str(),
                                          captured->gmHost.c_str());
        }, 5000))
        return "FAIL mainthread-timeout";

    if (state->result < 0)
        return "FAIL exception";

    LogF("[game] SETHOSTS lobby=%s sdb=%s gm=%s → 写了 %d 处",
         lobbyHost.c_str(), saveDataHost.c_str(), gmHost.c_str(), state->result);

    char response[64];
    _snprintf_s(response, sizeof(response), _TRUNCATE, "OK written=%d", state->result);
    return response;
}

std::string GameReturnToTitle()
{
    CallStatePtr state;
    std::string  failure;

    if (!PrepareCall(state, failure))
        return failure;

    auto captured = state;

    if (!MainThreadRun([captured] { captured->result = OpReturnToTitle(&captured->pointers); }, 5000))
        return "FAIL mainthread-timeout";

    if (state->result == 1)
        return "OK";

    // 拒绝执行不是错误, 是保护 —— 调用方（编排）该先让角色以正常途径登出到角色选择界面
    if (state->result == 0)
        return "FAIL not-at-charaselect";

    return "FAIL exception";
}

// 游戏内登出到角色选择界面。三步:
//   1. 发 /logout —— 走游戏自己的登出流程, 而不是硬调 returnToTitle（那个在世界里必崩)
//   2. 确认弹出来的 Yes/No
//   3. 等游戏倒数完、真的到了角色选择界面
std::string GameLogout(bool direct)
{
    CallStatePtr state;
    std::string  failure;

    if (!PrepareCall(state, failure))
        return failure;

    {
        auto captured = state;
        auto step     = direct
                            ? std::function<void()>([captured] { captured->result = OpDirectLogout(&captured->pointers); })
                            : std::function<void()>([captured] { captured->result = OpSendLogoutCommand(&captured->pointers); });

        if (!MainThreadRun(step, 5000))
            return "FAIL mainthread-timeout";
    }

    if (state->result < 0)
        return "FAIL exception";

    if (state->result == 0)
        return "OK already-out"; // 本来就不在世界里

    LogF("[game] 已发起登出 (%s)", direct ? "HandleLogout 直调" : "/logout 文本命令");

    // 直调那条不弹确认框, 只有文本命令那条要确认
    bool confirmed = direct;

    for (int i = 0; i < 60 && !confirmed; ++i)
    {
        auto step = std::make_shared<CallState>();
        step->pointers = state->pointers;

        if (MainThreadRun([step] { step->result = OpConfirmYesNo(&step->pointers); }, 3000) && step->result == 1)
            confirmed = true;
        else
            Sleep(250);
    }

    if (!confirmed)
        LogF("[game] ⚠ 没等到登出确认框（可能这个客户端不弹确认, 继续等界面变化)");

    // 游戏登出有几秒倒数, 给 60 秒
    for (int i = 0; i < 120; ++i)
    {
        auto step = std::make_shared<CallState>();
        step->pointers = state->pointers;

        if (MainThreadRun([step] { step->result = OpWhere(&step->pointers); }, 3000))
        {
            if (step->result == 2) { LogF("[game] LOGOUT 完成: 已到角色选择界面"); return "OK where=charaselect"; }
            if (step->result == 1) { LogF("[game] LOGOUT 完成: 已到标题界面");     return "OK where=title"; }
        }

        Sleep(500);
    }

    return "FAIL logout-timeout";
}

// 下号退出游戏。只发起、不等: 进程退不退由启动器看。
//   OK exiting       在世界里, 已调 HandleLogout(isExiting=true)
//   OK not-in-world  标题 / 选角 / 片头, 没有要登出的角色
// 主线程只等 3 秒: 下号时等不到就让启动器改发关闭消息, 不占着整个关闭时限
std::string GameExit()
{
    CallStatePtr state;
    std::string  failure;

    if (!PrepareCall(state, failure))
        return failure;

    auto captured = state;

    if (!MainThreadRun([captured] { captured->result = OpExitGame(&captured->pointers); }, 3000))
        return "FAIL mainthread-timeout";

    if (state->result == -2)
        return "FAIL where-unknown";

    if (state->result == -3)
        return "FAIL sigscan-failed";

    if (state->result < 0)
        return "FAIL exception";

    if (state->result == 0)
        return "OK not-in-world";

    LogF("[game] 已发起退出游戏 (HandleLogout isExiting=1)");
    return "OK exiting";
}

namespace
{
    struct FindWindowContext { DWORD processId; HWND found; };

    BOOL CALLBACK FindWindowProc(HWND hwnd, LPARAM param)
    {
        auto* context = reinterpret_cast<FindWindowContext*>(param);

        DWORD owner = 0;
        GetWindowThreadProcessId(hwnd, &owner);

        if (owner != context->processId)
            return TRUE;

        wchar_t className[64]{};
        GetClassNameW(hwnd, className, 64);

        if (lstrcmpiW(className, L"FFXIVGAME") != 0)
            return TRUE;

        context->found = hwnd;
        return FALSE;
    }

    HWND FindGameWindow()
    {
        FindWindowContext context {GetCurrentProcessId(), nullptr};
        EnumWindows(FindWindowProc, reinterpret_cast<LPARAM>(&context));
        return context.found;
    }
}

// 结束片头动画。客户端在标题界面闲置久了（IdleTime 涨到两万上下）会自己飘进去, 刚启动时也会先放一段;
// 动画期间 _TitleMenu 不存在, 换服那几步全都做不了。
//
// 游戏没有「跳过动画」的可调函数 —— 那东西就是任意输入就结束。所以这里给游戏窗口投一次 ESC,
// 和玩家按键走的是同一条消息路径（PostMessage 是异步的, 不占主线程)。
std::string GameSkipMovie()
{
    const HWND hwnd = FindGameWindow();

    if (hwnd == nullptr)
        return "FAIL no-window";

    for (int attempt = 0; attempt < 20; ++attempt)
    {
        const auto where = GameWhere();

        if (where.find("where=title") != std::string::npos ||
            where.find("where=charaselect") != std::string::npos ||
            where.find("where=ingame") != std::string::npos)
        {
            LogF("[game] SKIPMOVIE: %s (投了 %d 次 ESC)", where.c_str(), attempt);
            return where;
        }

        PostMessageW(hwnd, WM_KEYDOWN, VK_ESCAPE, 0x00010001);
        PostMessageW(hwnd, WM_KEYUP,   VK_ESCAPE, 0xC0010001);

        Sleep(500);
    }

    LogF("[game] SKIPMOVIE: 投了 20 次 ESC 仍没回到可操作界面");
    return "FAIL still-busy";
}

std::string GameWhere()
{
    CallStatePtr state;
    std::string  failure;

    if (!PrepareCall(state, failure))
        return failure;

    auto captured = state;

    if (!MainThreadRun([captured] { captured->result = OpWhere(&captured->pointers); }, 3000))
        return "FAIL mainthread-timeout";

    switch (state->result)
    {
        case 3:  return "OK where=ingame";
        case 2:  return "OK where=charaselect";
        case 1:  return "OK where=title";
        case 0:  return "OK where=busy"; // 片头动画 / 读盘 / 过场 —— 什么都别做, 等它稳定
        default: return "FAIL unknown";
    }
}

// =============================================================================
// WHOLIST —— 选角界面的角色列表（只读）
//
// WHY: 标题/选角界面 Lua 的 Player 无效, 拿不到角色名, 启动器的 /areas 就查不了。
// 游戏自己的选角列表里有全部需要的东西: 名字、ContentId(=SDO roleId)、当前/原始世界的 ID 和名字、
// LoginFlags（超域中）。世界名直接取条目里的字符串 —— 启动器那边没有世界 ID→名字的表。
//
// ⚠ 列表只在「角色选择」界面可信: 回到标题后向量里还留着上一个大区的旧列表,
//   换过登录大区之后那份就是错的。所以同时回 where, 由调用方决定信不信。
// =============================================================================

namespace
{
    constexpr int WHOLIST_MAX = 48; // 一个账号在一个大区最多 8 服 × 8 角色, 48 够用且响应不超 8KB

    struct CharaRow
    {
        unsigned long long contentId;
        unsigned char      index;
        unsigned char      loginFlags;
        unsigned short     currentWorldId;
        unsigned short     homeWorldId;
        char               name[offsets::CHARA_ENTRY_NAME_LEN + 1];
        char               currentWorldName[offsets::CHARA_ENTRY_NAME_LEN + 1];
        char               homeWorldName[offsets::CHARA_ENTRY_NAME_LEN + 1];
        unsigned           clickFlags; // 只有自动选角的命令填（大区角色条目 +0x74C）
    };

    struct WhoListData
    {
        int                where;
        int                total;   // 向量里一共几个（可能 > WHOLIST_MAX）
        int                count;   // 实际拷出几个
        int                selectedIndex;
        int                hoveredIndex;
        unsigned long long selectedContentId;
        unsigned long long hoveredContentId;
        CharaRow           rows[WHOLIST_MAX];
    };

    // 定长 char[32] → 以 0 结尾的串; 顺手把制表符/换行换成空格, 免得弄乱行格式
    void CopyFixedString(const uint8_t* source, char* destination)
    {
        size_t length = strnlen(reinterpret_cast<const char*>(source), offsets::CHARA_ENTRY_NAME_LEN);
        memcpy(destination, source, length);
        destination[length] = '\0';

        for (size_t i = 0; i < length; ++i)
        {
            if (destination[i] == '\t' || destination[i] == '\r' || destination[i] == '\n')
                destination[i] = ' ';
        }
    }

    bool OpWhoList(const Pointers* p, WhoListData* out)
    {
        memset(out, 0, sizeof(WhoListData));
        out->where = OpWhere(p);

        __try
        {
            const auto lobby = reinterpret_cast<uint8_t*>(p->agentLobby);
            if (lobby == nullptr)
                return false;

            out->selectedIndex     = ReadAt<unsigned char>(lobby, offsets::AGENT_LOBBY_SELECTED_CHARA_INDEX);
            out->hoveredIndex      = ReadAt<signed char>(lobby, offsets::AGENT_LOBBY_HOVERED_CHARA_INDEX);
            out->selectedContentId = ReadAt<unsigned long long>(lobby, offsets::AGENT_LOBBY_SELECTED_CONTENT_ID);
            out->hoveredContentId  = ReadAt<unsigned long long>(lobby, offsets::AGENT_LOBBY_HOVERED_CONTENT_ID);

            const auto vector = lobby + offsets::AGENT_LOBBY_CHARA_SELECT_ENTRIES;
            const auto first  = ReadAt<uint8_t**>(vector, offsets::STD_VECTOR_FIRST);
            const auto last   = ReadAt<uint8_t**>(vector, offsets::STD_VECTOR_LAST);

            if (first == nullptr || last == nullptr || last < first)
                return true; // 空列表不是错误

            out->total = static_cast<int>(last - first);

            // 超过一千个指针一定是读歪了, 宁可报空也别往下扫
            if (out->total > 1000)
            {
                out->total = 0;
                return false;
            }

            for (int i = 0; i < out->total && out->count < WHOLIST_MAX; ++i)
            {
                const auto entry = first[i];
                if (entry == nullptr)
                    continue;

                auto& row = out->rows[out->count++];
                row.contentId      = ReadAt<unsigned long long>(entry, offsets::CHARA_ENTRY_CONTENT_ID);
                row.index          = ReadAt<unsigned char>(entry, offsets::CHARA_ENTRY_INDEX);
                row.loginFlags     = ReadAt<unsigned char>(entry, offsets::CHARA_ENTRY_LOGIN_FLAGS);
                row.currentWorldId = ReadAt<unsigned short>(entry, offsets::CHARA_ENTRY_CURRENT_WORLD);
                row.homeWorldId    = ReadAt<unsigned short>(entry, offsets::CHARA_ENTRY_HOME_WORLD);

                CopyFixedString(entry + offsets::CHARA_ENTRY_NAME,               row.name);
                CopyFixedString(entry + offsets::CHARA_ENTRY_CURRENT_WORLD_NAME, row.currentWorldName);
                CopyFixedString(entry + offsets::CHARA_ENTRY_HOME_WORLD_NAME,    row.homeWorldName);
            }

            return true;
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            LogF("[game] WHOLIST 异常 code=0x%08X", GetExceptionCode());
            return false;
        }
    }

    const char* WhereName(int where)
    {
        switch (where)
        {
            case 3:  return "ingame";
            case 2:  return "charaselect";
            case 1:  return "title";
            case 0:  return "busy";
            default: return "unknown";
        }
    }
}

// =============================================================================
// 自动选角 —— 选角界面的只读状态与单步操作
//
// 分工: 这里每条命令只做一步、立即返回; 等待、重试、判断（等确认框、等排队、等进游戏）全在启动器那边。
// 操作命令（FOCUSCHARA / SELECTCHARA / ENTERCHARA / DIALOG）动手前先过界面闸, 不该动的时候一律拒绝。
// 读结构体时做合理性校验, 读歪了回 FAIL 而不是错数据 —— 偏移的出处与核对状态见 offsets.h「自动选角」一节。
// =============================================================================

namespace
{
    using FireCallbackFn = bool (*)(void* addon, unsigned int count, void* values, bool close);

    // 这两条特征码不在 GameResolve 的必中集合里: 用到时才解析, 命中不了只让依赖它的命令失败
    uintptr_t g_fireCallback      = 0;
    bool      g_fireCallbackTried = false;
    uintptr_t g_playerState       = 0;
    bool      g_playerStateTried  = false;

    bool ResolveFireCallback()
    {
        if (!g_fireCallbackTried)
        {
            g_fireCallbackTried = true;
            g_fireCallback      = ScanText(offsets::FIRE_CALLBACK_SIG);

            LogF("[game]   FireCallback            RVA=0x%llX",
                 static_cast<unsigned long long>(g_fireCallback ? g_fireCallback - ModuleBase() : 0));
        }

        return g_fireCallback != 0;
    }

    bool ResolvePlayerState()
    {
        if (!g_playerStateTried)
        {
            g_playerStateTried = true;
            g_playerState      = ScanStaticAddress(offsets::PLAYER_STATE_SIG, offsets::PLAYER_STATE_SIG_OFFSET);

            LogF("[game]   PlayerState 静态地址    RVA=0x%llX",
                 static_cast<unsigned long long>(g_playerState ? g_playerState - ModuleBase() : 0));
        }

        return g_playerState != 0;
    }

    // 堆对象指针的合理性: 非空、落在用户态地址范围内、按 8 字节对齐
    bool PlausiblePointer(const void* pointer)
    {
        const auto value = reinterpret_cast<uintptr_t>(pointer);
        return value >= 0x10000 && value < 0x00007FFFFFFF0000ULL && (value & 7) == 0;
    }

    // 读一个 std::vector 的元素个数。空向量返回 0; 指针不合理、长度不是元素大小的整数倍、
    // 或元素数超过上限都返回 -1（读歪了）
    int VectorCount(uint8_t* vector, size_t elementSize, int maxCount, uint8_t** firstOut)
    {
        *firstOut = nullptr;

        const auto first = ReadAt<uint8_t*>(vector, offsets::STD_VECTOR_FIRST);
        const auto last  = ReadAt<uint8_t*>(vector, offsets::STD_VECTOR_LAST);
        const auto end   = ReadAt<uint8_t*>(vector, offsets::STD_VECTOR_END);

        if (first == nullptr && last == nullptr)
            return 0;

        if (!PlausiblePointer(first) || last < first || end < last)
            return -1;

        const auto bytes = static_cast<size_t>(last - first);

        if (bytes % elementSize != 0 || bytes / elementSize > static_cast<size_t>(maxCount))
            return -1;

        *firstOut = first;
        return static_cast<int>(bytes / elementSize);
    }

    // 从 text 起的一个合法 UTF-8 序列占几个字节; 不合法返回 0
    size_t Utf8SequenceLength(const uint8_t* text, size_t remaining)
    {
        const uint8_t lead = text[0];
        size_t        need = 0;

        if (lead < 0x80)
            return 1;

        if (lead >= 0xC2 && lead <= 0xDF)
            need = 2;
        else if (lead >= 0xE0 && lead <= 0xEF)
            need = 3;
        else if (lead >= 0xF0 && lead <= 0xF4)
            need = 4;
        else
            return 0;

        if (remaining < need)
            return 0;

        for (size_t i = 1; i < need; ++i)
        {
            if ((text[i] & 0xC0) != 0x80)
                return 0;
        }

        // 过长编码、代理区、超出 U+10FFFF
        if ((lead == 0xE0 && text[1] < 0xA0) || (lead == 0xED && text[1] > 0x9F) ||
            (lead == 0xF0 && text[1] < 0x90) || (lead == 0xF4 && text[1] > 0x8F))
            return 0;

        return need;
    }

    // 定长名字字段 → 以 0 结尾的串。必须在 capacity 字节内以 0 结尾、是合法 UTF-8、不含控制字符,
    // 否则返回 false（读歪了）。destination 至少要有 capacity 字节
    bool CopyCheckedName(const uint8_t* source, size_t capacity, char* destination, bool allowEmpty)
    {
        destination[0] = '\0';

        const size_t length = strnlen(reinterpret_cast<const char*>(source), capacity);

        if (length >= capacity)
            return false;

        if (length == 0)
            return allowEmpty;

        for (size_t i = 0; i < length;)
        {
            if (source[i] < 0x20 || source[i] == 0x7F)
                return false;

            const size_t step = Utf8SequenceLength(source + i, length - i);
            if (step == 0)
                return false;

            i += step;
        }

        memcpy(destination, source, length);
        destination[length] = '\0';
        return true;
    }

    // 把一个大区角色条目（LobbyUIClientCharacterEntry, 0x758 字节）读成一行。
    // 当前服务器列表里的指针指向的也是这种条目（见 FindListCharacter）, 调用方已核对过指针落在大区角色列表里
    bool ReadCharaRow(uint8_t* entry, CharaRow* row)
    {
        row->contentId      = ReadAt<unsigned long long>(entry, offsets::CHARA_ENTRY_CONTENT_ID);
        row->index          = ReadAt<unsigned char>(entry, offsets::CHARA_ENTRY_INDEX);
        row->loginFlags     = ReadAt<unsigned char>(entry, offsets::CHARA_ENTRY_LOGIN_FLAGS);
        row->currentWorldId = ReadAt<unsigned short>(entry, offsets::CHARA_ENTRY_CURRENT_WORLD);
        row->homeWorldId    = ReadAt<unsigned short>(entry, offsets::CHARA_ENTRY_HOME_WORLD);
        row->clickFlags     = ReadAt<unsigned>(entry, offsets::DC_CHARA_ENTRY_CLICK_FLAGS);

        if (row->contentId == 0)
            return false;

        return CopyCheckedName(entry + offsets::CHARA_ENTRY_NAME, offsets::CHARA_ENTRY_NAME_LEN, row->name, false) &&
               CopyCheckedName(entry + offsets::CHARA_ENTRY_CURRENT_WORLD_NAME, offsets::CHARA_ENTRY_NAME_LEN,
                               row->currentWorldName, true) &&
               CopyCheckedName(entry + offsets::CHARA_ENTRY_HOME_WORLD_NAME, offsets::CHARA_ENTRY_NAME_LEN,
                               row->homeWorldName, true);
    }

    // 大区角色列表里客户端自己不认的条目（空位、已删除、或 ContentId 与镜像对不上）—— 它按序号取条目时查的就是这三样
    bool DcEntryUsable(uint8_t* entry)
    {
        const auto contentId = ReadAt<unsigned long long>(entry, offsets::CHARA_ENTRY_CONTENT_ID);

        return contentId != 0 &&
               ReadAt<unsigned char>(entry, offsets::DC_CHARA_ENTRY_DELETED_FLAG) == 0 &&
               ReadAt<unsigned long long>(entry, offsets::DC_CHARA_ENTRY_CONTENT_ID_MIRROR) == contentId;
    }

    bool RowMatches(const CharaRow* row, const char* name, unsigned long long contentId)
    {
        return contentId != 0 ? row->contentId == contentId : strcmp(row->name, name) == 0;
    }

    constexpr int FIND_OK        = 1;
    constexpr int FIND_NONE      = 0;
    constexpr int FIND_BAD_LIST  = -2; // 向量读歪
    constexpr int FIND_BAD_ENTRY = -3; // 条目读歪
    constexpr int FIND_AMBIGUOUS = -4; // 按名字找到不止一个

    // 在整个大区的角色列表（CurrentDataCenterCharacters, 元素内联）里找角色
    int FindDcCharacter(uint8_t* lobby, const char* name, unsigned long long contentId, CharaRow* out)
    {
        uint8_t*  first = nullptr;
        const int count = VectorCount(lobby + offsets::AGENT_LOBBY_DC_CHARACTERS, offsets::DC_CHARA_ENTRY_SIZE,
                                      offsets::LOBBY_MAX_CHARACTERS, &first);
        if (count < 0)
            return FIND_BAD_LIST;

        int found = 0;

        for (int i = 0; i < count; ++i)
        {
            const auto entry = first + static_cast<size_t>(i) * offsets::DC_CHARA_ENTRY_SIZE;

            if (!DcEntryUsable(entry))
                continue;

            CharaRow row;
            if (!ReadCharaRow(entry, &row))
                return FIND_BAD_ENTRY;

            if (!RowMatches(&row, name, contentId))
                continue;

            if (++found > 1)
                return FIND_AMBIGUOUS;

            *out = row;
        }

        return found;
    }

    // 指针是不是正好指着大区角色列表里的某个条目
    bool PointsIntoDcCharacters(const uint8_t* entry, const uint8_t* dcFirst, int dcCount)
    {
        if (dcFirst == nullptr || entry < dcFirst)
            return false;

        const auto offset = static_cast<size_t>(entry - dcFirst);

        return offset < static_cast<size_t>(dcCount) * offsets::DC_CHARA_ENTRY_SIZE && offset % offsets::DC_CHARA_ENTRY_SIZE == 0;
    }

    // 在当前显示的角色列表（CharaSelectEntries, 元素是指针）里找角色; position = 它在向量里的位置。
    // 客户端按回调里的序号取角色时用的就是这个位置（本机 exe RVA 0x4AF110: 列表[序号], 不看条目自己的 Index 字段）。
    // 列表里放的是大区角色列表（CurrentDataCenterCharacters）里条目的地址（RVA 0x4AE770）:
    // 指针不落在那个向量的元素边界上, 说明这份列表是旧的或者读歪了, 一律不用。
    int FindListCharacter(uint8_t* lobby, const char* name, unsigned long long contentId, CharaRow* out, int* position)
    {
        uint8_t*  first = nullptr;
        const int count = VectorCount(lobby + offsets::AGENT_LOBBY_CHARA_SELECT_ENTRIES, sizeof(void*),
                                      offsets::LOBBY_MAX_CHARACTERS, &first);
        if (count < 0)
            return FIND_BAD_LIST;

        uint8_t*  dcFirst = nullptr;
        const int dcCount = VectorCount(lobby + offsets::AGENT_LOBBY_DC_CHARACTERS, offsets::DC_CHARA_ENTRY_SIZE,
                                        offsets::LOBBY_MAX_CHARACTERS, &dcFirst);
        if (dcCount < 0)
            return FIND_BAD_LIST;

        int found = 0;

        for (int i = 0; i < count; ++i)
        {
            const auto entry = ReadAt<uint8_t*>(first, static_cast<uintptr_t>(i) * sizeof(void*));

            if (entry == nullptr)
                continue;

            if (!PointsIntoDcCharacters(entry, dcFirst, dcCount))
                return FIND_BAD_ENTRY;

            CharaRow row;
            if (!ReadCharaRow(entry, &row))
                return FIND_BAD_ENTRY;

            if (!RowMatches(&row, name, contentId))
                continue;

            if (++found > 1)
                return FIND_AMBIGUOUS;

            // 客户端自己不认的条目（已删除 / ContentId 镜像对不上）按序号取不出来, 发了回调也选不中
            if (!DcEntryUsable(entry))
                return FIND_BAD_ENTRY;

            *out      = row;
            *position = i;
        }

        return found;
    }

    // 客户端处理选角回调时, 先按 WorldIndex 在大区服务器表里取服务器, 再取那个服务器的角色列表。
    // 这里要求三处记的是同一个服务器: 服务器表[WorldIndex]、WorldId、存着的角色列表所属的服务器 ——
    // 对不上时列表里的位置对回调没有意义（客户端会当场重建列表）。
    bool ListBelongsToCurrentWorld(uint8_t* lobby, unsigned short* world, unsigned short* listWorld)
    {
        *world     = ReadAt<unsigned short>(lobby, offsets::AGENT_LOBBY_WORLD_ID);
        *listWorld = ReadAt<unsigned short>(lobby, offsets::AGENT_LOBBY_CHARA_SELECT_ENTRIES_WORLD);

        if (*world == 0 || *listWorld != *world)
            return false;

        uint8_t*  worlds     = nullptr;
        const int worldCount = VectorCount(lobby + offsets::AGENT_LOBBY_DC_WORLDS, offsets::DC_WORLD_ENTRY_SIZE,
                                           offsets::LOBBY_MAX_WORLDS, &worlds);
        const int worldIndex = ReadAt<short>(lobby, offsets::AGENT_LOBBY_WORLD_INDEX);

        if (worldCount <= 0 || worldIndex < 0 || worldIndex >= worldCount)
            return false;

        return ReadAt<unsigned short>(worlds + static_cast<size_t>(worldIndex) * offsets::DC_WORLD_ENTRY_SIZE,
                                      offsets::DC_WORLD_ENTRY_ID) == *world;
    }

    // 登录确认框答「是」时（回调类别 3, 本机 exe RVA 0x4E2D11）客户端登录的是「当前服务器角色列表[SelectedCharacterIndex]」——
    // 答的那一刻按 WorldIndex 和序号现取, 不是点角色时记下来的。这里按同样的办法取一遍, 返回它的 ContentId; 取不出来返回 0。
    unsigned long long ContentIdAboutToLogin(uint8_t* lobby)
    {
        unsigned short world     = 0;
        unsigned short listWorld = 0;

        if (!ListBelongsToCurrentWorld(lobby, &world, &listWorld))
            return 0;

        uint8_t*  first = nullptr;
        const int count = VectorCount(lobby + offsets::AGENT_LOBBY_CHARA_SELECT_ENTRIES, sizeof(void*),
                                      offsets::LOBBY_MAX_CHARACTERS, &first);

        uint8_t*  dcFirst = nullptr;
        const int dcCount = VectorCount(lobby + offsets::AGENT_LOBBY_DC_CHARACTERS, offsets::DC_CHARA_ENTRY_SIZE,
                                        offsets::LOBBY_MAX_CHARACTERS, &dcFirst);

        const int index = ReadAt<signed char>(lobby, offsets::AGENT_LOBBY_SELECTED_CHARA_INDEX);

        if (count <= 0 || dcCount <= 0 || index < 0 || index >= count)
            return 0;

        const auto entry = ReadAt<uint8_t*>(first, static_cast<uintptr_t>(index) * sizeof(void*));

        if (!PointsIntoDcCharacters(entry, dcFirst, dcCount) || !DcEntryUsable(entry))
            return 0;

        return ReadAt<unsigned long long>(entry, offsets::CHARA_ENTRY_CONTENT_ID);
    }

    void* FindAddon(const Pointers* p, const char* name)
    {
        if (p->unitManager == nullptr)
            return nullptr;

        const auto addon = reinterpret_cast<GetAddonByNameFn>(g_getAddonByName)(p->unitManager, name, 1);
        return PlausiblePointer(addon) ? addon : nullptr;
    }

    // 选角界面上开着是/否框、确定框或错误框: 有人（或客户端自己）正在处理别的事, 切服务器、选角色、点角色都不该插进去
    bool AnyLobbyDialog(const Pointers* p)
    {
        return FindAddon(p, "SelectYesno") != nullptr || FindAddon(p, "SelectOk") != nullptr ||
               FindAddon(p, "Dialogue") != nullptr;
    }

    // AtkValue{ Int }: +0 Type(u32)=3, +8 值
    void SetIntValue(uint8_t* value, int number)
    {
        memset(value, 0, offsets::ATK_VALUE_SIZE);
        *reinterpret_cast<unsigned*>(value) = offsets::ATK_VALUE_TYPE_INT;
        *reinterpret_cast<int*>(value + 8)  = number;
    }

    // 对 addon 发一条全是整数的回调（最多 3 个值）
    bool FireInts(void* addon, const int* numbers, unsigned int count)
    {
        uint8_t values[3 * offsets::ATK_VALUE_SIZE];

        if (count > 3)
            return false;

        for (unsigned int i = 0; i < count; ++i)
            SetIntValue(values + i * offsets::ATK_VALUE_SIZE, numbers[i]);

        return reinterpret_cast<FireCallbackFn>(g_fireCallback)(addon, count, values, true);
    }

    // ---- 对话框 --------------------------------------------------------------

    constexpr size_t DIALOG_TEXT_MAX = 600;

    struct DialogInfo
    {
        int      present;
        unsigned id;
        int      ready;
        int      visible;
        char     text[DIALOG_TEXT_MAX];
    };

    // 提示文字要进行式协议: 控制字符（含换行、制表符、文本宏的起止字节）换成空格, 不合法的 UTF-8 字节换成 '?'
    void SanitizeText(char* text)
    {
        const size_t length = strlen(text);

        for (size_t i = 0; i < length;)
        {
            const auto current = reinterpret_cast<uint8_t*>(text) + i;

            if (*current < 0x20 || *current == 0x7F)
            {
                text[i++] = ' ';
                continue;
            }

            const size_t step = Utf8SequenceLength(current, length - i);

            if (step == 0)
            {
                text[i++] = '?';
                continue;
            }

            i += step;
        }
    }

    // addon 的 AtkValues 里第一个非空字符串（只看前 8 个）
    void CopyFirstStringValue(void* addon, char* destination, size_t capacity)
    {
        const auto values = ReadAt<uint8_t*>(addon, offsets::ATK_UNIT_BASE_ATK_VALUES);
        const auto count  = ReadAt<unsigned short>(addon, offsets::ATK_UNIT_BASE_ATK_VALUES_COUNT);

        if (!PlausiblePointer(values))
            return;

        for (unsigned i = 0; i < count && i < 8; ++i)
        {
            const auto value = values + i * offsets::ATK_VALUE_SIZE;
            const auto type  = ReadAt<unsigned>(value, 0) & offsets::ATK_VALUE_TYPE_MASK;

            if (type != offsets::ATK_VALUE_TYPE_STRING && type != offsets::ATK_VALUE_TYPE_CONST_STRING)
                continue;

            const auto text = ReadAt<const char*>(value, 8);

            if (reinterpret_cast<uintptr_t>(text) < 0x10000 || text[0] == '\0')
                continue;

            const size_t length = strnlen(text, capacity - 1);
            memcpy(destination, text, length);
            destination[length] = '\0';
            return;
        }
    }

    // 读对话框的提示文字。文字只是给启动器判断用的附加信息, 读不到或读歪了留空, 不让整条命令失败。
    // SelectYesno / SelectOk 先取提示文字节点, 取不到再看 AtkValues; Dialogue 只看 AtkValues
    void ReadDialogText(void* addon, bool hasPromptNode, char* destination, size_t capacity)
    {
        destination[0] = '\0';

        __try
        {
            if (hasPromptNode)
            {
                const auto node = ReadAt<uint8_t*>(addon, offsets::ADDON_SELECT_PROMPT_TEXT);

                if (PlausiblePointer(node))
                    CopyUtf8String(node + offsets::ATK_TEXT_NODE_NODE_TEXT, destination, capacity);
            }

            if (destination[0] == '\0')
                CopyFirstStringValue(addon, destination, capacity);
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            destination[0] = '\0';
        }

        SanitizeText(destination);
    }

    void ReadDialog(const Pointers* p, const char* name, bool hasPromptNode, DialogInfo* out)
    {
        const auto addon = FindAddon(p, name);

        if (addon == nullptr)
            return;

        out->present = 1;
        out->id      = ReadAt<unsigned short>(addon, offsets::ATK_UNIT_BASE_ID);
        out->ready   = (ReadAt<unsigned char>(addon, offsets::ATK_UNIT_BASE_FLAGS1A1) >> offsets::ATK_UNIT_BASE_READY_BIT) & 1;
        out->visible = (ReadAt<unsigned>(addon, offsets::ATK_UNIT_BASE_FLAGS198) >> offsets::ATK_UNIT_BASE_VISIBLE_BIT) & 1;

        ReadDialogText(addon, hasPromptNode, out->text, sizeof(out->text));
    }

    // ---- LOBBYSTATE ----------------------------------------------------------

    struct LobbyStateData
    {
        int                where;
        unsigned           world;
        int                worldIndex;
        int                selectedIndex;
        unsigned long long hovered;
        int                locked;
        unsigned           updateStage;
        unsigned           uiStage;
        int                queue;
        unsigned           dialogId;
        unsigned           listWorld;
        int                loading;
        DialogInfo         yesno;
        DialogInfo         ok;
        DialogInfo         dialogue;
    };

    bool OpLobbyState(const Pointers* p, LobbyStateData* out)
    {
        memset(out, 0, sizeof(LobbyStateData));
        out->where = OpWhere(p);

        __try
        {
            const auto lobby = reinterpret_cast<uint8_t*>(p->agentLobby);

            out->world         = ReadAt<unsigned short>(lobby, offsets::AGENT_LOBBY_WORLD_ID);
            out->worldIndex    = ReadAt<short>(lobby, offsets::AGENT_LOBBY_WORLD_INDEX);
            out->selectedIndex = ReadAt<unsigned char>(lobby, offsets::AGENT_LOBBY_SELECTED_CHARA_INDEX);
            out->hovered       = ReadAt<unsigned long long>(lobby, offsets::AGENT_LOBBY_HOVERED_CONTENT_ID);
            out->locked        = ReadAt<unsigned char>(lobby, offsets::AGENT_LOBBY_TEMPORARY_LOCKED) != 0 ? 1 : 0;
            out->updateStage   = ReadAt<unsigned char>(lobby, offsets::AGENT_LOBBY_UPDATE_STAGE);
            out->uiStage       = ReadAt<unsigned char>(lobby, offsets::AGENT_LOBBY_UI_STAGE);
            out->queue         = ReadAt<int>(lobby, offsets::AGENT_LOBBY_QUEUE_POSITION);
            out->dialogId      = ReadAt<unsigned>(lobby, offsets::AGENT_LOBBY_DIALOG_ADDON_ID);
            out->listWorld     = ReadAt<unsigned short>(lobby, offsets::AGENT_LOBBY_CHARA_SELECT_ENTRIES_WORLD);

            ReadDialog(p, "SelectYesno", true,  &out->yesno);
            ReadDialog(p, "SelectOk",    true,  &out->ok);
            ReadDialog(p, "Dialogue",    false, &out->dialogue);

            // NowLoading 常驻, 读盘时才显示, 所以要看可见位而不是在不在
            const auto loading = FindAddon(p, "NowLoading");

            if (loading != nullptr)
                out->loading = (ReadAt<unsigned>(loading, offsets::ATK_UNIT_BASE_FLAGS198) >> offsets::ATK_UNIT_BASE_VISIBLE_BIT) & 1;

            return true;
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            LogF("[game] LOBBYSTATE 异常 code=0x%08X", GetExceptionCode());
            return false;
        }
    }

    void AppendDialogLine(std::string& response, const char* name, const DialogInfo& dialog)
    {
        if (dialog.present == 0)
            return;

        char line[DIALOG_TEXT_MAX + 96];
        _snprintf_s(line, sizeof(line), _TRUNCATE, "\nD\t%s\t%u\t%d\t%d\t%s", name, dialog.id, dialog.ready, dialog.visible,
                    dialog.text);
        response += line;
    }

    // ---- DIALOG --------------------------------------------------------------

    constexpr int DIALOG_YES = 0;
    constexpr int DIALOG_NO  = 1;
    constexpr int DIALOG_OK  = 2;

    // 节点事件链上的那条 ButtonClick
    void* FindButtonClickEvent(void* node)
    {
        if (!PlausiblePointer(node))
            return nullptr;

        auto atkEvent = ReadAt<void*>(node, offsets::ATK_RES_NODE_EVENT_MANAGER);

        for (int i = 0; i < 16 && PlausiblePointer(atkEvent); ++i)
        {
            if (ReadAt<unsigned char>(atkEvent, offsets::ATK_EVENT_TYPE) == offsets::ATK_EVENT_TYPE_BUTTON_CLICK)
                return atkEvent;

            atkEvent = ReadAt<void*>(atkEvent, offsets::ATK_EVENT_NEXT);
        }

        return nullptr;
    }

    // 返回: 1=点了 SelectYesno 2=点了 SelectOk 3=点了 Dialogue 0=没有对应的对话框
    //       -1=异常 -2=那是大厅自己开的确定框（排队提示）, 不点 -3=在游戏里, 不点 -4=Dialogue 的确定按钮找不到
    //       -5=不在选角界面（是/否框只在那里点） -6=是/否框不是大厅为登录开的那个 -7=登录请求已经发出（这时的是/否框是在问要不要取消登录）
    //       -8=答「是」会登录的不是指定的角色 -9=是/否框还没就绪
    //
    // 「是」等于替人确认登录, 所以它的全部前提在这同一次主线程执行里现查, 不信启动器上一次读到的状态:
    //   在选角界面; 是/否框是大厅自己开的那个（AgentLobby.DialogAddonId, 点角色的处理函数 RVA 0x4D4C80 把登录确认框的 id 写在那里）;
    //   登录请求还没发出（TemporaryLocked 为 0; 确认登录时客户端把它置 1, 之后出现的是/否框是取消登录的确认）;
    //   客户端这时会登录的角色（当前服务器角色列表[SelectedCharacterIndex], 见 ContentIdAboutToLogin）就是启动器要登录的那个。
    int OpDialog(const Pointers* p, int action, unsigned long long expectedContentId)
    {
        const int where = OpWhere(p);

        __try
        {
            // 游戏里的是/否框可能是任何东西（交易、丢弃物品…）, 这条命令只为登录流程服务
            if (where == 3)
                return -3;

            const auto fireInt = reinterpret_cast<FireCallbackIntFn>(g_fireCallbackInt);
            const auto lobby   = reinterpret_cast<uint8_t*>(p->agentLobby);

            if (action == DIALOG_YES || action == DIALOG_NO)
            {
                if (where != 2)
                    return -5;

                const auto yesno = FindAddon(p, "SelectYesno");

                if (yesno == nullptr)
                    return 0;

                if (((ReadAt<unsigned char>(yesno, offsets::ATK_UNIT_BASE_FLAGS1A1) >> offsets::ATK_UNIT_BASE_READY_BIT) & 1) == 0)
                    return -9;

                if (action == DIALOG_YES)
                {
                    const auto dialogId = ReadAt<unsigned>(lobby, offsets::AGENT_LOBBY_DIALOG_ADDON_ID);

                    if (dialogId == 0 || dialogId != ReadAt<unsigned short>(yesno, offsets::ATK_UNIT_BASE_ID))
                        return -6;

                    if (ReadAt<unsigned char>(lobby, offsets::AGENT_LOBBY_TEMPORARY_LOCKED) != 0)
                        return -7;

                    if (ContentIdAboutToLogin(lobby) != expectedContentId)
                        return -8;
                }

                fireInt(yesno, action == DIALOG_YES ? offsets::SELECT_YESNO_YES : offsets::SELECT_YESNO_NO);
                return 1;
            }

            const auto dialogue = FindAddon(p, "Dialogue");

            if (dialogue != nullptr)
            {
                const auto button = reinterpret_cast<GetComponentButtonFn>(g_getComponentButton)(
                    dialogue, offsets::DIALOGUE_OK_BUTTON_ID);

                if (!PlausiblePointer(button))
                    return -4;

                auto atkEvent = FindButtonClickEvent(ReadAt<void*>(button, offsets::ATK_COMPONENT_BASE_OWNER_NODE));

                if (atkEvent == nullptr)
                    atkEvent = FindButtonClickEvent(ReadAt<void*>(button, offsets::ATK_COMPONENT_BASE_RES_NODE));

                if (atkEvent == nullptr)
                    return -4;

                // 事件数据给一块全 0 的缓冲, 处理函数要读它时不至于解空指针
                unsigned char eventData[0x40]{};

                const auto vtable       = ReadAt<void**>(dialogue, 0);
                const auto receiveEvent = reinterpret_cast<ReceiveEventFn>(vtable[offsets::ATK_UNIT_BASE_RECEIVE_EVENT_VF]);

                receiveEvent(dialogue, offsets::ATK_EVENT_TYPE_BUTTON_CLICK,
                             static_cast<int>(ReadAt<unsigned>(atkEvent, offsets::ATK_EVENT_PARAM)), atkEvent, eventData);
                return 3;
            }

            const auto ok = FindAddon(p, "SelectOk");

            if (ok == nullptr)
                return 0;

            // 排队提示也是 SelectOk, 点它等于取消排队。大厅自己开的那个框（DialogAddonId 对得上）一律不点 ——
            // 名次读不到（还没下发、或偏移不对）时它照样是排队框; 有排队名次时不管是哪个框也不点
            const auto dialogId = ReadAt<unsigned>(lobby, offsets::AGENT_LOBBY_DIALOG_ADDON_ID);
            const auto queue    = ReadAt<int>(lobby, offsets::AGENT_LOBBY_QUEUE_POSITION);

            if ((dialogId != 0 && dialogId == ReadAt<unsigned short>(ok, offsets::ATK_UNIT_BASE_ID)) || queue > 0)
                return -2;

            fireInt(ok, offsets::SELECT_OK_OK);
            return 2;
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            LogF("[game] DIALOG 异常 code=0x%08X", GetExceptionCode());
            return -1;
        }
    }

    // ---- CHARAS --------------------------------------------------------------

    struct CharaListData
    {
        int                where;
        int                status;  // 1=正常 -1=异常 -2=向量读歪 -3=条目读歪（badIndex 是第几个）
        int                badIndex;
        int                total;   // 向量里一共几个
        int                count;   // 拷出几个
        int                skipped; // 客户端自己不认的条目（空位 / 已删除 / ContentId 镜像对不上）
        int                invalid; // skipped 里 ContentId 不为 0 的那些: 不是空位, 是已删除或读歪了
        int                selectedIndex;
        int                hoveredIndex;
        unsigned long long selectedContentId;
        unsigned long long hoveredContentId;
        CharaRow           rows[offsets::LOBBY_MAX_CHARACTERS];
    };

    void OpCharas(const Pointers* p, CharaListData* out)
    {
        memset(out, 0, sizeof(CharaListData));
        out->where  = OpWhere(p);
        out->status = -1;

        __try
        {
            const auto lobby = reinterpret_cast<uint8_t*>(p->agentLobby);

            out->selectedIndex     = ReadAt<unsigned char>(lobby, offsets::AGENT_LOBBY_SELECTED_CHARA_INDEX);
            out->hoveredIndex      = ReadAt<signed char>(lobby, offsets::AGENT_LOBBY_HOVERED_CHARA_INDEX);
            out->selectedContentId = ReadAt<unsigned long long>(lobby, offsets::AGENT_LOBBY_SELECTED_CONTENT_ID);
            out->hoveredContentId  = ReadAt<unsigned long long>(lobby, offsets::AGENT_LOBBY_HOVERED_CONTENT_ID);

            uint8_t* first = nullptr;
            out->total     = VectorCount(lobby + offsets::AGENT_LOBBY_DC_CHARACTERS, offsets::DC_CHARA_ENTRY_SIZE,
                                         offsets::LOBBY_MAX_CHARACTERS, &first);
            if (out->total < 0)
            {
                out->total  = 0;
                out->status = -2;
                return;
            }

            for (int i = 0; i < out->total; ++i)
            {
                const auto entry = first + static_cast<size_t>(i) * offsets::DC_CHARA_ENTRY_SIZE;

                if (!DcEntryUsable(entry))
                {
                    ++out->skipped;

                    if (ReadAt<unsigned long long>(entry, offsets::CHARA_ENTRY_CONTENT_ID) != 0)
                        ++out->invalid;

                    continue;
                }

                if (!ReadCharaRow(entry, &out->rows[out->count]))
                {
                    out->badIndex = i;
                    out->status   = -3;
                    return;
                }

                ++out->count;
            }

            out->status = 1;
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            LogF("[game] CHARAS 异常 code=0x%08X", GetExceptionCode());
            out->status = -1;
        }
    }

    void AppendCharaLine(std::string& response, const CharaRow& row)
    {
        char line[256];
        _snprintf_s(line, sizeof(line), _TRUNCATE, "\nC\t%llu\t%u\t%u\t%u\t%u\t%s\t%s\t%s\t%u",
                    row.contentId, row.index, row.loginFlags, row.currentWorldId, row.homeWorldId,
                    row.name, row.currentWorldName, row.homeWorldName, row.clickFlags);
        response += line;
    }

    // ---- FOCUSCHARA ----------------------------------------------------------
    // 选角界面切到「这个角色所在的服务器」。跨完大区点「开始游戏」进选角界面, 游戏显示的是它自己记住的服务器,
    // 不一定是角色所在的那个, 角色列表里就看不到它。

    struct FocusResult
    {
        int            status; // 2=已切换 1=本来就是 0=大区里没这个角色（或列表还没载入） -1=异常 -2=服务器列表不在
                               // -3=目标服务器不在本大区 -4=不在选角界面 -5=列表读歪 -6=同名不止一个 -7=发了回调但没切过去
                               // -8=界面上开着对话框
        unsigned short targetWorld;
        unsigned short beforeWorld;
        unsigned short afterWorld;
        int            slot;
    };

    void OpFocusCharacter(const Pointers* p, const char* name, unsigned long long contentId, FocusResult* out)
    {
        memset(out, 0, sizeof(FocusResult));
        out->slot = -1;

        const int where = OpWhere(p);

        __try
        {
            if (where != 2)
            {
                out->status = -4;
                return;
            }

            const auto lobby = reinterpret_cast<uint8_t*>(p->agentLobby);

            CharaRow  row{};
            const int found = FindDcCharacter(lobby, name, contentId, &row);

            if (found != FIND_OK)
            {
                out->status = found == FIND_NONE ? 0 : found == FIND_AMBIGUOUS ? -6 : -5;
                return;
            }

            // 超域中的角色列在它当前所在的服务器下, 其余（包括同大区跨服中的）列在原始服务器下
            out->targetWorld = row.loginFlags == offsets::LOGIN_FLAG_DC_TRAVELING ? row.currentWorldId : row.homeWorldId;
            out->beforeWorld = ReadAt<unsigned short>(lobby, offsets::AGENT_LOBBY_WORLD_ID);
            out->afterWorld  = out->beforeWorld;

            if (out->beforeWorld == out->targetWorld)
            {
                out->status = 1;
                return;
            }

            uint8_t*  worlds     = nullptr;
            const int worldCount = VectorCount(lobby + offsets::AGENT_LOBBY_DC_WORLDS, offsets::DC_WORLD_ENTRY_SIZE,
                                               offsets::LOBBY_MAX_WORLDS, &worlds);
            if (worldCount < 0)
            {
                out->status = -5;
                return;
            }

            for (int i = 0; i < worldCount && out->slot < 0; ++i)
            {
                const auto world = worlds + static_cast<size_t>(i) * offsets::DC_WORLD_ENTRY_SIZE;

                if (ReadAt<unsigned short>(world, offsets::DC_WORLD_ENTRY_ID) == out->targetWorld)
                    out->slot = i;
            }

            if (out->slot < 0)
            {
                out->status = -3;
                return;
            }

            const auto addon = FindAddon(p, "_CharaSelectWorldServer");

            if (addon == nullptr)
            {
                out->status = -2;
                return;
            }

            if (AnyLobbyDialog(p))
            {
                out->status = -8;
                return;
            }

            const int values[3] = {offsets::LOBBY_EVENT_SELECT_WORLD, 0, out->slot};
            FireInts(addon, values, 3);

            out->afterWorld = ReadAt<unsigned short>(lobby, offsets::AGENT_LOBBY_WORLD_ID);
            out->status     = out->afterWorld == out->targetWorld ? 2 : -7;
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            LogF("[game] FOCUSCHARA 异常 code=0x%08X", GetExceptionCode());
            out->status = -1;
        }
    }

    // ---- SELECTCHARA / ENTERCHARA --------------------------------------------

    struct CharaActionResult
    {
        int                status; // 1=成功 0=当前列表里没这个角色 -1=异常 -2=不在选角界面 -3=列表读歪 -4=同名不止一个
                                   // -6=发了选中回调但客户端选中的不是它 -7=暂时锁定 -8=角色带不可登录标志
                                   // -9=存着的角色列表不是当前服务器的 -10=点击之后客户端选中的不是它 -11=界面上开着对话框
                                   // -12=点这个角色会先弹别的是/否框（不是登录确认框）
        unsigned           clickFlags;
        unsigned short     world;
        unsigned short     listWorld;
        int                position;
        int                entryIndex;
        unsigned           loginFlags;
        unsigned long long contentId;
        int                selectedIndex;
        unsigned long long hovered;
    };

    // enter=false: 只高亮（21, 序号）并回读确认;
    // enter=true:  先高亮并回读确认, 再左键点击（29, 0, 序号）并再回读一次, 之后客户端弹登录确认框。
    // 全部检查、两条回调、两次回读都在这同一次主线程执行里, 中间没有别的帧, 列表不会在检查和点击之间变掉。
    //
    // 序号口径（本机 exe 静态核对）: 回调 21 / 29 里的序号是「当前服务器角色列表」这个向量里的位置 ——
    //   客户端按 WorldIndex 取服务器、取它的列表、直接取 列表[序号]（RVA 0x4E19B2 / 0x4DFD40 / 0x4D4C80 / 0x4AF110）,
    //   条目自己的 Index 字段（+0x10）不参与, 所以这里也不拿它做判断, 只带回去记日志。
    // 回读: 客户端取到 列表[序号] 后把它的 ContentId 写进 HoveredCharacterContentId（21 与 29 都写）,
    //   读回来等于要找的角色, 就证明客户端选中的确实是它; 不等就绝不往下点。
    void OpCharaAction(const Pointers* p, const char* name, unsigned long long contentId, bool enter, CharaActionResult* out)
    {
        memset(out, 0, sizeof(CharaActionResult));
        out->position = -1;

        const int where = OpWhere(p);

        __try
        {
            const auto addon = where == 2 ? FindAddon(p, "_CharaSelectListMenu") : nullptr;

            if (addon == nullptr)
            {
                out->status = -2;
                return;
            }

            if (AnyLobbyDialog(p))
            {
                out->status = -11;
                return;
            }

            const auto lobby = reinterpret_cast<uint8_t*>(p->agentLobby);

            // 角色列表是按服务器现建现存的一份; 存着的不是当前服务器那份时, 里面的位置对回调没有意义
            if (!ListBelongsToCurrentWorld(lobby, &out->world, &out->listWorld))
            {
                out->status = -9;
                return;
            }

            CharaRow  row{};
            const int found = FindListCharacter(lobby, name, contentId, &row, &out->position);

            if (found != FIND_OK)
            {
                out->status = found == FIND_NONE ? 0 : found == FIND_AMBIGUOUS ? -4 : -3;
                return;
            }

            out->contentId  = row.contentId;
            out->entryIndex = row.index;
            out->loginFlags = row.loginFlags;
            out->clickFlags = row.clickFlags;

            if (enter)
            {
                if (ReadAt<unsigned char>(lobby, offsets::AGENT_LOBBY_TEMPORARY_LOCKED) != 0)
                {
                    out->status = -7;
                    return;
                }

                if ((row.loginFlags & offsets::LOGIN_FLAG_BLOCKING_MASK) != 0)
                {
                    out->status = -8;
                    return;
                }

                if ((row.clickFlags & offsets::DC_CHARA_ENTRY_CLICK_PROMPT_MASK) != 0)
                {
                    out->status = -12;
                    return;
                }
            }

            const int select[2] = {offsets::LOBBY_EVENT_SELECT_CHARA, out->position};
            FireInts(addon, select, 2);

            out->selectedIndex = ReadAt<unsigned char>(lobby, offsets::AGENT_LOBBY_SELECTED_CHARA_INDEX);
            out->hovered       = ReadAt<unsigned long long>(lobby, offsets::AGENT_LOBBY_HOVERED_CONTENT_ID);

            if (out->selectedIndex != out->position || out->hovered != row.contentId)
            {
                out->status = -6;
                return;
            }

            if (!enter)
            {
                out->status = 1;
                return;
            }

            const int click[3] = {offsets::LOBBY_EVENT_CLICK_CHARA, 0, out->position};
            FireInts(addon, click, 3);

            out->selectedIndex = ReadAt<unsigned char>(lobby, offsets::AGENT_LOBBY_SELECTED_CHARA_INDEX);
            out->hovered       = ReadAt<unsigned long long>(lobby, offsets::AGENT_LOBBY_HOVERED_CONTENT_ID);
            out->status        = out->selectedIndex == out->position && out->hovered == row.contentId ? 1 : -10;
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            LogF("[game] %s 异常 code=0x%08X", enter ? "ENTERCHARA" : "SELECTCHARA", GetExceptionCode());
            out->status = -1;
        }
    }

    // ---- WHOAMI --------------------------------------------------------------

    struct WhoAmIData
    {
        int                status; // 1=正常 -1=异常 -2=PlayerState 读歪 -3=角色名读歪
        int                loaded;
        int                loggedIn;
        int                inZone;
        unsigned long long contentId;
        unsigned long long lobbyContentId;
        unsigned short     currentWorldId;
        unsigned short     homeWorldId;
        char               name[offsets::PLAYER_STATE_NAME_LEN + 1];
        char               currentWorldName[offsets::CHARA_ENTRY_NAME_LEN + 1];
        char               homeWorldName[offsets::CHARA_ENTRY_NAME_LEN + 1];
    };

    // Utf8String 里的世界名 → 定长缓冲; 不是合法名字就留空
    void CopyWorldName(uint8_t* utf8String, char* destination)
    {
        char raw[offsets::CHARA_ENTRY_NAME_LEN]{};
        CopyUtf8String(utf8String, raw, sizeof(raw));

        if (!CopyCheckedName(reinterpret_cast<const uint8_t*>(raw), sizeof(raw), destination, true))
            destination[0] = '\0';
    }

    void OpWhoAmI(const Pointers* p, WhoAmIData* out)
    {
        memset(out, 0, sizeof(WhoAmIData));
        out->status = -1;

        __try
        {
            const auto lobby = reinterpret_cast<uint8_t*>(p->agentLobby);
            const auto state = reinterpret_cast<uint8_t*>(g_playerState);

            out->loggedIn = ReadAt<unsigned char>(lobby, offsets::AGENT_LOBBY_IS_LOGGED_IN) != 0 ? 1 : 0;
            out->inZone   = ReadAt<unsigned char>(lobby, offsets::AGENT_LOBBY_IS_LOGGED_INTO_ZONE) != 0 ? 1 : 0;

            const auto loaded = ReadAt<unsigned char>(state, offsets::PLAYER_STATE_IS_LOADED);

            if (loaded > 1)
            {
                out->status = -2;
                return;
            }

            if (loaded == 0)
            {
                out->status = 1;
                return;
            }

            out->contentId = ReadAt<unsigned long long>(state, offsets::PLAYER_STATE_CONTENT_ID);

            if (out->contentId == 0)
            {
                out->status = -2;
                return;
            }

            if (!CopyCheckedName(state + offsets::PLAYER_STATE_NAME, offsets::PLAYER_STATE_NAME_LEN, out->name, false))
            {
                out->status = -3;
                return;
            }

            out->loaded = 1;

            // 世界信息在大厅数据里, 只有登录之后才是这个角色的
            if (out->loggedIn != 0)
            {
                out->lobbyContentId = ReadAt<unsigned long long>(lobby, offsets::AGENT_LOBBY_LOGGED_CONTENT_ID);
                out->currentWorldId = ReadAt<unsigned short>(lobby, offsets::AGENT_LOBBY_CURRENT_WORLD_ID);
                out->homeWorldId    = ReadAt<unsigned short>(lobby, offsets::AGENT_LOBBY_HOME_WORLD_ID);

                CopyWorldName(lobby + offsets::AGENT_LOBBY_CURRENT_WORLD_NAME, out->currentWorldName);
                CopyWorldName(lobby + offsets::AGENT_LOBBY_HOME_WORLD_NAME,    out->homeWorldName);
            }

            out->status = 1;
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            LogF("[game] WHOAMI 异常 code=0x%08X", GetExceptionCode());
            out->status = -1;
        }
    }

    // 操作命令共用的前置; 失败时 failure 是要回给启动器的那一行
    bool PrepareAction(CallStatePtr& state, std::string& failure)
    {
        if (!PrepareCall(state, failure))
            return false;

        if (!ResolveFireCallback())
        {
            failure = "FAIL sigscan-failed:FireCallback";
            return false;
        }

        if (state->pointers.unitManager == nullptr)
        {
            failure = "FAIL no-unit-manager";
            return false;
        }

        return true;
    }

    // 命令参数: 纯数字当 ContentId, 否则当角色名
    unsigned long long ParseContentId(const std::string& who)
    {
        return who.find_first_not_of("0123456789") == std::string::npos ? _strtoui64(who.c_str(), nullptr, 10) : 0ULL;
    }

    std::string RunCharaAction(const std::string& who, bool enter)
    {
        const char* command = enter ? "ENTERCHARA" : "SELECTCHARA";

        if (who.empty())
            return std::string("FAIL usage: ") + command + " <角色名|contentId>";

        CallStatePtr state;
        std::string  failure;

        if (!PrepareAction(state, failure))
            return failure;

        const auto contentId = ParseContentId(who);

        auto result   = std::make_shared<CharaActionResult>();
        auto name     = std::make_shared<std::string>(who);
        auto captured = state;

        if (!MainThreadRun([captured, result, name, contentId, enter]
            {
                OpCharaAction(&captured->pointers, name->c_str(), contentId, enter, result.get());
            }, 3000))
            return "FAIL mainthread-timeout";

        LogF("[game] %s %s → status=%d position=%d entryIndex=%d cid=%llu flags=%u selectedIndex=%d hovered=%llu",
             command, who.c_str(), result->status, result->position, result->entryIndex, result->contentId,
             result->loginFlags, result->selectedIndex, result->hovered);

        char response[160];

        switch (result->status)
        {
            case 1:
                _snprintf_s(response, sizeof(response), _TRUNCATE, "OK %s index=%d cid=%llu",
                            enter ? "clicked" : "selected", result->position, result->contentId);
                return response;
            case 0:  return "FAIL not-in-list";     // 当前服务器的列表里没有, 先 FOCUSCHARA
            case -9:
                // 列表还没按当前服务器重建 —— 调用方可稍后重试
                _snprintf_s(response, sizeof(response), _TRUNCATE, "FAIL not-in-list listWorld=%u world=%u",
                            result->listWorld, result->world);
                return response;
            case -2: return "FAIL not-charaselect";
            case -3: return "FAIL bad-list";
            case -4: return "FAIL ambiguous";       // 同名不止一个, 改用 ContentId
            case -6:
                _snprintf_s(response, sizeof(response), _TRUNCATE, "FAIL not-applied selectedIndex=%d hovered=%llu",
                            result->selectedIndex, result->hovered);
                return response;
            case -7: return "FAIL locked";
            case -8:
                _snprintf_s(response, sizeof(response), _TRUNCATE, "FAIL flags=%u", result->loginFlags);
                return response;
            case -10:
                // 已经点了, 但客户端选中的不是要找的角色: 调用方绝不能再去点登录确认框
                _snprintf_s(response, sizeof(response), _TRUNCATE, "FAIL clicked-other selectedIndex=%d hovered=%llu",
                            result->selectedIndex, result->hovered);
                return response;
            case -11: return "FAIL dialog-open";    // 有人（或客户端）正在处理对话框, 不插手
            case -12:
                _snprintf_s(response, sizeof(response), _TRUNCATE, "FAIL special-prompt flags74c=%u", result->clickFlags);
                return response;
            default: return "FAIL exception";
        }
    }
}

// 响应: 第一行
//   OK where=<ingame|charaselect|title|busy> world=<WorldId> worldIndex=<n> selectedIndex=<n> hovered=<cid> locked=<0|1>
//      stage=<LobbyUpdateStage> uiStage=<LobbyUIStage> queue=<QueuePosition> dialogId=<DialogAddonId>
//      yesno=<0|1> ok=<0|1> dialogue=<0|1> loading=<0|1> listWorld=<存着的角色列表属于哪个服务器>
// 有对话框时, 每个在场的对话框一行（字段用 \t 分隔）:
//   D  <SelectYesno|SelectOk|Dialogue>  <addon id>  <ready 0|1>  <visible 0|1>  <提示文字>
// 最后一行 `T <提示文字>` 是其中最该看的那个: 是/否框优先, 其次错误框, 最后确定框。提示文字里的换行已换成空格。
std::string GameLobbyState()
{
    CallStatePtr state;
    std::string  failure;

    if (!PrepareCall(state, failure))
        return failure;

    auto data     = std::make_shared<LobbyStateData>();
    auto captured = state;

    if (!MainThreadRun([captured, data] { captured->ok = OpLobbyState(&captured->pointers, data.get()); }, 3000))
        return "FAIL mainthread-timeout";

    if (!state->ok)
        return "FAIL exception";

    if (data->where < 0)
        return "FAIL unknown";

    char header[400];
    _snprintf_s(header, sizeof(header), _TRUNCATE,
                "OK where=%s world=%u worldIndex=%d selectedIndex=%d hovered=%llu locked=%d stage=%u uiStage=%u "
                "queue=%d dialogId=%u yesno=%d ok=%d dialogue=%d loading=%d listWorld=%u",
                WhereName(data->where), data->world, data->worldIndex, data->selectedIndex, data->hovered, data->locked,
                data->updateStage, data->uiStage, data->queue, data->dialogId,
                data->yesno.present, data->ok.present, data->dialogue.present, data->loading, data->listWorld);

    std::string response = header;

    AppendDialogLine(response, "SelectYesno", data->yesno);
    AppendDialogLine(response, "SelectOk",    data->ok);
    AppendDialogLine(response, "Dialogue",    data->dialogue);

    const DialogInfo* primary = data->yesno.present    ? &data->yesno
                              : data->dialogue.present ? &data->dialogue
                              : data->ok.present       ? &data->ok
                                                       : nullptr;
    if (primary != nullptr)
    {
        response += "\nT ";
        response += primary->text;
    }

    return response;
}

// 整个大区的角色（CurrentDataCenterCharacters）。响应与 WHOLIST 同格式, 第一行多 source=dc、skipped、invalid:
//   OK where=charaselect n=3 total=3 selected=<cid> selectedIndex=0 hovered=<cid> hoveredIndex=-1 source=dc skipped=0 invalid=0
// skipped = 没列出来的条目数（空位、已删除、ContentId 镜像对不上）; invalid = 其中 ContentId 不为 0 的 ——
// invalid 不为 0 时这份列表不能当作「这个号的全部角色」来用。列不全（回应放不下）时整条回 FAIL too-large, 不给半张表。
//   C  <contentId>  <index>  <loginFlags>  <curWorldId>  <homeWorldId>  <名字>  <当前世界名>  <原始世界名>  <条目 +0x74C 的标志>
// 与 WHOLIST 一样只在选角界面可信, 所以同时回 where。
std::string GameCharas()
{
    CallStatePtr state;
    std::string  failure;

    if (!PrepareCall(state, failure))
        return failure;

    auto data     = std::make_shared<CharaListData>();
    auto captured = state;

    if (!MainThreadRun([captured, data] { OpCharas(&captured->pointers, data.get()); }, 3000))
        return "FAIL mainthread-timeout";

    if (data->status == -2)
        return "FAIL bad-list";

    if (data->status == -3)
    {
        char response[64];
        _snprintf_s(response, sizeof(response), _TRUNCATE, "FAIL bad-entry index=%d", data->badIndex);
        return response;
    }

    if (data->status != 1)
        return "FAIL exception";

    // 启动器一次读 8192 字节, 整条回应不能超
    std::string body;
    int         listed = 0;

    for (; listed < data->count && body.size() < 7600; ++listed)
        AppendCharaLine(body, data->rows[listed]);

    // 半张表会让启动器把「号里有好几个角色」看成「只有一个」, 宁可整条失败
    if (listed < data->count)
        return "FAIL too-large";

    char header[320];
    _snprintf_s(header, sizeof(header), _TRUNCATE,
                "OK where=%s n=%d total=%d selected=%llu selectedIndex=%d hovered=%llu hoveredIndex=%d source=dc skipped=%d invalid=%d",
                WhereName(data->where), listed, data->total, data->selectedContentId, data->selectedIndex,
                data->hoveredContentId, data->hoveredIndex, data->skipped, data->invalid);

    LogF("[game] CHARAS where=%s n=%d/%d skipped=%d invalid=%d", WhereName(data->where), listed, data->total, data->skipped,
         data->invalid);
    return header + body;
}

// 游戏内当前角色。响应只有一行, 角色名可能带空格所以放在行尾:
//   OK loaded=<0|1> cid=<ContentId> world=<CurrentWorldId> home=<HomeWorldId> worldName=<当前世界名> homeName=<原始世界名>
//      loggedIn=<0|1> inZone=<0|1> lobbyCid=<大厅数据里记的 ContentId> name=<名字>
// loaded=0 时数字字段都是 0、名字为空。world / home / 世界名 / lobbyCid 只在 loggedIn=1 时有值。
std::string GameWhoAmI()
{
    CallStatePtr state;
    std::string  failure;

    if (!PrepareCall(state, failure))
        return failure;

    if (!ResolvePlayerState())
        return "FAIL sigscan-failed:PlayerState";

    auto data     = std::make_shared<WhoAmIData>();
    auto captured = state;

    if (!MainThreadRun([captured, data] { OpWhoAmI(&captured->pointers, data.get()); }, 3000))
        return "FAIL mainthread-timeout";

    switch (data->status)
    {
        case 1:  break;
        case -2: return "FAIL bad-state";
        case -3: return "FAIL bad-name";
        default: return "FAIL exception";
    }

    char response[512];
    _snprintf_s(response, sizeof(response), _TRUNCATE,
                "OK loaded=%d cid=%llu world=%u home=%u worldName=%s homeName=%s loggedIn=%d inZone=%d lobbyCid=%llu name=%s",
                data->loaded, data->contentId, data->currentWorldId, data->homeWorldId, data->currentWorldName,
                data->homeWorldName, data->loggedIn, data->inZone, data->lobbyContentId, data->name);
    return response;
}

// 参数: 角色名, 或纯数字的 ContentId。角色在整个大区的列表里找。
std::string GameFocusCharacter(const std::string& who)
{
    if (who.empty())
        return "FAIL usage: FOCUSCHARA <角色名|contentId>";

    CallStatePtr state;
    std::string  failure;

    if (!PrepareAction(state, failure))
        return failure;

    const auto contentId = ParseContentId(who);

    auto result   = std::make_shared<FocusResult>();
    auto name     = std::make_shared<std::string>(who);
    auto captured = state;

    if (!MainThreadRun([captured, result, name, contentId]
        {
            OpFocusCharacter(&captured->pointers, name->c_str(), contentId, result.get());
        }, 3000))
        return "FAIL mainthread-timeout";

    LogF("[game] FOCUSCHARA %s → status=%d world %u→%u (目标 %u) slot=%d",
         who.c_str(), result->status, result->beforeWorld, result->afterWorld, result->targetWorld, result->slot);

    char response[128];

    switch (result->status)
    {
        case 2:
            _snprintf_s(response, sizeof(response), _TRUNCATE, "OK switched world=%u slot=%d", result->targetWorld, result->slot);
            return response;
        case 1:
            _snprintf_s(response, sizeof(response), _TRUNCATE, "OK already world=%u", result->targetWorld);
            return response;
        case 0:  return "FAIL not-in-list";       // 列表还没载入 / 这个大区没这个角色 —— 调用方可稍后重试
        case -2: return "FAIL no-world-list";     // 服务器列表还没出来 —— 调用方可稍后重试
        case -3:
            // 目标服务器不在本大区（超域中的角色在原始大区的大厅里就是这样）
            _snprintf_s(response, sizeof(response), _TRUNCATE, "FAIL world-not-listed world=%u", result->targetWorld);
            return response;
        case -4: return "FAIL not-charaselect";
        case -5: return "FAIL bad-list";
        case -6: return "FAIL ambiguous";         // 同名不止一个, 改用 ContentId
        case -7:
            _snprintf_s(response, sizeof(response), _TRUNCATE, "FAIL not-applied world=%u target=%u", result->afterWorld,
                        result->targetWorld);
            return response;
        case -8: return "FAIL dialog-open";       // 有人（或客户端）正在处理对话框, 不插手
        default: return "FAIL exception";
    }
}

// 当前服务器的角色列表里选中（高亮）一个角色, 不进入
std::string GameSelectCharacter(const std::string& who)
{
    return RunCharaAction(who, false);
}

// 左键点击一个角色: 客户端自己做检查并弹登录确认框, 之后由启动器看 LOBBYSTATE 再发 DIALOG YES
std::string GameEnterCharacter(const std::string& who)
{
    return RunCharaAction(who, true);
}

// arguments: "YES <contentId>" 点登录确认框的「是」（contentId = 要登录的角色, 客户端选中的不是它就不点）;
//            "NO" 点是/否框的「否」; "OK" 点错误框或确定框（大厅自己开的确定框即排队提示不点）
std::string GameDialog(const std::string& arguments)
{
    const auto space  = arguments.find(' ');
    const auto button = arguments.substr(0, space);
    const auto extra  = space == std::string::npos ? std::string() : arguments.substr(space + 1);

    const int action = button == "YES" ? DIALOG_YES : button == "NO" ? DIALOG_NO : button == "OK" ? DIALOG_OK : -1;

    // 「是」必须说明是替哪个角色确认; 其余两个不带参数
    const auto expected = action == DIALOG_YES && !extra.empty() ? ParseContentId(extra) : 0ULL;

    if (action < 0 || (action == DIALOG_YES ? expected == 0 : !extra.empty()))
        return "FAIL usage: DIALOG <YES <contentId>|NO|OK>";

    CallStatePtr state;
    std::string  failure;

    if (!PrepareCall(state, failure))
        return failure;

    auto captured = state;

    if (!MainThreadRun([captured, action, expected] { captured->result = OpDialog(&captured->pointers, action, expected); }, 3000))
        return "FAIL mainthread-timeout";

    LogF("[game] DIALOG %s → %d", arguments.c_str(), state->result);

    switch (state->result)
    {
        case 1:  return "OK clicked addon=SelectYesno";
        case 2:  return "OK clicked addon=SelectOk";
        case 3:  return "OK clicked addon=Dialogue";
        case 0:  return "FAIL no-dialog";
        case -2: return "FAIL queueing";
        case -3: return "FAIL ingame";
        case -4: return "FAIL no-button";
        case -5: return "FAIL not-charaselect";
        case -6: return "FAIL not-login-confirm";   // 是/否框不是大厅为登录开的那个
        case -7: return "FAIL login-requested";     // 登录请求已发出, 这时的是/否框是在问要不要取消登录
        case -8: return "FAIL other-character";     // 客户端选中的不是指定的角色（有人点了别的角色）
        case -9: return "FAIL not-ready";           // 框还在打开, 稍后再试
        default: return "FAIL exception";
    }
}

// 响应格式（第一行是摘要, 之后每个角色一行, 字段用 \t 分隔）:
//   OK where=charaselect n=3 total=3 selected=<cid> selectedIndex=0 hovered=<cid> hoveredIndex=-1
//   C  <contentId>  <index>  <loginFlags>  <curWorldId>  <homeWorldId>  <名字>  <当前世界名>  <原始世界名>
std::string GameWhoList()
{
    CallStatePtr state;
    std::string  failure;

    if (!PrepareCall(state, failure))
        return failure;

    auto data     = std::make_shared<WhoListData>();
    auto captured = state;
    auto list     = data;

    if (!MainThreadRun([captured, list] { captured->ok = OpWhoList(&captured->pointers, list.get()); }, 3000))
        return "FAIL mainthread-timeout";

    if (!state->ok)
        return "FAIL exception";

    char header[256];
    _snprintf_s(header, sizeof(header), _TRUNCATE,
                "OK where=%s n=%d total=%d selected=%llu selectedIndex=%d hovered=%llu hoveredIndex=%d",
                WhereName(data->where), data->count, data->total,
                data->selectedContentId, data->selectedIndex, data->hoveredContentId, data->hoveredIndex);

    std::string response = header;

    for (int i = 0; i < data->count; ++i)
    {
        const auto& row = data->rows[i];

        char line[256];
        _snprintf_s(line, sizeof(line), _TRUNCATE, "\nC\t%llu\t%u\t%u\t%u\t%u\t%s\t%s\t%s",
                    row.contentId, row.index, row.loginFlags, row.currentWorldId, row.homeWorldId,
                    row.name, row.currentWorldName, row.homeWorldName);
        response += line;
    }

    LogF("[game] WHOLIST where=%s n=%d/%d selected=%llu", WhereName(data->where), data->count, data->total,
         data->selectedContentId);
    return response;
}

std::string GameReleaseLobbyContext()
{
    CallStatePtr state;
    std::string  failure;

    if (!PrepareCall(state, failure))
        return failure;

    auto captured = state;

    if (!MainThreadRun([captured] { captured->result = OpRelease(&captured->pointers); }, 5000))
        return "FAIL mainthread-timeout";

    return state->result == 1 ? "OK" : "FAIL exception";
}

std::string GameSetSid(const std::string& sid)
{
    if (sid.empty())
        return "FAIL empty-sid";

    CallStatePtr state;
    std::string  failure;

    if (!PrepareCall(state, failure))
        return failure;

    state->lobbyHost = sid; // 借这个字段带过去, 免得闭包捕引用
    auto captured    = state;

    if (!MainThreadRun([captured] { captured->result = OpSetSid(&captured->pointers, captured->lobbyHost.c_str()); }, 5000))
        return "FAIL mainthread-timeout";

    // ⚠ 不打印 sid 本身, 那是登录票据
    LogF("[game] SETSID 写入 %d 字节", static_cast<int>(sid.size()));

    return state->result == 1 ? "OK" : "FAIL exception";
}

namespace
{
    // 诊断: TITLEREADY 说找不到 _TitleMenu 时, 用它区分「确实没这个 addon」和「unitManager 指针就不对」。
    // 能列出一串合理的 addon 名 = 指针对; 一个都列不出来 = 偏移错。
    int OpListAddons(const Pointers* p, char* out, size_t capacity, void** unitManagerOut)
    {
        __try
        {
            const auto unitManager = reinterpret_cast<uint8_t*>(p->unitManager);
            *unitManagerOut = unitManager;

            if (unitManager == nullptr)
                return -1;

            const auto list    = unitManager + offsets::ATK_UNIT_MANAGER_ALL_LOADED;
            const auto count   = ReadAt<unsigned short>(list, offsets::ATK_UNIT_LIST_COUNT);
            const auto entries = reinterpret_cast<void**>(list + offsets::ATK_UNIT_LIST_ENTRIES);

            int written = _snprintf_s(out, capacity, _TRUNCATE, "count=%u:", count);
            int listed  = 0;

            for (unsigned short i = 0; i < count && i < 256 && listed < 40; ++i)
            {
                const auto addon = entries[i];
                if (addon == nullptr)
                    continue;

                const auto name = reinterpret_cast<const char*>(reinterpret_cast<uint8_t*>(addon) + offsets::ATK_UNIT_BASE_NAME);

                if (name[0] == '\0')
                    continue;

                const int room = static_cast<int>(capacity) - written;
                if (room < 40)
                    break;

                written += _snprintf_s(out + written, static_cast<size_t>(room), _TRUNCATE, " %.31s", name);
                ++listed;
            }

            return listed;
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            LogF("[game] 枚举 addon 异常 code=0x%08X", GetExceptionCode());
            return -1;
        }
    }
}

std::string GameListAddons()
{
    CallStatePtr state;
    std::string  failure;

    if (!PrepareCall(state, failure))
        return failure;

    auto captured = state;

    if (!MainThreadRun([captured]
        {
            captured->result = OpListAddons(&captured->pointers, captured->text, sizeof(captured->text), &captured->unitManager);
        }, 5000))
        return "FAIL mainthread-timeout";

    if (state->result < 0)
        return "FAIL exception";

    char response[1600];
    _snprintf_s(response, sizeof(response), _TRUNCATE, "OK unitManager=0x%p listed=%d %s",
                state->unitManager, state->result, state->text);

    LogF("[game] ADDONS → %s", response);
    return response;
}

std::string GameTitleReady()
{
    CallStatePtr state;
    std::string  failure;

    if (!PrepareCall(state, failure))
        return failure;

    auto captured = state;

    if (!MainThreadRun([captured] { captured->result = OpTitleReady(&captured->pointers); }, 3000))
        return "FAIL mainthread-timeout";

    if (state->result < 0)
        return "FAIL exception";

    return state->result == 1 ? "OK ready=1" : "OK ready=0";
}

std::string GameLogin()
{
    CallStatePtr state;
    std::string  failure;

    if (!PrepareCall(state, failure))
        return failure;

    auto captured = state;

    if (!MainThreadRun([captured] { captured->result = OpLogin(&captured->pointers); }, 5000))
        return "FAIL mainthread-timeout";

    if (state->result == 1) return "OK";
    if (state->result == 0) return "FAIL no-title-menu"; // 不在标题界面
    return "FAIL exception";
}

namespace
{
    // 标题界面守卫（常驻, 默认开）:
    //   在标题菜单 → 每 500ms 把 AgentLobby.IdleTime 归零, 它就永远飘不进片头动画
    //                （闲置到两万上下才触发, 归零等于永远够不着; DCTraveler 也是这么防的）
    //   已经在动画里（含刚启动那段开机动画）→ 给游戏窗口投一次 ESC 退出来
    //   在游戏内 / 角色选择界面 → 什么都不做
    //
    // WHY 常驻而不是换服时才开: 挂机时客户端多半停在标题, 一旦飘进动画,
    //   _TitleMenu 就不存在, 之后任何换服操作都得先把它弄出来 —— 那是白白多出来的一段等待。
    //
    // ⚠ 这条线程必须在卸载模块之前 join 掉。2026-08-15 实测教训: 先关掉再立刻 UNLOAD,
    //   线程可能还停在 Sleep(500) 里, 而 FreeLibraryAndExitThread 已经把模块代码页解除映射 ——
    //   它一醒来就跳进空地址, 直接把游戏带走。
    std::atomic<bool> g_titleGuard   {true};  // 行为开关（可经 KEEPALIVE ON/OFF 改）
    std::atomic<bool> g_guardRunning {false}; // 线程活着没
    HANDLE            g_guardThread = nullptr;

    DWORD WINAPI TitleGuardProc(LPVOID)
    {
        LogF("[game] 标题守卫线程启动（防止飘进片头动画）");

        bool lastMovie = false;

        while (g_guardRunning.load())
        {
            if (g_titleGuard.load())
            {
                // 堆上传值捕获: 超时的 job 可能下一帧才被 tick 跑到, 那时这轮循环早结束了
                auto state = std::make_shared<CallState>();

                MainThreadRun([state]
                {
                    if (!GetPointers(&state->pointers))
                        return;

                    const int where = OpWhere(&state->pointers);
                    state->result   = where;

                    if (where == 1)                       // 标题菜单: 压住 IdleTime
                        OpResetIdleTime(&state->pointers);
                    else if (where == 0)                  // 什么界面都不是: 看是不是在放动画
                        state->result = OpIsTitleMovie(&state->pointers) == 1 ? -10 : where;
                }, 1000);

                const bool inMovie = state->result == -10;

                if (inMovie)
                {
                    if (!lastMovie)
                        LogF("[game] 检测到片头动画, 自动投 ESC 退出");

                    const HWND hwnd = FindGameWindow();

                    if (hwnd != nullptr)
                    {
                        PostMessageW(hwnd, WM_KEYDOWN, VK_ESCAPE, 0x00010001);
                        PostMessageW(hwnd, WM_KEYUP,   VK_ESCAPE, 0xC0010001);
                    }
                }

                lastMovie = inMovie;
            }

            Sleep(500);
        }

        LogF("[game] 标题守卫线程退出");
        return 0;
    }
}

/// 启动常驻的标题守卫线程。模块一注进来就该开着 —— 挂机时客户端多半停在标题界面,
/// 让它飘进片头动画等于给后续每一次换服都白加一段等待。
bool GameStartTitleGuard()
{
    if (g_guardRunning.load())
        return true;

    g_guardRunning.store(true);
    g_guardThread = CreateThread(nullptr, 0, TitleGuardProc, nullptr, 0, nullptr);

    if (g_guardThread == nullptr)
    {
        g_guardRunning.store(false);
        LogF("[game] 标题守卫线程起不来 err=%lu", GetLastError());
        return false;
    }

    return true;
}

/// KEEPALIVE ON/OFF 现在只是开关守卫的行为（线程一直在）。
/// 保留这条命令是因为换服编排里会显式开一次, 且它能在需要时把守卫关掉。
std::string GameKeepAlive(bool enable)
{
    const bool was = g_titleGuard.load();
    g_titleGuard.store(enable);

    if (enable && !g_guardRunning.load() && !GameStartTitleGuard())
        return "FAIL cannot-start-thread";

    LogF("[game] 标题守卫 %s", enable ? "开" : "关");
    return was == enable ? (enable ? "OK already-on" : "OK already-off") : "OK";
}

void GameStopKeepAlive()
{
    g_guardRunning.store(false);

    if (g_guardThread == nullptr)
        return;

    // 必须等它真的退出来 —— 见 TitleGuardProc 上面的说明
    if (WaitForSingleObject(g_guardThread, 5000) != WAIT_OBJECT_0)
        LogF("[game] ⚠ 标题守卫线程没能在 5 秒内退出, 此时卸载模块是不安全的");

    CloseHandle(g_guardThread);
    g_guardThread = nullptr;
}

void* GameFrameworkPointer()
{
    if (g_frameworkStatic == 0)
        return nullptr;

    __try
    {
        return *reinterpret_cast<void**>(g_frameworkStatic);
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return nullptr;
    }
}

bool GameResolve()
{
    if (g_resolved)
        return true;

    const uintptr_t base = ModuleBase();

    g_frameworkStatic     = ScanStaticAddress(offsets::FRAMEWORK_INSTANCE_SIG, offsets::FRAMEWORK_INSTANCE_SIG_OFFSET);
    g_atkStageStatic      = ScanStaticAddress(offsets::ATK_STAGE_SIG, offsets::ATK_STAGE_SIG_OFFSET);
    g_returnToTitle       = ScanText(offsets::RETURN_TO_TITLE_SIG);
    g_releaseLobbyContext = ScanText(offsets::RELEASE_LOBBY_CONTEXT_SIG);
    g_setString           = ScanText(offsets::UTF8_SET_STRING_SIG);
    g_getAddonByName      = ScanText(offsets::GET_ADDON_BY_NAME_SIG);
    g_getComponentButton  = ScanText(offsets::GET_COMPONENT_BUTTON_SIG);
    g_processChatBoxEntry = ScanText(offsets::PROCESS_CHATBOX_ENTRY_SIG);

    if (g_processChatBoxEntry == 0)
        g_processChatBoxEntry = ScanTextRaw(offsets::PROCESS_CHATBOX_ENTRY_HOOKED_SIG);
    g_utf8Ctor            = ScanText(offsets::UTF8_CTOR_SIG);
    g_utf8Dtor            = ScanText(offsets::UTF8_DTOR_SIG);
    g_fireCallbackInt     = ScanText(offsets::FIRE_CALLBACK_INT_SIG);
    g_handleLogout        = ScanText(offsets::AGENT_LOBBY_HANDLE_LOGOUT_SIG);

    LogF("[game] 模块基址=0x%p .text 解析结果:", reinterpret_cast<void*>(base));
    LogF("[game]   Framework 静态指针      RVA=0x%llX", static_cast<unsigned long long>(g_frameworkStatic     ? g_frameworkStatic     - base : 0));
    LogF("[game]   returnToTitle           RVA=0x%llX", static_cast<unsigned long long>(g_returnToTitle       ? g_returnToTitle       - base : 0));
    LogF("[game]   releaseLobbyContext     RVA=0x%llX", static_cast<unsigned long long>(g_releaseLobbyContext ? g_releaseLobbyContext - base : 0));
    LogF("[game]   Utf8String::SetString   RVA=0x%llX", static_cast<unsigned long long>(g_setString           ? g_setString           - base : 0));
    LogF("[game]   GetAddonByName          RVA=0x%llX", static_cast<unsigned long long>(g_getAddonByName      ? g_getAddonByName      - base : 0));
    LogF("[game]   GetComponentButtonById  RVA=0x%llX", static_cast<unsigned long long>(g_getComponentButton  ? g_getComponentButton  - base : 0));
    LogF("[game]   AtkStage 静态指针       RVA=0x%llX", static_cast<unsigned long long>(g_atkStageStatic      ? g_atkStageStatic      - base : 0));
    LogF("[game]   ProcessChatBoxEntry     RVA=0x%llX", static_cast<unsigned long long>(g_processChatBoxEntry ? g_processChatBoxEntry - base : 0));
    LogF("[game]   Utf8String::Ctor/Dtor   RVA=0x%llX / 0x%llX",
         static_cast<unsigned long long>(g_utf8Ctor ? g_utf8Ctor - base : 0),
         static_cast<unsigned long long>(g_utf8Dtor ? g_utf8Dtor - base : 0));
    LogF("[game]   FireCallbackInt         RVA=0x%llX", static_cast<unsigned long long>(g_fireCallbackInt     ? g_fireCallbackInt     - base : 0));
    LogF("[game]   AgentLobby::HandleLogout RVA=0x%llX", static_cast<unsigned long long>(g_handleLogout       ? g_handleLogout        - base : 0));

    g_resolved = g_frameworkStatic != 0 && g_returnToTitle != 0 && g_releaseLobbyContext != 0 && g_setString != 0 &&
                 g_getAddonByName != 0 && g_getComponentButton != 0 && g_atkStageStatic != 0 &&
                 g_processChatBoxEntry != 0 && g_utf8Ctor != 0 && g_utf8Dtor != 0 && g_fireCallbackInt != 0;

    if (!g_resolved)
        LogF("[game] 有特征码没命中 —— 游戏版本变了或偏移库过期");

    return g_resolved;
}

std::string GameProbe()
{
    if (!GameResolve())
        return "FAIL sigscan-failed";

    // 结构体是主线程在维护的, 读也放到主线程上, 免得读到半个正在被改的指针。
    // 堆上传值捕获: 超时的 job 可能下一帧才跑, 那时这里的栈已经没了
    auto shared = std::make_shared<std::pair<ProbeData, bool>>();

    if (!MainThreadRun([shared] { shared->second = ProbeRaw(&shared->first); }, 3000))
        return "FAIL mainthread-timeout";

    if (!shared->second)
        return "FAIL probe-exception";

    const ProbeData& data = shared->first;

    const uintptr_t base = ModuleBase();

    char response[1024];
    _snprintf_s(response, sizeof(response), _TRUNCATE,
                "OK framework=0x%p ui=0x%p agentModule=0x%p agentLobby=0x%p network=0x%p "
                "idleTime=%lld ctx=0x%p state=%u devConfigCount=%d "
                "activeLobbyHost=%s lobbyHost0=%s saveDataBankHost=%s gameSessionLen=%d "
                "rva.returnToTitle=0x%llX rva.releaseLobbyContext=0x%llX rva.setString=0x%llX",
                data.framework, data.uiModule, data.agentModule, data.agentLobby, data.networkModule,
                data.idleTime, data.lobbyUiContext, static_cast<unsigned>(data.lobbyUiState), data.devConfigCount,
                data.activeLobbyHost[0] ? data.activeLobbyHost : "(空)",
                data.lobbyHost0[0]      ? data.lobbyHost0      : "(空)",
                data.saveDataBankHost[0] ? data.saveDataBankHost : "(空)",
                static_cast<int>(strlen(data.gameSession)),
                static_cast<unsigned long long>(g_returnToTitle - base),
                static_cast<unsigned long long>(g_releaseLobbyContext - base),
                static_cast<unsigned long long>(g_setString - base));

    LogF("[game] PROBE → %s", response);
    return response;
}