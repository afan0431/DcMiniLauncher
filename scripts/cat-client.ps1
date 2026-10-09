# dml-cat/1 command-line client for manual testing of the headless launcher.
#
# Starts "<Exe> --cat-launch [--cat-detached]", hands over pipe name + token on stdin, does the hello
# handshake, sends one request (launch / adopt) and then prints every event until the launcher exits.
# Extra requests (status / close / inject): append one JSON object per line to -CommandFile while it runs,
# e.g.  {"method":"close","params":{"timeoutSeconds":10}}
#
#   powershell -File scripts\cat-client.ps1 -Exe src\bin\win-x64\XIVLauncherCN.exe -Method launch -Params '{"operationId":"t1","accountName":"xxx","dalamud":true}'
#   powershell -File scripts\cat-client.ps1 -Exe src\bin\win-x64\XIVLauncherCN.exe -Method adopt  -Params '{"operationId":"t2","pid":1234,"processStartedAt":"2026-10-09T10:00:00.000Z"}'

param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][ValidateSet('launch', 'adopt')][string]$Method,
    [Parameter(Mandatory = $true)][string]$Params,
    [switch]$Detached,
    [switch]$Simulate,
    [string]$CommandFile
)

$ErrorActionPreference = 'Stop'

$exePath  = (Resolve-Path $Exe).Path
$pipeName = 'cat-dml-' + ([guid]::NewGuid().ToString('N'))
$token    = -join ((1..64) | ForEach-Object { '{0:x}' -f (Get-Random -Maximum 16) })

$switches = @('--cat-launch')
if ($Detached) { $switches += '--cat-detached' }
if ($Simulate) { $switches += '--cat-simulate' }

$psi = New-Object System.Diagnostics.ProcessStartInfo $exePath
$psi.Arguments              = $switches -join ' '
$psi.WorkingDirectory       = Split-Path -Parent $exePath
$psi.UseShellExecute        = $false
$psi.RedirectStandardInput  = $true
$psi.CreateNoWindow         = $true

$child = [System.Diagnostics.Process]::Start($psi)
$child.StandardInput.WriteLine((@{ pipeName = $pipeName; token = $token } | ConvertTo-Json -Compress))
$child.StandardInput.Flush()
Write-Host "[client] launcher pid=$($child.Id) pipe=$pipeName"

$pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', $pipeName, [System.IO.Pipes.PipeDirection]::InOut, [System.IO.Pipes.PipeOptions]::Asynchronous)
$pipe.Connect(20000)
$utf8   = New-Object System.Text.UTF8Encoding($false)
$reader = New-Object System.IO.StreamReader($pipe, $utf8)
$writer = New-Object System.IO.StreamWriter($pipe, $utf8)
$writer.AutoFlush = $true
$script:nextId = 0

function Send-Request([string]$name, $parameters) {
    $script:nextId++
    $frame = @{ jsonrpc = '2.0'; id = $script:nextId; method = $name; params = $parameters } | ConvertTo-Json -Compress -Depth 10
    $writer.WriteLine($frame)
    Write-Host "[client] >> $name"
}

Send-Request 'hello' @{ token = $token }
Write-Host "[client] << $($reader.ReadLine())"

Send-Request $Method ($Params | ConvertFrom-Json)

$pending  = $reader.ReadLineAsync()
$consumed = 0

while (-not $child.HasExited) {
    if ($pending.Wait(250)) {
        $line = $pending.Result
        if ($null -eq $line) { break }
        Write-Host "[$(Get-Date -Format HH:mm:ss.fff)] << $line"
        if ($line -match '"method":"game\.exited"' -or $line -match '"method":"launch\.failed"') {
            Write-Host '[client] game ended, waiting for the launcher to exit'
        }
        $pending = $reader.ReadLineAsync()
    }

    if ($CommandFile -and (Test-Path $CommandFile)) {
        $lines = @(Get-Content $CommandFile)
        for ($i = $consumed; $i -lt $lines.Count; $i++) {
            if ($lines[$i].Trim()) {
                $request = $lines[$i] | ConvertFrom-Json
                Send-Request $request.method $request.params
            }
        }
        $consumed = $lines.Count
    }
}

Write-Host "[client] launcher exited with code $($child.ExitCode)"
