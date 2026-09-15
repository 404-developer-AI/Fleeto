#Requires -Version 7
<#
.SYNOPSIS
    Sets up a local Fleeto development instance on Windows without Docker.

.DESCRIPTION
    Idempotent; safe to run again after pulling new code (it applies new migrations). A setup from before the internal rename
    to Fleeto (0.2.1) is moved: %LOCALAPPDATA%\Fleetify\dev becomes %LOCALAPPDATA%\Fleeto\dev, and the database fleetify_dev
    and the fleetify_* roles are renamed, so the local instance keeps its data and keys.
      1. Creates %LOCALAPPDATA%\Fleeto\dev with secrets (root key, signer key, database passwords) readable only
         by the current user. Nothing is written to the repository except Directory.Build.local.props, which holds
         public keys only and is gitignored.
      2. Creates the PostgreSQL roles and the database (needs the PostgreSQL superuser once).
      3. Creates development license and release signing keys and a development license for the local FQDN.
      4. Builds the solution and runs fleeto-tool migrate, which prints the first-admin setup link.

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
    [string]$Database = 'fleeto_dev',
    [string]$Fqdn = 'localhost',
    [int]$WebPort = 7100,
    [int]$AgentPort = 7200
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$devRoot = Join-Path $env:LOCALAPPDATA 'Fleeto\dev'
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
# A development setup from before the rename to Fleeto (0.2.1) keeps its secrets and keys: the folder moves.
$legacyDevRoot = Join-Path $env:LOCALAPPDATA 'Fleetify\dev'
if ((Test-Path $legacyDevRoot) -and -not (Test-Path $devRoot)) {
    Write-Step "Moving $legacyDevRoot to $devRoot"
    New-Item -ItemType Directory -Force (Split-Path $devRoot) | Out-Null
    Move-Item -Path $legacyDevRoot -Destination $devRoot
    $legacyParent = Split-Path $legacyDevRoot
    if (-not (Get-ChildItem $legacyParent -Force -ErrorAction SilentlyContinue)) { Remove-Item $legacyParent }
    Write-Ok 'secrets and development keys moved'
}

# ---------------------------------------------------------------------------------------------------------------
Write-Step "Secrets in $secrets"
New-Item -ItemType Directory -Force $secrets, $keys | Out-Null
# Only the current user may read the development secrets.
icacls $devRoot /inheritance:r /grant:r "$($env:USERNAME):(OI)(CI)F" | Out-Null

New-SecretFile (Join-Path $secrets 'root.key') { [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)) }
New-SecretFile (Join-Path $secrets 'signer.key') { [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)) }
$roles = [ordered]@{
    fleeto_migrator = 'db-migrator.password'
    fleeto_web      = 'db-web.password'
    fleeto_gateway  = 'db-gateway.password'
    fleeto_signer   = 'db-signer.password'
    fleeto_workers  = 'db-workers.password'
    fleeto_backup   = 'db-backup.password'
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

# Roles and database from before the rename to Fleeto (0.2.1) are renamed; their passwords are set again below. The migrations then
# rename the functions and triggers (migration RenameToFleeto).
$legacyRoles = @(Invoke-Psql 'postgres' "SELECT rolname FROM pg_roles WHERE rolname IN ('fleetify_migrator', 'fleetify_web', 'fleetify_gateway', 'fleetify_signer', 'fleetify_workers', 'fleetify_backup');" -Scalar | Where-Object { $_ })
foreach ($legacyRole in $legacyRoles) {
    $newRole = 'fleeto_' + $legacyRole.Substring('fleetify_'.Length)
    if (Invoke-Psql 'postgres' "SELECT 1 FROM pg_roles WHERE rolname = '$newRole';" -Scalar) {
        Write-Warning "Both $legacyRole and $newRole exist; $legacyRole is left alone. Drop it when nothing uses it."
        continue
    }
    Invoke-Psql 'postgres' "ALTER ROLE $legacyRole RENAME TO $newRole;" | Out-Null
    Write-Ok "renamed role $legacyRole to $newRole"
}
$legacyDatabase = $Database -replace '^fleeto', 'fleetify'
if ($legacyDatabase -ne $Database -and
    (Invoke-Psql 'postgres' "SELECT 1 FROM pg_database WHERE datname = '$legacyDatabase';" -Scalar) -and
    -not (Invoke-Psql 'postgres' "SELECT 1 FROM pg_database WHERE datname = '$Database';" -Scalar)) {
    Invoke-Psql 'postgres' "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '$legacyDatabase';" | Out-Null
    Invoke-Psql 'postgres' "ALTER DATABASE $legacyDatabase RENAME TO $Database;" | Out-Null
    Write-Ok "renamed database $legacyDatabase to $Database"
}

$roleSql = foreach ($role in $roles.Keys) {
    $password = (Get-Content (Join-Path $secrets $roles[$role]) -Raw).Trim()
    "DO `$`$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '$role') THEN CREATE ROLE $role LOGIN PASSWORD '$password'; ELSE ALTER ROLE $role LOGIN PASSWORD '$password'; END IF; END `$`$;"
}
Invoke-Psql 'postgres' ($roleSql -join "`n") | Out-Null
Write-Ok 'roles are up to date'

$exists = Invoke-Psql 'postgres' "SELECT 1 FROM pg_database WHERE datname = '$Database';" -Scalar
if (-not $exists) {
    Invoke-Psql 'postgres' "CREATE DATABASE $Database OWNER fleeto_migrator ENCODING 'UTF8';" | Out-Null
    Write-Ok "created database $Database"
}

$appRoles = ($roles.Keys | Where-Object { $_ -ne 'fleeto_migrator' }) -join ', '
Invoke-Psql $Database @"
ALTER SCHEMA public OWNER TO fleeto_migrator;
REVOKE ALL ON DATABASE $Database FROM PUBLIC;
GRANT CONNECT ON DATABASE $Database TO fleeto_migrator, $appRoles;
GRANT pg_read_all_data TO fleeto_backup;
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
    $tool = @('run', '--project', 'src/Fleeto.Tools', '--')
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
    <FleetoLicensePublicKeys>$licensePub</FleetoLicensePublicKeys>
    <FleetoReleasePublicKeys>$releasePub</FleetoReleasePublicKeys>
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
    dotnet build Fleeto.slnx -v q | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'build failed' }

    Write-Step 'Migrate and initialise the instance'
    $env:Fleeto__Database__Name = $Database
    $env:Fleeto__Database__Host = $PgHost
    $env:Fleeto__Database__Port = "$PgPort"
    dotnet run --project src/Fleeto.Tools --no-build -- migrate --fqdn $Fqdn --agent-host $Fqdn --agent-port $AgentPort --web-url "https://$($Fqdn):$WebPort" | Out-Host
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
