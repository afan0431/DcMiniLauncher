// 在游戏主线程上执行代码
//
// 手法: 给游戏窗口（类名 FFXIVGAME）子类化 WndProc + PostMessage 自定义消息。
// 窗口消息一定在「创建该窗口的线程」上被派发, 所以回调天然跑在那个线程上, 零偏移零特征码。
// 这一步同时把「窗口线程」和「进程主线程（创建时间最早的线程）」两个 id 都记下来,
// 实测核对二者是否同一个 —— 这正是本轮生死闸要回答的问题。
#include "MiniModule.h"

#include <atomic>
#include <memory>
#include <mutex>
#include <tlhelp32.h>
#include <vector>

namespace
{
    // WM_APP + 'ML'
    constexpr UINT WM_MINILAUNCHER_RUN = WM_APP + 0x4D4C;

    struct Job
    {
        std::function<void()> work;
        HANDLE                done      = nullptr;
        std::atomic<bool>     abandoned {false};
    };

    HWND     g_window       = nullptr;
    DWORD    g_windowThread = 0;
    WNDPROC  g_originalProc = nullptr;

    std::mutex                             g_queueMutex;
    std::vector<std::shared_ptr<Job>>      g_queue;

    struct FindWindowContext
    {
        DWORD processId;
        HWND  found;
    };

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

    // 绝不能让异常顺着 WndProc 冒回游戏 —— 那是直接把游戏搞崩。
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
            pending.swap(g_queue);
        }

        for (auto& job : pending)
        {
            if (!job->abandoned.load())
                RunGuarded(job->work);

            SetEvent(job->done);
        }
    }

    LRESULT CALLBACK HookedWndProc(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam)
    {
        if (message == WM_MINILAUNCHER_RUN)
        {
            DrainQueue();
            return 0;
        }

        return CallWindowProcW(g_originalProc, hwnd, message, wParam, lParam);
    }
}

bool MainThreadInstall()
{
    // 注入时机由启动器控制（等到窗口出现之后), 但仍留一段重试, 免得抢跑就直接判死
    for (int attempt = 0; attempt < 120 && g_window == nullptr; ++attempt)
    {
        g_window = FindGameWindow();

        if (g_window == nullptr)
            Sleep(500);
    }

    if (g_window == nullptr)
    {
        LogF("[mainthread] 没找到游戏窗口 (class=FFXIVGAME), 无法建立主线程通道");
        return false;
    }

    g_windowThread = GetWindowThreadProcessId(g_window, nullptr);

    g_originalProc = reinterpret_cast<WNDPROC>(
        SetWindowLongPtrW(g_window, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(HookedWndProc)));

    if (g_originalProc == nullptr)
    {
        LogF("[mainthread] 子类化窗口失败 err=%lu", GetLastError());
        g_window = nullptr;
        return false;
    }

    LogF("[mainthread] 已子类化游戏窗口 hwnd=0x%p 窗口线程=%lu 进程主线程=%lu",
         g_window, g_windowThread, ProcessMainThreadId());

    return true;
}

bool MainThreadUninstall()
{
    if (g_window == nullptr || g_originalProc == nullptr)
        return true;

    // 只在当前 WndProc 还是我们自己时才还原 —— 若之后又有别人（比如 bot 的 overlay）套了一层,
    // 强行还原会把它那层一起抹掉
    const auto current = reinterpret_cast<WNDPROC>(GetWindowLongPtrW(g_window, GWLP_WNDPROC));

    if (current != HookedWndProc)
    {
        LogF("[mainthread] 当前 WndProc 已被别人接管, 不还原（避免连带抹掉别人那层), 模块只能留在进程里");
        return false;
    }

    SetWindowLongPtrW(g_window, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(g_originalProc));

    // 还原之后可能仍有一次派发正卡在我们的 WndProc 里。发一条同步消息等窗口线程走完一轮,
    // 回来时就能确定没有任何调用栈还停在本模块代码上, 卸载才是安全的
    DWORD_PTR result = 0;
    SendMessageTimeoutW(g_window, WM_NULL, 0, 0, SMTO_ABORTIFHUNG, 5000, &result);

    LogF("[mainthread] 已还原游戏窗口 WndProc");

    g_window       = nullptr;
    g_originalProc = nullptr;
    return true;
}

bool MainThreadRun(const std::function<void()>& work, DWORD timeoutMs)
{
    if (g_window == nullptr)
        return false;

    if (GetCurrentThreadId() == g_windowThread)
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

    if (!PostMessageW(g_window, WM_MINILAUNCHER_RUN, 0, 0))
    {
        LogF("[mainthread] PostMessage 失败 err=%lu", GetLastError());
        job->abandoned.store(true);
        CloseHandle(job->done);
        return false;
    }

    const bool ok = WaitForSingleObject(job->done, timeoutMs) == WAIT_OBJECT_0;

    if (!ok)
    {
        // 超时: job 可能还排在队里。标记作废让派发端跳过执行, shared_ptr 保证对象活到那时,
        // 所以这里绝不能直接销毁 —— 事件句柄留给派发端最后 SetEvent 用完由 shared_ptr 析构收
        job->abandoned.store(true);
        LogF("[mainthread] 等待主线程执行超时 (%lums)", timeoutMs);
    }

    // done 句柄的所有权仍在 job 里; 这里只有在确定没人再碰它时才关
    if (ok)
    {
        CloseHandle(job->done);
        job->done = nullptr;
    }

    return ok;
}

HWND  MainThreadWindow()       { return g_window; }
DWORD MainThreadWindowThread() { return g_windowThread; }

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
