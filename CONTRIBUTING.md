# Contributing to QtsBilling

Thanks for your interest in contributing to **QtsBilling**! 🎉
Whether it's fixing a bug, improving documentation, or building a new module, your help is very welcome.

Please take a moment to review this document — it helps us keep the project welcoming, consistent and
easy to maintain.

By participating in this project you agree to abide by the [Code of Conduct](CODE_OF_CONDUCT.md).

---

## Table of Contents

- [Getting Started](#getting-started)
- [How Can I Contribute?](#how-can-i-contribute)
- [Development Workflow](#development-workflow)
- [Coding Conventions](#coding-conventions)
- [Testing](#testing)
- [Pull Request Checklist](#pull-request-checklist)
- [Reporting Security Vulnerabilities](#reporting-security-vulnerabilities)
- [License](#license)

---

## Getting Started

1. **Fork** the repository and **clone** your fork locally.
2. Install the prerequisites and configure your environment by following the
   [Getting Started](README.md#getting-started) and [Configuration](README.md#configuration)
   sections of the README.
3. Make sure the solution builds and the tests pass before you change anything:

   ```bash
   dotnet build QtsBilling.sln
   dotnet test TestQtsBilling
   dotnet test QtsBilling.Persistence.Sqlite.Tests
   ```

---

## How Can I Contribute?

### Report a bug

Open an issue using the **Bug report** template. Include:

- A clear, descriptive title and steps to reproduce
- Expected vs. actual behaviour
- Your environment (OS, .NET SDK version, PostgreSQL version)
- Logs or screenshots where helpful

### Suggest a feature

Open an issue using the **Feature request** template and describe the problem you're trying to solve,
not just the solution you have in mind.

### Improve documentation

Fixes to the README, XML doc comments, or this guide are always appreciated — no issue required for
small corrections.

### Submit a code change

For anything beyond a trivial fix, please **open an issue first** to discuss the change. This avoids
duplicated effort and ensures it fits the project's direction. If your change is part of the
PostgreSQL/MAUI migration, pick a task from [`docs/migration/`](docs/migration/), satisfy its Definition
of Done, and update [`docs/migration/task-status.md`](docs/migration/task-status.md) in the same PR.

---

## Development Workflow

We use a standard **fork + branch + pull request** flow:

1. Create a topic branch from `master`:

   ```bash
   git checkout -b feature/my-feature   # or fix/my-bug
   ```

2. Make focused commits with clear messages (see below).
3. Keep your branch up to date with the upstream `master`.
4. Push your branch to your fork and open a pull request against `master`.
5. Fill in the pull request template and request review.

### Commit messages

Write clear, imperative commit messages. Prefixes help us scan history quickly:

| Prefix | Use |
| --- | --- |
| `feat:` | A new feature |
| `fix:` | A bug fix |
| `docs:` | Documentation only |
| `refactor:` | Code change that neither fixes a bug nor adds a feature |
| `test:` | Adding or correcting tests |
| `chore:` | Build process, tooling or dependency changes |

Example: `feat(pos): add multi-line discount calculation`

---

## Coding Conventions

- **Match the surrounding code.** Consistency beats personal preference.
- Target **C# 14 / .NET 10** idioms (nullable reference types are enabled — keep them clean).
- Business writes go through **minimal-API feature slices** in `QtsWebApp/Features/*` over the
  `BillingDbContext` in `QtsBilling.Persistence.Postgres`; keep data access server-side. Financial
  invariants (append-only tables, balanced journals, tenant `business_id`, optimistic concurrency) are
  enforced in the context and the schema.
- Model amounts/quantities with the provider-neutral **value objects** in `QtsModels.ValueObjects`
  (`Money`, `Quantity`, `CurrencyCode`, `ScaledDecimal`) rather than raw `decimal`/`string`.
- Shared UI lives in the `QtsBilling.Ui` RCL and consumes feature interfaces (`ICustomerApiClient`,
  `IAccountApiClient`); components never touch HTTP, storage or a database and declare no `@rendermode`.
- Add **XML doc comments** (`///`) on public types and members.
- **Never commit secrets.** All credentials and keys come from environment variables
  (see [Configuration](README.md#configuration)). If you add a new setting, update `.env.example`
  and the README configuration table.
- Run a local build and keep the [Qodana](qodana.yaml) analysis clean where practical.

---

## Testing

- New or changed logic **must** be covered by tests. We use **xUnit** for tests.
- Focused tests live in `TestQtsBilling` (value objects + contracts) and
  `QtsBilling.Persistence.Sqlite.Tests` (the device-local store); they need **no external services**.
- PostgreSQL-backed flows are verified against a running instance (`QTS_POSTGRES_CONNECTION`), e.g. the
  Docker PostgreSQL 18.6 container from the README; an automated integration + CI harness is QB-26.
- Ensure the build and the tests pass locally before opening a pull request:

  ```bash
  dotnet test TestQtsBilling
  dotnet test QtsBilling.Persistence.Sqlite.Tests
  ```

---

## Pull Request Checklist

Before requesting review, confirm that your PR:

- [ ] Builds successfully (`dotnet build QtsBilling.sln`)
- [ ] Passes all tests (`dotnet test TestQtsBilling` and `dotnet test QtsBilling.Persistence.Sqlite.Tests`)
- [ ] Adds or updates tests for any new/changed logic
- [ ] Contains **no secrets** (connection strings, keys, passwords, tokens)
- [ ] Updates documentation (README / `.env.example`) for any new configuration or behaviour
- [ ] Is scoped to a single logical change with a clear description
- [ ] References the related issue (e.g. `Closes #123`)

---

## Reporting Security Vulnerabilities

**Please do NOT report security vulnerabilities through public GitHub issues.**

Instead, report them privately so we can address them responsibly:

- Use GitHub's **"Report a vulnerability"** (private security advisory) feature under the
  repository's **Security** tab, **or**
- Follow the disclosure process in [`SECURITY.md`](SECURITY.md).

Please include as much detail as possible: the affected version, steps to reproduce, and the potential
impact. We will acknowledge receipt and work with you on a fix and disclosure timeline.

---

## License

By contributing, you agree that your contributions will be licensed under the project's
[MIT License](LICENSE).

---

Thank you for helping make QtsBilling better! 🙏
