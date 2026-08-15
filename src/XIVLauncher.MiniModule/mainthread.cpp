// 在游戏主线程上执行代码 —— 挂 Framework::Tick 的虚表项
//
// ⚠ 2026-08-15 血的教训: 第一版走的是「子类化游戏窗口 WndProc + PostMessage」。它零偏移零特征码,
//   过生死闸够用, 在角色选择页调 returnToTitle 也没事 —— 但**从游戏内**调 returnToTitle 会闪退:
//   崩溃包显示 C0000005 崩在主线程的 Framework tick 里 (栈上就是 tick), 就在调用后 1.6 秒。
//   消息泵那个点并不是游戏状态机能承受重量级状态转换的地方。计划原本写的就是「hook 每帧函数
//   (如 Framework::Tick) 在主线程上执行」, 这次老实照做。
//
// 手法: Framework 的 Tick 是虚表第 4 号 (ClientStructs Framework.cs:149)。改虚表项比 inline hook
// 简单得多, 也不用引第三方库; 我们在原函数之前把队列里的活干完, 然后照常调原函数。
#include "MiniModule.h"
#include "offsets.h"

#include <atomic>
#include <memory>
#include <mutex>
#include <tlhelp32.h>
#include <vector>

namespace
{
    using TickFn = char (*)(void* framework);

    struct Job
    {
        std::function<void()> work;
        HANDLE                done      = nullptr;
        std::atomic<bool>     abandoned {false};
    };

    void*   g_framework    = nullptr;
    void**  g_vtable       = nullptr;
    TickFn  g_originalTick = nullptr;
    DWORD   g_tickThread   = 0;

    std::atomic<int>  g_insideHook {0};
    std::atomic<bool> g_installed  {false};

    std::mutex                        g_queueMutex;
    std::vector<std::shared_ptr<Job>> g_queue;

    // 绝不能让异常顺着 tick 冒回游戏。
    // ⚠ 单独一个函数: 带 __try 的函数里不能有需要展开的 C++ 对象 (MSVC C2712)
    void RunGuarded(const std::function<void()>& work)
    {
        __try
        {
            work();
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            LogF("[mainthread] job 执行中异常 code=0x%08X", GetExceptionCode());
        }
    }

    void DrainQueue()
    {
        std::vector<std::shared_ptr<Job>> pending;

        {
            std::lock_guard<std::mutex> lock(g_queueMutex);

            if (g_queue.empty())
                return;

            pending.swap(g_queue);
        }

        for (auto& job : pending)
        {
            if (!job->abandoned.load())
                RunGuarded(job->work);

            SetEvent(job->done);
        }
    }

    char HookedTick(void* framework)
    {
        g_insideHook.fetch_add(1);

        g_tickThread = GetCurrentThreadId();
        DrainQueue();

        const auto original = g_originalTick;

        g_insideHook.fetch_sub(1);

        // 卸载时可能刚好把 g_originalTick 清了; 那种情况下什么都不做比跳空地址强
        return original != nullptr ? original(framework) : 1;
    }

    bool PatchVTable(void* newFunction, void** previous)
    {
        DWORD oldProtect = 0;
        auto* slot       = &g_vtable[offsets::FRAMEWORK_TICK_VF];

        if (!VirtualProtect(slot, sizeof(void*), PAGE_READWRITE, &oldProtect))
        {
            LogF("[mainthread] VirtualProtect 失败 err=%lu", GetLastError());
            return false;
        }

        if (previous != nullptr)
            *previous = *slot;

        *slot = newFunction;

        DWORD ignored = 0;
        VirtualProtect(slot, sizeof(void*), oldProtect, &ignored);

        return true;
    }
}

bool MainThreadInstall()
{
    if (g_installed.load())
        return true;

    if (!GameResolve())
    {
        LogF("[mainthread] 特征码没解析出来, 无法挂 tick");
        return false;
    }

    // 刚注入时 Framework 可能还没建起来, 给一段重试
    for (int attempt = 0; attempt < 120 && g_framework == nullptr; ++attempt)
    {
        g_framework = GameFrameworkPointer();

        if (g_framework == nullptr)
            Sleep(500);
    }

    if (g_framework == nullptr)
    {
        LogF("[mainthread] 拿不到 Framework 实例");
        return false;
    }

    g_vtable = *reinterpret_cast<void***>(g_framework);

    if (!PatchVTable(reinterpret_cast<void*>(&HookedTick), reinterpret_cast<void**>(&g_originalTick)))
        return false;

    g_installed.store(true);
    LogF("[mainthread] 已挂上 Framework::Tick (framework=0x%p vtable=0x%p 原函数=0x%p)",
         g_framework, g_vtable, reinterpret_cast<void*>(g_originalTick));

    return true;
}

