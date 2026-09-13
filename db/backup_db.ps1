param(
    [string]$Server   = 'localhost',
    [string]$Database = 'opcua-nodeset-editor',
    # Postgres superuser/admin role these scripts authenticate as. $env:PGPASSWORD
    # must hold its password.
    [string]$AdminUser = 'pgadmin'
)

# Dumps the NodeSet Editor database to db\dump\<Database>-backup.sql. That directory is
# gitignored: a dump holds live user data and must never be committed. Defaults to a local
# server; pass -Server to target a deployed one. Admin role defaults to pgadmin; pass -AdminUser to override.

$pdump = "C:\Program Files\PostgreSQL\18\bin\pg_dump.exe"
$hostname = $Server
$database = $Database
$port = 5432
$dumpDir = Join-Path $PSScriptRoot 'dump'

if (-not $env:PGPASSWORD) {
    Write-Host 'No Password set. Call: $env:PGPASSWORD = <password>'
	exit 1
}

if (-not (Test-Path $dumpDir)) { New-Item -ItemType Directory -Path $dumpDir | Out-Null }
$outFile = Join-Path $dumpDir "$database-backup.sql"

& $pdump -h $hostname -p $port -b -v --no-owner --no-privileges -U $AdminUser -d $database -f $outFile