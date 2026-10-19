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
| QB-03 | PostgreSQL EF Core foundation and schema | **DONE (applies pending a PostgreSQL 18 instance)** | 38-table schema, migrations + integrity guards; DDL script generated and reviewed. |
| QB-04 | Secure identity, business membership and OIDC | TODO | Needs identity provider confirmation (OPEN-7) and PostgreSQL running. |
| QB-05 | Add typed, authenticated API to existing web host | TODO | Depends on QB-04. |
| QB-06 | Migrate customers, products and supplier masters | TODO | Depends on QB-03/QB-05. |
| QB-07 | Replace journals with balanced, immutable ledger | TODO | DB guards already in place (QB-03). |
| QB-08 | Transactional authoritative inventory | TODO | DB guards already in place (QB-03). |
| QB-09 | Atomic idempotent server-side sale posting | TODO | |
| QB-10 | Cash register, safe payments and receipts | TODO | |
| QB-11 | Immutable invoice and credit workflow | TODO | |
| QB-12 | Purchasing, expense, tax and discount integrity | TODO | |
| QB-13 | Financial and operational reports from authoritative facts | TODO | |
| QB-14 | Security, structured audit and real backup/restore | TODO | Card-data/PAN remediation is P0 (F-07). |
| QB-15 | Remove web client database dependencies | TODO | Release blocker until done (browser build currently ships DB client assemblies). |
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

## Current architecture state (after QB-03)

- `QtsModels` — legacy BSON entities retained **plus** new provider-neutral value objects (`Money`, `Quantity`, `CurrencyCode`, `ScaledDecimal`).
- `QtsBilling.Contracts` — wire DTOs: paging, customers, catalog, sales capture, sync envelopes; canonical JSON fingerprinting.
- `QtsBilling.Persistence.Postgres` — `BillingDbContext`, 38-table schema, `InitialCreate` + `AddIntegrityGuards` migrations.
- `QtsServices` / `QtsBilling` (WinForms) / `QtsWebApp` — unchanged legacy MongoDB paths; still the running system.
- No dual writes exist: the new PostgreSQL path is not yet referenced by any runtime host.
