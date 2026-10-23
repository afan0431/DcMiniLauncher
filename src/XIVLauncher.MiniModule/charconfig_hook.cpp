// 所有角色共用一套游戏配置 · 挂钩与线程
//
// 挂 FileInterface::OpenFile(self, widePath, mode)（mode: 0 读、1 写、2 追加; 返回 1 = 成功）:
//   读共用文件之前 → PrepareRead 放入这一套的最新版, 记下这个号这个文件的基线;
//   写 <NAME>.DAT.new.<hex>.<本进程 pid> 之前 → 记一条待查项, 等游戏把 .new 改名成 <NAME>.DAT 后 HandleWritten,
//   与基线不同才发布到本机共享目录。
// 判断与读写都在 charconfig_core.*; 这里只管挂钩、待查项、日志、心跳。
//
// 线程:
//   - 钩子跑在调用 OpenFile 的游戏线程上: 只做路径尾部比较, 命中共用文件才读写本机目录; 任何异常都吞掉并照常调原函数。
//   - 工作线程每 100 ms 查一次待查项, 处理改名已完成的; 定期写心跳。
//   - 角色名: 钩子里只登记要查的号; 工作线程经主线程通道按 ContentId 查到后存起来, 发布时取用（查不到就不写）。
//   - 游戏下一次写共用文件时顺手处理已改名完成的待查项, 读同一个文件之前先处理它的待查项。
//   - 进程正常退出时 DLL_PROCESS_DETACH 补处理剩下的（CharConfigProcessExit）, 只用 try_lock。
//
// 卸载: 先停钩子里的逻辑、禁用钩子, 等进行中的调用全部离开（计数包住对原函数的调用）, 再停工作线程、移除钩子。
// 函数头已被别人改写（在我们之上又挂了一层）时不禁用、拒绝卸载: 强行还原会把那一层一起抹掉。
#include "MiniModule.h"
#include "charconfig_core.h"
#include "third_party/minhook/include/MinHook.h"

#include <atomic>
#include <cstdlib>
#include <cstring>
#include <deque>
#include <map>
#include <memory>
#include <mutex>
#include <string>
#include <vector>

namespace
{
    using namespace charconfig;

    using OpenFileFn = uint8_t (*)(void* self, wchar_t* path, int mode);

    constexpr int kModeRead  = 0;
    constexpr int kModeWrite = 1;

    // 主: FFXIVClientStructs-cn FileInterface.OpenFile; 备: Dalamud.CharacterSync。都是调用点, 跟进 call 得到函数本体
    constexpr char kPrimarySignature[] = "E8 ?? ?? ?? ?? 0F B6 C8 3C 01";
    constexpr char kBackupSignature[]  = "E8 ?? ?? ?? ?? 3C 01 0F 85 1C 04 00 00";

    constexpr DWORD     kReadLockTimeoutMs = 1000;  // 游戏线程上等配置套互斥体的上限
    constexpr DWORD     kPollMs            = 100;
    constexpr ULONGLONG kWriteWaitMs       = 10000; // 等 .new 改名完成的上限
    constexpr ULONGLONG kHeartbeatMs       = 30000;
    constexpr ULONGLONG kTargetTimeSlack   = 100 * 10000ULL; // 100 ms（FILETIME 单位 100 ns）
    constexpr size_t    kRecentMax         = 12;
    constexpr size_t    kDiffKeysShown     = 40;
    constexpr size_t    kPatchBytes        = 8;
    constexpr int       kNameAttempts      = 4;     // 查不到角色名时最多查几次（选角列表里没有时等角色载入）
    constexpr ULONGLONG kNameRetryMs       = 15000;

    std::wstring g_root;
    DWORD        g_pid = 0;

    void*      g_target   = nullptr;
    OpenFileFn g_original = nullptr;
    uint8_t    g_patch[kPatchBytes]{}; // 挂上之后函数头的字节, 卸载前核对没被别人改写

