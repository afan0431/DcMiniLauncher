# 把模块挂到一个已经在跑的游戏进程上, 跑一遍生死闸自检, 然后自我卸载
#
#   powershell -ExecutionPolicy Bypass -File src\XIVLauncher.MiniModule\test\attach-to-game.ps1 [-ProcessId <pid>] [-KeepLoaded]
#
# 不给 -ProcessId 就自动找唯一的 ffxiv_dx11。默认跑完就 UNLOAD, 把游戏还原成没被碰过的样子。
# 这条路只做两件事: 子类化窗口 WndProc + 往窗口线程 PostMessage —— 不写游戏任何内存、不装任何 hook。

param(
    [int]$ProcessId = 0,
    [switch]$KeepLoaded,
    # 幂等写自检: 把读到的主机名原样写回去, 再读回来核对。验证 SetString + DevConfig 遍历这条写入路径,
    # 但因为写的值和原值完全一样, 对正在跑的客户端没有任何语义影响。不碰 RETURNTITLE/RELEASE。
    [switch]$SafeWriteTest,
    # 客户端停在标题界面时跑: 验 TITLEREADY / KEEPALIVE / RETURNTITLE。
    # 不点登录、不改主机名、不写 SID —— 那三条要么需要真订单, 要么会让客户端拿已用过的票据去登录。
    [switch]$TitleTest,
    # 盯梢模式: 先开保活（免得标题界面闲置久了飘进片头动画, 那时 _TitleMenu 是不存在的),
    # 然后轮询 TITLEREADY 直到认出标题菜单, 顺便把已加载的 addon 列出来。
    [switch]$TitleWatch,
    [int]$WatchSeconds = 90,
    # 进程里已经有模块时先让它卸载再注入新的 —— 改完代码验证时用, 免得测的还是旧那版
    [switch]$Reload,
    # 停在标题菜单时跑: RELEASE(作废大厅上下文) → LOGIN(点开始游戏), 主机名和 SID 都不动,
    # 于是等价于「用全新连接登录同一个大区」—— 真实换服流程去掉换服那两步的版本。
    [switch]$LoginTest
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

if ($already -gt 0 -and $Reload)
{
    Write-Host '[重载] 进程里已有模块, 先让它卸载'

    $old = New-Object System.IO.Pipes.NamedPipeClientStream('.', "minilauncher-$ProcessId", [System.IO.Pipes.PipeDirection]::InOut)
    $old.Connect(10000)
    $old.ReadMode = [System.IO.Pipes.PipeTransmissionMode]::Message

    try { Write-Host "  UNLOAD → $(Send-ModuleCommand -Pipe $old -Command 'UNLOAD')" }
    finally { $old.Dispose() }

    Start-Sleep -Milliseconds 1500
    $process.Refresh()
    $already = @($process.Modules | Where-Object { $_.ModuleName -eq 'MiniLauncherModule.dll' }).Count

    if ($already -gt 0) { throw '旧模块没能卸载干净, 不敢重复注入' }
}

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

    if ($SafeWriteTest)
    {
        Write-Host '--- 幂等写自检 (写回原值, 不改变任何东西) ---' -ForegroundColor Cyan

        $problems = @()

        if ($probe -notmatch 'activeLobbyHost=(\S+)')      { $problems += '没读到 activeLobbyHost' }  else { $lobbyHost = $Matches[1] }
        if ($probe -notmatch 'saveDataBankHost=(\S+)')     { $problems += '没读到 saveDataBankHost' } else { $saveHost  = $Matches[1] }
        if ($dump  -notmatch 'GMServerHost=([^\s}]+)')     { $problems += '没读到 GMServerHost' }     else { $gmHost    = $Matches[1] }

        if ($problems.Count -gt 0)
        {
            $problems | ForEach-Object { Write-Host "  [跳过] $_" -ForegroundColor Yellow }
        }
        else
        {
            Write-Host "  原值: lobby=$lobbyHost sdb=$saveHost gm=$gmHost"

            $written = Send-ModuleCommand -Pipe $pipe -Command "SETHOSTS $lobbyHost $saveHost $gmHost"
            Write-Host "  SETHOSTS → $written"

            # 写完读回来: 三个 NetworkModule 字段 + DevConfig 三项都必须还是原值
            $after     = Send-ModuleCommand -Pipe $pipe -Command 'PROBE'
            $afterDump = Send-ModuleCommand -Pipe $pipe -Command 'DUMP'

            $checks = @(
                @{ Name = 'activeLobbyHost';  Ok = ($after -match "activeLobbyHost=$([regex]::Escape($lobbyHost))") }
                @{ Name = 'lobbyHost0';       Ok = ($after -match "lobbyHost0=$([regex]::Escape($lobbyHost))") }
                @{ Name = 'saveDataBankHost'; Ok = ($after -match "saveDataBankHost=$([regex]::Escape($saveHost))") }
                @{ Name = 'devConfig.LobbyHost01';      Ok = ($afterDump -match "LobbyHost01=$([regex]::Escape($lobbyHost))") }
                @{ Name = 'devConfig.GMServerHost';     Ok = ($afterDump -match "GMServerHost=$([regex]::Escape($gmHost))") }
                @{ Name = 'devConfig.SaveDataBankHost'; Ok = ($afterDump -match "SaveDataBankHost=$([regex]::Escape($saveHost))") }
            )

            foreach ($check in $checks)
            {
                if ($check.Ok) { Write-Host "  [OK] $($check.Name) 写回后值不变" -ForegroundColor Green }
                else           { Write-Host "  [!!] $($check.Name) 写完对不上了" -ForegroundColor Red }
            }

            if ($written -notmatch 'written=(\d+)' -or [int]$Matches[1] -lt 6)
            {
                Write-Host "  [!!] SETHOSTS 只写了 $written —— DevConfig 那三项没找全" -ForegroundColor Red
            }
        }

        # 不在标题界面时应当明确回 no-title-menu, 而不是崩或超时 —— 验证 GetAddonByName/按钮查找这条路
        $login = Send-ModuleCommand -Pipe $pipe -Command 'LOGIN'
        Write-Host "  LOGIN (当前不在标题界面) → $login"
        Write-Host ''
    }

    if ($TitleTest)
    {
        Write-Host '--- 标题界面自检 (不点登录, 不改主机名) ---' -ForegroundColor Cyan

        $ready = Send-ModuleCommand -Pipe $pipe -Command 'TITLEREADY'
        Write-Host "  TITLEREADY → $ready"

        if ($ready -match 'ready=1') { Write-Host '  [OK] 认出了标题界面' -ForegroundColor Green }
        else { Write-Host '  [!!] 没认出标题界面 —— 编排会一直等下去' -ForegroundColor Red }

        # 保活: 开着的时候 IdleTime 应当被反复归零, 关掉之后它会重新涨上去
        Write-Host "  KEEPALIVE ON → $(Send-ModuleCommand -Pipe $pipe -Command 'KEEPALIVE ON')"
        Start-Sleep -Seconds 3

        $duringA = Send-ModuleCommand -Pipe $pipe -Command 'PROBE'
        Start-Sleep -Seconds 2
        $duringB = Send-ModuleCommand -Pipe $pipe -Command 'PROBE'

        Write-Host "  KEEPALIVE OFF → $(Send-ModuleCommand -Pipe $pipe -Command 'KEEPALIVE OFF')"
        Start-Sleep -Seconds 4
        $after = Send-ModuleCommand -Pipe $pipe -Command 'PROBE'

        $idleA = if ($duringA -match 'idleTime=(-?\d+)') { [int]$Matches[1] } else { -1 }
        $idleB = if ($duringB -match 'idleTime=(-?\d+)') { [int]$Matches[1] } else { -1 }
        $idleC = if ($after   -match 'idleTime=(-?\d+)') { [int]$Matches[1] } else { -1 }

        Write-Host "  idleTime: 保活中=$idleA / $idleB, 停保活 4 秒后=$idleC"

        if ($idleC -gt $idleB) { Write-Host '  [OK] 保活确实在压着 IdleTime (停掉后就涨回去了)' -ForegroundColor Green }
        else { Write-Host '  [?] IdleTime 没按预期变化, 需人工确认这个字段在标题界面是否会自增' -ForegroundColor Yellow }

        # 已经在标题界面了, 再调一次 returnToTitle 应当是安全的空操作 —— 验它不崩
        Write-Host "  RETURNTITLE → $(Send-ModuleCommand -Pipe $pipe -Command 'RETURNTITLE')"
        Start-Sleep -Seconds 2
        Write-Host "  TITLEREADY (调用后) → $(Send-ModuleCommand -Pipe $pipe -Command 'TITLEREADY')"
        Write-Host ''
    }

    if ($TitleWatch)
    {
        Write-Host '--- 盯梢标题菜单 (已开保活, 请退出片头动画) ---' -ForegroundColor Cyan
        Write-Host "  KEEPALIVE ON → $(Send-ModuleCommand -Pipe $pipe -Command 'KEEPALIVE ON')"

        $deadline = (Get-Date).AddSeconds($WatchSeconds)
        $ready    = $false
        $last     = ''

        while ((Get-Date) -lt $deadline -and -not $ready)
        {
            $response = Send-ModuleCommand -Pipe $pipe -Command 'TITLEREADY'

            if ($response -ne $last)
            {
                Write-Host "  [$(Get-Date -Format HH:mm:ss)] TITLEREADY → $response"
                $last = $response
            }

            if ($response -match 'ready=1') { $ready = $true; break }
            Start-Sleep -Seconds 2
        }

        if ($ready) { Write-Host '  [OK] 认出了标题菜单' -ForegroundColor Green }
        else { Write-Host "  [!!] $WatchSeconds 秒内没等到 ready=1" -ForegroundColor Red }

        # 不管认没认出来都把 addon 列出来: 列得出一串合理名字 = unitManager 指针是对的
        $addons = Send-ModuleCommand -Pipe $pipe -Command 'ADDONS'
        Write-Host "  ADDONS → $addons"

        Write-Host "  KEEPALIVE OFF → $(Send-ModuleCommand -Pipe $pipe -Command 'KEEPALIVE OFF')"
        Write-Host ''
    }

    if ($LoginTest)
    {
        Write-Host '--- RELEASE + LOGIN 自检 (主机名与 SID 都不动) ---' -ForegroundColor Cyan

        $ready = Send-ModuleCommand -Pipe $pipe -Command 'TITLEREADY'

        if ($ready -notmatch 'ready=1')
        {
            Write-Host "  [跳过] 现在不在标题菜单 ($ready)" -ForegroundColor Yellow
        }
        else
        {
            $before = Send-ModuleCommand -Pipe $pipe -Command 'PROBE'
            if ($before -match 'ctx=(\S+) state=(\d+)') { Write-Host "  RELEASE 前: ctx=$($Matches[1]) state=$($Matches[2])" }

            Write-Host "  RELEASE → $(Send-ModuleCommand -Pipe $pipe -Command 'RELEASE')"

            $after = Send-ModuleCommand -Pipe $pipe -Command 'PROBE'
            if ($after -match 'ctx=(\S+) state=(\d+)')
            {
                Write-Host "  RELEASE 后: ctx=$($Matches[1]) state=$($Matches[2])"
                if ($Matches[2] -eq '0') { Write-Host '  [OK] LobbyUIClient 的 Context/State 已清零' -ForegroundColor Green }
                else { Write-Host '  [!!] State 没被清零' -ForegroundColor Red }
            }

            # 保活开着, 免得后面等登录的时候飘进片头动画
            $null = Send-ModuleCommand -Pipe $pipe -Command 'KEEPALIVE ON'
            Write-Host "  LOGIN → $(Send-ModuleCommand -Pipe $pipe -Command 'LOGIN')"

            # 登录成功的判据: 标题菜单消失, 角色选择相关 addon 出现
            $deadline = (Get-Date).AddSeconds(60)
            $done     = $false

            while ((Get-Date) -lt $deadline)
            {
                Start-Sleep -Seconds 3
                $addons = Send-ModuleCommand -Pipe $pipe -Command 'ADDONS'

                if ($addons -match 'CharaSelect|_CharaSelectListMenu|CharaSelectWorldServer')
                {
                    Write-Host '  [OK] 出现角色选择界面 —— 全新连接登录成功' -ForegroundColor Green
                    Write-Host "  ADDONS → $addons"
                    $done = $true
                    break
                }

                if ($addons -match '_TitleConnect|NowLoading') { Write-Host "  [$(Get-Date -Format HH:mm:ss)] 连接中…" }
            }

            if (-not $done)
            {
                Write-Host '  [!!] 60 秒内没等到角色选择界面 —— 看游戏画面上是什么提示' -ForegroundColor Red
                Write-Host "  ADDONS → $(Send-ModuleCommand -Pipe $pipe -Command 'ADDONS')"
            }

            $null = Send-ModuleCommand -Pipe $pipe -Command 'KEEPALIVE OFF'
        }

        Write-Host ''
    }

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
