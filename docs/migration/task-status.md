# Migration task status

> Branch `migration/pg-sqlite-maui` · Local baseline `9a7c093` · Updated 2026-10-19
>
> Status legend: **DONE** (implemented + verified locally) · **PARTIAL** (implemented, verification
> blocked) · **TODO** · **GATED** (requires a user codeword / business decision).
> Every task's Definition of Done lives in `docs/migration/tasks/QB-xx.md`.

| Task | Title | Status | Notes |
|---|---|---|---|
| QB-00 | Verify baseline and production data inventory | **DONE (partial by design)** | Code-level inventory complete; live-data items marked UNVERIFIED (no reachable MongoDB). |
| QB-01 | Establish clean solution boundaries without deleting WinForms | **DONE** | Web host `net10.0` and independent of WinForms; WASM client off Windows TFM; WinForms retained. |
| QB-02 | Canonical IDs, value objects and contracts | **DONE** | `QtsBilling.Contracts` + `QtsModels.ValueObjects`; 41 focused tests pass. |
| QB-03 | PostgreSQL EF Core foundation and schema | **DONE (verified live on PostgreSQL 18.6)** | 38-table schema, migrations + integrity guards; applied to a live instance and exercised with real transactions. |
| QB-04 | Secure identity, business membership and OIDC | **DONE (server core)** | Identity on PostgreSQL, permission policies, tenant claims, enrolment gate, reset flow; OIDC/PKCE device grant deferred to QB-22. |
| QB-05 | Add typed, authenticated API to existing web host | **DONE (first slice)** | `/api/v1` auth/customers/health, ProblemDetails + codes, correlation ids, OpenAPI, typed clients. |
| QB-06 | Migrate customers, products and supplier masters | **PARTIAL — customers + products done** | Customers, products/variants on PostgreSQL behind the API; suppliers/units/categories/brands/groups/tax rates/discounts still Mongo. |
| QB-07 | Replace journals with balanced, immutable ledger | **DONE (verified live on PostgreSQL 18.6)** | Chart of accounts, periods, atomic posting, reversals, source idempotency, derived trial balance. |
| QB-08 | Transactional authoritative inventory | **DONE (verified live on PostgreSQL 18.6)** | Movement+balance atomicity, no oversell under concurrency, replay-safe references, reconciliation report. |
| QB-09 | Atomic idempotent server-side sale posting | **DONE (verified live on PostgreSQL 18.6)** | `POST/GET /api/v1/pos/sales`; one transaction = sale + snapshots + stock + payment + balanced journal + audit; `Idempotency-Key` replay returns the stored sale, changed payload refused; race-safe against oversell. |
| QB-10 | Cash register, safe payments and receipts | **DONE (verified live on PostgreSQL 18.6)** | Drawer sessions (one open per register), derived expected cash, counted/variance close with audited movements and reasons; card disabled (`PAYMENT_METHOD_DISABLED`); snapshot receipts (JSON + printable HTML with a provisional template); full refunds (balanced reversal + stock return + drawer movement, replay-safe). |
| QB-11 | Immutable invoice and credit workflow | TODO | |
| QB-12 | Purchasing, expense, tax and discount integrity | TODO | |
| QB-13 | Financial and operational reports from authoritative facts | TODO | |
| QB-14 | Security, structured audit and real backup/restore | TODO | Card-data/PAN remediation is P0 (F-07). |
| QB-15 | Remove web client database dependencies | **DONE (current surface)** | Browser bundle contains no Mongo/Npgsql/EF/QtsServices assets; CI bundle check still TODO (QB-26). |
| QB-16 | Extract shared MudBlazor Razor components | TODO | |
| QB-17 | Implement device-local SQLite persistence | TODO | Scaled-decimal helpers exist (QB-02). |
| QB-18 | Create MAUI Windows Blazor Hybrid application | TODO | Requires `dotnet workload install maui-windows` approval. |
| QB-19 | Implement offline cash-only sale capture | TODO | |
| QB-20 | Server idempotent sync-push handler | TODO | Schema ready (`processed_operations`, payload fingerprint helper ready). |
| QB-21 | Implement safe server master-data change feed | TODO | Schema ready (`sync_revisions`, `sync_changes`). |
| QB-22 | MAUI push/pull coordinator and conflict-resolution queue | TODO | |
| QB-23 | Build read-only Mongo source inventory and export tool | TODO | Blocked: no reachable MongoDB. |
| QB-24 | Deterministic transform and PostgreSQL import | TODO | `legacy_identifier_map` ready. |
| QB-25 | Produce signed reconciliation report and account-reset plan | TODO | Blocked by QB-23/24. |
| QB-26 | Focused tests and CI pipelines | TODO | No CI exists in the repository today. |
| QB-27 | Operational runbooks, backup, packaging and observability | TODO | |
| QB-28 | End-to-end rehearsal and pilot approval | TODO | Requires running PostgreSQL. |
| QB-29 | Remove WinForms completely | **GATED** | Requires the user's deletion codeword; nothing removed. |
| QB-30 | Production cutover and post-cutover integrity verification | **GATED** | Requires the user's cutover codeword and business decisions. |

