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
#include <vector>

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

    // 命令按空格切; SETSID 的参数是登录票据, 所以日志里只打命令名不打参数
    std::vector<std::string> Split(const std::string& text)
    {
        std::vector<std::string> parts;
        size_t                   cursor = 0;

        while (cursor < text.size())
        {
            const auto begin = text.find_first_not_of(' ', cursor);
            if (begin == std::string::npos)
                break;

            const auto end = text.find(' ', begin);
            parts.push_back(text.substr(begin, end == std::string::npos ? std::string::npos : end - begin));

            if (end == std::string::npos)
                break;

            cursor = end + 1;
        }

        return parts;
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

        // ---- 换服原语 ----------------------------------------------------
        if (command == "WHERE")
            return GameWhere();

        if (command == "WHOLIST")
            return GameWhoList();

        if (command == "MENUSTATUS")
            return ContextMenuStatus();

        if (command == "SKIPMOVIE")
            return GameSkipMovie();

        if (command == "LOGOUT")
            return GameLogout(false); // /logout 文本命令 + 确认框

        if (command == "LOGOUT DIRECT")
            return GameLogout(true);  // 直接调 AgentLobby::HandleLogout

        if (command == "RETURNTITLE")
            return GameReturnToTitle();

        if (command == "RELEASE")
            return GameReleaseLobbyContext();

        if (command == "TITLEREADY")
            return GameTitleReady();

        if (command == "ADDONS")
            return GameListAddons();

        if (command == "LOGIN")
            return GameLogin();

        if (command == "KEEPALIVE ON")
            return GameKeepAlive(true);

        if (command == "KEEPALIVE OFF")
            return GameKeepAlive(false);

        // 角色名可能带空格（外服那种「名 姓」）, 所以整行剩下的都算参数, 不走下面的 Split
        if (command.rfind("FOCUSCHARA ", 0) == 0)
            return GameFocusCharacter(command.substr(11));

        const auto parts = Split(command);

        if (!parts.empty() && parts[0] == "SETHOSTS")
        {
            if (parts.size() != 4)
                return "FAIL usage:SETHOSTS <lobbyHost> <saveDataHost> <gmHost>";

            return GameSetHosts(parts[1], parts[2], parts[3]);
        }

        if (!parts.empty() && parts[0] == "SETSID")
        {
            if (parts.size() != 2)
                return "FAIL usage:SETSID <sid>";

            return GameSetSid(parts[1]);
        }

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

            // SETSID 的参数是登录票据 —— 只记命令名
            const bool secret = command.rfind("SETSID", 0) == 0;
            // WHOLIST 的正文是一整张角色表, 日志里只留第一行摘要（game.cpp 已记过）
            const auto shown = response.substr(0, response.find('\n'));
            LogF("[pipe] < %s | > %s", secret ? "SETSID <已隐去>" : command.c_str(), shown.c_str());

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
