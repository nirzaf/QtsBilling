# QtsBilling server configuration, security operations and recovery

_Last reviewed: 2026-10-19 (QB-14). This document is part of the migration deliverables: it covers the
server host (`QtsWebApp`), PostgreSQL 18, key rotation, backup/restore and the device-local SQLite policy.
It deliberately contains **no secrets** — configuration values live in the deployment environment or the
secret store, never in the repository._

## 1. Runtime configuration

| Setting | Required | Purpose |
|---|---|---|
| `QTS_POSTGRES_CONNECTION` | yes | Npgsql connection string for the authoritative database. Use the least-privilege application role in production (see §3). |
| `ASPNETCORE_ENVIRONMENT` | yes | `Production` in deployed environments. Development seeds the demo owner and returns reset tickets in responses. |
| `QTS_DATAPROTECTION_PATH` | production: yes | Directory for the ASP.NET Core Data Protection key ring (session cookies, reset tickets, token providers). Use a persistent volume, **not** the repository and not the database. |
| `QTS_OPENIDDICT_*` | QB-22 | OIDC/PKCE device-grant configuration; introduced with the sync/device work, not used by the web host today. |

Development defaults (documented fixtures only): PostgreSQL in the `qtsbilling-pg` container on
`localhost:5433`, database/user/password `qtsbilling`, demo owner `owner@qtsbilling.local` with the seeded
development password. These values are allowlisted by the static scan because they are intentionally public
development fixtures; they must never be reused for a deployed environment.

## 2. Failure model (QB-14)

- API failures are typed JSON `application/problem+json` bodies with a stable `code` (see
  `QtsBilling.Contracts.Abstractions.ApiErrorCodes`) and a `traceId`. This includes:
  - domain rule failures (their precise status/code),
  - malformed payloads and bad parameters (`400 VALIDATION_FAILED`),
  - missing sessions (`401 UNAUTHENTICATED`), missing permissions (`403 FORBIDDEN`),
  - unexpected failures (`500 INTERNAL_ERROR`, no exception text, SQL or stack trace).
- There is **no error logger that writes to the database**. Application logs go to the configured sinks
  (stdout/collector) only; the audit trail is written transactionally with the domain change it describes.
  A failing log sink is caught and can never recurse, replace the typed failure or change the response.
- Request correlation: `CorrelationIdMiddleware` assigns/keeps a trace id; every API response carries it in
  the ProblemDetails `traceId` and every structured audit entry records it.

## 3. Database roles and credentials

- **Owner/migration role** (`qtsbilling` in development) owns the schema and runs `dotnet ef database update`.
- **Application role** (production) is created by `scripts/provision-postgres-roles.ps1`:
  - `LOGIN`, `CONNECT`, `USAGE` on the schema;
  - `SELECT/INSERT/UPDATE/DELETE` on tables, `USAGE/SELECT` on sequences, `EXECUTE` on functions;
  - **no** `CREATE`/`ALTER`/`DROP`, no ownership; `PUBLIC` privileges are revoked from the schema and database.
  - Default privileges are set for future objects created by the owner role.
- The API host must connect with the application role. Schema changes are a deployment step performed with
  the migration role, never by the running application.
- Rotation: rotate the application role password through the secret store and `ALTER ROLE`; rotate the
  owner/migration credential separately and less frequently. Never reuse the development password.
- Data Protection keys: keep `QTS_DATAPROTECTION_PATH` on a persistent, access-controlled volume. Rotating
  the key ring invalidates existing session cookies and outstanding reset tickets (users sign in again);
  rotation is therefore a deliberate operation, not a side effect of redeployment. Back up the key ring
  separately from the database dump — a database restore alone cannot decrypt cookies issued before it.

## 4. Backup and recovery (real backups, not metadata)

The repository ships three scripts; all operate against the PostgreSQL container and produce/verify real
logical backups.

| Script | Purpose |
|---|---|
| `scripts/backup-postgres.ps1` | `pg_dump --format=custom --compress=6` inside the container, copied to `backups/` with a sidecar manifest (timestamp, size, SHA-256, tool version). |
| `scripts/restore-postgres.ps1` | Restores a dump into an **explicitly named disposable database**; refuses the live database name; verifies the SHA-256 digest against the manifest; requires `-Force` to drop/recreate an existing target. |
| `scripts/restore-drill.ps1` | Backup → restore into `qtsbilling_restore_drill` → reconcile row counts for authoritative tables, posted journal debit/credit totals and identity users → drop the drill database. Fails if any value differs or if the reconciliation query itself fails. |

