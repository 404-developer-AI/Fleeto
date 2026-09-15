<#
.SYNOPSIS
    Signs and publishes a Fleeto release that the release workflow left as a draft on GitHub.

.DESCRIPTION
    Runs on a Steaan workstation with the release private key, never in CI or on a VPS (deploy/RELEASING.md):
      1. downloads install.sh, manifest.json and SHA256SUMS from the draft release and checks the hashes;
      2. shows the manifest and checks that it names this version and that install.sh has the hash it lists;
      3. checks that the public key next to the private key is the first key in FLEETIFY_RELEASE_PUBLIC_KEYS, so the
         signatures verify in the install.sh of this release;
      4. signs both files, verifies the signatures, uploads them and, after confirmation, publishes the release.

.EXAMPLE
    pwsh deploy/sign-release.ps1 -Version 0.2.0-alpha.2 -Key C:/Users/christiaan/FleetoKeys/test-release/release-signing.key
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [string] $Key,
    # Defaults to release-signing.pub next to the private key.
    [string] $PublicKey,
    [string] $Repository = '404-developer-AI/Fleeto'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Fail([string] $cause, [string] $next) {
    Write-Host "Error: $cause" -ForegroundColor Red
    if ($next) { Write-Host "       $next" }
    exit 1
}

function Invoke-Checked([string] $what, [scriptblock] $command) {
    & $command
    if ($LASTEXITCODE -ne 0) { Fail "$what failed (exit code $LASTEXITCODE)." 'See the output above.' }
}

if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z]{1,10}(\.[0-9A-Za-z]{1,10}){0,4})?$') {
    Fail "'$Version' is not a version." 'Use the form 0.2.0 or 0.2.0-alpha.2, without the v.'
}
$tag = "v$Version"
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not (Test-Path $Key)) { Fail "The private key $Key does not exist." 'Pass the path of release-signing.key with -Key.' }
if (-not $PublicKey) { $PublicKey = Join-Path (Split-Path -Parent $Key) 'release-signing.pub' }
if (-not (Test-Path $PublicKey)) { Fail "The public key $PublicKey does not exist." 'Pass release-signing.pub with -PublicKey.' }
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { Fail 'The GitHub CLI (gh) is not installed.' 'Install it and run gh auth login.' }

# 1. The draft and its files.
$release = gh release view $tag --repo $Repository --json isDraft,isPrerelease,assets | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) { Fail "Release $tag does not exist on GitHub." 'Wait for the Release workflow of the tag to finish (gh run list --workflow Release).' }
if (-not $release.isDraft) { Fail "Release $tag is already published." 'A published release is not signed again; make a new version instead.' }

$work = Join-Path ([System.IO.Path]::GetTempPath()) "fleeto-release-$Version"
if (Test-Path $work) { Remove-Item -Recurse -Force $work }
New-Item -ItemType Directory -Path $work | Out-Null
Invoke-Checked 'Downloading the release files' {
    gh release download $tag --repo $Repository --dir $work --pattern install.sh --pattern manifest.json --pattern SHA256SUMS
}

$installSh = Join-Path $work 'install.sh'
$manifestPath = Join-Path $work 'manifest.json'
foreach ($line in Get-Content (Join-Path $work 'SHA256SUMS')) {
    if ($line -notmatch '^([0-9a-f]{64})\s+\*?\./releases/[^/]+/(install\.sh|manifest\.json)$') { continue }
    $actual = (Get-FileHash (Join-Path $work $Matches[2]) -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $Matches[1]) { Fail "$($Matches[2]) does not match SHA256SUMS of the workflow." 'Do not sign it: download the draft again or rerun the Release workflow.' }
}

# 2. The manifest.
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
if ($manifest.version -ne $Version) { Fail "The manifest describes version $($manifest.version), not $Version." 'Do not sign it.' }
$installHash = (Get-FileHash $installSh -Algorithm SHA256).Hash.ToLowerInvariant()
if ($manifest.installShSha256 -ne $installHash) { Fail 'install.sh does not have the hash the manifest lists.' 'Do not sign it: rerun the Release workflow.' }
Write-Host ''
Write-Host "Release $tag$(if ($release.isPrerelease) { ' (pre-release)' })" -ForegroundColor Cyan
Write-Host "  rollback mode: $($manifest.rollback)"
foreach ($image in $manifest.images.PSObject.Properties) { Write-Host ("  {0,-8} {1}" -f $image.Name, $image.Value) }
Write-Host '  Compare the digests with the Release workflow log (gh run view <run id> --log) before you continue.'
$answer = Read-Host 'Do the digests match the Release workflow log? [y/N]'
if ($answer -notmatch '^[Yy]') { Fail 'Signing cancelled: the digests were not confirmed.' 'Nothing was signed or uploaded; the release stays a draft.' }

# 3. The key must be the one the builds trust.
$publicKeyValue = (Get-Content $PublicKey -Raw).Trim()
$trusted = (gh variable get FLEETIFY_RELEASE_PUBLIC_KEYS --repo $Repository)
if ($LASTEXITCODE -ne 0) { Fail 'Could not read the repository variable FLEETIFY_RELEASE_PUBLIC_KEYS.' 'Check gh auth status and your access to the repository.' }
if (($trusted.Trim() -split ';')[0].Trim() -ne $publicKeyValue) {
    Fail 'This release key is not the first key in FLEETIFY_RELEASE_PUBLIC_KEYS, so install.sh would reject the signatures.' 'Use the matching release key, or set the variable and build a new release.'
}

# 4. Sign, verify, upload, publish.
Push-Location $repoRoot
try {
    foreach ($file in @($installSh, $manifestPath)) {
        Invoke-Checked "Signing $(Split-Path -Leaf $file)" { dotnet run --project src/Fleetify.Tools -c Release -- release sign --key $Key --file $file }
        Invoke-Checked "Verifying $(Split-Path -Leaf $file)" { dotnet run --project src/Fleetify.Tools -c Release -- release verify --public-key $PublicKey --file $file }
    }
}
finally {
    Pop-Location
}

Invoke-Checked 'Uploading the signatures' { gh release upload $tag --repo $Repository --clobber "$installSh.sig" "$manifestPath.sig" }
Write-Host ''
$answer = Read-Host "Publish $tag now? VPSes see it at their next install.sh run. [y/N]"
if ($answer -notmatch '^[Yy]') {
    Write-Host "Signatures uploaded; $tag stays a draft. Publish later with: gh release edit $tag --repo $Repository --draft=false"
    exit 0
}
Invoke-Checked 'Publishing the release' { gh release edit $tag --repo $Repository --draft=false }
Write-Host "Release $tag is published." -ForegroundColor Green
Write-Host "First install on a VPS: gh release download $tag --repo $Repository --pattern 'install.sh*' (deploy/README.md, First install)."