    std::atomic<bool> g_hooked {false}; // 钩子装着
    std::atomic<bool> g_active {false}; // 钩子里做共用配置的事; false 时直接透传
    std::atomic<int>  g_inside {0};     // 正在钩子里的调用数
    std::atomic<bool> g_heartbeatDirty {false}; // 有新动作, 工作线程下一轮写心跳
    std::atomic<bool> g_workerRunning {false};

    HANDLE g_worker    = nullptr;
    HANDLE g_stopEvent = nullptr;

    std::unique_ptr<MachineConfigCache> g_machine;

    // 这个进程里每个号每个文件这次登录读到的内容
    struct Baseline
    {
        std::string setId;
        bool        has = false;
        std::string hash;
        Bytes       bytes; // 只给 ADDON 存: 发布时列出不同的记录键
    };

    std::mutex                      g_baselineMutex;
    std::map<std::string, Baseline> g_baselines; // 键 = <cid>|<NAME>

    struct PendingWrite
    {
        std::wstring newPath;
        PathMatch    match;
        ULONGLONG    queuedTick = 0;
        ULONGLONG    queuedTime = 0; // FILETIME
        bool         seen       = false; // 见过 .new 文件存在
    };

    std::mutex                g_pendingMutex;
    std::vector<PendingWrite> g_pending;

    struct NameLookup
    {
        int       attempts = 0;
        ULONGLONG dueTick  = 0;
    };

    std::mutex                        g_nameMutex;
    std::map<std::string, std::string> g_names;       // cid → 角色名（UTF-8）
    std::map<std::string, NameLookup>  g_nameLookups; // 还要查名字的号

    std::mutex              g_statusMutex;
    std::string             g_lastCid;
    std::string             g_lastError;
    std::string             g_hookError;
    std::deque<std::string> g_recent;
    ULONGLONG               g_targetRva = 0;

    // ---- 工具 -------------------------------------------------------------------

    ULONGLONG FileTimeValue(const FILETIME& time)
    {
        return (static_cast<ULONGLONG>(time.dwHighDateTime) << 32) | time.dwLowDateTime;
    }

    ULONGLONG SystemTimeValue()
    {
        FILETIME now{};
        GetSystemTimeAsFileTime(&now);
        return FileTimeValue(now);
    }

    std::string BaselineKey(const PathMatch& match)
    {
        return ToUtf8(match.cidHex) + "|" + match.file->fileName;
    }

    std::string LocalTimeText()
    {
        SYSTEMTIME st;
        GetLocalTime(&st);

        char buffer[16];
        _snprintf_s(buffer, sizeof(buffer), _TRUNCATE, "%02d:%02d:%02d", st.wHour, st.wMinute, st.wSecond);
        return buffer;
    }

    // noWait = true 时只试一次: 进程退出时别的线程已被结束, 可能正持着锁
    bool Acquire(std::unique_lock<std::mutex>& lock, bool noWait)
    {
        if (noWait)
            return lock.try_lock();

        lock.lock();
        return true;
    }

    void Log(bool noWait, const std::string& text)
    {
        if (noWait)
            LogFNoWait("[charconfig] %s", text.c_str());
        else
            LogF("[charconfig] %s", text.c_str());
    }

    // 记一条动作: 写日志、进最近动作表; error = true 时同时记为 lastError, 成功的动作清掉 lastError
    void Record(const std::string& text, bool error, bool clearsError, bool noWait = false)
    {
        Log(noWait, text);

        std::unique_lock<std::mutex> lock(g_statusMutex, std::defer_lock);
        if (!Acquire(lock, noWait))
            return;

        g_recent.push_back(LocalTimeText() + " " + text);
        while (g_recent.size() > kRecentMax)
            g_recent.pop_front();

        if (error)
            g_lastError = text;
        else if (clearsError && g_hookError.empty())
            g_lastError.clear();
    }

    void SetLastCid(const std::wstring& cidHex)
    {
        std::lock_guard<std::mutex> lock(g_statusMutex);
        g_lastCid = ToUtf8(cidHex);
    }

    std::string CachedName(const std::string& cid, bool noWait);

