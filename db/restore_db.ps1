param(
    [string]$Server     = 'localhost',
    [string]$Database   = 'opcua-nodeset-editor-staging',
    [string]$BackupFile = '',
    [string]$AppUser    = 'nodeset-editor-app',
    # Postgres superuser/admin role these scripts authenticate as. $env:PGPASSWORD
    # must hold its password.
    [string]$AdminUser = 'pgadmin'
)

$ErrorActionPreference = 'Stop'

# Creates the target database if it doesn't exist, then restores a pg_dump
# SQL file (produced by .\backup_db.ps1) into it.
#
# Defaults assume a local server; pass -Server / -Database to target a deployed
# one. Admin role defaults to pgadmin. -BackupFile defaults to the file
# backup_db.ps1 writes under db\dump (gitignored, since a dump holds live user data).

$psql      = 'C:\Program Files\PostgreSQL\18\bin\psql.exe'
$port      = 5432

if (-not $BackupFile) {
    $BackupFile = Join-Path $PSScriptRoot 'dump\opcua-nodeset-editor-backup.sql'
}

if (-not $env:PGPASSWORD) {
    Write-Host 'No password set. Call: $env:PGPASSWORD = <password>' -ForegroundColor Yellow
    exit 1
}

if (-not (Test-Path $BackupFile)) {
    Write-Host "Backup file not found: $BackupFile" -ForegroundColor Red
    Write-Host "Run .\backup_db.ps1 first to produce it." -ForegroundColor Yellow
    exit 1
}

# Prefer encrypted connections; falls back if the target has no SSL.
$env:PGSSLMODE = 'prefer'

Write-Host "Ensuring database '$Database' exists on $Server ..." -ForegroundColor Cyan
$ensureDbSql = @"
SELECT 'CREATE DATABASE "$Database"'
WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = '$Database')\gexec
"@
$ensureDbSql | & $psql -h $Server -p $port -U $adminUser -d postgres -v ON_ERROR_STOP=1
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Restoring '$BackupFile' into $Server / $Database ..." -ForegroundColor Cyan
& $psql -h $Server -p $port -U $adminUser -d $Database -v ON_ERROR_STOP=1 -f $BackupFile
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Applying grants for role '$AppUser' ..." -ForegroundColor Cyan
$grantSqlPath = Join-Path $PSScriptRoot 'grant_webapp.sql'
& $psql -h $Server -p $port -U $adminUser -d $Database -v "app_user=$AppUser" -v "admin_user=$AdminUser" -f $grantSqlPath
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Done. $Server / $Database restored from $BackupFile." -ForegroundColor Green

# Print the runtime app's connection string. SSL mode follows the rule:
#   localhost (loopback) -> Prefer (local dev Postgres often has no SSL)
#   anything else        -> Require (Azure / staging / prod must use TLS)
$sslMode = if ($Server -in @('localhost', '127.0.0.1', '::1')) { 'Prefer' } else { 'Require' }
$appConnStr = "Server=$Server;Database=$Database;Port=$port;User Id=$AppUser;Password=<APP_PASSWORD>;Ssl Mode=$sslMode;"
Write-Host ""
Write-Host "App connection string (replace <APP_PASSWORD> with the role's password):" -ForegroundColor Cyan
Write-Host "  $appConnStr"
