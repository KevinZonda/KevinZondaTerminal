$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repositoryRoot 'src\KevinZonda.Terminal\KevinZonda.Terminal.csproj'
$executable = Join-Path $repositoryRoot 'src\KevinZonda.Terminal\bin\Debug\net10.0-windows\KevinZonda.Terminal.exe'
$environmentProbe = Join-Path ([IO.Path]::GetTempPath()) "kterm-smoke-$([Guid]::NewGuid().ToString('N')).txt"
$completionProbe = Join-Path ([IO.Path]::GetTempPath()) "kterm-smoke-complete-$([Guid]::NewGuid().ToString('N')).txt"
$recentWorkspaceProbe = Join-Path ([IO.Path]::GetTempPath()) "kterm-smoke-recent-$([Guid]::NewGuid().ToString('N')).json"

dotnet build $project --nologo
if ($LASTEXITCODE -ne 0) {
    throw "KevinZonda Terminal build failed with exit code $LASTEXITCODE."
}

$env:KTERM_SMOKE_TEST = '1'
$env:KTERM_SMOKE_OUTPUT = $environmentProbe
$env:KTERM_SMOKE_COMPLETE = $completionProbe
$env:KTERM_RECENT_WORKSPACES_FILE = $recentWorkspaceProbe
$env:KTERM_DISABLE_JUMP_LIST = '1'
try {
    $application = Start-Process -FilePath $executable -WorkingDirectory (Split-Path $executable) -PassThru
}
finally {
    Remove-Item Env:\KTERM_SMOKE_TEST -ErrorAction SilentlyContinue
    Remove-Item Env:\KTERM_SMOKE_OUTPUT -ErrorAction SilentlyContinue
    Remove-Item Env:\KTERM_SMOKE_COMPLETE -ErrorAction SilentlyContinue
    Remove-Item Env:\KTERM_RECENT_WORKSPACES_FILE -ErrorAction SilentlyContinue
    Remove-Item Env:\KTERM_DISABLE_JUMP_LIST -ErrorAction SilentlyContinue
}

$uiProcess = $null
$children = @()
try {
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        Start-Sleep -Milliseconds 250
        $application.Refresh()
        if ($application.HasExited) {
            throw "KevinZonda Terminal exited during smoke initialization with code $($application.ExitCode)."
        }

        if ($null -eq $uiProcess) {
            $uiChild = Get-CimInstance Win32_Process |
                Where-Object {
                    $_.ParentProcessId -eq $application.Id -and
                    $_.CommandLine -like '*--kterm-ui-child*'
                } |
                Select-Object -First 1
            if ($null -ne $uiChild) {
                $uiProcess = Get-Process -Id $uiChild.ProcessId
            }
        }

        if ($null -eq $uiProcess) {
            continue
        }
        $uiProcess.Refresh()
        if ($uiProcess.HasExited) {
            throw "KevinZonda Terminal UI exited during smoke initialization with code $($uiProcess.ExitCode)."
        }

        $children = @(Get-CimInstance Win32_Process | Where-Object ParentProcessId -eq $uiProcess.Id)
        $terminalHosts = @(
            $children |
                Where-Object Name -in @('conhost.exe', 'OpenConsole.exe', 'OpenConsole.Enhanced.exe')
        )
        $shells = @(
            $children |
                Where-Object Name -notin @(
                    'conhost.exe',
                    'OpenConsole.exe',
                    'OpenConsole.Enhanced.exe',
                    'msedgewebview2.exe',
                    'KevinZonda.Terminal.exe'
                )
        )
    }
    while (($shells.Count -lt 5 -or
        $terminalHosts.Count -lt 5 -or
        -not (Test-Path -LiteralPath $environmentProbe) -or
        -not (Test-Path -LiteralPath $completionProbe)) -and [DateTime]::UtcNow -lt $deadline)

    if ($shells.Count -ne 5) {
        throw "Expected 5 independent Shell processes, found $($shells.Count)."
    }
    if ($terminalHosts.Count -ne 5) {
        throw "Expected 5 ConPTY host processes, found $($terminalHosts.Count)."
    }
    if (-not (Test-Path -LiteralPath $environmentProbe)) {
        throw 'The shell environment probe did not complete.'
    }
    if (-not (Test-Path -LiteralPath $completionProbe)) {
        throw 'The desktop automation sequence did not complete.'
    }
    if (-not (Test-Path -LiteralPath $recentWorkspaceProbe)) {
        throw 'The recent workspace record was not created.'
    }

    $recentWorkspaces = Get-Content -Raw -LiteralPath $recentWorkspaceProbe | ConvertFrom-Json
    $expectedWorkspace = [IO.Path]::GetFullPath((Split-Path $executable))
    if ($recentWorkspaces.workspaces.Count -ne 1 -or
        -not [string]::Equals(
            $recentWorkspaces.workspaces[0],
            $expectedWorkspace,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unexpected recent workspace record: $($recentWorkspaces.workspaces -join ', ')."
    }

    $environmentValues = @(Get-Content -LiteralPath $environmentProbe)
    if ($environmentValues.Count -ne 2 -or
        $environmentValues[0] -ne 'xterm-256color' -or
        $environmentValues[1] -ne 'truecolor') {
        throw "Unexpected shell environment: $($environmentValues -join ', ')."
    }
}
finally {
    $childProcessIds = @($children.ProcessId)
    if ($null -ne $uiProcess -and -not $uiProcess.HasExited) {
        $null = $uiProcess.CloseMainWindow()
        if (-not $uiProcess.WaitForExit(12000)) {
            Stop-Process -Id $uiProcess.Id
            throw 'KevinZonda Terminal did not close cleanly within 12 seconds.'
        }
    }
    if (-not $application.HasExited -and -not $application.WaitForExit(5000)) {
        Stop-Process -Id $application.Id
        throw 'KevinZonda Terminal supervisor did not exit after its UI closed.'
    }

    Start-Sleep -Milliseconds 800
    $remaining = @(Get-Process -Id $childProcessIds -ErrorAction SilentlyContinue)
    if ($remaining.Count -ne 0) {
        throw "KevinZonda Terminal left $($remaining.Count) child processes running after shutdown."
    }

    [IO.File]::Delete($environmentProbe)
    [IO.File]::Delete($completionProbe)
    [IO.File]::Delete($recentWorkspaceProbe)
}

Write-Output 'KevinZonda Terminal smoke test passed: recent workspace, xterm-256color/truecolor, 2 tabs, 2x2 active layout, 5 Shells, 5 ConPTY hosts, 0 leaked child processes.'