    void Heartbeat(bool noWait = false)
    {
        ModuleHeartbeat heartbeat;
        heartbeat.moduleVersion = MINIMODULE_VERSION;
        heartbeat.hooked        = g_hooked.load();

        {
            std::unique_lock<std::mutex> lock(g_statusMutex, std::defer_lock);
            if (!Acquire(lock, noWait))
                return;

            heartbeat.lastCid   = g_lastCid;
            heartbeat.lastError = g_lastError;
        }

        if (!heartbeat.lastCid.empty())
            heartbeat.lastName = CachedName(heartbeat.lastCid, noWait);

        std::string error;
        if (!WriteHeartbeat(g_root, g_pid, heartbeat, &error))
            Log(noWait, "写心跳失败: " + error);
    }

    // 游戏线程上不写文件: 交给工作线程; 工作线程没起来时当场写
    void RequestHeartbeat()
    {
        if (g_workerRunning.load())
            g_heartbeatDirty.store(true);
        else
            Heartbeat();
    }

    std::string Narrow(const std::wstring& text)
    {
        return ToUtf8(text);
    }

    bool FileExists(const std::wstring& path)
    {
        return GetFileAttributesW(path.c_str()) != INVALID_FILE_ATTRIBUTES;
    }

    // ---- 角色名 -----------------------------------------------------------------

    // 钩子里调: 只登记, 不查
    void WantName(const std::string& cid)
    {
        std::lock_guard<std::mutex> lock(g_nameMutex);

        if (g_names.count(cid) == 0 && g_nameLookups.count(cid) == 0)
            g_nameLookups[cid] = NameLookup();
    }

    std::string CachedName(const std::string& cid, bool noWait)
    {
        std::unique_lock<std::mutex> lock(g_nameMutex, std::defer_lock);
        if (!Acquire(lock, noWait))
            return {};

        const auto it = g_names.find(cid);
        return it != g_names.end() ? it->second : std::string();
    }

    // 工作线程里调: 每次最多查一个到期的号（要等主线程, 最长几秒）
    void ResolveNames()
    {
        const ULONGLONG now = GetTickCount64();
        std::string     cid;

        {
            std::lock_guard<std::mutex> lock(g_nameMutex);

            for (const auto& item : g_nameLookups)
            {
                if (item.second.dueTick <= now)
                {
                    cid = item.first;
                    break;
                }
            }
        }

        if (cid.empty())
            return;

        std::string name;
        const bool  found = GameCharaName(_strtoui64(cid.c_str(), nullptr, 16), &name);

        std::lock_guard<std::mutex> lock(g_nameMutex);

        const auto it = g_nameLookups.find(cid);
        if (it == g_nameLookups.end())
            return;

        if (found)
        {
            g_names[cid] = name;
            g_nameLookups.erase(it);
            g_heartbeatDirty.store(true);
            LogF("[charconfig] 角色名 cid=%s → %s", cid.c_str(), name.c_str());
            return;
        }

        if (++it->second.attempts >= kNameAttempts)
        {
            g_nameLookups.erase(it);
            LogF("[charconfig] 查不到 cid=%s 的角色名（%d 次）, 发布时不写角色名", cid.c_str(), kNameAttempts);
            return;
        }

        it->second.dueTick = GetTickCount64() + kNameRetryMs;
    }

    // 待查项是否已改名完成: .new 不在了, 且见过它或者 <NAME>.DAT 是挂钩之后写的
    bool WriteFinished(PendingWrite& item)
    {
        if (FileExists(item.newPath))
        {
            item.seen = true;
            return false;
        }

        if (item.seen)
            return true;

        WIN32_FILE_ATTRIBUTE_DATA attributes{};
        if (!GetFileAttributesExW(item.match.characterFile.c_str(), GetFileExInfoStandard, &attributes))
            return false;

        return FileTimeValue(attributes.ftLastWriteTime) + kTargetTimeSlack >= item.queuedTime;
    }

    // ---- 写之后 -----------------------------------------------------------------

