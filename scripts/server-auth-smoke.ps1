param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$serverProject = Join-Path $repositoryRoot 'src\KevinZonda.Terminal.Server\KevinZonda.Terminal.Server.csproj'
$serverExecutable = Join-Path $repositoryRoot "src\KevinZonda.Terminal.Server\bin\$Configuration\net10.0-windows\kterm-server.exe"
$testScript = Join-Path $repositoryRoot 'scripts\test-kterm-server-auth.mjs'
$testPassword = 'kterm-server-auth-smoke-password'
$additionalPassword = 'kterm-server-auth-smoke-password-rotated'
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$temporaryDirectory = [IO.Path]::GetFullPath((Join-Path $temporaryRoot "kterm-server-auth-$([Guid]::NewGuid().ToString('N'))"))
if (-not $temporaryDirectory.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The authentication smoke-test directory escaped the system temporary directory.'
}
$authFile = Join-Path $temporaryDirectory 'server_auth.json'

dotnet build $serverProject -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) {
    throw "kterm-server build failed with exit code $LASTEXITCODE."
}

New-Item -ItemType Directory -Path $temporaryDirectory | Out-Null
$server = $null
try {
    @($testPassword, $testPassword) | & $serverExecutable auth init --file $authFile
    if ($LASTEXITCODE -ne 0) {
        throw "kterm-server auth init failed with exit code $LASTEXITCODE."
    }

    @($testPassword) | & $serverExecutable auth verify --file $authFile
    if ($LASTEXITCODE -ne 0) {
        throw "kterm-server auth verify failed with exit code $LASTEXITCODE."
    }

    @($additionalPassword, $additionalPassword) | & $serverExecutable auth add --file $authFile
    if ($LASTEXITCODE -ne 0) {
        throw "kterm-server auth add failed with exit code $LASTEXITCODE."
    }

    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $port = ([Net.IPEndPoint]$listener.LocalEndpoint).Port
    $listener.Stop()
    $url = "http://127.0.0.1:$port"
    $authenticatedStandardOutput = Join-Path $temporaryDirectory 'authenticated.stdout.log'
    $authenticatedStandardError = Join-Path $temporaryDirectory 'authenticated.stderr.log'

    $server = Start-Process `
        -FilePath $serverExecutable `
        -ArgumentList @('--urls', $url, '--auth-mode', 'required', '--auth-file', $authFile) `
        -WorkingDirectory $repositoryRoot `
        -WindowStyle Hidden `
        -RedirectStandardOutput $authenticatedStandardOutput `
        -RedirectStandardError $authenticatedStandardError `
        -PassThru

    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        Start-Sleep -Milliseconds 200
        $server.Refresh()
        if ($server.HasExited) {
            throw "kterm-server exited during authenticated startup with code $($server.ExitCode)."
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
        throw 'Authenticated kterm-server did not become healthy within 15 seconds.'
    }

    $env:KTERM_TEST_PASSWORD = $testPassword
    & node $testScript $url
    if ($LASTEXITCODE -ne 0) {
        $authenticatedLogs =
            (Get-Content -Raw -LiteralPath $authenticatedStandardOutput) +
            (Get-Content -Raw -LiteralPath $authenticatedStandardError)
        throw "kterm-server authentication protocol test failed with code $LASTEXITCODE.`n$authenticatedLogs"
    }

    Stop-Process -Id $server.Id
    $null = $server.WaitForExit(5000)
    $server = $null

    Set-Content -LiteralPath $authFile -Encoding utf8 -Value '{"allowedHash":[]}'
    $standardOutput = Join-Path $temporaryDirectory 'auto-empty.stdout.log'
    $standardError = Join-Path $temporaryDirectory 'auto-empty.stderr.log'
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $port = ([Net.IPEndPoint]$listener.LocalEndpoint).Port
    $listener.Stop()
    $url = "http://127.0.0.1:$port"

    $server = Start-Process `
        -FilePath $serverExecutable `
        -ArgumentList @('--urls', $url, '--auth-mode', 'auto', '--auth-file', $authFile) `
        -WorkingDirectory $repositoryRoot `
        -WindowStyle Hidden `
        -RedirectStandardOutput $standardOutput `
        -RedirectStandardError $standardError `
        -PassThru

    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        Start-Sleep -Milliseconds 200
        $server.Refresh()
        if ($server.HasExited) {
            throw "kterm-server exited during empty auto-mode startup with code $($server.ExitCode)."
        }
        try {
            $page = Invoke-WebRequest -UseBasicParsing $url -TimeoutSec 2
        }
        catch {
            $page = $null
        }
    }
    while (($null -eq $page -or $page.StatusCode -ne 200) -and [DateTime]::UtcNow -lt $deadline)

    if ($null -eq $page -or $page.StatusCode -ne 200) {
        throw 'Empty auto-mode kterm-server did not expose the no-password frontend within 15 seconds.'
    }

    $dashboardPage = Invoke-WebRequest -UseBasicParsing "$url/dashboard/" -TimeoutSec 2
    if ($dashboardPage.StatusCode -ne 200 -or
        -not $dashboardPage.Content.Contains('id="dashboard-app"') -or
        -not $dashboardPage.Content.Contains('Local Configuration')) {
        throw 'Empty auto-mode kterm-server did not expose the disabled Dashboard frontend.'
    }
    $dashboardStatus = Invoke-RestMethod -UseBasicParsing "$url/api/dashboard/status" -TimeoutSec 2
    if ($dashboardStatus.enabled -ne $false) {
        throw 'Empty auto-mode Dashboard management was not disabled.'
    }

    Stop-Process -Id $server.Id
    $null = $server.WaitForExit(5000)
    $server = $null
    $logs = (Get-Content -Raw -LiteralPath $standardOutput) + (Get-Content -Raw -LiteralPath $standardError)
    if (-not $logs.Contains('No Pass Hash, fallback to No Pass.')) {
        throw 'Empty auto-mode startup did not emit the expected fallback warning.'
    }
}
finally {
    Remove-Item Env:KTERM_TEST_PASSWORD -ErrorAction SilentlyContinue
    if ($null -ne $server -and -not $server.HasExited) {
        Stop-Process -Id $server.Id
        $null = $server.WaitForExit(5000)
    }
    if (Test-Path -LiteralPath $temporaryDirectory) {
        Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force
    }
}

Write-Output 'kterm-server authentication smoke test passed.'
