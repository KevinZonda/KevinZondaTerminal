param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repositoryRoot 'KevinZonda.Terminal.slnx'
$serverExecutable = Join-Path $repositoryRoot "src\KevinZonda.Terminal.Server\bin\$Configuration\net10.0-windows\kterm-server.exe"
$launcherExecutable = Join-Path $repositoryRoot "src\KevinZonda.Terminal.Server.Launcher\bin\$Configuration\net10.0-windows\kterm-server-launcher.exe"

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

dotnet build $solution -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) {
    throw "Launcher solution build failed with exit code $LASTEXITCODE."
}

$controlledServer = $null
$launcher = $null
$launcherServer = $null
$previousMutexSuffix = $env:KTERM_LAUNCHER_MUTEX_SUFFIX
try {
    $controlUrl = Get-FreeLoopbackUrl
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $serverExecutable
    $startInfo.Arguments = "--launcher-control --urls $controlUrl --auth-mode disabled"
    $startInfo.CreateNoWindow = $true
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $controlledServer = [Diagnostics.Process]::new()
    $controlledServer.StartInfo = $startInfo
    if (-not $controlledServer.Start()) {
        throw 'Unable to start the directly controlled Server.'
    }
    $standardOutput = $controlledServer.StandardOutput.ReadToEndAsync()
    $standardError = $controlledServer.StandardError.ReadToEndAsync()
    try {
        Wait-ForHealth $controlUrl $controlledServer
    }
    catch {
        $controlledServer.StandardInput.Close()
        if (-not $controlledServer.WaitForExit(5000)) {
            Stop-Process -Id $controlledServer.Id
            $null = $controlledServer.WaitForExit(5000)
        }
        throw "$($_.Exception.Message)`n$($standardOutput.Result)`n$($standardError.Result)"
    }
    $controlledServer.StandardInput.WriteLine('shutdown')
    $controlledServer.StandardInput.Flush()
    if (-not $controlledServer.WaitForExit(10000)) {
        throw 'Server did not honor the Launcher stdin shutdown command within 10 seconds.'
    }
    if ($controlledServer.ExitCode -ne 0) {
        throw "Launcher-controlled Server exited with code $($controlledServer.ExitCode).`n$($standardOutput.Result)`n$($standardError.Result)"
    }
    $controlledServer.Dispose()
    $controlledServer = $null

    $launcherUrl = Get-FreeLoopbackUrl
    $env:KTERM_LAUNCHER_MUTEX_SUFFIX = [Guid]::NewGuid().ToString('N')
    $launcher = Start-Process `
        -FilePath $launcherExecutable `
        -ArgumentList @('--urls', $launcherUrl, '--auth-mode', 'disabled') `
        -WorkingDirectory $repositoryRoot `
        -WindowStyle Hidden `
        -PassThru
    Wait-ForHealth $launcherUrl $launcher

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
    if ($null -ne $controlledServer -and -not $controlledServer.HasExited) {
        Stop-Process -Id $controlledServer.Id
    }
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

Write-Output 'kterm-server Launcher smoke test passed: stdin shutdown and Job Object cleanup.'