    std::string JoinKeys(const std::vector<std::string>& keys)
    {
        std::string text;
        for (size_t i = 0; i < keys.size() && i < kDiffKeysShown; ++i)
        {
            if (!text.empty())
                text += ",";
            text += keys[i];
        }

        if (keys.size() > kDiffKeysShown)
            text += ",…";
        return text;
    }

    void HandleFinished(const PendingWrite& item, bool noWait)
    {
        const FileSpec&   spec = *item.match.file;
        const std::string name = spec.fileName;
        const std::string cid  = Narrow(item.match.cidHex);

        MachineConfig config;
        if (noWait)
        {
            bool        missing = false;
            std::string error;
            if (!ReadMachineConfig(g_root, &config, &missing, &error))
                config = MachineConfig();
        }
        else
        {
            config = g_machine->Get(GetTickCount64());
        }

        if (!config.FileEnabled(spec))
        {
            Record("不发布 " + name + " cid=" + cid + ": 本机没有启用共用或这一类没开", false, false, noWait);
            return;
        }

        const std::string key = BaselineKey(item.match);
        Baseline          baseline;
        bool              found = false;

        {
            std::unique_lock<std::mutex> lock(g_baselineMutex, std::defer_lock);
            if (!Acquire(lock, noWait))
                return;

            const auto it = g_baselines.find(key);
            if (it != g_baselines.end())
            {
                baseline = it->second;
                found    = true;
            }
        }

        const bool hasBaseline = found && baseline.has && baseline.setId == config.setId;

        PublishSource source;
        source.cid      = cid;
        source.charName = CachedName(cid, noWait);
        source.gamePid  = g_pid;

        const WrittenOutcome outcome =
            HandleWritten(g_root, config.setId, spec, item.match.characterFile, hasBaseline, baseline.hash, source);

        switch (outcome.result)
        {
            case WrittenResult::Published:
            {
                {
                    std::unique_lock<std::mutex> lock(g_baselineMutex, std::defer_lock);
                    if (Acquire(lock, noWait))
                    {
                        Baseline& updated = g_baselines[key];
                        updated.setId     = config.setId;
                        updated.has       = true;
                        updated.hash      = outcome.hash;
                        updated.bytes     = spec.category == Category::Hud ? outcome.content : Bytes();
                    }
                }

                char seq[32];
                _snprintf_s(seq, sizeof(seq), _TRUNCATE, "%llu", static_cast<unsigned long long>(outcome.seq));
                std::string text = "发布 " + name + " cid=" + cid + " 套=" + config.setId + " outbox=" + seq;

                if (spec.category == Category::Hud)
                {
                    if (baseline.bytes.empty())
                        text += " 不同的记录: 基线没有这个文件";
                    else
                        text += " 不同的记录: " + JoinKeys(AddonDiffKeys(baseline.bytes.data(), baseline.bytes.size(),
                                                                       outcome.content.data(), outcome.content.size()));
                }

                Record(text, false, true, noWait);
                break;
            }

            case WrittenResult::Unchanged:
                Record("丢弃 " + name + " cid=" + cid + ": 与登录时相同", false, true, noWait);
                break;

            case WrittenResult::NoBaseline:
                Record("不发布 " + name + " cid=" + cid + ": " +
                       (found && baseline.has && baseline.setId != config.setId ? std::string("登录后换了配置套") : outcome.reason),
                       false, false, noWait);
                break;

            case WrittenResult::Invalid:
                Record("不发布 " + name + " cid=" + cid + ": 写回的文件不合法: " + outcome.reason, true, false, noWait);
                break;

            case WrittenResult::ReadFailed:
            case WrittenResult::Failed:
            default:
                Record("发布 " + name + " cid=" + cid + " 失败: " + outcome.reason, true, false, noWait);
                break;
        }
    }

