# MiniLauncherModule 离线冒烟: 不开游戏, 用假宿主 (testhost.exe) 把整条链子跑一遍
#
#   powershell -ExecutionPolicy Bypass -File src\XIVLauncher.MiniModule\test\run-gate-test.ps1
#
# 覆盖: 注入 → 管道通话 (VERSION/PING) → 主线程派发 (MAINTHREAD) → 自我卸载 (UNLOAD)
# 不覆盖: 游戏自己的窗口线程是不是主线程、与 bot/其他 hook 共存 —— 那两条只有真机能答。

$ErrorActionPreference = 'Stop'

$testDir   = Split-Path -Parent $MyInvocation.MyCommand.Path
$moduleDir = Split-Path -Parent $testDir
$objDir    = Join-Path $moduleDir 'obj'
$dllPath   = Join-Path (Join-Path (Split-Path -Parent $moduleDir) 'bin\win-x64') 'MiniLauncherModule.dll'
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

function Send-ModuleCommand
{
    param([System.IO.Pipes.NamedPipeClientStream]$Pipe, [string]$Command)

    $payload = [Text.Encoding]::UTF8.GetBytes($Command)
    $Pipe.Write($payload, 0, $payload.Length)
    $Pipe.Flush()

    $buffer = New-Object byte[] 1024
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

        # 假宿主的主线程就是窗口线程, 所以这里必须 same=1; 真机上这一位才是未知数
        $mainThread = Send-ModuleCommand -Pipe $pipe -Command 'MAINTHREAD'
        Write-Host "MAINTHREAD → $mainThread"
        if ($mainThread -notmatch 'same=1') { $failures += "MAINTHREAD 没能在主线程上跑: $mainThread" }

        $unknown = Send-ModuleCommand -Pipe $pipe -Command 'NOSUCHCOMMAND'
        if ($unknown -ne 'FAIL unknown-command') { $failures += "未知命令的回应不对: $unknown" }

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
