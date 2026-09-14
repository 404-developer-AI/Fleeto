#Requires -Version 7
<#
.SYNOPSIS
    Builds the Fleeto agent for Windows amd64: agent/dist/windows-amd64/fleetify-agent.exe.

.DESCRIPTION
    The version comes from <Version> in Directory.Build.props, so server and agent share one version number.
    The Steaan release public keys come from FleetifyReleasePublicKeys in Directory.Build.local.props when that file
    exists (development keys written by setup-dev.ps1), or from -ReleasePublicKeys (CI). Public keys only.
    The build is static (CGO_ENABLED=0) and reproducible-friendly (-trimpath, no build id path leaks).

.PARAMETER Output
    Output directory. Default: agent/dist/windows-amd64.

.PARAMETER ReleasePublicKeys
    Base64 ed25519 public keys separated by ';'. Overrides Directory.Build.local.props.
#>
[CmdletBinding()]
param(
    [string] $Output,
    [string] $ReleasePublicKeys
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '../..')
$agentDir = Join-Path $repoRoot 'agent'
if (-not $Output) { $Output = Join-Path $agentDir 'dist/windows-amd64' }

$go = Get-Command go -ErrorAction SilentlyContinue
if (-not $go) {
    $candidate = 'C:\Program Files\Go\bin\go.exe'
    if (-not (Test-Path $candidate)) { throw 'Go was not found. Install Go 1.27 or add it to PATH, then run this script again.' }
    $go = Get-Item $candidate
}
$goExe = if ($go -is [System.Management.Automation.CommandInfo]) { $go.Source } else { $go.FullName }

[xml] $props = Get-Content (Join-Path $repoRoot 'Directory.Build.props') -Raw
$version = @($props.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ })[0]
if (-not $version) { throw 'No <Version> found in Directory.Build.props.' }
if ($version -notmatch '^\d+\.\d+\.\d+([-+][0-9A-Za-z.-]+)?$') { throw "The version '$version' in Directory.Build.props is not a semantic version." }

if (-not $ReleasePublicKeys) {
    $localProps = Join-Path $repoRoot 'Directory.Build.local.props'
    if (Test-Path $localProps) {
        [xml] $local = Get-Content $localProps -Raw
        $ReleasePublicKeys = @($local.Project.PropertyGroup | ForEach-Object { $_.FleetifyReleasePublicKeys } | Where-Object { $_ })[0]
    }
}
$ReleasePublicKeys = if ($ReleasePublicKeys) { $ReleasePublicKeys.Trim() } else { '' }
if ($ReleasePublicKeys -and $ReleasePublicKeys -notmatch '^[A-Za-z0-9+/=;]+$') {
    throw 'FleetifyReleasePublicKeys must be base64 keys separated by semicolons.'
}
if (-not $ReleasePublicKeys) { Write-Warning 'No release public keys found: building a development agent without them.' }

New-Item -ItemType Directory -Force -Path $Output | Out-Null
$outFile = Join-Path $Output 'fleetify-agent.exe'
$module = 'github.com/404-developer-AI/Fleeto/agent'
$ldflags = "-s -w -X $module/internal/version.Version=$version -X $module/internal/version.ReleasePublicKeys=$ReleasePublicKeys"

$env:CGO_ENABLED = '0'
$env:GOOS = 'windows'
$env:GOARCH = 'amd64'
Push-Location $agentDir
try {
    & $goExe build -trimpath -ldflags $ldflags -o $outFile ./cmd/fleetify-agent
    if ($LASTEXITCODE -ne 0) { throw "go build failed with exit code $LASTEXITCODE." }
}
finally {
    Pop-Location
    Remove-Item Env:CGO_ENABLED, Env:GOOS, Env:GOARCH -ErrorAction SilentlyContinue
}

$hash = (Get-FileHash -Algorithm SHA256 $outFile).Hash.ToLowerInvariant()
Write-Host "Built $outFile"
Write-Host "Version $version"
Write-Host "SHA-256 $hash"
