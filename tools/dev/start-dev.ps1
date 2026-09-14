#Requires -Version 7
<#
.SYNOPSIS
    Starts a local Fleeto instance: signer, gateway, workers and web, each in its own PowerShell window.

.DESCRIPTION
    Run tools/dev/setup-dev.ps1 once first. The signer starts first because the gateway needs a certificate from it.
    Close a window (or press Ctrl+C in it) to stop that component. Logs appear in each window; the web also writes
    src/Fleetify.Web/logs/.

    URLs:
      Web UI           https://localhost:7100
      Agent gateway    https://localhost:7200  (agents connect here; mTLS)
      Gateway health   http://localhost:5201/health

.PARAMETER NoBuild
    Skip the build (use after a successful build).
#>
[CmdletBinding()]
param(
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$secrets = Join-Path $env:LOCALAPPDATA 'Fleetify\dev\secrets'

if (-not (Test-Path (Join-Path $secrets 'root.key'))) {
    throw "No development secrets found in $secrets. Run tools/dev/setup-dev.ps1 first."
}

Push-Location $repo
try {
    if (-not $NoBuild) {
        Write-Host '==> Build' -ForegroundColor Cyan
        dotnet build Fleetify.slnx -v q | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

        Write-Host '==> Apply pending migrations' -ForegroundColor Cyan
        $env:Fleetify__Database__Name = 'fleetify_dev'
        dotnet run --project src/Fleetify.Tools --no-build -- migrate --fqdn localhost --agent-host localhost --agent-port 7200 --web-url https://localhost:7100 | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'Migration failed.' }
    }

    $components = @(
        @{ Name = 'Signer';  Project = 'src/Fleetify.Signer';  Delay = 3 },
        @{ Name = 'Gateway'; Project = 'src/Fleetify.Gateway'; Delay = 2 },
        @{ Name = 'Workers'; Project = 'src/Fleetify.Workers'; Delay = 1 },
        @{ Name = 'Web';     Project = 'src/Fleetify.Web';     Delay = 0 }
    )

    foreach ($component in $components) {
        $command = @"
`$Host.UI.RawUI.WindowTitle = 'Fleeto $($component.Name)'
`$env:DOTNET_ENVIRONMENT = 'Development'
`$env:ASPNETCORE_ENVIRONMENT = 'Development'
Set-Location '$repo'
dotnet run --project $($component.Project) --no-build
"@
        Write-Host "==> Starting $($component.Name)" -ForegroundColor Cyan
        Start-Process pwsh -ArgumentList @('-NoExit', '-NoProfile', '-Command', $command) | Out-Null
        Start-Sleep -Seconds $component.Delay
    }
}
finally {
    Pop-Location
}

Write-Host ''
Write-Host 'Fleeto is starting. Open https://localhost:7100' -ForegroundColor Green
Write-Host 'First time: open the setup link printed by setup-dev.ps1 (or run setup-dev.ps1 again for a fresh one).'
