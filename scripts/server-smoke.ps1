param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repositoryRoot 'src\KevinZonda.Terminal.Server\KevinZonda.Terminal.Server.csproj'
$executable = Join-Path $repositoryRoot "src\KevinZonda.Terminal.Server\bin\$Configuration\net10.0-windows\kterm-server.exe"
$testScript = Join-Path $repositoryRoot 'scripts\test-kterm-server.mjs'

dotnet build $project -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) {
    throw "kterm-server build failed with exit code $LASTEXITCODE."
}

$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$listener.Start()
$port = ([Net.IPEndPoint]$listener.LocalEndpoint).Port
$listener.Stop()
$url = "http://127.0.0.1:$port"

$server = Start-Process `
    -FilePath $executable `
    -ArgumentList @('--urls', $url) `
    -WorkingDirectory $repositoryRoot `
    -WindowStyle Hidden `
    -PassThru

function Get-DescendantProcessIds([int]$ParentId) {
    $all = @(Get-CimInstance Win32_Process)
    $pending = [Collections.Generic.Queue[int]]::new()
    $result = [Collections.Generic.List[int]]::new()
    $pending.Enqueue($ParentId)
    while ($pending.Count -gt 0) {
        $current = $pending.Dequeue()
        foreach ($child in $all | Where-Object ParentProcessId -eq $current) {
            $result.Add([int]$child.ProcessId)
            $pending.Enqueue([int]$child.ProcessId)
        }
    }
    return @($result)
}

$baselineChildIds = @()
$sessionChildIds = @()
$shutdownChildIds = @()
try {
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        Start-Sleep -Milliseconds 200
        $server.Refresh()
        if ($server.HasExited) {
            throw "kterm-server exited during startup with code $($server.ExitCode)."
        }

        try {
            $health = Invoke-WebRequest -UseBasicParsing "$url/healthz" -TimeoutSec 2
        }
        catch {
            $health = $null
        }
    }
    while (($null -eq $health -or $health.StatusCode -ne 200) -and [DateTime]::UtcNow -lt $deadline)

    if ($null -eq $health -or $health.StatusCode -ne 200) {
        throw 'kterm-server did not become healthy within 15 seconds.'
    }

    $baselineChildIds = @(Get-DescendantProcessIds $server.Id)
    & node $testScript $url
    if ($LASTEXITCODE -ne 0) {
        throw "kterm-server browser protocol test failed with code $LASTEXITCODE."
    }

    $cleanupDeadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        Start-Sleep -Milliseconds 250
        $sessionChildIds = @(
            Get-DescendantProcessIds $server.Id |
                Where-Object { $_ -notin $baselineChildIds }
        )
    }
    while ($sessionChildIds.Count -gt 0 -and [DateTime]::UtcNow -lt $cleanupDeadline)

    if ($sessionChildIds.Count -ne 0) {
        throw "kterm-server left $($sessionChildIds.Count) Shell or ConPTY processes after the browser disconnected."
    }
}
finally {
    if (-not $server.HasExited) {
        $shutdownChildIds = @(Get-DescendantProcessIds $server.Id)
    }
    if (-not $server.HasExited) {
        Stop-Process -Id $server.Id
        $null = $server.WaitForExit(5000)
    }

    Start-Sleep -Milliseconds 500
    $remaining = if ($shutdownChildIds.Count -gt 0) {
        @(Get-Process -Id $shutdownChildIds -ErrorAction SilentlyContinue)
    }
    else {
        @()
    }
    if ($remaining.Count -ne 0) {
        throw "kterm-server left $($remaining.Count) child processes after shutdown."
    }
}

Write-Output 'kterm-server smoke test passed: HTTP assets, WebSocket protocol, local Shell I/O, disconnect cleanup, and shutdown cleanup.'
