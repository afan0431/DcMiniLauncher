# MiniLauncherModule 离线冒烟: 不开游戏, 用假宿主 (testhost.exe) 把整条链子跑一遍
#
#   powershell -ExecutionPolicy Bypass -File src\XIVLauncher.MiniModule\test\run-gate-test.ps1
#
# 覆盖: 注入 → 管道通话 (VERSION/PING) → 没有主线程通道时 MAINTHREAD 回超时 → 自动选角各命令在非游戏进程里安全回 FAIL → 自我卸载 (UNLOAD)
#       → 注入前写的只读模式文件被模块读到（VERSION 带 mode=readonly）
#       → 共用游戏配置找不到 FileInterface::OpenFile 时不挂钩, CHARCONFIG 与心跳 modules\<pid>.json 写明原因
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

# 第二个假宿主冒充 FileInterface::OpenFile（testhost.cpp 的 FAKE_OPENFILE）, 给共用游戏配置的挂钩跑整条链子
$hookHostExe    = Join-Path $objDir 'testhost-openfile.exe'
$hookHostObjDir = Join-Path $objDir 'testhost-openfile'
if (-not (Test-Path $hookHostObjDir)) { New-Item -ItemType Directory -Force -Path $hookHostObjDir | Out-Null }

$batPath = Join-Path $objDir 'build-testhost.cmd'
@"
@echo off
call "$vcvars" >nul
if errorlevel 1 exit /b 1
cd /d "$testDir"
cl.exe /nologo /std:c++17 /utf-8 /EHsc /W4 /O2 /MT /DUNICODE /D_UNICODE /Fo"$objDir/" /Fe"$hostExe" testhost.cpp /link user32.lib
if errorlevel 1 exit /b 1
ml64.exe /nologo /c /Fo"$hookHostObjDir\testhost-callsites.obj" testhost-callsites.asm
if errorlevel 1 exit /b 1
cl.exe /nologo /std:c++17 /utf-8 /EHsc /W4 /O2 /MT /DUNICODE /D_UNICODE /DFAKE_OPENFILE /Fo"$hookHostObjDir/" /Fe"$hookHostExe" testhost.cpp "$hookHostObjDir\testhost-callsites.obj" /link user32.lib
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

    # 与登录器只为读等级而注入时一样: 注入前按 pid 写只读模式文件
    $modeFile = Join-Path $env:ProgramData "DcMiniLauncher\module-$($process.Id).mode"
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $modeFile) | Out-Null
    [System.IO.File]::WriteAllText($modeFile, 'readonly')

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

        if ($version -notmatch 'version=0\.10\.0 ') { $failures += "模块版本不是 0.10.0: $version" }
        if ($version -notmatch 'mode=readonly ') { $failures += "模块没有读到只读模式文件: $version" }

        # 自动选角的命令: 假宿主不是游戏, 特征码一条都命中不了, 每条都必须安全地回 FAIL, 宿主不能挂
        $expected = [ordered]@{
            'LOBBYSTATE'            = 'FAIL sigscan-failed'
            'CHARAS'                = 'FAIL sigscan-failed'
            'WHOAMI'                = 'FAIL sigscan-failed'
            'LEVELS'                = 'FAIL sigscan-failed'
            'DUTY'                  = 'FAIL sigscan-failed'
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

        # 共用游戏配置: 假宿主里找不到 FileInterface::OpenFile, 不挂钩, 状态与心跳都写明原因
        $charConfig = Send-ModuleCommand -Pipe $pipe -Command 'CHARCONFIG'
        Write-Host "CHARCONFIG → $charConfig"
        if (-not $charConfig.StartsWith('OK hooked=0 ')) { $failures += "CHARCONFIG 应回 OK hooked=0: $charConfig" }
        if ($charConfig -notmatch 'hookError=找不到 FileInterface::OpenFile') { $failures += "CHARCONFIG 没写明挂不上的原因: $charConfig" }

        $heartbeatPath = Join-Path $env:LOCALAPPDATA "Cat\char-config\modules\$($process.Id).json"
        if (-not (Test-Path $heartbeatPath)) { $failures += "没有心跳文件 $heartbeatPath" }
        else
        {
            $heartbeat = [System.IO.File]::ReadAllText($heartbeatPath, [Text.Encoding]::UTF8) | ConvertFrom-Json
            Write-Host "心跳 → hooked=$($heartbeat.hooked) lastError=$($heartbeat.lastError)"
            if ($heartbeat.hooked -ne $false) { $failures += "心跳 hooked 应为 false: $($heartbeat.hooked)" }
            if ($heartbeat.pid -ne $process.Id) { $failures += "心跳 pid 不对: $($heartbeat.pid)" }
            if ($heartbeat.moduleVersion -ne '0.10.0') { $failures += "心跳 moduleVersion 不对: $($heartbeat.moduleVersion)" }
            if ("$($heartbeat.lastError)" -notmatch '找不到 FileInterface::OpenFile') { $failures += "心跳 lastError 没写明原因: $($heartbeat.lastError)" }
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
    if ($modeFile -and (Test-Path $modeFile)) { Remove-Item $modeFile -Force }
    if ($heartbeatPath -and (Test-Path $heartbeatPath)) { Remove-Item $heartbeatPath -Force }
}

# ---- 4. 共用游戏配置: 冒充 OpenFile 的假宿主 -----------------------------------
# 覆盖: 主备特征码找到假宿主里的调用点 → MinHook 挂上 → 读之前放入（角色目录不存在先建）→ 这一套没有的文件不放入
#       → 保存内容与登录时相同不发布 → 改了才发布（outbox、state.json pending、current）→ 没有文件的号第一次保存也发布
#       → UNLOAD 时卸下钩子、模块卸载、宿主照常打开文件
# 本机目录用环境变量 CAT_CHARCONFIG_ROOT 指到临时目录, 不碰 %LOCALAPPDATA%\Cat\char-config。

function Get-Sha256Hex([byte[]]$Bytes)
{
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return (($sha.ComputeHash($Bytes) | ForEach-Object { $_.ToString('x2') }) -join '') }
    finally { $sha.Dispose() }
}

