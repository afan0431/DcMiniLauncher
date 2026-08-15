// 游戏结构体访问 —— 本轮只读
//
// 目的: 在动任何一个字节之前, 先证明 offsets.h 里那堆偏移在这台机器的这个客户端上是对的。
// 判据是「读出来的东西能和外部已知事实对上」: 当前大厅主机名应当形如 ffxivlobby0N.ff14.sdo.com,
// 且与启动时所连大区一致。对不上就说明偏移错了 —— 那种情况下绝不能进入写入/调用阶段。
#include "MiniModule.h"
#include "offsets.h"

#include <atomic>
#include <cstdio>

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

    ProbeData data{};
    bool      ok = false;

    if (!MainThreadRun([&] { ok = ProbeRaw(&data); }, 3000))
        return "FAIL mainthread-timeout";

    if (!ok || data.networkModule == nullptr || data.agentLobby == nullptr)
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

    char devGm[160]{}, devSaveData[160]{}, devLobby01[160]{};
    MainThreadRun([&] { CopyDevConfigHosts(data.framework, devGm, devSaveData, devLobby01, sizeof(devGm)); }, 3000);

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

    uintptr_t g_getAddonByName    = 0;
    uintptr_t g_getComponentButton = 0;

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

    int OpReturnToTitle(const Pointers* p)
    {
        __try
        {
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

    // 轮询期保活: 标题界面闲置久了会飘进片头动画（IdleTime 涨到两万上下就触发), DCTraveler 每帧把
    // IdleTime 归零, 我们按 500ms 一次。
    //
    // ⚠ 这条线程必须在卸载模块之前 join 掉。2026-08-15 实测教训: 先 KEEPALIVE OFF 再立刻 UNLOAD,
    //   线程可能还停在 Sleep(500) 里, 而 FreeLibraryAndExitThread 已经把模块代码页解除映射 ——
    //   它一醒来就跳进空地址, 直接把游戏带走。
    std::atomic<bool> g_keepAlive {false};
    HANDLE            g_keepAliveThread = nullptr;

    DWORD WINAPI KeepAliveProc(LPVOID)
    {
        LogF("[game] 保活线程启动");

        while (g_keepAlive.load())
        {
            Pointers pointers{};
            MainThreadRun([&]
            {
                if (GetPointers(&pointers))
                    OpResetIdleTime(&pointers);
            }, 1000);

            Sleep(500);
        }

        LogF("[game] 保活线程退出");
        return 0;
    }

    // 所有换服命令共用的前置: 特征码解析 + 指针取全, 缺一不可
    bool PrepareCall(Pointers* pointers, std::string& failure)
    {
        if (!GameResolve())
        {
            failure = "FAIL sigscan-failed";
            return false;
        }

        bool ok = false;
        if (!MainThreadRun([&] { ok = GetPointers(pointers); }, 3000))
        {
            failure = "FAIL mainthread-timeout";
            return false;
        }

        if (!ok)
        {
            failure = "FAIL pointers-unavailable";
            return false;
        }

        return true;
    }
}

std::string GameSetHosts(const std::string& lobbyHost, const std::string& saveDataHost, const std::string& gmHost)
{
    Pointers    pointers{};
    std::string failure;

    if (!PrepareCall(&pointers, failure))
        return failure;

    int written = -1;
    if (!MainThreadRun([&] { written = OpSetHosts(&pointers, lobbyHost.c_str(), saveDataHost.c_str(), gmHost.c_str()); }, 5000))
        return "FAIL mainthread-timeout";

    if (written < 0)
        return "FAIL exception";

    LogF("[game] SETHOSTS lobby=%s sdb=%s gm=%s → 写了 %d 处", lobbyHost.c_str(), saveDataHost.c_str(), gmHost.c_str(), written);

    char response[64];
    _snprintf_s(response, sizeof(response), _TRUNCATE, "OK written=%d", written);
    return response;
}

std::string GameReturnToTitle()
{
    Pointers    pointers{};
    std::string failure;

    if (!PrepareCall(&pointers, failure))
        return failure;

    int result = -1;
    if (!MainThreadRun([&] { result = OpReturnToTitle(&pointers); }, 5000))
        return "FAIL mainthread-timeout";

    return result == 1 ? "OK" : "FAIL exception";
}

std::string GameReleaseLobbyContext()
{
    Pointers    pointers{};
    std::string failure;

    if (!PrepareCall(&pointers, failure))
        return failure;

    int result = -1;
    if (!MainThreadRun([&] { result = OpRelease(&pointers); }, 5000))
        return "FAIL mainthread-timeout";

    return result == 1 ? "OK" : "FAIL exception";
}

std::string GameSetSid(const std::string& sid)
{
    if (sid.empty())
        return "FAIL empty-sid";

    Pointers    pointers{};
    std::string failure;

    if (!PrepareCall(&pointers, failure))
        return failure;

    int result = -1;
    if (!MainThreadRun([&] { result = OpSetSid(&pointers, sid.c_str()); }, 5000))
        return "FAIL mainthread-timeout";

    // ⚠ 不打印 sid 本身, 那是登录票据
    LogF("[game] SETSID 写入 %d 字节", static_cast<int>(sid.size()));

    return result == 1 ? "OK" : "FAIL exception";
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
    Pointers    pointers{};
    std::string failure;

    if (!PrepareCall(&pointers, failure))
        return failure;

    char  names[1400]{};
    void* unitManager = nullptr;
    int   listed      = -1;

    if (!MainThreadRun([&] { listed = OpListAddons(&pointers, names, sizeof(names), &unitManager); }, 5000))
        return "FAIL mainthread-timeout";

    if (listed < 0)
        return "FAIL exception";

    char response[1600];
    _snprintf_s(response, sizeof(response), _TRUNCATE, "OK unitManager=0x%p listed=%d %s", unitManager, listed, names);

    LogF("[game] ADDONS → %s", response);
    return response;
}

std::string GameTitleReady()
{
    Pointers    pointers{};
    std::string failure;

    if (!PrepareCall(&pointers, failure))
        return failure;

    int result = -1;
    if (!MainThreadRun([&] { result = OpTitleReady(&pointers); }, 3000))
        return "FAIL mainthread-timeout";

    if (result < 0)
        return "FAIL exception";

    return result == 1 ? "OK ready=1" : "OK ready=0";
}

std::string GameLogin()
{
    Pointers    pointers{};
    std::string failure;

    if (!PrepareCall(&pointers, failure))
        return failure;

    int result = -1;
    if (!MainThreadRun([&] { result = OpLogin(&pointers); }, 5000))
        return "FAIL mainthread-timeout";

    if (result == 1) return "OK";
    if (result == 0) return "FAIL no-title-menu"; // 不在标题界面
    return "FAIL exception";
}

std::string GameKeepAlive(bool enable)
{
    if (enable == g_keepAlive.load())
        return enable ? "OK already-on" : "OK already-off";

    if (enable)
    {
        g_keepAlive.store(true);
        g_keepAliveThread = CreateThread(nullptr, 0, KeepAliveProc, nullptr, 0, nullptr);

        if (g_keepAliveThread == nullptr)
        {
            g_keepAlive.store(false);
            return "FAIL cannot-start-thread";
        }

        return "OK";
    }

    GameStopKeepAlive();
    return "OK";
}

void GameStopKeepAlive()
{
    g_keepAlive.store(false);

    if (g_keepAliveThread == nullptr)
        return;

    // 必须等它真的退出来 —— 见上面 g_keepAliveThread 的说明
    if (WaitForSingleObject(g_keepAliveThread, 5000) != WAIT_OBJECT_0)
        LogF("[game] ⚠ 保活线程没能在 5 秒内退出, 此时卸载模块是不安全的");

    CloseHandle(g_keepAliveThread);
    g_keepAliveThread = nullptr;
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

    LogF("[game] 模块基址=0x%p .text 解析结果:", reinterpret_cast<void*>(base));
    LogF("[game]   Framework 静态指针      RVA=0x%llX", static_cast<unsigned long long>(g_frameworkStatic     ? g_frameworkStatic     - base : 0));
    LogF("[game]   returnToTitle           RVA=0x%llX", static_cast<unsigned long long>(g_returnToTitle       ? g_returnToTitle       - base : 0));
    LogF("[game]   releaseLobbyContext     RVA=0x%llX", static_cast<unsigned long long>(g_releaseLobbyContext ? g_releaseLobbyContext - base : 0));
    LogF("[game]   Utf8String::SetString   RVA=0x%llX", static_cast<unsigned long long>(g_setString           ? g_setString           - base : 0));
    LogF("[game]   GetAddonByName          RVA=0x%llX", static_cast<unsigned long long>(g_getAddonByName      ? g_getAddonByName      - base : 0));
    LogF("[game]   GetComponentButtonById  RVA=0x%llX", static_cast<unsigned long long>(g_getComponentButton  ? g_getComponentButton  - base : 0));
    LogF("[game]   AtkStage 静态指针       RVA=0x%llX", static_cast<unsigned long long>(g_atkStageStatic      ? g_atkStageStatic      - base : 0));

    g_resolved = g_frameworkStatic != 0 && g_returnToTitle != 0 && g_releaseLobbyContext != 0 && g_setString != 0 &&
                 g_getAddonByName != 0 && g_getComponentButton != 0 && g_atkStageStatic != 0;

    if (!g_resolved)
        LogF("[game] 有特征码没命中 —— 游戏版本变了或偏移库过期");

    return g_resolved;
}

std::string GameProbe()
{
    if (!GameResolve())
        return "FAIL sigscan-failed";

    ProbeData data{};

    // 结构体是主线程在维护的, 读也放到主线程上, 免得读到半个正在被改的指针
    bool ok = false;
    if (!MainThreadRun([&] { ok = ProbeRaw(&data); }, 3000))
        return "FAIL mainthread-timeout";

    if (!ok)
        return "FAIL probe-exception";

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