    // 取出已改名完成与等超时的待查项; key 非空时只看这个号这个文件的
    void TakeFinished(const std::string* key, std::vector<PendingWrite>* finished, std::vector<PendingWrite>* expired)
    {
        const ULONGLONG now = GetTickCount64();

        std::lock_guard<std::mutex> lock(g_pendingMutex);

        for (size_t i = 0; i < g_pending.size();)
        {
            PendingWrite& item = g_pending[i];

            if (key != nullptr && BaselineKey(item.match) != *key)
            {
                ++i;
                continue;
            }

            if (WriteFinished(item))
                finished->push_back(std::move(item));
            else if (now - item.queuedTick >= kWriteWaitMs)
                expired->push_back(std::move(item));
            else
            {
                ++i;
                continue;
            }

            g_pending.erase(g_pending.begin() + static_cast<std::ptrdiff_t>(i));
        }
    }

    void ProcessPending(const std::string* key)
    {
        std::vector<PendingWrite> finished;
        std::vector<PendingWrite> expired;
        TakeFinished(key, &finished, &expired);

        for (const auto& item : expired)
            Record("放弃 " + std::string(item.match.file->fileName) + " cid=" + Narrow(item.match.cidHex) +
                   ": 等 .new 改名超过 10 秒", true, false);

        for (const auto& item : finished)
            HandleFinished(item, false);

        if (!finished.empty() || !expired.empty())
            RequestHeartbeat();
    }

    // ---- 钩子 -------------------------------------------------------------------

    void OnRead(const wchar_t* path)
    {
        PathMatch match;
        if (!MatchReadPath(path, &match))
            return;

        const std::string key = BaselineKey(match);

        // 同一个文件刚写过、还没处理: 先处理, 免得放入把游戏刚写的内容盖掉
        ProcessPending(&key);

        std::string         configError;
        const MachineConfig config = g_machine->Get(GetTickCount64(), &configError);

        if (!config.FileEnabled(*match.file))
        {
            std::lock_guard<std::mutex> lock(g_baselineMutex);
            g_baselines.erase(key);
            return;
        }

        SetLastCid(match.cidHex);
        WantName(Narrow(match.cidHex));

        const PrepareOutcome outcome =
            PrepareRead(g_root, config.setId, *match.file, match.characterFile, kReadLockTimeoutMs);

        {
            std::lock_guard<std::mutex> lock(g_baselineMutex);
            Baseline& baseline = g_baselines[key];
            baseline.setId     = config.setId;
            baseline.has       = outcome.hasBaseline;
            baseline.hash      = outcome.baseline;
            baseline.bytes     = match.file->category == Category::Hud ? outcome.baselineBytes : Bytes();
        }

        const std::string name = match.file->fileName;
        const std::string cid  = Narrow(match.cidHex);
        char version[32];
        _snprintf_s(version, sizeof(version), _TRUNCATE, "%lld", static_cast<long long>(outcome.version));

        switch (outcome.result)
        {
            case PrepareResult::Placed:
                Record("放入 " + name + " cid=" + cid + " 套=" + config.setId + " 版本=" + version +
                       (outcome.pending ? "（本机发布未上传）" : ""), false, true);
                RequestHeartbeat();
                break;

            case PrepareResult::AlreadyLatest:
                Log(false, "已是最新 " + name + " cid=" + cid + " 版本=" + version);
                break;

            case PrepareResult::NoLatest:
                Log(false, "没有放入 " + name + " cid=" + cid + ": " + outcome.reason +
                    (outcome.hasBaseline ? "" : "（没有基线, 这次登录不发布）"));
                break;

            case PrepareResult::LatestInvalid:
            case PrepareResult::Failed:
            default:
                Record("放入 " + name + " cid=" + cid + " 失败: " + outcome.reason +
                       (outcome.hasBaseline ? "" : "（没有基线, 这次登录不发布）"), true, false);
                RequestHeartbeat();
                break;
        }
    }

    void OnWrite(const wchar_t* path)
    {
        PathMatch match;
        if (!MatchWritePath(path, g_pid, &match))
            return;

        // 游戏在保存一组文件: 上一个已改名完成的这里就处理掉, 不等工作线程（游戏随后可能直接退出）
        ProcessPending(nullptr);

        PendingWrite item;
        item.newPath    = path;
        item.match      = std::move(match);
        item.queuedTick = GetTickCount64();
        item.queuedTime = SystemTimeValue();

        std::lock_guard<std::mutex> lock(g_pendingMutex);
        g_pending.push_back(std::move(item));
    }

