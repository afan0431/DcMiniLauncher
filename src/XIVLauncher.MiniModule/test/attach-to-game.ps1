# 把模块挂到一个已经在跑的游戏进程上, 跑一遍生死闸自检, 然后自我卸载
#
#   powershell -ExecutionPolicy Bypass -File src\XIVLauncher.MiniModule\test\attach-to-game.ps1 [-ProcessId <pid>] [-KeepLoaded]
#
# 不给 -ProcessId 就自动找唯一的 ffxiv_dx11。默认跑完就 UNLOAD, 把游戏还原成没被碰过的样子。
# 这条路只做两件事: 子类化窗口 WndProc + 往窗口线程 PostMessage —— 不写游戏任何内存、不装任何 hook。

param(
    [int]$ProcessId = 0,
    [switch]$KeepLoaded
)

$ErrorActionPreference = 'Stop'

$testDir   = Split-Path -Parent $MyInvocation.MyCommand.Path
$moduleDir = Split-Path -Parent $testDir
$dllPath   = Join-Path (Join-Path (Split-Path -Parent $moduleDir) 'bin\win-x64') 'MiniLauncherModule.dll'

if (-not (Test-Path $dllPath)) { throw "模块还没编译: $dllPath（先跑 build.ps1）" }

if ($ProcessId -eq 0)
{
    $candidates = @(Get-Process ffxiv_dx11 -ErrorAction SilentlyContinue)
    if ($candidates.Count -ne 1) { throw "自动定位失败: 找到 $($candidates.Count) 个 ffxiv_dx11, 请用 -ProcessId 明确指定" }
    $ProcessId = $candidates[0].Id
}

$process = Get-Process -Id $ProcessId
Write-Host "目标: $($process.ProcessName) pid=$ProcessId 窗口标题=$($process.MainWindowTitle)"

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class GameInjector
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

    # 消息模式管道: 缓冲区比整条回应小会直接读失败, PROBE/DUMP 的回应上千字节
    $buffer = New-Object byte[] 8192
    $read   = $Pipe.Read($buffer, 0, $buffer.Length)
    return [Text.Encoding]::UTF8.GetString($buffer, 0, $read)
}

$already = @($process.Modules | Where-Object { $_.ModuleName -eq 'MiniLauncherModule.dll' }).Count

if ($already -eq 0)
{
    $failure = [GameInjector]::Inject($ProcessId, $dllPath)
    if ($failure) { throw "注入失败: $failure" }

    $process.Refresh()
    if (@($process.Modules | Where-Object { $_.ModuleName -eq 'MiniLauncherModule.dll' }).Count -eq 0)
    {
        throw '远程线程跑完了, 但模块列表里没有 DLL（LoadLibraryW 失败）'
    }
    Write-Host '[OK] 已注入'
}
else { Write-Host '[跳过] 模块已经在进程里了' }

$pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', "minilauncher-$ProcessId", [System.IO.Pipes.PipeDirection]::InOut)
$pipe.Connect(10000)
$pipe.ReadMode = [System.IO.Pipes.PipeTransmissionMode]::Message

try
{
    Write-Host "VERSION → $(Send-ModuleCommand -Pipe $pipe -Command 'VERSION')"
    Write-Host "PING    → $(Send-ModuleCommand -Pipe $pipe -Command 'PING')"

    # 生死闸: same=1 表示我们能在游戏主线程上执行代码 —— 换服那几个函数必须跑在这里
    $mainThread = Send-ModuleCommand -Pipe $pipe -Command 'MAINTHREAD'
    Write-Host "MAINTHREAD → $mainThread"

    if ($mainThread -match 'same=1') { Write-Host '[生死闸] 通过: 窗口线程 = 进程主线程' -ForegroundColor Green }
    else { Write-Host '[生死闸] 窗口线程不是主线程, 换服要改走 Framework::Tick hook' -ForegroundColor Yellow }

    # 只读探针: 顺着 Framework 一路读到大厅主机名。读出来的主机名能和当前所连大区对上 = 偏移是对的
    $probe = Send-ModuleCommand -Pipe $pipe -Command 'PROBE'
    Write-Host "`nPROBE → $probe`n"

    $dump = Send-ModuleCommand -Pipe $pipe -Command 'DUMP'
    Write-Host "DUMP  → $dump`n"

    if (-not $KeepLoaded) { Write-Host "UNLOAD  → $(Send-ModuleCommand -Pipe $pipe -Command 'UNLOAD')" }
}
finally { $pipe.Dispose() }

Start-Sleep -Milliseconds 800
$process.Refresh()

if ($process.HasExited) { Write-Host '[!!] 游戏进程已经退出' -ForegroundColor Red }
else { Write-Host "[OK] 游戏进程仍然活着 (pid=$ProcessId)" -ForegroundColor Green }

$logPath = Join-Path $env:TEMP "minilauncher-module-$ProcessId.log"
if (Test-Path $logPath)
{
    Write-Host "`n--- 模块日志 $logPath ---"
    Get-Content $logPath -Encoding UTF8 | ForEach-Object { Write-Host "  $_" }
}
