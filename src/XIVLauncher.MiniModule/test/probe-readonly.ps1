# 只读采样: 对一个已经注入了模块的游戏进程, 依次发只读命令, 把原始回应连同时间戳追加到日志文件
#
#   pwsh -NoProfile -File src\XIVLauncher.MiniModule\test\probe-readonly.ps1 -ProcessId <pid> -LogPath <日志文件> [-Loop] [-IntervalMs 1000]
#
# 用途: 实机核对 offsets.h「自动选角」一节的偏移 —— 停在选角界面比对, 或加 -Loop 后手动登录一次全程采样。
# 只连接已存在的管道 \\.\pipe\minilauncher-<pid>, 不负责注入; 发的全是只读命令, 不改游戏任何状态。
# 管道一次只服务一个客户端: 启动器正连着模块时这里连不上, 该轮记一行 pipe-unavailable 后继续。
# -Loop 时每轮重新连接、采完即断开, 两轮之间别的客户端可以插进来; Ctrl+C 结束。

param(
    [Parameter(Mandatory = $true)]
    [int]$ProcessId,
    [Parameter(Mandatory = $true)]
    [string]$LogPath,
    [switch]$Loop,
    [int]$IntervalMs = 1000,
    [int]$ConnectTimeoutMs = 2000
)

$ErrorActionPreference = 'Stop'

$commands = @('VERSION', 'WHERE', 'LOBBYSTATE', 'CHARAS', 'WHOLIST', 'WHOAMI')
$pipeName = "minilauncher-$ProcessId"
$utf8     = New-Object System.Text.UTF8Encoding($false)

$logDir = Split-Path -Parent ([System.IO.Path]::GetFullPath($LogPath))
if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Force -Path $logDir | Out-Null }

function Write-ProbeLog
{
    param([string]$Text)

    [System.IO.File]::AppendAllText($LogPath, $Text + "`r`n", $utf8)
}

function Send-ModuleCommand
{
    param([System.IO.Pipes.NamedPipeClientStream]$Pipe, [string]$Command)

    $payload = $utf8.GetBytes($Command)
    $Pipe.Write($payload, 0, $payload.Length)
    $Pipe.Flush()

    # 消息模式: 一条回应可能比缓冲区长, 读到消息结束为止
    $buffer = New-Object byte[] 16384
    $stream = New-Object System.IO.MemoryStream

    do
    {
        $read = $Pipe.Read($buffer, 0, $buffer.Length)
        if ($read -le 0) { throw '模块断开了管道' }
        $stream.Write($buffer, 0, $read)
    }
    while (-not $Pipe.IsMessageComplete)

    return $utf8.GetString($stream.ToArray())
}

function Invoke-ProbeRound
{
    param([int]$Round)

    $stamp = (Get-Date).ToString('yyyy-MM-ddTHH:mm:ss.fffzzz')
    $pipe  = New-Object System.IO.Pipes.NamedPipeClientStream('.', $pipeName, [System.IO.Pipes.PipeDirection]::InOut)

    try
    {
        try
        {
            $pipe.Connect($ConnectTimeoutMs)
        }
        catch
        {
            Write-ProbeLog "[$stamp] #$Round pipe-unavailable $($_.Exception.Message)"
            Write-Host "#$Round 连不上 \\.\pipe\$pipeName（模块没注入, 或启动器正占着管道）"
            return
        }

        $pipe.ReadMode = [System.IO.Pipes.PipeTransmissionMode]::Message

        $summary = @()

        foreach ($command in $commands)
        {
            $stamp    = (Get-Date).ToString('yyyy-MM-ddTHH:mm:ss.fffzzz')
            $response = Send-ModuleCommand -Pipe $pipe -Command $command

            Write-ProbeLog "[$stamp] #$Round $command"
            Write-ProbeLog $response

            if ($command -eq 'WHERE' -or $command -eq 'LOBBYSTATE') { $summary += ($response -split "`n")[0] }
        }

        Write-ProbeLog ''
        Write-Host "#$Round $($summary -join ' | ')"
    }
    finally
    {
        $pipe.Dispose()
    }
}

Write-ProbeLog "=== probe-readonly pid=$ProcessId 开始 $((Get-Date).ToString('yyyy-MM-ddTHH:mm:ss.fffzzz')) loop=$($Loop.IsPresent) intervalMs=$IntervalMs ==="
Write-Host "采样写入 $LogPath"

$round = 1
Invoke-ProbeRound -Round $round

while ($Loop)
{
    Start-Sleep -Milliseconds $IntervalMs
    $round += 1
    Invoke-ProbeRound -Round $round
}