    void BeforeOpen(const wchar_t* path, int mode)
    {
        try
        {
            if (mode == kModeRead)
                OnRead(path);
            else if (mode == kModeWrite)
                OnWrite(path);
        }
        catch (const std::exception& ex)
        {
            LogF("[charconfig] 钩子里异常: %s", ex.what());
        }
        catch (...)
        {
            LogF("[charconfig] 钩子里未知异常");
        }
    }

    // ⚠ 单独一个函数: 带 __try 的函数里不能有需要展开的 C++ 对象 (MSVC C2712)
    void BeforeOpenGuarded(const wchar_t* path, int mode)
    {
        __try
        {
            BeforeOpen(path, mode);
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            LogF("[charconfig] 钩子里异常 code=0x%08X", GetExceptionCode());
        }
    }

    uint8_t HookedOpenFile(void* self, wchar_t* path, int mode)
    {
        // 计数包住对原函数的调用: 卸载端等它归零才释放跳板
        g_inside.fetch_add(1);

        if (g_active.load() && path != nullptr && (mode == kModeRead || mode == kModeWrite))
            BeforeOpenGuarded(path, mode);

        const uint8_t result = g_original(self, path, mode);

        g_inside.fetch_sub(1);
        return result;
    }

    // ---- 工作线程 ---------------------------------------------------------------

    void WorkerTick(ULONGLONG* lastHeartbeat)
    {
        try
        {
            ProcessPending(nullptr);
            ResolveNames();

            const ULONGLONG now = GetTickCount64();
            if (g_heartbeatDirty.exchange(false) || now - *lastHeartbeat >= kHeartbeatMs)
            {
                *lastHeartbeat = now;
                Heartbeat();
            }
        }
        catch (const std::exception& ex)
        {
            LogF("[charconfig] 工作线程异常: %s", ex.what());
        }
        catch (...)
        {
            LogF("[charconfig] 工作线程未知异常");
        }
    }

    void WorkerTickGuarded(ULONGLONG* lastHeartbeat)
    {
        __try
        {
            WorkerTick(lastHeartbeat);
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            LogF("[charconfig] 工作线程异常 code=0x%08X", GetExceptionCode());
        }
    }

    DWORD WINAPI WorkerMain(LPVOID)
    {
        ULONGLONG lastHeartbeat = GetTickCount64();

        while (WaitForSingleObject(g_stopEvent, kPollMs) == WAIT_TIMEOUT)
            WorkerTickGuarded(&lastHeartbeat);

        return 0;
    }

    bool StopWorker()
    {
        if (g_worker == nullptr)
            return true;

        SetEvent(g_stopEvent);
        g_workerRunning.store(false);

        // 最长一次处理要等配置套互斥体 10 秒
        if (WaitForSingleObject(g_worker, 20000) != WAIT_OBJECT_0)
        {
            LogF("[charconfig] ⚠ 工作线程 20 秒没退出, 卸载不安全");
            return false;
        }

        CloseHandle(g_worker);
        g_worker = nullptr;
        CloseHandle(g_stopEvent);
        g_stopEvent = nullptr;
        return true;
    }

    // ---- 挂钩 -------------------------------------------------------------------

