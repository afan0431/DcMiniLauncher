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