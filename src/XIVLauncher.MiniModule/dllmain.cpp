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
        {
            LogF("[module] 主线程通道未建立, 只能应答 PING/VERSION");
        }
        else
        {
            GameStartTitleGuard(); // 常驻: 别让客户端停在标题时飘进片头动画
            ContextMenuInstall();  // 选角界面右键「跨区旅行」; 装不上只少这一项
        }

        PipeServerRun();

        // 顺序要紧: 先把自己起的线程收干净, 再还原各处 hook, 最后才谈卸载自己。
        // 右键菜单的 hook 要在 Tick 之前还原 —— 它安装时借的是主线程通道。
        GameStopKeepAlive();

        const bool menuRestored = ContextMenuUninstall();
        const bool restored     = MainThreadUninstall() && menuRestored;

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
