// MiniLauncher 游戏内模块（F4）· 公共声明
//
// 硬约束: 不依赖 Dalamud、不引 CLR。纯 native、只做「必须在进程内做」的事。
// 本轮（生死闸）只验证两件事: 能被注入进游戏、能在游戏主线程上执行代码。
#pragma once

#define WIN32_LEAN_AND_MEAN
#include <windows.h>

#include <functional>
#include <string>

// 模块版本, 通过 pipe 的 VERSION 命令回给启动器, 便于确认注进去的是哪一版
#define MINIMODULE_VERSION "0.1.0-gate"

// ---- 日志 (log.cpp) ----------------------------------------------------------
// 日志落在 %TEMP%\minilauncher-module-<pid>.log —— 游戏目录不保证可写, 且这里天然按 PID 分开
void        LogInit();
void        LogShutdown();
void        LogF(const char* format, ...);
std::string LogFilePath();

// ---- 主线程执行 (mainthread.cpp) --------------------------------------------
// 通过给游戏窗口挂 WndProc 子类化 + PostMessage 派发, 换取「在游戏主线程上跑一段代码」。
// 之所以不先上 sigscan + MinHook 勾 Framework::Tick: 那条路要养国服偏移/特征码, 是 F4 后面
// 真正换服时才必须的脆弱面; 生死闸这一步只想证明「注得进 + 跑在主线程」, 用零偏移的办法证。
bool MainThreadInstall();   // 找游戏窗口并子类化; 失败返回 false
bool MainThreadUninstall(); // 还原 WndProc; 还原不了（别人又套了一层）返回 false, 此时不可卸载本模块

// 把 job 派到游戏窗口线程上执行, 阻塞等待其完成; 超时返回 false
bool MainThreadRun(const std::function<void()>& job, DWORD timeoutMs);

HWND  MainThreadWindow();       // 已定位到的游戏窗口
DWORD MainThreadWindowThread(); // 该窗口所属线程 id
DWORD ProcessMainThreadId();    // 本进程创建时间最早的线程 = 主线程

// ---- 特征码扫描 (sigscan.cpp) -----------------------------------------------
// 复刻 Dalamud SigScanner 的语义, 好让 ClientStructs / DCTraveler 的特征码原样可用
uintptr_t ModuleBase();
uintptr_t ScanText(const char* signature);                     // 命中 E8/E9 时自动跟进目标
uintptr_t ScanStaticAddress(const char* signature, int offset); // RIP 相对寻址的静态地址

// ---- 游戏结构体 (game.cpp) --------------------------------------------------
bool        GameResolve(); // 解析特征码, 只做一次
std::string GameProbe();   // 只读自检: 把关键指针和当前大厅主机名读出来核对偏移
std::string GameDump();    // 只读诊断: 摊开 Utf8String 头部 + 在 NetworkModule 里反查主机名

// ---- 命名管道服务端 (pipe.cpp) ----------------------------------------------
// \\.\pipe\minilauncher-<pid> —— 按 PID 分开, 天然满足「多开不串」
// 阻塞运行, 收到 UNLOAD/进程退出才返回
void PipeServerRun();
