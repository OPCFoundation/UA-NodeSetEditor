param(
    [Parameter(Mandatory = $true)]
    [string]$Server,
    [Parameter(Mandatory = $true)]
    [string]$Database,
    [Parameter(Mandatory = $true)]
    [string]$AppUser,
    [SecureString]$AppPassword,
    # Postgres superuser/admin role these scripts authenticate as. $env:PGPASSWORD
    # must hold its password.
    [string]$AdminUser = 'pgadmin'
)

$ErrorActionPreference = 'Stop'

# Creates (or updates the password for) a Postgres login role and grants it the
# CRUD privileges needed to operate the NodeSetEditor app on a single database.
#
# Composition:
#   1. CREATE ROLE "<AppUser>" WITH LOGIN PASSWORD '...'    (or ALTER ROLE if exists)
#   2. Apply grant_webapp.sql against -Database with -v app_user=<AppUser>
#
# The admin role defaults to pgadmin (-AdminUser); $env:PGPASSWORD must hold its password.
# If -AppPassword is not supplied, you'll be prompted for it (no echo).

$port          = 5432
$psql          = 'C:\Program Files\PostgreSQL\18\bin\psql.exe'
$grantSqlPath  = Join-Path $PSScriptRoot 'grant_webapp.sql'

if (-not $env:PGPASSWORD) {
    Write-Host 'No admin password set. Call: $env:PGPASSWORD = <admin password>' -ForegroundColor Yellow
    exit 1
}

if (-not (Test-Path $grantSqlPath)) {
    Write-Host "Missing grant_webapp.sql at $grantSqlPath" -ForegroundColor Red
    exit 1
}

# Prompt for the app password if not supplied. SecureString never appears in
# command history or scrolled-back terminal text.
if (-not $AppPassword) {
    $AppPassword = Read-Host -AsSecureString -Prompt "Password for new role '$AppUser'"
}

# Convert SecureString to plain text at the latest possible moment.
$bstr = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($AppPassword)
try {
    $plainAppPassword = [System.Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
}
finally {
    [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
}

# Escape single quotes for the SQL literal: ' -> ''. Identifier quoting (the
# role name) uses double-quotes and we double those as well to be safe.
$pwLiteral   = $plainAppPassword.Replace("'", "''")
$nameLiteral = $AppUser.Replace('"', '""')

$env:PGSSLMODE = 'prefer'

# 1. Verify that the target database exists; bail early with a clear error if
#    not (we're not in the business of creating databases here).
Write-Host "Checking database '$Database' exists on $Server ..." -ForegroundColor Cyan
$dbExists = & $psql -h $Server -p $port -U $adminUser -d postgres -A -t `
    -c "SELECT 1 FROM pg_database WHERE datname = '$($Database.Replace("'", "''"))'"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if (-not ($dbExists | Where-Object { $_.Trim() -eq '1' })) {
    Write-Host "Database '$Database' does not exist on $Server. Create it first via initialize_db.ps1 or restore_db.ps1." -ForegroundColor Red
    exit 1
}

# 2. CREATE ROLE if missing, ALTER ROLE if it already exists. Both paths leave
#    the role with LOGIN privilege and the supplied password.
Write-Host "Creating or updating role '$AppUser' ..." -ForegroundColor Cyan
$createOrUpdateSql = @"
DO `$do`$
BEGIN
    IF EXISTS (SELECT FROM pg_roles WHERE rolname = '$($AppUser.Replace("'", "''"))') THEN
        EXECUTE 'ALTER ROLE "$nameLiteral" WITH LOGIN PASSWORD ''$pwLiteral''';
    ELSE
        EXECUTE 'CREATE ROLE "$nameLiteral" WITH LOGIN PASSWORD ''$pwLiteral''';
    END IF;
END
`$do`$;
"@
$createOrUpdateSql | & $psql -h $Server -p $port -U $adminUser -d postgres -v ON_ERROR_STOP=1
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# Scrub the plaintext password from PowerShell scope.
$plainAppPassword  = $null
$createOrUpdateSql = $null

# 3. Apply CRUD grants to the target database.
Write-Host "Applying grants for role '$AppUser' on $Database ..." -ForegroundColor Cyan
& $psql -h $Server -p $port -U $adminUser -d $Database -v "app_user=$AppUser" -v "admin_user=$AdminUser" -f $grantSqlPath
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host ""
Write-Host "Done. Role '$AppUser' is ready on $Server / $Database." -ForegroundColor Green

# 4. Print the runtime app's connection string. SSL mode follows the rule:
#    localhost (loopback) -> Prefer; anything else -> Require.
$sslMode = if ($Server -in @('localhost', '127.0.0.1', '::1')) { 'Prefer' } else { 'Require' }
$appConnStr = "Server=$Server;Database=$Database;Port=$port;User Id=$AppUser;Password=<APP_PASSWORD>;Ssl Mode=$sslMode;"
Write-Host ""
Write-Host "App connection string (substitute the password you just set for <APP_PASSWORD>):" -ForegroundColor Cyan
Write-Host "  $appConnStr"
