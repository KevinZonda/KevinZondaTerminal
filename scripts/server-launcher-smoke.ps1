param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repositoryRoot 'KevinZonda.Terminal.slnx'
$serverExecutable = Join-Path $repositoryRoot "src\KevinZonda.Terminal.Server\bin\$Configuration\net10.0-windows\kterm-server.exe"
$launcherExecutable = Join-Path $repositoryRoot "src\KevinZonda.Terminal.Server.Launcher\bin\$Configuration\net10.0-windows\kterm-server-launcher.exe"
$serverSmoke = Join-Path $repositoryRoot 'scripts\test-kterm-server.mjs'

function Get-FreeLoopbackUrl {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try {
        $port = ([Net.IPEndPoint]$listener.LocalEndpoint).Port
        return "http://127.0.0.1:$port"
    }
    finally {
        $listener.Stop()
    }
}

function Wait-ForHealth([string]$Url, [Diagnostics.Process]$Owner) {
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        Start-Sleep -Milliseconds 200
        $Owner.Refresh()
        if ($Owner.HasExited) {
            throw "Process $($Owner.Id) exited before its Server became healthy."
        }
        try {
            $health = Invoke-WebRequest -UseBasicParsing "$Url/healthz" -TimeoutSec 2
        }
        catch {
            $health = $null
        }
    }
    while (($null -eq $health -or $health.StatusCode -ne 200) -and [DateTime]::UtcNow -lt $deadline)

    if ($null -eq $health -or $health.StatusCode -ne 200) {
        throw "Server at $Url did not become healthy within 15 seconds."
    }
}

function Test-ServerPipeControl {
    $pipeName = "kterm-server-launcher-smoke-$([Guid]::NewGuid().ToString('N'))"
    $pipeOptions = [IO.Pipes.PipeOptions]::Asynchronous -bor [IO.Pipes.PipeOptions]::CurrentUserOnly
    $pipe = [IO.Pipes.NamedPipeServerStream]::new(
        $pipeName,
        [IO.Pipes.PipeDirection]::InOut,
        1,
        [IO.Pipes.PipeTransmissionMode]::Byte,
        $pipeOptions)
    $server = $null
    try {
        $connection = $pipe.WaitForConnectionAsync()
        $startInfo = [Diagnostics.ProcessStartInfo]::new()
        $startInfo.FileName = $serverExecutable
        $startInfo.Arguments = "--launcher-pipe $pipeName --urls $(Get-FreeLoopbackUrl) --auth-mode disabled"
        $startInfo.UseShellExecute = $true
        $startInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
        $startInfo.WorkingDirectory = $repositoryRoot
        $server = [Diagnostics.Process]::Start($startInfo)
        if (-not $connection.Wait(15000)) {
            throw 'Server did not connect to its Launcher pipe within 15 seconds.'
        }

        $reader = [IO.StreamReader]::new($pipe, [Text.Encoding]::UTF8, $false, 1024, $true)
        $logs = $reader.ReadToEndAsync()
        $writer = [IO.StreamWriter]::new(
            $pipe,
            [Text.UTF8Encoding]::new($false),
            1024,
            $true)
        $writer.AutoFlush = $true
        $writer.WriteLine('{"type":"shutdown"}')
        if (-not $server.WaitForExit(15000)) {
            throw 'Server did not honor the Launcher pipe shutdown command within 15 seconds.'
        }
        $logText = $logs.GetAwaiter().GetResult()
        if ($server.ExitCode -ne 0) {
            throw "Launcher pipe-controlled Server exited with code $($server.ExitCode).`n$logText"
        }
        if (-not $logText.Contains('Launcher requested server shutdown.')) {
            throw 'Server logs did not reach the Launcher pipe.'
        }
    }
    finally {
        if ($null -ne $server -and -not $server.HasExited) {
            Stop-Process -Id $server.Id
        }
        $pipe.Dispose()
    }
}

dotnet build $solution -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) {
    throw "Launcher solution build failed with exit code $LASTEXITCODE."
}
Test-ServerPipeControl

$launcher = $null
$launcherServer = $null
$previousMutexSuffix = $env:KTERM_LAUNCHER_MUTEX_SUFFIX
try {
    $launcherUrl = Get-FreeLoopbackUrl
    $env:KTERM_LAUNCHER_MUTEX_SUFFIX = [Guid]::NewGuid().ToString('N')
    $launcher = Start-Process `
        -FilePath $launcherExecutable `
        -ArgumentList @('--urls', $launcherUrl, '--auth-mode', 'disabled') `
        -WorkingDirectory $repositoryRoot `
        -WindowStyle Hidden `
        -PassThru
    Wait-ForHealth $launcherUrl $launcher

    & node $serverSmoke $launcherUrl --shell-io-only
    if ($LASTEXITCODE -ne 0) {
        throw "Launcher Shell I/O smoke test failed with exit code $LASTEXITCODE."
    }

    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    do {
        $launcherServerInfo = Get-CimInstance Win32_Process |
            Where-Object { $_.ParentProcessId -eq $launcher.Id -and $_.Name -eq 'kterm-server.exe' } |
            Select-Object -First 1
        if ($null -eq $launcherServerInfo) {
            Start-Sleep -Milliseconds 100
        }
    }
    while ($null -eq $launcherServerInfo -and [DateTime]::UtcNow -lt $deadline)
    if ($null -eq $launcherServerInfo) {
        throw 'Unable to identify the Server process owned by the Launcher.'
    }
    $launcherServer = Get-Process -Id $launcherServerInfo.ProcessId

    Stop-Process -Id $launcher.Id
    $null = $launcher.WaitForExit(5000)
    $launcher = $null

    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    do {
        Start-Sleep -Milliseconds 100
        $remainingServer = Get-Process -Id $launcherServer.Id -ErrorAction SilentlyContinue
    }
    while ($null -ne $remainingServer -and [DateTime]::UtcNow -lt $deadline)
    if ($null -ne $remainingServer) {
        throw 'The Launcher process job did not clean up kterm-server after Launcher termination.'
    }
    $launcherServer = $null
}
finally {
    if ($null -ne $launcher -and -not $launcher.HasExited) {
        Stop-Process -Id $launcher.Id
    }
    if ($null -ne $launcherServer) {
        $remainingServer = Get-Process -Id $launcherServer.Id -ErrorAction SilentlyContinue
        if ($null -ne $remainingServer) {
            Stop-Process -Id $remainingServer.Id
        }
    }
    if ($null -eq $previousMutexSuffix) {
        Remove-Item Env:KTERM_LAUNCHER_MUTEX_SUFFIX -ErrorAction SilentlyContinue
    }
    else {
        $env:KTERM_LAUNCHER_MUTEX_SUFFIX = $previousMutexSuffix
    }
}

Write-Output 'kterm-server Launcher smoke test passed: pipe logs/shutdown, Shell I/O, and Job Object cleanup.'
