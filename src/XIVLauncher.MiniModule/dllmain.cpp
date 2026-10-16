// 入口: 启动器用 CreateRemoteThread + LoadLibraryW 把本 DLL 注进游戏进程。
//
// DllMain 里什么都不干（loader lock 下做事是经典死锁源）, 只起一条自己的线程。
#include "MiniModule.h"

#include <cstring>

namespace
{
    HMODULE g_module   = nullptr;
    bool    g_readOnly = false;

    // 启动器注入前按 pid 写的模式文件 <ProgramData>\DcMiniLauncher\module-<pid>.mode: 内容以 readonly 开头 = 只读模式。
    // 只为读角色和等级而注入时用它: 不装标题守卫、不装选角右键菜单, 游戏的行为与没注入时一样。没有这个文件 = 照常全装
    bool ReadModeFile()
    {
        wchar_t     programData[MAX_PATH]{};
        const DWORD length = GetEnvironmentVariableW(L"ProgramData", programData, MAX_PATH);

        std::wstring path = (length > 0 && length < MAX_PATH) ? programData : L"C:\\ProgramData";

        wchar_t fileName[64];
        _snwprintf_s(fileName, _countof(fileName), _TRUNCATE, L"\\DcMiniLauncher\\module-%lu.mode", GetCurrentProcessId());
        path += fileName;

        const HANDLE file = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr,
                                        OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);

        if (file == INVALID_HANDLE_VALUE)
            return false;

        char  buffer[32]{};
        DWORD read = 0;
        const BOOL ok = ReadFile(file, buffer, sizeof(buffer) - 1, &read, nullptr);
        CloseHandle(file);

        return ok && read >= 8 && strncmp(buffer, "readonly", 8) == 0;
    }

    DWORD WINAPI ModuleMain(LPVOID)
    {
        LogInit();
        g_readOnly = ReadModeFile();
        LogF("=== MiniLauncher 模块 %s 已注入 pid=%lu 模式=%s ===", MINIMODULE_VERSION, GetCurrentProcessId(), g_readOnly ? "只读" : "完整");

        const bool mainThreadOk = MainThreadInstall();

        if (!mainThreadOk)
        {
            LogF("[module] 主线程通道未建立, 只能应答 PING/VERSION");
        }
        else if (g_readOnly)
        {
            LogF("[module] 只读模式: 不装标题守卫和选角右键菜单");
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

bool ModuleReadOnly()
{
    return g_readOnly;
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
