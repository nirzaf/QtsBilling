# QtsBilling PostgreSQL least-privilege roles (QB-14/QB-32).
#
# Provision creates a migration owner and a separate DML-only runtime role. Verify proves the runtime
# role can read application data but cannot create, alter, truncate, trigger, or drop database objects.
# Passwords are accepted as process parameters/environment values, sent to the client without logging,
# and are never written to repository files.

[CmdletBinding()]
param(
    [ValidateSet("Docker", "Psql")]
    [string]$Mode = "Docker",
    [ValidateSet("Both", "Provision", "Verify")]
    [string]$Action = "Both",
    [string]$Container = "qtsbilling-pg",
    [string]$HostName = "localhost",
    [int]$Port = 5432,
    [string]$Database = "qtsbilling",
    [string]$AdminUser = "qtsbilling",
    [string]$AdminPassword = "",
    [string]$MigratorUser = "qts_migrator",
    [string]$MigratorPassword = "",
    [string]$AppUser = "qtsbilling_app",
    [Parameter(Mandatory = $true)]
    [string]$AppPassword
)

$ErrorActionPreference = "Stop"

foreach ($identifier in @($Database, $AdminUser, $MigratorUser, $AppUser)) {
    if ($identifier -notmatch '^[A-Za-z_][A-Za-z0-9_]*$') {
        throw "Invalid PostgreSQL identifier supplied to the role provisioner."
    }
}

if ($Mode -eq "Psql" -and [string]::IsNullOrWhiteSpace($AdminPassword)) {
    throw "AdminPassword is required in Psql mode."
}
if ($Action -in @("Both", "Provision") -and [string]::IsNullOrWhiteSpace($MigratorPassword)) {
    throw "MigratorPassword is required when provisioning roles."
}

function Invoke-DatabaseSql {
    param(
        [Parameter(Mandatory = $true)][string]$TargetDatabase,
        [Parameter(Mandatory = $true)][string]$Username,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$Password,
        [Parameter(Mandatory = $true)][string]$Sql
    )

    if ($Mode -eq "Docker") {
        $output = @(& docker exec --env "PGPASSWORD=$Password" $Container psql --username $Username --dbname $TargetDatabase --no-psqlrc --set=ON_ERROR_STOP=1 --command $Sql 2>&1)
        $exitCode = $LASTEXITCODE
    }
    else {
        $hadPassword = Test-Path Env:PGPASSWORD
        $previousPassword = $env:PGPASSWORD
        $env:PGPASSWORD = $Password
        try {
            $output = @($Sql | & psql --no-psqlrc --host $HostName --port $Port --username $Username --dbname $TargetDatabase --set=ON_ERROR_STOP=1 --file=- 2>&1)
            $exitCode = $LASTEXITCODE
        }
        finally {
            if ($hadPassword) {
                $env:PGPASSWORD = $previousPassword
            }
            else {
                Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
            }
        }
    }

    return [pscustomobject]@{ ExitCode = $exitCode; Output = $output }
}

if ($Action -in @("Both", "Provision")) {
    $escapedMigratorPassword = $MigratorPassword.Replace("'", "''")
    $escapedAppPassword = $AppPassword.Replace("'", "''")
    $provisionSql = @"
DO `$`$
BEGIN
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = '$MigratorUser') THEN
        CREATE ROLE $MigratorUser LOGIN CREATEDB PASSWORD '$escapedMigratorPassword';
    ELSE
        ALTER ROLE $MigratorUser WITH LOGIN CREATEDB PASSWORD '$escapedMigratorPassword';
    END IF;
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = '$AppUser') THEN
        CREATE ROLE $AppUser LOGIN PASSWORD '$escapedAppPassword';
    ELSE
        ALTER ROLE $AppUser WITH LOGIN PASSWORD '$escapedAppPassword';
    END IF;
END
`$`$;

ALTER DATABASE $Database OWNER TO $MigratorUser;
REVOKE ALL ON DATABASE $Database FROM PUBLIC;
GRANT CONNECT, TEMPORARY, CREATE ON DATABASE $Database TO $MigratorUser;
GRANT CONNECT ON DATABASE $Database TO $AppUser;
REVOKE ALL ON SCHEMA public FROM PUBLIC;
GRANT USAGE, CREATE ON SCHEMA public TO $MigratorUser;
GRANT USAGE ON SCHEMA public TO $AppUser;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO $AppUser;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO $AppUser;
GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA public TO $AppUser;
ALTER DEFAULT PRIVILEGES FOR ROLE $MigratorUser IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO $AppUser;
ALTER DEFAULT PRIVILEGES FOR ROLE $MigratorUser IN SCHEMA public GRANT USAGE, SELECT ON SEQUENCES TO $AppUser;
"@

    Write-Output "provisioning migration and runtime roles on '$Database' using $Mode mode"
    $provisionResult = Invoke-DatabaseSql -TargetDatabase $Database -Username $AdminUser -Password $AdminPassword -Sql $provisionSql
    $provisionResult.Output | ForEach-Object { Write-Output $_ }
    if ($provisionResult.ExitCode -ne 0) {
        throw "Role provisioning failed with exit code $($provisionResult.ExitCode)."
    }

}

if ($Action -in @("Both", "Verify")) {
    Write-Output "---- verification 1: runtime role reads business data (must succeed)"
    $readResult = Invoke-DatabaseSql -TargetDatabase $Database -Username $AppUser -Password $AppPassword -Sql "SELECT COUNT(*) FROM products"
    Write-Output "  SELECT COUNT(*) FROM products -> exit $($readResult.ExitCode)"

    $probes = @(
        @{ Name = "CREATE TABLE"; Sql = "CREATE TABLE qts_least_privilege_probe (x int)" },
        @{ Name = "ALTER TABLE"; Sql = "ALTER TABLE products ADD COLUMN qts_least_privilege_probe integer" },
        @{ Name = "TRUNCATE TABLE"; Sql = "TRUNCATE TABLE products" },
        @{ Name = "CREATE TRIGGER"; Sql = "CREATE TRIGGER qts_least_privilege_probe BEFORE UPDATE ON products FOR EACH ROW EXECUTE FUNCTION pg_catalog.suppress_redundant_updates_trigger()" },
        @{ Name = "DROP TABLE"; Sql = "DROP TABLE audit_events" }
    )
    $ddlRefused = $true
    foreach ($probe in $probes) {
        $probeResult = Invoke-DatabaseSql -TargetDatabase $Database -Username $AppUser -Password $AppPassword -Sql $probe.Sql
        $refused = $probeResult.ExitCode -ne 0
        Write-Output "  $($probe.Name) -> exit $($probeResult.ExitCode) (refused: $refused)"
        if (-not $refused) { $ddlRefused = $false }
    }

    if ($readResult.ExitCode -ne 0 -or -not $ddlRefused) {
        Write-Output "== least-privilege verification FAILED =="
        exit 1
    }

    Write-Output "== least-privilege verification PASSED: DML permitted; CREATE, ALTER, TRUNCATE, trigger creation, and DROP refused =="
}