function Get-OutboxCount([string]$SetDir)
{
    $outbox = Join-Path $SetDir 'outbox'
    if (-not (Test-Path $outbox)) { return 0 }
    return @(Get-ChildItem -Path $outbox -Filter '*.json').Count
}

# 等到条件成立（工作线程每 100 ms 处理一次）; 超时返回 $false
function Wait-Until([scriptblock]$Condition, [int]$TimeoutMs = 3000)
{
    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMs)
    while ([DateTime]::UtcNow -lt $deadline)
    {
        if (& $Condition) { return $true }
        Start-Sleep -Milliseconds 100
    }
    return [bool](& $Condition)
}

$utf8NoBom = New-Object Text.UTF8Encoding($false)
$charRoot  = Join-Path $env:TEMP ('charconfig-gate-' + [guid]::NewGuid().ToString('N'))
$setId     = 'gate'
$cid       = '00400012DC10855C'
$charDir   = Join-Path $charRoot "game\FFXIV_CHR$cid"
$setDir    = Join-Path $charRoot "sets\$setId"
New-Item -ItemType Directory -Force -Path (Join-Path $setDir 'current') | Out-Null

# 这一套只有 MACRO.DAT（第 3 版）
$macroLatest = New-Object byte[] 286752
$macroLatest[0] = 1; $macroLatest[1000] = 7
[IO.File]::WriteAllBytes((Join-Path $setDir 'current\MACRO.DAT'), $macroLatest)
$macroSha = Get-Sha256Hex $macroLatest
[IO.File]::WriteAllText((Join-Path $setDir 'state.json'),
    "{`"schema`":1,`"files`":{`"MACRO.DAT`":{`"version`":3,`"sha256`":`"$macroSha`",`"size`":286752,`"pending`":false,`"source`":{`"workstation`":null,`"cid`":null,`"name`":null,`"at`":null}}}}",
    $utf8NoBom)
[IO.File]::WriteAllText((Join-Path $charRoot 'machine.json'),
    "{`"schema`":1,`"enabled`":true,`"blockedReason`":null,`"setId`":`"$setId`",`"setName`":`"gate`",`"categories`":[`"hotbar`",`"macro`",`"keybind`",`"logFilter`",`"character`",`"keyboard`",`"gamepad`",`"hud`",`"cards`"],`"updatedAt`":`"2026-10-24T00:00:00Z`"}",
    $utf8NoBom)