Operational policy:

1. Run `restore-drill.ps1` at least weekly and after every migration, in a disposable environment.
2. Keep nightly custom-format dumps plus the Data Protection key ring copy; retention is a deployment
   decision (recommend 30 days of dailies and 12 monthly archives).
3. A manifest row/file **describes** a backup; it is never a substitute for the dump. A backup is only
   considered valid after a restore drill reconciles against the source.
4. Restores into production require a change window, a fresh backup first, and a post-restore
   `restore-drill`-style reconciliation against the pre-restore dump.
5. The dev database currently contains test fixtures; production dumps must be handled as business data.

## 5. Logging and redaction

- Passwords, password-reset tickets, session cookies and authorization headers are never logged,
  persisted in audit summaries, or included in error responses.
- The development reset-ticket shortcut returns the ticket **in the response body only**; the log records
  that a ticket was generated, never its value.
- Authentication audit entries mask the account identifier (`o***@example.test`) and record a reason code
  (`invalid-credentials`, `unknown-or-inactive`, `setup-required`, `locked-out`) plus the correlation id.
  They never record the submitted password.
- Structured audit is append-only (`audit_events`): business-scoped entries plus the caller's own
  authentication entries are readable through `GET /api/v1/audit-events` with the `audit.view` permission
  (granted to Owner only until a wider audit role is agreed).
- Do not enable full request-body logging at the proxy for `/api/v1/auth/*` or payment routes.

## 6. Secret hygiene

- `scripts/secret-scan.ps1` scans every tracked file for connection-string passwords, private keys,
  static-IV AES usage and hardcoded API keys/tokens. Only the documented development fixtures are
  allowlisted; any new finding fails the scan and must be reviewed before commit.
- Recommend running the scan in CI on every pull request and before every release tag.
- No custom cryptography: password hashing is ASP.NET Core Identity's PBKDF2 implementation; token/cookie
  protection is ASP.NET Core Data Protection (random per-key material, no static IV).

## 7. SQLite device-local data protection and recovery policy (OPEN-8)

SQLite is device-local only and never authoritative. The device store and MAUI host have landed (QB-17/18/19
on the installed `maui-windows` workload); this policy is binding for the shipped device implementation:

- **Confidentiality:** the device database lives inside the app's private sandbox and relies on OS-level
  protection (BitLocker/device encryption + per-user ACLs) as the minimum. The final at-rest encryption
  choice (SQLCipher vs. encrypted container) remains **OPEN-8**; no plaintext credentials or CVV may ever be
  stored on the device regardless of the choice. Card data is never captured (D-36).
- **Integrity/recovery:** SQLite `journal_mode=WAL` for crash safety; run `PRAGMA integrity_check` on start
  and after an unclean shutdown; if it fails, quarantine the file, re-initialize the local store and
  re-sync from the server (the local store is disposable by design).
- **Backups:** copy the SQLite file (or take a `VACUUM INTO` snapshot) before applying a schema upgrade;
  keep one previous snapshot per device for rollback. Device backups must not be treated as business
  backups — the authoritative backup is the PostgreSQL dump.
- **Sync queue retention:** provisional operations and their idempotency keys stay until the server
  acknowledges them; after acknowledgement the local copy may be pruned. A `RESYNC_REQUIRED` response
  wipes and re-pulls authoritative state rather than attempting local repair.
- **Wipe/retirement:** remove the sandbox (database + key material used for local confidentiality) on
  sign-out/retirement; revoke the device through `devices.manage`.

## 8. Verified in QB-14

See `docs/migration/tasks/qb14-evidence.txt` for the live runs: backup restored and reconciled (18 values,
all match, drill database dropped), least-privilege role proven (DML permitted, DDL refused), static scan
clean (5 allowlisted development fixtures, 0 unallowlisted findings), typed 500 with `INTERNAL_ERROR` and
no leakage from a host with an unreachable database, masked/correlated audit entries, typed 401/403, and
reset-token log redaction.

## 9. Device-local SQLite store as implemented (QB-17)

The policy in section 7 is now backed by `QtsBilling.Persistence.Sqlite`. Operational rules for the
device implementation:

