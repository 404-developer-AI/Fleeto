#Requires -Version 7
<#
.SYNOPSIS
    Builds the Fleeto agent and watchdog for Windows amd64: agent/dist/windows-amd64/fleetify-{agent,watchdog}.exe.

.DESCRIPTION
    The version comes from <Version> in Directory.Build.props, so server and agent share one version number.
    The Steaan release public keys come from FleetifyReleasePublicKeys in Directory.Build.local.props when that file
    exists (development keys written by setup-dev.ps1), or from -ReleasePublicKeys (CI). Public keys only.
    The build is static (CGO_ENABLED=0) and reproducible-friendly (-trimpath, empty build id), as in the Dockerfiles.
    With -Sign it also writes agent/dist/manifest.json with the hashes of both binaries and signs it with the development release
    key of setup-dev.ps1, so a local gateway (appsettings.Development.json) offers them as an agent update (0.2.1).

.PARAMETER Output
    Output directory. Default: agent/dist/windows-amd64.

.PARAMETER ReleasePublicKeys
    Base64 ed25519 public keys separated by ';'. Overrides Directory.Build.local.props.

.PARAMETER Sign
    Write and sign manifest.json in the parent of the output directory with the development release key.

.PARAMETER Version
    Version stamped into the binaries and the manifest. Default: <Version> in Directory.Build.props. Use a higher
    pre-release (0.2.1-dev.2) to test an update against agents built earlier.
#>
[CmdletBinding()]
param(
    [string] $Output,
    [string] $ReleasePublicKeys,
    [switch] $Sign,
    [string] $Version
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
$version = if ($Version) { $Version } else { @($props.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ })[0] }
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
$module = 'github.com/404-developer-AI/Fleeto/agent'
$ldflags = "-s -w -buildid= -X $module/internal/version.Version=$version -X $module/internal/version.ReleasePublicKeys=$ReleasePublicKeys"
$outFiles = @()

$env:CGO_ENABLED = '0'
$env:GOOS = 'windows'
$env:GOARCH = 'amd64'
Push-Location $agentDir
try {
    foreach ($component in 'agent', 'watchdog') {
        $outFile = Join-Path $Output "fleetify-$component.exe"
        & $goExe build -trimpath -buildvcs=false -ldflags $ldflags -o $outFile "./cmd/fleetify-$component"
        if ($LASTEXITCODE -ne 0) { throw "go build of fleetify-$component failed with exit code $LASTEXITCODE." }
        $outFiles += $outFile
    }
}
finally {
    Pop-Location
    Remove-Item Env:CGO_ENABLED, Env:GOOS, Env:GOARCH -ErrorAction SilentlyContinue
}

Write-Host "Version $version"
foreach ($outFile in $outFiles) {
    Write-Host "Built $outFile"
    Write-Host "  SHA-256 $((Get-FileHash -Algorithm SHA256 $outFile).Hash.ToLowerInvariant())"
}

if ($Sign) {
    # The manifest describes <platform>-<architecture>/<file> relative to its own directory, the layout the gateway serves.
    $distDir = Split-Path -Parent (Resolve-Path $Output)
    $key = Join-Path $env:LOCALAPPDATA 'Fleetify/dev/keys/release-signing.key'
    if (-not (Test-Path $key)) { throw "The development release key $key does not exist. Run tools/dev/setup-dev.ps1 first." }
    $manifest = Join-Path $distDir 'manifest.json'
    Push-Location $repoRoot
    try {
        dotnet run --project src/Fleetify.Tools -- release agent-manifest --version $version --agent-binaries $distDir --out $manifest
        if ($LASTEXITCODE -ne 0) { throw "release agent-manifest failed with exit code $LASTEXITCODE." }
        dotnet run --project src/Fleetify.Tools -- release sign --key $key --file $manifest
        if ($LASTEXITCODE -ne 0) { throw "release sign failed with exit code $LASTEXITCODE." }
    }
    finally {
        Pop-Location
    }
    Write-Host "Signed $manifest; a running development gateway picks it up within a minute."
}
