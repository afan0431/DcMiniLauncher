# 编译 MiniLauncherModule.dll（F4 游戏内模块）
#
# 产物直接落到启动器的输出目录 src/bin/win-x64/, 启动器按自身 BaseDirectory 找它。
# 需要 VS Build Tools 的 C++ 工作负载（MSVC + Windows SDK）; 用 vswhere 定位, 不写死版本号。
#
#   powershell -ExecutionPolicy Bypass -File src\XIVLauncher.MiniModule\build.ps1

param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
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

$sources  = 'dllmain.cpp log.cpp mainthread.cpp pipe.cpp sigscan.cpp game.cpp contextmenu.cpp'

# ⚠ /Fo 的目录必须以 `/` 收尾, 不能是 `\`: MSVC 的 argv 解析把 `\"` 当转义引号,
#   写成 /Fo"…obj\" 会把后面的源文件名一起吞进同一个参数 (报 D8003 缺少源文件名)
# /utf-8: 源码是无 BOM 的 UTF-8, 不给这个标志 MSVC 会按系统代码页(936)读, 中文注释直接把语法搞崩
$clArgs = "/nologo /std:c++17 /utf-8 /EHsc /W4 /WX $optimize /MT /LD /Zi /DUNICODE /D_UNICODE " +
          "/Fo`"$objDir/`" /Fd`"$objDir\MiniLauncherModule.pdb`" /Fe`"$outDll`" $sources " +
          "/link /IMPLIB:`"$objDir\MiniLauncherModule.lib`" /DEBUG user32.lib advapi32.lib"

$batPath = Join-Path $objDir 'build.cmd'
@"
@echo off
call "$vcvars" >nul
if errorlevel 1 exit /b 1
cd /d "$moduleDir"
cl.exe $clArgs
"@ | Set-Content -Path $batPath -Encoding OEM

cmd.exe /c "`"$batPath`""
if ($LASTEXITCODE -ne 0) { throw "编译失败 (exit $LASTEXITCODE)" }

Write-Host "OK → $outDll" -ForegroundColor Green