- **Location and access:** one database file per device in the app's private sandbox
  (`FileSystem.AppDataDirectory`); the web application must never open it. The library refuses to start
  against a file whose `PRAGMA quick_check` fails, and it never deletes or rebuilds a corrupt file - the
  device quarantines it, re-initializes and re-syncs (the cache is disposable by design).
- **Confidentiality:** the file is not encrypted by the application. Protection comes from the sandbox plus
  OS-level device encryption and per-user ACLs. **OPEN-8** (SQLCipher vs. encrypted container) is still open;
  no credentials, tokens or card/CVV data may be written to the device under any choice (D-36, D-46).
- **Durability settings:** `journal_mode=WAL`, `synchronous=FULL`, `foreign_keys=ON`, `busy_timeout=5000`,
  one in-process write gate. Capture and receipt application are single transactions, so a crash leaves the
  device with either the complete operation or none of it.
- **Upgrades:** schema migrations run only at startup, before sync starts. The device takes a `VACUUM INTO`
  snapshot next to the database before applying them, and startup is blocked when the file carries an
  unknown migration id or a newer `user_version` - the pending money rows are never discarded to "fix" a
  schema mismatch.
- **Retention:** outbox rows, provisional sales, cash movements, receipts, the device profile and the sync
  cursors are retained until the server acknowledges the operation (and rejected operations are kept for
  review). Only the cache group (`cached_products`, `cached_customers`, `cached_stock`,
  `cache_tombstones`) is discardable and is what a `RESYNC_REQUIRED` re-pull replaces.
- **Money representation:** money and quantity are stored only as scaled `INTEGER` values (money x10 000,
  quantity x1 000 000). No `REAL` column exists; precision loss and overflow are rejected, never rounded.

## 10. Verified in QB-17

See `docs/migration/tasks/qb17-evidence.txt`: 20 temp-file SQLite tests (atomic capture and rollback,
idempotent replay, outbox durability across a restart and a cache rebuild, crash recovery, `typeof()` proof
for integer money columns, v1 -> v2 schema upgrade with pending records plus the pre-migration snapshot,
blocked startup on an unknown/newer schema with the money rows intact, database-enforced cash-only and
lifecycle constraints) and the static boundary checks (no Npgsql/web reference, no floating-point
column, PostgreSQL migration history untouched).

## 11. Runbooks (QB-27)

Every procedure below is written so that it never destroys business data. The rehearsal
(`scripts/qb28-rehearsal.ps1`) runs the checkable parts of them.

### 11.1 Deploy the web host

1. Apply the schema: `dotnet ef database update --project QtsBilling.Persistence.Postgres` (the app also
   applies pending migrations at startup - prefer the explicit command for a controlled rollout).
2. Provision the runtime role: `scripts/provision-postgres-roles.ps1` (DML only, no DDL, no ownership).
3. Configure the environment: `QTS_POSTGRES_CONNECTION`, `QTS_DATAPROTECTION_PATH` (a persistent key
   ring - without it every restart invalidates the sessions), the ASP.NET Core URLs and, in production,
   a TLS terminator in front of the app.
4. Start the host and check `GET /api/v1/health` before routing traffic to it.
5. Roll back by rolling the *process* back; the schema is migrated forward only.

### 11.2 Backup and restore

- Backup: `scripts/backup-postgres.ps1` writes a `pg_dump` artifact plus a `.json` manifest with the
  SHA-256 of the dump. Copy both to the backup store; the manifest is what proves the copy is intact.
- Restore: `scripts/restore-postgres.ps1` loads a dump into a chosen database. Always restore into a
  **disposable** database first, reconcile it (`scripts/restore-drill.ps1` does exactly that and drops the
  drill database afterwards).
- A restore is not a rollback: restoring over a live database replaces it. Decide, rehearse, and record
  the decision.

### 11.3 Credential rotation

| What | How | What it invalidates |
|---|---|---|
| Database password | Change it in the database, then update `QTS_POSTGRES_CONNECTION` and restart the host. | Nothing; the sessions survive (the key ring is separate). |
| Data Protection key ring | Add a new key to `QTS_DATAPROTECTION_PATH` and keep the old file. | Nothing while the old key is kept; removing it invalidates every session and ticket. |
| Device enrolment | Revoke the device in `device_registrations` (or through `devices.manage`). | The device can no longer push or pull; its queued operations stay on the device for review. |
| User password | The reset flow (`/api/v1/auth/password-reset/*`). | The old session of that user ends with the security stamp rotation. |

