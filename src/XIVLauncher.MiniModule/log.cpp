// 日志: 注入模块出问题时这是唯一的现场, 所以每条都立刻 flush, 宁可慢
#include "MiniModule.h"

#include <cstdarg>
#include <cstdio>
#include <mutex>

namespace
{
    HANDLE      g_file = INVALID_HANDLE_VALUE;
    std::string g_path;
    std::mutex  g_mutex;

    std::string TimeStamp()
    {
        SYSTEMTIME st;
        GetLocalTime(&st);

        char buffer[32];
        _snprintf_s(buffer, sizeof(buffer), _TRUNCATE, "%02d:%02d:%02d.%03d",
                    st.wHour, st.wMinute, st.wSecond, st.wMilliseconds);

        return buffer;
    }
}

void LogInit()
{
    std::lock_guard<std::mutex> lock(g_mutex);

    if (g_file != INVALID_HANDLE_VALUE)
        return;

    wchar_t tempDir[MAX_PATH]{};
    if (GetTempPathW(MAX_PATH, tempDir) == 0)
        return;

    wchar_t fullPath[MAX_PATH]{};
    _snwprintf_s(fullPath, MAX_PATH, _TRUNCATE, L"%sminilauncher-module-%lu.log", tempDir, GetCurrentProcessId());

    g_file = CreateFileW(fullPath, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                         nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);

    // 路径本身也要能报给启动器, 转成 UTF-8 存下来
    char utf8[MAX_PATH * 3]{};
    WideCharToMultiByte(CP_UTF8, 0, fullPath, -1, utf8, sizeof(utf8), nullptr, nullptr);
    g_path = utf8;
}

void LogShutdown()
{
    std::lock_guard<std::mutex> lock(g_mutex);

    if (g_file == INVALID_HANDLE_VALUE)
        return;

    CloseHandle(g_file);
    g_file = INVALID_HANDLE_VALUE;
}

std::string LogFilePath()
{
    std::lock_guard<std::mutex> lock(g_mutex);
    return g_path;
}

void LogF(const char* format, ...)
{
    char body[1024];

    va_list args;
    va_start(args, format);
    _vsnprintf_s(body, sizeof(body), _TRUNCATE, format, args);
    va_end(args);

    char line[1280];
    const int length = _snprintf_s(line, sizeof(line), _TRUNCATE, "[%s][tid=%lu] %s\r\n",
                                   TimeStamp().c_str(), GetCurrentThreadId(), body);
    if (length <= 0)
        return;

    std::lock_guard<std::mutex> lock(g_mutex);

    if (g_file == INVALID_HANDLE_VALUE)
        return;

    DWORD written = 0;
    WriteFile(g_file, line, static_cast<DWORD>(length), &written, nullptr);
    FlushFileBuffers(g_file);
}
