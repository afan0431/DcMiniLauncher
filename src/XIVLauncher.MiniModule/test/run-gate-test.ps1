# MiniLauncherModule 离线冒烟: 不开游戏, 用假宿主 (testhost.exe) 把整条链子跑一遍
#
#   powershell -ExecutionPolicy Bypass -File src\XIVLauncher.MiniModule\test\run-gate-test.ps1
#
# 覆盖: 注入 → 管道通话 (VERSION/PING) → 没有主线程通道时 MAINTHREAD 回超时 → 自动选角各命令在非游戏进程里安全回 FAIL → 自我卸载 (UNLOAD)
# 不覆盖: 主线程派发（通道挂在游戏的 Framework::Tick 上, 假宿主没有）、与 bot/其他 hook 共存 —— 只有真机能答。

param([string]$DllPath)

$ErrorActionPreference = 'Stop'

$testDir   = Split-Path -Parent $MyInvocation.MyCommand.Path
$moduleDir = Split-Path -Parent $testDir
$objDir    = Join-Path $moduleDir 'obj'
# -DllPath 可指向别的产物（例如拿旧版验证下面的「取名不许卡住」断言确实会红）
$dllPath   = if ($DllPath) { $DllPath } else { Join-Path (Join-Path (Split-Path -Parent $moduleDir) 'bin\win-x64') 'MiniLauncherModule.dll' }
$hostExe   = Join-Path $objDir 'testhost.exe'

if (-not (Test-Path $dllPath)) { throw "模块还没编译: $dllPath（先跑 build.ps1）" }

# ---- 1. 编译假宿主 ----------------------------------------------------------
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vsPath  = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
$vcvars  = Join-Path $vsPath 'VC\Auxiliary\Build\vcvars64.bat'

$batPath = Join-Path $objDir 'build-testhost.cmd'
@"
@echo off
call "$vcvars" >nul
if errorlevel 1 exit /b 1
cd /d "$testDir"
cl.exe /nologo /std:c++17 /utf-8 /EHsc /W4 /O2 /MT /DUNICODE /D_UNICODE /Fo"$objDir/" /Fe"$hostExe" testhost.cpp /link user32.lib
"@ | Set-Content -Path $batPath -Encoding OEM

