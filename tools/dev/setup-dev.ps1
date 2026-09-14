#Requires -Version 7
<#
.SYNOPSIS
    Sets up a local Fleeto development instance on Windows without Docker.

.DESCRIPTION
    Idempotent; safe to run again after pulling new code (it applies new migrations).
      1. Creates %LOCALAPPDATA%\Fleetify\dev with secrets (root key, signer key, database passwords) readable only
         by the current user. Nothing is written to the repository except Directory.Build.local.props, which holds
         public keys only and is gitignored.
      2. Creates the PostgreSQL roles and the database (needs the PostgreSQL superuser once).
      3. Creates development license and release signing keys and a development license for the local FQDN.
      4. Builds the solution and runs fleetify-tool migrate, which prints the first-admin setup link.

.EXAMPLE
    pwsh tools/dev/setup-dev.ps1
    pwsh tools/dev/setup-dev.ps1 -PgSuperPassword postgres
#>
[CmdletBinding()]
param(
    [string]$PgHost = 'localhost',
    [int]$PgPort = 5432,
    [string]$PgSuperUser = 'postgres',
    [string]$PgSuperPassword,
    [string]$Database = 'fleetify_dev',
    [string]$Fqdn = 'localhost',
    [int]$WebPort = 7100,
    [int]$AgentPort = 7200
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$devRoot = Join-Path $env:LOCALAPPDATA 'Fleetify\dev'
$secrets = Join-Path $devRoot 'secrets'
$keys = Join-Path $devRoot 'keys'

function Write-Step([string]$message) { Write-Host "==> $message" -ForegroundColor Cyan }
function Write-Ok([string]$message) { Write-Host "    $message" -ForegroundColor Green }

function New-SecretFile([string]$path, [scriptblock]$value) {
    if (-not (Test-Path $path)) {
        Set-Content -Path $path -Value (& $value) -NoNewline -Encoding ascii
        Write-Ok "created $(Split-Path $path -Leaf)"
    }
}

function Invoke-Psql([string]$db, [string]$sql, [switch]$Scalar) {
    $arguments = @('-h', $PgHost, '-p', $PgPort, '-U', $PgSuperUser, '-d', $db, '-v', 'ON_ERROR_STOP=1', '-X', '-q')
    if ($Scalar) { $arguments += @('-tA') }
    $output = $sql | & $script:psql @arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw "psql failed: $output" }
    return $output
}

# ---------------------------------------------------------------------------------------------------------------
Write-Step "Secrets in $secrets"
New-Item -ItemType Directory -Force $secrets, $keys | Out-Null
# Only the current user may read the development secrets.
icacls $devRoot /inheritance:r /grant:r "$($env:USERNAME):(OI)(CI)F" | Out-Null

New-SecretFile (Join-Path $secrets 'root.key') { [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)) }
New-SecretFile (Join-Path $secrets 'signer.key') { [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)) }
$roles = [ordered]@{
    fleetify_migrator = 'db-migrator.password'
    fleetify_web      = 'db-web.password'
    fleetify_gateway  = 'db-gateway.password'
    fleetify_signer   = 'db-signer.password'
    fleetify_workers  = 'db-workers.password'
    fleetify_backup   = 'db-backup.password'
}
foreach ($file in $roles.Values) {
    New-SecretFile (Join-Path $secrets $file) { [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(24)).ToLowerInvariant() }
}

# ---------------------------------------------------------------------------------------------------------------
Write-Step "PostgreSQL roles and database '$Database'"
$script:psql = (Get-Command psql -ErrorAction SilentlyContinue).Source
if (-not $script:psql) {
    $script:psql = Get-ChildItem 'C:\Program Files\PostgreSQL\*\bin\psql.exe' -ErrorAction SilentlyContinue |
        Sort-Object { [int]($_.Directory.Parent.Name -replace '\D', '') } -Descending |
        Select-Object -First 1 -ExpandProperty FullName
}
if (-not $script:psql) { throw 'psql.exe was not found. Install PostgreSQL 17 or add its bin folder to PATH.' }

if (-not $PgSuperPassword) {
    $PgSuperPassword = $env:PGPASSWORD
}
if (-not $PgSuperPassword) {
    $secure = Read-Host "Password for PostgreSQL user '$PgSuperUser'" -AsSecureString
    $PgSuperPassword = [Net.NetworkCredential]::new('', $secure).Password
}
$env:PGPASSWORD = $PgSuperPassword

