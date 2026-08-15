// 假宿主: 冒充游戏进程给模块做离线冒烟
//
// 只复刻模块依赖的两个前提 —— 一个类名为 FFXIVGAME 的窗口 + 在主线程上跑的消息循环。
// 有了它就能在不开游戏的情况下把「注入 / 管道 / 主线程派发 / 卸载」整条链子跑通,
// 剩下真机才能答的问题只有一个: 游戏的窗口线程是不是它的主线程。
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <cstdio>

LRESULT CALLBACK TestWndProc(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam)
{
    if (message == WM_DESTROY)
    {
        PostQuitMessage(0);
        return 0;
    }

    return DefWindowProcW(hwnd, message, wParam, lParam);
}

int wmain()
{
    WNDCLASSEXW wc{};
    wc.cbSize        = sizeof(wc);
    wc.lpfnWndProc   = TestWndProc;
    wc.hInstance     = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"FFXIVGAME";

    if (RegisterClassExW(&wc) == 0)
    {
        printf("RegisterClassEx 失败 err=%lu\n", GetLastError());
        return 1;
    }

    const HWND hwnd = CreateWindowExW(0, L"FFXIVGAME", L"MiniModule TestHost", WS_OVERLAPPEDWINDOW,
                                      CW_USEDEFAULT, CW_USEDEFAULT, 400, 200,
                                      nullptr, nullptr, wc.hInstance, nullptr);

    if (hwnd == nullptr)
    {
        printf("CreateWindowEx 失败 err=%lu\n", GetLastError());
        return 1;
    }

    printf("testhost pid=%lu 主线程=%lu hwnd=0x%p\n", GetCurrentProcessId(), GetCurrentThreadId(), hwnd);
    fflush(stdout);

    MSG message;
    while (GetMessageW(&message, nullptr, 0, 0) > 0)
    {
        TranslateMessage(&message);
        DispatchMessageW(&message);
    }

    return 0;
}