### 11.4 Device procedures

- **Lost or stolen device:** revoke the enrolment (the server refuses every further push and pull), then
  wipe the sandbox on the device when it comes back. The queued operations on that device are lost with it -
  take a copy of the queue before wiping if the sales matter.
- **Retired device:** wipe the sandbox (`%LOCALAPPDATA%\<publisher>\<app>\Data`) and revoke the enrolment.
- **Device fell behind:** the server answers `RESYNC_REQUIRED`; the device wipes its cache and re-pulls the
  baseline. The pending queue is not touched.
- **Device with a stuck queue:** the queue never blocks the UI. Check the device's API-call log
  (`api-calls.log`) and the startup report (`startup-diagnostics.txt`); a terminal refusal is shown as
  *Rejected* with the error code and must be resolved by an operator, not by retrying.
- **Device re-enrolled to another business:** the catalog refresh rebuilds the cache and re-resolves the
  location and the register, so no rows of the old business survive.

### 11.5 Failure procedures

- **Failed schema migration on a device:** the store refuses to start, the pending money rows stay exactly as
  they were, and a pre-migration snapshot sits next to the database. Restore the snapshot or ship a fixed
  build; never delete the file to "unblock" the device.
- **Corrupted device database:** quarantine the file, let the device re-initialize and re-sync from the
  server, and report the incident. The device cache is disposable; the server is the truth.
- **Server disk full:** the server fails writes instead of losing them. Free space, restart, and check the
  pending operations on the devices - they are still on the devices.

### 11.6 Packaging (desktop client)

- **Unpackaged (the release workflow):** `dotnet publish QtsBilling.Maui -c Release -f net10.0-windows10.0.19041.0
  -p:RuntimeIdentifier=win-x64 -p:WindowsPackageType=None`. The published folder is the application.
- **MSIX:** the identity lives in `Platforms/Windows/Package.appxmanifest`
  (`com.qtsbilling.desktop`); build with `-p:WindowsPackageType=MSIX` and sign with the operator's
  certificate. The WebView2 runtime is a prerequisite; the bootstrapper installs it when it is missing.

## 12. Observability (QB-27)

| Surface | What it carries | Where |
|---|---|---|
| Server responses | A correlation id (`X-Correlation-Id`) on every request and in every typed failure. | The response headers and the log. |
| Server logs | Structured events per request; failures are typed (`code`, `traceId`), never a stack trace. | The host log. |
| Audit feed | Append-only `audit_events`: sign-ins, permission changes, money movements. | `GET /api/v1/audit-events` (Owner, `audit.view`). |
| Health | The liveness/readiness of the host. | `GET /api/v1/health`. |
| Prometheus metrics | OpenTelemetry meter `QtsBilling.Server`: sync outcomes, POS posting duration, accounting-period lock wait, and idempotency replay/conflict counts. | `GET /metrics`; scrape from a trusted monitoring network. |
| Device startup report | Device id, server address, local-store schema, journal mode, integrity result, shared routes, whether the sync coordinator is registered. | `%LOCALAPPDATA%\<publisher>\<app>\Data\startup-diagnostics.txt`. |
| Device API log | Every API call with method, route, status and duration - never a credential or a payload body. | `%LOCALAPPDATA%\<publisher>\<app>\Data\api-calls.log`. |
| Device queue | The pending operations with their state, the open conflicts and the feed revision. | The point-of-sale screen. |

What to watch day to day: the pending-operation count and the conflict count on the devices (a device that
cannot push is accumulating money that the server has not seen), the `RESYNC_REQUIRED` rate, the failed
authentication rate in the audit feed, and the backup job's success (a missing manifest means an unprovable
backup).

The scrape exporter is pinned to `OpenTelemetry.Exporter.Prometheus.AspNetCore` 1.19.1-beta.1. The
OpenTelemetry .NET project currently labels this pull exporter prerelease, so keep it pinned and review
the exporter status and compatibility before upgrading it.

## 13. Verified in QB-27

The rehearsal (QB-28) runs the checkable parts of these runbooks on every rehearsal: the secret scan, the
health check, the idempotent push smoke, the change-feed smoke, the backup with its SHA-256 manifest and
the restore drill into a disposable database.
