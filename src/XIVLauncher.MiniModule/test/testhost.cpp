// 假宿主: 冒充游戏进程给模块做离线冒烟
//
// 只复刻模块依赖的两个前提 —— 一个类名为 FFXIVGAME 的窗口 + 在主线程上跑的消息循环。
// 有了它就能在不开游戏的情况下把「注入 / 管道 / 主线程派发 / 卸载」整条链子跑通,
// 剩下真机才能答的问题只有一个: 游戏的窗口线程是不是它的主线程。
//
// 定义 FAKE_OPENFILE 编出的版本另外冒充 FileInterface::OpenFile:
//   - testhost-callsites.asm 在代码节里放两处调用点, 字节与游戏里的主备特征码相同, 都指向 FakeOpenFile, 模块能照常找到并挂钩;
//   - 从标准输入读命令, 照游戏的方式打开文件, 每条命令回一行到标准输出:
//       READ <路径>             以读方式打开（mode 0）后关闭              → OK <返回值>
//       SAVE <路径>|<内容来源>  照游戏保存: 写 <路径>.new.<hex>.<pid hex>（mode 1）→ 删 .old → 原文件改名 .old → .new 改名为原文件 → OK
//       QUIT                    结束                                      → OK
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <cstdint>
#include <cstdio>

#ifdef FAKE_OPENFILE
#include <string>
#include <vector>

namespace
{
    // FileInterface 只用到的部分: 打开后的句柄
    struct FakeFileInterface
    {
        HANDLE handle = INVALID_HANDLE_VALUE;
    };
}

// 签名同 FileInterface::OpenFile(self, widePath, mode); 1 = 成功
extern "C" __declspec(noinline) uint8_t FakeOpenFile(void* self, wchar_t* path, int mode)
{
    auto* file = static_cast<FakeFileInterface*>(self);

    const DWORD access      = mode == 0 ? GENERIC_READ : GENERIC_WRITE;
    const DWORD disposition = mode == 0 ? OPEN_EXISTING : (mode == 1 ? CREATE_ALWAYS : OPEN_ALWAYS);

    file->handle = CreateFileW(path, access, FILE_SHARE_READ, nullptr, disposition, FILE_ATTRIBUTE_NORMAL, nullptr);
    return file->handle != INVALID_HANDLE_VALUE ? 1 : 0;
}

namespace
{
    std::wstring Widen(const std::string& text)
    {
        if (text.empty())
            return {};

        const int length = MultiByteToWideChar(CP_UTF8, 0, text.data(), static_cast<int>(text.size()), nullptr, 0);
        std::wstring result(static_cast<size_t>(length), L'\0');
        MultiByteToWideChar(CP_UTF8, 0, text.data(), static_cast<int>(text.size()), result.data(), length);
        return result;
    }

    void Reply(const char* text)
    {
        printf("%s\n", text);
        fflush(stdout);
    }

    bool ReadAll(const std::wstring& path, std::vector<char>* out)
    {
        const HANDLE file = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, 0, nullptr);
        if (file == INVALID_HANDLE_VALUE)
            return false;

        LARGE_INTEGER size{};
        GetFileSizeEx(file, &size);
        out->resize(static_cast<size_t>(size.QuadPart));

        DWORD read = 0;
        const BOOL ok = out->empty() || ReadFile(file, out->data(), static_cast<DWORD>(out->size()), &read, nullptr);
        CloseHandle(file);
        return ok && read == out->size();
    }

    void HandleRead(const std::wstring& path)
    {
        FakeFileInterface file;
        std::vector<wchar_t> buffer(path.begin(), path.end());
        buffer.push_back(L'\0');

        const uint8_t result = FakeOpenFile(&file, buffer.data(), 0);
        if (file.handle != INVALID_HANDLE_VALUE)
            CloseHandle(file.handle);

        char text[32];
        _snprintf_s(text, sizeof(text), _TRUNCATE, "OK %u", result);
        Reply(text);
    }

    void HandleSave(const std::wstring& path, const std::wstring& source)
    {
        std::vector<char> content;
        if (!ReadAll(source, &content))
        {
            Reply("FAIL read-source");
            return;
        }

        wchar_t suffix[64];
        _snwprintf_s(suffix, _countof(suffix), _TRUNCATE, L".new.%llX.%lX", GetTickCount64(), GetCurrentProcessId());
        const std::wstring newPath = path + suffix;
        const std::wstring oldPath = path + L".old";

        FakeFileInterface    file;
        std::vector<wchar_t> buffer(newPath.begin(), newPath.end());
        buffer.push_back(L'\0');

        if (FakeOpenFile(&file, buffer.data(), 1) != 1)
        {
            Reply("FAIL open-new");
            return;
        }

        DWORD written = 0;
        WriteFile(file.handle, content.data(), static_cast<DWORD>(content.size()), &written, nullptr);
        CloseHandle(file.handle);

        DeleteFileW(oldPath.c_str());
        MoveFileW(path.c_str(), oldPath.c_str());

        if (!MoveFileW(newPath.c_str(), path.c_str()))
        {
            Reply("FAIL rename");
            return;
        }

        Reply("OK");
    }

    DWORD WINAPI CommandLoop(LPVOID window)
    {
        char line[4096];

        while (fgets(line, sizeof(line), stdin) != nullptr)
        {
            std::string command = line;
            while (!command.empty() && (command.back() == '\n' || command.back() == '\r'))
                command.pop_back();

            // Windows PowerShell 5.1 写标准输入时第一行带 UTF-8 BOM
            if (command.rfind("\xEF\xBB\xBF", 0) == 0)
                command.erase(0, 3);

            if (command == "QUIT")
            {
                Reply("OK");
                break;
            }

            if (command.rfind("READ ", 0) == 0)
            {
                HandleRead(Widen(command.substr(5)));
                continue;
            }

            if (command.rfind("SAVE ", 0) == 0)
            {
                const std::string rest      = command.substr(5);
                const size_t      separator = rest.find('|');

                if (separator == std::string::npos)
                    Reply("FAIL usage");
                else
                    HandleSave(Widen(rest.substr(0, separator)), Widen(rest.substr(separator + 1)));
                continue;
            }

            Reply("FAIL unknown-command");
        }

        PostMessageW(static_cast<HWND>(window), WM_CLOSE, 0, 0);
        return 0;
    }
}
#endif

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

#ifdef FAKE_OPENFILE
    const HANDLE commands = CreateThread(nullptr, 0, CommandLoop, hwnd, 0, nullptr);
    if (commands != nullptr)
        CloseHandle(commands);
#else
    printf("testhost pid=%lu 主线程=%lu hwnd=0x%p\n", GetCurrentProcessId(), GetCurrentThreadId(), hwnd);
    fflush(stdout);
#endif

    MSG message;
    while (GetMessageW(&message, nullptr, 0, 0) > 0)
    {
        TranslateMessage(&message);
        DispatchMessageW(&message);
    }

    return 0;
}
