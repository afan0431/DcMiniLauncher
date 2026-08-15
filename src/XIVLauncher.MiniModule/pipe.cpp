// 命名管道服务端: \\.\pipe\minilauncher-<pid>
//
// 方向是「启动器命令模块」, 不是模块对外提供服务 —— 在线那半（下单/轮询/RefreshSID）留在启动器 C# 里,
// native 只被命令。按 PID 起名, 多开天然不串。
//
// 协议是行式文本 (UTF-8, 消息模式一问一答):
//   PING        → OK PONG
//   VERSION     → OK version=… pid=… log=…
//   MAINTHREAD  → OK tid=… window=… main=… same=0|1   （在主线程上取到的线程 id）
//   UNLOAD      → OK BYE, 随后模块自我卸载
#include "MiniModule.h"

#include <atomic>
#include <cstdio>
#include <string>

namespace
{
    std::atomic<bool> g_stop {false};

    std::string PipeName()
    {
        char name[64];
        _snprintf_s(name, sizeof(name), _TRUNCATE, "\\\\.\\pipe\\minilauncher-%lu", GetCurrentProcessId());
        return name;
    }

    std::string Trim(const std::string& value)
    {
        const auto begin = value.find_first_not_of(" \t\r\n");
        if (begin == std::string::npos)
            return {};

        const auto end = value.find_last_not_of(" \t\r\n");
        return value.substr(begin, end - begin + 1);
    }

    std::string HandleMainThread()
    {
        DWORD observed = 0;

        // 3 秒: 游戏正常跑时窗口消息一帧内就派发完; 拿不到说明主线程被卡住或通道没建起来
        if (!MainThreadRun([&observed] { observed = GetCurrentThreadId(); }, 3000))
            return "FAIL mainthread-timeout";

        const DWORD windowThread = MainThreadWindowThread();
        const DWORD mainThread   = ProcessMainThreadId();

        char response[160];
        _snprintf_s(response, sizeof(response), _TRUNCATE, "OK tid=%lu window=%lu main=%lu same=%d",
                    observed, windowThread, mainThread, observed == mainThread ? 1 : 0);

        LogF("[pipe] MAINTHREAD → %s", response);
        return response;
    }

    std::string HandleCommand(const std::string& command)
    {
        if (command == "PING")
            return "OK PONG";

        if (command == "VERSION")
        {
            char response[512];
            _snprintf_s(response, sizeof(response), _TRUNCATE, "OK version=%s pid=%lu log=%s",
                        MINIMODULE_VERSION, GetCurrentProcessId(), LogFilePath().c_str());
            return response;
        }

        if (command == "MAINTHREAD")
            return HandleMainThread();

        // 只读自检: 解析特征码 + 顺着 Framework 读到大厅主机名, 用来核对偏移对不对
        if (command == "PROBE")
            return GameProbe();

        if (command == "DUMP")
            return GameDump();

        if (command == "UNLOAD")
        {
            g_stop.store(true);
            return "OK BYE";
        }

        return "FAIL unknown-command";
    }

    // 一个客户端的完整会话; 客户端断开就返回
    void ServeSession(HANDLE pipe)
    {
        char  buffer[1024];
        DWORD read = 0;

        while (!g_stop.load() && ReadFile(pipe, buffer, sizeof(buffer) - 1, &read, nullptr) && read > 0)
        {
            buffer[read] = '\0';

            const std::string command  = Trim(buffer);
            const std::string response = HandleCommand(command);

            LogF("[pipe] < %s | > %s", command.c_str(), response.c_str());

            DWORD written = 0;
            if (!WriteFile(pipe, response.c_str(), static_cast<DWORD>(response.size()), &written, nullptr))
            {
                LogF("[pipe] 回包失败 err=%lu", GetLastError());
                return;
            }

            FlushFileBuffers(pipe);
        }
    }
}

void PipeServerRun()
{
    const std::string name = PipeName();
    LogF("[pipe] 服务端启动 %s", name.c_str());

    while (!g_stop.load())
    {
        const HANDLE pipe = CreateNamedPipeA(name.c_str(),
                                             PIPE_ACCESS_DUPLEX,
                                             PIPE_TYPE_MESSAGE | PIPE_READMODE_MESSAGE | PIPE_WAIT,
                                             1,      // 一次只服务一个启动器
                                             4096, 4096,
                                             0,
                                             nullptr); // 默认安全描述符 = 只有本用户/系统能连

        if (pipe == INVALID_HANDLE_VALUE)
        {
            LogF("[pipe] 创建管道失败 err=%lu, 1 秒后重试", GetLastError());
            Sleep(1000);
            continue;
        }

        const BOOL connected = ConnectNamedPipe(pipe, nullptr) ? TRUE : (GetLastError() == ERROR_PIPE_CONNECTED);

        if (connected)
        {
            LogF("[pipe] 启动器已连接");
            ServeSession(pipe);
            LogF("[pipe] 会话结束");
        }

        DisconnectNamedPipe(pipe);
        CloseHandle(pipe);
    }

    LogF("[pipe] 服务端退出");
}