    // 主备特征码都要命中、各自唯一、指向同一地址; 否则返回原因
    std::string ResolveTarget(uintptr_t* target)
    {
        size_t          primaryCount = 0;
        size_t          backupCount  = 0;
        const uintptr_t primary      = ScanTextCount(kPrimarySignature, &primaryCount);
        const uintptr_t backup       = ScanTextCount(kBackupSignature, &backupCount);

        auto describe = [](const char* label, size_t count) -> std::string
        {
            if (count == 0)
                return std::string(label) + "未命中";

            char text[64];
            _snprintf_s(text, sizeof(text), _TRUNCATE, "%s命中 %llu 处, 要求唯一", label, static_cast<unsigned long long>(count));
            return text;
        };

        std::string reason;
        if (primaryCount != 1)
            reason = describe("主特征码", primaryCount);
        if (backupCount != 1)
            reason += (reason.empty() ? "" : "; ") + describe("备特征码", backupCount);
        if (!reason.empty())
            return "找不到 FileInterface::OpenFile: " + reason;

        const uintptr_t base = ModuleBase();
        if (primary != backup)
        {
            char text[128];
            _snprintf_s(text, sizeof(text), _TRUNCATE, "主备特征码指向不同地址 (RVA 0x%llX / 0x%llX)",
                        static_cast<unsigned long long>(primary - base), static_cast<unsigned long long>(backup - base));
            return text;
        }

        if (!TextSectionContains(primary))
            return "FileInterface::OpenFile 的地址不在游戏代码节里";

        *target = primary;
        return {};
    }

    // 本机目录根: 默认 %LOCALAPPDATA%\Cat\char-config; 离线测试的假宿主用环境变量 CAT_CHARCONFIG_ROOT 指到临时目录
    std::wstring RootDirectory()
    {
        wchar_t     buffer[MAX_PATH]{};
        const DWORD length = GetEnvironmentVariableW(L"CAT_CHARCONFIG_ROOT", buffer, MAX_PATH);

        if (length > 0 && length < MAX_PATH)
            return buffer;

        return DefaultRoot();
    }

    void FailInstall(const std::string& reason)
    {
        {
            std::lock_guard<std::mutex> lock(g_statusMutex);
            g_hookError = reason;
        }

        Record("没有挂钩: " + reason, true, false);
        Heartbeat();
    }
}

bool CharConfigInstall()
{
    if (g_hooked.load())
        return true;

    g_root    = RootDirectory();
    g_pid     = GetCurrentProcessId();
    g_machine = std::make_unique<MachineConfigCache>(g_root, 1000);

    uintptr_t         target = 0;
    const std::string reason = ResolveTarget(&target);
    if (!reason.empty())
    {
        FailInstall(reason);
        return false;
    }

    MH_STATUS status = MH_Initialize();
    if (status != MH_OK && status != MH_ERROR_ALREADY_INITIALIZED)
    {
        FailInstall(std::string("MinHook 初始化失败: ") + MH_StatusToString(status));
        return false;
    }

    g_target = reinterpret_cast<void*>(target);

    status = MH_CreateHook(g_target, reinterpret_cast<LPVOID>(&HookedOpenFile), reinterpret_cast<LPVOID*>(&g_original));
    if (status != MH_OK)
    {
        MH_Uninitialize();
        FailInstall(std::string("创建钩子失败: ") + MH_StatusToString(status));
        return false;
    }

    g_active.store(true);

    status = MH_EnableHook(g_target);
    if (status != MH_OK)
    {
        g_active.store(false);
        MH_RemoveHook(g_target);
        MH_Uninitialize();
        FailInstall(std::string("启用钩子失败: ") + MH_StatusToString(status));
        return false;
    }

    memcpy(g_patch, g_target, kPatchBytes);
    g_hooked.store(true);
    g_targetRva = target - ModuleBase();

    g_stopEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    g_worker    = g_stopEvent != nullptr ? CreateThread(nullptr, 0, WorkerMain, nullptr, 0, nullptr) : nullptr;

    g_workerRunning.store(g_worker != nullptr);

    if (g_worker == nullptr)
    {
        LogF("[charconfig] ⚠ 工作线程没起来 err=%lu, 写回只在游戏下次读写共用文件或退出时处理", GetLastError());

        if (g_stopEvent != nullptr)
        {
            CloseHandle(g_stopEvent);
            g_stopEvent = nullptr;
        }
    }

    char text[96];
    _snprintf_s(text, sizeof(text), _TRUNCATE, "已挂上 FileInterface::OpenFile (RVA 0x%llX)",
                static_cast<unsigned long long>(g_targetRva));
    Record(text, false, true);
    Heartbeat();

    return true;
}