$roleSql = foreach ($role in $roles.Keys) {
    $password = (Get-Content (Join-Path $secrets $roles[$role]) -Raw).Trim()
    "DO `$`$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '$role') THEN CREATE ROLE $role LOGIN PASSWORD '$password'; ELSE ALTER ROLE $role LOGIN PASSWORD '$password'; END IF; END `$`$;"
}
Invoke-Psql 'postgres' ($roleSql -join "`n") | Out-Null
Write-Ok 'roles are up to date'

$exists = Invoke-Psql 'postgres' "SELECT 1 FROM pg_database WHERE datname = '$Database';" -Scalar
if (-not $exists) {
    Invoke-Psql 'postgres' "CREATE DATABASE $Database OWNER fleetify_migrator ENCODING 'UTF8';" | Out-Null
    Write-Ok "created database $Database"
}

$appRoles = ($roles.Keys | Where-Object { $_ -ne 'fleetify_migrator' }) -join ', '
Invoke-Psql $Database @"
ALTER SCHEMA public OWNER TO fleetify_migrator;
REVOKE ALL ON DATABASE $Database FROM PUBLIC;
GRANT CONNECT ON DATABASE $Database TO fleetify_migrator, $appRoles;
GRANT pg_read_all_data TO fleetify_backup;
DO `$`$ BEGIN
  IF EXISTS (SELECT 1 FROM pg_available_extensions WHERE name = 'timescaledb') THEN
    CREATE EXTENSION IF NOT EXISTS timescaledb;
  END IF;
END `$`$;
"@ | Out-Null
$timescale = Invoke-Psql $Database "SELECT extversion FROM pg_extension WHERE extname = 'timescaledb';" -Scalar
if ($timescale) { Write-Ok "TimescaleDB $timescale enabled" } else { Write-Ok 'TimescaleDB not installed: check results use a plain table (fine for development)' }
Remove-Item Env:PGPASSWORD

# ---------------------------------------------------------------------------------------------------------------
Write-Step "Development signing keys in $keys"
Push-Location $repo
try {
    $tool = @('run', '--project', 'src/Fleetify.Tools', '--')
    if (-not (Test-Path (Join-Path $keys 'license-signing.key'))) {
        dotnet @tool license keygen --out $keys | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'license keygen failed' }
    }
    if (-not (Test-Path (Join-Path $keys 'release-signing.key'))) {
        dotnet @tool release keygen --out $keys | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'release keygen failed' }
    }

    $licensePub = (Get-Content (Join-Path $keys 'license-signing.pub') -Raw).Trim()
    $releasePub = (Get-Content (Join-Path $keys 'release-signing.pub') -Raw).Trim()
    @"
<Project>
  <!-- Written by tools/dev/setup-dev.ps1. Development PUBLIC keys only; gitignored. -->
  <PropertyGroup>
    <FleetifyLicensePublicKeys>$licensePub</FleetifyLicensePublicKeys>
    <FleetifyReleasePublicKeys>$releasePub</FleetifyReleasePublicKeys>
  </PropertyGroup>
</Project>
"@ | Set-Content -Path (Join-Path $repo 'Directory.Build.local.props') -Encoding utf8
    Write-Ok 'Directory.Build.local.props written'

    $license = Join-Path $devRoot 'dev-license.txt'
    if (-not (Test-Path $license)) {
        dotnet @tool license sign --key (Join-Path $keys 'license-signing.key') --customer 'Local development' `
            --fqdn $Fqdn --count 25 --expires (Get-Date).AddYears(1).ToString('yyyy-MM-dd') --out $license | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'license sign failed' }
    }

    # -----------------------------------------------------------------------------------------------------------
    Write-Step 'Build'
    dotnet build Fleetify.slnx -v q | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'build failed' }

    Write-Step 'Migrate and initialise the instance'
    $env:Fleetify__Database__Name = $Database
    $env:Fleetify__Database__Host = $PgHost
    $env:Fleetify__Database__Port = "$PgPort"
    dotnet run --project src/Fleetify.Tools --no-build -- migrate --fqdn $Fqdn --agent-host $Fqdn --agent-port $AgentPort --web-url "https://$($Fqdn):$WebPort" | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'migrate failed' }
}
finally {
    Pop-Location
}

Write-Host ''
Write-Host 'Done. Next steps:' -ForegroundColor Cyan
Write-Host '  1. Start the server:      pwsh tools/dev/start-dev.ps1'
Write-Host '  2. Open the setup link printed above and create the first admin.'
Write-Host "  3. Load the development license from $devRoot\dev-license.txt in Settings, Licensing."
Write-Host '  4. Build the agent:       pwsh tools/dev/build-agent.ps1'
