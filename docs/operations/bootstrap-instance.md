# Initialising a new instance (operator bootstrap)

This runbook creates the first owner, business, location, chart of accounts and open accounting period on a
migrated, empty QTS Billing database. It is an operator command. The web application has no setup route, and the
development seeder never runs in production.

## Before you start

- The database is PostgreSQL 18, migrated to the head schema, and holds no businesses or users.
- The host is configured with the production profile: the reviewed signing certificate, the data-protection
  path and the connection string (`QTS_POSTGRES_CONNECTION`). The command loads the same configuration as the service.
- Choose an instance identity: a GUID you keep with the deployment record, for example from `New-Guid` in PowerShell.
  Running the bootstrap again with the same identity reports `AlreadyInitialised`; a different identity reports
  `InstanceMismatch`.
- Decide the opening mode:
  - `NewBusiness`: the business starts with zero balances. Pass `--acknowledge-zero-openings` to confirm this.
  - `PendingNativeOpening`: opening balances come from an approved native package (task 12). Posting waits for it.

## Run the command

Open an interactive terminal on the server. The command reads the owner password from a hidden prompt and never
accepts it on the command line.

```bash
dotnet QtsWebApp.dll --bootstrap-instance --instance-id <guid> --owner-email <email> --business-name "<business>" --location-name "<location>" --location-code <code> --opening-mode NewBusiness --acknowledge-zero-openings
```

Optional options: `--legal-name`, `--base-currency` (default `QAR`), `--time-zone` (default `Asia/Qatar`),
`--settlement-scale` (`0`, `2`, `3` or `4`; default `2`), `--fiscal-year-start-month` (1–12; default `1`) and
`--approved-opening-manifest-hash`.

## Exit codes

| Code | Meaning | Changes made |
|---|---|---|
| 0 | Initialised | Owner, business, location, chart, settings, period, marker and audit event were written in one transaction. |
| 2 | AlreadyInitialised | None. |
| 3 | InstanceMismatch | None. |
| 4 | NotEmpty (businesses or users exist without a marker) | None. |
| 5 | Invalid request (demo values, bad currency, time zone, scale, or unacknowledged zero openings) | None. |
| 6 | Schema not at head | None. Apply the migrations first. |
| 1 | Missing terminal, bad options, or mismatched passwords | None. |

An interrupted run rolls back completely, so it can be run again with the same arguments.

## After initialisation

The bootstrap is closed permanently. Sign in as the owner with the password chosen at the terminal. Further users
are added through the administration screens (task 07), not by running the command again.