## Current architecture state (after QB-07 / QB-08 / QB-09 / QB-10)

- `QtsModels` — legacy BSON entities retained **plus** new provider-neutral value objects (`Money`, `Quantity`, `CurrencyCode`, `ScaledDecimal`).
- `QtsBilling.Contracts` — wire DTOs (paging, customers, catalog, accounting, inventory, sales capture, sync envelopes, identity), canonical JSON fingerprinting, typed API clients (`CustomerApiClient`, `AccountApiClient`) and `ApiException`.
- `QtsBilling.Persistence.Postgres` — `BillingDbContext` (Identity-enabled), 38-table schema + identity tables, migrations `InitialCreate`, `AddIntegrityGuards`, `AddIdentity`, `AddLedgerSourceIdempotency`, `AddSaleIdempotencyReceipt`, `AddCashRegisterSessionsAndRefunds`.
- `QtsWebApp` — ASP.NET Core host on **PostgreSQL**: Blazor app + `/api/v1` (auth, customers, products, accounts, periods, journal entries, trial balance, locations, inventory movements/balances/reconciliation, POS sales, cash registers/receipts/refunds, health), cookie sessions with permission policies, ProblemDetails/correlation ids, contract JSON options, OpenAPI in Development.
- **Sales (QB-09).** One request posts the sale, line snapshots, stock movements, payment, balanced journal and audit event atomically; `Idempotency-Key` + payload fingerprint receipts make retries safe (replay returns the stored sale; a changed payload is refused).
- **Cash drawers, receipts and refunds (QB-10).** Cash sales can name a register with an open drawer session; movements are audited with reasons; expected cash is derived from movements and close stores counted/variance. Card capture is disabled until a verified PSP exists. Receipts render from snapshots (JSON + printable HTML, provisional template ready for offline). Refunds post the balanced reversal, return stock and record the drawer movement, replay-safe via `Idempotency-Key`.
- `QtsWebApp.Client` — browser talks to the API only; **no** database assemblies or credentials in the bundle (QB-15).
- `QtsServices` / `QtsBilling` (WinForms) — unchanged legacy MongoDB paths; WinForms removal is gated (QB-29). Cash register, invoices, purchasing, expenses and reporting still live there until QB-10…QB-14.
- No dual writes exist: the PostgreSQL path serves its own (new) surface; legacy Mongo data has not been imported (QB-23/24 pending a reachable source).

## Local development environment (this machine)

```text
PostgreSQL 18.6 container: docker run -d --name qtsbilling-pg -p 5433:5432 \
  -e POSTGRES_USER=qtsbilling -e POSTGRES_PASSWORD=qtsbilling -e POSTGRES_DB=qtsbilling postgres:18.6
connection string:  QTS_POSTGRES_CONNECTION="Host=localhost;Port=5433;Database=qtsbilling;Username=qtsbilling;Password=qtsbilling"
run the web host:   dotnet run --project QtsWebApp.csproj --no-launch-profile --urls http://localhost:5188   (workdir QtsWebApp\QtsWebApp)
migrations:         dotnet ef database update --project QtsBilling.Persistence.Postgres   (from the repository root)
```

Development bootstrap: the first start applies migrations and creates one **Owner** account
(`owner@qtsbilling.local`, password from `QtsBilling:DevOwner:Password`, default `QtsBilling!Dev2026`)
whose setup is deliberately pending. Sign-in is refused with `PASSWORD_RESET_REQUIRED` until the
setup ticket (logged at startup, or returned by `POST /api/v1/auth/password-reset/request` in
Development) is completed via `POST /api/v1/auth/password-reset/complete`.

## Test status

```text
dotnet test TestQtsBilling --filter "FullyQualifiedName~TestQtsBilling.ValueObjects|FullyQualifiedName~TestQtsBilling.Contracts"
    → 41 passed / 0 failed
dotnet test TestQtsBilling
    → 41 passed / 93 failed — every failure is a legacy suite that needs a live source:
      System.InvalidOperationException: The 'QTS_MONGODB_REMOTE' environment variable is not set
      (pre-existing condition, not a regression; split fixtures land with QB-23/QB-26)
```
