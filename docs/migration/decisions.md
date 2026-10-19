# Migration Decisions Record (no secrets)

> Companion to `docs/migration/inventory.md`. Records the governing decisions from the
> implementation specification (v1.0, 19 Oct 2026) plus decisions taken during implementation.
> **D-items marked OPEN require a business owner answer; the "default" column is what will be
> implemented if no new facts arrive.**

## Part A — Adopted architecture decisions (spec §0.1)

| ID | Decision |
|---|---|
| D-01 | **PostgreSQL 18** is the only authoritative production database; EF Core 10 + `Npgsql.EntityFrameworkCore.PostgreSQL` 10.x on .NET 10. |
| D-02 | **SQLite** (EF Core 10, `Microsoft.EntityFrameworkCore.Sqlite`) is device-local only: cache, provisional offline operations, outbox, acknowledgements, sync cursors. Never an independent ledger. |
| D-03 | **.NET MAUI Blazor Hybrid (Windows only)** replaces the entire `QtsBilling/` WinForms project. Not WinForms-with-a-WebView. |
| D-04 | The existing Blazor web UI is retained; shared presentation moves to an RCL. Web stays online-only. |
| D-05 | All business writes go through server-side application services/API. No PostgreSQL credentials or direct connections in WASM or MAUI. |
| D-06 | No dual-production-database writes. MongoDB is a read-only migration source and is removed from runtime at cutover. |
| D-07 | No silent data repair; unmappable records are quarantined; financial imbalance blocks cutover. |
| D-08 | Staged delivery on a branch. WinForms deletion (QB-29) and production cutover (QB-30) require the explicit user codeword(s); this document authorizes neither. |

## Part B — Implementation decisions taken in this branch

| ID | Decision | Rationale |
|---|---|---|
| D-10 | Implementation branch: `migration/pg-sqlite-maui` (from local `master` at `9a7c093`). | Spec §0.1/§2.1: develop in a branch; keep WinForms recoverable. |
| D-11 | Source of truth for this effort is the local clone (`9a7c093`, open-source snapshot), not the spec's GitHub SHA `952c9b26`. All F-xx findings re-verified locally (`inventory.md` §6). | The upstream SHA is not present in this clone. |
| D-12 | New projects (spec §2.1 names): `QtsBilling.Contracts`, `QtsBilling.Persistence.Postgres`, `QtsBilling.Persistence.Sqlite`, `QtsBilling.Ui` (RCL), `QtsBilling.Maui`, `QtsBilling.Migration`. Existing projects retained. | "Recommended smallest maintainable project topology". |
| D-13 | Web host targets plain `net10.0`; WASM client targets plain `net10.0`; only the MAUI Windows app carries a Windows-specific TFM (`net10.0-windows10.0.19041.0`). | Spec §2.1 build-portability correction. |
| D-14 | Interim (QB-01 → QB-15): the WASM client keeps its `QtsServices` reference **only** until the API boundary (QB-05/06/15) replaces it. This is recorded as a **release blocker** — no interim browser build is published. | Spec QB-01 acceptance. |
| D-15 | The solution already runs on .NET 10 / C# 14 (previous change set, part of local `master`). No re-upgrade needed; package patch levels tracked in the csproj files. | Local baseline reconciliation. |
| D-16 | Postgres entity model uses `Guid` PKs (`Guid.CreateVersion7()` for new records), `numeric(18,4)` money, `numeric(18,6)` quantity, `numeric(12,6)` rates, `timestamptz` UTC, `version bigint` concurrency. | Spec §3.1 conventions. |
| D-17 | Canonical journal = `journal_entries` + `journal_lines`; `Transactions` and `JournalEntries` legacy sources both map into it with a `source_type`/`source_id` namespace. Posted rows immutable; corrections via reversal. Deferred DB constraint trigger enforces balance at commit. | Spec §3.4, F-03/F-04. |
| D-18 | Inventory authority = `inventory_balances` (unique per business/location/variant, `on_hand >= 0`) + immutable `inventory_movements`. `Product.Quantity`/`Inventory.Quantity` cease to be authorities. | Spec §3.5, F-02/F-12. |
| D-19 | Sync protocol: per-business transactionally locked `sync_revision` counter + ordinal (commit-ordered), `processed_operations` idempotency with payload fingerprint, tombstones, `RESYNC_REQUIRED` on expired cursor. At-least-once delivery, exactly-once business effect. | Spec §4.4/§4.5. |
| D-20 | Legacy identity: `User.Password` is plaintext → **no** password migration. Users must establish fresh credentials via reset/invitation (QB-04/25). | Spec §5.1, F-06. |
| D-21 | Card data: `CardNumber`/`CardSecurity` are never migrated, logged, serialized to sync or persisted; only PSP reference + brand/last-four may exist. Offline card payments are disabled; `HandlePayment` false-success is removed. | Spec §5.3, F-07/F-09. |
| D-22 | `Application`-level encryption: retire the static-IV AES-CBC helper; use a single reviewed AEAD (AES-GCM, fresh nonce, keyed identifier) only where genuinely required. | Spec §5.4, F-08. |
| D-23 | Tenant isolation: every business-owned row carries `business_id`; compound FKs prevent cross-business linkage; server authorization + DB constraints both enforced (RLS only if authorization is demonstrably correct). | Spec §3.1/§3.3, F-22. |
| D-24 | Local PostgreSQL for development/CI: **resolved** — PostgreSQL 18.6 runs in the Docker container `qtsbilling-pg` (host port 5433) and all migrations are applied and verified there (including the integrity guards, exercised with real transactions). | Environment now available; QB-26 integration tests can target it. |
| D-25 | MAUI workload (`maui-windows`) is not installed; QB-18 will require `dotnet workload install maui-windows` approval (large download) before the Windows shell can build. | Environment constraint. |
| D-26 | Identity = ASP.NET Core Identity on PostgreSQL (Guid keys) with **cookie** sessions for the web app; permission claims are resolved server-side from the membership role at sign-in and re-checked by policy on every request. OpenIddict OIDC/PKCE is reserved for the MAUI public client (QB-22). | OPEN-7 default; no external IdP needed, and the browser never handles tokens or credentials. |
| D-27 | CSRF stance for the cookie-authenticated API: `SameSite=Strict` session cookie, no cross-origin CORS, state changes only through the typed clients over the same origin. Antiforgery tokens remain available for future HTML form posts. | Cross-site requests cannot carry the session cookie; verified by design + 401/403 behaviour. |
| D-28 | Every account (including the development owner) starts with `PasswordResetRequired = true`: sign-in is refused until a one-time reset/invitation ticket is completed. Tickets are returned in the API response **in Development only**; Production delivers them out of band. | Implements the QB-04 "no usable credential until safe enrolment" rule; no default password is ever usable. |
| D-29 | `DELETE /api/v1/customers/{id}` deactivates instead of erasing master data (soft delete); `includeInactive=true` exposes deactivated rows for audit. | Historical invoices/sales keep resolving the customer (specification §3.7, no silent data loss). |