cmd.exe /c "`"$batPath`"" | Out-Null
if ($LASTEXITCODE -ne 0) { throw "假宿主编译失败 (exit $LASTEXITCODE)" }

# ---- 2. 注入用的 P/Invoke（与 MiniModuleInjector.cs 同一套调用) --------------
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class Injector
{
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr VirtualAllocEx(IntPtr p, IntPtr addr, UIntPtr size, uint type, uint protect);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool WriteProcessMemory(IntPtr p, IntPtr addr, byte[] buf, UIntPtr size, out UIntPtr written);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandle(string name);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    static extern IntPtr GetProcAddress(IntPtr m, string name);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr CreateRemoteThread(IntPtr p, IntPtr attrs, UIntPtr stack, IntPtr start, IntPtr param, uint flags, out uint tid);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr h);

    public static string Inject(int pid, string dllPath)
    {
        IntPtr proc = OpenProcess(0x0002 | 0x0400 | 0x0008 | 0x0020 | 0x0010, false, pid);
        if (proc == IntPtr.Zero) return "OpenProcess 失败 err=" + Marshal.GetLastWin32Error();

        byte[] bytes = Encoding.Unicode.GetBytes(dllPath + "\0");
        IntPtr remote = VirtualAllocEx(proc, IntPtr.Zero, (UIntPtr)bytes.Length, 0x1000 | 0x2000, 0x04);
        if (remote == IntPtr.Zero) return "VirtualAllocEx 失败 err=" + Marshal.GetLastWin32Error();

        UIntPtr written;
        if (!WriteProcessMemory(proc, remote, bytes, (UIntPtr)bytes.Length, out written))
            return "WriteProcessMemory 失败 err=" + Marshal.GetLastWin32Error();

        IntPtr load = GetProcAddress(GetModuleHandle("kernel32.dll"), "LoadLibraryW");
        uint tid;
        IntPtr thread = CreateRemoteThread(proc, IntPtr.Zero, UIntPtr.Zero, load, remote, 0, out tid);
        if (thread == IntPtr.Zero) return "CreateRemoteThread 失败 err=" + Marshal.GetLastWin32Error();

        uint wait = WaitForSingleObject(thread, 30000);
        CloseHandle(thread);
        CloseHandle(proc);

        if (wait != 0) return "等待远程线程失败 wait=" + wait;
        return null;
    }
}
'@

# 复刻 MinionLauncher_64 挂载前的句柄遍历: 复制目标进程的每个 File 句柄, 在工作线程里
# GetFileInformationByHandleEx(FileNameInfo), 只等 100 ms。它超时后留下的线程会写坏内存让启动器崩溃,
# 所以模块的管道在「客户端连着、模块正等下一条命令」时也必须立即答得出名字（见 pipe.cpp 文件头）。
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

public static class HandleNameProbe
{
    [DllImport("ntdll.dll")] static extern int NtQuerySystemInformation(int cls, IntPtr buf, int len, out int ret);
    [DllImport("ntdll.dll")] static extern int NtQueryObject(IntPtr h, int cls, IntPtr buf, int len, out int ret);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(int access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool DuplicateHandle(IntPtr sp, IntPtr sh, IntPtr tp, out IntPtr th, int access, bool inherit, int options);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetFileInformationByHandleEx(IntPtr h, int cls, IntPtr buf, int len);

    static string TypeName(IntPtr h)
    {
        IntPtr buf = Marshal.AllocHGlobal(0x1000);
        try
        {
            int ret;
            if (NtQueryObject(h, 2, buf, 0x1000, out ret) != 0) return "";
            return Marshal.PtrToStringUni(Marshal.ReadIntPtr(buf, 8), Marshal.ReadInt16(buf) / 2);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    // 返回取名超过 timeoutMs 的 File 句柄（十六进制值）; 空表 = 没有会卡住的
    public static List<string> SlowFileHandles(int pid, int timeoutMs)
    {
        var slow = new List<string>();
        IntPtr process = OpenProcess(0x0440, false, pid); // PROCESS_QUERY_INFORMATION | PROCESS_DUP_HANDLE, 与 MinionLauncher 相同
        if (process == IntPtr.Zero) throw new InvalidOperationException("OpenProcess err=" + Marshal.GetLastWin32Error());

        int size = 0x100000, ret;
        IntPtr info;
        while (true)
        {
            info = Marshal.AllocHGlobal(size);
            int status = NtQuerySystemInformation(0x40, info, size, out ret); // SystemExtendedHandleInformation
            if (status == unchecked((int)0xC0000004)) { Marshal.FreeHGlobal(info); size *= 2; continue; }
            if (status != 0) throw new InvalidOperationException("NtQuerySystemInformation 0x" + status.ToString("X"));
            break;
        }

        long count = Marshal.ReadInt64(info);
        for (long i = 0; i < count; i++)
        {
            IntPtr entry = new IntPtr(info.ToInt64() + 16 + i * 40);
            if (Marshal.ReadInt64(entry, 8) != pid) continue;

            IntPtr value = Marshal.ReadIntPtr(entry, 16), copy;
            if (!DuplicateHandle(process, value, GetCurrentProcess(), out copy, 0, false, 2)) continue;
            if (TypeName(copy) != "File") { CloseHandle(copy); continue; }

            IntPtr target = copy;
            var worker = new Thread(() =>
            {
                IntPtr buf = Marshal.AllocHGlobal(0x1000);
                GetFileInformationByHandleEx(target, 2, buf, 0x1000); // FileNameInfo
                Marshal.FreeHGlobal(buf);
            });
            worker.IsBackground = true;
            worker.Start();

            if (worker.Join(timeoutMs)) CloseHandle(copy);
            else slow.Add("0x" + value.ToInt64().ToString("X")); // 卡住的副本不关: 关了正被阻塞的句柄正是要避免的事
        }

        Marshal.FreeHGlobal(info);
        CloseHandle(process);
        return slow;
    }
}
'@

function Send-ModuleCommand
{
    param([System.IO.Pipes.NamedPipeClientStream]$Pipe, [string]$Command)

    $payload = [Text.Encoding]::UTF8.GetBytes($Command)
    $Pipe.Write($payload, 0, $payload.Length)
    $Pipe.Flush()

    $buffer = New-Object byte[] 8192
    $read   = $Pipe.Read($buffer, 0, $buffer.Length)
    return [Text.Encoding]::UTF8.GetString($buffer, 0, $read)
}

# ---- 3. 起假宿主 → 注入 → 通话 ----------------------------------------------
$failures = @()
$process  = Start-Process -FilePath $hostExe -PassThru
Start-Sleep -Milliseconds 500

try
{
    Write-Host "testhost pid=$($process.Id)"

    $error1 = [Injector]::Inject($process.Id, $dllPath)
    if ($error1) { throw "注入失败: $error1" }

    $process.Refresh()
    $loaded = @($process.Modules | Where-Object { $_.ModuleName -eq 'MiniLauncherModule.dll' }).Count
    if ($loaded -eq 0) { throw '注入后模块列表里没有 MiniLauncherModule.dll' }
    Write-Host '[OK] 模块已出现在目标进程的模块列表里'

    $pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', "minilauncher-$($process.Id)", [System.IO.Pipes.PipeDirection]::InOut)
    $pipe.Connect(10000)
    $pipe.ReadMode = [System.IO.Pipes.PipeTransmissionMode]::Message

    try
    {
        $version = Send-ModuleCommand -Pipe $pipe -Command 'VERSION'
        Write-Host "VERSION → $version"
        if (-not $version.StartsWith('OK')) { $failures += 'VERSION 没回 OK' }

        $ping = Send-ModuleCommand -Pipe $pipe -Command 'PING'
        Write-Host "PING → $ping"
        if ($ping -ne 'OK PONG') { $failures += 'PING 没回 OK PONG' }

        # 此刻客户端连着、模块的管道线程正等下一条命令 —— 正是 MinionLauncher 撞上就会崩的状态
        $slow = [HandleNameProbe]::SlowFileHandles($process.Id, 100)
        if ($slow.Count -gt 0) { $failures += "宿主里有 File 句柄取名超过 100 ms（MinionLauncher 挂载会崩）: $($slow -join ', ')" }
        else { Write-Host '[OK] 会话中按句柄取名都在 100 ms 内返回' }

        # 主线程通道挂在游戏的 Framework::Tick 上, 要先解析出游戏的特征码; 假宿主里解析不出来,
        # 所以这里只验它老实回超时而不是卡死或把宿主带走。真正的主线程派发只有真机能验
        $mainThread = Send-ModuleCommand -Pipe $pipe -Command 'MAINTHREAD'
        Write-Host "MAINTHREAD → $mainThread"
        if ($mainThread -ne 'FAIL mainthread-timeout') { $failures += "MAINTHREAD 的回应不对: $mainThread" }

        $unknown = Send-ModuleCommand -Pipe $pipe -Command 'NOSUCHCOMMAND'
        if ($unknown -ne 'FAIL unknown-command') { $failures += "未知命令的回应不对: $unknown" }

        if ($version -notmatch 'version=0\.6\.1 ') { $failures += "模块版本不是 0.6.1: $version" }

        # 自动选角的命令: 假宿主不是游戏, 特征码一条都命中不了, 每条都必须安全地回 FAIL, 宿主不能挂
        $expected = [ordered]@{
            'LOBBYSTATE'            = 'FAIL sigscan-failed'
            'CHARAS'                = 'FAIL sigscan-failed'
            'WHOAMI'                = 'FAIL sigscan-failed'
            'FOCUSCHARA 测试角色'   = 'FAIL sigscan-failed'
            'SELECTCHARA 测试角色'  = 'FAIL sigscan-failed'
            'SELECTCHARA 123456'    = 'FAIL sigscan-failed'
            'ENTERCHARA 测试角色'   = 'FAIL sigscan-failed'
            'ENTERCHARA DIRECT 测试角色' = 'FAIL not-implemented'
            'DIALOG YES 123456'     = 'FAIL sigscan-failed'
            'DIALOG NO'             = 'FAIL sigscan-failed'
            'DIALOG OK'             = 'FAIL sigscan-failed'
            'DIALOG YES'            = 'FAIL usage: DIALOG <YES <contentId>|NO|OK>'
            'DIALOG YES 测试角色'   = 'FAIL usage: DIALOG <YES <contentId>|NO|OK>'
            'DIALOG NO 123456'      = 'FAIL usage: DIALOG <YES <contentId>|NO|OK>'
            'DIALOG MAYBE'          = 'FAIL usage: DIALOG <YES <contentId>|NO|OK>'
        }

        foreach ($command in $expected.Keys)
        {
            $answer = Send-ModuleCommand -Pipe $pipe -Command $command
            Write-Host "$command → $answer"
            if ($answer -ne $expected[$command]) { $failures += "$command 的回应不对: $answer（应为 $($expected[$command])）" }
        }

        $unload = Send-ModuleCommand -Pipe $pipe -Command 'UNLOAD'
        Write-Host "UNLOAD → $unload"
    }
    finally { $pipe.Dispose() }

    Start-Sleep -Milliseconds 1000
    $process.Refresh()
    $stillLoaded = @($process.Modules | Where-Object { $_.ModuleName -eq 'MiniLauncherModule.dll' }).Count
    if ($stillLoaded -ne 0) { $failures += '收到 UNLOAD 后模块没有从进程里卸载' }
    else { Write-Host '[OK] 模块已自我卸载' }

    if ($process.HasExited) { $failures += "假宿主进程挂了 (exit=$($process.ExitCode))" }
    else { Write-Host '[OK] 宿主进程仍然活着' }
}
finally
{
    if (-not $process.HasExited) { $process.Kill() }
}

# ---- 4. 结论 ----------------------------------------------------------------
$logPath = Join-Path $env:TEMP "minilauncher-module-$($process.Id).log"
if (Test-Path $logPath)
{
    Write-Host "`n--- 模块日志 $logPath ---"
    # 模块日志是 UTF-8, 不指定编码 PS 5.1 会按 ANSI 读成乱码
    Get-Content $logPath -Encoding UTF8 | ForEach-Object { Write-Host "  $_" }
}

if ($failures.Count -gt 0)
{
    Write-Host "`n失败:" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    exit 1
}

Write-Host "`n全部通过" -ForegroundColor Green
