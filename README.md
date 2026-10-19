<div align="center">

# QtsBilling

**An accounting platform with Point of Sale (POS) and inventory management — .NET 10, Blazor, PostgreSQL, API-first, with a shared Blazor component library, a .NET MAUI Windows hybrid desktop client, and a device-local SQLite offline store.**

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![C#](https://img.shields.io/badge/C%23-14-239120?logo=csharp&logoColor=white)](https://learn.microsoft.com/dotnet/csharp/)
[![PostgreSQL](https://img.shields.io/badge/PostgreSQL-18-336791?logo=postgresql&logoColor=white)](https://www.postgresql.org/)
[![EF Core](https://img.shields.io/badge/EF_Core-10-5C2D91)](https://learn.microsoft.com/ef/core/)
[![Blazor](https://img.shields.io/badge/UI-MudBlazor-5C2D91?logo=blazor&logoColor=white)](https://mudblazor.com/)
[![PRs Welcome](https://img.shields.io/badge/PRs-welcome-brightgreen.svg)](CONTRIBUTING.md)

</div>

---

## Table of Contents

- [About](#about)
- [Architecture Decisions](#architecture-decisions)
- [Features](#features)
- [Technology Stack](#technology-stack)
- [Architecture](#architecture)
- [Solution Structure](#solution-structure)
- [Project Status](#project-status)
- [Getting Started](#getting-started)
- [Configuration](#configuration)
- [Running the Apps](#running-the-apps)
- [Applying Database Migrations](#applying-database-migrations)
- [Running Tests](#running-tests)
- [Roadmap](#roadmap)
- [Security](#security)
- [Contributing](#contributing)
- [Code of Conduct](#code-of-conduct)
- [License](#license)
- [Acknowledgments](#acknowledgments)

---

## About

**QtsBilling** is a desktop-and-web accounting platform for small and medium businesses that
combines double-entry bookkeeping, a Point of Sale terminal, and inventory management.

The platform is **PostgreSQL-authoritative and API-first**: a typed `/api/v1` boundary with
**ASP.NET Core Identity**, a shared MudBlazor component library (`QtsBilling.Ui`), a **device-local
SQLite** store, and a **.NET MAUI** Windows hybrid desktop client. The architecture migration is tracked
in [`docs/migration/`](docs/migration/) (server + desktop complete; sync handlers and CI remaining).

| Surface | Storage | Runtime |
| --- | --- | --- |
| Web app (`QtsWebApp`) + `/api/v1` | **PostgreSQL 18** (authoritative) | ASP.NET Core host — Blazor Server + WebAssembly rendering, MudBlazor |
| Browser (`QtsWebApp.Client`) | Contracts only — talks to `/api/v1` over HTTP | Blazor WebAssembly |
| Desktop (`QtsBilling.Maui`) | device-local **SQLite** store + REST API | .NET MAUI Blazor Hybrid (Windows) |
| Shared UI (`QtsBilling.Ui`) | Contracts + feature interfaces only | Razor Class Library used by web and desktop |

---

## Architecture Decisions

QtsBilling is a **hybrid online/offline, API-first** system. Governing decisions and per-task status are in
[`docs/migration/decisions.md`](docs/migration/decisions.md) and
[`docs/migration/task-status.md`](docs/migration/task-status.md). The load-bearing rules:

- **D-01** — PostgreSQL 18 is the only authoritative production database. EF Core 10 + Npgsql.
- **D-02** — SQLite is device-local only: cache, provisional offline operations, outbox, sync cursors. **Never an independent ledger.**
- **D-03** — the desktop client is a .NET MAUI Blazor Hybrid (Windows) app (`QtsBilling.Maui`).
- **D-04** — The Blazor web UI is retained; shared presentation lives in the `QtsBilling.Ui` RCL. Web stays online-only.
- **D-05** — All business writes go through server-side application services / API. No PostgreSQL credentials or direct connections in WASM or MAUI.
- **D-06** — A single authoritative datastore; no dual-database writes.
- **D-07** — No silent data repair. Unmappable records are quarantined; financial imbalance blocks cutover.
- **D-08** — Production cutover (QB-30) requires an explicit business-owner codeword.

---

## Features

All features below are **live and PostgreSQL-backed**.

### API surface — typed `/api/v1` feature slices

Each area is an ASP.NET Core minimal-API slice with ProblemDetails, stable error codes, correlation IDs and OpenAPI in Development:

| Area | What it covers |
| --- | --- |
| **Auth** | sign-in/out, session, password-reset request & complete |
| **Customers** | CRUD, search, paging, soft-deactivate |
| **Catalog** | products and variants |
| **Accounting** | accounts, periods, journal entries, trial balance |
| **Inventory** | locations, movements, balances, reconciliation |
| **POS sales** | atomic idempotent posting — stock, payment and a balanced journal in one transaction |
| **Cash registers** | drawer sessions, counted/variance close, audited movements |
| **Receipts & refunds** | snapshot JSON + printable HTML; balanced reversal, stock return and drawer movement |
| **Invoices** | draft / issue / void / full credit note, transactional numbering, payment allocation |
| **Suppliers & purchasing** | purchase orders, `GRN-` receipts and `PR-` returns — stock moves exactly once |
| **Expenses** | one balanced entry per expense, append-only reversal |
| **Pricing** | tax rates (bounded exact decimals, single default) and validated discounts |
| **Reports** | balance sheet, P&L, cash, stock & sales — derived from posted facts, business-scoped |
| **Audit** | append-only feed, masked identifiers, `audit.view` permission |
| **Sync** | idempotent operation push and the commit-ordered change feed |
| **Health** | liveness |

### Capabilities

#### Platform & security

- **Authentication** — ASP.NET Core Identity on PostgreSQL (Guid keys), cookie sessions with `SameSite=Strict`, strong password policy, lockout, permission-based claims and policies. Every account starts with `PasswordResetRequired = true`; sign-in is refused until a one-time setup ticket is completed, so **no default password is ever usable**.
- **Multi-business tenant isolation** — every business-owned row carries `business_id`; composite FKs prevent cross-tenant linkage; enforced server-side *and* by the schema.

#### Financial integrity

- **Canonical, immutable ledger** — `journal_entries` + `journal_lines` with optimistic concurrency (`version`); posted rows are append-only, corrections happen via reversal; deferred triggers enforce balance at commit and reject illegal updates.
- **Transactional inventory** — `inventory_balances` (unique per business / location / variant, `on_hand >= 0`) + append-only `inventory_movements`. `Product.Quantity` and `Inventory.Quantity` cease to be authorities.

#### Sales, POS & documents

- **Atomic, idempotent sales** — one transaction posts the sale, its line snapshots, the stock movements, the confirmed payment, the balanced journal entry and the audit event. `Idempotency-Key` + payload fingerprint receipts make retries safe (replay returns the original sale; a changed payload under the same key is refused). Concurrent terminals cannot oversell.
- **Truthful payments and receipts** — card capture is disabled until a verified PSP integration exists; receipts render from the immutable sale snapshots (JSON + printable HTML) with a visually distinct provisional template for offline capture; refunds reverse the sale, return the stock and keep the drawer arithmetic accounted.
- **Immutable invoices** — drafts stay editable with server-computed totals; issuing allocates unique scoped numbers inside the posting transaction and freezes the line snapshots (catalog renames never change a historical invoice). Posted invoices are never edited: corrections are a void (unpaid) or a full credit note that references the original and reverses its entry; payments allocate against the open balance with `Idempotency-Key` receipts.

#### Procurement & reporting

- **Purchasing and expense integrity** — a purchase order is a commitment: stock moves only through receipts, which carry quantity/cost snapshots, `GRN-`/`PR-` numbers and `Idempotency-Key` replay safety so a retried delivery never doubles stock. Expenses post one balanced ledger entry each and reverse exactly once; tax rates are exact bounded decimals with a single default, and disallowed discount definitions are refused before they reach the database.
- **Statements from authoritative facts** — the balance sheet, P&L, cash, stock and sales reports are computed per request from posted journal lines and inventory movements: no stored or mutable balances, no materialized snapshots, provisional offline money excluded by construction, and every response echoes the business it was computed for (verified tie-outs: sheet balanced, P&L = current earnings, cash = trial balance, stock = inventory balances).

#### Reliability & shared UI

- **Typed failures, audited sign-ins, real backups** — every API failure is a ProblemDetails body with a stable code and a correlation id (no stack/SQL leakage, no empty lists, no database-writing error logger); authentication outcomes are audited append-only with masked e-mails, and the development reset ticket never reaches a log. Backups are real `pg_dump` artifacts with SHA-256 manifests, restored into a disposable database and reconciled by a drill; the runtime database role is DML-only. See `docs/migration/deployment.md`.
- **One MudBlazor surface** — the customer page, its dialog, layout and navigation live in `QtsBilling.Ui` and render from injected `ICustomerApiClient`/`IAccountApiClient` interfaces: no component touches HTTP or a database, hosts own render modes/assets/routing, and a platform-injection HTML-render smoke proves the same component works outside the web host (the MAUI host build still awaits the workload).

#### Offline & sync (desktop)

- **Offline-first device store** — the device keeps one WAL SQLite file with a discardable cache (products, customers, stock) and a durable pending group (provisional sales, cash movements, outbox, inbox receipts, sync cursors). Money and quantities are checked scaled integers (precision loss and overflow are rejected, never rounded), an offline capture commits sale + lines + cash + outbox atomically, a cache rebuild can never touch pending money, the outbox survives crashes and restarts, and startup blocks on a failed integrity check or an unknown/newer schema after snapshotting the file (20 temp-file tests, QB-17).
- **Sells while offline** — the desktop client sells cash from its cached catalog: the capture is committed atomically into the device store, the receipt is clearly marked `PROVISIONAL` with a provisional reference (never a sale or invoice number), and everything that is still waiting for the server is listed in the pending queue. One operation id can never create two sales, a failed capture is never reported as a success, and the offline permission is limited to cash with `sales.post` on an enrolled location and register (QB-19).
- **Offline sales land exactly once** — `POST /api/v1/sync/operations` accepts a captured operation exactly once: the receipt row and the sale share one transaction, a replay returns the original sale, the same key with a different body is a stable conflict, and the server re-prices and re-checks the stock, the tax rate, the drawer and the permissions — the device never decides a business fact (QB-20).
- **A device learns what changed** — every master-data write appends its projection in the same transaction, with a row-locked per-business revision, so the change feed is commit ordered: a device walks the `(revision, ordinal)` cursor, a deactivation arrives as a tombstone, and a cursor the server cannot explain is answered with `RESYNC_REQUIRED` instead of a silent gap (QB-21).
- **One desktop client, same components** — the Windows desktop app (`QtsBilling.Maui`) renders the shared customer page, layout and navigation in a WebView2, signs in against the same API without embedding a credential, keeps the session in the Windows secure store, opens the device-local SQLite store at startup and stays usable while the server is down; the web host remains independently deployable (QB-18).

#### Building blocks

- **Value objects** — provider-neutral primitives: `Money` (`numeric(18,4)`), `Quantity` (`numeric(18,6)`), `CurrencyCode`, `ScaledDecimal`.
- **Sync protocol** — per-business `sync_revisions` with commit-ordered ordinal, `processed_operations` idempotency with payload fingerprint, `sync_changes` change feed, tombstones and `RESYNC_REQUIRED` on an expired cursor. The idempotent **sync-push** handler (`POST /api/v1/sync/operations`) is in place; the master-data change feed (QB-21) and the MAUI push/pull coordinator (QB-22) are the remaining pieces.

---

## Technology Stack

| Layer | Technology |
| --- | --- |
| Language | C# 14 |
| Runtime | .NET 10 |
| Web UI | Blazor Server + WebAssembly, [MudBlazor](https://mudblazor.com/) 9.11 |
| Authoritative DB | [PostgreSQL 18](https://www.postgresql.org/) via EF Core 10 + [Npgsql.EntityFrameworkCore.PostgreSQL](https://www.npgsql.org/) 10.x |
| Identity | ASP.NET Core Identity on PostgreSQL (Guid keys), cookie authentication |
| Desktop | .NET MAUI Blazor Hybrid, Windows (`net10.0-windows10.0.19041.0`) |
| Device-local DB | `Microsoft.EntityFrameworkCore.Sqlite` (offline store + outbox) |
| Shared UI | [`QtsBilling.Ui`](QtsBilling.Ui/) Razor Class Library (MudBlazor) |
| Contracts | Plain-`net10.0` DTOs, typed API clients, canonical-JSON fingerprinting |
| Client storage | [Blazored.LocalStorage](https://github.com/Blazored/LocalStorage) 4.5 |
| Guard clauses | [Throw](https://github.com/amantinband/throw) 1.4 |
| Testing | [xUnit](https://xunit.net/) 2.9, [Moq](https://github.com/devlooped/moq) 4.21, [Bogus](https://github.com/bchavez/Bogus) 35.6, [coverlet](https://github.com/coverlet-coverage/coverlet) 10.1 |
| Tooling | `dotnet-ef` 10.0.13 (local manifest in [`dotnet-tools.json`](dotnet-tools.json)) |
| Code quality | [Qodana](https://www.jetbrains.com/qodana/) (`qodana.yaml`), Dependabot |

---

## Architecture

```
   Clients
   |-- QtsWebApp.Client   (Blazor WebAssembly) -- QtsBilling.Ui + Contracts, no DB access
   +-- QtsBilling.Maui    (Windows hybrid)     -- QtsBilling.Ui + Contracts + device-local SQLite
                    |
                    |  HTTPS, typed API clients   (the desktop client is offline-tolerant)
                    v
   QtsWebApp   (ASP.NET Core host)
     - Blazor Server UI + the shared QtsBilling.Ui RCL
     - /api/v1 minimal-API feature slices
     - Identity, permission policies, correlation IDs, ProblemDetails, OpenAPI (dev)
                    |
                    v
   QtsBilling.Persistence.Postgres   (EF Core 10, Npgsql, BillingDbContext, 38-table schema + guards)
                    |
                    v
   PostgreSQL 18   <-- the single authoritative store

   Domain value objects (QtsModels.ValueObjects): Money, Quantity, CurrencyCode, ScaledDecimal

Browser and desktop both consume the shared QtsBilling.Ui RCL + QtsBilling.Contracts wire DTOs; neither
ships database credentials or drivers. The desktop keeps a device-local SQLite store for offline capture.
```

- **Presentation** — Blazor (server + WASM) for the web and a .NET MAUI Blazor Hybrid Windows desktop
  client. Shared MudBlazor components live in the `QtsBilling.Ui` RCL, consumed by both hosts (QB-16/QB-18).
- **Application/API** — `QtsWebApp` hosts a typed `/api/v1` minimal-API surface with feature slices
  under `Features/*` and cross-cutting concerns under `Infrastructure/*` (correlation IDs,
  ProblemDetails, permission policies, claims factory, dev seeder). The browser never touches a
  database directly.
- **Persistence** — `QtsBilling.Persistence.Postgres` owns the authoritative `BillingDbContext`
  (`IdentityDbContext`), domain-split entity configuration, snake_case naming, financial invariants
  enforced at both context and DB (deferred balance triggers, append-only tables, cross-tenant FK
  guards, draft-vs-issued constraints).
- **Domain** — `QtsModels` hosts the provider-neutral **value objects** (`Money`, `Quantity`,
  `CurrencyCode`, `ScaledDecimal`).
- **Contracts** — `QtsBilling.Contracts` is the browser/MAUI-facing wire format: DTOs (Customers /
  Catalog / Sales / Inventory / Accounting / Identity / Sync), canonical JSON, typed API clients,
  `ApiException` and stable error codes.

---

## Solution Structure

| Project | Type | Target | Role | Status |
| --- | --- | --- | --- | --- |
| [`QtsModels`](QtsModels/) | Class library | `net10.0` | Provider-neutral [`ValueObjects/`](QtsModels/ValueObjects/) (`Money`, `Quantity`, `CurrencyCode`, `ScaledDecimal`) | ✅ Active |
| [`QtsBilling.Contracts`](QtsBilling.Contracts/) | Class library | `net10.0` | Wire DTOs, canonical-JSON fingerprinting, typed API clients (`CustomerApiClient`, `AccountApiClient`), `ApiException`, error codes, paging | ✅ QB-02 |
| [`QtsBilling.Persistence.Postgres`](QtsBilling.Persistence.Postgres/) | Class library | `net10.0` | EF Core 10 + Npgsql `BillingDbContext` (Identity-enabled), 38-table schema, migrations, snake_case naming, append-only + financial invariants, optimistic concurrency | ✅ QB-03 / QB-04 |
| [`QtsWebApp`](QtsWebApp/QtsWebApp/) | ASP.NET Core host | `net10.0` | Blazor Server UI + `/api/v1` (auth, customers, catalog, accounting, inventory, sales, cash register, refunds, invoices, purchasing, expenses, pricing, reports, audit, sync, health); Identity cookie auth + permission policies; correlation IDs; ProblemDetails; OpenAPI in Dev | ✅ QB-05…QB-14 |
| [`QtsWebApp.Client`](QtsWebApp/QtsWebApp.Client/) | Blazor WebAssembly | `net10.0` | Browser UI + shared components — references Contracts and `QtsBilling.Ui`; **no database or credential material** ships in the bundle | ✅ QB-15 / QB-16 |
| [`TestQtsBilling`](TestQtsBilling/) | xUnit project | `net10.0` | Value-object + contract tests (no external services required) | ✅ Active |
| [`QtsBilling.Ui`](QtsBilling.Ui/) | Razor Class Library | `net10.0` | Shared MudBlazor components (customer page/dialog, layout, nav) — Contracts + MudBlazor only, feature-interface injection, no render modes | ✅ QB-16 |
| [`QtsBilling.Ui.Smoke`](QtsBilling.Ui.Smoke/) | Console harness | `net10.0` | HTML-render platform smoke: renders the shared customer component with stub feature interfaces outside the web host | ✅ QB-16 |
| [`QtsBilling.Persistence.Sqlite`](QtsBilling.Persistence.Sqlite/) | Class library | `net10.0` | Device-local store: one WAL SQLite file per device (discardable cache + durable outbox/receipts/sync state), checked scaled-integer money, own migration history, single-writer gate (QB-17) | ✅ QB-17 |
| [`QtsBilling.Persistence.Sqlite.Tests`](QtsBilling.Persistence.Sqlite.Tests/) | xUnit project | `net10.0` | 20 temp-file SQLite tests: atomic capture, rollback, idempotency, restart/cache-rebuild durability, crash recovery, integer comparisons, schema upgrade with pending records (QB-17) | ✅ QB-17 |
| [`QtsBilling.Maui`](QtsBilling.Maui/) | .NET MAUI Blazor Hybrid | `net10.0-windows10.0.19041.0` | Windows desktop client: shared `QtsBilling.Ui` components in a WebView2, device identity, DPAPI secure session, device-local SQLite store, offline-tolerant API adapters (QB-18) | ✅ QB-18 |
| _planned_ `QtsBilling.Migration` | Class library | `net10.0` | Historical data import, transform and reconciliation tooling (QB-23…QB-25) | 📋 Planned |

> **Platform note.** Only the MAUI Windows desktop host needs a Windows-specific TFM
> (`net10.0-windows10.0.19041.0`). The web host, browser client and all libraries are plain `net10.0`
> and build portably.

---

## Project Status

| Area | Status |
| --- | --- |
| Contracts + value objects (QB-02) | ✅ Implemented; focused tests pass |
| PostgreSQL schema + EF Core foundation (QB-03) | ✅ Verified live on PostgreSQL 18.6 (integrity guards exercised with real transactions) |
| Identity, permissions, tenant isolation (QB-04) | ✅ Server core done; OIDC/PKCE deferred to QB-22 |
| Typed authenticated `/api/v1` (QB-05) | ✅ Live: auth, customers, catalog, accounting, inventory, health |
| Customers + catalog masters (QB-06) | 🟡 Customers and products/variants on PostgreSQL; supplier/unit/category masters not all exposed as dedicated endpoints yet |
| Balanced immutable ledger (QB-07) | ✅ Done, verified live — accounts, periods, atomic posting, reversals, source idempotency, trial balance |
| Transactional inventory (QB-08) | ✅ Done, verified live — movement+balance atomicity, no oversell, replay-safe references, reconciliation |
| Atomic idempotent sale posting (QB-09) | ✅ Done, verified live |
| Cash register, receipts, refunds (QB-10) | ✅ Done, verified live |
| Invoices & credit notes (QB-11) | ✅ Done, verified live |
| Purchasing, expenses, tax, discounts (QB-12) | ✅ Done, verified live |
| Financial & operational reports (QB-13) | ✅ Done, verified live |
| Security, audit, real backup/restore (QB-14) | ✅ Done, verified live |
| Browser off the database (QB-15) | ✅ Done for current surface |
| Shared `QtsBilling.Ui` RCL (QB-16) | ✅ Done (web + desktop) |
| Device-local SQLite store (QB-17) | ✅ Done, verified with temp-file tests |
| MAUI Windows desktop client (QB-18) | ✅ Done, verified on Windows 11 |
| Offline cash-only capture (QB-19) | ✅ Done, verified live |
| Sync-push handler (QB-20) | ✅ Done, verified live |
| Change feed (QB-21) | ✅ Done, verified live |
| Device sync loop + conflict queue (QB-22) | ✅ Done, verified live |
| OIDC/PKCE device flow | 📋 TODO — the client still uses the session endpoint |
| Historical data import + reconciliation (QB-23 … QB-25) | ⛔ Blocked — needs a reachable legacy source (and the legacy field mapping) |
| Tests, CI/CD, secret scanning (QB-26) | ✅ Done — the CI and release pipelines are in place |
| Runbooks, packaging/MSIX, observability, rehearsal (QB-27 / QB-28) | ✅ Done — the runbooks live in docs/migration/deployment.md, the rehearsal passes 7/7 |
| Production cutover (QB-30) | 🔒 GATED — requires an explicit business-owner codeword |

Full task-by-task view: [`docs/migration/task-status.md`](docs/migration/task-status.md).
Definition of Done per task: [`docs/migration/tasks/`](docs/migration/tasks/).

---

## Getting Started

### Prerequisites

- **[.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)**
- **PostgreSQL 18.6** — either via Docker:

  ```bash
  docker run -d --name qtsbilling-pg -p 5433:5432 \
    -e POSTGRES_USER=qtsbilling \
    -e POSTGRES_PASSWORD=qtsbilling \
    -e POSTGRES_DB=qtsbilling \
    postgres:18.6
  ```

  …or a local install.
- **.NET MAUI workload** — run `dotnet workload install maui-windows` only to build the Windows desktop client (`QtsBilling.Maui`).
- An IDE such as **Visual Studio 2022** (17.12+) or **JetBrains Rider**.

### 1. Clone

```bash
git clone https://github.com/nirzaf/QtsBilling.git
cd QtsBilling
```

### 2. Configure your environment

Set the PostgreSQL connection string (see [Configuration](#configuration)). The default in code
targets `Host=localhost;Port=5432`; the Docker example above maps to `5433`, so set the variable:

```powershell
$env:QTS_POSTGRES_CONNECTION = "Host=localhost;Port=5433;Database=qtsbilling;Username=qtsbilling;Password=qtsbilling"
```

To persist the Identity/session key ring across restarts, optionally set a Data Protection path:

```powershell
$env:QTS_DATAPROTECTION_PATH = "C:\qts-billing\keys"
```

### 3. Restore, build, apply migrations

```bash
dotnet restore QtsBilling.sln
dotnet build QtsBilling.sln
dotnet tool restore
dotnet ef database update --project QtsBilling.Persistence.Postgres
```

---

## Configuration

All secrets come from **environment variables**. Nothing sensitive is stored in source control —
use [`.env.example`](.env.example) as a template.

| Variable | Applies to | Required? | Description | Default |
| --- | --- | --- | --- | --- |
| `QTS_POSTGRES_CONNECTION` | Web host / API / migrations | **Yes** to run the web app or apply migrations | Npgsql connection string for the authoritative PostgreSQL 18 database | *localhost dev fallback* — set a real value for anything other than the throwaway local case |
| `QTS_DATAPROTECTION_PATH` | Web host | No | Directory in which to persist the ASP.NET Core Data Protection key ring (session/ticket keys) across restarts | *unset — ephemeral ring (development)* |
| `QtsBilling:DevOwner:Password` | Dev seeder | No | Password used to seed the local development Owner account | `QtsBilling!Dev2026` |

The connection string is resolved by
[`PostgresServiceCollectionExtensions.ResolveConnectionString()`](QtsBilling.Persistence.Postgres/PostgresServiceCollectionExtensions.cs)
— it reads `QTS_POSTGRES_CONNECTION` and falls back to a throwaway `Host=localhost` development default.
All secrets come from environment variables; nothing sensitive is stored in source control.

---

## Running the Apps

### Web host (Blazor Server + WebAssembly + `/api/v1`)

```bash
dotnet run --project QtsWebApp/QtsWebApp/QtsWebApp.csproj --urls http://localhost:5188
```

Then open `http://localhost:5188`. In Development, OpenAPI is served at `/openapi/v1.json`.

On **first start**, the app applies pending migrations and seeds one Owner account
(`owner@qtsbilling.local`). Sign-in is intentionally refused with `PASSWORD_RESET_REQUIRED` until
the one-time setup ticket is completed — the ticket is logged at startup and is also returned by
`POST /api/v1/auth/password-reset/request` in Development (never in Production).

### Desktop client (.NET MAUI, Windows)

```bash
dotnet build QtsBilling.Maui/QtsBilling.Maui.csproj
```

Requires the `maui-windows` workload (`dotnet workload install maui-windows`). The desktop client signs in
against the same `/api/v1` (no embedded credential), keeps the session in the Windows secure store, and
uses the device-local SQLite store for offline capture. Set the API base address in the app's server
settings (`QtsBilling.Maui/Services/ServerSettings.cs`).

---

## Applying Database Migrations

The PostgreSQL schema is managed with EF Core migrations. The local tool manifest
([`dotnet-tools.json`](dotnet-tools.json)) pins `dotnet-ef` to **10.0.13**.

```bash
# One-time per clone
dotnet tool restore

# Apply pending migrations to the connection in QTS_POSTGRES_CONNECTION
dotnet ef database update --project QtsBilling.Persistence.Postgres

# Author a new migration after a model change
dotnet ef migrations add <Name> --project QtsBilling.Persistence.Postgres
```

Current PostgreSQL migrations (see
[`QtsBilling.Persistence.Postgres/Migrations/`](QtsBilling.Persistence.Postgres/Migrations/)):

| Migration | Purpose |
| --- | --- |
| `InitialCreate` | 38-table schema (core, master data, catalog, inventory, sales, accounting, purchasing, platform) |
| `AddIntegrityGuards` | Deferred balance triggers, unique indexes, append-only / draft-vs-issued / cross-tenant FK guards |
| `AddIdentity` | ASP.NET Core Identity tables in the same schema |
| `AddLedgerSourceIdempotency` | Source-namespace uniqueness on `journal_entries` |
| `AddSaleIdempotencyReceipt` | Sale `Idempotency-Key` + payload-fingerprint receipts |
| `AddCashRegisterSessionsAndRefunds` | Drawer sessions, counted/variance close, refunds |
| `AddCreditNotesAndInvoiceSupport` | Invoice lifecycle, `INV-` sequences, credit notes |
| `AddPurchasingReceiptsAndExpensePosting` | Purchase-order receipts/returns, expense posting, pricing |
| `HardenPurchaseOrderLineReceiptGuard` | Narrowed PO-line draft guard for receipt bookkeeping |

The device store keeps its **own** migration history (separate from PostgreSQL); apply it with
`dotnet ef database update --project QtsBilling.Persistence.Sqlite`
(`InitialLocalStore`, `AddSaleLineNonNegativeChecks`).

---

## Running Tests

```bash
# Value objects + contracts (no external services).
dotnet test TestQtsBilling

# Device-local SQLite store (no external services).
dotnet test QtsBilling.Persistence.Sqlite.Tests

# Coverage.
dotnet test TestQtsBilling --collect:"XPlat Code Coverage"
```

PostgreSQL-backed flows are verified against a running instance (the Docker container above). CI
(build + the suites above + an integration run against a Postgres service container + a WASM bundle
check) is planned under **QB-26**.

---

## Roadmap

Per-task detail lives in [`docs/migration/tasks/`](docs/migration/tasks/); the aggregate tracker is
[`docs/migration/task-status.md`](docs/migration/task-status.md). Highlights:

**Done (server + desktop):** QB-01…QB-22, plus the CI pipelines, the operational runbooks and the rehearsal (QB-26…QB-28) — solution boundaries; contracts and value objects;
the PostgreSQL schema with a balanced immutable ledger (QB-07), transactional inventory (QB-08), atomic
idempotent sales (QB-09), cash register / receipts / refunds (QB-10), invoices & credit notes (QB-11),
purchasing / expenses / tax / discounts (QB-12), derived reports (QB-13), and security / audit / real
backup-restore (QB-14); the shared `QtsBilling.Ui` RCL (QB-16); the device-local SQLite store (QB-17);
the MAUI Windows client (QB-18) with offline cash-only capture (QB-19); and the sync side — the idempotent
push handler (QB-20), the commit-ordered change feed (QB-21) and the device synchronization loop with its conflict queue (QB-22).

**Remaining:**

- **OIDC/PKCE device flow** — the standards-based device authorisation for the desktop client.
- **QB-23 … QB-25** — out-of-band import of historical data (export → deterministic transform →
  PostgreSQL load), plus a signed reconciliation report and account-reset plan.
- **Pilot approval** — the business decision that opens the pilot (which business, which devices, which
  cashiers and the acceptance window).
- **QB-30** — production cutover and post-cutover integrity verification (gated behind the business-owner's
  cutover codeword).

Have an idea? Open a
[feature request](https://github.com/nirzaf/QtsBilling/issues/new?template=feature_request.md).

---

## Security

- **No secrets in source control.** Connection strings and keys come exclusively from environment
  variables. `.env`, `secrets.json` and other local secret files are ignored by
  [`.gitignore`](.gitignore); use [`.env.example`](.env.example) as a template.
- **Server-side data access only.** Neither the WebAssembly bundle nor the MAUI desktop client carries
  **database credentials, database drivers, or direct database connections** (the desktop keeps only a
  device-local SQLite store). All state changes flow through `/api/v1` behind Identity + permission policies.
- **Authentication hardening.** Strong password policy, lockout on repeated failures, per-request
  permission re-check, SameSite=Strict session cookie, no cross-origin CORS, and a safe enrolment
  gate — every account starts with `PasswordResetRequired = true`; **no default password is ever
  usable**.
- **Tenant isolation.** Every business-owned row carries `business_id`; composite FKs and
  server-side authorization enforce cross-tenant separation.
- **Immutable financial facts.** Posted journals, sale lines, invoice lines and movement/audit rows
  are append-only; corrections go through reversal. Enforced both by the `SaveChanges` invariants
  in `BillingDbContext` and by PostgreSQL deferred triggers.
- **Payment card data.** `CardNumber` / `CardSecurity` are **never** migrated, logged, serialized
  to sync payloads, or persisted — only the PSP reference + brand / last-four may exist. Offline
  card payments are disabled (see decisions **D-21**, QB-14 / F-07).
- **Rotate exposed credentials.** If a credential was ever committed to this repository's history,
  treat it as compromised and rotate it — removing it from source does **not** remove it from Git
  history.
- **Report vulnerabilities privately.** See [`SECURITY.md`](SECURITY.md) for the disclosure process.

---

## Contributing

Contributions of any kind are welcome. Please read [`CONTRIBUTING.md`](CONTRIBUTING.md) for the
development process, coding conventions and pull-request checklist. If your contribution is part
of the migration effort, follow [`docs/migration/`](docs/migration/): pick a QB-xx task, satisfy
its Definition of Done, and update
[`docs/migration/task-status.md`](docs/migration/task-status.md) as part of the PR.

---

## Code of Conduct

This project adheres to the [Contributor Covenant Code of Conduct](CODE_OF_CONDUCT.md). By
participating, you are expected to uphold this code. Please report unacceptable behaviour as
described there.

---

## License

Distributed under the **MIT License**. See [`LICENSE`](LICENSE) for more information.

---

## Acknowledgments

- [PostgreSQL](https://www.postgresql.org/) and [Npgsql](https://www.npgsql.org/) — the authoritative data platform
- [Entity Framework Core 10](https://learn.microsoft.com/ef/core/) — model, migrations, change tracking
- [ASP.NET Core Identity](https://learn.microsoft.com/aspnet/core/security/authentication/identity) — authentication and membership
- [Blazor](https://learn.microsoft.com/aspnet/core/blazor/) and [MudBlazor](https://mudblazor.com/) — web UI
- [.NET MAUI](https://learn.microsoft.com/dotnet/maui/) — the Windows hybrid desktop client
- [xUnit.net](https://xunit.net/), [Moq](https://github.com/devlooped/moq) and [Bogus](https://github.com/bchavez/Bogus) — testing
- [QuestPDF](https://www.questpdf.com/) — planned PDF reporting engine

---

<div align="center">

**Maintained by [M.F.M Fazrin](https://github.com/nirzaf) and contributors.**

If you find QtsBilling useful, consider giving the repository a ⭐ on GitHub!

</div>
