// 入口: 启动器用 CreateRemoteThread + LoadLibraryW 把本 DLL 注进游戏进程。
//
// DllMain 里什么都不干（loader lock 下做事是经典死锁源）, 只起一条自己的线程。
#include "MiniModule.h"

namespace
{
    HMODULE g_module = nullptr;

    DWORD WINAPI ModuleMain(LPVOID)
    {
        LogInit();
        LogF("=== MiniLauncher 模块 %s 已注入 pid=%lu ===", MINIMODULE_VERSION, GetCurrentProcessId());

        const bool mainThreadOk = MainThreadInstall();

        if (!mainThreadOk)
            LogF("[module] 主线程通道未建立, 只能应答 PING/VERSION");

        PipeServerRun();

        // 顺序要紧: 先把自己起的线程收干净, 再谈还原窗口和卸载自己
        GameStopKeepAlive();

        const bool restored = MainThreadUninstall();

        LogF("=== MiniLauncher 模块退出 (窗口已还原=%d) ===", restored ? 1 : 0);
        LogShutdown();

        // 还原不了就不能把自己从进程里摘掉 —— 那样游戏下一条消息会跳进已释放的代码
        if (restored)
            FreeLibraryAndExitThread(g_module, 0); // 不返回

        return 0;
    }
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason != DLL_PROCESS_ATTACH)
        return TRUE;

    g_module = module;
    DisableThreadLibraryCalls(module);

    const HANDLE thread = CreateThread(nullptr, 0, ModuleMain, nullptr, 0, nullptr);

    if (thread != nullptr)
        CloseHandle(thread);

    return TRUE;
}
