param(
    [string]$Server = 'localhost',
    [string]$Database = 'opcua-nodeset-editor',
    [string]$AppUser = 'nodeset-editor-app',
    # Drop and recreate the database before applying the EF Core schema. Use
    # this when DbContext column types changed (EF Core's EnsureCreated only
    # creates missing tables; it does not alter existing ones).
    [switch]$Reset,
    # Postgres superuser/admin role these scripts authenticate as. $env:PGPASSWORD
    # must hold its password.
    [string]$AdminUser = 'pgadmin'
)

$ErrorActionPreference = 'Stop'

# Builds NodeSetEditor.DbTool, applies the EF Core schema, then loads the
# Core OPC UA NodeSet from the OPC Foundation latest branch.
#
# Admin user is fixed across all environments; Server and Database vary
# (production / staging / test / local).

$port           = 5432
$coreNodeSetUrl = 'https://raw.githubusercontent.com/OPCFoundation/UA-Nodeset/refs/heads/latest/Schema/Opc.Ua.NodeSet2.xml'
$psql           = 'C:\Program Files\PostgreSQL\18\bin\psql.exe'

if (-not $env:PGPASSWORD) {
    Write-Host 'No password set. Call: $env:PGPASSWORD = <password>' -ForegroundColor Yellow
    exit 1
}

$repoRoot  = Split-Path -Parent $PSScriptRoot
$dbToolDir = Join-Path $repoRoot 'NodeSetEditor.DbTool'

# Prefer encrypted connections, but fall back if the local server has no SSL.
$env:PGSSLMODE = 'prefer'
$connStr = "Server=$Server;Database=$Database;Port=$port;User Id=$adminUser;Password=$env:PGPASSWORD;Ssl Mode=Prefer;"

if ($Reset) {
    Write-Host "Dropping database '$Database' on $Server (Reset=true) ..." -ForegroundColor Yellow
    $dropDbSql = @"
SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '$Database' AND pid <> pg_backend_pid();
DROP DATABASE IF EXISTS "$Database";
"@
    $dropDbSql | & $psql -h $Server -p $port -U $adminUser -d postgres -v ON_ERROR_STOP=1
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

Write-Host "Ensuring database '$Database' exists on $Server ..." -ForegroundColor Cyan
$ensureDbSql = @"
SELECT 'CREATE DATABASE "$Database"'
WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = '$Database')\gexec
"@
$ensureDbSql | & $psql -h $Server -p $port -U $adminUser -d postgres -v ON_ERROR_STOP=1
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Building NodeSetEditor.DbTool..." -ForegroundColor Cyan
dotnet build $dbToolDir -c Release --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Migrating $Server / $Database ..." -ForegroundColor Cyan
dotnet run --project $dbToolDir -c Release --no-build -- migrate "--ConnectionStrings:Postgres=$connStr"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$coreNodeSetPath = Join-Path $env:TEMP 'Opc.Ua.NodeSet2.xml'
Write-Host "Downloading Core NodeSet -> $coreNodeSetPath" -ForegroundColor Cyan
Invoke-WebRequest -Uri $coreNodeSetUrl -OutFile $coreNodeSetPath -UseBasicParsing

Write-Host "Importing Core NodeSet..." -ForegroundColor Cyan
dotnet run --project $dbToolDir -c Release --no-build -- import-file "$coreNodeSetPath" "--ConnectionStrings:Postgres=$connStr"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Applying grants for role '$AppUser' on $Database ..." -ForegroundColor Cyan
$grantSqlPath = Join-Path $PSScriptRoot 'grant_webapp.sql'
& $psql -h $Server -p $port -U $adminUser -d $Database -v "app_user=$AppUser" -v "admin_user=$AdminUser" -f $grantSqlPath
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Done. $Server / $Database is ready." -ForegroundColor Green

# Print the runtime app's connection string. SSL mode follows the rule:
#   localhost (loopback) -> Prefer (local dev Postgres often has no SSL)
#   anything else        -> Require (Azure / staging / prod must use TLS)
$sslMode = if ($Server -in @('localhost', '127.0.0.1', '::1')) { 'Prefer' } else { 'Require' }
$appConnStr = "Server=$Server;Database=$Database;Port=$port;User Id=$AppUser;Password=<APP_PASSWORD>;Ssl Mode=$sslMode;"
Write-Host ""
Write-Host "App connection string (replace <APP_PASSWORD> with the role's password):" -ForegroundColor Cyan
Write-Host "  $appConnStr"