## Part C — OPEN decisions required from the business (spec §13)

| # | Decision | Default if unanswered | Owner (to confirm) |
|---|---|---|---|
| OPEN-1 | Live data availability / production topology (U-01) | No cutover; develop against disposable fixtures | Business/ops |
| OPEN-2 | Multi-business vs single-business reality (U-02) | Strict isolation for a single verified business (explicit logged assignment) | Business |
| OPEN-3 | Stock costing method FIFO/LIFO/AVCO (U-06) | No method accepted until supported; block inventory-finance cutover | Finance |
| OPEN-4 | Offline oversell policy acceptance (U-09) | Provisional cash-only sales + conflict review; no global stock guarantee | Business |
| OPEN-5 | Offline revocation lag acceptance (spec default ≤ 24 h grant TTL) | Enforce ≤ 24 h, disclose lag | Security/business |
| OPEN-6 | Payment gateway | Disabled until a verified PSP integration exists | Business |
| OPEN-7 | Identity provider (self-hosted Identity + OpenIddict default) | Self-hosted reviewed Identity + OpenIddict | IT |
| OPEN-8 | SQLite confidentiality (BitLocker managed policy vs SQLCipher) | Require managed disk encryption; SQLCipher only if impossible | IT/security |
| OPEN-9 | Legacy monetary precision tolerance policy | Exact reconciliation; strict mismatch fails | Finance |
| OPEN-10 | Invoice numbering/series + jurisdictional tax rules (U-05) | Configurable; no certified-compliance claim | Finance/legal |
| OPEN-11 | Migration window / RTO-RPO | Planned freeze; no dual writes | Business/ops |
| OPEN-12 | WinForms deletion codeword (QB-29) + cutover codeword (QB-30) | Not executed | Business owner |

## Part D — Execution log (this branch)

| Date | Work | Evidence |
|---|---|---|
| 2026-10-19 | QB-00 inventory + decision record (live data UNVERIFIED) | `docs/migration/inventory.md`, `docs/migration/decisions.md`, `docs/migration/inventory.redacted.json` |
| 2026-10-19 | QB-01 boundary correction (web host `net10.0`, WinForms reference removed; WASM `net10.0`) | see `Definition of Done` in `docs/migration/tasks/QB-01.md` |
| 2026-10-19 | QB-02 contracts/domain value objects | `docs/migration/tasks/QB-02.md` |
| 2026-10-19 | QB-03 PostgreSQL EF Core foundation | `docs/migration/tasks/QB-03.md` |
| 2026-10-19 | QB-03 verified live on PostgreSQL 18.6 (migrations applied; unbalanced-journal, movement/balance, append-only, cross-tenant FK and draft-vs-issued guards exercised with real SQL) | `docs/migration/tasks/QB-03.md` (live verification table) |
| 2026-10-19 | QB-04 identity, membership, permissions and enrolment gate | `docs/migration/tasks/QB-04.md` |
| 2026-10-19 | QB-05 typed authenticated API (`/api/v1` auth, customers, health) | `docs/migration/tasks/QB-05.md` |
| 2026-10-19 | QB-06 customers vertical slice on PostgreSQL (API + UI) | `docs/migration/tasks/QB-06.md` |
| 2026-10-19 | QB-15 browser off the database (Mongo/Npgsql/EF absent from the WebAssembly bundle) | `docs/migration/tasks/QB-15.md` |

> Remaining tasks QB-04 … QB-30 are tracked in `docs/migration/task-status.md`.
