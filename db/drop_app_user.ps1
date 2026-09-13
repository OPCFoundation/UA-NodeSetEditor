param(
    [Parameter(Mandatory = $true)]
    [string]$Server,
    [Parameter(Mandatory = $true)]
    [string]$AppUser,
    [switch]$WhatIf,
    # Postgres superuser/admin role these scripts authenticate as. $env:PGPASSWORD
    # must hold its password.
    [string]$AdminUser = 'pgadmin'
)

$ErrorActionPreference = 'Stop'

# Fully removes an app role from a Postgres cluster:
#   1. Strips all default-privilege rules and existing grants in EVERY database
#      (DROP OWNED BY is per-database, so we have to iterate).
#   2. Drops the role itself at the cluster level.
#
# A role can't be dropped while it still holds grants in any database, so step 1
# is required before step 2 succeeds. Admin role defaults to pgadmin (-AdminUser); pass
# -Server and -AppUser explicitly. Use -WhatIf to preview without making changes.

$port         = 5432
$psql         = 'C:\Program Files\PostgreSQL\18\bin\psql.exe'
$revokeSqlPath = Join-Path $PSScriptRoot 'revoke_webapp.sql'

if (-not $env:PGPASSWORD) {
    Write-Host 'No password set. Call: $env:PGPASSWORD = <password>' -ForegroundColor Yellow
    exit 1
}

if (-not (Test-Path $revokeSqlPath)) {
    Write-Host "Missing revoke_webapp.sql at $revokeSqlPath" -ForegroundColor Red
    exit 1
}

$env:PGSSLMODE = 'prefer'

# 1. Enumerate every non-template database on the server.
Write-Host "Enumerating databases on $Server ..." -ForegroundColor Cyan
$dbs = & $psql -h $Server -p $port -U $adminUser -d postgres `
    -A -t -c 'SELECT datname FROM pg_database WHERE NOT datistemplate ORDER BY datname'
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$dbs = $dbs | Where-Object { $_ -and $_.Trim() } | ForEach-Object { $_.Trim() }

Write-Host ("Found {0} database(s): {1}" -f $dbs.Count, ($dbs -join ', ')) -ForegroundColor Cyan

if ($WhatIf) {
    Write-Host ""
    Write-Host "[WhatIf] Would run revoke_webapp.sql with -v app_user='$AppUser' against:" -ForegroundColor Yellow
    foreach ($db in $dbs) { Write-Host "  - $db" }
    Write-Host "[WhatIf] Then would: DROP ROLE `"$AppUser`""  -ForegroundColor Yellow
    exit 0
}

# 2. Run revoke_webapp.sql against each database. Errors continue (the role
#    might not have grants in some DBs -- that is fine and expected).
foreach ($db in $dbs) {
    Write-Host ""
    Write-Host "Revoking from $Server / $db ..." -ForegroundColor Cyan
    & $psql -h $Server -p $port -U $adminUser -d $db -v "app_user=$AppUser" -v "admin_user=$AdminUser" -f $revokeSqlPath
    if ($LASTEXITCODE -ne 0) {
        Write-Host "  (revoke returned non-zero on '$db' -- continuing)" -ForegroundColor Yellow
    }
}

# 3. Drop the role itself. Quote the identifier so role names with hyphens or
#    mixed case are handled correctly.
Write-Host ""
Write-Host "Dropping role '$AppUser' ..." -ForegroundColor Cyan
$dropSql = "DROP ROLE IF EXISTS ""$AppUser"";"
$dropSql | & $psql -h $Server -p $port -U $adminUser -d postgres -v ON_ERROR_STOP=1
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host ""
Write-Host "Done. Role '$AppUser' has been removed from $Server." -ForegroundColor Green
