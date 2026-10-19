<div align="center">

# QtsBilling

**An offline-first accounting system with Point of Sale (POS) and inventory management — built with .NET 10, WinForms, Blazor and MongoDB.**

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![C#](https://img.shields.io/badge/C%23-14-239120?logo=csharp&logoColor=white)](https://learn.microsoft.com/dotnet/csharp/)
[![MongoDB](https://img.shields.io/badge/MongoDB-4EA94B?logo=mongodb&logoColor=white)](https://www.mongodb.com/)
[![Blazor](https://img.shields.io/badge/UI-MudBlazor-5C2D91?logo=blazor&logoColor=white)](https://mudblazor.com/)
[![PRs Welcome](https://img.shields.io/badge/PRs-welcome-brightgreen.svg)](CONTRIBUTING.md)

</div>

---

## Table of Contents

- [About](#about)
- [Features](#features)
- [Technology Stack](#technology-stack)
- [Architecture](#architecture)
- [Solution Structure](#solution-structure)
- [Project Status](#project-status)
- [Getting Started](#getting-started)
- [Configuration](#configuration)
- [Running the Apps](#running-the-apps)
- [Running Tests](#running-tests)
- [Roadmap](#roadmap)
- [Security](#security)
- [Contributing](#contributing)
- [Code of Conduct](#code-of-conduct)
- [License](#license)
- [Acknowledgments](#acknowledgments)

---

## About

**QtsBilling** is a desktop-and-web accounting platform for small and medium businesses. It combines
double-entry accounting, a Point of Sale terminal, and inventory management in a single solution backed
by MongoDB.

The solution ships **two front ends over a shared domain and service layer**:

- a **Windows Forms** desktop client, and
- a **Blazor** web app (interactive Server + WebAssembly rendering) built with **MudBlazor**.

Both clients consume the same `QtsServices` business-logic library and `QtsModels` domain entities, so
behaviour stays consistent across desktop and web.

> **Note:** QtsBilling is under active development. The service and domain layers are substantially
> built out and covered by tests; the user interfaces are early-stage. See
> [Project Status](#project-status) and [Roadmap](#roadmap) for what works today.

---

## Features

**Domain & service layer (implemented and unit-tested)**

- **Customer management** — full CRUD, search and paging
- **Accounting** — chart of accounts, journal entries, double-entry bookkeeping, balance sheet
- **Point of Sale** — sales transaction processing
- **Inventory management** — stock levels and product tracking
- **Purchasing** — purchase orders and supplier workflows
- **Invoicing, discounts, taxes and expenses**
- **User management** — login/logout, role management, deactivation
- **System utilities** — audit logging, configuration, backup/restore, and local↔cloud sync

**User interfaces (early-stage)**

- **Windows Forms** desktop client
- **Blazor + MudBlazor** web client (Server + WebAssembly)
- The **Customers** module is currently wired end-to-end in both clients

---

## Technology Stack

| Layer | Technology |
| --- | --- |
| Language | C# 14 |
| Runtime | .NET 10 |
| Desktop UI | Windows Forms (`net10.0-windows`) |
| Web UI | Blazor Server + WebAssembly, [MudBlazor](https://mudblazor.com/) 9.x |
| Database | MongoDB via the official [MongoDB.Driver](https://mongodb.github.io/mongo-csharp-driver/) 3.12 |
| Client storage | [Blazored.LocalStorage](https://github.com/Blazored/LocalStorage) 4.5 |
| Guard clauses | [Throw](https://github.com/amantinband/throw) 1.4 |
| Testing | [xUnit](https://xunit.net/) 2.9, [Moq](https://github.com/devlooped/moq) 4.21, [Bogus](https://github.com/bchavez/Bogus) 35.6, [coverlet](https://github.com/coverlet-coverage/coverlet) 10.1 |
| Code quality | [Qodana](https://www.jetbrains.com/qodana/) (`qodana.yaml`), Dependabot |

---

## Architecture

QtsBilling follows a classic **three-tier architecture** with a shared domain core:

```
┌─────────────────────────────┐     ┌─────────────────────────────┐
│   QtsBilling (WinForms)     │     │  QtsWebApp / QtsWebApp.Client│
│     Presentation Layer      │     │     Presentation Layer       │
└──────────────┬──────────────┘     └──────────────┬──────────────┘
               │                                    │
               └──────────────┬─────────────────────┘
                              ▼
                  ┌───────────────────────┐
                  │      QtsServices      │   Business Logic + Data Access
                  │  (service-per-domain) │   (MongoDB.Driver)
                  └───────────┬───────────┘
                              ▼
                  ┌───────────────────────┐
                  │       QtsModels       │   Domain entities (POCOs)
                  └───────────┬───────────┘
                              ▼
                        ┌───────────┐
                        │  MongoDB  │
                        └───────────┘
```

- **Presentation layer** — WinForms and Blazor collect user input and render data.
- **Business logic layer** (`QtsServices`) — one service class per domain (e.g. `CustomerService`,
  `AccountingService`, `PosService`). Services encapsulate business rules, validation and MongoDB
  data access, and expose both synchronous and `async` APIs.
- **Domain layer** (`QtsModels`) — MongoDB-mapped POCOs. Most entities derive from `BaseEntity`,
  which provides `Id`, `CreatedAt`, `UpdatedAt` and `DeletedAt`.
- **Data store** — MongoDB. Collections are created on demand via
  [`DatabaseUtilities.GetOrCreateCollection`](QtsServices/UtilityServices/DatabaseUtilities.cs).

---

## Solution Structure

| Project | Type | Target | Responsibility |
| --- | --- | --- | --- |
| [`QtsModels`](QtsModels/) | Class library | `net10.0` | Domain entities and shared constants |
| [`QtsServices`](QtsServices/) | Class library | `net10.0` | Business logic, validation and MongoDB data access |
| [`QtsBilling`](QtsBilling/) | WinForms app | `net10.0-windows` | Desktop client |
| [`QtsWebApp`](QtsWebApp/QtsWebApp/) | Blazor Server host | `net10.0-windows` | Web host (Server + WASM render modes) |
| [`QtsWebApp.Client`](QtsWebApp/QtsWebApp.Client/) | Blazor WebAssembly | `net10.0-windows` | Interactive web UI (MudBlazor) |
| [`TestQtsBilling`](TestQtsBilling/) | xUnit test project | `net10.0` | Service-layer test suite |

At a glance: **~86 domain models**, **17 services**, and **93 tests** across 15 test files.

> **Platform note:** the application projects (`QtsBilling`, `QtsWebApp`, `QtsWebApp.Client`) target
> `net10.0-windows` and therefore build and run on **Windows**. The `QtsModels`, `QtsServices` and
> `TestQtsBilling` libraries are cross-platform (`net10.0`).

---

## Project Status

| Area | Status |
| --- | --- |
| Domain models (`QtsModels`) | ✅ Broad coverage |
| Service layer (`QtsServices`) | ✅ Implemented + tested |
| Automated tests | ✅ 93 tests (integration-style, require MongoDB) |
| Customers module (WinForms + Blazor) | ✅ Working end-to-end |
| POS / Inventory / Accounting UI | 🚧 Services ready, UI in progress |
| PDF reporting (e.g. invoices, statements) | 📋 Planned |
| CI/CD pipeline | 📋 Planned |

---

## Getting Started

### Prerequisites

- **[.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)**
- **Windows 10/11** — required to build and run the desktop and web apps
- **MongoDB** — either a local server ([Community Edition](https://www.mongodb.com/try/download/community))
  or a [MongoDB Atlas](https://www.mongodb.com/atlas) cluster
- An IDE such as **Visual Studio 2022** (17.12 or later) or **JetBrains Rider** (optional but recommended)

### 1. Clone the repository

```bash
git clone https://github.com/nirzaf/QtsBilling.git
cd QtsBilling
```

### 2. Configure your environment

QtsBilling reads all secrets from **environment variables** — nothing sensitive is stored in source
control. Start from the provided template:

```bash
# Review .env.example, then set the variables in your shell / user environment.
```

Set at least the primary connection string. For local-only development you can point it at your local
MongoDB:

**Windows (PowerShell — current session)**

```powershell
$env:QTS_MONGODB_REMOTE = "mongodb://localhost:27017/qts-Billing"
```

**Windows (persistent, user scope)**

```powershell
setx QTS_MONGODB_REMOTE "mongodb://localhost:27017/qts-Billing"
```

**macOS / Linux**

```bash
export QTS_MONGODB_REMOTE="mongodb://localhost:27017/qts-Billing"
```

See [Configuration](#configuration) for the full list of variables.

### 3. Restore and build

```bash
dotnet restore QtsBilling.sln
dotnet build QtsBilling.sln
```

---

## Configuration

All configuration is supplied through environment variables:

| Variable | Required | Description | Default |
| --- | --- | --- | --- |
| `QTS_MONGODB_REMOTE` | **Yes** | Primary/remote MongoDB connection string. May point at a local instance for development. | *none — throws if unset* |
| `QTS_MONGODB_LOCAL` | No | Local MongoDB connection string used by offline-capable features. | `mongodb://localhost:27017/qts-Billing` |
| `QTS_ENCRYPTION_KEY` | Only if using encryption | Base64-encoded AES key for `EncryptionService`. | *none — throws if used unset* |
| `QTS_ENCRYPTION_IV` | Only if using encryption | Base64-encoded AES initialization vector. | *none — throws if used unset* |

The connection-string accessors live in
[`QtsModels/GlobalConstants.cs`](QtsModels/GlobalConstants.cs).

**Generating an AES key/IV pair**

```csharp
QtsServices.UtilityServices.EncryptionService.GenerateKeyAndIV();
// Prints ready-to-use QTS_ENCRYPTION_KEY / QTS_ENCRYPTION_IV values.
```

> 💡 In Visual Studio you can also add these variables to a launch profile's `environmentVariables`
> section for convenient local debugging (never commit real secrets).

---

## Running the Apps

### Desktop client (WinForms)

```bash
dotnet run --project QtsBilling/QtsBilling.csproj
```

### Web client (Blazor Server + WebAssembly)

```bash
dotnet run --project QtsWebApp/QtsWebApp/QtsWebApp.csproj
```

Then open the URL printed to the console (for example `https://localhost:7124`).

---

## Running Tests

The test suite uses **xUnit** with **Moq** and **Bogus**. Several tests are integration-style and
connect to MongoDB, so configure `QTS_MONGODB_REMOTE` (see [Configuration](#configuration)) before
running them.

```bash
# Run all tests
dotnet test TestQtsBilling/TestQtsBilling.csproj

# With coverage collection
dotnet test TestQtsBilling/TestQtsBilling.csproj --collect:"XPlat Code Coverage"
```

---

## Roadmap

- [ ] Wire up UI for the POS, Inventory, Accounting and Invoicing services
- [ ] PDF report generation (financial statements, invoices, receipts)
- [ ] Role-based access control across the UIs
- [ ] CI/CD pipeline (build + test) with a MongoDB service container
- [ ] Dependency-injection refactor for service construction and testability
- [ ] Packaging/installer for the desktop client
- [ ] Broader test coverage and mocking of the data layer

Have an idea? Open a [feature request](https://github.com/nirzaf/QtsBilling/issues/new?template=feature_request.md).

---

## Security

- **No secrets in source control.** Connection strings and encryption keys are read exclusively from
  environment variables. `.env`, `secrets.json` and other local secret files are ignored by
  [`.gitignore`](.gitignore); use [`.env.example`](.env.example) as a template.
- **Rotate exposed credentials.** If a credential was ever committed to this repository's history, treat
  it as compromised and rotate it — removing it from the current source does **not** remove it from
  Git history.
- **Report vulnerabilities privately.** Please do **not** open a public issue for security
  problems. See [CONTRIBUTING.md](CONTRIBUTING.md) for the private disclosure process.

---

## Contributing

Contributions are what make the open-source community such an amazing place to learn, inspire and
create. Any contribution — from a typo fix to a new module — is **greatly appreciated**.

Please read [`CONTRIBUTING.md`](CONTRIBUTING.md) for details on our development process, coding
conventions and how to submit a pull request.

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

- [MongoDB.Driver](https://mongodb.github.io/mongo-csharp-driver/) — official MongoDB C# driver
- [MudBlazor](https://mudblazor.com/) — Material Design components for Blazor
- [xUnit.net](https://xunit.net/), [Moq](https://github.com/devlooped/moq) and [Bogus](https://github.com/bchavez/Bogus) — testing
- [QuestPDF](https://www.questpdf.com/) — planned PDF reporting engine

---

<div align="center">

**Maintained by [M.F.M Fazrin](https://github.com/nirzaf) and contributors.**

If you find QtsBilling useful, consider giving the repository a ⭐ on GitHub!

</div>
