# 编译 MiniLauncherModule.dll（F4 游戏内模块）
#
# 产物直接落到启动器的输出目录 src/bin/win-x64/, 启动器按自身 BaseDirectory 找它。
# 需要 VS Build Tools 的 C++ 工作负载（MSVC + Windows SDK）; 用 vswhere 定位, 不写死版本号。
#
#   powershell -ExecutionPolicy Bypass -File src\XIVLauncher.MiniModule\build.ps1
#   powershell -ExecutionPolicy Bypass -File src\XIVLauncher.MiniModule\build.ps1 -Test
#
# -Test: 编完模块后再编离线测试 test\charconfig-test.cpp（共用游戏配置的纯逻辑）并运行, 有失败时报错退出。
#        真实样本目录由环境变量 CAT_CHARCONFIG_SAMPLES 指定, 默认 %USERPROFILE%\.cat\shared-config, 不存在时跳过那一组。

param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$Test
)

$ErrorActionPreference = 'Stop'

$moduleDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$objDir    = Join-Path $moduleDir 'obj'
$outDir    = Join-Path (Split-Path -Parent $moduleDir) 'bin\win-x64'
$outDll    = Join-Path $outDir 'MiniLauncherModule.dll'

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) { throw "找不到 vswhere: $vswhere（没装 Visual Studio / Build Tools）" }

$vsPath = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if ([string]::IsNullOrWhiteSpace($vsPath)) { throw '没有装 C++ 工作负载 (Microsoft.VisualStudio.Component.VC.Tools.x86.x64)' }

$vcvars = Join-Path $vsPath 'VC\Auxiliary\Build\vcvars64.bat'
if (-not (Test-Path $vcvars)) { throw "找不到 vcvars64.bat: $vcvars" }

foreach ($dir in @($objDir, $outDir)) {
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
}

# 游戏进程里不保证有对应版本的 VC 运行时 DLL, 所以静态链接 (/MT), 让模块自带
$optimize = if ($Configuration -eq 'Debug') { '/Od /DDEBUG' } else { '/O2 /DNDEBUG' }

$sources  = 'dllmain.cpp log.cpp mainthread.cpp pipe.cpp sigscan.cpp game.cpp contextmenu.cpp charconfig_core.cpp charconfig_hook.cpp'
$libs     = 'user32.lib advapi32.lib bcrypt.lib shell32.lib ole32.lib'

# ⚠ /Fo 的目录必须以 `/` 收尾, 不能是 `\`: MSVC 的 argv 解析把 `\"` 当转义引号,
#   写成 /Fo"…obj\" 会把后面的源文件名一起吞进同一个参数 (报 D8003 缺少源文件名)
# /utf-8: 源码是无 BOM 的 UTF-8, 不给这个标志 MSVC 会按系统代码页(936)读, 中文注释直接把语法搞崩
# MinHook（third_party/minhook, 原样复制的 C 源码）单独编成目标文件: 第三方代码不套本项目的 /W4 /WX
$minHookObjDir  = Join-Path $objDir 'minhook'
if (-not (Test-Path $minHookObjDir)) { New-Item -ItemType Directory -Force -Path $minHookObjDir | Out-Null }
$minHookSources = 'third_party\minhook\src\buffer.c third_party\minhook\src\hook.c third_party\minhook\src\trampoline.c third_party\minhook\src\hde\hde64.c'
$minHookObjs    = "`"$minHookObjDir\buffer.obj`" `"$minHookObjDir\hook.obj`" `"$minHookObjDir\trampoline.obj`" `"$minHookObjDir\hde64.obj`""
$minHookArgs    = "/nologo /c /W3 $optimize /MT /Zi /Fo`"$minHookObjDir/`" /Fd`"$minHookObjDir\minhook.pdb`" $minHookSources"

$clArgs = "/nologo /std:c++17 /utf-8 /EHsc /W4 /WX $optimize /MT /LD /Zi /DUNICODE /D_UNICODE " +
          "/Fo`"$objDir/`" /Fd`"$objDir\MiniLauncherModule.pdb`" /Fe`"$outDll`" $sources $minHookObjs " +
          "/link /IMPLIB:`"$objDir\MiniLauncherModule.lib`" /DEBUG $libs"

$batPath = Join-Path $objDir 'build.cmd'
@"
@echo off
call "$vcvars" >nul
if errorlevel 1 exit /b 1
cd /d "$moduleDir"
cl.exe $minHookArgs
if errorlevel 1 exit /b 1
cl.exe $clArgs
"@ | Set-Content -Path $batPath -Encoding OEM

cmd.exe /c "`"$batPath`""
if ($LASTEXITCODE -ne 0) { throw "编译失败 (exit $LASTEXITCODE)" }

Write-Host "OK → $outDll" -ForegroundColor Green

if (-not $Test) { return }

# ---- 离线测试 ---------------------------------------------------------------
# 目标文件放单独的子目录: 与模块同名的 charconfig_core.obj 编译选项不同（模块是 /LD）
$testObjDir = Join-Path $objDir 'charconfig-test'
$testExe    = Join-Path $objDir 'charconfig-test.exe'
if (-not (Test-Path $testObjDir)) { New-Item -ItemType Directory -Force -Path $testObjDir | Out-Null }

$testArgs = "/nologo /std:c++17 /utf-8 /EHsc /W4 /WX $optimize /MT /Zi /DUNICODE /D_UNICODE " +
            "/Fo`"$testObjDir/`" /Fd`"$testObjDir\charconfig-test.pdb`" /Fe`"$testExe`" charconfig_core.cpp test\charconfig-test.cpp " +
            "/link /DEBUG $libs"

$testBat = Join-Path $objDir 'build-charconfig-test.cmd'
@"
@echo off
call "$vcvars" >nul
if errorlevel 1 exit /b 1
cd /d "$moduleDir"
cl.exe $testArgs
"@ | Set-Content -Path $testBat -Encoding OEM

cmd.exe /c "`"$testBat`""
if ($LASTEXITCODE -ne 0) { throw "离线测试编译失败 (exit $LASTEXITCODE)" }

# 测试程序输出 UTF-8
$previousEncoding = [Console]::OutputEncoding
[Console]::OutputEncoding = [Text.Encoding]::UTF8
try { & $testExe; $testExit = $LASTEXITCODE }
finally { [Console]::OutputEncoding = $previousEncoding }

if ($testExit -ne 0) { throw "离线测试有失败 (exit $testExit)" }
Write-Host "离线测试通过" -ForegroundColor Green