$startInfo = New-Object Diagnostics.ProcessStartInfo $hookHostExe
$startInfo.UseShellExecute        = $false
$startInfo.RedirectStandardInput  = $true
$startInfo.RedirectStandardOutput = $true
$startInfo.CreateNoWindow         = $true
$startInfo.EnvironmentVariables['CAT_CHARCONFIG_ROOT'] = $charRoot

$hookHost   = [Diagnostics.Process]::Start($startInfo)
$hookHostId = $hookHost.Id
$exitHostId = $null

function Send-HostCommand([string]$Line)
{
    $hookHost.StandardInput.WriteLine($Line)
    $hookHost.StandardInput.Flush()

    $task = $hookHost.StandardOutput.ReadLineAsync()
    if (-not $task.Wait(15000)) { throw "假宿主 15 秒没回应: $Line" }
    return $task.Result
}

Start-Sleep -Milliseconds 500

try
{
    Write-Host "`ntesthost-openfile pid=$($hookHost.Id)"

    $hookModeFile = Join-Path $env:ProgramData "DcMiniLauncher\module-$($hookHost.Id).mode"
    if (Test-Path $hookModeFile) { Remove-Item $hookModeFile -Force }

    $error2 = [Injector]::Inject($hookHost.Id, $dllPath)
    if ($error2) { throw "注入失败: $error2" }

    $hookPipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', "minilauncher-$($hookHost.Id)", [System.IO.Pipes.PipeDirection]::InOut)
    $hookPipe.Connect(10000)
    $hookPipe.ReadMode = [System.IO.Pipes.PipeTransmissionMode]::Message

    try
    {
        $status = Send-ModuleCommand -Pipe $hookPipe -Command 'CHARCONFIG'
        Write-Host "CHARCONFIG → $(($status -split "`n")[0])"
        if (-not $status.StartsWith('OK hooked=1 ')) { $failures += "冒充 OpenFile 的假宿主里没挂上: $status" }
        if ($status -notmatch 'enabled=1' -or $status -notmatch "set=$setId") { $failures += "没读到 machine.json: $status" }

        # 读之前放入: 角色目录还不存在
        $macroPath = Join-Path $charDir 'MACRO.DAT'
        $answer = Send-HostCommand "READ $macroPath"
        if ($answer -ne 'OK 1') { $failures += "READ MACRO.DAT 应打开成功（先放入）: $answer" }
        if (-not (Test-Path $macroPath)) { $failures += '读之前没有放入 MACRO.DAT' }
        elseif ((Get-Sha256Hex ([IO.File]::ReadAllBytes($macroPath))) -ne $macroSha) { $failures += '放入的 MACRO.DAT 不是这一套的最新版' }
        else { Write-Host '[OK] 读之前放入了这一套的 MACRO.DAT' }

        # 这一套没有 KEYBIND.DAT: 不放入, 游戏照常读不到
        $keybindPath = Join-Path $charDir 'KEYBIND.DAT'
        $answer = Send-HostCommand "READ $keybindPath"
        if ($answer -ne 'OK 0') { $failures += "READ KEYBIND.DAT 应打开失败（这一套没有）: $answer" }
        if (Test-Path $keybindPath) { $failures += '这一套没有 KEYBIND.DAT 却放入了' }

        # 保存内容与登录时相同: 不发布
        $sourcePath = Join-Path $charRoot 'source.dat'
        [IO.File]::WriteAllBytes($sourcePath, $macroLatest)
        $answer = Send-HostCommand "SAVE $macroPath|$sourcePath"
        if ($answer -ne 'OK') { $failures += "SAVE MACRO.DAT（未改）失败: $answer" }
        Start-Sleep -Milliseconds 800
        if ((Get-OutboxCount $setDir) -ne 0) { $failures += '保存内容与登录时相同却发布了' }
        else { Write-Host '[OK] 没改的保存不发布' }

        # 改了才发布
        $macroEdited = [byte[]]$macroLatest.Clone()
        $macroEdited[5] = 9
        [IO.File]::WriteAllBytes($sourcePath, $macroEdited)
        $answer = Send-HostCommand "SAVE $macroPath|$sourcePath"
        if ($answer -ne 'OK') { $failures += "SAVE MACRO.DAT（改过）失败: $answer" }
        if (-not (Wait-Until { (Get-OutboxCount $setDir) -eq 1 })) { $failures += "改过的 MACRO.DAT 没发布（outbox $(Get-OutboxCount $setDir) 项）" }
        else
        {
            $state = [IO.File]::ReadAllText((Join-Path $setDir 'state.json'), [Text.Encoding]::UTF8) | ConvertFrom-Json
            $entry = $state.files.'MACRO.DAT'
            if ($entry.pending -ne $true) { $failures += "发布后 state.json 的 MACRO.DAT 应 pending: $($entry | ConvertTo-Json -Compress)" }
            if ($entry.version -ne 3) { $failures += "本机发布不应改 version: $($entry.version)" }
            if ($entry.sha256 -ne (Get-Sha256Hex $macroEdited)) { $failures += 'state.json 的 MACRO.DAT 哈希不是改过的内容' }
            if ((Get-Sha256Hex ([IO.File]::ReadAllBytes((Join-Path $setDir 'current\MACRO.DAT')))) -ne (Get-Sha256Hex $macroEdited)) { $failures += 'current\MACRO.DAT 不是改过的内容' }
            Write-Host '[OK] 改过的保存发布到 outbox、state.json、current'

            # 假宿主里没有选角列表和 PlayerState, 查不到角色名: outbox 的 charName 与 state 的 source.name 都是 null
            $outboxItem = Get-ChildItem -Path (Join-Path $setDir 'outbox') -Filter '*.json' | Select-Object -First 1
            $outboxJson = [IO.File]::ReadAllText($outboxItem.FullName, [Text.Encoding]::UTF8) | ConvertFrom-Json
            if ($null -ne $outboxJson.charName) { $failures += "查不到角色名时 outbox 的 charName 应为 null: $($outboxJson.charName)" }
            if ($null -ne $entry.source.name) { $failures += "查不到角色名时 state.json 的 source.name 应为 null: $($entry.source.name)" }
        }

        # 读时这个号没有 KEYBIND.DAT（基线为空）: 第一次保存就发布
        $keybind = New-Object byte[] 24608
        $keybind[0] = 3
        [IO.File]::WriteAllBytes($sourcePath, $keybind)
        $answer = Send-HostCommand "SAVE $keybindPath|$sourcePath"
        if ($answer -ne 'OK') { $failures += "SAVE KEYBIND.DAT 失败: $answer" }
        if (-not (Wait-Until { (Get-OutboxCount $setDir) -eq 2 })) { $failures += "新建的 KEYBIND.DAT 没发布（outbox $(Get-OutboxCount $setDir) 项）" }
        else { Write-Host '[OK] 原来没有的文件第一次保存就发布' }

        $status = Send-ModuleCommand -Pipe $hookPipe -Command 'CHARCONFIG'
        Write-Host "CHARCONFIG →`n$status"
        foreach ($expected in @('放入 MACRO.DAT', '丢弃 MACRO.DAT', '发布 MACRO.DAT', '发布 KEYBIND.DAT'))
        {
            if ($status -notmatch [regex]::Escape($expected)) { $failures += "CHARCONFIG 最近动作里没有「$expected」" }
        }

        $unload = Send-ModuleCommand -Pipe $hookPipe -Command 'UNLOAD'
        Write-Host "UNLOAD → $unload"
    }
    finally { $hookPipe.Dispose() }

    Start-Sleep -Milliseconds 1500
    $hookHost.Refresh()
    if (@($hookHost.Modules | Where-Object { $_.ModuleName -eq 'MiniLauncherModule.dll' }).Count -ne 0) { $failures += '挂着钩子时收到 UNLOAD, 模块没有卸载' }
    else { Write-Host '[OK] 卸下钩子后模块已卸载' }

    if ($hookHost.HasExited) { $failures += "冒充 OpenFile 的假宿主挂了 (exit=$($hookHost.ExitCode))" }
    else
    {
        $answer = Send-HostCommand "READ $macroPath"
        if ($answer -ne 'OK 1') { $failures += "卸载后宿主打开文件不正常: $answer" }
        else { Write-Host '[OK] 卸载后宿主照常打开文件' }
    }

    $hookHeartbeatPath = Join-Path $charRoot "modules\$($hookHost.Id).json"
    if (-not (Test-Path $hookHeartbeatPath)) { $failures += "没有心跳文件 $hookHeartbeatPath" }
    else
    {
        $heartbeat = [IO.File]::ReadAllText($hookHeartbeatPath, [Text.Encoding]::UTF8) | ConvertFrom-Json
        if ($heartbeat.hooked -ne $false -or "$($heartbeat.lastError)" -ne '模块已卸载') { $failures += "卸载后心跳不对: $($heartbeat | ConvertTo-Json -Compress)" }
        if ($heartbeat.lastCid -ne $cid) { $failures += "心跳 lastCid 不对: $($heartbeat.lastCid)" }
    }

    if (-not $hookHost.HasExited) { [void](Send-HostCommand 'QUIT') }
    if (-not $hookHost.WaitForExit(3000)) { $hookHost.Kill() }

    # 挂着钩子时进程正常退出: 保存完立即退出, 不能卡住或崩溃, 写回仍要发布（工作线程或退出时补发布）
    $outboxBefore = Get-OutboxCount $setDir
    $exitHost = [Diagnostics.Process]::Start($startInfo)
    $hookHost   = $exitHost
    $exitHostId = $exitHost.Id
    Start-Sleep -Milliseconds 500

    $exitModeFile = Join-Path $env:ProgramData "DcMiniLauncher\module-$($exitHost.Id).mode"
    if (Test-Path $exitModeFile) { Remove-Item $exitModeFile -Force }

    $error3 = [Injector]::Inject($exitHost.Id, $dllPath)
    if ($error3) { throw "注入失败: $error3" }

    # 管道建好说明挂钩已做完（挂钩在管道之前）
    $exitPipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', "minilauncher-$($exitHost.Id)", [System.IO.Pipes.PipeDirection]::InOut)
    $exitPipe.Connect(10000)
    $exitPipe.Dispose()

    $answer = Send-HostCommand "READ $macroPath"
    if ($answer -ne 'OK 1') { $failures += "退出测试 READ MACRO.DAT 失败: $answer" }

    $macroExit = [byte[]]$macroLatest.Clone()
    $macroExit[6] = 4
    [IO.File]::WriteAllBytes($sourcePath, $macroExit)
    $exitHost.StandardInput.WriteLine("SAVE $macroPath|$sourcePath")
    $exitHost.StandardInput.WriteLine('QUIT')
    $exitHost.StandardInput.Flush()

    if (-not $exitHost.WaitForExit(15000)) { $failures += '挂着钩子时进程退出卡住了（15 秒）' }
    elseif ($exitHost.ExitCode -ne 0) { $failures += ('挂着钩子时进程退出码不对: 0x{0:X8}' -f $exitHost.ExitCode) }
    elseif ((Get-OutboxCount $setDir) -ne $outboxBefore + 1) { $failures += "保存后立即退出, 写回没有发布（outbox $outboxBefore → $(Get-OutboxCount $setDir)）" }
    else { Write-Host '[OK] 挂着钩子时保存后立即退出: 正常退出且已发布' }
}
finally
{
    if (-not $hookHost.WaitForExit(3000)) { $hookHost.Kill() }
    if (Test-Path $charRoot) { Remove-Item $charRoot -Recurse -Force }
}

# ---- 5. 结论 ----------------------------------------------------------------
foreach ($logPid in @($process.Id, $hookHostId, $exitHostId) | Where-Object { $_ })
{
    $logPath = Join-Path $env:TEMP "minilauncher-module-$logPid.log"
    if (Test-Path $logPath)
    {
        Write-Host "`n--- 模块日志 $logPath ---"
        # 模块日志是 UTF-8, 不指定编码 PS 5.1 会按 ANSI 读成乱码
        Get-Content $logPath -Encoding UTF8 | ForEach-Object { Write-Host "  $_" }
    }
}

if ($failures.Count -gt 0)
{
    Write-Host "`n失败:" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    exit 1
}

Write-Host "`n全部通过" -ForegroundColor Green