bool CharConfigUninstall()
{
    if (!g_hooked.load())
        return StopWorker();

    if (memcmp(g_target, g_patch, kPatchBytes) != 0)
    {
        LogF("[charconfig] 函数头已被别人改写, 不还原, 模块只能留在进程里");
        return false;
    }

    g_active.store(false);

    const MH_STATUS status = MH_DisableHook(g_target);
    if (status != MH_OK)
    {
        LogF("[charconfig] 禁用钩子失败: %s, 模块只能留在进程里", MH_StatusToString(status));
        return false;
    }

    g_hooked.store(false);

    // 禁用之后可能仍有调用停在钩子里（原函数在等磁盘）, 等它们出来再释放跳板
    for (int i = 0; i < 1000 && g_inside.load() > 0; ++i)
        Sleep(10);

    if (g_inside.load() > 0)
    {
        LogF("[charconfig] ⚠ 仍有调用停在钩子里, 卸载不安全");
        return false;
    }

    // 计数归零只说明没人在钩子体内, 可能还有线程停在返回路径上
    Sleep(200);

    if (!StopWorker())
        return false;

    {
        std::lock_guard<std::mutex> lock(g_pendingMutex);
        if (!g_pending.empty())
            LogF("[charconfig] 卸载时还有 %llu 个待查项, 不再处理", static_cast<unsigned long long>(g_pending.size()));
        g_pending.clear();
    }

    MH_RemoveHook(g_target);
    MH_Uninitialize();

    Record("已卸下 FileInterface::OpenFile 钩子", false, false);
    {
        std::lock_guard<std::mutex> lock(g_statusMutex);
        g_lastError = "模块已卸载";
    }
    Heartbeat();

    return true;
}

void CharConfigProcessExit()
{
    if (!g_active.load())
        return;

    std::vector<PendingWrite> finished;

    {
        std::unique_lock<std::mutex> lock(g_pendingMutex, std::try_to_lock);
        if (!lock.owns_lock())
            return;

        for (auto& item : g_pending)
        {
            if (WriteFinished(item))
                finished.push_back(std::move(item));
        }

        g_pending.clear();
    }

    for (const auto& item : finished)
        HandleFinished(item, true);

    if (!finished.empty())
        Heartbeat(true);
}

std::string CharConfigStatus()
{
    MachineConfig config;
    std::string   configError = "模块还没初始化共用配置";

    if (g_machine)
        config = g_machine->Get(GetTickCount64(), &configError);

    size_t pending = 0;
    {
        std::lock_guard<std::mutex> lock(g_pendingMutex);
        pending = g_pending.size();
    }

    size_t baselines = 0;
    {
        std::lock_guard<std::mutex> lock(g_baselineMutex);
        baselines = g_baselines.size();
    }

    std::string lastCid;
    {
        std::lock_guard<std::mutex> lock(g_statusMutex);
        lastCid = g_lastCid;
    }

    const std::string lastName = lastCid.empty() ? std::string() : CachedName(lastCid, false);

    std::lock_guard<std::mutex> lock(g_statusMutex);

    char head[160];
    _snprintf_s(head, sizeof(head), _TRUNCATE, "OK hooked=%d rva=0x%llX enabled=%d pending=%llu baselines=%llu",
                g_hooked.load() ? 1 : 0, static_cast<unsigned long long>(g_targetRva), config.enabled ? 1 : 0,
                static_cast<unsigned long long>(pending), static_cast<unsigned long long>(baselines));

    std::string response = head;
    response += " set=" + (config.setId.empty() ? std::string("-") : config.setId);
    response += "\nsetName=" + config.setName;
    response += "\nconfigError=" + configError;
    response += "\nhookError=" + g_hookError;
    response += "\nlastCid=" + g_lastCid;
    response += "\nlastName=" + lastName;
    response += "\nlastError=" + g_lastError;
    response += "\nrecent:";

    for (const auto& line : g_recent)
        response += "\n" + line;

    return response;
}
