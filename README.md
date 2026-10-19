<div align="center">

# QtsBilling

**An accounting platform with Point of Sale (POS) and inventory management — .NET 10, Blazor, PostgreSQL, and an API-first boundary, with a MAUI Windows hybrid client and SQLite offline tier under active development.**

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
- [Architecture in Flux](#architecture-in-flux)
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

The project is currently mid-migration. The web + API surface has moved onto **PostgreSQL** with
**ASP.NET Core Identity** and a typed `/api/v1` boundary; the browser bundle has been decoupled from
any database dependency. The legacy **MongoDB**-backed service layer and the **Windows Forms**
desktop client are still present and are being retired task by task according to
[`docs/migration/`](docs/migration/).

| Surface | Storage | Runtime |
| --- | --- | --- |
| Web app (`QtsWebApp`) + `/api/v1` | **PostgreSQL 18** (authoritative) | ASP.NET Core host — Blazor Server + WebAssembly rendering, MudBlazor |
| Browser (`QtsWebApp.Client`) | Contracts only — talks to `/api/v1` over HTTP | Blazor WebAssembly |
| Desktop (`QtsBilling`) — legacy | MongoDB | Windows Forms (`net10.0-windows`) |
| Services (`QtsServices`) — legacy | MongoDB.Driver 3.12 | Class library; the browser no longer references it |
| MAUI hybrid + SQLite offline | SQLite (device cache + outbox) + REST API | .NET MAUI Blazor Hybrid (Windows) — **planned** |

---

## Architecture in Flux

QtsBilling is transitioning from a three-tier MongoDB/WinForms app to a **hybrid online/offline,
API-first architecture**. Governing decisions and per-task status are in
[`docs/migration/decisions.md`](docs/migration/decisions.md) and
[`docs/migration/task-status.md`](docs/migration/task-status.md). The load-bearing rules:

- **D-01** — PostgreSQL 18 is the only authoritative production database. EF Core 10 + Npgsql.
- **D-02** — SQLite is device-local only: cache, provisional offline operations, outbox, sync cursors. **Never an independent ledger.**
- **D-03** — .NET MAUI Blazor Hybrid (Windows) replaces the WinForms `QtsBilling/` project (gated at QB-29).
- **D-04** — The existing Blazor web UI is retained; shared presentation moves to an RCL. Web stays online-only.
- **D-05** — All business writes go through server-side application services / API. No PostgreSQL credentials or direct connections in WASM or MAUI.
- **D-06** — No dual-production-database writes. MongoDB is a read-only migration source, removed at cutover.
- **D-07** — No silent data repair. Unmappable records are quarantined; financial imbalance blocks cutover.
- **D-08** — WinForms deletion (QB-29) and production cutover (QB-30) require explicit business-owner codewords.

---

## Features

**Web + API (live, PostgreSQL-backed)**

- Typed `/api/v1` endpoints implemented as ASP.NET Core minimal-API feature slices with ProblemDetails, error codes, correlation IDs and OpenAPI in Development — **auth**; **customers**; **catalog** (products / variants); **accounting** (accounts, periods, journal entries, trial balance); **inventory** (locations, movements, balances, reconciliation); **health**; **POS sales** (`POST /api/v1/pos/sales`, atomic idempotent posting with stock, payment and balanced journal); **cash registers** (drawer sessions with derived expected cash, counted/variance close, audited movements); **receipts** (snapshot JSON + printable HTML); **refunds** (balanced reversal + stock return + drawer movement); **invoices** (draft/issue/void/full credit note, transactional numbering, payment allocation with idempotency receipts, printable HTML); **suppliers**; **purchase orders** (draft/order/cancel plus idempotent receipts `GRN-` and returns `PR-` that move stock exactly once); **expenses** (one balanced entry per expense, append-only reversal); **tax rates** (bounded exact decimals, single default); **discounts** (validated definitions); **reports** (balance sheet, profit and loss, cash, stock and sales summaries derived from posted journal and inventory facts, base-currency-only, business-scoped); **audit events** (append-only structured feed with masked identifiers, correlation ids and the `audit.view` permission).
- **Authentication** — ASP.NET Core Identity on PostgreSQL (Guid keys), cookie sessions with `SameSite=Strict`, strong password policy, lockout, permission-based claims and policies. Every account starts with `PasswordResetRequired = true`; sign-in is refused until a one-time setup ticket is completed, so **no default password is ever usable**.
- **Multi-business tenant isolation** — every business-owned row carries `business_id`; composite FKs prevent cross-tenant linkage; enforced server-side *and* by the schema.
- **Canonical, immutable ledger** — `journal_entries` + `journal_lines` with optimistic concurrency (`version`); posted rows are append-only, corrections happen via reversal; deferred triggers enforce balance at commit and reject illegal updates.
- **Transactional inventory** — `inventory_balances` (unique per business / location / variant, `on_hand >= 0`) + append-only `inventory_movements`. `Product.Quantity` and `Inventory.Quantity` cease to be authorities.
- **Atomic, idempotent sales** — one transaction posts the sale, its line snapshots, the stock movements, the confirmed payment, the balanced journal entry and the audit event. `Idempotency-Key` + payload fingerprint receipts make retries safe (replay returns the original sale; a changed payload under the same key is refused). Concurrent terminals cannot oversell.
- **Truthful payments and receipts** - card capture is disabled until a verified PSP integration exists; receipts render from the immutable sale snapshots (JSON + printable HTML) with a visually distinct provisional template for offline capture; refunds reverse the sale, return the stock and keep the drawer arithmetic accounted.
- **Immutable invoices** - drafts stay editable with server-computed totals; issuing allocates unique scoped numbers inside the posting transaction and freezes the line snapshots (catalog renames never change a historical invoice). Posted invoices are never edited: corrections are a void (unpaid) or a full credit note that references the original and reverses its entry; payments allocate against the open balance with `Idempotency-Key` receipts.
- **Purchasing and expense integrity** - a purchase order is a commitment: stock moves only through receipts, which carry quantity/cost snapshots, `GRN-`/`PR-` numbers and `Idempotency-Key` replay safety so a retried delivery never doubles stock. Expenses post one balanced ledger entry each and reverse exactly once; tax rates are exact bounded decimals with a single default, and disallowed discount definitions are refused before they reach the database.
- **Statements from authoritative facts** - the balance sheet, P&L, cash, stock and sales reports are computed per request from posted journal lines and inventory movements: no stored or mutable balances, no materialized snapshots, provisional offline money excluded by construction, and every response echoes the business it was computed for (verified tie-outs: sheet balanced, P&L = current earnings, cash = trial balance, stock = inventory balances).
- **Typed failures, audited sign-ins, real backups** - every API failure is a ProblemDetails body with a stable code and a correlation id (no stack/SQL leakage, no empty lists, no database-writing error logger); authentication outcomes are audited append-only with masked e-mails, and the development reset ticket never reaches a log. Backups are real `pg_dump` artifacts with SHA-256 manifests, restored into a disposable database and reconciled by a drill; the runtime database role is DML-only. See `docs/migration/deployment.md`.
- **One MudBlazor surface** - the customer page, its dialog, layout and navigation live in `QtsBilling.Ui` and render from injected `ICustomerApiClient`/`IAccountApiClient` interfaces: no component touches HTTP or a database, hosts own render modes/assets/routing, and a platform-injection HTML-render smoke proves the same component works outside the web host (the MAUI host build still awaits the workload).
- **Offline-first device store** - the device keeps one WAL SQLite file with a discardable cache (products, customers, stock) and a durable pending group (provisional sales, cash movements, outbox, inbox receipts, sync cursors). Money and quantities are checked scaled integers (precision loss and overflow are rejected, never rounded), an offline capture commits sale + lines + cash + outbox atomically, a cache rebuild can never touch pending money, the outbox survives crashes and restarts, and startup blocks on a failed integrity check or an unknown/newer schema after snapshotting the file (20 temp-file tests, QB-17).
- **Sells while offline** - the desktop client sells cash from its cached catalog: the capture is committed atomically into the device store, the receipt is clearly marked `PROVISIONAL` with a provisional reference (never a sale or invoice number), and everything that is still waiting for the server is listed in the pending queue. One operation id can never create two sales, a failed capture is never reported as a success, and the offline permission is limited to cash with `sales.post` on an enrolled location and register (QB-19).
- **Offline sales land exactly once** - `POST /api/v1/sync/operations` accepts a captured operation exactly once: the receipt row and the sale share one transaction, a replay returns the original sale, the same key with a different body is a stable conflict, and the server re-prices and re-checks the stock, the tax rate, the drawer and the permissions - the device never decides a business fact (QB-20).
- **One desktop client, same components** - the Windows desktop app (`QtsBilling.Maui`) renders the shared customer page, layout and navigation in a WebView2, signs in against the same API without embedding a credential, keeps the session in the Windows secure store, opens the device-local SQLite store at startup and stays usable while the server is down; the web host remains independently deployable (QB-18).
- **Value objects** — provider-neutral primitives: `Money` (`numeric(18,4)`), `Quantity` (`numeric(18,6)`), `CurrencyCode`, `ScaledDecimal`.
- **Sync protocol ready in schema** — per-business `sync_revisions` with commit-ordered ordinal, `processed_operations` idempotency with payload fingerprint, `sync_changes` change feed, tombstones and `RESYNC_REQUIRED` on expired cursor. Handlers land with QB-20/21/22.

**Legacy (MongoDB path, retained while migration proceeds)**

- **Customer / Supplier / Product / Category / Brand / Unit / Discount / Tax / Expense / Invoice / Purchase Order / Sale / Journal / Ledger / Balance Sheet / POS** — 17 service classes on MongoDB.Driver.
- **Windows Forms** desktop client.
- **System utilities** — audit logging, configuration, backup/restore, local↔cloud sync.

---

## Technology Stack

| Layer | Technology |
| --- | --- |
| Language | C# 14 |
| Runtime | .NET 10 |
| Web UI | Blazor Server + WebAssembly, [MudBlazor](https://mudblazor.com/) 9.11 |
| Desktop (legacy) | Windows Forms (`net10.0-windows`) |
| Desktop (planned) | .NET MAUI Blazor Hybrid, Windows (QB-18) |
| Authoritative DB | [PostgreSQL 18](https://www.postgresql.org/) via EF Core 10 + [Npgsql.EntityFrameworkCore.PostgreSQL](https://www.npgsql.org/) 10.x |
| Identity | ASP.NET Core Identity on PostgreSQL (Guid keys), cookie authentication |
| Device-local DB (planned) | `Microsoft.EntityFrameworkCore.Sqlite` (QB-17) |
| Legacy DB | [MongoDB.Driver](https://mongodb.github.io/mongo-csharp-driver/) 3.12 (read-only migration source, retired at cutover) |
| Contracts | Plain-`net10.0` DTOs, typed API clients, canonical-JSON fingerprinting |
| Client storage | [Blazored.LocalStorage](https://github.com/Blazored/LocalStorage) 4.5 |
| Guard clauses | [Throw](https://github.com/amantinband/throw) 1.4 |
| Testing | [xUnit](https://xunit.net/) 2.9, [Moq](https://github.com/devlooped/moq) 4.21, [Bogus](https://github.com/bchavez/Bogus) 35.6, [coverlet](https://github.com/coverlet-coverage/coverlet) 10.1 |
| Tooling | `dotnet-ef` 10.0.13 (local manifest in [`dotnet-tools.json`](dotnet-tools.json)) |
| Code quality | [Qodana](https://www.jetbrains.com/qodana/) (`qodana.yaml`), Dependabot |

---

## Architecture

```
                     ┌─────────────────────────────────────────────┐
                     │  QtsWebApp.Client  (Blazor WebAssembly)     │
                     │  References Contracts only — no DB          │
                     └────────────────────┬────────────────────────┘
                                          │ HTTP via typed API clients
                                          ▼
                     ┌─────────────────────────────────────────────┐
                     │  QtsWebApp  (ASP.NET Core host)             │
                     │  · Blazor Server UI                         │
                     │  · /api/v1  minimal-API endpoints           │
                     │  · Identity · policies · correlation IDs    │
                     │  · ProblemDetails · OpenAPI (dev)           │
                     └────┬──────────────────────────────┬─────────┘
                          │                              │
                          ▼                              ▼
       ┌────────────────────────────────┐    ┌──────────────────────────┐
       │ QtsBilling.Persistence.Postgres│    │ (legacy) QtsServices     │
       │  EF Core 10 · Npgsql 10.x      │    │  MongoDB.Driver 3.12     │
       │  BillingDbContext (Identity)   │    │  17 service classes      │
       │  38-table schema + guards      │    └─────────────┬────────────┘
       │  append-only + concurrency     │                  │
       └────────────────┬───────────────┘                  ▼
                        │                          ┌──────────────┐
                        ▼                          │   MongoDB    │
                ┌────────────────┐                 │ (legacy src) │
                │  PostgreSQL 18 │                 └──────────────┘
                │  authoritative │
                └────────────────┘

Shared now:
   QtsBilling.Ui (RCL) — MudBlazor components used by the web client and the desktop host (QB-16)
   QtsBilling.Persistence.Sqlite — device-local store: WAL SQLite, discardable cache + durable
   outbox/receipts/sync state, checked scaled-integer money, own migration history (QB-17)
   QtsBilling.Maui — Windows desktop host: the same shared components in a WebView2, device
   identity, DPAPI secure session, offline-tolerant API (QB-18)

Planned (docs/migration/tasks):
   ┌───────────────────────────────────────────────────────────────┐
   │ QtsBilling.Maui (Windows hybrid)  ⇄  QtsBilling.Ui (RCL)      │
   │      ⇄  QtsBilling.Persistence.Sqlite (offline cache + outbox)│
   │      ⇄  sync-push / change-feed server handlers               │
   │ QtsBilling.Migration  — Mongo → Postgres export/transform/    │
   │                          import + reconciliation              │
   └───────────────────────────────────────────────────────────────┘
```

- **Presentation** — Blazor (server + WASM) for web and a .NET MAUI Blazor Hybrid Windows desktop client;
  WinForms (legacy) is retained until QB-29 (gated). Shared MudBlazor components live in
  `QtsBilling.Ui` and are consumed by the web client and the desktop host (QB-16/QB-18).
- **Application/API** — `QtsWebApp` hosts a typed `/api/v1` minimal-API surface with feature slices
  under `Features/*` and cross-cutting concerns under `Infrastructure/*` (correlation IDs,
  ProblemDetails, permission policies, claims factory, dev seeder). The browser never touches a
  database directly.
- **Persistence** — `QtsBilling.Persistence.Postgres` owns the authoritative `BillingDbContext`
  (`IdentityDbContext`), domain-split entity configuration, snake_case naming, financial invariants
  enforced at both context and DB (deferred balance triggers, append-only tables, cross-tenant FK
  guards, draft-vs-issued constraints).
- **Domain** — `QtsModels` retains legacy BSON entities and now also hosts the provider-neutral
  **value objects** (`Money`, `Quantity`, `CurrencyCode`, `ScaledDecimal`).
- **Contracts** — `QtsBilling.Contracts` is the browser/MAUI-facing wire format: DTOs (Customers /
  Catalog / Sales / Inventory / Accounting / Identity / Sync), canonical JSON, typed API clients,
  `ApiException` and stable error codes.

---

## Solution Structure

| Project | Type | Target | Role | Status |
| --- | --- | --- | --- | --- |
| [`QtsModels`](QtsModels/) | Class library | `net10.0` | Legacy BSON entities + new [`ValueObjects/`](QtsModels/ValueObjects/) (`Money`, `Quantity`, `CurrencyCode`, `ScaledDecimal`) | ✅ Active |
| [`QtsBilling.Contracts`](QtsBilling.Contracts/) | Class library | `net10.0` | Wire DTOs, canonical-JSON fingerprinting, typed API clients (`CustomerApiClient`, `AccountApiClient`), `ApiException`, error codes, paging | ✅ QB-02 |
| [`QtsBilling.Persistence.Postgres`](QtsBilling.Persistence.Postgres/) | Class library | `net10.0` | EF Core 10 + Npgsql `BillingDbContext` (Identity-enabled), 38-table schema, migrations, snake_case naming, append-only + financial invariants, optimistic concurrency | ✅ QB-03 / QB-04 |
| [`QtsWebApp`](QtsWebApp/QtsWebApp/) | ASP.NET Core host | `net10.0` | Blazor Server UI + `/api/v1` (auth, customers, products, accounting, inventory, health); Identity cookie auth + permission policies; correlation IDs; ProblemDetails; OpenAPI in Dev | ✅ QB-05 / QB-06 |
| [`QtsWebApp.Client`](QtsWebApp/QtsWebApp.Client/) | Blazor WebAssembly | `net10.0` | Browser UI + shared components — references Contracts and `QtsBilling.Ui`; **no database or credential material** ships in the bundle | ✅ QB-15 / QB-16 |
| [`QtsServices`](QtsServices/) | Class library | `net10.0` | Legacy 17 service classes over MongoDB.Driver (still used by the WinForms desktop app) | ♻️ Legacy — retiring per QB-07…QB-14 |
| [`QtsBilling`](QtsBilling/) | WinForms app | `net10.0-windows` | Legacy desktop client | ♻️ Retained until QB-29 (gated) |
| [`TestQtsBilling`](TestQtsBilling/) | xUnit project | `net10.0` | Focused tests for value objects and contracts (41 pass) **and** legacy service integration tests (93, need a live MongoDB) | ✅ Active |
| [`QtsBilling.Ui`](QtsBilling.Ui/) | Razor Class Library | `net10.0` | Shared MudBlazor components (customer page/dialog, layout, nav) — Contracts + MudBlazor only, feature-interface injection, no render modes | ✅ QB-16 |
| [`QtsBilling.Ui.Smoke`](QtsBilling.Ui.Smoke/) | Console harness | `net10.0` | HTML-render platform smoke: renders the shared customer component with stub feature interfaces outside the web host | ✅ QB-16 |
| [`QtsBilling.Persistence.Sqlite`](QtsBilling.Persistence.Sqlite/) | Class library | `net10.0` | Device-local store: one WAL SQLite file per device (discardable cache + durable outbox/receipts/sync state), checked scaled-integer money, own migration history, single-writer gate (QB-17) | ✅ QB-17 |
| [`QtsBilling.Persistence.Sqlite.Tests`](QtsBilling.Persistence.Sqlite.Tests/) | xUnit project | `net10.0` | 20 temp-file SQLite tests: atomic capture, rollback, idempotency, restart/cache-rebuild durability, crash recovery, integer comparisons, schema upgrade with pending records (QB-17) | ✅ QB-17 |
| [`QtsBilling.Maui`](QtsBilling.Maui/) | .NET MAUI Blazor Hybrid | `net10.0-windows10.0.19041.0` | Windows desktop client: shared `QtsBilling.Ui` components in a WebView2, device identity, DPAPI secure session, device-local SQLite store, offline-tolerant API adapters (QB-18) | ✅ QB-18 |
| _planned_ `QtsBilling.Migration` | Class library | `net10.0` | Mongo → Postgres export/transform/import + reconciliation (QB-23…QB-25) | 📋 Planned |

> **Platform note.** Only the (future) MAUI project needs a Windows-specific TFM. The web host and
> browser client are plain `net10.0` and build portably. The legacy WinForms project still targets
> `net10.0-windows`.

---

## Project Status

| Area | Status |
| --- | --- |
| Contracts + value objects (QB-02) | ✅ Implemented; 41 focused tests pass |
| PostgreSQL EF Core foundation (QB-03) | ✅ Implemented and verified live against PostgreSQL 18.6 (integrity guards exercised with real transactions) |
| Identity, permissions, tenant isolation (QB-04) | ✅ Server core done; OIDC/PKCE deferred to QB-22 |
| Typed authenticated `/api/v1` (QB-05) | ✅ Live: auth, customers, catalog, accounting, inventory, health |
| Customers + catalog masters on PostgreSQL (QB-06) | 🟡 Partial — customers and products/variants done; suppliers / units / categories / brands / groups / tax rates / discounts still Mongo |
| Balanced, immutable ledger (QB-07) | ✅ Done, verified live on PostgreSQL 18.6 — chart of accounts, periods, atomic posting, reversals, source idempotency, derived trial balance |
| Transactional authoritative inventory (QB-08) | ✅ Done, verified live — movement+balance atomicity, no oversell under concurrency, replay-safe references, reconciliation |
| Browser off the database (QB-15) | ✅ Done for current surface; CI bundle-check still TODO (QB-26) |
| Sales posting / cash register / invoices / purchasing / reports (QB-09 … QB-14) | 📋 TODO — ledger (QB-07) and inventory (QB-08) foundations are already live |
| MAUI Windows hybrid client (QB-18) | 📋 TODO — requires `dotnet workload install maui-windows` |
| SQLite offline persistence + sync (QB-17, QB-19 … QB-22) | 📋 TODO |
| Data migration + reconciliation (QB-23 … QB-25) | 📋 TODO — blocked on a reachable MongoDB source |
| CI/CD + secret scanning (QB-26) | 📋 TODO — not configured yet |
| WinForms deletion (QB-29) and production cutover (QB-30) | 🔒 GATED — require explicit business-owner codewords |

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
- **Windows 10/11** to build and run the **legacy WinForms** desktop client.
- (Optional) **MongoDB** (local or Atlas) — only if you want to run the legacy `QtsServices` or the 93 Mongo-dependent integration tests.
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

If you intend to run the legacy Mongo services or the 93 legacy integration tests, also set:

```powershell
$env:QTS_MONGODB_REMOTE = "mongodb://localhost:27017/qts-Billing"
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
| `QTS_POSTGRES_CONNECTION` | New PostgreSQL path | **Yes** to run the web app or apply migrations | Npgsql connection string for the authoritative PostgreSQL 18 database | *localhost dev fallback* — set a real value for anything other than the throwaway local case |
| `QTS_MONGODB_REMOTE` | Legacy `QtsServices` | Only when you use the legacy Mongo path | Primary/remote MongoDB connection string | *none — throws if unset* |
| `QTS_MONGODB_LOCAL` | Legacy `QtsServices` | No | Local MongoDB connection string for offline / sync features | `mongodb://localhost:27017/qts-Billing` |
| `QTS_ENCRYPTION_KEY` | Legacy `EncryptionService` | Only if using the legacy encryption helper | Base64-encoded AES key | *none — throws if used unset* |
| `QTS_ENCRYPTION_IV` | Legacy `EncryptionService` | Only if using the legacy encryption helper | Base64-encoded AES IV | *none — throws if used unset* |
| `QtsBilling:DevOwner:Password` | Dev seeder | No | Password used to seed the local development Owner account | `QtsBilling!Dev2026` |

Connection-string accessors:

- **New:** [`PostgresServiceCollectionExtensions.ResolveConnectionString()`](QtsBilling.Persistence.Postgres/PostgresServiceCollectionExtensions.cs)
- **Legacy:** [`QtsModels/GlobalConstants.cs`](QtsModels/GlobalConstants.cs)

> **Note on `EncryptionService`.** The static-IV AES-CBC helper is on the retire list (see decision
> **D-22**): it will be replaced with a reviewed AEAD (AES-GCM, fresh nonce, keyed identifier) only
> where genuinely required. Do not build new features on it.

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

### Desktop client (legacy WinForms)

```bash
dotnet run --project QtsBilling/QtsBilling.csproj
```

Requires `QTS_MONGODB_REMOTE`. Retained until QB-29 (gated) removes it in favour of the MAUI
Blazor Hybrid client.

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

Current migrations:

| Migration | Purpose |
| --- | --- |
| `20261019082345_InitialCreate` | 38-table schema (core, master data, catalog, inventory, sales, accounting, purchasing, platform) |
| `20261019082426_AddIntegrityGuards` | Deferred balance triggers, unique indexes, append-only / draft-vs-issued / cross-tenant FK guards |
| `20261019111310_AddIdentity` | ASP.NET Core Identity tables in the same schema |
| `20261019114425_AddLedgerSourceIdempotency` | Source-namespace uniqueness on `journal_entries` for replay safety |

---

## Running Tests

```bash
# Focused — value objects and contracts. No external services needed (41 tests).
dotnet test TestQtsBilling \
  --filter "FullyQualifiedName~TestQtsBilling.ValueObjects|FullyQualifiedName~TestQtsBilling.Contracts"

# Full suite. The 93 legacy integration tests connect to MongoDB and require QTS_MONGODB_REMOTE.
dotnet test TestQtsBilling

# Coverage.
dotnet test TestQtsBilling --collect:"XPlat Code Coverage"
```

CI (build + focused tests + integration tests against a Postgres service container + WASM bundle
check) is planned under **QB-26**.

---

## Roadmap

Per-task detail lives in [`docs/migration/tasks/`](docs/migration/tasks/); the aggregate tracker is
[`docs/migration/task-status.md`](docs/migration/task-status.md). Highlights:

- **QB-09 … QB-14** — port sales posting, cash register & payments, invoices & credits, purchasing, expenses/taxes/discounts, and financial/operational reports onto the PostgreSQL authoritative ledger (journals **QB-07** and inventory **QB-08** are already live and verified).
- **QB-16** — extract shared MudBlazor presentation into `QtsBilling.Ui` (RCL).
- **QB-17** — device-local `QtsBilling.Persistence.Sqlite` (cache, outbox, receipts, sync cursors).
- **QB-18** — `QtsBilling.Maui` Windows Blazor Hybrid desktop application (built and running).
- **QB-19** — offline cash-only capture on the device, with the provisional receipt and the pending queue.
- **QB-20** — server-side idempotent sync-push handler (`POST /api/v1/sync/operations`).
- **QB-18** — `QtsBilling.Maui` Windows Blazor Hybrid application.
- **QB-19** — cash-only offline sale capture on the device.
- **QB-20 / QB-21** — server idempotent `sync-push` handler and master-data change feed.
- **QB-22** — MAUI push/pull coordinator, conflict-resolution queue, OIDC/PKCE device flow.
- **QB-23 … QB-25** — read-only Mongo source inventory and export, deterministic transform and Postgres import, signed reconciliation report + account-reset plan.
- **QB-26** — focused tests and CI pipelines (build, tests, secret scanning, WASM bundle check).
- **QB-27** — operational runbooks, backup, packaging, observability.
- **QB-28** — end-to-end rehearsal and pilot approval.
- **QB-29** — remove WinForms (gated behind the business-owner's deletion codeword).
- **QB-30** — production cutover and post-cutover integrity verification (gated behind the business-owner's cutover codeword).

Have an idea? Open a
[feature request](https://github.com/nirzaf/QtsBilling/issues/new?template=feature_request.md).

---

## Security

- **No secrets in source control.** Connection strings and keys come exclusively from environment
  variables. `.env`, `secrets.json` and other local secret files are ignored by
  [`.gitignore`](.gitignore); use [`.env.example`](.env.example) as a template.
- **Server-side data access only.** The WebAssembly bundle (and, in the future, the MAUI client)
  carries **no database credentials, no database drivers, and no direct database connections**.
  All state changes flow through `/api/v1` behind Identity + permission policies.
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
- [.NET MAUI](https://learn.microsoft.com/dotnet/maui/) — planned Windows hybrid client
- [MongoDB.Driver](https://mongodb.github.io/mongo-csharp-driver/) — legacy data source (retiring at cutover)
- [xUnit.net](https://xunit.net/), [Moq](https://github.com/devlooped/moq) and [Bogus](https://github.com/bchavez/Bogus) — testing
- [QuestPDF](https://www.questpdf.com/) — planned PDF reporting engine

---

<div align="center">

**Maintained by [M.F.M Fazrin](https://github.com/nirzaf) and contributors.**

If you find QtsBilling useful, consider giving the repository a ⭐ on GitHub!

</div>
