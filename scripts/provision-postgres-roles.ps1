# QtsBilling PostgreSQL least-privilege application role (QB-14).
#
# Creates (idempotently) a DML-only application role and default privileges for future tables, then proves
# the boundary inside the disposable verification step: the role can read business data but cannot run DDL.
# The password is taken as a parameter, never printed and never stored in the repository - supply it from
# your deployment secret store.
#
# Example:
#   powershell -File scripts/provision-postgres-roles.ps1 -AppPassword (Read-Host -AsSecureString | ConvertFrom-SecureString -AsPlainText)

[CmdletBinding()]
param(
    [string]$Container = "qtsbilling-pg",
    [string]$Database = "qtsbilling",
    [string]$OwnerUser = "qtsbilling",
    [string]$AppUser = "qtsbilling_app",
    [Parameter(Mandatory = $true)]
    [string]$AppPassword
)

$ErrorActionPreference = "Stop"

$escapedPassword = $AppPassword.Replace("'", "''")

$sql = @"
DO `$`$
BEGIN
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = '$AppUser') THEN
        CREATE ROLE $AppUser LOGIN PASSWORD '$escapedPassword';
    ELSE
        ALTER ROLE $AppUser WITH LOGIN PASSWORD '$escapedPassword';
    END IF;
END
`$`$;

-- The schema and connection surface are not public.
REVOKE ALL ON DATABASE $Database FROM PUBLIC;
REVOKE ALL ON SCHEMA public FROM PUBLIC;
GRANT CONNECT ON DATABASE $Database TO $AppUser;
GRANT USAGE ON SCHEMA public TO $AppUser;

-- DML only: no CREATE, no ALTER, no DROP, no ownership.
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO $AppUser;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO $AppUser;
GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA public TO $AppUser;

ALTER DEFAULT PRIVILEGES FOR ROLE $OwnerUser IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO $AppUser;
ALTER DEFAULT PRIVILEGES FOR ROLE $OwnerUser IN SCHEMA public GRANT USAGE, SELECT ON SEQUENCES TO $AppUser;
ALTER DEFAULT PRIVILEGES FOR ROLE $OwnerUser IN SCHEMA public GRANT EXECUTE ON FUNCTIONS TO $AppUser;
"@

Write-Output "provisioning role '$AppUser' on '$Database' (container $Container)"

docker exec $Container psql --username $OwnerUser --dbname $Database --set=ON_ERROR_STOP=1 --command $sql 2>&1 | ForEach-Object { $_ }

if ($LASTEXITCODE -ne 0) {
    throw "Role provisioning failed with exit code $LASTEXITCODE."
}

Write-Output "---- verification 1: app role reads business data (must succeed)"
$readResult = docker exec --env "PGPASSWORD=$AppPassword" $Container psql --username $AppUser --dbname $Database --tuples-only --no-align --command "SELECT COUNT(*) FROM products"
$readExit = $LASTEXITCODE
Write-Output "  SELECT COUNT(*) FROM products -> exit $readExit, value $("$readResult".Trim())"

Write-Output "---- verification 2: app role runs DDL (must be refused)"
$previousPreference = $ErrorActionPreference
$ErrorActionPreference = "Continue"
$ddlOutput = docker exec --env "PGPASSWORD=$AppPassword" $Container psql --username $AppUser --dbname $Database --command "CREATE TABLE qts_least_privilege_probe (x int)" 2>&1
$ddlExit = $LASTEXITCODE
$ddlRefused = $ddlExit -ne 0
$ddlOutput | Select-Object -First 2 | ForEach-Object { "  $_" }

Write-Output "---- verification 3: app role drops a table (must be refused)"
$dropOutput = docker exec --env "PGPASSWORD=$AppPassword" $Container psql --username $AppUser --dbname $Database --command "DROP TABLE audit_events" 2>&1
$dropExit = $LASTEXITCODE
$dropRefused = $dropExit -ne 0
$ErrorActionPreference = $previousPreference
Write-Output "  DROP TABLE audit_events -> exit $dropExit (refused: $dropRefused)"

if ($readExit -ne 0 -or -not $ddlRefused -or -not $dropRefused) {
    Write-Output "== least-privilege verification FAILED =="
    exit 1
}

Write-Output "== least-privilege verification PASSED: DML permitted, DDL refused, role is not table owner =="