bool MainThreadUninstall()
{
    if (!g_installed.load())
        return true;

    void* current = nullptr;

    {
        DWORD oldProtect = 0;
        auto* slot       = &g_vtable[offsets::FRAMEWORK_TICK_VF];

        if (VirtualProtect(slot, sizeof(void*), PAGE_READWRITE, &oldProtect))
        {
            current = *slot;

            // 别人又在我们之上套了一层就不还原 —— 强行还原会把它那层一起抹掉
            if (current == reinterpret_cast<void*>(&HookedTick))
                *slot = reinterpret_cast<void*>(g_originalTick);

            DWORD ignored = 0;
            VirtualProtect(slot, sizeof(void*), oldProtect, &ignored);
        }
    }

    if (current != reinterpret_cast<void*>(&HookedTick))
    {
        LogF("[mainthread] 虚表项已被别人接管, 不还原, 模块只能留在进程里");
        return false;
    }

    // 还原之后可能仍有一帧正卡在我们的 hook 里, 等它出来再谈卸载
    for (int i = 0; i < 200 && g_insideHook.load() > 0; ++i)
        Sleep(10);

    if (g_insideHook.load() > 0)
    {
        LogF("[mainthread] ⚠ 仍有调用停在 hook 里, 卸载不安全");
        return false;
    }

    g_installed.store(false);
    g_originalTick = nullptr;

    LogF("[mainthread] 已还原 Framework::Tick");
    return true;
}

bool MainThreadRun(const std::function<void()>& work, DWORD timeoutMs)
{
    if (!g_installed.load())
        return false;

    if (GetCurrentThreadId() == g_tickThread && g_insideHook.load() > 0)
    {
        work();
        return true;
    }

    auto job  = std::make_shared<Job>();
    job->work = work;
    job->done = CreateEventW(nullptr, TRUE, FALSE, nullptr);

    if (job->done == nullptr)
        return false;

    {
        std::lock_guard<std::mutex> lock(g_queueMutex);
        g_queue.push_back(job);
    }

    const bool ok = WaitForSingleObject(job->done, timeoutMs) == WAIT_OBJECT_0;

    if (!ok)
    {
        // 超时: job 可能还排在队里。标记作废让 tick 跳过它。
        // ⚠ 调用方给的闭包一律要求「捕获的东西自己活着」(shared_ptr 传值), 因为这里没法保证
        //   tick 不会在下一帧才碰它 —— 早期版本闭包按引用捕栈变量, 超时后就是写野指针。
        job->abandoned.store(true);
        LogF("[mainthread] 等待主线程执行超时 (%lums)", timeoutMs);
    }
    else
    {
        CloseHandle(job->done);
        job->done = nullptr;
    }

    return ok;
}

HWND  MainThreadWindow()       { return nullptr; } // 不再依赖窗口
DWORD MainThreadWindowThread() { return g_tickThread; }

DWORD ProcessMainThreadId()
{
    const DWORD processId = GetCurrentProcessId();

    const HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
    if (snapshot == INVALID_HANDLE_VALUE)
        return 0;

    THREADENTRY32 entry{};
    entry.dwSize = sizeof(entry);

    DWORD     mainThread = 0;
    ULONGLONG earliest   = MAXULONGLONG;

    if (Thread32First(snapshot, &entry))
    {
        do
        {
            if (entry.th32OwnerProcessID != processId)
                continue;

            const HANDLE thread = OpenThread(THREAD_QUERY_LIMITED_INFORMATION, FALSE, entry.th32ThreadID);
            if (thread == nullptr)
                continue;

            FILETIME creation{}, exit{}, kernel{}, user{};

            if (GetThreadTimes(thread, &creation, &exit, &kernel, &user))
            {
                const ULONGLONG value = (static_cast<ULONGLONG>(creation.dwHighDateTime) << 32) | creation.dwLowDateTime;

                if (value < earliest)
                {
                    earliest   = value;
                    mainThread = entry.th32ThreadID;
                }
            }

            CloseHandle(thread);
        }
        while (Thread32Next(snapshot, &entry));
    }

    CloseHandle(snapshot);
    return mainThread;
}
