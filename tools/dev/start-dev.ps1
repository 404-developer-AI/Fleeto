#Requires -Version 7
<#
.SYNOPSIS
    Starts a local Fleeto instance: signer, gateway, workers and web, each in its own PowerShell window on Windows, or
    together in this terminal on Linux.

.DESCRIPTION
    Run tools/dev/setup-dev.ps1 once first. The signer starts first because the gateway needs a certificate from it.
    On Windows, close a window (or press Ctrl+C in it) to stop that component; logs appear in each window. On Linux every
    log line is prefixed with its component and Ctrl+C stops all four. The web also writes src/Fleeto.Web/logs/.

    URLs:
      Web UI           https://localhost:7100
      Agent gateway    https://localhost:7200  (agents connect here; mTLS)
      Gateway health   http://localhost:5201/health
      Remote relay     ws://localhost:5202/relay/  (browser side of remote sessions; web points there in Development)

.PARAMETER NoBuild
    Skip the build (use after a successful build).
#>
[CmdletBinding()]
param(
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
# %LOCALAPPDATA% on Windows, ~/.local/share on Linux. The components expand %LOCALAPPDATA% in their development settings
# and do not look for the development secrets outside Windows by themselves, so both are passed on.
$env:LOCALAPPDATA = [Environment]::GetFolderPath('LocalApplicationData')
$secrets = Join-Path $env:LOCALAPPDATA 'Fleeto/dev/secrets'
$env:FLEETO_SECRETS_DIR = $secrets

if (-not (Test-Path (Join-Path $secrets 'root.key'))) {
    throw "No development secrets found in $secrets. Run tools/dev/setup-dev.ps1 first."
}

Push-Location $repo
try {
    if (-not $NoBuild) {
        Write-Host '==> Build' -ForegroundColor Cyan
        dotnet build Fleeto.slnx -v q | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

        Write-Host '==> Apply pending migrations' -ForegroundColor Cyan
        $env:Fleeto__Database__Name = 'fleeto_dev'
        dotnet run --project src/Fleeto.Tools --no-build -- migrate --fqdn localhost --agent-host localhost --agent-port 7200 --web-url https://localhost:7100 | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'Migration failed.' }
    }

    $components = @(
        @{ Name = 'Signer';  Project = 'src/Fleeto.Signer';  Delay = 3 },
        @{ Name = 'Gateway'; Project = 'src/Fleeto.Gateway'; Delay = 2 },
        @{ Name = 'Workers'; Project = 'src/Fleeto.Workers'; Delay = 1 },
        @{ Name = 'Web';     Project = 'src/Fleeto.Web';     Delay = 0 }
    )

    if (-not $IsWindows) {
        $env:DOTNET_ENVIRONMENT = 'Development'
        $env:ASPNETCORE_ENVIRONMENT = 'Development'
        $running = @()
        try {
            foreach ($component in $components) {
                Write-Host "==> Starting $($component.Name)" -ForegroundColor Cyan
                $info = [Diagnostics.ProcessStartInfo]::new('dotnet')
                foreach ($argument in 'run', '--project', $component.Project, '--no-build') { $info.ArgumentList.Add($argument) }
                $info.WorkingDirectory = $repo
                $info.RedirectStandardOutput = $true
                $info.RedirectStandardError = $true
                $process = [Diagnostics.Process]::Start($info)
                $running += [pscustomobject]@{
                    Prefix = '[{0,-7}]' -f $component.Name
                    Process = $process
                    Streams = @($process.StandardOutput, $process.StandardError)
                    Reads = @($process.StandardOutput.ReadLineAsync(), $process.StandardError.ReadLineAsync())
                }
                Start-Sleep -Seconds $component.Delay
            }
            Write-Host ''
            Write-Host 'Fleeto is starting. Open https://localhost:7100 (Ctrl+C stops every component)' -ForegroundColor Green

            # Relay the output of every component until all have exited or Ctrl+C is pressed.
            while ($running | Where-Object { $_.Reads | Where-Object { $_ } }) {
                $idle = $true
                foreach ($entry in $running) {
                    for ($i = 0; $i -lt 2; $i++) {
                        $read = $entry.Reads[$i]
                        if (-not $read -or -not $read.IsCompleted) { continue }
                        $idle = $false
                        if ($null -eq $read.Result) {
                            $entry.Reads[$i] = $null
                            continue
                        }
                        Write-Host "$($entry.Prefix) $($read.Result)"
                        $entry.Reads[$i] = $entry.Streams[$i].ReadLineAsync()
                    }
                }
                if ($idle) { Start-Sleep -Milliseconds 50 }
            }
        }
        finally {
            foreach ($entry in $running) {
                if (-not $entry.Process.HasExited) { $entry.Process.Kill($true) }
            }
        }
        return
    }

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
