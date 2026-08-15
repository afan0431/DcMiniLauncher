// 游戏结构体访问 —— 本轮只读
//
// 目的: 在动任何一个字节之前, 先证明 offsets.h 里那堆偏移在这台机器的这个客户端上是对的。
// 判据是「读出来的东西能和外部已知事实对上」: 当前大厅主机名应当形如 ffxivlobby0N.ff14.sdo.com,
// 且与启动时所连大区一致。对不上就说明偏移错了 —— 那种情况下绝不能进入写入/调用阶段。
#include "MiniModule.h"
#include "offsets.h"

#include <cstdio>

namespace
{
    uintptr_t g_frameworkStatic     = 0;
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

    char response[1280];
    _snprintf_s(response, sizeof(response), _TRUNCATE, "OK %s %s %s %s ffxivInNetworkModule=%s",
                active, lobby0, saveData, session, hitText);

    LogF("[game] DUMP → %s", response);
    return response;
}

bool GameResolve()
{
    if (g_resolved)
        return true;

    const uintptr_t base = ModuleBase();

    g_frameworkStatic     = ScanStaticAddress(offsets::FRAMEWORK_INSTANCE_SIG, offsets::FRAMEWORK_INSTANCE_SIG_OFFSET);
    g_returnToTitle       = ScanText(offsets::RETURN_TO_TITLE_SIG);
    g_releaseLobbyContext = ScanText(offsets::RELEASE_LOBBY_CONTEXT_SIG);
    g_setString           = ScanText(offsets::UTF8_SET_STRING_SIG);

    LogF("[game] 模块基址=0x%p .text 解析结果:", reinterpret_cast<void*>(base));
    LogF("[game]   Framework 静态指针      RVA=0x%llX", static_cast<unsigned long long>(g_frameworkStatic     ? g_frameworkStatic     - base : 0));
    LogF("[game]   returnToTitle           RVA=0x%llX", static_cast<unsigned long long>(g_returnToTitle       ? g_returnToTitle       - base : 0));
    LogF("[game]   releaseLobbyContext     RVA=0x%llX", static_cast<unsigned long long>(g_releaseLobbyContext ? g_releaseLobbyContext - base : 0));
    LogF("[game]   Utf8String::SetString   RVA=0x%llX", static_cast<unsigned long long>(g_setString           ? g_setString           - base : 0));

    g_resolved = g_frameworkStatic != 0 && g_returnToTitle != 0 && g_releaseLobbyContext != 0 && g_setString != 0;

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
