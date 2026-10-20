# QtsBilling — SMB Completion and Production Hardening
## Single-file atomic implementation specification

**Mode:** SPEC_ONLY. **Implementation status:** not implemented by this review. **Decision:** the inspected revision is a useful foundation, but is **not ready for unrestricted production use**. A successful build and the current green CI run do not cover the financial, authorization, offline-recovery and release defects below.

<a id="summary"></a>
## 1. Summary, scope and measurable acceptance

### 1.1 Verified baseline and evidence limits

| Item | Recorded value |
|---|---|
| Repository | `nirzaf/QtsBilling` |
| Remote default branch | `master` |
| Reviewed commit | `5910493a67b6d5342d83bf6509c1c3e484a3e908` |
| Commit time | 19 October 2026, 19:29:02 UTC / 22:29:02 Asia/Qatar |
| Review date | 19 October 2026 |
| Commit subject | QB-30 cutover runbook, signed reconciliation and post-cutover integrity verification |
| Source access | Live GitHub connector; reads pinned to this SHA, not the user's local working tree |
| Repository policy | Root `AGENTS.md` returned 404; do not invent repository policy. Recheck nested/current policy before implementation. |
| Observed remote CI | Run `39311366902`, completed successfully at 19:38:35 UTC; Windows desktop build and Linux server/library/domain/SQLite jobs succeeded. |
| What CI did not establish | No PostgreSQL/API financial integration, snapshot importer integration, secret-scan, actual Windows workflow, signing or production cutover gate is present in that run's manifest. |
| User's “78 tests” and “18 gates” | Reported results, not independently reproduced. The current CI does not execute the import tests or establish that the cutover gates are trustworthy. |
| Local execution | No application builds, tests, database commands, migrations, restores, deployments or repository mutations were run. The authoring runtime has no `dotnet` executable. |
| Production evidence | No production database, frozen input, backup contents, enrolled device fleet, cash drawer, payment provider or pilot was inspected. |
| Artifact | This document only. Every proposed code/schema/UI/API/test addition is **NEW**, unless explicitly identified as existing. |

Source authority: database constraints and transactions → executable authorization/domain logic → wire contracts → clients/UI → tests and workflow manifests → documentation → historical claims. `VERIFIED` means the source or remote metadata was inspected, not that the feature was exercised in production. `INFERENCE` identifies the consequence of observed code. `RECOMMENDATION` and `NEW` are design choices. Status values are `IMPLEMENTED`, `PARTIAL`, `MISSING`, and `UNVERIFIED`; they apply to the stated capability, not the whole module. A “DONE” task document is not a substitute for these distinctions.

### 1.2 Requested outcome and interpretation

Deliver a complete **horizontal SMB baseline** for retail, wholesale/distribution and service/trade businesses: a person can configure a business, receive stock, sell online or capture cash provisionally offline, invoice and collect, purchase and pay suppliers, reconcile stock/cash/bank balances, close a period and obtain trustworthy reports through usable screens. Do not market this as covering every industry or assign an unsupported coverage percentage.

Retain .NET 10/C# 14, ASP.NET Core, EF Core/Npgsql/PostgreSQL 18, MudBlazor shared components, the HTTP-only browser and the Windows MAUI Blazor Hybrid client with SQLite. Reuse `SalePosting`, `InventoryLedger`, `LedgerWriter`, `SyncTransaction`, the contract library and the real SQLite fixture. Do not introduce microservices, a new frontend framework, a generic repository layer, a workflow engine, an event-sourcing platform or dual database authority.

**Explicit cleanup decision:** remove MongoDB/WinForms-specific code, dependencies, compatibility paths, fixtures, commands, comments and documentation from the current tracked product tree and newly produced release artifacts. Retire the source-specific migration CLI; replace its useful onboarding purpose with a typed **native QtsBilling import format**, not an adapter for an old provider. No old-client or old-provider fallback is required. This instruction does **not** authorize deletion of current financial facts, pending offline cash, applied migration history, Git history or production backups. Historical Git objects, old release assets and forks are a separate irreversible administration scope requiring the owner's exact confirmation codeword. No codeword is known to this review.

### 1.3 Non-goals and unsupported profiles

The baseline does not include payroll, manufacturing/MRP, multi-company consolidation, foreign-currency transaction accounting, tax filing/certified e-invoicing, regulated pharmacy traceability, advanced lot/serial/expiry operations, restaurant kitchen workflows, loyalty/coupon engines, online marketplaces, native Android/iOS/macOS, or offline browser storage. These are not silently represented by enums, empty screens or enabled menu entries. Products requiring unsupported costing/traceability must be blocked from that profile until a separate verified slice exists. Keep card acceptance disabled until a separately verified PSP integration is approved; no PAN/CVV fields, logs or storage are permitted.

Basic stock and non-stock service items, variants, unit/pack conversion, quotes, orders, reservations, partial delivery, invoices, credit notes, customer advances, supplier bills/payments, stock counts/transfers, expenses, bank reconciliation, tax summaries, HTML printing and bounded attachments are in scope. Simple reorder suggestions are in scope; automatic purchasing and forecasting are not.

### 1.4 Business actors and end-to-end acceptance journeys

| Journey | Required result | Principal actors | Gate |
|---|---|---|---|
| J01 — Onboard | Owner enrolment, MFA, business/currency/time-zone/period/account mappings, users, location/register and device all configured without editing SQL or source. | Owner, accountant | G2 / G5 |
| J02 — Retail online | Receipt → valued stock → register opening/funding → mixed-tax sale → exact tender/change → final receipt → partial return/refund → counted close. Every stock/cash/document effect ties to a journal. | Purchaser, cashier, manager | G1 / G5 |
| J03 — Retail offline | Bound grant + verified cache → durable cash capture → crash/restart → repeated push → one server posting/receipt; rejection retains cash liability and supports supervised recovery/refund. | Cashier, manager | G3 |
| J04 — Wholesale | Quote → confirmed order → credit-limit check → reservation → partial delivery → invoice → partial collection/advance allocation → credit/return. No duplicate stock or revenue. | Sales staff, warehouse, accountant | G6 |
| J05 — Services | Non-stock service quote/invoice → deposit/partial payment → expense → final settlement and statement; no invented inventory movement. | Owner, service staff, accountant | G6 |
| J06 — Procurement | PO is only a commitment; GRN recognizes stock/clearing; supplier bill establishes AP/input tax; payment clears AP; returns/credits are linked and capped. | Purchaser, warehouse, accountant | G6 |
| J07 — Control | Stock count and paired transfer, bank reconciliation, AR/AP ageing, ledger drilldown, period close, reproducible financial statements. | Manager, accountant | G6 |
| J08 — Operations | Restore and reconcile an isolated backup; upgrade server/device without losing pending money; deploy the exact tested/signed artifact only after owner approval. | Operator, owner | G7 |

### 1.5 Requirements and release acceptance

| ID | Requirement | Objective pass condition |
|---|---|---|
| R01 | Current-source truth and removal | No unsupported completion claim; current-tree/release dependency and content inspection finds no removed-provider/platform trace or source-format adapter. |
| R02 | Safe production admission | Any failed, missing, stale or unauthenticated prerequisite stops before a production write; approvals bind the exact environment/build/schema/evidence. |
| R03 | Identity and tenant/location isolation | Inactive/revoked users, memberships, businesses and devices lose authority; all cross-business/location resource substitutions are refused at API and FK layers. |
| R04 | Secure web/native sessions | CSRF, TLS, origin, rate-limit and reset/MFA controls pass; native login uses system-browser authorization code + PKCE, not password/cookie copying. |
| R05 | Exact pricing and payment truth | Same commercial inputs give identical server/device amounts; inclusive tax balances; customer credit is not recorded as collected cash; settlement/clearing is explicit. |
| R06 | Financial immutability | Every posted journal balances per entry/business/currency; posted document terms and lines cannot be changed via API or the application DB role; corrections are linked new facts. |
| R07 | Stock and cost authority | No unauthorized negative/oversold stock; quantity and value tie to immutable movements; receipts, returns, transfers and COGS post at the defined cost. |
| R08 | Retry and concurrency correctness | Same scoped key/body replays one stored result; changed subject/body conflicts; lost commit acknowledgement, races and process restarts never duplicate money or stock. |
| R09 | Safe offline money | Transient outcomes remain retryable; terminal outcomes are durable; rejected cash remains visible until refunded/recovered; no rebase, sign-out or cache reset deletes it. |
| R10 | Correct synchronization | Bound device feed, versioned baseline, floor/epoch and cursor validation; cache application and cursor move are atomic; tombstones and location filtering work. |
| R11 | Usable masters and POS | Every supported master can be created/edited/selected through permission-aware UI; online web checkout works; catalog and history page past the first page. |
| R12 | Order-to-cash | Quote/order/delivery/invoice/payment/credit workflows complete through UI with quantity/balance caps and one recognition source. |
| R13 | Procure-to-pay | Supplier/PO/receipt/bill/payment/return/credit workflows complete through UI and reconcile to inventory, GRNI and AP. |
| R14 | Operational accounting | Cash/bank/expense/stock-count/transfer workflows and period control are available with reasons, approvals and evidence. |
| R15 | Trustworthy reporting | Historical sales survive later refunds; quantity retains six decimals; as-of AR/AP/stock/ledger tie-outs hold under concurrent posting. |
| R16 | Native onboarding/import | Typed, bounded, authenticated native inputs only; per-aggregate atomicity, exact counts/totals, durable quarantine and reproducible signatures. |
| R17 | UI and document quality | Keyboard operation, focus/error/empty/loading/conflict states, accessible labels, desktop/browser layouts, printing and safe export/attachment handling pass. |
| R18 | Operability and delivery | Actual CI covers integration and security; release promotes tested bytes; signed desktop install/upgrade, backup recovery and pilot evidence pass. |

All listed financial equalities are exact at their declared scales. Never “fix” a failed equality by adding an unexplained suspense amount, excluding failed rows, changing a historical snapshot or accepting a globally cancelling difference.

<a id="gaps"></a>
## 2. Current-state and gap matrix

### 2.1 What to keep

PostgreSQL persistence and the portable web project graph are implemented. Typed contracts, Identity password hashing/lockout, customer CRUD, product/default-variant APIs, journal/number allocation, stock movement/balance transactions, sales, full cash refunds, basic invoices, supplier/PO/receipt routes, expenses, derived reports, audit events, real SQLite storage and Windows hosting exist. Preserve working behavior and strengthen specific gaps; do not rebuild these modules from scratch. The shared UI currently supplies Customers and local POS, not a complete administration/retail/procurement/accounting interface. The snapshot-import project is executable, but its controls do not substantiate the production claims.

### 2.2 Evidenced defects and missing capabilities

Severity is the potential consequence in the supported workflow, not a claim that a production incident occurred. For static defects, the test column in the tasks is a **planned reproducer**.

| Finding | Severity / status | Verified current evidence → consequence / target | Tasks |
|---|---|---|---|
| F01 | Critical / PARTIAL | Cutover accepts caller-provided expected codeword and continues after failed gates; string/length tests replace reliable verification. Fail closed and separate read-only admission from approved execution. [E40](#e-e40) | 01, 20–21, 61, 63–64 |
| F02 | Critical / PARTIAL | Cutover backup/SQL/CLI/HTTP targets are not cryptographically/configurationally bound to one environment; skipped import can pass; automatic device upsert is not enrolment approval. [E40](#e-e40) | 08, 20–21, 23 |
| F03 | Critical / PARTIAL | Import consumes unverified transformed files and skips missing files; reconciliation does not fail count mismatch or calculate totals. [E37](#e-e37) | 02, 18–20 |
| F04 | Critical / PARTIAL | Import raw upserts and map writes are separate; reruns reset version/timestamps and can overwrite rows; identifiers are interpolated and reference rules traverse all mappings. [E38](#e-e38) | 02, 18–20 |
| F05 | High / PARTIAL | Manifest paths are unconstrained; file hashing/read race, unbounded reads and shallow sensitive-field detection remain. The emitted report is not the exact body originally signed. [E36](#e-e36), [E37](#e-e37), [E39](#e-e39) | 18–20 |
| F06 | High / PARTIAL | Production password recovery generates a token without delivering it; setup-required response precedes password validation. [E05](#e-e05) | 06–07, 55–56 |
| F07 | Critical / PARTIAL | Permission policy checks cached claims, not current active membership/business/location authorization. [E04](#e-e04) | 04–06, 23–24 |
| F08 | Critical / PARTIAL | Numerous parent/reference FKs omit business scope despite composite-FK claims. A product/category/device/invoice/expense relationship can be structurally cross-tenant. [E06](#e-e06), [E07](#e-e07) | 05, 08, 31–33 |
| F09 | High / PARTIAL | Web secure-cookie/proxy/JSON-CSRF/rate-limit and production key-ring requirements are not fully enforced by host configuration. SameSite alone is not the selected defence. [E03](#e-e03) | 06–07, 58 |
| F10 | Critical / PARTIAL | Offline authorization reuses cached session/settings without a signed expiry-bound grant; cached session can return after API errors. [E23](#e-e23)–[E25](#e-e25) | 22–24, 29 |
| F11 | Critical / PARTIAL | Inclusive POS totals credit tax twice in the journal's revenue/tax composition. Example: tax-inclusive 115 at 15% creates DR 115 but CR revenue 115 + tax 15. [E11](#e-e11), [E12](#e-e12) | 03, 12–13 |
| F12 | High / PARTIAL | Device/UI tax calculation always adds tax and lacks canonical rounding; server and device can disagree or overflow the local scale. [E26](#e-e26) | 03, 12, 29, 35 |
| F13 | High / PARTIAL | Discount amounts lack a complete actor-limit/approval policy; input scale/enum handling is inconsistent. Catalog price/tax reads occur outside the commit revalidation boundary. [E11](#e-e11) | 03, 06, 11–12, 32–33 |
| F14 | Critical / PARTIAL | Customer-credit sales create a confirmed payment; cheque/bank/other are confirmed from a supplied reference string. No reliable settlement/clearing subledger is established. [E11](#e-e11), [E12](#e-e12) | 13, 17, 39, 45–46, 51 |
| F15 | Critical / PARTIAL | COGS uses mutable catalog purchase price or zero, not costed stock; FIFO/LIFO/AVCO settings do not prove implemented valuation. [E11](#e-e11), [E12](#e-e12) | 08, 15–16, 48, 53 |
| F16 | Critical / PARTIAL | GRN/return changes stock but omits the corresponding inventory/clearing journal. [E29](#e-e29), [E30](#e-e30) | 15–16, 44–47 |
| F17 | High / PARTIAL | First balance creation is not race-safe; the deferred stock guard can compare an intermediate NEW value with the final sum. [E09](#e-e09), [E13](#e-e13) | 09, 11, 14 |
| F18 | High / PARTIAL | API/design allow-negative policy conflicts with the non-negative database check. Prohibit negative stock consistently for this release. [E06](#e-e06), [E44](#e-e44) | 08, 14, 48 |
| F19 | Critical / PARTIAL | Invoice-line guard omits INSERT and old-parent validation; context checks do not protect all posted headers against direct SQL/state rewind. [E08](#e-e08), [E09](#e-e09) | 09, 37–38 |
| F20 | High / PARTIAL | Missing accounting period is accepted; close/post locking and overlap exclusion are incomplete; invalid time zone falls back to UTC. [E10](#e-e10) | 08, 10–11, 52–53 |
| F21 | High / PARTIAL | Body-only receipt hash can replay a receipt on another route/operation (notably purchase receipt vs return). Retry helpers can retain tracked state; unique-race outcomes require fingerprint recheck. [E14](#e-e14), [E15](#e-e15), [E29](#e-e29) | 11, 16–17, 24, 37–46 |
| F22 | Critical / PARTIAL | Sync does not establish identical full validation and device/location/user binding before committing; client fields are not authorization. [E15](#e-e15), [E16](#e-e16) | 04–05, 23–24 |
| F23 | High / PARTIAL | Some sync rejections are not persisted; same operation may later produce another outcome. [E15](#e-e15), [E16](#e-e16) | 24–25 |
| F24 | Critical / PARTIAL | SQLite permanently stores TransientRetry in its single inbox slot, so a later Posted receipt conflicts. [E21](#e-e21), [E22](#e-e22) | 25, 28–29 |
| F25 | High / PARTIAL | Coordinator does not fully use sending/retry scheduling; manual/timer overlap and receipt/error classification need a durable state machine. [E18](#e-e18), [E21](#e-e21) | 25, 28 |
| F26 | Critical / PARTIAL | Cache is mutated before “skip for conflict”; conflict resolution only marks a label; stock location and non-customer tombstones are mishandled. [E19](#e-e19) | 26–29 |
| F27 | High / PARTIAL | Page application/cursor updates are separate; unknown/malformed projections can be skipped while cursor advances; no verified baseline/floor/epoch recovery. [E17](#e-e17)–[E19](#e-e19) | 26–28 |
| F28 | Critical / PARTIAL | Rejected cash disappears from pending-money summaries; queue/profile methods do not consistently isolate a device's active business/location. [E21](#e-e21), [E22](#e-e22) | 23, 25, 27–29 |
| F29 | Critical / MISSING | Complete online browser POS is missing: current browser local-capture service is intentionally a stub. [E26](#e-e26), [E27](#e-e27) | 30, 34–36 |
| F30 | High / MISSING | Master/admin/accounting/invoice/purchasing/reporting UI is not established by Customers/local-POS pages; APIs alone are not a useful application. [E01](#e-e01), [E26](#e-e26), UI directory review | 30–36, 40, 43, 47, 49, 52, 54, 56 |
| F31 | High / PARTIAL | Manual drawer paid-in/out changes drawer only, without idempotency or counter-account journal; drawer/GL tie-out is incomplete. [E33](#e-e33) | 11, 17, 36, 51 |
| F32 | High / PARTIAL | Invoice issue creates receivable/revenue but does not define stock fulfilment or source-sale no-double-recognition; draft delete races issue. [E31](#e-e31), [E32](#e-e32) | 09, 11, 38–43 |
| F33 | High / PARTIAL | Full cash refund/credit foundations exist; partial return, unapplied customer credit payout and line-level caps remain unsupported. [E44](#e-e44) | 37–40 |
| F34 | High / MISSING | Supplier billing/AP allocation, cheque clearing, bank reconciliation and complete customer open-item controls are not supplied by PO/expense CRUD. [E28](#e-e28)–[E35](#e-e35) | 13, 31, 39, 45–46, 51 |
| F35 | High / MISSING | Quotes, reservations, partial delivery and service/wholesale end-to-end workflows require explicit NEW models and screens. | 38, 41–43 |
| F36 | High / MISSING | Paired stock transfer, approved count/variance and reorder workflow are not equivalent to allowing arbitrary movement enum values. [E13](#e-e13), [E06](#e-e06) | 14–16, 48–49 |
| F37 | High / PARTIAL | Stock report rounds quantities to four decimals; historical sales depend on mutable current status and dates use UTC boundaries rather than configured business dates. [E34](#e-e34), [E35](#e-e35) | 03, 10, 53–54 |
| F38 | High / PARTIAL | Related reports load independent queries without a common snapshot; valuation and AR/AP facts are incomplete. [E34](#e-e34), [E35](#e-e35) | 15–16, 39, 45–46, 53 |
| F39 | Critical / PARTIAL | Green CI excludes the critical integration/import/security checks; source-era local pass counts are not current test evidence. [E41](#e-e41) | 01, 57, 60, 63 |
| F40 | Critical / PARTIAL | A v-tag can build/publish new bytes without a tested-artifact/approval/signature gate. Branch protection flag was false; rulesets and external protections were not independently established. [E42](#e-e42), remote metadata | 60–61 |
| F41 | Critical / PARTIAL | Operations prose recommends local-store wipe/reinitialization/sign-out removal despite pending-money durability. [E45](#e-e45) | 25–29, 58, 64 |
| F42 | High / PARTIAL | Literal provider/platform traces remain in source comments, project files, docs and smoke blacklist; current-tree “zero” claim is false for text traces. GitHub search results are evidence, not a complete binary scan. | 02, 57, 64 |
| F43 | High / MISSING | Self-service business setup, location/account mapping, role/device administration and first-live-period setup need complete trusted workflows, not demo seeding/direct SQL. [E03](#e-e03)–[E07](#e-e07), [E40](#e-e40) | 04, 07–08, 23, 56 |
| F44 | High / UNVERIFIED | Production backups, restore/PITR targets, key custody, actual signed installation, email delivery, approval identities and pilot devices remain external evidence requirements. | 07, 20–23, 55, 58, 61–64 |
| F45 | Medium / PARTIAL | Module labels/enums/claims overstate usable capability; old docs are inconsistent and contain broken text encoding. Native package/version/feature admission needs to replace stale checklists. [E43](#e-e43)–[E45](#e-e45) | 01–02, 30, 57, 64 |

<a id="decisions"></a>
## 3. Decisions and deterministic implementation contracts

These contracts are normative for the atomic tasks. A task references them to avoid repeating a financial/security design in every recipe. Existing behavior is not implicitly endorsed where it conflicts with these contracts. Changes to a decision must cite new evidence and update its tasks/tests before implementation.

<a id="d01"></a>
### D01 — Architecture, code ownership and cleanup

**EXISTING:** PostgreSQL is authoritative; SQLite is only cache, provisional cash, durable operations and receipt/cursor state. The server owns financial and permission decisions. Shared UI owns no database connection; native host owns SQLite and platform integration; browser remains online-only.

**NEW cleanup:** remove `QtsBilling.Migration/` and `QtsBilling.Migration.Tests/` and their solution/project/workflow/document references after the replacement onboarding gate is ready, not as an unreviewed production deletion. Introduce `QtsBilling.DataImport/` for native typed CSV/NDJSON inputs and put its tests in `TestQtsBilling/DataImport/` to avoid another test project. Reuse reviewed hashing/file-reading concepts, not source-specific mapping, dynamic SQL or old identifier conversion. Only native UUID/business-owned keys and explicit external import references are accepted. No ObjectId/BSON parsing, source credential lookup or old-schema compatibility switch remains.

Remove provider/platform mentions from active documentation and source comments, including the search-evidenced files listed in Task 02. Replace negative dependency blacklists with an explicit allowed project graph. A neutral audit entry can record that a retired import path was removed without naming the retired technologies. Financial provenance stays immutable; `legacy_identifier_map` is a generic historical table rather than permission to keep an old runtime. Where current rows exist, introduce a native `external_record_links` replacement and copy/reconcile permitted provenance before separately authorized retirement; never alter already-applied migration files to disguise history. Existing migration classes contain no proof that a production database is empty. Git history/old assets are not rewritten by this plan.

**No backward compatibility:** deploy one native contract generation across server/browser/desktop. Unsupported clients receive `426 CLIENT_UPGRADE_REQUIRED`; there is no adapter or dual-write path. Existing pending financial operations must be drained, or moved through an explicitly verified one-time native conversion/quarantine before upgrade. “No compatibility” never means silently deleting cash already taken.

<a id="d02"></a>
### D02 — Domain boundary, configuration and permissions

Keep five base roles: Owner, Manager, Cashier, Accountant and Viewer. Add granular permissions in the existing `Permissions`/`RolePermissions` model rather than trusting hidden buttons. Server loads current actor, active business membership, policy version and allowed locations. Role changes/deactivation/revocation increment an authorization version and invalidate affected sessions/grants. An authenticated user can select only one of their active businesses; a device installation is enrolled to exactly one business/location/register context until a supervised, pending-queue-safe re-enrolment.

| Capability | Owner | Manager | Cashier | Accountant | Viewer |
|---|---|---|---|---|---|
| User/role/security settings | Full, MFA | No privilege escalation; only delegated invitation | No | No | No |
| Catalog/stock/purchasing | Full | Operate approved locations; adjustment limits | Read sellable catalog only | Read/cost reporting; financial posting | Read only explicitly assigned operational scope |
| Sales | Full | Post/approve discounts/returns within limits | Post; no refund/price override without approval | Read/allocate receivables | No write |
| Registers | Full | Fund, cash moves, approve variance/final close | Open/count/request close assigned register | Reconcile/read, no cashier impersonation | No |
| Journals/periods/AP/AR/bank | Full | Only explicitly delegated operations | No financial reports by default | Post/reverse/close/reconcile | Optional explicitly assigned financial read |
| Device/offline/conflict recovery | Full | Enrol/revoke/reconcile assigned locations | Use own grant; no self-enrolment or approval | Review financial consequences | No |
| Audit/export | Full | Scoped operational audit | Own receipts/current shift | Financial audit/reports | Only granted non-sensitive reports |

Use existing permission names where semantics match. NEW granular names include `business.manage`, `locations.manage`, `users.invite`, `roles.manage`, `pricing.override`, `discounts.approve`, `inventory.cost.view`, `inventory.count`, `inventory.transfer`, `cash.move`, `registers.close.approve`, `payments.confirm`, `receivables.manage`, `payables.manage`, `bank.reconcile`, `documents.manage`, `import.manage`, `sync.resolve` and `reports.financial`. Default grants must be declared in code and tested; no client-supplied permission list, tenant ID or administrator-supplied arbitrary role string can bypass these policies.

A relationship between business-owned rows must use `(id,business_id)` at both FK and principal key. A document's location, customer/supplier, variant, tax, account, session, source/reversal and allocation references must belong to that business. Location permission is additionally checked by the server; a composite FK alone does not prove the actor may use a location.

<a id="d03"></a>
### D03 — Numeric model, tax, discounts and document arithmetic

**EXISTING:** C# decimal, PostgreSQL money `numeric(18,4)`, quantity `numeric(18,6)`, rates `numeric(12,6)`, checked scaled SQLite integers. Keep these; do not introduce floating point for money or quantity. Amount inputs must fit declared scale/range before writes; reject rather than silently round submitted payments, quantities or stored unit prices. Intermediate multiplication/division may use decimal with checked overflow. All derived financial rounding uses `MidpointRounding.AwayFromZero`; do not mix with default bankers' rounding.

**NEW shared pure calculation contract:** `QtsModels/ValueObjects/CommercialAmounts.cs` accepts quantity, unit price, tax mode/rate, authorized discount and a captured pricing policy version. For each line, compute rounded gross `G=Round4(q×p)`; discount `D` is 0…G. Exclusive: revenue base `N=G−D`, tax `T=Round4(N×r)`, line payable `L=N+T`. Inclusive: `L=G−D`, `T=Round4(L×r/(1+r))`, revenue base `N=L−T`. Thus **N+T=L in both modes**. Store N/D/T/L and the policy/tax-mode/rate snapshots. The journal credits sum(N), not sum(G−D) for inclusive lines.

Header totals are sums of already-rounded lines. Allocate a header discount across eligible lines proportionally by pre-discount amount, in integer money units, distributing the remainder deterministically by line ordinal; then compute tax from each discounted line. Reject discounts that exceed actor allowance or require approval. Store approver/reason and policy version. No stacking/targeting engine is introduced.

Business setup declares currency and settlement decimal places (0…4, finance-approved; not inferred from the user's location). Header payable is rounded to the configured settlement scale; store explicit `RoundingAmount = PayableTotal − Sum(LinePayable)` and post it to the configured rounding account. It is not hidden in tax or inventory. Unit price remains four-decimal, quantity six-decimal. Offline uses the exact same policy version and functions. Historical documents keep these values unchanged if settings later change.

Tests include mixed inclusive/exclusive/exempt lines, 115 inclusive at 15% → revenue 100/tax 15, fractional quantity, half-way rounding, maximum range, zero/100% discount, settlement rounding, excessive precision, negative/undefined enum values and device/server equality. A free line is allowed under policy; a zero-total sale has no fabricated positive payment and still records authorized stock/cost consequences.

<a id="d04"></a>
### D04 — Posting, numbering, periods, immutability and lock order

A posted aggregate commits validation, authorizations, locked state, financial document and line snapshots, stock quantity/value movements, settled-payment/open-item consequences, balanced journal, scoped receipt, audit and change projections in **one PostgreSQL transaction**. No provider/network/file I/O occurs inside it. Provider confirmation is independently persisted and then consumed with an atomic compare-and-set; a client reference string is not proof.

Require one valid open accounting period for a financial effective date. Missing, overlapping, closed or locked periods fail. Posting and closing lock the same period boundary; closing cannot commit past an in-flight post. Reject invalid time-zone identifiers at setup, never silently use UTC. Store UTC event timestamps and explicit business effective dates; use configured business/location calendar boundaries for reports and numbering. Once facts exist, base currency and accounting policy cannot be casually changed.

Use a consistent order for affected locks: business write/revision anchor for transactions that emit feed changes; applicable period rows ordered by ID; document/source rows ordered by type and ID; cash sessions ordered by ID; inventory keys ordered by `(location_id,variant_id)`; numbering rows ordered by series/year. A transaction that needs several of these declares them before taking later locks. Do not add broad table locks. Profile the existing per-business commit-ordered feed serialization; do not remove ordering to gain speed. Request/transaction timeouts and bounded retries must surface stable outcomes.

PostgreSQL constraints/triggers must protect direct application-role SQL as well as EF. Posted journal headers/lines, document commercial terms, receipt/return lines, value movements and audit are immutable; allowed settlement/status transitions are narrowly defined. Validate both OLD and NEW parents on line reparenting and INSERT/UPDATE/DELETE on issued lines. A deferred consistency trigger reads **current persisted final state**, not an earlier trigger event's NEW snapshot. The application role cannot disable triggers, own tables, truncate accounting tables or edit applied migration history. Use additive migrations and quarantine inconsistent existing rows before enabling validated constraints. Corrections use new linked reversal/credit facts, never historical reprice or deletion.

<a id="d05"></a>
### D05 — Idempotency and retries

Reuse current per-aggregate keys/receipts and `ProcessedOperation`; do not build an unrelated queue or generic transaction framework. Add missing command receipts only for commands without a durable replay result (cash movement, issue/close/approval). Scope keys by business and operation kind; fingerprint includes the command kind, route subject ID, location/context and normalized semantic payload. Offline additionally binds device, grant, captured user and operation ID. A receipt for a receipt command cannot satisfy a return or another order's route.

On replay, recheck current access to the result but do not reprice or repeat side effects. Same key + same fingerprint returns the stored original result; same key + different fingerprint/subject returns `409 SAME_KEY_DIFFERENT_PAYLOAD`. On a uniqueness race, read the winning receipt in a clean context and compare the fingerprint before returning it. Persist stable terminal offline rejection once; malformed/unauthorized envelopes must not let attackers reserve other devices' keys. Transient failures, throttling, expired interactive authentication and unknown commit outcomes are **not** immutable terminal receipts.

Execution-strategy retries use a fresh context/reloaded aggregate and the same preallocated operation identity. A lost commit acknowledgement first checks the scoped receipt; never issues a new sale/key blindly. Propagate cancellation without claiming failure after a known commit. Financial request deadlines have an `OutcomeUnknown` client state resolved by lookup/replay. Online→offline fallback after an ambiguous online submit is forbidden until non-commit is established. Catch translated `DbUpdateException`/inner PostgreSQL constraint failures, not only raw `PostgresException`, and map named constraints to stable errors.

<a id="d06"></a>
### D06 — Stock quantity, reservations and perpetual valuation

Use base-unit quantity for authority; selling/purchase packs are conversion snapshots. `inventory_balances` remains unique per business/location/variant. Initialize the balance row with a scoped database upsert (or lock an existing stable variant/location anchor), then lock it; repeated lines are normalized before posting. All updates must retain quantity = sum(movements). No negative stock in this release, including adjustments; remove the contradictory override path rather than dropping its guard.

**NEW supported valuation:** moving weighted average for new native businesses. Existing FIFO/LIFO/AVCO-labelled stock requires finance-approved opening valuation before activation; do not pretend the catalog purchase price is a costing engine. Add `ValueOnHand` (4 decimals), `AverageUnitCost` (8 decimals) and authoritative `ValueDelta` (4 decimals) on cost movements. Quantity is six-decimal. Average is informative; movement value is authoritative. Receipt adds the approved net acquisition value. Issue consumes average value, with the final depletion taking the exact remaining value so quantity zero implies value zero. Reject an unvalued tracked-stock issue, rather than using zero by default. A zero-cost donation is an explicit approved opening/receipt reason.

Returned customer stock uses the original issue's allocated cost, capped by original quantity/value. Supplier returns remove current stock value; the difference from original supplier consideration is an explicit configured purchase-price variance, not a silent revaluation. Transfers carry exactly the source value to destination in one transaction (no profit). Counts/adjustments require reason, approval and inventory gain/loss journal. Reservations reduce **available**, not on-hand or ledger value; available = on-hand − active reserved. A reservation has an expiry/fulfilment/cancellation lifecycle and is converted/released in the same transaction as delivery.

<a id="d07"></a>
### D07 — Payment truth, open items and drawer custody

A sale is either paid by an explicitly settled tender, financed as customer credit, or partly settled with an open receivable. Customer credit creates an AR open item and **no confirmed payment row**. Cash is settled only on acknowledged receipt; external transfer requires an authorized operator's independent confirmation reference/evidence. Cheques enter `PendingClearance`, not bank cash; clear/bounce are new audited transitions and journals. Unsupported methods and undefined enum values fail. Cards stay disabled. Store no full card details or secrets.

Each allocation links a settlement/advance/credit to a specific same-business customer/supplier open item, amount and effective date. Lock both sides; allocation cannot exceed remaining credit or debit. Keep allocation/reversal history append-only. Overpayment is either refused or explicitly accepted as an advance; it is never hidden by changing invoice totals. Payment and invoice statuses are derived projections, not evidence replacing allocation facts. Customer and supplier control-account journals must carry party/open-item references.

Registers map to a cash account and funding source. Opening float is a carry-forward or linked cash transfer already recognized in the ledger, not new revenue or automatically duplicated cash. A paid-in/out requires a reason and approved counter-account/transfer document; drawer and journal commit once. A closing count is observational; an approved variance adjustment is separate. Closing with active offline grants/pending or rejected held cash is `AwaitingReconciliation`, not final. Late accepted operations belong to their captured session, not whichever session happens to be open later. A final closed period/session never silently reopens; supervised recovery creates current-period correcting facts with original references.

Physical cash remains represented until its custody is resolved: `PendingHeldCash`, `RejectedHeldCash`, `PostedCash` and `RefundedCash` are distinct. Sign-out/re-enrolment/upgrade cannot erase unresolved amounts. A supervisor's refund/recovery records actor, reason, amount, payment evidence and links to the original operation; “Keep local” is not a financial resolution.

<a id="d08"></a>
### D08 — Accounting recognition matrix

Account IDs come from validated business mappings, not hard-coded account numbers. Seed sensible templates, but store references and verify account kind/activity/scope before posting.

| Operation | Debit | Credit | Stock / open item |
|---|---|---|---|
| Cash POS sale | Drawer/cash for payable, rounding account as needed | Net sales revenue + output tax + rounding as applicable | COGS debit / inventory credit at movement value; stock reduced once. |
| Credit sale / standalone service invoice | Customer AR | Revenue/output tax/rounding | AR item; no fictitious payment; services have no stock. |
| Goods invoice with immediate fulfilment | Customer AR and COGS | Revenue/tax/rounding and inventory | One issue command includes dispatch; no independent duplicate sale. |
| Order delivery before invoice | Delivered-not-invoiced cost asset | Inventory | Quantity moves once; invoice later DR COGS / CR delivered-not-invoiced. Revenue recognized at invoice issue. |
| Invoice presentation of an already posted sale | None | None | Same recognition/open-item source, display-only legal document; does not re-post stock, revenue or cash. |
| Customer collection | Cash/bank or cheque-clearing as appropriate | Customer AR | Allocation reduces exact open item; pending cheque is not bank settlement. |
| Customer advance | Cash/bank | Customer-advance liability | Later allocation DR advance / CR AR; never revenue until earned. |
| Customer credit/return | Sales returns + tax adjustment | Customer AR/credit balance | Separate restock DR inventory / CR COGS at original cost; damaged/non-returned goods do not silently restock. |
| Customer cash refund | Customer credit/refund payable | Cash/bank | Capped by available credit and original tender policy; new refund fact. |
| Goods receipt | Inventory at receipt value | GRNI clearing | PO itself posts nothing; received goods and receipt journal commit together. |
| Supplier bill matched to receipt | GRNI + approved input tax/price variance | Supplier AP | No second stock increase; AP open item and duplicate supplier-invoice guard. |
| Unbilled supplier return | GRNI, with explicit value variance if needed | Inventory | Linked original receipt and quantity/value cap. |
| Billed supplier return | Supplier-return clearing, with explicit variance | Inventory | Supplier credit DR AP / CR return clearing + input-tax adjustment. |
| Supplier payment/advance | Supplier AP / supplier-advance asset | Cash/bank/clearing | Append-only allocation and settlement lifecycle. |
| Operating expense | Expense and approved recoverable input tax | Cash/bank or AP | Receipt/evidence; not automatically COGS. |
| Drawer/warehouse transfer | Destination cash/inventory | Source cash/inventory | Paired movements; no profit or unexplained balance change. |
| Count/approved variance | Inventory loss/shortage or asset | Asset or inventory gain/overage | Reason/approval; posted correction only. |

Taxes, deductibility, numbering and settlement rules require business configuration and finance validation; this is not a jurisdictional-compliance certification. Defaults block unsupported tax/costing cases instead of inventing legal policy.

<a id="d09"></a>
### D09 — Commercial lifecycle and immutable documents

Quotes: Draft → Sent → Accepted/Rejected/Expired/Cancelled; no ledger or stock. Orders: Draft → Confirmed → PartiallyFulfilled → Fulfilled/Closed; cancel only remaining quantities and release reservations. Price changes on a draft recalculate visibly; accepted quote/order commercial terms are versioned snapshots, with approved amendments rather than silent edits. Shipment/delivery notes refer to quantities and order lines; one fulfilment cannot be invoiced twice.

Invoices: Draft → Issued → PartiallySettled/Settled, with separate credits/void facts. Issue snapshots seller/customer legal identity, currency, tax policy, terms, line descriptions, units/conversions, prices, discount/tax amounts, due date and numbering. A later customer/business/catalog edit cannot alter a historical print. Draft deletion/issue/edit all lock the same row and enforce an expected version. Voiding is only allowed when no settlement/fulfilment consequences would be lost; otherwise use a linked credit/return. Partial credit and partial refund are capped per original line and value, not only by header total.

Purchases: Draft → Ordered → PartiallyReceived → Received/Closed. Amendments after ordering preserve the original version; do not lower ordered quantity below fulfilled quantity. Returns link a specific receipt line and do not automatically reopen the original PO for replacement. A replacement is an explicit approved amendment/new order. Supplier bills are separate from PO/GRN and have supplier-reference uniqueness scoped to business/supplier. Basic supplier and customer statements include open/settled/overdue documents, allocations and advances.

<a id="d10"></a>
### D10 — Native identity and offline authorization

**NEW:** OpenIddict server/client 7.7.2 is the reviewed stable release baseline (official release dated 10 October 2026; see external sources). Use authorization code with PKCE S256 and the system browser for Windows. No password grant, embedded login webview, client secret in desktop, or copying the web application's cookies. Web may keep secure same-origin Identity cookies. The API validates native bearer issuer/audience/lifetime and live authorization. Exact redirect URIs, state/nonce and single-use code validation are mandatory; certificate/key rotation and persistent token stores are production prerequisites.

Owner/authorized manager enrols a generated device identity to business/location/register; the server returns a bound device session and signed offline-cash grant. Grant fields: `grantId`, user/device/business/location/register/session IDs, permission/policy versions, currency/settlement scale, `issuedAtUtc`, `expiresAtUtc`, issuer/audience/key ID. Default maximum offline grant is **8 hours**, never more than 24; this is a NEW safe default, not existing enforcement. Server may shorten it. The device verifies signature and identity using pinned trusted issuer keys acquired online, never a private server key. Cached `SessionDto` alone never grants offline rights. An explicit 401/403/revoked response suspends new capture; it does not fall back to stale permission.

Signed grant credentials stay in Windows secure storage; SQLite stores only grant ID, digest and non-secret captured policy metadata. The server retains grant identity for pending-operation verification/recovery, so logout does not destroy cash provenance. Use server time plus monotonic elapsed time within a process; detect significant backwards clock movement across restart and fail closed for new capture until online renewal. Do not claim perfect clock trust or instantaneous offline revocation. Revocation lag is bounded by grant expiry and requires pilot acceptance. At upload, server checks current authority and the grant/operation binding; an expired/revoked/closed-context operation goes to durable review with cash preserved, not automatic historical backdating. OIDC alone does not establish device enrolment or location authority.

<a id="d11"></a>
### D11 — Offline operation and receipt state machines

A captured sale stores immutable native payload, payload schema/policy generation, operation/device/user/context IDs, original grant ID/digest (not its credential), captured timestamp, line/amount snapshots, original cash session and fingerprint atomically with provisional cash and outbox. Show a provisional receipt only after durable commit. Maintain the same operation identity for ambiguous retries; editing a captured sale means a new linked recovery command, never changing its payload under the original key.

Outbox: `Pending → Sending → Acknowledged` for a terminal posted result; `Sending → RetryScheduled` for transient/429/network/unknown response; `Sending → NeedsReview` for a durably rejected business operation. Authentication/device suspension pauses new sends/capture until renewal/review; it does not synthesize an irreversible financial rejection from every HTTP 409/403. Terminal `InboxReceipt` is reserved for `Posted/AlreadyPosted/NeedsReview` with server result identity/hash. A transient observation updates attempts/backoff/error only and cannot block a later success. Duplicate terminal receipts compare semantic stable fields; timestamps or response omission must not manufacture a conflict.

The coordinator is single-flight across timer/manual triggers, partitions operations by enrolled context, marks Sending transactionally before I/O, and schedules capped exponential backoff with jitter. Startup recovers interrupted Sending to Pending only after checking local terminal receipts. A permanently failed operation is never retried with a new identity automatically. Rejected cash is a custody obligation until linked refund/recovery; retain immutable records and an operator-visible exception queue. No outbox blanket purge.

<a id="d12"></a>
### D12 — Change-feed, baseline and cache semantics

Existing commit-ordered `(revision,ordinal)` feed remains, but every request is bound to authenticated device/business/location/permissions. Feed projection IDs distinguish product from variant. Stock payload location must equal the authorized destination; never remap another warehouse's stock into the local warehouse. Product inactivity disables all affected cached variants. Apply tombstones for all subscribed entity types and preserve pending sale snapshots separately.

**NEW baseline protocol:** server produces a consistent, paged baseline for a device subscription at one repeatable-read boundary, with `epoch`, schema version, subscription hash, floor and high-water cursor. A small PostgreSQL-backed baseline lease stores bounded immutable pages/checksums until expiry, avoiding a long-lived transaction across HTTP requests. Subsequent writes follow the high-water cursor. Client stages a complete baseline, verifies page continuity/checksums/scope, then swaps only cache tables and stores the cursor under the SQLite write gate in one transaction. Pending money is untouched. Expired lease/floor mismatch/changed subscription requires a new baseline; never assume replay from zero reconstructs data that predates feed emission.

Each incremental page validates schema, scope, order and previous cursor, stages rows, and commits cache changes + tombstones + review records + next cursor atomically. Unknown financial/master projection versions are explicit upgrade/resync failures, not silently skipped rows. Replays are monotonic no-ops. Existing immutable pending sale terms need not freeze the live catalog: apply current server catalog while flagging affected pending operations for supervised review; never mutate their original captured values. Review decisions have real effects, approval and audit, not just a `ResolvedAtUtc` flag.

<a id="d13"></a>
### D13 — Native onboarding import and reconciliation

Native import supports only explicit QtsBilling record types: business-owned customers, suppliers, units/categories/products/variants, configured account openings, valued stock openings, outstanding customer/supplier documents and advances. Openings are posted through the same invariant-preserving services and an explicit approved opening period. Do not import arbitrary tables, passwords, historical provider encodings, or raw posted-row upserts. New-account invitations establish credentials.

A native manifest binds schema version, business ID, batch ID, capture/effective date, file relative paths, row counts, lengths and SHA256 hashes. Signed approval binds that exact manifest, mapping version (native field mapping only), expected target schema/build and permitted record types. Validate paths under a chosen root, reject symlinks/path traversal/duplicate files/unexpected files and impose byte/row/depth limits. Hash and parse a stable read-only snapshot; recursively reject secret/PAN/CVV fields and invalid NDJSON entries before import. Stage outside production; no log/raw quarantine leaks sensitive values.

Use a transaction per complete aggregate, never per journal row. Batch ID is an audit grouping, not permission to recreate a prior source fact: also enforce unique `(business_id,dataset_namespace,record_type,external_id)` and derive stable target identity without batch ID. Re-import through another batch compares the prior payload/target rather than creating another opening. Unique `(business_id,batch_id,record_type,external_id)` links store target ID and canonical source fingerprint. Exact repeat is a no-op; changed body or mapping under the same identity conflicts. Never reset created-at/version or overwrite native financial rows. Missing parents, duplicate natural keys and unmappable data are durably quarantined with safe source identity, reason and masked diagnostics. Counts reconcile `source = accepted + quarantined`, accepted IDs exist exactly once, and every money/quantity/open-item total is compared in declared units. A batch is not accepted because total journal debits and credits globally cancel.

A report is built once, canonicalized once, hashed and signed over exactly the emitted payload. Store algorithm, key ID, manifest/build/schema/database identity, counts, per-business/per-document totals and exception IDs. Verify with constant-time comparison using the independent verifier key; no length/regex test. HMAC authenticates evidence to key holders, **not** a business owner's approval; signed approval is a separate trusted identity/environment gate. Exact exceptions can be approved only if they are classified non-financial informational exclusions. Unbalanced/missing/duplicate financial facts, unowned rows and unexplained custody differences cannot be waived by a wildcard string. A report with missing totals fails closed.

<a id="d14"></a>
### D14 — UI, documents, performance and delivery

Shared MudBlazor pages consume typed feature clients/interfaces; hosts register implementations and own render modes/assets. Use complete loading/empty/error/permission/validation/stale-version/outcome-unknown/offline states. Do not return empty data to disguise failure. Server-side cursor paging/search replaces first-100-row truncation. Cancel superseded searches, preserve user edits on validation errors, and require explicit review when a command's price/version changed. Keyboard/tab/focus, screen-reader labels and responsive usable targets are acceptance criteria; no new translations or platforms are assumed.

HTML invoice/receipt/statement print remains the smallest supported document output; browser print-to-PDF is not claimed to be a server PDF engine. Render historical snapshots with HTML encoding, no executable content/external tracking assets. CSV exports neutralize formula injection, use declared decimal/date formats, preserve six-decimal quantities and are tenant/permission scoped. Attachments are bounded PDF/PNG/JPEG, magic-byte validated, stored outside webroot under opaque IDs, retrieved through authorization, never executable/HTML/SVG. Receipt numbers, references and filenames must be encoded. Email is delivered by one configured SMTP adapter through a durable transactional delivery outbox; do not call a provider inside a financial transaction.

**NEW capacity target for acceptance, not a measured result:** seed 50,000 variants, 100,000 customers, 1,000,000 journal lines and 20 concurrent online terminals in one business plus an isolation-control business; a device has 10,000 cached variants and 5,000 pending operations. On the documented reference environment, target p95 ≤ 750 ms for paged reads and ≤ 1.5 s for ordinary online sale posting (excluding provider I/O), 0 lost/duplicate effects and no unbounded memory growth. Large reports may be bounded asynchronous exports only after measurement justifies it; do not add workers/caches speculatively. Record hardware/data/query plans and observed results; failing targets require measured tuning, not removing invariants.

<a id="d15"></a>
### D15 — Production release, recovery and evidence

CI must execute real PostgreSQL 18 integration tests using disposable databases and application-role SQL guard tests, plus existing domain/SQLite suites, native import tests, shared UI smoke, focused browser journeys, secret/dependency/bundle checks and Windows build. Development runs only affected test filters; full regression is a CI/release responsibility. Existing shell smoke scripts are not substitutes for assertions. No test may inherit a production connection variable; fixtures require an allowlisted disposable target and random database name.

Release builds once at a verified SHA; promote the same digested artifacts through staging/pilot/production. A tag does not imply test or owner approval. Pin workflows/dependencies, use least-privilege tokens, required status checks/protected environments and trusted signing. Windows target remains the current Windows TFM; test signed MSIX installation/upgrade where packaged deployment is chosen. The current unpackaged ZIP is not signed installation evidence. Verify publisher/manifest version and never bundle production credentials.

Read-only admission verifies artifact/schema/environment identity, current backup hash + demonstrated restore, complete reconciliation, closed import/freeze state, native-client generation, device/pending-cash status, permission/identity readiness and read-only canary results. An explicit trusted owner codeword/approval and change window are separate prerequisites for production writes. A failed prerequisite aborts; skip is not pass. Remove auto-enrolment, demo identity and wildcard-exception approval from cutover.

Proposed recovery targets: PostgreSQL RPO ≤ 15 minutes and RTO ≤ 4 hours, subject to operator acceptance and measured drill. Nightly dumps alone do not achieve the proposed RPO: select managed PITR/WAL backup appropriate to the actual hosting environment. Back up key rings, native signing/issuer keys, attachments and configuration alongside database recovery metadata. Restore into isolated targets and reconcile per entry/document/stock/open item, not just row counts. Device recovery preserves its whole consistent pending-money database/WAL snapshot; corrupted stores stop capture and require supervised recovery, never wipe-and-reseed. After new financial writes, rollback is restore/reconcile under separate authorization or fix-forward—not a blind binary downgrade or return to a retired datastore.

<a id="d16"></a>
### D16 — Business decisions and safe defaults

Proceed with one base currency per business, configurable location/time zone, no negative stock, exact reconciliation, simple non-stacking discounts, moving weighted average only for new native stock, cash-only offline capture, an eight-hour bounded grant, online administration and no card acceptance. These are implementation defaults, not inferred production approvals. Existing valuations, tax/numbering/cash-rounding policy, email/signing key custody, retention, RPO/RTO, actual pilot participants and production approvals remain explicit external/finance gates. Do not ask agents to invent accounting policy when source data conflicts.


<a id="d17"></a>
### D17 — Deterministic integration bindings and native API additions

These are **NEW design contracts**, not descriptions of endpoints currently deployed. Existing route registrations in the evidence registry remain authoritative where unchanged. Do not implement a generic router that writes arbitrary entities merely because the route table groups similar resources.

**Shared file scope.** A task that adds an entity also modifies `QtsBilling.Persistence.Postgres/BillingDbContext.cs` (DbSet/registration) and the appropriate `BillingDbContext.Model.cs` or `BillingDbContext.Model.Financial.cs` configuration; new SQLite fields modify `QtsBilling.Persistence.Sqlite/LocalStoreDbContext.cs`. All new business entities use native `Guid Id`, `Guid BusinessId`, immutable UTC creation time, `long Version` for mutable drafts, scoped composite FKs and restrictive deletion for historical sources. Add explicit snake-case tables/indexes using the existing naming convention. A command needs no new generic repository/unit-of-work abstraction.

A new `XEndpoints` class has `MapXEndpoints(IEndpointRouteBuilder)` registered explicitly in `QtsWebApp/QtsWebApp/Program.cs`; a new service is registered with its correct scoped lifetime there, never singleton DbContext. UI task feature clients are created in the matching `QtsBilling.Contracts/<Feature>/<Feature>ApiClient.cs` alongside their interface, with method names matching the native command (`ListAsync`, `GetAsync`, `CreateAsync`, `UpdateAsync`, `PostAsync`, `ReverseAsync`, plus named lifecycle verbs). Registration is explicit in both `QtsWebApp/QtsWebApp.Client/Program.cs` and `QtsBilling.Maui/MauiProgram.cs`, with the server prerender registration in the server Program where required. `QtsBilling.Ui` continues to reference Contracts, not persistence. Its pure calculation facade may transitively consume provider-neutral QtsModels, never EF/Npgsql/SQLite.

Stable NEW error constants are added only as needed in `QtsBilling.Contracts/Abstractions/ApiErrorCodes.cs`: `CLIENT_UPGRADE_REQUIRED`, `OFFLINE_GRANT_EXPIRED`, `PENDING_CASH_UNRESOLVED`, `VALUATION_REQUIRED`, `APPROVAL_REQUIRED` and the existing conflict/resync codes. `OutcomeUnknown` is a client state, not a fabricated server terminal receipt. Permissions are declared in `QtsBilling.Contracts/Identity/IdentityDtos.cs` and mapped/enforced in `QtsWebApp/QtsWebApp/Infrastructure/QtsIdentity.cs`. Every state-changing endpoint runs current BusinessActor/location checks, not only `.RequireAuthorization`. Existing declarations are reused only when their semantics match D02. UI route assembly registration stays in `QtsWebApp/QtsWebApp.Client/Routes.razor` and `QtsBilling.Maui/Components/Routes.razor`; shared pages do not set host render mode. Existing `QtsBilling.Ui/Layout/INavContributor.cs` remains the native extension point. These exact integration paths are part of the owning task's B scope only when required for that task.

| Owning task | NEW HTTP surface (prefix `/api/v1` unless stated) | Required policy and principal contract fields |
|---|---|---|
| 07–08 | `/businesses/current`, `/businesses/current/settings`; `/locations`; `/users/invitations`; `/auth/mfa/*`; `/auth/business-selection` | `business.manage` / `locations.manage` / `users.invite`; expected Version, explicit business identity/currency/time zone/settlement scale/account mappings; user-selected business must be an active membership. Initial bootstrap remains a protected operator process, not open registration. |
| 23 | `/devices/enrolments`, `/devices/{id}/revoke`, `/devices/{id}/offline-grants` | `devices.manage` for lifecycle; device-bound grant renewal under current `sales.post`; location/register/session IDs, challenge, device identity and policy version. |
| 26 | `/sync/baselines` POST; `/sync/baselines/{id}/pages/{page}` GET; extend existing changes endpoint | Current enrolled device and subscribed permissions; epoch/schema/subscription hash/high-water, page counts/digests/lease; no caller-chosen tenant. |
| 32 | `/catalog-masters/units`, `/categories`, `/brands`, `/product-groups` under `/catalog-masters` | Existing product-read/product-write plus D02 limits; Guid ID/parent, name/code, active, Version; resource-specific typed validation. Existing pricing routes remain native. |
| 33 | `/products/{id}/variants` POST; `/products/{id}/variants/{variantId}/sell-units` GET/POST; `/.../sell-units/{unitId}` PUT | `products.write`; variant/base-unit identity, label, exact base factor, four-decimal price, barcode, effective version, active. |
| 37 | `/sales-returns` POST/GET; `/sales-returns/{id}` GET; `/sales-returns/{id}/refunds` POST | `sales.refund` and authorized stock/settlement scope; source sale/invoice and line IDs, qty, disposition, reason, payout account/session, Idempotency-Key. Current full-refund route calls the same native core or is removed with all current callers updated; no old-system adapter. |
| 39 | `/receivables/open-items` GET; `/receivables/receipts` POST; `/receivables/allocations` POST; `/receivables/credit-payouts` POST | `receivables.manage` / `payments.confirm`; customer, source IDs, amount/currency, effective date, settlement evidence, explicit item allocations and reason. |
| 41–42 | `/quotes`, `/sales-orders` list/create/detail/draft update; named `/send`, `/accept`, `/reject`, `/confirm`, `/amend`, `/cancel-remainder`; `/sales-orders/{id}/reservations`, `/deliveries` | `sales.post` plus dedicated `orders.manage` / `inventory.dispatch` NEW; source revision, native lines and terms; no generic arbitrary status update. |
| 45–46 | `/supplier-bills` list/create/detail/update draft; `/{id}/post`, `/{id}/credits`; `/payables/open-items`, `/payables/payments`, `/payables/allocations` | `payables.manage` / `payments.confirm`; supplier reference, receipt-line matches, due date/tax/variance, settlement/account evidence and exact capped allocations. |
| 48 | `/inventory/counts` create/read/observations/approve/post; `/inventory/transfers` create/read/post | `inventory.count` / `inventory.transfer` plus variance approval; location(s), baseline version/time, observed or moved base quantities, source lines/reason and key. |
| 51 | `/bank-accounts`, `/bank-statements/import-preview`, `/bank-statements`, `/{id}/matches`, `/{id}/finalize` | `bank.reconcile`; scoped account, native file digest/schema, period/opening/closing balances and explicit statement/settlement allocations. |
| 55 | `/documents` POST; `/documents/{id}` GET; `/{id}/retire` POST; `/invoices/{id}/email` POST; `/messages/{id}` GET | `documents.manage` plus original business-document access; opaque subject/file IDs, safe content type/digest, recipient confirmation and exact document version. |

**Request/result model.** New mutable drafts expose `Version` and honor If-Match. Native financial commands carry a required stable idempotency header and effective business date; their body never accepts authoritative running balances, journal totals, actor permission lists or tenant substitution. Each result contains resource ID/number, state, currency, exact totals, Version where mutable, original source links and correlation ID. Lists use the existing paging envelope with explicit next cursor; pagination identity binds filters/scope. Detail DTOs never expose internal entity graphs or secret fields.

**Additional minimal schema keys.** `OpenItem` stores business/party/source kind+ID, document/effective/due dates, original amount/currency and journal source; open balance is derived. `Allocation` stores immutable payment/credit/item IDs and positive exact amount with unique command/source key. `Reservation` stores order-line/location/variant and remaining base quantity; `DeliveryLine` stores original order-line, base quantity, original cost ValueDelta and invoiced quantity allocation. `SupplierBillLine` stores receipt-line ID, matched quantity/net/tax/variance and original legal terms. Counts/transfers keep immutable approved baselines and movement groups. File/message/baseline leases are operational records with bounded retention, not a second financial ledger. Add only fields required by these recipes.

**Migrations.** The checked PostgreSQL source head is `20261019131237_HardenPurchaseOrderLineReceiptGuard`; the checked SQLite tree contains `20261019171350_AddSyncConflicts` as its latest migration. Neither establishes which migrations are applied to a production database. Generate new timestamped migration + designer + model snapshot from the owning task's logical name; timestamps cannot truthfully be pre-invented. Before approval, record actual generated paths/SQL, verify model alignment and test upgrade from the pinned schema with representative existing/pending records. New roles, permissions, DTOs, configuration fields, DB model entries and project references must ship with the owning runtime task, not be omitted as “integration later.”

**Execution sizing.** A task with several distinct endpoint bodies is a bounded vertical slice, not permission for a broad refactor. Implement its individual recipe steps sequentially and review the declared diff. Where contracts/model/registration create more than three production files, the named gate explains the inseparable integration; do not split a money invariant across independent deployments. Never create unrelated abstractions to meet an arbitrary file count.

<a id="task-index"></a>
## 4. Dependency-ordered task index and integration gates

The ordering below is deliberate. A task may be developed on its own branch with focused tests, but a partial gate is not releasable. Generated EF migrations include their designer/snapshot companions and are inseparable from the corresponding schema/runtime tests. Larger vertical slices that touch contracts, host wiring and shared UI are explicitly coupled; do not split them into independently deployable unsafe fragments.

| Gate | Scope | Release condition |
|---|---|---|
| G0 — Baseline/safety | Verified source, test harness, disabled unsafe production admission and current-tree cleanup plan | No claim of production approval; deterministic source/test inventory. |
| G1 — Money and data integrity | Exact arithmetic, DB invariants, periods, retry/stock/value/settlement/drawer fixes | All critical financial/race reproductions pass on PostgreSQL; no silent data repair. |
| G2 — Identity and onboarding | Current authorization, composite scope, secure web/native identity, settings | Real enrolment/recovery and cross-tenant/location negative tests pass. |
| G3 — Offline safety | Device grants, durable outcomes, atomic cache/cursor, coordinator and cash custody | Crash/retry/revocation/resync/rejection journeys preserve all money. |
| G4 — Native data/admission | Native import, reconciliation and fail-closed readiness | Authenticated manifest/report and exact financial tie-outs; no old-source path. |
| G5 — Daily retail usability | Shared shell, masters, online POS, registers, customer/supplier screens | Cashier/manager can complete J01–J03 without SQL/API tools. |
| G6 — SMB workflows | Orders, AR/AP, procurement, expenses, stock control, bank/accounting/reports | J04–J07 pass through UI and reconcile to posted facts. |
| G7 — Production candidate | CI, signed artifacts, backup recovery, Windows/browser pilot and final trace scan | All applicable gates + independent owner/environment approvals; no paper-only PASS. |


| Task | Priority | Dependencies | Gate | Primary path |
|---|---|---|---|---|
| [01 — Establish a safe PostgreSQL/API regression harness and release-blocker ledger](#task-01) | P0 | — | G0 | `TestQtsBilling/TestQtsBilling.csproj` |
| [02 — Remove retired provider/platform traces and isolate native onboarding](#task-02) | P0 | 01 | G0 + G4 | `QtsBilling.sln` |
| [03 — Define one exact commercial amount contract for server and device](#task-03) | P0 | 01 | G1 | `QtsModels/ValueObjects/CommercialAmounts.cs` |
| [04 — Enforce current business and location authority on every sensitive operation](#task-04) | P0 | 01 | G2 | `QtsWebApp/QtsWebApp/Infrastructure/QtsIdentity.cs` |
| [05 — Complete composite tenant foreign keys and location-bound references](#task-05) | P0 | 01, 04 | G2 | `QtsBilling.Persistence.Postgres/BillingDbContext.Model.cs` |
| [06 — Harden the HTTP authentication and validation boundary](#task-06) | P0 | 01, 04 | G2 | `QtsWebApp/QtsWebApp/Program.cs` |
| [07 — Complete trusted owner enrolment, invitations, password recovery and MFA](#task-07) | P0 | 04, 06 | G2 | `QtsWebApp/QtsWebApp/Features/Auth/AuthEndpoints.cs` |
| [08 — Provide native business, location and financial setup](#task-08) | P1 | 04, 05, 07 | G2 | `QtsWebApp/QtsWebApp/Features/Setup/BusinessSetupEndpoints.cs` |
| [09 — Enforce posted-document immutability and final-state database guards](#task-09) | P0 | 01, 05 | G1 | `QtsBilling.Persistence.Postgres/BillingDbContext.cs` |
| [10 — Make journal posting and period close mutually consistent](#task-10) | P0 | 08, 09 | G1 | `QtsWebApp/QtsWebApp/Features/Accounting/LedgerWriter.cs` |
| [11 — Make all financial commands scope-safe under duplicate and uncertain retries](#task-11) | P0 | 01, 04, 09, 10 | G1 | `QtsBilling.Contracts/Serialization/CanonicalJson.cs` |
| [12 — Repair POS price, inclusive-tax, discount and posting validation](#task-12) | P0 | 03, 04, 08, 10, 11 | G1 | `QtsWebApp/QtsWebApp/Features/Sales/SalePosting.cs` |
| [13 — Separate actual settlement, customer credit and payment clearing](#task-13) | P0 | 03, 04, 08, 10, 11, 12 | G1 | `QtsBilling.Persistence.Postgres/Entities/Sales.cs` |
| [14 — Make stock balance creation and movement posting race-safe](#task-14) | P0 | 05, 09, 10, 11 | G1 | `QtsWebApp/QtsWebApp/Features/Inventory/InventoryLedger.cs` |
| [15 — Introduce real perpetual stock valuation and COGS](#task-15) | P0 | 03, 08, 10, 11, 14 | G1 | `QtsBilling.Persistence.Postgres/Entities/Inventory.cs` |
| [16 — Post goods receipts and returns to inventory and clearing accounts atomically](#task-16) | P0 | 10, 11, 14, 15 | G1 + G6 | `QtsWebApp/QtsWebApp/Features/Purchasing/PurchasingEndpoints.cs` |
| [17 — Reconcile cash-drawer custody with the general ledger](#task-17) | P0 | 10, 11, 13 | G1 + G3 | `QtsWebApp/QtsWebApp/Features/Sales/CashRegisterEndpoints.cs` |
| [18 — Validate a provider-neutral native onboarding package before reading business rows](#task-18) | P0 | 01, 02, 03, 05 | G4 | `QtsBilling.DataImport/NativePackageReader.cs` |
| [19 — Apply native masters and approved openings with atomic source identity](#task-19) | P0 | 08, 09, 10, 11, 15, 18 | G4 | `QtsBilling.DataImport/NativeImportWriter.cs` |
| [20 — Verify reconciliation content, cryptographic authenticity and individual approvals](#task-20) | P0 | 18, 19 | G4 | `QtsBilling.DataImport/ReconciliationVerifier.cs` |
| [21 — Replace the cutover script with fail-closed environment-bound admission](#task-21) | P0 | 01, 02, 07, 17, 20 | G4 + G7 | `scripts/qb30-cutover.ps1` |
| [22 — Implement a standards-based native identity endpoint with PKCE](#task-22) | P0 | 04, 05, 06, 07 | G2 | `QtsWebApp/QtsWebApp/Features/Auth/NativeAuthorizationEndpoints.cs` |
| [23 — Bind Windows sign-in and offline grants to the enrolled device](#task-23) | P0 | 08, 17, 22 | G2 + G3 | `QtsWebApp/QtsWebApp/Features/Devices/DeviceEndpoints.cs` |
| [24 — Unify sync-push validation and durable terminal outcomes](#task-24) | P0 | 11, 12, 13, 14, 15, 17, 23 | G3 | `QtsWebApp/QtsWebApp/Features/Sync/SyncOperationEndpoints.cs` |
| [25 — Repair local receipt state, retry transitions and unresolved cash totals](#task-25) | P0 | 03, 17, 24 | G3 | `QtsBilling.Persistence.Sqlite/SqliteLocalStore.cs` |
| [26 — Provide a complete device-bound change feed and resync baseline](#task-26) | P0 | 04, 05, 11, 23, 24 | G3 | `QtsWebApp/QtsWebApp/Features/Sync/SyncChangeEndpoints.cs` |
| [27 — Apply cache, tombstones, conflicts and cursor in one SQLite transaction](#task-27) | P0 | 25, 26 | G3 | `QtsBilling.Persistence.Sqlite/SqliteLocalStore.Sync.cs` |
| [28 — Make device synchronization single-flight, resumable and scope-safe](#task-28) | P0 | 23, 24, 25, 26, 27 | G3 | `QtsBilling.Maui/Services/DeviceSyncCoordinator.cs` |
| [29 — Complete offline checkout, provisional receipt and rejected-cash recovery](#task-29) | P0 | 03, 17, 23, 25, 27, 28 | G3 | `QtsBilling.Persistence.Sqlite/Pos/LocalPosStore.cs` |
| [30 — Create a permission-aware shared application shell and complete paging](#task-30) | P1 | 04, 06, 08, 29 | G5 | `QtsBilling.Ui/Layout/NavMenu.razor` |
| [31 — Complete customer and supplier master terms and native party identity](#task-31) | P1 | 04, 05, 08, 11 | G5 | `QtsWebApp/QtsWebApp/Features/Customers/CustomerEndpoints.cs` |
| [32 — Expose complete native catalog and pricing master workflows](#task-32) | P1 | 03, 04, 05, 08, 11, 26 | G5 | `QtsWebApp/QtsWebApp/Features/Catalog/CatalogMasterEndpoints.cs` |
| [33 — Implement explicit variants, sell units, pack conversions and barcode lookup](#task-33) | P1 | 03, 05, 12, 14, 15, 32 | G5 | `QtsWebApp/QtsWebApp/Features/Catalog/ProductEndpoints.cs` |
| [34 — Deliver complete catalog, master and trading-party screens](#task-34) | P1 | 30, 31, 32, 33 | G5 | `QtsBilling.Ui/Pages/Catalog/Products.razor` |
| [35 — Enable real online POS checkout in browser and desktop](#task-35) | P1 | 12, 13, 14, 15, 17, 29, 30, 33, 34 | G5 | `QtsBilling.Contracts/Sales/SaleApiClient.cs` |
| [36 — Complete register, sale history, receipt and custody screens](#task-36) | P1 | 17, 30, 35 | G5 | `QtsBilling.Ui/Pages/Sales/RegisterSessions.razor` |
| [37 — Implement capped partial returns, credits and refund settlement](#task-37) | P1 | 10, 11, 13, 15, 17, 35, 36 | G6 | `QtsWebApp/QtsWebApp/Features/Sales/RefundEndpoints.cs` |
| [38 — Connect invoicing to a single stock and revenue recognition source](#task-38) | P1 | 03, 09, 10, 11, 13, 15, 31, 33, 37 | G6 | `QtsWebApp/QtsWebApp/Features/Invoices/InvoiceEndpoints.cs` |
| [39 — Complete customer open items, collections, advances and credit payout](#task-39) | P1 | 11, 13, 17, 31, 37, 38 | G6 | `QtsWebApp/QtsWebApp/Features/Receivables/ReceivableEndpoints.cs` |
| [40 — Deliver invoice, collection and customer statement screens](#task-40) | P1 | 30, 31, 34, 37, 38, 39 | G6 | `QtsBilling.Ui/Pages/Invoices/Invoices.razor` |
| [41 — Add quotes and sales orders with versioned commitments](#task-41) | P1 | 03, 04, 05, 08, 11, 31, 33, 39 | G6 | `QtsBilling.Persistence.Postgres/Entities/SalesOrders.cs` |
| [42 — Add reserved stock, partial deliveries and invoice linkage](#task-42) | P1 | 10, 11, 14, 15, 38, 39, 41 | G6 | `QtsWebApp/QtsWebApp/Features/Orders/FulfillmentService.cs` |
| [43 — Deliver quotation, order and fulfillment workbenches](#task-43) | P1 | 30, 34, 40, 41, 42 | G6 | `QtsBilling.Ui/Pages/Orders/Quotes.razor` |
| [44 — Complete purchase-order amendments, receiving and return commitments](#task-44) | P1 | 11, 16, 31, 32, 33 | G6 | `QtsWebApp/QtsWebApp/Features/Purchasing/PurchasingEndpoints.cs` |
| [45 — Implement supplier bills, input tax and AP recognition](#task-45) | P1 | 03, 05, 10, 11, 13, 15, 16, 31, 44 | G6 | `QtsWebApp/QtsWebApp/Features/Payables/SupplierBillEndpoints.cs` |
| [46 — Complete supplier payments, advances and credit allocations](#task-46) | P1 | 11, 13, 17, 45 | G6 | `QtsWebApp/QtsWebApp/Features/Payables/SupplierPaymentEndpoints.cs` |
| [47 — Deliver procurement and supplier-account workbenches](#task-47) | P1 | 30, 34, 44, 45, 46 | G6 | `QtsBilling.Ui/Pages/Purchasing/PurchaseOrders.razor` |
| [48 — Implement controlled stock counts and paired warehouse transfers](#task-48) | P1 | 10, 11, 14, 15, 17, 33, 42 | G6 | `QtsWebApp/QtsWebApp/Features/Inventory/StockControlEndpoints.cs` |
| [49 — Deliver inventory visibility, counts, transfers and reorder workbench](#task-49) | P1 | 30, 33, 34, 44, 48 | G6 | `QtsBilling.Ui/Pages/Inventory/Stock.razor` |
| [50 — Complete expense evidence, tax classification and approval](#task-50) | P1 | 03, 04, 05, 08, 10, 11, 13, 17, 45 | G6 | `QtsWebApp/QtsWebApp/Features/Expenses/ExpenseEndpoints.cs` |
| [51 — Implement native bank statements and reconciliation](#task-51) | P1 | 10, 11, 13, 17, 39, 46, 50 | G6 | `QtsWebApp/QtsWebApp/Features/Banking/BankReconciliationEndpoints.cs` |
| [52 — Deliver accounting, cash/bank and period-close workbenches](#task-52) | P1 | 30, 39, 46, 48, 50, 51 | G6 | `QtsBilling.Ui/Pages/Accounting/Ledger.razor` |
| [53 — Repair historical reports and add exact AR/AP and valuation tie-outs](#task-53) | P0 | 03, 10, 13, 15, 16, 17, 37, 38, 39, 45, 46, 48, 51 | G6 | `QtsWebApp/QtsWebApp/Features/Reporting/ReportEndpoints.cs` |
| [54 — Deliver actionable dashboards, reports and safe exports](#task-54) | P1 | 30, 36, 40, 47, 49, 52, 53 | G6 | `QtsBilling.Ui/Pages/Reporting/Reports.razor` |
| [55 — Add authorized document attachments and reliable transactional email delivery](#task-55) | P1 | 04, 05, 06, 07, 11, 38, 45, 50 | G2 + G6 + G7 | `QtsWebApp/QtsWebApp/Features/Documents/DocumentEndpoints.cs` |
| [56 — Deliver owner administration, settings, device and audit screens](#task-56) | P1 | 07, 08, 23, 30, 31, 32, 55 | G2 + G5 | `QtsBilling.Ui/Pages/Admin/BusinessSettings.razor` |
| [57 — Close contract, route, concurrency and dependency integration gaps](#task-57) | P0 | 02, 06, 11, 23, 24, 26, 31, 32, 33, 35, 38, 39, 41, 42, 44, 45, 46, 48, 50, 51, 53, 55, 56 | G0 + G7 | `QtsBilling.Contracts/Serialization/ContractJson.cs` |
| [58 — Harden production configuration, backups and lossless recovery](#task-58) | P0 | 20, 21, 25, 27, 55, 57 | G7 | `scripts/backup-postgres.ps1` |
| [59 — Add measurable observability, performance limits and retention controls](#task-59) | P1 | 24, 26, 28, 53, 55, 57, 58 | G7 | `QtsWebApp/QtsWebApp/Infrastructure/OperationalTelemetry.cs` |
| [60 — Make CI prove PostgreSQL, native import, offline and UI correctness](#task-60) | P0 | 01, 19, 20, 24, 25, 27, 28, 35, 37, 38, 39, 42, 45, 46, 48, 50, 51, 53, 55, 57, 58, 59 | G7 | `.github/workflows/ci.yml` |
| [61 — Promote only tested signed artifacts through explicit release approval](#task-61) | P0 | 21, 23, 57, 58, 60 | G7 | `.github/workflows/release.yml` |
| [62 — Verify Windows lifecycle, browser accessibility and receipt peripherals](#task-62) | P1 | 23, 28, 29, 34, 35, 36, 40, 43, 47, 49, 52, 54, 56, 61 | G7 | `TestQtsBilling/Ui/SupportedHostJourneys.cs` |
| [63 — Rehearse full business journeys, load and recovery before pilot acceptance](#task-63) | P0 | 20, 21, 29, 35, 36, 37, 40, 43, 47, 49, 50, 51, 52, 53, 54, 55, 56, 58, 59, 60, 61, 62 | G7 | `TestQtsBilling/Acceptance/BusinessJourneyTests.cs` |
| [64 — Finalize the native product tree, truthful readiness record and handover](#task-64) | P0 | 01–63 | G7 | `README.md` |

**Scheduling note:** numbers are a valid implementation order, not sixty-four independent deployments. Identity delivery (07 + 55), commercial/stock/settlement integrity (12–17), native onboarding replacement (02 + 18–21), and offline grant/receipt/feed/coordinator behavior (23–29) are explicit must-ship-together gates. Do not mark a coupled capability usable before its final integration passes.

<a id="tasks"></a>
## 5. Atomic tasks

For each task, A–G is mandatory. B lists primary production files plus the test file; the exact narrow integration bindings in D17 are part of that scope where needed. New migration timestamps and runtime results are deliberately not invented.

<a id="task-01"></a>
## Atomic Task 01 - Establish a safe PostgreSQL/API regression harness and release-blocker ledger

### A. Objective

**Priority:** P0. **Requirements:** `R01`, `R02`, `R18`. **Findings:** `F01`, `F39`, `F45`.
**Dependencies:** None; start from the verified baseline. **Integration gate:** G0.

**Current → target:** Current CI proves a subset of builds and non-PostgreSQL tests; live-evidence prose cannot reproduce the critical failures. → A safe test foundation can reproduce each P0 defect without touching any existing deployment; the release ledger explicitly blocks production pending the relevant gates.

**Acceptance:** The target behavior is observable through the owning API/UI/store and every applicable scenario in E passes; release additionally requires the named gate, not just this file diff.

**Non-goals:** No production database access, no automatic migration of the developer database, no full-suite requirement for every task.

### B. Scope

| Action | Exact path | Change / exclusion |
|---|---|---|
| MODIFY | `TestQtsBilling/TestQtsBilling.csproj` | Reference the web host and add Microsoft.AspNetCore.Mvc.Testing 10.0.13; reuse existing xUnit/assertion dependencies. |
| MODIFY | `QtsWebApp/QtsWebApp/Program.cs` | Expose the top-level Program type to WebApplicationFactory and permit explicit test-only service overrides; no production bypass. |
| CREATE — NEW | `TestQtsBilling/Integration/PostgresApiFixture.cs` | Disposable PostgreSQL/API fixture with owner/application roles, two businesses, scoped actors and deterministic clock/data. |
| CREATE — NEW | `TestQtsBilling/Integration/FinancialAssertions.cs` | Per-entry/document/stock/open-item assertions and before/after state capture. |
| CREATE — NEW test | `TestQtsBilling/Integration/HarnessSafetyTests.cs` | Owning focused assertions below; reuse fixtures rather than duplicate a test infrastructure. |

All other production paths are excluded except the narrow, explicitly enumerated [D17 integration bindings](#d17). Applied migrations, posted facts and unresolved cash are never edited/deleted by this scope.

### C. Verified anchors and NEW insertions

- **EXISTING / VERIFIED:** [QtsBilling.sln — source lines 1-34](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.sln#L1-L34): `Solution project graph`. [E01](#e-e01).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Program.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Program.cs): `Identity registration, cookies, API registration, WebLocalPosService`. [E03](#e-e03).
- **EXISTING / VERIFIED:** [.github/workflows/ci.yml — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/.github/workflows/ci.yml): `build-and-test; build-desktop`. [E41](#e-e41).
- **EXISTING / VERIFIED:** [QtsBilling.Persistence.Sqlite.Tests/LocalStoreHarness.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Persistence.Sqlite.Tests/LocalStoreHarness.cs): `LocalStoreHarness; TestData`. [E46](#e-e46).

All CREATE/GENERATE symbols, routes and constraints in B/D are **NEW**. Source presence is not runtime proof; do not infer executed results from old evidence prose.

### D. Ordered implementation recipe

1. Create fixture databases only through a dedicated QTS_TEST_POSTGRES_CONNECTION on an allowlisted local/CI endpoint. Require a random qts_test_ database name and a fixture marker; reject QTS_POSTGRES_CONNECTION fallback, non-test names and production endpoints before any DDL. Cleanup may remove only the fixture-owned random database.
2. Apply existing migrations in the disposable fixture with the owner role, then execute application requests and direct-SQL guard checks with the DML-only application role. Do not use EF InMemory or SQLite to prove PostgreSQL locking/constraints.
3. Provide two businesses and locations with deliberately similar SKUs/account codes; Owner/Manager/Cashier/Accountant/Viewer and revoked/inactive identities. Test authentication must be a test-host override unavailable in production, not an endpoint/header backdoor.
4. Add reusable snapshots of posted journal entry/lines, stock quantity/value, payments, open items, audit and receipts. Assertions compare exact decimals and distinguish before/after transaction failure.
5. Record this SHA and the observed CI run in a native readiness ledger; mark critical cases NOT RUN until actually exercised. A failing future regression blocks the candidate; do not disable assertions to retain old pass totals.

Apply the specific D01–D17 policies relevant to this slice. Finish the narrow route/model/client registration in B, record generated migration paths, and preserve a safe stop/fix-forward path before presenting the diff for review.

### E. Focused tests

**NEW file:** `TestQtsBilling/Integration/HarnessSafetyTests.cs`. Name the class `HarnessSafetyTests` in the directory-derived namespace specified in §6.3.

- **T01.01:** Given a production-shaped connection or database name, when the fixture initializes, then it fails before opening a privileged connection.
- **T01.02:** Given two fixture businesses, when a cross-business resource ID is submitted, then the harness can assert both HTTP denial and zero affected rows.
- **T01.03:** Given an injected exception before transaction commit, when state is compared, then no partial financial or audit effect remains.
- **T01.04:** Given a financial equality mismatch on a single entry, when FinancialAssertions runs, then global cancellation elsewhere cannot make it pass.

Reuse existing or earlier-task fixtures. For a mutation, also assert denied tenant/location/permission and injected pre-commit failure leave zero partial financial/audit effects; for a read/UI-only task assert no mutation. Reuse the same fixture rather than duplicating equivalent tests.

### F. Verification

**Status: NOT RUN - SPEC_ONLY.** The following filter becomes executable only after the NEW test class and prerequisites are implemented.

Requires Task 01's isolated PostgreSQL 18/API fixture, .NET 10 SDK and explicitly validated QTS_TEST_POSTGRES_CONNECTION. Fixture DDL/data writes are permitted only in a later authorized test run; never inherit production credentials.

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~TestQtsBilling.Integration.HarnessSafetyTests"
```

**PASS:** owning cases and relevant existing regression filters pass, with exact committed-state assertions where applicable; no skipped required prerequisite, new unreviewed dependency, secret leak or `git diff --check` failure. A build or mocked response alone is not financial proof.

Select the checked server or Windows build command in §6.3 only when this task changes that host. Full multi-platform regression belongs in CI/release, not every local edit.

### G. Definition of done

A safe test foundation can reproduce each P0 defect without touching any existing deployment; the release ledger explicitly blocks production pending the relevant gates. Evidence includes the exact implementation SHA, focused test outcome and minimal reviewed diff. All new constraints/contracts/permissions/host wiring ship together under **G0**; unresolved external prerequisites stay explicit.

Fixture safety is demonstrated first. Existing CI success stays recorded as limited evidence; it is not renamed a production certification. No unnecessary dependency or compatibility path remains. Recovery preserves historical facts and pending money. Record remaining blockers; spec completion is not implementation completion or owner production approval.

<a id="task-02"></a>
## Atomic Task 02 - Remove retired provider/platform traces and isolate native onboarding

### A. Objective

**Priority:** P0. **Requirements:** `R01`, `R16`. **Findings:** `F03`, `F04`, `F42`, `F45`.
**Dependencies:** [Task 01](#task-01) **Integration gate:** G0 + G4.

**Current → target:** Old runtime projects are absent, but code comments, docs, a smoke blacklist and source-specific import tooling still preserve the retired-provider surface. → The supported tree and release dependencies contain only native PostgreSQL/SQLite/Windows Hybrid behavior; no old schema/identifier/provider fallback remains.

**Acceptance:** The target behavior is observable through the owning API/UI/store and every applicable scenario in E passes; release additionally requires the named gate, not just this file diff.

**Non-goals:** No Git history rewrite, force push, production row deletion or silent destruction of migration evidence. Only the current product tree and newly built artifacts are cleaned.

### B. Scope

| Action | Exact path | Change / exclusion |
|---|---|---|
| MODIFY | `QtsBilling.sln` | Plan removal of the two source-specific import project entries when the native replacement gate is ready. |
| DELETE — PLANNED / owner-gated | `QtsBilling.Migration/` | Retire all seven checked source/project files as one gated import replacement, not a renamed compatibility adapter. |
| DELETE — PLANNED / owner-gated | `QtsBilling.Migration.Tests/` | Retire source-format tests; port only provider-neutral integrity scenarios to native import tests. |
| MODIFY | `QtsBilling.Ui.Smoke/Program.cs` | Replace the old-provider blacklist with an allowed assembly/project dependency policy. |
| MODIFY | `QtsBilling.Persistence.Sqlite/QtsBilling.Persistence.Sqlite.csproj` | Remove source-era comments; retain actual EF SQLite and contract/value-object references. |
| MODIFY | `README.md` | Describe only the current native architecture and verified status; remove retired-platform instructions. |
| CREATE — NEW test | `TestQtsBilling/Architecture/NativeBoundaryTests.cs` | Owning focused assertions below; reuse fixtures rather than duplicate a test infrastructure. |

All other production paths are excluded except the narrow, explicitly enumerated [D17 integration bindings](#d17). Applied migrations, posted facts and unresolved cash are never edited/deleted by this scope.

### C. Verified anchors and NEW insertions

- **EXISTING / VERIFIED:** [QtsBilling.sln — source lines 1-34](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.sln#L1-L34): `Solution project graph`. [E01](#e-e01).
- **EXISTING / VERIFIED:** [QtsBilling.Migration/SnapshotSource.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Migration/SnapshotSource.cs): `VerifyAsync; ReadDocumentsAsync; InventoryAsync`. [E36](#e-e36).
- **EXISTING / VERIFIED:** [QtsBilling.Migration/Program.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Migration/Program.cs): `ImportAsync; ReconcileAsync; TransformAsync`. [E37](#e-e37).
- **EXISTING / VERIFIED:** [QtsBilling.Migration/PostgresImporter.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Migration/PostgresImporter.cs): `ImportAsync; UpsertAsync; WriteIdentifierMapAsync`. [E38](#e-e38).
- **EXISTING / VERIFIED:** [QtsBilling.Migration/ReconciliationReport.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Migration/ReconciliationReport.cs): `BuildBody; Sign; Compare`. [E39](#e-e39).
- **EXISTING / VERIFIED:** [docs/migration/task-status.md — source lines 1-85](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/docs/migration/task-status.md#L1-L85): `QB-06 and QB-23 through QB-30 status claims`. [E43](#e-e43).
- **EXISTING / VERIFIED:** [docs/migration/decisions.md — source lines 1-155](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/docs/migration/decisions.md#L1-L155): `D-01...D-41 and historical decisions`. [E44](#e-e44).
- **EXISTING / VERIFIED:** [README.md — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/README.md): `Architecture / status / project descriptions`. [E48](#e-e48).

All CREATE/GENERATE symbols, routes and constraints in B/D are **NEW**. Source presence is not runtime proof; do not infer executed results from old evidence prose.

### D. Ordered implementation recipe

1. Inventory case-insensitive text/path/package/project references in tracked files, including generated files, fixtures, docs, scripts and lock files. The verified hit set includes README.md, docs/migration/decisions.md, task-status.md, inventory.redacted.json, tasks/QB-26.md, QB-23-25.md, QB-30.md, qb17-evidence.txt and qb23-25-evidence.txt. Update or retire these exact files; also inspect any additional hits rather than asserting the search result is exhaustive.
2. Disable the unsafe source-import/cutover invocation path in the candidate immediately; couple final project deletion with Tasks 18-21 so no script retains a broken reference. Do not leave a disabled compatibility flag, source credentials, BSON/ObjectId decoder or permissive old-payload handler.
3. Preserve financial provenance independently of source implementation: current imported rows and identifier lineage require an exact native reconciliation before table retirement. Never edit an applied migration or erase posted facts to obtain a clean string scan.
4. Use a positive assembly/project graph for boundary checks: browser/RCL may consume Contracts and approved UI dependencies; MAUI may additionally consume SQLite; only server/import tooling consumes PostgreSQL. Avoid adding forbidden product names into the checker itself.
5. Update current documentation in UTF-8 with neutral, truthful replacement decisions. Git history, old releases, archived backups and external forks are explicitly outside this working-tree change and require separate authorized administration.

Apply the specific D01–D17 policies relevant to this slice. Finish the narrow route/model/client registration in B, record generated migration paths, and preserve a safe stop/fix-forward path before presenting the diff for review.

### E. Focused tests

**NEW file:** `TestQtsBilling/Architecture/NativeBoundaryTests.cs`. Name the class `NativeBoundaryTests` in the directory-derived namespace specified in §6.3.

- **T02.01:** Given the candidate tracked tree, when content/dependency inspection runs, then no retired-provider/platform terms, packages, project edges or source adapters remain in the supported tree.
- **T02.02:** Given a source-era identifier wrapper or manifest, when native import is attempted after replacement, then it is rejected as unsupported rather than coerced.
- **T02.03:** Given a posted opening and an unresolved local cash operation, when cleanup is applied, then both remain byte/value-identical and queryable through native recovery.
- **T02.04:** Given the smoke executable, when dependency checks run, then valid Contracts-only UI succeeds and an injected persistence reference fails.

Reuse existing or earlier-task fixtures. For a mutation, also assert denied tenant/location/permission and injected pre-commit failure leave zero partial financial/audit effects; for a read/UI-only task assert no mutation. Reuse the same fixture rather than duplicating equivalent tests.

### F. Verification

**Status: NOT RUN - SPEC_ONLY.** The following filter becomes executable only after the NEW test class and prerequisites are implemented.

Requires .NET 10 SDK and the NEW owning test class. Use pure fixtures/native files; no production service connection.

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~TestQtsBilling.Architecture.NativeBoundaryTests"
```

**PASS:** owning cases and relevant existing regression filters pass, with exact committed-state assertions where applicable; no skipped required prerequisite, new unreviewed dependency, secret leak or `git diff --check` failure. A build or mocked response alone is not financial proof.

After the replacement is implemented: dotnet run --project QtsBilling.Ui.Smoke/QtsBilling.Ui.Smoke.csproj -c Release. Complete content scan includes binary/package inspection; a text grep alone is not proof.

Select the checked server or Windows build command in §6.3 only when this task changes that host. Full multi-platform regression belongs in CI/release, not every local edit.

### G. Definition of done

The supported tree and release dependencies contain only native PostgreSQL/SQLite/Windows Hybrid behavior; no old schema/identifier/provider fallback remains. Evidence includes the exact implementation SHA, focused test outcome and minimal reviewed diff. All new constraints/contracts/permissions/host wiring ship together under **G0 + G4**; unresolved external prerequisites stay explicit.

No unnecessary dependency or compatibility path remains. Recovery preserves historical facts and pending money. Record remaining blockers; spec completion is not implementation completion or owner production approval.

<a id="task-03"></a>
## Atomic Task 03 - Define one exact commercial amount contract for server and device

### A. Objective

**Priority:** P0. **Requirements:** `R05`, `R08`. **Findings:** `F11`, `F12`, `F13`, `F37`.
**Dependencies:** [Task 01](#task-01) **Integration gate:** G1.

**Current → target:** Server, device and UI derive tax/rounding differently; inclusive revenue and quantity reporting are inconsistent. → All commercial calculations are pure, deterministic and shared; line/header/settlement values have explicit scales and balance identities.

**Acceptance:** The target behavior is observable through the owning API/UI/store and every applicable scenario in E passes; release additionally requires the named gate, not just this file diff.

**Non-goals:** No currency-conversion service, tax certification or promotion engine. Do not change historical stored amounts.

### B. Scope

| Action | Exact path | Change / exclusion |
|---|---|---|
| CREATE — NEW | `QtsModels/ValueObjects/CommercialAmounts.cs` | Pure checked pricing/discount/tax/document-rounding functions under D03. |
| MODIFY | `QtsBilling.Contracts/Sales/SaleCaptureDtos.cs` | Native line tax mode, policy version and document rounding contract; no floating-point fields. |
| MODIFY | `QtsBilling.Contracts/Pos/PosDtos.cs` | Expose the same precision/policy fields for offline display and capture. |
| MODIFY | `QtsBilling.Contracts/QtsBilling.Contracts.csproj` | Reference only the provider-neutral QtsModels project for one shared calculation implementation. |
| CREATE — NEW | `QtsBilling.Contracts/Pos/CommercialAmountCalculator.cs` | Thin typed facade over CommercialAmounts for RCL/server/device callers; no duplicate arithmetic. |
| CREATE — NEW test | `TestQtsBilling/ValueObjects/CommercialAmountsTests.cs` | Owning focused assertions below; reuse fixtures rather than duplicate a test infrastructure. |

All other production paths are excluded except the narrow, explicitly enumerated [D17 integration bindings](#d17). Applied migrations, posted facts and unresolved cash are never edited/deleted by this scope.

### C. Verified anchors and NEW insertions

- **EXISTING / VERIFIED:** [QtsBilling.Persistence.Postgres/QtsBilling.Persistence.Postgres.csproj — source lines 1-22](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Persistence.Postgres/QtsBilling.Persistence.Postgres.csproj#L1-L22): `PackageReference / TargetFramework`. [E02](#e-e02).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Sales/SalePosting.cs — source lines 1-310](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Sales/SalePosting.cs#L1-L310): `ValidatePayload; ResolveContextAsync; PriceLines; ValidatePayment`. [E11](#e-e11).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Sales/SalePosting.cs — source lines 310-665](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Sales/SalePosting.cs#L310-L665): `PostAsync / posting transaction and revenue journal`. [E12](#e-e12).
- **EXISTING / VERIFIED:** [QtsBilling.Persistence.Sqlite/SqliteLocalStore.cs — source lines 1-290](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Persistence.Sqlite/SqliteLocalStore.cs#L1-L290): `InitializeAsync; CaptureProvisionalCashSaleAsync`. [E20](#e-e20).
- **EXISTING / VERIFIED:** [QtsBilling.Ui/Pages/Pos.razor.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Ui/Pages/Pos.razor.cs): `LineTax; LineTotal; CaptureAsync`. [E26](#e-e26).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Reporting/ReportEndpoints.cs — source lines 240-550](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Reporting/ReportEndpoints.cs#L240-L550): `StockSummaryAsync; SalesSummaryAsync`. [E35](#e-e35).

All CREATE/GENERATE symbols, routes and constraints in B/D are **NEW**. Source presence is not runtime proof; do not infer executed results from old evidence prose.

### D. Ordered implementation recipe

1. Implement D03 exactly, returning gross, authorized discount, net revenue, tax, payable and rounding as distinct values. The function accepts policy/currency settlement scale explicitly; it cannot read system culture or the database.
2. Validate input scale/range and use checked decimal arithmetic. Use quantity six decimals and unit-price/money four; intermediate division is permitted but derived money rounds AwayFromZero. Undefined tax modes and invalid rates fail before persistence.
3. For mixed lines, sum already rounded line amounts; allocate header discount in integer four-decimal units with deterministic ordinal remainder. Store a policy identifier so cached computations can be matched to the server.
4. Keep provider-neutral value objects free of EF/BSON/UI references. Preserve existing checked ScaledDecimal converters, adapting callers rather than replacing them.
5. Define the new contract generation and upgrade failure explicitly. Old wire shapes are not guessed or silently interpreted; Tasks 12, 29 and 57 wire every consumer before release.

Apply the specific D01–D17 policies relevant to this slice. Finish the narrow route/model/client registration in B, record generated migration paths, and preserve a safe stop/fix-forward path before presenting the diff for review.

### E. Focused tests

**NEW file:** `TestQtsBilling/ValueObjects/CommercialAmountsTests.cs`. Name the class `CommercialAmountsTests` in the directory-derived namespace specified in §6.3.

- **T03.01:** Given 115 inclusive at rate 0.15, when calculated, then revenue=100, tax=15 and payable=115.
- **T03.02:** Given one inclusive, one exclusive and one exempt line, when discounted and totalled, then sum(revenue)+sum(tax)+rounding equals payable exactly.
- **T03.03:** Given quantity 0.000001 and valid four-decimal price, when computed and serialized, then no quantity precision is lost.
- **T03.04:** Given halfway rounding, overflow, excessive input scale or invalid enum, when the task-specific command or transition is exercised, then output is deterministic or an explicit validation failure.
- **T03.05:** Given a header discount with remainder, when line order is unchanged, then allocation and canonical payload are identical across server/device cultures.

Reuse existing or earlier-task fixtures. For a mutation, also assert denied tenant/location/permission and injected pre-commit failure leave zero partial financial/audit effects; for a read/UI-only task assert no mutation. Reuse the same fixture rather than duplicating equivalent tests.

### F. Verification

**Status: NOT RUN - SPEC_ONLY.** The following filter becomes executable only after the NEW test class and prerequisites are implemented.

Requires .NET 10 SDK and the NEW owning test class. Use pure fixtures/native files; no production service connection.

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~TestQtsBilling.ValueObjects.CommercialAmountsTests"
```

**PASS:** owning cases and relevant existing regression filters pass, with exact committed-state assertions where applicable; no skipped required prerequisite, new unreviewed dependency, secret leak or `git diff --check` failure. A build or mocked response alone is not financial proof.

Select the checked server or Windows build command in §6.3 only when this task changes that host. Full multi-platform regression belongs in CI/release, not every local edit.

### G. Definition of done

All commercial calculations are pure, deterministic and shared; line/header/settlement values have explicit scales and balance identities. Evidence includes the exact implementation SHA, focused test outcome and minimal reviewed diff. All new constraints/contracts/permissions/host wiring ship together under **G1**; unresolved external prerequisites stay explicit.

No unnecessary dependency or compatibility path remains. Recovery preserves historical facts and pending money. Record remaining blockers; spec completion is not implementation completion or owner production approval.

<a id="task-04"></a>
## Atomic Task 04 - Enforce current business and location authority on every sensitive operation

### A. Objective

**Priority:** P0. **Requirements:** `R03`, `R04`. **Findings:** `F07`, `F08`, `F22`, `F43`.
**Dependencies:** [Task 01](#task-01) **Integration gate:** G2.

**Current → target:** Cookie claims provide authority without current membership/business/location checks; multi-business selection and scoped roles are incomplete. → Every command and protected query resolves an active actor and permitted resource scope; stale claims never preserve revoked authority.

**Acceptance:** The target behavior is observable through the owning API/UI/store and every applicable scenario in E passes; release additionally requires the named gate, not just this file diff.

**Non-goals:** No global super-admin bypass, impersonation feature or external tenant directory integration.

### B. Scope

| Action | Exact path | Change / exclusion |
|---|---|---|
| MODIFY | `QtsWebApp/QtsWebApp/Infrastructure/QtsIdentity.cs` | Current authorization/version checks and narrowed base-role mapping. |
| CREATE — NEW | `QtsWebApp/QtsWebApp/Infrastructure/BusinessActor.cs` | Request-scoped actor/business/allowed-location resolution; no client trust. |
| MODIFY | `QtsBilling.Contracts/Identity/IdentityDtos.cs` | Explicit business selection, authorization version and granular permission definitions. |
| MODIFY | `QtsWebApp/QtsWebApp/Features/Auth/AuthEndpoints.cs` | List/select only active memberships and invalidate current authority safely. |
| CREATE — NEW test | `TestQtsBilling/Integration/BusinessAuthorizationTests.cs` | Owning focused assertions below; reuse fixtures rather than duplicate a test infrastructure. |

MODIFY existing `QtsWebApp/QtsWebApp/Program.cs` only for required route/service registration, and `QtsBilling.Contracts/Identity/IdentityDtos.cs` / `QtsWebApp/QtsWebApp/Infrastructure/QtsIdentity.cs` only for this task's explicit D02 policies.

All other production paths are excluded except the narrow, explicitly enumerated [D17 integration bindings](#d17). Applied migrations, posted facts and unresolved cash are never edited/deleted by this scope.

### C. Verified anchors and NEW insertions

- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Program.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Program.cs): `Identity registration, cookies, API registration, WebLocalPosService`. [E03](#e-e03).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Infrastructure/QtsIdentity.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Infrastructure/QtsIdentity.cs): `PermissionAuthorizationHandler.HandleRequirementAsync; QtsClaimsPrincipalFactory`. [E04](#e-e04).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Auth/AuthEndpoints.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Auth/AuthEndpoints.cs): `LoginAsync; RequestPasswordResetAsync; CompletePasswordResetAsync`. [E05](#e-e05).
- **EXISTING / VERIFIED:** [QtsBilling.Persistence.Postgres/BillingDbContext.Model.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Persistence.Postgres/BillingDbContext.Model.cs): `ConfigureCore; ConfigureMasterData; ConfigureCatalog; ConfigureInventory`. [E06](#e-e06).

All CREATE/GENERATE symbols, routes and constraints in B/D are **NEW**. Source presence is not runtime proof; do not infer executed results from old evidence prose.

### D. Ordered implementation recipe

1. Add native BusinessActor resolution that reloads active user, selected active membership, active business and allowed location grants. Cache only within a request; if a cross-request cache is introduced later, version invalidation must be proven first.
2. Move permission decisions into policies backed by this actor. Do not simply add another global filter while retaining unscoped raw SQL; resource handlers must use business/location parameters derived from the actor.
3. Provide authenticated GET /api/v1/auth/businesses and POST /api/v1/auth/select-business. Selection issues a fresh session for an existing membership; a body business ID is never proof. Device context selection additionally follows its enrolment binding.
4. Apply D02 least-privilege defaults. Cashier no longer receives unrestricted financial reports or arbitrary drawer cash movement authority from RegistersManage. Return 401 for invalid session, 403 for denied capability, 404 for inaccessible resource identifiers without leaking another tenant.
5. User/membership/business deactivation and role change increment a security/authorization version, invalidate sessions/grants, and are audited. Enforce the same checks on financial reads, exports, sync and background delivery/download access.

Apply the specific D01–D17 policies relevant to this slice. Finish the narrow route/model/client registration in B, record generated migration paths, and preserve a safe stop/fix-forward path before presenting the diff for review.

### E. Focused tests

**NEW file:** `TestQtsBilling/Integration/BusinessAuthorizationTests.cs`. Name the class `BusinessAuthorizationTests` in the directory-derived namespace specified in §6.3.

- **T04.01:** Given an active cookie and then deactivated membership, when posting or reading a financial report, then authority is refused without waiting for sign-out.
- **T04.02:** Given a cashier assigned location A, when location B is used in body/query/path, then no rows or totals from B are exposed.
- **T04.03:** Given memberships in A and B, when selecting a non-member C, then no new claim/session is issued.
- **T04.04:** Given a role downgrade during a request boundary, when the next sensitive request runs, then old permission claims cannot authorize it.
- **T04.05:** Given Owner in one business, when another business resource ID is guessed, then Owner status does not bypass scope.

Reuse existing or earlier-task fixtures. For a mutation, also assert denied tenant/location/permission and injected pre-commit failure leave zero partial financial/audit effects; for a read/UI-only task assert no mutation. Reuse the same fixture rather than duplicating equivalent tests.

### F. Verification

**Status: NOT RUN - SPEC_ONLY.** The following filter becomes executable only after the NEW test class and prerequisites are implemented.

Requires Task 01's isolated PostgreSQL 18/API fixture, .NET 10 SDK and explicitly validated QTS_TEST_POSTGRES_CONNECTION. Fixture DDL/data writes are permitted only in a later authorized test run; never inherit production credentials.

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~TestQtsBilling.Integration.BusinessAuthorizationTests"
```

**PASS:** owning cases and relevant existing regression filters pass, with exact committed-state assertions where applicable; no skipped required prerequisite, new unreviewed dependency, secret leak or `git diff --check` failure. A build or mocked response alone is not financial proof.

Select the checked server or Windows build command in §6.3 only when this task changes that host. Full multi-platform regression belongs in CI/release, not every local edit.

### G. Definition of done

Every command and protected query resolves an active actor and permitted resource scope; stale claims never preserve revoked authority. Evidence includes the exact implementation SHA, focused test outcome and minimal reviewed diff. All new constraints/contracts/permissions/host wiring ship together under **G2**; unresolved external prerequisites stay explicit.

No unnecessary dependency or compatibility path remains. Recovery preserves historical facts and pending money. Record remaining blockers; spec completion is not implementation completion or owner production approval.

<a id="task-05"></a>
## Atomic Task 05 - Complete composite tenant foreign keys and location-bound references

### A. Objective

**Priority:** P0. **Requirements:** `R03`, `R06`. **Findings:** `F08`, `F22`.
**Dependencies:** [Task 01](#task-01), [Task 04](#task-04) **Integration gate:** G2.

**Current → target:** Only selected inventory/journal relationships have structural business isolation; many financial and master links use globally valid but wrong-tenant IDs. → Every business-owned reference is constrained to a same-business principal, with deterministic refusal and a safe preflight for existing inconsistencies.

**Acceptance:** The target behavior is observable through the owning API/UI/store and every applicable scenario in E passes; release additionally requires the named gate, not just this file diff.

**Non-goals:** No blanket row-level-security rollout or mutation of applied migrations; RLS is not a substitute for complete ownership checks.

### B. Scope

| Action | Exact path | Change / exclusion |
|---|---|---|
| MODIFY | `QtsBilling.Persistence.Postgres/BillingDbContext.Model.cs` | Composite master, category, variant and device references. |
| MODIFY | `QtsBilling.Persistence.Postgres/BillingDbContext.Model.Financial.cs` | Composite period/reversal/invoice/payment/expense/source/allocation references. |
| GENERATE — NEW | `QtsBilling.Persistence.Postgres/Migrations/` | NEW logical migration EnforceBusinessReferenceIntegrity, its generated designer and updated model snapshot. |
| CREATE — NEW test | `TestQtsBilling/Integration/TenantConstraintTests.cs` | Owning focused assertions below; reuse fixtures rather than duplicate a test infrastructure. |

All other production paths are excluded except the narrow, explicitly enumerated [D17 integration bindings](#d17). Applied migrations, posted facts and unresolved cash are never edited/deleted by this scope.

### C. Verified anchors and NEW insertions

- **EXISTING / VERIFIED:** [QtsBilling.Persistence.Postgres/BillingDbContext.Model.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Persistence.Postgres/BillingDbContext.Model.cs): `ConfigureCore; ConfigureMasterData; ConfigureCatalog; ConfigureInventory`. [E06](#e-e06).
- **EXISTING / VERIFIED:** [QtsBilling.Persistence.Postgres/BillingDbContext.Model.Financial.cs — source lines 210-445](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Persistence.Postgres/BillingDbContext.Model.Financial.cs#L210-L445): `ConfigureAccounting; ConfigurePurchasing`. [E07](#e-e07).
- **EXISTING / VERIFIED:** [QtsBilling.Persistence.Postgres/Migrations/20261019082426_AddIntegrityGuards.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Persistence.Postgres/Migrations/20261019082426_AddIntegrityGuards.cs): `qts_invoice_lines_draft_guard; qts_inventory_balance_guard; deferred balance triggers`. [E09](#e-e09).

All CREATE/GENERATE symbols, routes and constraints in B/D are **NEW**. Source presence is not runtime proof; do not infer executed results from old evidence prose.

### D. Ordered implementation recipe

1. Enumerate every mapped business-owned FK in the EF model. Replace single-column references identified in E06/E07, including device location, category/account parent, all product master references, variant product, invoice customer/location/source, journal period/reversal/location and expense supplier/category/account.
2. Use principal alternate key (Id,BusinessId) and dependent (ForeignId,BusinessId). Nullable references remain optional, but non-null IDs must resolve within the tenant. Add missing unique principal keys/indexes only where needed.
3. For device/session/order/fulfilment references that must share a location, add the necessary composite location relationship or server-validated immutable binding; document which invariant is DB-enforced and which is actor policy.
4. Preflight current rows with explicit cross-business/orphan queries. Any inconsistent financial reference blocks applying validated constraints; produce a masked row-level remediation report, never assign the first business automatically.
5. Generate a new migration after the checked head; do not edit AddIntegrityGuards or any applied migration. Stage/validate constraints with a measured lock plan; runtime checks ship in the same G2 gate. Rollback cannot re-enable a known cross-tenant write path.

Apply the specific D01–D17 policies relevant to this slice. Finish the narrow route/model/client registration in B, record generated migration paths, and preserve a safe stop/fix-forward path before presenting the diff for review.

### E. Focused tests

**NEW file:** `TestQtsBilling/Integration/TenantConstraintTests.cs`. Name the class `TenantConstraintTests` in the directory-derived namespace specified in §6.3.

- **T05.01:** Given a product in A and a category/variant/unit in B, when inserted through raw application-role SQL, then the FK rejects the link.
- **T05.02:** Given an invoice/expense/journal in A and a B-owned customer/account/location/period, when the task-specific command or transition is exercised, then SQL and API paths both reject it.
- **T05.03:** Given an optional null reference, when saved, then it succeeds without a fabricated principal.
- **T05.04:** Given invalid pre-existing references in a disposable upgraded fixture, when the task-specific command or transition is exercised, then migration preflight fails with exact affected IDs and performs no silent repair.

Reuse existing or earlier-task fixtures. For a mutation, also assert denied tenant/location/permission and injected pre-commit failure leave zero partial financial/audit effects; for a read/UI-only task assert no mutation. Reuse the same fixture rather than duplicating equivalent tests.

### F. Verification

**Status: NOT RUN - SPEC_ONLY.** The following filter becomes executable only after the NEW test class and prerequisites are implemented.

Requires Task 01's isolated PostgreSQL 18/API fixture, .NET 10 SDK and explicitly validated QTS_TEST_POSTGRES_CONNECTION. Fixture DDL/data writes are permitted only in a later authorized test run; never inherit production credentials.

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~TestQtsBilling.Integration.TenantConstraintTests"
```

**PASS:** owning cases and relevant existing regression filters pass, with exact committed-state assertions where applicable; no skipped required prerequisite, new unreviewed dependency, secret leak or `git diff --check` failure. A build or mocked response alone is not financial proof.

Select the checked server or Windows build command in §6.3 only when this task changes that host. Full multi-platform regression belongs in CI/release, not every local edit.

### G. Definition of done

Every business-owned reference is constrained to a same-business principal, with deterministic refusal and a safe preflight for existing inconsistencies. Evidence includes the exact implementation SHA, focused test outcome and minimal reviewed diff. All new constraints/contracts/permissions/host wiring ship together under **G2**; unresolved external prerequisites stay explicit.

No unnecessary dependency or compatibility path remains. Recovery preserves historical facts and pending money. Record remaining blockers; spec completion is not implementation completion or owner production approval.

<a id="task-06"></a>
## Atomic Task 06 - Harden the HTTP authentication and validation boundary

### A. Objective

**Priority:** P0. **Requirements:** `R03`, `R04`, `R08`. **Findings:** `F06`, `F09`, `F13`.
**Dependencies:** [Task 01](#task-01), [Task 04](#task-04) **Integration gate:** G2.

**Current → target:** Cookie SameSite and generic middleware are treated as complete protection; JSON state-changing endpoints and production proxy/key configuration are not fully gated. → Production API enforces origin/session/CSRF boundaries, safe error semantics and bounded input/rate controls without breaking native bearer requests.

**Acceptance:** The target behavior is observable through the owning API/UI/store and every applicable scenario in E passes; release additionally requires the named gate, not just this file diff.

**Non-goals:** No blanket CORS wildcard, authentication bypass, or in-transaction provider calls.

### B. Scope

| Action | Exact path | Change / exclusion |
|---|---|---|
| MODIFY | `QtsWebApp/QtsWebApp/Program.cs` | Trusted proxies/origins, secure cookies, rate limiting and explicit cookie-API antiforgery enforcement. |
| MODIFY | `QtsWebApp/QtsWebApp/Infrastructure/ApiSupport.cs` | Bounded ProblemDetails, translated database failures and correlation sanitation. |
| MODIFY | `QtsBilling.Contracts/Serialization/ApiHttp.cs` | Antiforgery transport for cookie-authenticated writes and explicit upgrade/error handling. |
| CREATE — NEW test | `TestQtsBilling/Integration/HttpBoundaryTests.cs` | Owning focused assertions below; reuse fixtures rather than duplicate a test infrastructure. |

All other production paths are excluded except the narrow, explicitly enumerated [D17 integration bindings](#d17). Applied migrations, posted facts and unresolved cash are never edited/deleted by this scope.

### C. Verified anchors and NEW insertions

- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Program.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Program.cs): `Identity registration, cookies, API registration, WebLocalPosService`. [E03](#e-e03).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Auth/AuthEndpoints.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Auth/AuthEndpoints.cs): `LoginAsync; RequestPasswordResetAsync; CompletePasswordResetAsync`. [E05](#e-e05).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Sales/SalePosting.cs — source lines 1-310](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Sales/SalePosting.cs#L1-L310): `ValidatePayload; ResolveContextAsync; PriceLines; ValidatePayment`. [E11](#e-e11).
- **EXISTING / VERIFIED:** [.github/workflows/ci.yml — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/.github/workflows/ci.yml): `build-and-test; build-desktop`. [E41](#e-e41).

All CREATE/GENERATE symbols, routes and constraints in B/D are **NEW**. Source presence is not runtime proof; do not infer executed results from old evidence prose.

### D. Ordered implementation recipe

1. For cookie-authenticated unsafe verbs, validate an antiforgery request token/header and same-origin policy. Obtain a token through an authenticated same-origin endpoint; never rely on UseAntiforgery alone for arbitrary JSON. Native bearer calls use their separate authorization scheme and do not inherit a cookie-CSRF bypass header.
2. Set production cookie Secure=Always, HttpOnly and explicit SameSite; configure forwarded headers only for trusted proxy addresses and constrain allowed hosts/origins. Fail startup on insecure production public URI or missing persistent protected key-ring configuration.
3. Apply bounded login/reset and financial-command rate limits with Retry-After; reject excessive body size, invalid enums, unknown contract generations and malformed cursor/version headers before domain writes.
4. Normalize translated EF/Npgsql constraint errors into established codes without SQL, stack traces or secret data. Validate incoming correlation IDs for size/format; preserve one correlation through audit and logs.
5. Ensure client failures stay visible: no empty list or cached privileged success after a denied API response. Maintain explicit cancellation and OutcomeUnknown handling for commands.

Apply the specific D01–D17 policies relevant to this slice. Finish the narrow route/model/client registration in B, record generated migration paths, and preserve a safe stop/fix-forward path before presenting the diff for review.

### E. Focused tests

**NEW file:** `TestQtsBilling/Integration/HttpBoundaryTests.cs`. Name the class `HttpBoundaryTests` in the directory-derived namespace specified in §6.3.

- **T06.01:** Given a valid session cookie but absent/invalid antiforgery header, when a cross-origin state change is attempted, then it fails with no write.
- **T06.02:** Given trusted native bearer auth without a cookie, when a permitted command runs, then cookie antiforgery rules do not break it.
- **T06.03:** Given spoofed forwarded headers, malformed enums, oversized payload or invalid cursor, when the task-specific command or transition is exercised, then rejection is bounded and does not expose SQL.
- **T06.04:** Given repeated reset/login requests, when the task-specific command or transition is exercised, then throttling returns a stable generic result and does not leak account existence.
- **T06.05:** Given a DbUpdateException wrapping a known unique constraint, when the task-specific command or transition is exercised, then it maps to the intended 409 rather than a leaked 500.

Reuse existing or earlier-task fixtures. For a mutation, also assert denied tenant/location/permission and injected pre-commit failure leave zero partial financial/audit effects; for a read/UI-only task assert no mutation. Reuse the same fixture rather than duplicating equivalent tests.

### F. Verification

**Status: NOT RUN - SPEC_ONLY.** The following filter becomes executable only after the NEW test class and prerequisites are implemented.

Requires Task 01's isolated PostgreSQL 18/API fixture, .NET 10 SDK and explicitly validated QTS_TEST_POSTGRES_CONNECTION. Fixture DDL/data writes are permitted only in a later authorized test run; never inherit production credentials.

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~TestQtsBilling.Integration.HttpBoundaryTests"
```

**PASS:** owning cases and relevant existing regression filters pass, with exact committed-state assertions where applicable; no skipped required prerequisite, new unreviewed dependency, secret leak or `git diff --check` failure. A build or mocked response alone is not financial proof.

Select the checked server or Windows build command in §6.3 only when this task changes that host. Full multi-platform regression belongs in CI/release, not every local edit.

### G. Definition of done

Production API enforces origin/session/CSRF boundaries, safe error semantics and bounded input/rate controls without breaking native bearer requests. Evidence includes the exact implementation SHA, focused test outcome and minimal reviewed diff. All new constraints/contracts/permissions/host wiring ship together under **G2**; unresolved external prerequisites stay explicit.

No unnecessary dependency or compatibility path remains. Recovery preserves historical facts and pending money. Record remaining blockers; spec completion is not implementation completion or owner production approval.

<a id="task-07"></a>
## Atomic Task 07 - Complete trusted owner enrolment, invitations, password recovery and MFA

### A. Objective

**Priority:** P0. **Requirements:** `R03`, `R04`, `R18`. **Findings:** `F06`, `F09`, `F43`, `F44`.
**Dependencies:** [Task 04](#task-04), [Task 06](#task-06) **Integration gate:** G2.

**Current → target:** Demo setup and reset handlers exist, but production token delivery and self-service secure administration are incomplete. → A real owner can enrol and recover access without default credentials, logged tokens or SQL edits; privileged roles use MFA with auditable recovery.

**Acceptance:** The target behavior is observable through the owning API/UI/store and every applicable scenario in E passes; release additionally requires the named gate, not just this file diff.

**Non-goals:** No Entra/M365 integration mandate and no custom password cryptography.

### B. Scope

| Action | Exact path | Change / exclusion |
|---|---|---|
| MODIFY | `QtsWebApp/QtsWebApp/Features/Auth/AuthEndpoints.cs` | Safe first-owner/invitation/reset/MFA flows; generic external responses. |
| CREATE — NEW | `QtsWebApp/QtsWebApp/Features/Auth/AccountEnrolment.cs` | Single-use enrolment, privileged MFA/recovery and delivery scheduling. |
| CREATE — NEW | `QtsBilling.Contracts/Identity/EnrolmentDtos.cs` | Native invitations, setup and MFA challenge contracts. |
| GENERATE — NEW | `QtsBilling.Persistence.Postgres/Migrations/` | NEW AccountEnrolmentState schema and model companions only if required beyond Identity token/security fields. |
| CREATE — NEW test | `TestQtsBilling/Integration/AccountEnrolmentTests.cs` | Owning focused assertions below; reuse fixtures rather than duplicate a test infrastructure. |

MODIFY existing `QtsWebApp/QtsWebApp/Program.cs` only for required route/service registration, and `QtsBilling.Contracts/Identity/IdentityDtos.cs` / `QtsWebApp/QtsWebApp/Infrastructure/QtsIdentity.cs` only for this task's explicit D02 policies.

All other production paths are excluded except the narrow, explicitly enumerated [D17 integration bindings](#d17). Applied migrations, posted facts and unresolved cash are never edited/deleted by this scope.

### C. Verified anchors and NEW insertions

- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Program.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Program.cs): `Identity registration, cookies, API registration, WebLocalPosService`. [E03](#e-e03).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Infrastructure/QtsIdentity.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Infrastructure/QtsIdentity.cs): `PermissionAuthorizationHandler.HandleRequirementAsync; QtsClaimsPrincipalFactory`. [E04](#e-e04).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Auth/AuthEndpoints.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Auth/AuthEndpoints.cs): `LoginAsync; RequestPasswordResetAsync; CompletePasswordResetAsync`. [E05](#e-e05).
- **EXISTING / VERIFIED:** [docs/migration/decisions.md — source lines 1-155](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/docs/migration/decisions.md#L1-L155): `D-01...D-41 and historical decisions`. [E44](#e-e44).

All CREATE/GENERATE symbols, routes and constraints in B/D are **NEW**. Source presence is not runtime proof; do not infer executed results from old evidence prose.

### D. Ordered implementation recipe

1. Provide one-time first-owner bootstrap through an operator-supplied, short-lived secret delivered outside the repository; atomically consume it, require a new password and enrol MFA before privileged operations. Disable demo seeding entirely in Production.
2. Owner invitations create inactive/unusable credentials until the intended recipient completes an expiring, single-use ticket. Role/business/location assignments are server-controlled and cannot be submitted by the recipient as authority.
3. Use Identity password/token/MFA primitives. Require MFA for Owner and Accountant and for privileged approval elevation; Manager approval capabilities require MFA. Recovery codes are shown once, hashed/protected appropriately and never logged.
4. Production reset/invitation delivery is queued transactionally and dispatched through the one configured channel introduced in Task 55. Until configured, readiness fails; do not return a misleading successful delivery claim. External responses are generic before password validation.
5. On reset, privilege change or recovery, rotate security stamp and revoke affected native sessions/offline grants. Persist/encrypt Data Protection keys; routine key rotation retains required decryption keys rather than deleting the ring.

Apply the specific D01–D17 policies relevant to this slice. Finish the narrow route/model/client registration in B, record generated migration paths, and preserve a safe stop/fix-forward path before presenting the diff for review.

### E. Focused tests

**NEW file:** `TestQtsBilling/Integration/AccountEnrolmentTests.cs`. Name the class `AccountEnrolmentTests` in the directory-derived namespace specified in §6.3.

- **T07.01:** Given a production bootstrap already consumed, when replayed, then a second owner cannot be created.
- **T07.02:** Given an unknown vs setup-required email, when anonymous reset/login requests are made, then responses do not expose distinct account state.
- **T07.03:** Given an expired or reused invitation/reset ticket, when the task-specific command or transition is exercised, then no credentials or role grants change.
- **T07.04:** Given a password reset/MFA recovery, when the task-specific command or transition is exercised, then prior privileged sessions and offline authorization are invalidated at the next online check.
- **T07.05:** Given missing delivery configuration, when the task-specific command or transition is exercised, then production readiness is blocked and no email-sent claim is recorded.

Reuse existing or earlier-task fixtures. For a mutation, also assert denied tenant/location/permission and injected pre-commit failure leave zero partial financial/audit effects; for a read/UI-only task assert no mutation. Reuse the same fixture rather than duplicating equivalent tests.

### F. Verification

**Status: NOT RUN - SPEC_ONLY.** The following filter becomes executable only after the NEW test class and prerequisites are implemented.

Requires Task 01's isolated PostgreSQL 18/API fixture, .NET 10 SDK and explicitly validated QTS_TEST_POSTGRES_CONNECTION. Fixture DDL/data writes are permitted only in a later authorized test run; never inherit production credentials.

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~TestQtsBilling.Integration.AccountEnrolmentTests"
```

**PASS:** owning cases and relevant existing regression filters pass, with exact committed-state assertions where applicable; no skipped required prerequisite, new unreviewed dependency, secret leak or `git diff --check` failure. A build or mocked response alone is not financial proof.

Select the checked server or Windows build command in §6.3 only when this task changes that host. Full multi-platform regression belongs in CI/release, not every local edit.

### G. Definition of done

A real owner can enrol and recover access without default credentials, logged tokens or SQL edits; privileged roles use MFA with auditable recovery. Evidence includes the exact implementation SHA, focused test outcome and minimal reviewed diff. All new constraints/contracts/permissions/host wiring ship together under **G2**; unresolved external prerequisites stay explicit.

Real delivery, first-owner custody and recovery drill remain BLOCKED_EXTERNAL until exercised with the chosen production channel. Couple with Task 55 before release. No unnecessary dependency or compatibility path remains. Recovery preserves historical facts and pending money. Record remaining blockers; spec completion is not implementation completion or owner production approval.

<a id="task-08"></a>
## Atomic Task 08 - Provide native business, location and financial setup

### A. Objective

**Priority:** P1. **Requirements:** `R03`, `R05`, `R06`, `R11`. **Findings:** `F15`, `F18`, `F20`, `F43`.
**Dependencies:** [Task 04](#task-04), [Task 05](#task-05), [Task 07](#task-07) **Integration gate:** G2.

**Current → target:** Business/location and account concepts exist, but routine operation relies on seed data and hard-coded account codes. → A new native business can be configured completely and activated only when financial/security prerequisites are valid.

**Acceptance:** The target behavior is observable through the owning API/UI/store and every applicable scenario in E passes; release additionally requires the named gate, not just this file diff.

**Non-goals:** No automatic country-specific tax certification, multiple transaction currencies or unrestricted owner overrides.

### B. Scope

| Action | Exact path | Change / exclusion |
|---|---|---|
| CREATE — NEW | `QtsWebApp/QtsWebApp/Features/Setup/BusinessSetupEndpoints.cs` | Owner/accountant-controlled setup API and readiness validation. |
| CREATE — NEW | `QtsBilling.Contracts/Setup/BusinessSetupDtos.cs` | Business, location, currency, period, account-mapping and cash-policy contracts. |
| MODIFY | `QtsBilling.Persistence.Postgres/Entities/Core.cs` | Explicit settings/authorization versions and supported accounting configuration. |
| GENERATE — NEW | `QtsBilling.Persistence.Postgres/Migrations/` | NEW BusinessFinancialSettings with same-business account references and model configuration. |
| CREATE — NEW test | `TestQtsBilling/Integration/BusinessSetupTests.cs` | Owning focused assertions below; reuse fixtures rather than duplicate a test infrastructure. |

MODIFY existing `QtsBilling.Persistence.Postgres/BillingDbContext.cs` and the appropriate checked `BillingDbContext.Model.cs` / `BillingDbContext.Model.Financial.cs` only for the listed model; GENERATE designer/snapshot companions with the named new migration.

MODIFY existing `QtsWebApp/QtsWebApp/Program.cs` only for required route/service registration, and `QtsBilling.Contracts/Identity/IdentityDtos.cs` / `QtsWebApp/QtsWebApp/Infrastructure/QtsIdentity.cs` only for this task's explicit D02 policies.

All other production paths are excluded except the narrow, explicitly enumerated [D17 integration bindings](#d17). Applied migrations, posted facts and unresolved cash are never edited/deleted by this scope.

### C. Verified anchors and NEW insertions

- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Program.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Program.cs): `Identity registration, cookies, API registration, WebLocalPosService`. [E03](#e-e03).
- **EXISTING / VERIFIED:** [QtsBilling.Persistence.Postgres/BillingDbContext.Model.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Persistence.Postgres/BillingDbContext.Model.cs): `ConfigureCore; ConfigureMasterData; ConfigureCatalog; ConfigureInventory`. [E06](#e-e06).
- **EXISTING / VERIFIED:** [QtsBilling.Persistence.Postgres/BillingDbContext.Model.Financial.cs — source lines 210-445](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Persistence.Postgres/BillingDbContext.Model.Financial.cs#L210-L445): `ConfigureAccounting; ConfigurePurchasing`. [E07](#e-e07).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Accounting/LedgerWriter.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Accounting/LedgerWriter.cs): `PostWithinTransactionAsync; AllocateNumberAsync; LocalDate`. [E10](#e-e10).
- **EXISTING / VERIFIED:** [docs/migration/decisions.md — source lines 1-155](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/docs/migration/decisions.md#L1-L155): `D-01...D-41 and historical decisions`. [E44](#e-e44).

All CREATE/GENERATE symbols, routes and constraints in B/D are **NEW**. Source presence is not runtime proof; do not infer executed results from old evidence prose.

### D. Ordered implementation recipe

1. Add GET/PUT /api/v1/business/settings and POST/PUT /api/v1/locations with expected version and business/location authorization. Validate time zone explicitly, currency code and settlement scale; preserve identity fields required on issued documents.
2. Create validated account-role mappings for cash/safe/drawers, bank, AR, AP, advances, inventory, COGS, revenue, output/input tax, GRNI, delivered-not-invoiced, supplier-return clearing, gain/loss, rounding and price variance. Seed an editable chart template, not immutable hard-coded numeric account lookup.
3. Require an open, non-overlapping opening period and approved supported costing policy before financial activation. Use moving weighted average only for new native stock. Existing valuation changes require a signed opening/reconciliation plan.
4. Lock immutable-with-history fields once posted facts exist: base currency, conversion basis and accounting policy cannot be changed by ordinary settings PATCH. Settings changes that affect new transactions increment policy version and emit a scoped feed projection.
5. Provide a readiness response of concrete unmet prerequisites: account mappings, tax/cash policy, key custody, reset delivery, location/register/device enrolment, unresolved openings. Never make default business or location selection from the first database row.

Apply the specific D01–D17 policies relevant to this slice. Finish the narrow route/model/client registration in B, record generated migration paths, and preserve a safe stop/fix-forward path before presenting the diff for review.

### E. Focused tests

**NEW file:** `TestQtsBilling/Integration/BusinessSetupTests.cs`. Name the class `BusinessSetupTests` in the directory-derived namespace specified in §6.3.

- **T08.01:** Given an invalid time zone or foreign-business account mapping, when the task-specific command or transition is exercised, then setup fails with field errors and no partially activated business.
- **T08.02:** Given a valid empty business, when native setup completes, then daily workflows need no SQL or demo account.
- **T08.03:** Given posted financial history, when base currency/cost method changes are attempted, then normal settings updates refuse them.
- **T08.04:** Given missing opening period/account mapping, when sale posting is attempted, then it is blocked explicitly.
- **T08.05:** Given an allowed price/tax-policy update, when the task-specific command or transition is exercised, then the version changes and historical documents stay unchanged.

Reuse existing or earlier-task fixtures. For a mutation, also assert denied tenant/location/permission and injected pre-commit failure leave zero partial financial/audit effects; for a read/UI-only task assert no mutation. Reuse the same fixture rather than duplicating equivalent tests.

### F. Verification

**Status: NOT RUN - SPEC_ONLY.** The following filter becomes executable only after the NEW test class and prerequisites are implemented.

Requires Task 01's isolated PostgreSQL 18/API fixture, .NET 10 SDK and explicitly validated QTS_TEST_POSTGRES_CONNECTION. Fixture DDL/data writes are permitted only in a later authorized test run; never inherit production credentials.

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~TestQtsBilling.Integration.BusinessSetupTests"
```

**PASS:** owning cases and relevant existing regression filters pass, with exact committed-state assertions where applicable; no skipped required prerequisite, new unreviewed dependency, secret leak or `git diff --check` failure. A build or mocked response alone is not financial proof.

Select the checked server or Windows build command in §6.3 only when this task changes that host. Full multi-platform regression belongs in CI/release, not every local edit.

### G. Definition of done

A new native business can be configured completely and activated only when financial/security prerequisites are valid. Evidence includes the exact implementation SHA, focused test outcome and minimal reviewed diff. All new constraints/contracts/permissions/host wiring ship together under **G2**; unresolved external prerequisites stay explicit.

No unnecessary dependency or compatibility path remains. Recovery preserves historical facts and pending money. Record remaining blockers; spec completion is not implementation completion or owner production approval.

<a id="task-09"></a>
## Atomic Task 09 - Enforce posted-document immutability and final-state database guards

### A. Objective

**Priority:** P0. **Requirements:** `R06`, `R07`, `R08`. **Findings:** `F17`, `F19`, `F32`.
**Dependencies:** [Task 01](#task-01), [Task 05](#task-05) **Integration gate:** G1.

**Current → target:** Current guards cover selected context writes and omit critical direct-SQL paths and OLD-parent/INSERT checks. → Posted terms cannot be changed through EF, bulk SQL or application-role DML; deferred stock checks assess the actual final state.

**Acceptance:** The target behavior is observable through the owning API/UI/store and every applicable scenario in E passes; release additionally requires the named gate, not just this file diff.

**Non-goals:** Never edit applied migrations or bypass guards for the native importer. Existing inconsistent rows block the migration until explicitly reconciled.

### B. Scope

| Action | Exact path | Change / exclusion |
|---|---|---|
| MODIFY | `QtsBilling.Persistence.Postgres/BillingDbContext.cs` | Align context guards with the exact native posted-state contract. |
| GENERATE — NEW | `QtsBilling.Persistence.Postgres/Migrations/` | NEW HardenPostedDocumentAndFinalStateGuards; new SQL functions/triggers plus designer/snapshot. |
| MODIFY | `QtsWebApp/QtsWebApp/Features/Invoices/InvoiceEndpoints.cs` | Lock and recheck draft state/version in the same transaction for edit/delete/issue. |
| CREATE — NEW test | `TestQtsBilling/Integration/PostedInvariantTests.cs` | Owning focused assertions below; reuse fixtures rather than duplicate a test infrastructure. |

MODIFY existing `QtsWebApp/QtsWebApp/Program.cs` only for required route/service registration, and `QtsBilling.Contracts/Identity/IdentityDtos.cs` / `QtsWebApp/QtsWebApp/Infrastructure/QtsIdentity.cs` only for this task's explicit D02 policies.

All other production paths are excluded except the narrow, explicitly enumerated [D17 integration bindings](#d17). Applied migrations, posted facts and unresolved cash are never edited/deleted by this scope.

### C. Verified anchors and NEW insertions

- **EXISTING / VERIFIED:** [QtsBilling.Persistence.Postgres/BillingDbContext.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Persistence.Postgres/BillingDbContext.cs): `SaveChanges guards and AppendOnlyTypes`. [E08](#e-e08).
- **EXISTING / VERIFIED:** [QtsBilling.Persistence.Postgres/Migrations/20261019082426_AddIntegrityGuards.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Persistence.Postgres/Migrations/20261019082426_AddIntegrityGuards.cs): `qts_invoice_lines_draft_guard; qts_inventory_balance_guard; deferred balance triggers`. [E09](#e-e09).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Invoices/InvoiceEndpoints.cs — source lines 360-650](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Invoices/InvoiceEndpoints.cs#L360-L650): `DeleteAsync; IssueAsync`. [E32](#e-e32).

All CREATE/GENERATE symbols, routes and constraints in B/D are **NEW**. Source presence is not runtime proof; do not infer executed results from old evidence prose.

### D. Ordered implementation recipe

1. Create an explicit allowed-transition/immutable-column matrix for journal/sale/invoice/payment/credit/PO/receipt/return headers and lines. Header status is not permission to rewind a posted document to draft.
2. New invoice/PO line triggers handle INSERT, UPDATE and DELETE; on reparenting, validate both old and new owning documents and prohibit moving historical lines into editable drafts. Guard financial headers and settled-payment amount/currency/subject fields as well.
3. Replace the faulty stock deferred check in a new migration so it reads the final persisted balance and complete movement sum for the key at commit, including absence on either side. Keep per-entry deferred journal balancing and enforce valid nonempty posted entries.
4. Ensure the application role cannot TRUNCATE/disable triggers or bypass guards through bulk DML. Owner/migration privileges are not used by the web app.
5. Lock invoice draft row before bulk deleting or replacing lines; recheck version and status inside that transaction. A concurrent issue must either serialize first and refuse the edit, or issue the fully edited draft—never delete an issued invoice.

Apply the specific D01–D17 policies relevant to this slice. Finish the narrow route/model/client registration in B, record generated migration paths, and preserve a safe stop/fix-forward path before presenting the diff for review.

### E. Focused tests

**NEW file:** `TestQtsBilling/Integration/PostedInvariantTests.cs`. Name the class `PostedInvariantTests` in the directory-derived namespace specified in §6.3.

- **T09.01:** Given an issued invoice, when inserting a new line, changing totals or moving a line to a draft via raw application-role SQL, then every path fails.
- **T09.02:** Given two valid updates to one stock key in one transaction, when the task-specific command or transition is exercised, then final correct quantity/movement state commits; a mismatched final state fails.
- **T09.03:** Given a race between draft deletion/edit and issue, when the task-specific command or transition is exercised, then one complete legal outcome occurs and no issued history disappears.
- **T09.04:** Given a posted journal/payment, when mutable state is abused to rewind/reparent/change amount, then the database refuses it.
- **T09.05:** Given a valid reversal/settlement projection transition, when the task-specific command or transition is exercised, then it remains possible without relaxing immutable commercial terms.

Reuse existing or earlier-task fixtures. For a mutation, also assert denied tenant/location/permission and injected pre-commit failure leave zero partial financial/audit effects; for a read/UI-only task assert no mutation. Reuse the same fixture rather than duplicating equivalent tests.

### F. Verification

**Status: NOT RUN - SPEC_ONLY.** The following filter becomes executable only after the NEW test class and prerequisites are implemented.

Requires Task 01's isolated PostgreSQL 18/API fixture, .NET 10 SDK and explicitly validated QTS_TEST_POSTGRES_CONNECTION. Fixture DDL/data writes are permitted only in a later authorized test run; never inherit production credentials.

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~TestQtsBilling.Integration.PostedInvariantTests"
```

**PASS:** owning cases and relevant existing regression filters pass, with exact committed-state assertions where applicable; no skipped required prerequisite, new unreviewed dependency, secret leak or `git diff --check` failure. A build or mocked response alone is not financial proof.

Select the checked server or Windows build command in §6.3 only when this task changes that host. Full multi-platform regression belongs in CI/release, not every local edit.

### G. Definition of done

Posted terms cannot be changed through EF, bulk SQL or application-role DML; deferred stock checks assess the actual final state. Evidence includes the exact implementation SHA, focused test outcome and minimal reviewed diff. All new constraints/contracts/permissions/host wiring ship together under **G1**; unresolved external prerequisites stay explicit.

No unnecessary dependency or compatibility path remains. Recovery preserves historical facts and pending money. Record remaining blockers; spec completion is not implementation completion or owner production approval.

<a id="task-10"></a>
## Atomic Task 10 - Make journal posting and period close mutually consistent

### A. Objective

**Priority:** P0. **Requirements:** `R06`, `R08`, `R14`. **Findings:** `F20`.
**Dependencies:** [Task 08](#task-08), [Task 09](#task-09) **Integration gate:** G1.

**Current → target:** Posting can proceed with no accounting period; closing and posting do not share a proven lock boundary. → Every financial entry is in one open business period, close cannot race a posting, and numbering/date rules are reproducible.

**Acceptance:** The target behavior is observable through the owning API/UI/store and every applicable scenario in E passes; release additionally requires the named gate, not just this file diff.

**Non-goals:** No period reopen by ordinary user PATCH. Finance approval is required for exceptional reopening/correcting procedures.

### B. Scope

| Action | Exact path | Change / exclusion |
|---|---|---|
| MODIFY | `QtsWebApp/QtsWebApp/Features/Accounting/LedgerWriter.cs` | Require/lock a valid period, native account mappings and deterministic business date/numbering. |
| MODIFY | `QtsWebApp/QtsWebApp/Features/Accounting/AccountingEndpoints.cs` | Period lifecycle, closed-state controls and journal input/close validation. |
| GENERATE — NEW | `QtsBilling.Persistence.Postgres/Migrations/` | NEW AccountingPeriodAndPostingIntegrity constraints, indexes and guarded transitions. |
| CREATE — NEW test | `TestQtsBilling/Integration/PeriodPostingTests.cs` | Owning focused assertions below; reuse fixtures rather than duplicate a test infrastructure. |

MODIFY existing `QtsWebApp/QtsWebApp/Program.cs` only for required route/service registration, and `QtsBilling.Contracts/Identity/IdentityDtos.cs` / `QtsWebApp/QtsWebApp/Infrastructure/QtsIdentity.cs` only for this task's explicit D02 policies.

All other production paths are excluded except the narrow, explicitly enumerated [D17 integration bindings](#d17). Applied migrations, posted facts and unresolved cash are never edited/deleted by this scope.

### C. Verified anchors and NEW insertions

- **EXISTING / VERIFIED:** [QtsBilling.Persistence.Postgres/BillingDbContext.Model.Financial.cs — source lines 210-445](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Persistence.Postgres/BillingDbContext.Model.Financial.cs#L210-L445): `ConfigureAccounting; ConfigurePurchasing`. [E07](#e-e07).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Accounting/LedgerWriter.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Accounting/LedgerWriter.cs): `PostWithinTransactionAsync; AllocateNumberAsync; LocalDate`. [E10](#e-e10).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Reporting/ReportEndpoints.cs — source lines 1-240](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Reporting/ReportEndpoints.cs#L1-L240): `BalanceSheetAsync; ProfitAndLossAsync`. [E34](#e-e34).

All CREATE/GENERATE symbols, routes and constraints in B/D are **NEW**. Source presence is not runtime proof; do not infer executed results from old evidence prose.

### D. Ordered implementation recipe

1. Apply D04 period validation to every LedgerWriter call. Require exact account business/activity/kind, currency, balanced nonempty lines and valid effective date; reject a missing period rather than nulling its reference.
2. Prevent overlapping periods under a business setup lock or exclusion constraint suitable for the installed PostgreSQL configuration; avoid an unneeded extension if a transactionally locked business anchor suffices.
3. Use a shared period row lock during posting and exclusive closing; close verifies pending/uncertain financial commands, unreconciled openings and defined subledger differences. Late offline operations cannot backdate into a closed period.
4. Preserve transactional sequence allocation but obtain prefix/year/series from native setup and business effective date. Rollback does not consume a committed document number; cancelled/voided issued numbers are never recycled.
5. Original posted entries remain reportable after reversal. Store a new linked reversing entry and derive reversal status, rather than filtering the original out of historical reporting. Invalid timezone/default-period conditions are typed failures.

Apply the specific D01–D17 policies relevant to this slice. Finish the narrow route/model/client registration in B, record generated migration paths, and preserve a safe stop/fix-forward path before presenting the diff for review.

### E. Focused tests

**NEW file:** `TestQtsBilling/Integration/PeriodPostingTests.cs`. Name the class `PeriodPostingTests` in the directory-derived namespace specified in §6.3.

- **T10.01:** Given no/open/closed/overlapping periods, when posting, then only the single valid open period succeeds.
- **T10.02:** Given close and post racing on the same date, when the task-specific command or transition is exercised, then either the post is included before close or it is refused after close; no post appears outside the lock decision.
- **T10.03:** Given concurrent numbering and one rollback, when the task-specific command or transition is exercised, then committed documents have unique scoped numbers and immutable used numbers.
- **T10.04:** Given an original entry and later reversal, when the task-specific command or transition is exercised, then each remains in the correct historical reporting period.
- **T10.05:** Given an invalid business time zone, when the task-specific command or transition is exercised, then configuration/posting fails instead of silently using UTC.

Reuse existing or earlier-task fixtures. For a mutation, also assert denied tenant/location/permission and injected pre-commit failure leave zero partial financial/audit effects; for a read/UI-only task assert no mutation. Reuse the same fixture rather than duplicating equivalent tests.

### F. Verification

**Status: NOT RUN - SPEC_ONLY.** The following filter becomes executable only after the NEW test class and prerequisites are implemented.

Requires Task 01's isolated PostgreSQL 18/API fixture, .NET 10 SDK and explicitly validated QTS_TEST_POSTGRES_CONNECTION. Fixture DDL/data writes are permitted only in a later authorized test run; never inherit production credentials.

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~TestQtsBilling.Integration.PeriodPostingTests"
```

**PASS:** owning cases and relevant existing regression filters pass, with exact committed-state assertions where applicable; no skipped required prerequisite, new unreviewed dependency, secret leak or `git diff --check` failure. A build or mocked response alone is not financial proof.

Select the checked server or Windows build command in §6.3 only when this task changes that host. Full multi-platform regression belongs in CI/release, not every local edit.

### G. Definition of done

Every financial entry is in one open business period, close cannot race a posting, and numbering/date rules are reproducible. Evidence includes the exact implementation SHA, focused test outcome and minimal reviewed diff. All new constraints/contracts/permissions/host wiring ship together under **G1**; unresolved external prerequisites stay explicit.

No unnecessary dependency or compatibility path remains. Recovery preserves historical facts and pending money. Record remaining blockers; spec completion is not implementation completion or owner production approval.

<a id="task-11"></a>
## Atomic Task 11 - Make all financial commands scope-safe under duplicate and uncertain retries

### A. Objective

**Priority:** P0. **Requirements:** `R06`, `R08`. **Findings:** `F13`, `F17`, `F21`, `F23`, `F31`, `F32`.
**Dependencies:** [Task 01](#task-01), [Task 04](#task-04), [Task 09](#task-09), [Task 10](#task-10) **Integration gate:** G1.

**Current → target:** Idempotency differs by route and some retries reuse stale tracked state or return a conflicting race winner. → Every financial command uses D05 with stable result identity and no duplicate business effects under retries or changed route subjects.

**Acceptance:** The target behavior is observable through the owning API/UI/store and every applicable scenario in E passes; release additionally requires the named gate, not just this file diff.

**Non-goals:** No promise of exactly-once network delivery; guarantee at-most-one accepted business effect with durable replay semantics.

### B. Scope

| Action | Exact path | Change / exclusion |
|---|---|---|
| MODIFY | `QtsBilling.Contracts/Serialization/CanonicalJson.cs` | Scoped semantic fingerprint helper including command kind and route subject. |
| MODIFY | `QtsWebApp/QtsWebApp/Features/Sync/SyncTransaction.cs` | Fresh-state retry/verification contract with declared lock order. |
| CREATE — NEW | `QtsWebApp/QtsWebApp/Infrastructure/FinancialCommandReceipt.cs` | Small receipt lookup/compare helper for existing aggregate receipts; no new generic repository. |
| GENERATE — NEW | `QtsBilling.Persistence.Postgres/Migrations/` | NEW missing scoped command-receipt/unique constraints, only for financial commands without a durable result. |
| CREATE — NEW test | `TestQtsBilling/Integration/FinancialIdempotencyTests.cs` | Owning focused assertions below; reuse fixtures rather than duplicate a test infrastructure. |

All other production paths are excluded except the narrow, explicitly enumerated [D17 integration bindings](#d17). Applied migrations, posted facts and unresolved cash are never edited/deleted by this scope.

### C. Verified anchors and NEW insertions

- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Accounting/LedgerWriter.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Accounting/LedgerWriter.cs): `PostWithinTransactionAsync; AllocateNumberAsync; LocalDate`. [E10](#e-e10).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Sync/SyncTransaction.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Sync/SyncTransaction.cs): `RunAsync`. [E14](#e-e14).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Sync/SyncOperationEndpoints.cs — source lines 1-460](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Sync/SyncOperationEndpoints.cs#L1-L460): `ProcessAsync / operation and batch handlers`. [E15](#e-e15).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Purchasing/PurchasingEndpoints.cs — source lines 640-1030](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Purchasing/PurchasingEndpoints.cs#L640-L1030): `ConfirmOrderAsync; CancelOrderAsync; PostReceiptAsync`. [E29](#e-e29).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Sales/CashRegisterEndpoints.cs — source lines 409-550](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Sales/CashRegisterEndpoints.cs#L409-L550): `AddMovementAsync`. [E33](#e-e33).

All CREATE/GENERATE symbols, routes and constraints in B/D are **NEW**. Source presence is not runtime proof; do not infer executed results from old evidence prose.

### D. Ordered implementation recipe

1. Introduce a small native fingerprint envelope of operation kind, route subject, business/location context and canonical semantic payload. Do not include transport trace/time noise; validate context before accepting a key.
2. Keep sale/payment/refund/receipt/expense existing durable receipts and unique source constraints. Add missing command receipts for state-changing operations without one. Return original document/result fields consistently; a display number may not disappear on replay.
3. Change transaction retry callers to reload entities in a clean context and follow D04 lock ordering. Predetermine the operation ID; on commit uncertainty, use an independent clean read of the receipt before retrying writes.
4. For unique/deadlock/serialization failures, distinguish safe retry from business conflict. A uniqueness winner must match both fingerprint and subject. Catch wrapped provider exceptions by named constraint, not message substring.
5. Integrate the helper into each affected feature in its dedicated downstream task (12,16,17,24,37-46,48,50-51). No gate ships while some enabled financial command still uses the old body-only/unknown-outcome behavior.

Apply the specific D01–D17 policies relevant to this slice. Finish the narrow route/model/client registration in B, record generated migration paths, and preserve a safe stop/fix-forward path before presenting the diff for review.

### E. Focused tests

**NEW file:** `TestQtsBilling/Integration/FinancialIdempotencyTests.cs`. Name the class `FinancialIdempotencyTests` in the directory-derived namespace specified in §6.3.

- **T11.01:** Given one key/body submitted concurrently, when the task-specific command or transition is exercised, then exactly one business effect is committed and all successful replays identify that result.
- **T11.02:** Given the same body/key against receipt and return routes or two order IDs, when the task-specific command or transition is exercised, then changed scope conflicts.
- **T11.03:** Given a simulated dropped commit acknowledgement, when the task-specific command or transition is exercised, then the next attempt reads the existing receipt and creates no second entry/stock movement.
- **T11.04:** Given a serialization retry after tracked-state mutation, when the task-specific command or transition is exercised, then the new attempt reloads clean values and does not double-increment.
- **T11.05:** Given a concurrent winner with another payload, when the task-specific command or transition is exercised, then uniqueness recovery returns 409, not the winner as if it matched.

Reuse existing or earlier-task fixtures. For a mutation, also assert denied tenant/location/permission and injected pre-commit failure leave zero partial financial/audit effects; for a read/UI-only task assert no mutation. Reuse the same fixture rather than duplicating equivalent tests.

### F. Verification

**Status: NOT RUN - SPEC_ONLY.** The following filter becomes executable only after the NEW test class and prerequisites are implemented.

Requires Task 01's isolated PostgreSQL 18/API fixture, .NET 10 SDK and explicitly validated QTS_TEST_POSTGRES_CONNECTION. Fixture DDL/data writes are permitted only in a later authorized test run; never inherit production credentials.

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~TestQtsBilling.Integration.FinancialIdempotencyTests"
```

**PASS:** owning cases and relevant existing regression filters pass, with exact committed-state assertions where applicable; no skipped required prerequisite, new unreviewed dependency, secret leak or `git diff --check` failure. A build or mocked response alone is not financial proof.

Select the checked server or Windows build command in §6.3 only when this task changes that host. Full multi-platform regression belongs in CI/release, not every local edit.

### G. Definition of done

Every financial command uses D05 with stable result identity and no duplicate business effects under retries or changed route subjects. Evidence includes the exact implementation SHA, focused test outcome and minimal reviewed diff. All new constraints/contracts/permissions/host wiring ship together under **G1**; unresolved external prerequisites stay explicit.

No unnecessary dependency or compatibility path remains. Recovery preserves historical facts and pending money. Record remaining blockers; spec completion is not implementation completion or owner production approval.

<a id="task-12"></a>
## Atomic Task 12 - Repair POS price, inclusive-tax, discount and posting validation

### A. Objective

**Priority:** P0. **Requirements:** `R05`, `R06`, `R08`. **Findings:** `F11`, `F13`, `F15`.
**Dependencies:** [Task 03](#task-03), [Task 04](#task-04), [Task 08](#task-08), [Task 10](#task-10), [Task 11](#task-11) **Integration gate:** G1.

**Current → target:** Inclusive totals and revenue journal differ; arbitrary line discounts and stale catalog data can reach posting. → Online POS produces balanced, exact historical commercial facts and rejects stale or unauthorized pricing with a reviewable outcome.

**Acceptance:** The target behavior is observable through the owning API/UI/store and every applicable scenario in E passes; release additionally requires the named gate, not just this file diff.

**Non-goals:** No card enablement, promotion engine or historical repricing.

### B. Scope

| Action | Exact path | Change / exclusion |
|---|---|---|
| MODIFY | `QtsWebApp/QtsWebApp/Features/Sales/SalePosting.cs` | Use shared pricing; revalidate active product/tax/policy inside the transaction; correct revenue journal. |
| MODIFY | `QtsWebApp/QtsWebApp/Features/Sales/SaleEndpoints.cs` | Consistent native payload, expected-price/version and replay/error contract. |
| MODIFY | `QtsBilling.Contracts/Sales/SaleCaptureDtos.cs` | Authorized override/discount evidence, captured policy and explicit amount result fields. |
| CREATE — NEW test | `TestQtsBilling/Integration/PosPostingTests.cs` | Owning focused assertions below; reuse fixtures rather than duplicate a test infrastructure. |

MODIFY existing `QtsWebApp/QtsWebApp/Program.cs` only for required route/service registration, and `QtsBilling.Contracts/Identity/IdentityDtos.cs` / `QtsWebApp/QtsWebApp/Infrastructure/QtsIdentity.cs` only for this task's explicit D02 policies.

All other production paths are excluded except the narrow, explicitly enumerated [D17 integration bindings](#d17). Applied migrations, posted facts and unresolved cash are never edited/deleted by this scope.

### C. Verified anchors and NEW insertions

- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Sales/SalePosting.cs — source lines 1-310](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Sales/SalePosting.cs#L1-L310): `ValidatePayload; ResolveContextAsync; PriceLines; ValidatePayment`. [E11](#e-e11).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Sales/SalePosting.cs — source lines 310-665](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Sales/SalePosting.cs#L310-L665): `PostAsync / posting transaction and revenue journal`. [E12](#e-e12).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Inventory/InventoryLedger.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Inventory/InventoryLedger.cs): `ApplyAsync`. [E13](#e-e13).

All CREATE/GENERATE symbols, routes and constraints in B/D are **NEW**. Source presence is not runtime proof; do not infer executed results from old evidence prose.

### D. Ordered implementation recipe

1. Replace local calculations with D03 CommercialAmounts. Journal credits net revenue and output tax separately; include explicit document rounding account when required. Validate mixed tax modes and all amount identities before stock mutation.
2. Inside the posting transaction reload active products/variants, unit conversion, tax policy and authorization version under the selected price-read consistency strategy. Reject missing/inactive customer/variant or unsupported pricing/costing profile.
3. Compare the requested price/policy/quantity with current authorized values. On a price change return 409 PRICE_CHANGED with bounded new quote details; cashier must explicitly reconfirm under a new command identity if no prior command committed.
4. Apply actor discount/override limits; require an authorized approval reference for exceeding them and snapshot approver/reason. Ignore neither the stored discount policy nor a failed approval.
5. Keep the existing atomic sale/line/stock/payment/journal/audit composition. Task 15 supplies actual cost movement values before G1 release; until then tracked-stock production activation remains blocked. Never fall back to catalog purchase price or zero.

Apply the specific D01–D17 policies relevant to this slice. Finish the narrow route/model/client registration in B, record generated migration paths, and preserve a safe stop/fix-forward path before presenting the diff for review.

### E. Focused tests

**NEW file:** `TestQtsBilling/Integration/PosPostingTests.cs`. Name the class `PosPostingTests` in the directory-derived namespace specified in §6.3.

- **T12.01:** Given an inclusive taxable sale, when the task-specific command or transition is exercised, then total debit equals revenue+tax credits exactly and the final receipt uses those snapshots.
- **T12.02:** Given a catalog price/tax change between initial read and commit, when the task-specific command or transition is exercised, then the transaction either uses a valid pinned quote policy or refuses with PRICE_CHANGED; it never silently reprices.
- **T12.03:** Given a cashier above their discount limit or with a foreign approval ID, when the task-specific command or transition is exercised, then no sale/stock/journal/audit result is committed.
- **T12.04:** Given repeated lines, fractional units and zero/100% discount, when the task-specific command or transition is exercised, then validated amounts and stock consequences are deterministic.
- **T12.05:** Given a failure after stock mutation before journal/receipt commit, when the task-specific command or transition is exercised, then all business effects roll back.

Reuse existing or earlier-task fixtures. For a mutation, also assert denied tenant/location/permission and injected pre-commit failure leave zero partial financial/audit effects; for a read/UI-only task assert no mutation. Reuse the same fixture rather than duplicating equivalent tests.

### F. Verification

**Status: NOT RUN - SPEC_ONLY.** The following filter becomes executable only after the NEW test class and prerequisites are implemented.

Requires Task 01's isolated PostgreSQL 18/API fixture, .NET 10 SDK and explicitly validated QTS_TEST_POSTGRES_CONNECTION. Fixture DDL/data writes are permitted only in a later authorized test run; never inherit production credentials.

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~TestQtsBilling.Integration.PosPostingTests"
```

**PASS:** owning cases and relevant existing regression filters pass, with exact committed-state assertions where applicable; no skipped required prerequisite, new unreviewed dependency, secret leak or `git diff --check` failure. A build or mocked response alone is not financial proof.

Select the checked server or Windows build command in §6.3 only when this task changes that host. Full multi-platform regression belongs in CI/release, not every local edit.

### G. Definition of done

Online POS produces balanced, exact historical commercial facts and rejects stale or unauthorized pricing with a reviewable outcome. Evidence includes the exact implementation SHA, focused test outcome and minimal reviewed diff. All new constraints/contracts/permissions/host wiring ship together under **G1**; unresolved external prerequisites stay explicit.

This task is not releasable without Tasks 13-16 even though code can be developed before their implementation; G1 couples the commercial/settlement/valuation fixes. No unnecessary dependency or compatibility path remains. Recovery preserves historical facts and pending money. Record remaining blockers; spec completion is not implementation completion or owner production approval.

<a id="task-13"></a>
## Atomic Task 13 - Separate actual settlement, customer credit and payment clearing

### A. Objective

**Priority:** P0. **Requirements:** `R05`, `R06`, `R12`, `R14`. **Findings:** `F14`, `F34`.
**Dependencies:** [Task 03](#task-03), [Task 04](#task-04), [Task 08](#task-08), [Task 10](#task-10), [Task 11](#task-11), [Task 12](#task-12) **Integration gate:** G1.

**Current → target:** Credit and reference-only external methods are marked confirmed, conflating receivables with money collected. → Cash, receivables, advances and pending/cleared external tenders have truthful distinct accounting and state transitions.

**Acceptance:** The target behavior is observable through the owning API/UI/store and every applicable scenario in E passes; release additionally requires the named gate, not just this file diff.

**Non-goals:** No PSP integration in this task. Existing financial classification corrections require external finance review.

### B. Scope

| Action | Exact path | Change / exclusion |
|---|---|---|
| MODIFY | `QtsBilling.Persistence.Postgres/Entities/Sales.cs` | Explicit settlement state/evidence, native credit/open-item references; no fictitious collected tender. |
| MODIFY | `QtsWebApp/QtsWebApp/Features/Sales/SalePosting.cs` | Cash/credit/split settlement posting with correct journal/open item consequences. |
| CREATE — NEW | `QtsWebApp/QtsWebApp/Features/Payments/PaymentSettlement.cs` | Settled vs pending-clearing transition rules shared by customer/supplier flows. |
| GENERATE — NEW | `QtsBilling.Persistence.Postgres/Migrations/` | NEW SettlementAndOpenItemFoundation; append-only settlement/allocation identities and constraints. |
| CREATE — NEW test | `TestQtsBilling/Integration/PaymentTruthTests.cs` | Owning focused assertions below; reuse fixtures rather than duplicate a test infrastructure. |

MODIFY existing `QtsBilling.Persistence.Postgres/BillingDbContext.cs` and the appropriate checked `BillingDbContext.Model.cs` / `BillingDbContext.Model.Financial.cs` only for the listed model; GENERATE designer/snapshot companions with the named new migration.

All other production paths are excluded except the narrow, explicitly enumerated [D17 integration bindings](#d17). Applied migrations, posted facts and unresolved cash are never edited/deleted by this scope.

### C. Verified anchors and NEW insertions

- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Sales/SalePosting.cs — source lines 1-310](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Sales/SalePosting.cs#L1-L310): `ValidatePayload; ResolveContextAsync; PriceLines; ValidatePayment`. [E11](#e-e11).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Sales/SalePosting.cs — source lines 310-665](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Sales/SalePosting.cs#L310-L665): `PostAsync / posting transaction and revenue journal`. [E12](#e-e12).
- **EXISTING / VERIFIED:** [QtsBilling.Persistence.Postgres/BillingDbContext.Model.Financial.cs — source lines 210-445](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Persistence.Postgres/BillingDbContext.Model.Financial.cs#L210-L445): `ConfigureAccounting; ConfigurePurchasing`. [E07](#e-e07).
- **EXISTING / VERIFIED:** [docs/migration/decisions.md — source lines 1-155](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/docs/migration/decisions.md#L1-L155): `D-01...D-41 and historical decisions`. [E44](#e-e44).

All CREATE/GENERATE symbols, routes and constraints in B/D are **NEW**. Source presence is not runtime proof; do not infer executed results from old evidence prose.

### D. Ordered implementation recipe

1. Implement D07/D08: a customer-credit portion creates an AR open item and no confirmed payment; a cash portion creates a settled cash fact only for the acknowledged amount. Validate currency/scale, customer terms/credit authority and total coverage.
2. Define settlement states PendingClearance, Settled, Rejected/Bounced and Reversed with immutable amount/subject and new transition evidence. Cards and unknown enum values remain disabled; a reference alone cannot become verified provider success.
3. For bank transfer, require authorized independent confirmation evidence; for cheque, use clearing account until cleared. Bound operator permissions and preserve uploaded reference/evidence separately from secret/card fields.
4. Create party/open-item links required by Tasks 39 and 45-46. Lock allocations and prevent duplicate settlement/over-allocation; accept overpayment only as an explicit advance.
5. Audit classification/cash custody. Reconcile any current credit-as-confirmed fixture/production rows through approved correcting journals and provenance, not deletion or silent status rewrites of history.

Apply the specific D01–D17 policies relevant to this slice. Finish the narrow route/model/client registration in B, record generated migration paths, and preserve a safe stop/fix-forward path before presenting the diff for review.

### E. Focused tests

**NEW file:** `TestQtsBilling/Integration/PaymentTruthTests.cs`. Name the class `PaymentTruthTests` in the directory-derived namespace specified in §6.3.

- **T13.01:** Given a credit sale, when the task-specific command or transition is exercised, then AR increases and no confirmed cash/bank payment is created.
- **T13.02:** Given mixed cash and credit, when the task-specific command or transition is exercised, then actual cash + open receivable equals payable and the journal balances.
- **T13.03:** Given a cheque reference only, when the task-specific command or transition is exercised, then bank cash remains unchanged until authorized clearance; bounce produces a linked reversal.
- **T13.04:** Given an arbitrary numeric payment enum or card attempt, when the task-specific command or transition is exercised, then the command is rejected.
- **T13.05:** Given concurrent allocation/clearance or replay, when the task-specific command or transition is exercised, then the same settlement cannot be counted twice.

Reuse existing or earlier-task fixtures. For a mutation, also assert denied tenant/location/permission and injected pre-commit failure leave zero partial financial/audit effects; for a read/UI-only task assert no mutation. Reuse the same fixture rather than duplicating equivalent tests.

### F. Verification

**Status: NOT RUN - SPEC_ONLY.** The following filter becomes executable only after the NEW test class and prerequisites are implemented.

Requires Task 01's isolated PostgreSQL 18/API fixture, .NET 10 SDK and explicitly validated QTS_TEST_POSTGRES_CONNECTION. Fixture DDL/data writes are permitted only in a later authorized test run; never inherit production credentials.

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~TestQtsBilling.Integration.PaymentTruthTests"
```

**PASS:** owning cases and relevant existing regression filters pass, with exact committed-state assertions where applicable; no skipped required prerequisite, new unreviewed dependency, secret leak or `git diff --check` failure. A build or mocked response alone is not financial proof.

Select the checked server or Windows build command in §6.3 only when this task changes that host. Full multi-platform regression belongs in CI/release, not every local edit.

### G. Definition of done

Cash, receivables, advances and pending/cleared external tenders have truthful distinct accounting and state transitions. Evidence includes the exact implementation SHA, focused test outcome and minimal reviewed diff. All new constraints/contracts/permissions/host wiring ship together under **G1**; unresolved external prerequisites stay explicit.

No unnecessary dependency or compatibility path remains. Recovery preserves historical facts and pending money. Record remaining blockers; spec completion is not implementation completion or owner production approval.

<a id="task-14"></a>
## Atomic Task 14 - Make stock balance creation and movement posting race-safe

### A. Objective

**Priority:** P0. **Requirements:** `R07`, `R08`. **Findings:** `F17`, `F18`, `F21`, `F36`.
**Dependencies:** [Task 05](#task-05), [Task 09](#task-09), [Task 10](#task-10), [Task 11](#task-11) **Integration gate:** G1.

**Current → target:** Missing-balance rows are created in tracked state and can race; generic movement types expose business effects without their complete workflow. → Every stock mutation uses one concurrency-safe path and cannot oversell, create duplicate balances or bypass the intended sales/purchase/transfer process.

**Acceptance:** The target behavior is observable through the owning API/UI/store and every applicable scenario in E passes; release additionally requires the named gate, not just this file diff.

**Non-goals:** No table-level lock or dropping the on-hand guard to hide a conflict.

### B. Scope

| Action | Exact path | Change / exclusion |
|---|---|---|
| MODIFY | `QtsWebApp/QtsWebApp/Features/Inventory/InventoryLedger.cs` | Scoped balance initialization, lock order and exact final quantity. |
| MODIFY | `QtsWebApp/QtsWebApp/Features/Inventory/InventoryEndpoints.cs` | Native movement allowlist, resource validation and no negative override. |
| GENERATE — NEW | `QtsBilling.Persistence.Postgres/Migrations/` | NEW stock consistency/index amendments only where required beyond Task 09. |
| CREATE — NEW test | `TestQtsBilling/Integration/InventoryConcurrencyTests.cs` | Owning focused assertions below; reuse fixtures rather than duplicate a test infrastructure. |

MODIFY existing `QtsWebApp/QtsWebApp/Program.cs` only for required route/service registration, and `QtsBilling.Contracts/Identity/IdentityDtos.cs` / `QtsWebApp/QtsWebApp/Infrastructure/QtsIdentity.cs` only for this task's explicit D02 policies.

All other production paths are excluded except the narrow, explicitly enumerated [D17 integration bindings](#d17). Applied migrations, posted facts and unresolved cash are never edited/deleted by this scope.

### C. Verified anchors and NEW insertions

- **EXISTING / VERIFIED:** [QtsBilling.Persistence.Postgres/BillingDbContext.Model.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Persistence.Postgres/BillingDbContext.Model.cs): `ConfigureCore; ConfigureMasterData; ConfigureCatalog; ConfigureInventory`. [E06](#e-e06).
- **EXISTING / VERIFIED:** [QtsBilling.Persistence.Postgres/Migrations/20261019082426_AddIntegrityGuards.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Persistence.Postgres/Migrations/20261019082426_AddIntegrityGuards.cs): `qts_invoice_lines_draft_guard; qts_inventory_balance_guard; deferred balance triggers`. [E09](#e-e09).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Inventory/InventoryLedger.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Inventory/InventoryLedger.cs): `ApplyAsync`. [E13](#e-e13).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Sync/SyncTransaction.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Sync/SyncTransaction.cs): `RunAsync`. [E14](#e-e14).

All CREATE/GENERATE symbols, routes and constraints in B/D are **NEW**. Source presence is not runtime proof; do not infer executed results from old evidence prose.

### D. Ordered implementation recipe

1. Create a balance with INSERT ON CONFLICT DO NOTHING against the composite unique key, then SELECT FOR UPDATE, or lock a pre-existing stable anchor before creation. Normalize same-key request lines and reuse tracked rows deliberately.
2. Acquire all affected inventory locks in D04 order; lock the business feed revision before later domain locks when projections are required. Retry bounded serialization/deadlock failures only through Task 11.
3. Reject negative final on-hand on every path. Remove allowNegative as an accepted contract/permission escape; use a supervised discrepancy workflow rather than invalid stock.
4. Restrict generic inventory API to approved opening/count adjustments. Sales, purchase receipts/returns and transfers may not be forged by submitting their enum to the generic endpoint; their owning workflows call InventoryLedger internally.
5. Validate active product/variant/location, quantity scale, base-unit conversion and reference scope. Final-state DB equality, movement uniqueness and audit all remain in the same transaction.

Apply the specific D01–D17 policies relevant to this slice. Finish the narrow route/model/client registration in B, record generated migration paths, and preserve a safe stop/fix-forward path before presenting the diff for review.

### E. Focused tests

**NEW file:** `TestQtsBilling/Integration/InventoryConcurrencyTests.cs`. Name the class `InventoryConcurrencyTests` in the directory-derived namespace specified in §6.3.

- **T14.01:** Given two concurrent first receipts for a previously absent balance, when the task-specific command or transition is exercised, then exactly one balance exists with the sum of both quantities.
- **T14.02:** Given two terminals buying the last unit, when the task-specific command or transition is exercised, then only one succeeds and quantity never becomes negative.
- **T14.03:** Given repeated variant lines in one request and an injected rollback, when the task-specific command or transition is exercised, then no duplicate balance or partial movement survives.
- **T14.04:** Given generic stock API with Sale/Transfer/PurchaseReceipt or allowNegative, when the task-specific command or transition is exercised, then it refuses the workflow bypass.
- **T14.05:** Given two-key transfers in opposite directions under concurrency, when the task-specific command or transition is exercised, then ordered locking prevents an unbounded deadlock/retry loop.

Reuse existing or earlier-task fixtures. For a mutation, also assert denied tenant/location/permission and injected pre-commit failure leave zero partial financial/audit effects; for a read/UI-only task assert no mutation. Reuse the same fixture rather than duplicating equivalent tests.

### F. Verification

**Status: NOT RUN - SPEC_ONLY.** The following filter becomes executable only after the NEW test class and prerequisites are implemented.

Requires Task 01's isolated PostgreSQL 18/API fixture, .NET 10 SDK and explicitly validated QTS_TEST_POSTGRES_CONNECTION. Fixture DDL/data writes are permitted only in a later authorized test run; never inherit production credentials.

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~TestQtsBilling.Integration.InventoryConcurrencyTests"
```

**PASS:** owning cases and relevant existing regression filters pass, with exact committed-state assertions where applicable; no skipped required prerequisite, new unreviewed dependency, secret leak or `git diff --check` failure. A build or mocked response alone is not financial proof.

Select the checked server or Windows build command in §6.3 only when this task changes that host. Full multi-platform regression belongs in CI/release, not every local edit.

### G. Definition of done

Every stock mutation uses one concurrency-safe path and cannot oversell, create duplicate balances or bypass the intended sales/purchase/transfer process. Evidence includes the exact implementation SHA, focused test outcome and minimal reviewed diff. All new constraints/contracts/permissions/host wiring ship together under **G1**; unresolved external prerequisites stay explicit.

No unnecessary dependency or compatibility path remains. Recovery preserves historical facts and pending money. Record remaining blockers; spec completion is not implementation completion or owner production approval.

<a id="task-15"></a>
## Atomic Task 15 - Introduce real perpetual stock valuation and COGS

### A. Objective

**Priority:** P0. **Requirements:** `R06`, `R07`, `R15`. **Findings:** `F15`, `F16`, `F38`.
**Dependencies:** [Task 03](#task-03), [Task 08](#task-08), [Task 10](#task-10), [Task 11](#task-11), [Task 14](#task-14) **Integration gate:** G1.

**Current → target:** Sales use mutable catalog purchase price or zero for COGS; stock value cannot reliably reconcile to the general ledger. → Every tracked-stock movement carries an exact cost value based on posted acquisition history and ties to inventory/COGS accounts.

**Acceptance:** The target behavior is observable through the owning API/UI/store and every applicable scenario in E passes; release additionally requires the named gate, not just this file diff.

**Non-goals:** No FIFO/LIFO/lot engine or retrospective revaluation. Finance-approved opening valuation is BLOCKED_DECISION where current data lacks cost evidence.

### B. Scope

| Action | Exact path | Change / exclusion |
|---|---|---|
| MODIFY | `QtsBilling.Persistence.Postgres/Entities/Inventory.cs` | ValueOnHand, authoritative ValueDelta and informative AverageUnitCost. |
| CREATE — NEW | `QtsWebApp/QtsWebApp/Features/Inventory/InventoryValuation.cs` | D06 moving-average receipt/issue/return/transfer value rules. |
| MODIFY | `QtsWebApp/QtsWebApp/Features/Sales/SalePosting.cs` | Consume valuation result for original cost snapshots and journal values. |
| GENERATE — NEW | `QtsBilling.Persistence.Postgres/Migrations/` | NEW PerpetualInventoryValue with value/quantity invariants and model precision. |
| CREATE — NEW test | `TestQtsBilling/Integration/InventoryValuationTests.cs` | Owning focused assertions below; reuse fixtures rather than duplicate a test infrastructure. |

MODIFY existing `QtsBilling.Persistence.Postgres/BillingDbContext.cs` and the appropriate checked `BillingDbContext.Model.cs` / `BillingDbContext.Model.Financial.cs` only for the listed model; GENERATE designer/snapshot companions with the named new migration.

All other production paths are excluded except the narrow, explicitly enumerated [D17 integration bindings](#d17). Applied migrations, posted facts and unresolved cash are never edited/deleted by this scope.

### C. Verified anchors and NEW insertions

- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Sales/SalePosting.cs — source lines 1-310](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Sales/SalePosting.cs#L1-L310): `ValidatePayload; ResolveContextAsync; PriceLines; ValidatePayment`. [E11](#e-e11).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Sales/SalePosting.cs — source lines 310-665](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Sales/SalePosting.cs#L310-L665): `PostAsync / posting transaction and revenue journal`. [E12](#e-e12).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Inventory/InventoryLedger.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Inventory/InventoryLedger.cs): `ApplyAsync`. [E13](#e-e13).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Purchasing/PurchasingEndpoints.cs — source lines 1020-1170](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Purchasing/PurchasingEndpoints.cs#L1020-L1170): `PostReceiptAsync commit path`. [E30](#e-e30).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Reporting/ReportEndpoints.cs — source lines 240-550](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Reporting/ReportEndpoints.cs#L240-L550): `StockSummaryAsync; SalesSummaryAsync`. [E35](#e-e35).

All CREATE/GENERATE symbols, routes and constraints in B/D are **NEW**. Source presence is not runtime proof; do not infer executed results from old evidence prose.

### D. Ordered implementation recipe

1. Implement D06 moving weighted average only. Add quantity/value running balance and movement ValueDelta; informative average/unit cost uses eight decimals but authoritative posted values remain four.
2. Under the same stock lock, receipts add approved net value; issues allocate value at current average and final depletion consumes the exact remaining value. Never permit negative residual value or zero quantity with stranded value.
3. Returns use capped original allocated cost; transfers preserve source value; counts/variance use an explicit gain/loss mapping. Procurement price/consideration differences post to the configured variance account, not catalogue-price edits.
4. Replace COGS computation in SalePosting with the actual returned issue value, storing the original allocation on each sale line. Prevent catalog purchase-price edits from changing already posted values.
5. Add a native valuation opening preview/reconciliation. If current records lack valid valuation, mark them blocked for inventory-finance activation and require approved openings; do not infer a cost method or synthesize balancing data.

Apply the specific D01–D17 policies relevant to this slice. Finish the narrow route/model/client registration in B, record generated migration paths, and preserve a safe stop/fix-forward path before presenting the diff for review.

### E. Focused tests

**NEW file:** `TestQtsBilling/Integration/InventoryValuationTests.cs`. Name the class `InventoryValuationTests` in the directory-derived namespace specified in §6.3.

- **T15.01:** Given 10 units at 10 and 10 units at 20, when 5 are issued, then cost is 75 and remaining value 225.
- **T15.02:** Given fractional issues and final depletion, when the task-specific command or transition is exercised, then quantity zero has value zero exactly without lost rounding residual.
- **T15.03:** Given a customer return after another purchase changes average, when the task-specific command or transition is exercised, then restock uses original sale cost, not current catalog price.
- **T15.04:** Given a warehouse transfer, when the task-specific command or transition is exercised, then combined quantity/value and company profit are unchanged.
- **T15.05:** Given unvalued tracked stock or an unsupported configured method, when the task-specific command or transition is exercised, then issue is blocked with an actionable finance error.

Reuse existing or earlier-task fixtures. For a mutation, also assert denied tenant/location/permission and injected pre-commit failure leave zero partial financial/audit effects; for a read/UI-only task assert no mutation. Reuse the same fixture rather than duplicating equivalent tests.

### F. Verification

**Status: NOT RUN - SPEC_ONLY.** The following filter becomes executable only after the NEW test class and prerequisites are implemented.

Requires Task 01's isolated PostgreSQL 18/API fixture, .NET 10 SDK and explicitly validated QTS_TEST_POSTGRES_CONNECTION. Fixture DDL/data writes are permitted only in a later authorized test run; never inherit production credentials.

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~TestQtsBilling.Integration.InventoryValuationTests"
```

**PASS:** owning cases and relevant existing regression filters pass, with exact committed-state assertions where applicable; no skipped required prerequisite, new unreviewed dependency, secret leak or `git diff --check` failure. A build or mocked response alone is not financial proof.

Select the checked server or Windows build command in §6.3 only when this task changes that host. Full multi-platform regression belongs in CI/release, not every local edit.

### G. Definition of done

Every tracked-stock movement carries an exact cost value based on posted acquisition history and ties to inventory/COGS accounts. Evidence includes the exact implementation SHA, focused test outcome and minimal reviewed diff. All new constraints/contracts/permissions/host wiring ship together under **G1**; unresolved external prerequisites stay explicit.

No unnecessary dependency or compatibility path remains. Recovery preserves historical facts and pending money. Record remaining blockers; spec completion is not implementation completion or owner production approval.

<a id="task-16"></a>
## Atomic Task 16 - Post goods receipts and returns to inventory and clearing accounts atomically

### A. Objective

**Priority:** P0. **Requirements:** `R06`, `R07`, `R08`, `R13`. **Findings:** `F16`, `F21`.
**Dependencies:** [Task 10](#task-10), [Task 11](#task-11), [Task 14](#task-14), [Task 15](#task-15) **Integration gate:** G1 + G6.

**Current → target:** Goods documents update stock but do not recognize its value in inventory/GRNI; route-kind is absent from the replay fingerprint. → Each accepted GRN/return has one stock/value effect, one appropriate balanced clearing journal, one audit/receipt and correct source-line caps.

**Acceptance:** The target behavior is observable through the owning API/UI/store and every applicable scenario in E passes; release additionally requires the named gate, not just this file diff.

**Non-goals:** No unapproved landed-cost allocation or foreign-currency purchase recognition; Tasks 45-46 complete AP.

### B. Scope

| Action | Exact path | Change / exclusion |
|---|---|---|
| MODIFY | `QtsWebApp/QtsWebApp/Features/Purchasing/PurchasingEndpoints.cs` | GRN/return journals, scoped replay and receipt-line return linkage. |
| MODIFY | `QtsBilling.Persistence.Postgres/Entities/Purchasing.cs` | Journal linkage and original receipt/return quantity/value evidence. |
| MODIFY | `QtsBilling.Contracts/Purchasing/PurchasingDtos.cs` | Native receipt/return reference and valuation result fields. |
| GENERATE — NEW | `QtsBilling.Persistence.Postgres/Migrations/` | NEW PurchasingReceiptAccounting and return caps/model companions. |
| CREATE — NEW test | `TestQtsBilling/Integration/PurchasingReceiptPostingTests.cs` | Owning focused assertions below; reuse fixtures rather than duplicate a test infrastructure. |

MODIFY existing `QtsBilling.Persistence.Postgres/BillingDbContext.cs` and the appropriate checked `BillingDbContext.Model.cs` / `BillingDbContext.Model.Financial.cs` only for the listed model; GENERATE designer/snapshot companions with the named new migration.

MODIFY existing `QtsWebApp/QtsWebApp/Program.cs` only for required route/service registration, and `QtsBilling.Contracts/Identity/IdentityDtos.cs` / `QtsWebApp/QtsWebApp/Infrastructure/QtsIdentity.cs` only for this task's explicit D02 policies.

All other production paths are excluded except the narrow, explicitly enumerated [D17 integration bindings](#d17). Applied migrations, posted facts and unresolved cash are never edited/deleted by this scope.

### C. Verified anchors and NEW insertions

- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Purchasing/PurchasingEndpoints.cs — source lines 1-120](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Purchasing/PurchasingEndpoints.cs#L1-L120): `MapPurchasingEndpoints`. [E28](#e-e28).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Purchasing/PurchasingEndpoints.cs — source lines 640-1030](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Purchasing/PurchasingEndpoints.cs#L640-L1030): `ConfirmOrderAsync; CancelOrderAsync; PostReceiptAsync`. [E29](#e-e29).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Purchasing/PurchasingEndpoints.cs — source lines 1020-1170](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Purchasing/PurchasingEndpoints.cs#L1020-L1170): `PostReceiptAsync commit path`. [E30](#e-e30).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Accounting/LedgerWriter.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Accounting/LedgerWriter.cs): `PostWithinTransactionAsync; AllocateNumberAsync; LocalDate`. [E10](#e-e10).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Inventory/InventoryLedger.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Inventory/InventoryLedger.cs): `ApplyAsync`. [E13](#e-e13).

All CREATE/GENERATE symbols, routes and constraints in B/D are **NEW**. Source presence is not runtime proof; do not infer executed results from old evidence prose.

### D. Ordered implementation recipe

1. Keep PO placement non-financial. At GRN commit, lock the order/receipt lines and stock keys, value the receipt, and post DR Inventory / CR GRNI using configured accounts; non-stock service acceptance follows the approved service-expense clearing rule.
2. Return requests reference original receipt line IDs, not just PO lines. Cap returned quantity/value by original receipt less previous returns and current on-hand; distinguish unbilled GRNI reversal from billed supplier-return clearing per D08.
3. Include operation kind/order/source receipt/location in the Task 11 fingerprint and receipt. A same key on another route or order conflicts; race winner must match. Store JournalEntryId and immutable movement/line references.
4. Do not automatically reopen ordered quantities when goods are returned. Track cumulative receipts and returns separately; replacement supply requires an explicit amendment/new order.
5. One transaction commits receipt header/lines, stock quantity/value, journal, received/returned projections, audit and replay result. Any failure rolls everything back. Bulk-load receipt detail rows to remove per-receipt N+1 queries where this route lists history.

Apply the specific D01–D17 policies relevant to this slice. Finish the narrow route/model/client registration in B, record generated migration paths, and preserve a safe stop/fix-forward path before presenting the diff for review.

### E. Focused tests

**NEW file:** `TestQtsBilling/Integration/PurchasingReceiptPostingTests.cs`. Name the class `PurchasingReceiptPostingTests` in the directory-derived namespace specified in §6.3.

- **T16.01:** Given a PO alone, when the task-specific command or transition is exercised, then neither stock nor ledger moves; given GRN, then stock value equals the inventory debit and GRNI credit.
- **T16.02:** Given same-key replay or receipt-vs-return substitution, when the task-specific command or transition is exercised, then one original result or a scoped conflict occurs, never another stock effect.
- **T16.03:** Given a partial return, when the task-specific command or transition is exercised, then quantity/cost cannot exceed the original unreturned receipt and correct clearing/variance journals are produced.
- **T16.04:** Given an accounting-period closure or failure after stock mutation, when the task-specific command or transition is exercised, then no partial receipt or movement survives.
- **T16.05:** Given a receipt later invoiced by a supplier, when the task-specific command or transition is exercised, then the bill clears GRNI without adding stock again.

Reuse existing or earlier-task fixtures. For a mutation, also assert denied tenant/location/permission and injected pre-commit failure leave zero partial financial/audit effects; for a read/UI-only task assert no mutation. Reuse the same fixture rather than duplicating equivalent tests.

### F. Verification

**Status: NOT RUN - SPEC_ONLY.** The following filter becomes executable only after the NEW test class and prerequisites are implemented.

Requires Task 01's isolated PostgreSQL 18/API fixture, .NET 10 SDK and explicitly validated QTS_TEST_POSTGRES_CONNECTION. Fixture DDL/data writes are permitted only in a later authorized test run; never inherit production credentials.

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~TestQtsBilling.Integration.PurchasingReceiptPostingTests"
```

**PASS:** owning cases and relevant existing regression filters pass, with exact committed-state assertions where applicable; no skipped required prerequisite, new unreviewed dependency, secret leak or `git diff --check` failure. A build or mocked response alone is not financial proof.

Select the checked server or Windows build command in §6.3 only when this task changes that host. Full multi-platform regression belongs in CI/release, not every local edit.

### G. Definition of done

Each accepted GRN/return has one stock/value effect, one appropriate balanced clearing journal, one audit/receipt and correct source-line caps. Evidence includes the exact implementation SHA, focused test outcome and minimal reviewed diff. All new constraints/contracts/permissions/host wiring ship together under **G1 + G6**; unresolved external prerequisites stay explicit.

No unnecessary dependency or compatibility path remains. Recovery preserves historical facts and pending money. Record remaining blockers; spec completion is not implementation completion or owner production approval.

<a id="task-17"></a>
## Atomic Task 17 - Reconcile cash-drawer custody with the general ledger

### A. Objective

**Priority:** P0. **Requirements:** `R06`, `R08`, `R09`. **Findings:** `F28`, `F31`.
**Dependencies:** [Task 10](#task-10), [Task 11](#task-11), [Task 13](#task-13) **Integration gate:** G1 + G3.

**Current → target:** Drawer paid-in/out records are independent of ledger entries and have no replay receipt; rejected device cash is outside the pending total. → Drawer movements, ledger transfers, physical counts and unresolved device custody remain individually traceable without duplicated cash.

**Acceptance:** The target behavior is observable through the owning API/UI/store and every applicable scenario in E passes; release additionally requires the named gate, not just this file diff.

**Non-goals:** No cash disappearance through queue deletion, silent variance write-off or automatic reassignment to the next session.

### B. Scope

| Action | Exact path | Change / exclusion |
|---|---|---|
| MODIFY | `QtsWebApp/QtsWebApp/Features/Sales/CashRegisterEndpoints.cs` | Idempotent drawer operations, funding references and close reconciliation. |
| MODIFY | `QtsBilling.Contracts/Sales/PosCashDtos.cs` | Counter-account, custody, expected version and reconciliation status contracts. |
| MODIFY | `QtsBilling.Persistence.Postgres/Entities/Sales.cs` | Movement source/journal links and closing reconciliation state. |
| GENERATE — NEW | `QtsBilling.Persistence.Postgres/Migrations/` | NEW DrawerCustodyControls and scoped command receipts. |
| CREATE — NEW test | `TestQtsBilling/Integration/DrawerCustodyTests.cs` | Owning focused assertions below; reuse fixtures rather than duplicate a test infrastructure. |

MODIFY existing `QtsBilling.Persistence.Postgres/BillingDbContext.cs` and the appropriate checked `BillingDbContext.Model.cs` / `BillingDbContext.Model.Financial.cs` only for the listed model; GENERATE designer/snapshot companions with the named new migration.

MODIFY existing `QtsWebApp/QtsWebApp/Program.cs` only for required route/service registration, and `QtsBilling.Contracts/Identity/IdentityDtos.cs` / `QtsWebApp/QtsWebApp/Infrastructure/QtsIdentity.cs` only for this task's explicit D02 policies.

All other production paths are excluded except the narrow, explicitly enumerated [D17 integration bindings](#d17). Applied migrations, posted facts and unresolved cash are never edited/deleted by this scope.

### C. Verified anchors and NEW insertions

- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Sales/CashRegisterEndpoints.cs — source lines 409-550](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Sales/CashRegisterEndpoints.cs#L409-L550): `AddMovementAsync`. [E33](#e-e33).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Sales/SalePosting.cs — source lines 310-665](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Sales/SalePosting.cs#L310-L665): `PostAsync / posting transaction and revenue journal`. [E12](#e-e12).
- **EXISTING / VERIFIED:** [QtsBilling.Persistence.Sqlite/SqliteLocalStore.cs — source lines 340-650](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Persistence.Sqlite/SqliteLocalStore.cs#L340-L650): `GetDueOutboxAsync; MarkOutboxSendingAsync; ApplyReceiptAsync; GetPendingSummaryAsync`. [E21](#e-e21).

All CREATE/GENERATE symbols, routes and constraints in B/D are **NEW**. Source presence is not runtime proof; do not infer executed results from old evidence prose.

### D. Ordered implementation recipe

1. Implement D07: opening float is a referenced transfer from an approved cash/bank account or a carry-forward already recognized in the ledger; never recognize revenue or opening cash twice.
2. Paid-in/out requires a controlled reason, permitted counter-account and Idempotency-Key. In one transaction lock the session, write balanced journal, drawer movement, audit and command receipt. Invoice collections, expenses and refunds link their existing settlement instead of posting a second journal.
3. Close records counted cash and derived expected cash separately. Do not post a variance automatically; a manager approves a linked adjustment with reason, amount and opposing account. Reclosing returns the same receipt or a version conflict.
4. Use Open → Closing/AwaitingReconciliation → Closed. A live offline grant or unresolved pending/rejected cash prevents final reconciliation; a known device cannot silently post into another session after reconnect. A supervised late receipt is a new reconciliation event against original custody.
5. Distinguish central settled cash, pending device cash and rejected-held cash in summaries. Update API and daily-close checks; no client-supplied expected balance is authoritative.

Apply the specific D01–D17 policies relevant to this slice. Finish the narrow route/model/client registration in B, record generated migration paths, and preserve a safe stop/fix-forward path before presenting the diff for review.

### E. Focused tests

**NEW file:** `TestQtsBilling/Integration/DrawerCustodyTests.cs`. Name the class `DrawerCustodyTests` in the directory-derived namespace specified in §6.3.

- **T17.01:** Given funding from safe cash to a drawer, when the task-specific command or transition is exercised, then company cash is unchanged and both custody accounts reconcile.
- **T17.02:** Given double-click paid-out or concurrent close, when the task-specific command or transition is exercised, then one movement/journal occurs or the closed-session command is refused.
- **T17.03:** Given a count variance, when the task-specific command or transition is exercised, then the count is stored but the ledger changes only through separately authorized adjustment.
- **T17.04:** Given a device with collected cash not yet accepted, when the task-specific command or transition is exercised, then final close is AwaitingReconciliation and exposes its amount.
- **T17.05:** Given a late accepted offline operation, when the task-specific command or transition is exercised, then original-session reconciliation is updated with an audit record without rewriting an old count.

Reuse existing or earlier-task fixtures. For a mutation, also assert denied tenant/location/permission and injected pre-commit failure leave zero partial financial/audit effects; for a read/UI-only task assert no mutation. Reuse the same fixture rather than duplicating equivalent tests.

### F. Verification

**Status: NOT RUN - SPEC_ONLY.** The following filter becomes executable only after the NEW test class and prerequisites are implemented.

Requires Task 01's isolated PostgreSQL 18/API fixture, .NET 10 SDK and explicitly validated QTS_TEST_POSTGRES_CONNECTION. Fixture DDL/data writes are permitted only in a later authorized test run; never inherit production credentials.

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~TestQtsBilling.Integration.DrawerCustodyTests"
```

**PASS:** owning cases and relevant existing regression filters pass, with exact committed-state assertions where applicable; no skipped required prerequisite, new unreviewed dependency, secret leak or `git diff --check` failure. A build or mocked response alone is not financial proof.

Select the checked server or Windows build command in §6.3 only when this task changes that host. Full multi-platform regression belongs in CI/release, not every local edit.

### G. Definition of done

Drawer movements, ledger transfers, physical counts and unresolved device custody remain individually traceable without duplicated cash. Evidence includes the exact implementation SHA, focused test outcome and minimal reviewed diff. All new constraints/contracts/permissions/host wiring ship together under **G1 + G3**; unresolved external prerequisites stay explicit.

No unnecessary dependency or compatibility path remains. Recovery preserves historical facts and pending money. Record remaining blockers; spec completion is not implementation completion or owner production approval.

<a id="task-18"></a>
## Atomic Task 18 - Validate a provider-neutral native onboarding package before reading business rows

### A. Objective

**Priority:** P0. **Requirements:** `R01`, `R16`. **Findings:** `F03`, `F05`, `F42`.
**Dependencies:** [Task 01](#task-01), [Task 02](#task-02), [Task 03](#task-03), [Task 05](#task-05) **Integration gate:** G4.

**Current → target:** The retired snapshot reader trusts manifest paths and rereads files; import can bypass snapshot verification. → A native onboarding package is authenticated, complete, bounded and immutably bound to a target business before transformation or writes.

**Acceptance:** The target behavior is observable through the owning API/UI/store and every applicable scenario in E passes; release additionally requires the named gate, not just this file diff.

**Non-goals:** No old snapshot decoder, provider package, database connection to a source or automatic best-effort import.

### B. Scope

| Action | Exact path | Change / exclusion |
|---|---|---|
| CREATE — NEW | `QtsBilling.DataImport/NativePackageReader.cs` | Native manifest, bounded verified streams and sensitive-field rejection. |
| CREATE — NEW | `QtsBilling.DataImport/NativeImportContracts.cs` | Versioned native resource schemas, external IDs, hashes and approved business binding. |
| CREATE — NEW | `QtsBilling.DataImport/Program.cs` | Validate/preview/apply/reconcile commands; apply disabled unless explicit native admission succeeds. |
| CREATE — NEW | `QtsBilling.DataImport/QtsBilling.DataImport.csproj` | net10.0 executable; Contracts/QtsModels/Postgres references only, no source-provider dependency. |
| MODIFY | `QtsBilling.sln` | Add native DataImport; final removal of source-format projects is coupled with Tasks 02 and 21. |
| CREATE — NEW test | `TestQtsBilling/DataImport/NativePackageTests.cs` | Owning focused assertions below; reuse fixtures rather than duplicate a test infrastructure. |

MODIFY the NEW `QtsBilling.DataImport/QtsBilling.DataImport.csproj` from Task 18 only for explicit native dependencies; update `TestQtsBilling/TestQtsBilling.csproj` with the native tool project reference for its focused tests.

All other production paths are excluded except the narrow, explicitly enumerated [D17 integration bindings](#d17). Applied migrations, posted facts and unresolved cash are never edited/deleted by this scope.

### C. Verified anchors and NEW insertions

- **EXISTING / VERIFIED:** [QtsBilling.Migration/SnapshotSource.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Migration/SnapshotSource.cs): `VerifyAsync; ReadDocumentsAsync; InventoryAsync`. [E36](#e-e36).
- **EXISTING / VERIFIED:** [QtsBilling.Migration/Program.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Migration/Program.cs): `ImportAsync; ReconcileAsync; TransformAsync`. [E37](#e-e37).
- **EXISTING / VERIFIED:** [QtsBilling.Migration/ReconciliationReport.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Migration/ReconciliationReport.cs): `BuildBody; Sign; Compare`. [E39](#e-e39).

All CREATE/GENERATE symbols, routes and constraints in B/D are **NEW**. Source presence is not runtime proof; do not infer executed results from old evidence prose.

### D. Ordered implementation recipe

1. Use D13 native schema only: manifest schemaVersion, batchId, targetBusinessId, effectiveBusinessDate and entries with relative path/resource/count/byteLength/SHA-256. Accept approved native DTOs, not source database collections or arbitrary target tables.
2. Resolve paths under the package root; reject absolute paths, traversal, duplicate/case-colliding paths, links/reparse escapes and oversized/deep JSON. Every listed resource must exist, every supplied business file must be declared, and manifest counts must equal parsed native rows.
3. Validate signature/approval against an independently configured public trust or HMAC key before content processing. Hash and parse the same immutable staged bytes, or reverify immediately before the verified stream is consumed; do not trust a second unverified read.
4. Recursively reject credential, private-key, token and prohibited payment data fields using schema allowlists, including nested arrays. Quarantine only non-sensitive reason/source identity; do not echo rejected payloads or secrets.
5. Validation/preview never writes PostgreSQL. Emit a manifest-bound report with all rejected inputs, no silent skipping; a missing/altered resource stops apply. The package generator and docs use neutral native names.

Apply the specific D01–D17 policies relevant to this slice. Finish the narrow route/model/client registration in B, record generated migration paths, and preserve a safe stop/fix-forward path before presenting the diff for review.

### E. Focused tests

**NEW file:** `TestQtsBilling/DataImport/NativePackageTests.cs`. Name the class `NativePackageTests` in the directory-derived namespace specified in §6.3.

- **T18.01:** Given a missing file, byte/hash/count mismatch or undeclared file, when the task-specific command or transition is exercised, then validation fails before any target write.
- **T18.02:** Given traversal, symlink escape or case-colliding paths, when the task-specific command or transition is exercised, then the reader rejects the package.
- **T18.03:** Given nested credentials or prohibited card fields, when the task-specific command or transition is exercised, then no raw value reaches output/log/quarantine.
- **T18.04:** Given mutation between validation and apply, when the task-specific command or transition is exercised, then the consumed digest mismatch blocks import.
- **T18.05:** Given a valid native customer/product/opening package, when the task-specific command or transition is exercised, then preview identifies the exact resources and target business.

Reuse existing or earlier-task fixtures. For a mutation, also assert denied tenant/location/permission and injected pre-commit failure leave zero partial financial/audit effects; for a read/UI-only task assert no mutation. Reuse the same fixture rather than duplicating equivalent tests.

### F. Verification

**Status: NOT RUN - SPEC_ONLY.** The following filter becomes executable only after the NEW test class and prerequisites are implemented.

Requires .NET 10 SDK and the NEW owning test class. Use pure fixtures/native files; no production service connection.

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~TestQtsBilling.DataImport.NativePackageTests"
```

**PASS:** owning cases and relevant existing regression filters pass, with exact committed-state assertions where applicable; no skipped required prerequisite, new unreviewed dependency, secret leak or `git diff --check` failure. A build or mocked response alone is not financial proof.

Select the checked server or Windows build command in §6.3 only when this task changes that host. Full multi-platform regression belongs in CI/release, not every local edit.

### G. Definition of done

A native onboarding package is authenticated, complete, bounded and immutably bound to a target business before transformation or writes. Evidence includes the exact implementation SHA, focused test outcome and minimal reviewed diff. All new constraints/contracts/permissions/host wiring ship together under **G4**; unresolved external prerequisites stay explicit.

No unnecessary dependency or compatibility path remains. Recovery preserves historical facts and pending money. Record remaining blockers; spec completion is not implementation completion or owner production approval.

<a id="task-19"></a>
## Atomic Task 19 - Apply native masters and approved openings with atomic source identity

### A. Objective

**Priority:** P0. **Requirements:** `R06`, `R07`, `R16`. **Findings:** `F03`, `F04`.
**Dependencies:** [Task 08](#task-08), [Task 09](#task-09), [Task 10](#task-10), [Task 11](#task-11), [Task 15](#task-15), [Task 18](#task-18) **Integration gate:** G4.

**Current → target:** The existing importer upserts raw tables separately from provenance and can overwrite dates/version or native financial state. → Native onboarding is deterministic, tenant-bound and exactly repeatable, without a generic SQL writer or mutation of posted history.

**Acceptance:** The target behavior is observable through the owning API/UI/store and every applicable scenario in E passes; release additionally requires the named gate, not just this file diff.

**Non-goals:** No historical transaction reconstruction without actual immutable native facts; no catch-and-continue that loses a rejection.

### B. Scope

| Action | Exact path | Change / exclusion |
|---|---|---|
| CREATE — NEW | `QtsBilling.DataImport/NativeImportWriter.cs` | Typed resource handlers and transactional source-link verification. |
| CREATE — NEW | `QtsBilling.Persistence.Postgres/Entities/ExternalRecordLink.cs` | Native batch/resource/external identity, immutable payload hash and target linkage. |
| CREATE — NEW | `QtsBilling.DataImport/NativeOpeningWriter.cs` | Approved balanced ledger, party open-item and stock/value openings. |
| GENERATE — NEW | `QtsBilling.Persistence.Postgres/Migrations/` | NEW NativeExternalRecordLinks; copy/reconcile existing provenance only if authorized. |
| CREATE — NEW test | `TestQtsBilling/Integration/NativeImportAtomicityTests.cs` | Owning focused assertions below; reuse fixtures rather than duplicate a test infrastructure. |

MODIFY existing `QtsBilling.Persistence.Postgres/BillingDbContext.cs` and the appropriate checked `BillingDbContext.Model.cs` / `BillingDbContext.Model.Financial.cs` only for the listed model; GENERATE designer/snapshot companions with the named new migration.

MODIFY the NEW `QtsBilling.DataImport/QtsBilling.DataImport.csproj` from Task 18 only for explicit native dependencies; update `TestQtsBilling/TestQtsBilling.csproj` with the native tool project reference for its focused tests.

All other production paths are excluded except the narrow, explicitly enumerated [D17 integration bindings](#d17). Applied migrations, posted facts and unresolved cash are never edited/deleted by this scope.

### C. Verified anchors and NEW insertions

- **EXISTING / VERIFIED:** [QtsBilling.Migration/PostgresImporter.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Migration/PostgresImporter.cs): `ImportAsync; UpsertAsync; WriteIdentifierMapAsync`. [E38](#e-e38).
- **EXISTING / VERIFIED:** [QtsBilling.Persistence.Postgres/BillingDbContext.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Persistence.Postgres/BillingDbContext.cs): `SaveChanges guards and AppendOnlyTypes`. [E08](#e-e08).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Accounting/LedgerWriter.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Accounting/LedgerWriter.cs): `PostWithinTransactionAsync; AllocateNumberAsync; LocalDate`. [E10](#e-e10).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Inventory/InventoryLedger.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Inventory/InventoryLedger.cs): `ApplyAsync`. [E13](#e-e13).

All CREATE/GENERATE symbols, routes and constraints in B/D are **NEW**. Source presence is not runtime proof; do not infer executed results from old evidence prose.

### D. Ordered implementation recipe

1. Implement fixed typed handlers in dependency order: business setup references, masters, approved accounting/party and inventory openings. Only Task 18 verified immutable input is accepted; no arbitrary SQL identifiers or table names from mappings.
2. Within each aggregate transaction, validate all tenant references, write its domain rows and external_record_links unique on business/batch/resource/externalId. Same identity/hash returns its prior target; changed body or inconsistent target is a conflict, never an upsert overwrite.
3. Preserve supplied lawful business dates separately from import timestamps. Never reset existing version/created date or rewrite a posted document. Stable generated target IDs derive from an approved namespace and complete external identity, with collision checks.
4. Opening inventory quantity/value and opening AR/AP balances must reconcile to the balanced opening journal. Use the same domain precision/constraints; do not directly patch running balances. Interdependent opening groups are one transaction or staged then activated atomically.
5. Persist every late database rejection as a bounded non-sensitive quarantine result tied to batch/row, included in reconciliation. Stop on unexpected failures; a restart resumes only verified committed aggregates and cannot leave target-without-provenance rows.

Apply the specific D01–D17 policies relevant to this slice. Finish the narrow route/model/client registration in B, record generated migration paths, and preserve a safe stop/fix-forward path before presenting the diff for review.

### E. Focused tests

**NEW file:** `TestQtsBilling/Integration/NativeImportAtomicityTests.cs`. Name the class `NativeImportAtomicityTests` in the directory-derived namespace specified in §6.3.

- **T19.01:** Given interruption between target and source-link writes, when the task-specific command or transition is exercised, then both roll back or both are committed.
- **T19.02:** Given identical rerun, when the task-specific command or transition is exercised, then target IDs, dates, versions, journal and stock effects remain unchanged.
- **T19.03:** Given changed payload, another business or a native existing record, when the task-specific command or transition is exercised, then apply refuses overwrite.
- **T19.04:** Given balanced party and inventory openings, when the task-specific command or transition is exercised, then AR/AP control accounts and stock value equal their detailed openings.
- **T19.05:** Given a constraint failure midway, when the task-specific command or transition is exercised, then the report records the rejected aggregate and an unexplained count cannot pass reconciliation.

Reuse existing or earlier-task fixtures. For a mutation, also assert denied tenant/location/permission and injected pre-commit failure leave zero partial financial/audit effects; for a read/UI-only task assert no mutation. Reuse the same fixture rather than duplicating equivalent tests.

### F. Verification

**Status: NOT RUN - SPEC_ONLY.** The following filter becomes executable only after the NEW test class and prerequisites are implemented.

Requires Task 01's isolated PostgreSQL 18/API fixture, .NET 10 SDK and explicitly validated QTS_TEST_POSTGRES_CONNECTION. Fixture DDL/data writes are permitted only in a later authorized test run; never inherit production credentials.

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~TestQtsBilling.Integration.NativeImportAtomicityTests"
```

**PASS:** owning cases and relevant existing regression filters pass, with exact committed-state assertions where applicable; no skipped required prerequisite, new unreviewed dependency, secret leak or `git diff --check` failure. A build or mocked response alone is not financial proof.

Select the checked server or Windows build command in §6.3 only when this task changes that host. Full multi-platform regression belongs in CI/release, not every local edit.

### G. Definition of done

Native onboarding is deterministic, tenant-bound and exactly repeatable, without a generic SQL writer or mutation of posted history. Evidence includes the exact implementation SHA, focused test outcome and minimal reviewed diff. All new constraints/contracts/permissions/host wiring ship together under **G4**; unresolved external prerequisites stay explicit.

No unnecessary dependency or compatibility path remains. Recovery preserves historical facts and pending money. Record remaining blockers; spec completion is not implementation completion or owner production approval.

<a id="task-20"></a>
## Atomic Task 20 - Verify reconciliation content, cryptographic authenticity and individual approvals

### A. Objective

**Priority:** P0. **Requirements:** `R06`, `R16`, `R18`. **Findings:** `F03`, `F04`, `F05`.
**Dependencies:** [Task 18](#task-18), [Task 19](#task-19) **Integration gate:** G4.

**Current → target:** The current reconciliation command does not populate financial comparisons and does not verify its output signature; the serialized report timestamp differs from the signed body. → Readiness consumes one verifiable report whose contents, environment and specific allowed exceptions are independently authenticated.

**Acceptance:** The target behavior is observable through the owning API/UI/store and every applicable scenario in E passes; release additionally requires the named gate, not just this file diff.

**Non-goals:** No blanket exception bypass or conflation of a tool-generated signature with owner approval.

### B. Scope

| Action | Exact path | Change / exclusion |
|---|---|---|
| CREATE — NEW | `QtsBilling.DataImport/ReconciliationVerifier.cs` | Canonical report body, signature validation and exact source/target comparisons. |
| CREATE — NEW | `QtsBilling.DataImport/ReconciliationContracts.cs` | Per-resource/aggregate/party/value totals and immutable exception identities. |
| CREATE — NEW | `TestQtsBilling/DataImport/ReconciliationFixtures.cs` | Small deterministic signed and tampered reports for validation tests. |
| CREATE — NEW test | `TestQtsBilling/DataImport/ReconciliationTests.cs` | Owning focused assertions below; reuse fixtures rather than duplicate a test infrastructure. |

MODIFY the NEW `QtsBilling.DataImport/QtsBilling.DataImport.csproj` from Task 18 only for explicit native dependencies; update `TestQtsBilling/TestQtsBilling.csproj` with the native tool project reference for its focused tests.

All other production paths are excluded except the narrow, explicitly enumerated [D17 integration bindings](#d17). Applied migrations, posted facts and unresolved cash are never edited/deleted by this scope.

### C. Verified anchors and NEW insertions

- **EXISTING / VERIFIED:** [QtsBilling.Migration/Program.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Migration/Program.cs): `ImportAsync; ReconcileAsync; TransformAsync`. [E37](#e-e37).
- **EXISTING / VERIFIED:** [QtsBilling.Migration/PostgresImporter.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Migration/PostgresImporter.cs): `ImportAsync; UpsertAsync; WriteIdentifierMapAsync`. [E38](#e-e38).
- **EXISTING / VERIFIED:** [QtsBilling.Migration/ReconciliationReport.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Migration/ReconciliationReport.cs): `BuildBody; Sign; Compare`. [E39](#e-e39).
- **EXISTING / VERIFIED:** [scripts/qb30-cutover.ps1 — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/scripts/qb30-cutover.ps1): `Add-Gate and whole cutover flow`. [E40](#e-e40).

All CREATE/GENERATE symbols, routes and constraints in B/D are **NEW**. Source presence is not runtime proof; do not infer executed results from old evidence prose.

### D. Ordered implementation recipe

1. Build one canonical report body exactly once; use its timestamp and bytes both for persisted payload and digest/signature. Include batch/input/mapping-schema/tool SHA, target fingerprint/business IDs, schema head and immutable result IDs.
2. Compare actual parsed source, accepted native rows, quarantined rows and persisted targets: source=accepted+quarantine; compare identity mappings and amounts/quantities per aggregate, party, account and location, not counts alone. D13 exact scale comparisons apply.
3. Add a verify command that recomputes the digest and HMAC/signature with explicit key ID/trust configuration and constant-time comparison. A hex string is not proof; unsigned output is preview-only and cannot admit production.
4. Approve only enumerated non-financial exceptions by immutable exception ID, reason, approver, timestamp and report digest. Unbalanced journal, missing ownership, lost cash, duplicate financial effect or unverifiable stock valuation is non-waivable.
5. Persist audit and a machine-readable acceptance result. Missing comparisons, unexpected extra rows, signature/key mismatch or a new exception after approval fails closed. Never print signing keys or raw sensitive rejected rows.

Apply the specific D01–D17 policies relevant to this slice. Finish the narrow route/model/client registration in B, record generated migration paths, and preserve a safe stop/fix-forward path before presenting the diff for review.

### E. Focused tests

**NEW file:** `TestQtsBilling/DataImport/ReconciliationTests.cs`. Name the class `ReconciliationTests` in the directory-derived namespace specified in §6.3.

- **T20.01:** Given report body, timestamp, exception or target fingerprint altered after signing, when the task-specific command or transition is exercised, then verification fails.
- **T20.02:** Given equal counts but wrong financial totals or swapped tenant ownership, when the task-specific command or transition is exercised, then reconciliation fails.
- **T20.03:** Given approved exception A and new exception B, when the task-specific command or transition is exercised, then approval does not cover B.
- **T20.04:** Given an unsigned preview or a signature with unknown key ID, when the task-specific command or transition is exercised, then production admission is denied.
- **T20.05:** Given exact authenticated native results, when the task-specific command or transition is exercised, then reproducible verification succeeds without modifying source or target.

Reuse existing or earlier-task fixtures. For a mutation, also assert denied tenant/location/permission and injected pre-commit failure leave zero partial financial/audit effects; for a read/UI-only task assert no mutation. Reuse the same fixture rather than duplicating equivalent tests.

### F. Verification

**Status: NOT RUN - SPEC_ONLY.** The following filter becomes executable only after the NEW test class and prerequisites are implemented.

Requires .NET 10 SDK and the NEW owning test class. Use pure fixtures/native files; no production service connection.

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~TestQtsBilling.DataImport.ReconciliationTests"
```

**PASS:** owning cases and relevant existing regression filters pass, with exact committed-state assertions where applicable; no skipped required prerequisite, new unreviewed dependency, secret leak or `git diff --check` failure. A build or mocked response alone is not financial proof.

Select the checked server or Windows build command in §6.3 only when this task changes that host. Full multi-platform regression belongs in CI/release, not every local edit.

### G. Definition of done

Readiness consumes one verifiable report whose contents, environment and specific allowed exceptions are independently authenticated. Evidence includes the exact implementation SHA, focused test outcome and minimal reviewed diff. All new constraints/contracts/permissions/host wiring ship together under **G4**; unresolved external prerequisites stay explicit.

No unnecessary dependency or compatibility path remains. Recovery preserves historical facts and pending money. Record remaining blockers; spec completion is not implementation completion or owner production approval.

<a id="task-21"></a>
## Atomic Task 21 - Replace the cutover script with fail-closed environment-bound admission

### A. Objective

**Priority:** P0. **Requirements:** `R16`, `R17`, `R18`. **Findings:** `F01`, `F02`, `F03`, `F05`, `F44`.
**Dependencies:** [Task 01](#task-01), [Task 02](#task-02), [Task 07](#task-07), [Task 17](#task-17), [Task 20](#task-20) **Integration gate:** G4 + G7.

**Current → target:** Failed gates are accumulated rather than stopping further steps; expected codeword and evidence formats can be supplied without independent authenticity. → No production mutation can begin after a failed prerequisite or on an environment different from the reviewed approval manifest.

**Acceptance:** The target behavior is observable through the owning API/UI/store and every applicable scenario in E passes; release additionally requires the named gate, not just this file diff.

**Non-goals:** No actual cutover, restore, user reset, device enrolment or data write in this specification. Run harness only with explicit doubles in future tests.

### B. Scope

| Action | Exact path | Change / exclusion |
|---|---|---|
| MODIFY | `scripts/qb30-cutover.ps1` | Read-only admission followed by a separately authorized, explicit execution phase. |
| CREATE — NEW | `scripts/Test-ProductionAdmission.ps1` | Structured checks, target binding, authenticated evidence and failure propagation. |
| CREATE — NEW | `TestQtsBilling/Operations/AdmissionScriptHarness.cs` | Run script functions with controlled external-command doubles; no real production tools. |
| CREATE — NEW test | `TestQtsBilling/Operations/ProductionAdmissionTests.cs` | Owning focused assertions below; reuse fixtures rather than duplicate a test infrastructure. |

All other production paths are excluded except the narrow, explicitly enumerated [D17 integration bindings](#d17). Applied migrations, posted facts and unresolved cash are never edited/deleted by this scope.

### C. Verified anchors and NEW insertions

- **EXISTING / VERIFIED:** [scripts/qb30-cutover.ps1 — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/scripts/qb30-cutover.ps1): `Add-Gate and whole cutover flow`. [E40](#e-e40).
- **EXISTING / VERIFIED:** [.github/workflows/ci.yml — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/.github/workflows/ci.yml): `build-and-test; build-desktop`. [E41](#e-e41).
- **EXISTING / VERIFIED:** [docs/migration/deployment.md — source lines 1-150](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/docs/migration/deployment.md#L1-L150): `Sections 7 and 9`. [E45](#e-e45).

All CREATE/GENERATE symbols, routes and constraints in B/D are **NEW**. Source presence is not runtime proof; do not infer executed results from old evidence prose.

### D. Ordered implementation recipe

1. Separate read-only preflight/admission from authorized apply. Validate the exact server/database identifier, environment, business IDs, release SHA/digests, migration head, native package/report hash, approved exceptions and backup drill against one signed approval manifest.
2. Obtain expected approval from the protected owner-controlled process, not a second caller-supplied comparison value. The actual user-designated confirmation codeword is required for irreversible execution and must never be embedded in source or logs.
3. Make every native command check exit code and stderr appropriately; use explicit terminating errors. Failed, missing, skipped or unknown checks block all subsequent writes. Do not continue with reset, enrolment, import or schema actions after preflight failure.
4. No default local container, test database, automatic sample device or demo user can satisfy production gates. Identity/device readiness is evidence of already approved configuration; never enrol arbitrary devices just to turn a gate green.
5. Financial checks reflect D07/D08: settled money plus open receivables/advances and reversals, not simplistic sales=confirmed payments. Stop/fix-forward retains posted facts; post-first-write rollback requires reconciliation and separate authorization. G7 must also pass before execution is enabled.

Apply the specific D01–D17 policies relevant to this slice. Finish the narrow route/model/client registration in B, record generated migration paths, and preserve a safe stop/fix-forward path before presenting the diff for review.

### E. Focused tests

**NEW file:** `TestQtsBilling/Operations/ProductionAdmissionTests.cs`. Name the class `ProductionAdmissionTests` in the directory-derived namespace specified in §6.3.

- **T21.01:** Given failure at gate 1, when the task-specific command or transition is exercised, then no later mutating external command is invoked.
- **T21.02:** Given user-controlled matching codeword values without trusted approval, when the task-specific command or transition is exercised, then admission fails.
- **T21.03:** Given server A approval and database B command target, when the task-specific command or transition is exercised, then execution is refused.
- **T21.04:** Given nonzero import/backup/verification exit, missing evidence or skipped check, when the task-specific command or transition is exercised, then the script exits nonzero.
- **T21.05:** Given all valid fixture evidence, when the task-specific command or transition is exercised, then read-only preflight yields a bound checklist but does not imply owner production authorization.

Reuse existing or earlier-task fixtures. For a mutation, also assert denied tenant/location/permission and injected pre-commit failure leave zero partial financial/audit effects; for a read/UI-only task assert no mutation. Reuse the same fixture rather than duplicating equivalent tests.

### F. Verification

**Status: NOT RUN - SPEC_ONLY.** The following filter becomes executable only after the NEW test class and prerequisites are implemented.

Use explicit fake process/provider adapters or fixture-only PowerShell 7 harnesses. Never execute production admission/restore/deploy as a test side effect.

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~TestQtsBilling.Operations.ProductionAdmissionTests"
```

**PASS:** owning cases and relevant existing regression filters pass, with exact committed-state assertions where applicable; no skipped required prerequisite, new unreviewed dependency, secret leak or `git diff --check` failure. A build or mocked response alone is not financial proof.

PowerShell 7 is required for the scripted harness; CI must fail rather than silently skip it. Do not execute qb30-cutover.ps1 against a real target during ordinary tests.

Select the checked server or Windows build command in §6.3 only when this task changes that host. Full multi-platform regression belongs in CI/release, not every local edit.

### G. Definition of done

No production mutation can begin after a failed prerequisite or on an environment different from the reviewed approval manifest. Evidence includes the exact implementation SHA, focused test outcome and minimal reviewed diff. All new constraints/contracts/permissions/host wiring ship together under **G4 + G7**; unresolved external prerequisites stay explicit.

No unnecessary dependency or compatibility path remains. Recovery preserves historical facts and pending money. Record remaining blockers; spec completion is not implementation completion or owner production approval.

<a id="task-22"></a>
## Atomic Task 22 - Implement a standards-based native identity endpoint with PKCE

### A. Objective

**Priority:** P0. **Requirements:** `R03`, `R04`, `R10`. **Findings:** `F07`, `F09`, `F10`, `F44`.
**Dependencies:** [Task 04](#task-04), [Task 05](#task-05), [Task 06](#task-06), [Task 07](#task-07) **Integration gate:** G2.

**Current → target:** The Windows app currently copies a cookie-based session; the declared native OIDC/PKCE flow is absent. → The Windows public client uses a verified authorization-code flow without embedded credentials or a client secret.

**Acceptance:** The target behavior is observable through the owning API/UI/store and every applicable scenario in E passes; release additionally requires the named gate, not just this file diff.

**Non-goals:** No external IdP, password-grant shortcut or unrelated authentication framework. Production certificates/issuer configuration are BLOCKED_EXTERNAL.

### B. Scope

| Action | Exact path | Change / exclusion |
|---|---|---|
| CREATE — NEW | `QtsWebApp/QtsWebApp/Features/Auth/NativeAuthorizationEndpoints.cs` | Authorization-code/PKCE, approved native-client registration, revocation and consent boundaries. |
| MODIFY | `QtsWebApp/QtsWebApp/Program.cs` | OpenIddict server/validation registration and distinct cookie/bearer policies. |
| MODIFY | `QtsBilling.Persistence.Postgres/BillingDbContext.cs` | OpenIddict persistence integrated with the existing Identity store. |
| GENERATE — NEW | `QtsBilling.Persistence.Postgres/Migrations/` | NEW NativeAuthorizationStore with application/token/authorization entities. |
| CREATE — NEW test | `TestQtsBilling/Integration/NativeAuthorizationTests.cs` | Owning focused assertions below; reuse fixtures rather than duplicate a test infrastructure. |

MODIFY existing `QtsWebApp/QtsWebApp/Program.cs` only for required route/service registration, and `QtsBilling.Contracts/Identity/IdentityDtos.cs` / `QtsWebApp/QtsWebApp/Infrastructure/QtsIdentity.cs` only for this task's explicit D02 policies.

All other production paths are excluded except the narrow, explicitly enumerated [D17 integration bindings](#d17). Applied migrations, posted facts and unresolved cash are never edited/deleted by this scope.

### C. Verified anchors and NEW insertions

- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Program.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Program.cs): `Identity registration, cookies, API registration, WebLocalPosService`. [E03](#e-e03).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Infrastructure/QtsIdentity.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Infrastructure/QtsIdentity.cs): `PermissionAuthorizationHandler.HandleRequirementAsync; QtsClaimsPrincipalFactory`. [E04](#e-e04).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Auth/AuthEndpoints.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Auth/AuthEndpoints.cs): `LoginAsync; RequestPasswordResetAsync; CompletePasswordResetAsync`. [E05](#e-e05).
- **EXISTING / VERIFIED:** [QtsBilling.Maui/Services/SessionStore.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Maui/Services/SessionStore.cs): `SaveSessionAsync; GetSessionAsync`. [E25](#e-e25).

All CREATE/GENERATE symbols, routes and constraints in B/D are **NEW**. Source presence is not runtime proof; do not infer executed results from old evidence prose.

### D. Ordered implementation recipe

1. Use the documented OpenIddict 7.7.2 server/ASP.NET Core/EF integration; pin the minimal applicable packages in the owning project files. Reuse ASP.NET Core Identity users and trusted enrolment, not another identity system.
2. Expose standard discovery/authorization/token/revocation endpoints and require code + PKCE S256, exact registered redirect URIs, state/nonce and issuer/audience checks. Disable password, implicit and unnecessary grants; do not confuse PKCE with the device-code grant.
3. Issue short-lived native access tokens and rotate/revoke refresh tokens with reuse detection. Validate current user, business membership, scope and device relationship on every sensitive API, not merely token signature.
4. Protect signing/encryption keys externally with explicit key IDs and retained verification keys; refuse Production startup with development keys. Register only the approved native public client and scopes; no wildcard callback or shared secret in MAUI.
5. Keep browser authentication as same-origin secure cookies plus CSRF protections. Scheme selection is explicit per endpoint; cookie and bearer principals cannot blend scopes. Audit enrolment, issuance and revocation without token material.

Apply the specific D01–D17 policies relevant to this slice. Finish the narrow route/model/client registration in B, record generated migration paths, and preserve a safe stop/fix-forward path before presenting the diff for review.

### E. Focused tests

**NEW file:** `TestQtsBilling/Integration/NativeAuthorizationTests.cs`. Name the class `NativeAuthorizationTests` in the directory-derived namespace specified in §6.3.

- **T22.01:** Given missing or wrong verifier, callback, state, issuer or audience, when the task-specific command or transition is exercised, then native authorization fails.
- **T22.02:** Given refresh-token replay or revoked user/device, when the task-specific command or transition is exercised, then new authorization is denied and current business writes fail.
- **T22.03:** Given browser cross-origin mutation or token for another client/business, when the task-specific command or transition is exercised, then the API rejects it.
- **T22.04:** Given process restart/key rotation with retained valid verification keys, when the task-specific command or transition is exercised, then legitimate in-flight sessions follow the defined lifetime.
- **T22.05:** Given a clean Windows public client, when the task-specific command or transition is exercised, then no client secret/password capture is required by the native app.

Reuse existing or earlier-task fixtures. For a mutation, also assert denied tenant/location/permission and injected pre-commit failure leave zero partial financial/audit effects; for a read/UI-only task assert no mutation. Reuse the same fixture rather than duplicating equivalent tests.

### F. Verification

**Status: NOT RUN - SPEC_ONLY.** The following filter becomes executable only after the NEW test class and prerequisites are implemented.

Requires Task 01's isolated PostgreSQL 18/API fixture, .NET 10 SDK and explicitly validated QTS_TEST_POSTGRES_CONNECTION. Fixture DDL/data writes are permitted only in a later authorized test run; never inherit production credentials.

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~TestQtsBilling.Integration.NativeAuthorizationTests"
```

**PASS:** owning cases and relevant existing regression filters pass, with exact committed-state assertions where applicable; no skipped required prerequisite, new unreviewed dependency, secret leak or `git diff --check` failure. A build or mocked response alone is not financial proof.

Select the checked server or Windows build command in §6.3 only when this task changes that host. Full multi-platform regression belongs in CI/release, not every local edit.

### G. Definition of done

The Windows public client uses a verified authorization-code flow without embedded credentials or a client secret. Evidence includes the exact implementation SHA, focused test outcome and minimal reviewed diff. All new constraints/contracts/permissions/host wiring ship together under **G2**; unresolved external prerequisites stay explicit.

No unnecessary dependency or compatibility path remains. Recovery preserves historical facts and pending money. Record remaining blockers; spec completion is not implementation completion or owner production approval.

<a id="task-23"></a>
## Atomic Task 23 - Bind Windows sign-in and offline grants to the enrolled device

### A. Objective

**Priority:** P0. **Requirements:** `R03`, `R04`, `R09`, `R10`. **Findings:** `F10`, `F22`, `F28`, `F43`.
**Dependencies:** [Task 08](#task-08), [Task 17](#task-17), [Task 22](#task-22) **Integration gate:** G2 + G3.

**Current → target:** Local settings and indefinitely reusable session summaries act as offline permission; native enrolment is not a complete owner workflow. → Only a server-approved, expiring grant permits offline cash capture on the exact user/device/business/location/register session.

**Acceptance:** The target behavior is observable through the owning API/UI/store and every applicable scenario in E passes; release additionally requires the named gate, not just this file diff.

**Non-goals:** No offline card, journal, purchasing, refund or admin grants. Real Windows certificate/browser callback verification is a G7 platform check.

### B. Scope

| Action | Exact path | Change / exclusion |
|---|---|---|
| CREATE — NEW | `QtsWebApp/QtsWebApp/Features/Devices/DeviceEndpoints.cs` | Approved enrolment/revocation and signed offline capture grant issuance. |
| MODIFY | `QtsBilling.Maui/Services/DeviceAccountApiClient.cs` | System-browser OpenIddict client adapter; no cached-session authorization fallback. |
| MODIFY | `QtsBilling.Maui/Services/SessionStore.cs` | Origin/user/business/device-bound token and grant storage with explicit lifetime. |
| CREATE — NEW | `QtsBilling.Contracts/Identity/OfflineGrantDtos.cs` | Issuer/audience/subject/scope/session/time/permission-revision fields. |
| CREATE — NEW test | `TestQtsBilling/Integration/DeviceGrantTests.cs` | Owning focused assertions below; reuse fixtures rather than duplicate a test infrastructure. |

MODIFY existing `QtsWebApp/QtsWebApp/Program.cs` only for required route/service registration, and `QtsBilling.Contracts/Identity/IdentityDtos.cs` / `QtsWebApp/QtsWebApp/Infrastructure/QtsIdentity.cs` only for this task's explicit D02 policies.

All other production paths are excluded except the narrow, explicitly enumerated [D17 integration bindings](#d17). Applied migrations, posted facts and unresolved cash are never edited/deleted by this scope.

### C. Verified anchors and NEW insertions

- **EXISTING / VERIFIED:** [QtsBilling.Maui/Services/DeviceLocalPosService.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Maui/Services/DeviceLocalPosService.cs): `CaptureCashSaleAsync`. [E23](#e-e23).
- **EXISTING / VERIFIED:** [QtsBilling.Maui/Services/DeviceAccountApiClient.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Maui/Services/DeviceAccountApiClient.cs): `GetSessionAsync`. [E24](#e-e24).
- **EXISTING / VERIFIED:** [QtsBilling.Maui/Services/SessionStore.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Maui/Services/SessionStore.cs): `SaveSessionAsync; GetSessionAsync`. [E25](#e-e25).
- **EXISTING / VERIFIED:** [QtsBilling.Persistence.Postgres/BillingDbContext.Model.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Persistence.Postgres/BillingDbContext.Model.cs): `ConfigureCore; ConfigureMasterData; ConfigureCatalog; ConfigureInventory`. [E06](#e-e06).

All CREATE/GENERATE symbols, routes and constraints in B/D are **NEW**. Source presence is not runtime proof; do not infer executed results from old evidence prose.

### D. Ordered implementation recipe

1. Use D10 system-browser authorization through the official native client OS integration; reject non-TLS server URLs outside explicit local development. Remove cookie-copy sign-in after native rollout; tokens and grants are never stored in Preferences/SQLite.
2. Owner enrolment names an active same-business location/register, verifies a one-time challenge and records an immutable device identity. Revocation invalidates online access immediately and prevents future grants; re-enrolment cannot overwrite another device or pending cash context.
3. Sign a distinct bounded offline grant containing issuer/audience/kid/version/user/device/business/location/registerSession/currency/permissions/authRevision/issuedAt/expiry. Recommended lifetime eight hours, never above approved 24-hour bound. Scope only cash sales; current API access is still rechecked at push.
4. Validate signature, complete binding, expiry and monotonic elapsed time/clock rollback before capture. Cached UI identity is display-only. On 401/403 or known revocation, suspend capture; never fall back to stale privilege.
5. Changing server/account/business/location with unresolved local cash requires resolving/exporting supervised custody first or selecting a separately partitioned store; never wipe or silently relabel it. Logout clears credentials, not unacknowledged money.

Apply the specific D01–D17 policies relevant to this slice. Finish the narrow route/model/client registration in B, record generated migration paths, and preserve a safe stop/fix-forward path before presenting the diff for review.

### E. Focused tests

**NEW file:** `TestQtsBilling/Integration/DeviceGrantTests.cs`. Name the class `DeviceGrantTests` in the directory-derived namespace specified in §6.3.

- **T23.01:** Given expired, forged, copied-to-another-device or wrong-register grant, when the task-specific command or transition is exercised, then capture is refused.
- **T23.02:** Given clock rollback or server rejection, when the task-specific command or transition is exercised, then stale permissions do not authorize another sale.
- **T23.03:** Given enrolment for a location in another business, when the task-specific command or transition is exercised, then both API and database reject it.
- **T23.04:** Given account/server switch with pending cash, when the task-specific command or transition is exercised, then its original identity and recovery access remain intact.
- **T23.05:** Given native sign-in/logout/restart, when the task-specific command or transition is exercised, then secrets remain in secure storage and cash records survive.

Reuse existing or earlier-task fixtures. For a mutation, also assert denied tenant/location/permission and injected pre-commit failure leave zero partial financial/audit effects; for a read/UI-only task assert no mutation. Reuse the same fixture rather than duplicating equivalent tests.

### F. Verification

**Status: NOT RUN - SPEC_ONLY.** The following filter becomes executable only after the NEW test class and prerequisites are implemented.

Requires Task 01's isolated PostgreSQL 18/API fixture, .NET 10 SDK and explicitly validated QTS_TEST_POSTGRES_CONNECTION. Fixture DDL/data writes are permitted only in a later authorized test run; never inherit production credentials.

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~TestQtsBilling.Integration.DeviceGrantTests"
```

**PASS:** owning cases and relevant existing regression filters pass, with exact committed-state assertions where applicable; no skipped required prerequisite, new unreviewed dependency, secret leak or `git diff --check` failure. A build or mocked response alone is not financial proof.

Also compile QtsBilling.Maui on Windows and exercise the system-browser callback under the declared Windows/WebView2 support matrix; do not count a server-only test as native proof.

Select the checked server or Windows build command in §6.3 only when this task changes that host. Full multi-platform regression belongs in CI/release, not every local edit.

### G. Definition of done

Only a server-approved, expiring grant permits offline cash capture on the exact user/device/business/location/register session. Evidence includes the exact implementation SHA, focused test outcome and minimal reviewed diff. All new constraints/contracts/permissions/host wiring ship together under **G2 + G3**; unresolved external prerequisites stay explicit.

No unnecessary dependency or compatibility path remains. Recovery preserves historical facts and pending money. Record remaining blockers; spec completion is not implementation completion or owner production approval.

<a id="task-24"></a>
## Atomic Task 24 - Unify sync-push validation and durable terminal outcomes

### A. Objective

**Priority:** P0. **Requirements:** `R03`, `R08`, `R10`. **Findings:** `F13`, `F21`, `F22`, `F23`.
**Dependencies:** [Task 11](#task-11), [Task 12](#task-12), [Task 13](#task-13), [Task 14](#task-14), [Task 15](#task-15), [Task 17](#task-17), [Task 23](#task-23) **Integration gate:** G3.

**Current → target:** Early validation failures are not remembered, envelope/payload/device locations can diverge, and concurrent winner handling is incomplete. → Each authenticated, well-formed native operation has one stable business outcome or an explicitly retryable transport failure.

**Acceptance:** The target behavior is observable through the owning API/UI/store and every applicable scenario in E passes; release additionally requires the named gate, not just this file diff.

**Non-goals:** No raw collection replication, legacy wire decoder or direct device authority over ledger facts.

### B. Scope

| Action | Exact path | Change / exclusion |
|---|---|---|
| MODIFY | `QtsWebApp/QtsWebApp/Features/Sync/SyncOperationEndpoints.cs` | Complete bounded validation, device scope binding and one terminal receipt path. |
| MODIFY | `QtsBilling.Contracts/Sync/SyncDtos.cs` | Native operation and canonical terminal result fields. |
| MODIFY | `QtsBilling.Persistence.Postgres/Entities/Platform.cs` | Full receipt subject/fingerprint/reference and approved capture identity. |
| GENERATE — NEW | `QtsBilling.Persistence.Postgres/Migrations/` | NEW CompleteSyncReceipts with scope and immutable outcome guards. |
| CREATE — NEW test | `TestQtsBilling/Integration/SyncPushIntegrityTests.cs` | Owning focused assertions below; reuse fixtures rather than duplicate a test infrastructure. |

MODIFY existing `QtsBilling.Persistence.Postgres/BillingDbContext.cs` and the appropriate checked `BillingDbContext.Model.cs` / `BillingDbContext.Model.Financial.cs` only for the listed model; GENERATE designer/snapshot companions with the named new migration.

MODIFY existing `QtsWebApp/QtsWebApp/Program.cs` only for required route/service registration, and `QtsBilling.Contracts/Identity/IdentityDtos.cs` / `QtsWebApp/QtsWebApp/Infrastructure/QtsIdentity.cs` only for this task's explicit D02 policies.

All other production paths are excluded except the narrow, explicitly enumerated [D17 integration bindings](#d17). Applied migrations, posted facts and unresolved cash are never edited/deleted by this scope.

### C. Verified anchors and NEW insertions

- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Sync/SyncOperationEndpoints.cs — source lines 1-460](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Sync/SyncOperationEndpoints.cs#L1-L460): `ProcessAsync / operation and batch handlers`. [E15](#e-e15).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Sync/SyncOperationEndpoints.cs — source lines 460-633](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Sync/SyncOperationEndpoints.cs#L460-L633): `TryReadSale; NeedsReviewAsync; rejected receipt handling`. [E16](#e-e16).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Sales/SalePosting.cs — source lines 1-310](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Sales/SalePosting.cs#L1-L310): `ValidatePayload; ResolveContextAsync; PriceLines; ValidatePayment`. [E11](#e-e11).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Sales/SalePosting.cs — source lines 310-665](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Sales/SalePosting.cs#L310-L665): `PostAsync / posting transaction and revenue journal`. [E12](#e-e12).

All CREATE/GENERATE symbols, routes and constraints in B/D are **NEW**. Source presence is not runtime proof; do not infer executed results from old evidence prose.

### D. Ordered implementation recipe

1. Validate bounded body/version/type/line count and shared SalePosting validation before expensive work. Bind authenticated device, captured user/grant, business, envelope location, payload location and original register session; reject mismatches before data disclosure.
2. Fingerprint the entire semantic command including subject/type/scope. A replay reads the stored receipt and original posted number/reference; a uniqueness-race winner must match the same fingerprint. Use one canonical result shape for first response and replay.
3. Resolve prices, current authority, stock and period through repaired shared posting logic. On accepted sale commit the complete result receipt, sale, stock/value, settlement/AR, journal and audit in one transaction.
4. Persist all authenticated semantic rejections, including pre-posting price/tax/register failures, as stable NeedsReview receipts. Malformed/unauthenticated requests are transport failures, not fabricated immutable receipts; transient DB/network failures are retryable and do not consume the terminal slot.
5. For batches, report per-operation results and resumable remaining work without claiming atomicity across the whole batch. Never change a refused operation body under the same ID; supervised correction is a linked new operation after cash resolution.

Apply the specific D01–D17 policies relevant to this slice. Finish the narrow route/model/client registration in B, record generated migration paths, and preserve a safe stop/fix-forward path before presenting the diff for review.

### E. Focused tests

**NEW file:** `TestQtsBilling/Integration/SyncPushIntegrityTests.cs`. Name the class `SyncPushIntegrityTests` in the directory-derived namespace specified in §6.3.

- **T24.01:** Given envelope/payload/device location or captured user mismatch, when the task-specific command or transition is exercised, then no sale is posted.
- **T24.02:** Given price rejection before posting and later catalog correction, when the task-specific command or transition is exercised, then the original operation still returns its stored NeedsReview outcome.
- **T24.03:** Given twenty identical concurrent pushes and lost first response, when the task-specific command or transition is exercised, then one sale and one stable complete receipt exist.
- **T24.04:** Given same ID with different body racing, when the task-specific command or transition is exercised, then the losing payload receives SAME_KEY_DIFFERENT_PAYLOAD.
- **T24.05:** Given injected transient failure, when the task-specific command or transition is exercised, then retry can later post; no permanent rejection was invented.

Reuse existing or earlier-task fixtures. For a mutation, also assert denied tenant/location/permission and injected pre-commit failure leave zero partial financial/audit effects; for a read/UI-only task assert no mutation. Reuse the same fixture rather than duplicating equivalent tests.

### F. Verification

**Status: NOT RUN - SPEC_ONLY.** The following filter becomes executable only after the NEW test class and prerequisites are implemented.

Requires Task 01's isolated PostgreSQL 18/API fixture, .NET 10 SDK and explicitly validated QTS_TEST_POSTGRES_CONNECTION. Fixture DDL/data writes are permitted only in a later authorized test run; never inherit production credentials.

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~TestQtsBilling.Integration.SyncPushIntegrityTests"
```

**PASS:** owning cases and relevant existing regression filters pass, with exact committed-state assertions where applicable; no skipped required prerequisite, new unreviewed dependency, secret leak or `git diff --check` failure. A build or mocked response alone is not financial proof.

Select the checked server or Windows build command in §6.3 only when this task changes that host. Full multi-platform regression belongs in CI/release, not every local edit.

### G. Definition of done

Each authenticated, well-formed native operation has one stable business outcome or an explicitly retryable transport failure. Evidence includes the exact implementation SHA, focused test outcome and minimal reviewed diff. All new constraints/contracts/permissions/host wiring ship together under **G3**; unresolved external prerequisites stay explicit.

No unnecessary dependency or compatibility path remains. Recovery preserves historical facts and pending money. Record remaining blockers; spec completion is not implementation completion or owner production approval.

<a id="task-25"></a>
## Atomic Task 25 - Repair local receipt state, retry transitions and unresolved cash totals

### A. Objective

**Priority:** P0. **Requirements:** `R09`, `R10`. **Findings:** `F24`, `F25`, `F28`.
**Dependencies:** [Task 03](#task-03), [Task 17](#task-17), [Task 24](#task-24) **Integration gate:** G3.

**Current → target:** A TransientRetry occupies the inbox receipt key, preventing later Posted; rejected cash disappears from the pending-money total. → Retries can recover to a final outcome, while every amount physically collected remains visible until reconciled or refunded.

**Acceptance:** The target behavior is observable through the owning API/UI/store and every applicable scenario in E passes; release additionally requires the named gate, not just this file diff.

**Non-goals:** No deletion of pending or rejected operations as a migration strategy.

### B. Scope

| Action | Exact path | Change / exclusion |
|---|---|---|
| MODIFY | `QtsBilling.Persistence.Sqlite/SqliteLocalStore.cs` | Separate transient attempts from terminal receipts; scope queue/custody reads. |
| MODIFY | `QtsBilling.Persistence.Sqlite/Entities/SyncEntities.cs` | Attempt/terminal state data and original scope binding. |
| MODIFY | `QtsBilling.Persistence.Sqlite/Entities/ProvisionalEntities.cs` | Cash-held/refunded/recovered disposition independent of posting outcome. |
| GENERATE — NEW | `QtsBilling.Persistence.Sqlite/Migrations/` | NEW DurableTerminalReceiptsAndCashDisposition with preservation of existing pending rows. |
| CREATE — NEW test | `QtsBilling.Persistence.Sqlite.Tests/TerminalReceiptAndCashTests.cs` | Owning focused assertions below; reuse fixtures rather than duplicate a test infrastructure. |

MODIFY existing `QtsBilling.Persistence.Sqlite/LocalStoreDbContext.cs` for the listed native fields/constraints; preserve the checked local migration chain.

All other production paths are excluded except the narrow, explicitly enumerated [D17 integration bindings](#d17). Applied migrations, posted facts and unresolved cash are never edited/deleted by this scope.

### C. Verified anchors and NEW insertions

- **EXISTING / VERIFIED:** [QtsBilling.Persistence.Sqlite/SqliteLocalStore.cs — source lines 1-290](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Persistence.Sqlite/SqliteLocalStore.cs#L1-L290): `InitializeAsync; CaptureProvisionalCashSaleAsync`. [E20](#e-e20).
- **EXISTING / VERIFIED:** [QtsBilling.Persistence.Sqlite/SqliteLocalStore.cs — source lines 340-650](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Persistence.Sqlite/SqliteLocalStore.cs#L340-L650): `GetDueOutboxAsync; MarkOutboxSendingAsync; ApplyReceiptAsync; GetPendingSummaryAsync`. [E21](#e-e21).
- **EXISTING / VERIFIED:** [QtsBilling.Persistence.Sqlite/SqliteLocalStore.cs — source lines 780-1040](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Persistence.Sqlite/SqliteLocalStore.cs#L780-L1040): `SaveDeviceProfileAsync; MapOutcome; ReceiptMatches; cursor helpers`. [E22](#e-e22).

All CREATE/GENERATE symbols, routes and constraints in B/D are **NEW**. Source presence is not runtime proof; do not infer executed results from old evidence prose.

### D. Ordered implementation recipe

1. Preserve current atomic capture/write gate. Store transient transport attempts separately from the immutable terminal inbox row; Pending → Sending → Pending is recoverable, while terminal Posted/NeedsReview receipt application is idempotent.
2. Validate a receipt against original operation identity/scope/fingerprint and stored terminal result, not just an arbitrary supplied OperationId. Stable replay number/reference must be preserved; a different terminal outcome becomes an integrity alert.
3. Add explicit cash custody state: HeldPending, HeldNeedsReview, AcceptedByServer, RefundedLocallyWithApproval/Recovered. Report held pending and rejected money separately and in a combined unresolved total; transport classification never deletes money.
4. Partition outbox, receipts, drafts, profiles and summaries by approved server/device/business/user context. Reject rebind with unresolved records. Keep retained acknowledgements sufficient for crash/replay diagnosis.
5. Upgrade with verified SQLite snapshot and known schema; migrate existing transient inbox records into retry history without discarding capture data. Unknown/malformed records block capture and expose recovery rather than silently repaired status.

Apply the specific D01–D17 policies relevant to this slice. Finish the narrow route/model/client registration in B, record generated migration paths, and preserve a safe stop/fix-forward path before presenting the diff for review.

### E. Focused tests

**NEW file:** `QtsBilling.Persistence.Sqlite.Tests/TerminalReceiptAndCashTests.cs`. Name the class `TerminalReceiptAndCashTests` in the directory-derived namespace specified in §6.3.

- **T25.01:** Given TransientRetry followed by Posted, when the task-specific command or transition is exercised, then the operation becomes acknowledged with one terminal receipt.
- **T25.02:** Given identical final receipt repeated, when the task-specific command or transition is exercised, then cash and draft state change once; conflicting final receipt is refused.
- **T25.03:** Given rejected sale with cash collected, when the task-specific command or transition is exercised, then unresolved custody still includes the amount.
- **T25.04:** Given upgrade/restart during receipt application, when the task-specific command or transition is exercised, then the complete old or new transaction is visible, never partial cash state.
- **T25.05:** Given another user/business/server context, when the task-specific command or transition is exercised, then the queue cannot be read or rebound as that context.

Reuse existing or earlier-task fixtures. For a mutation, also assert denied tenant/location/permission and injected pre-commit failure leave zero partial financial/audit effects; for a read/UI-only task assert no mutation. Reuse the same fixture rather than duplicating equivalent tests.

### F. Verification

**Status: NOT RUN - SPEC_ONLY.** The following filter becomes executable only after the NEW test class and prerequisites are implemented.

Use the EXISTING real-file `LocalStoreHarness`; fixture-owned temporary files only. No server or production DB is needed.

```text
dotnet test QtsBilling.Persistence.Sqlite.Tests/QtsBilling.Persistence.Sqlite.Tests.csproj -c Release --filter "FullyQualifiedName~QtsBilling.Persistence.Sqlite.Tests.TerminalReceiptAndCashTests"
```

**PASS:** owning cases and relevant existing regression filters pass, with exact committed-state assertions where applicable; no skipped required prerequisite, new unreviewed dependency, secret leak or `git diff --check` failure. A build or mocked response alone is not financial proof.

Select the checked server or Windows build command in §6.3 only when this task changes that host. Full multi-platform regression belongs in CI/release, not every local edit.

### G. Definition of done

Retries can recover to a final outcome, while every amount physically collected remains visible until reconciled or refunded. Evidence includes the exact implementation SHA, focused test outcome and minimal reviewed diff. All new constraints/contracts/permissions/host wiring ship together under **G3**; unresolved external prerequisites stay explicit.

No unnecessary dependency or compatibility path remains. Recovery preserves historical facts and pending money. Record remaining blockers; spec completion is not implementation completion or owner production approval.

<a id="task-26"></a>
## Atomic Task 26 - Provide a complete device-bound change feed and resync baseline

### A. Objective

**Priority:** P0. **Requirements:** `R03`, `R10`. **Findings:** `F22`, `F27`.
**Dependencies:** [Task 04](#task-04), [Task 05](#task-05), [Task 11](#task-11), [Task 23](#task-23), [Task 24](#task-24) **Integration gate:** G3.

**Current → target:** Reset-to-zero feed reading cannot reconstruct pre-feed data or expired history; location/device permission binding is incomplete. → A fresh or expired device obtains a complete consistent native baseline, then a strictly ordered authorized incremental feed.

**Acceptance:** The target behavior is observable through the owning API/UI/store and every applicable scenario in E passes; release additionally requires the named gate, not just this file diff.

**Non-goals:** No full database dump, unbounded snapshot held in memory, or deletion of financial audit history to shrink the feed.

### B. Scope

| Action | Exact path | Change / exclusion |
|---|---|---|
| MODIFY | `QtsWebApp/QtsWebApp/Features/Sync/SyncChangeEndpoints.cs` | Subscription-bound cursor, retention floor, epoch and native baseline endpoints. |
| MODIFY | `QtsWebApp/QtsWebApp/Features/Sync/SyncProjections.cs` | Complete product/tax/base-unit/stock projections and tombstones. |
| CREATE — NEW | `QtsWebApp/QtsWebApp/Features/Sync/SyncBaseline.cs` | Bounded immutable snapshot materialization and page leases. |
| GENERATE — NEW | `QtsBilling.Persistence.Postgres/Migrations/` | NEW SyncEpochAndBaselineLease state and indexes. |
| CREATE — NEW test | `TestQtsBilling/Integration/SyncBaselineTests.cs` | Owning focused assertions below; reuse fixtures rather than duplicate a test infrastructure. |

MODIFY existing `QtsWebApp/QtsWebApp/Program.cs` only for required route/service registration, and `QtsBilling.Contracts/Identity/IdentityDtos.cs` / `QtsWebApp/QtsWebApp/Infrastructure/QtsIdentity.cs` only for this task's explicit D02 policies.

All other production paths are excluded except the narrow, explicitly enumerated [D17 integration bindings](#d17). Applied migrations, posted facts and unresolved cash are never edited/deleted by this scope.

### C. Verified anchors and NEW insertions

- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Sync/SyncChangeEndpoints.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Sync/SyncChangeEndpoints.cs): `Change-feed endpoint`. [E17](#e-e17).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Inventory/InventoryLedger.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Inventory/InventoryLedger.cs): `ApplyAsync`. [E13](#e-e13).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Sync/SyncTransaction.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Sync/SyncTransaction.cs): `RunAsync`. [E14](#e-e14).
- **EXISTING / VERIFIED:** [QtsBilling.Persistence.Sqlite/SqliteLocalStore.Sync.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Persistence.Sqlite/SqliteLocalStore.Sync.cs): `ApplyChangesAsync; ApplyProjectionAsync; ApplyTombstoneAsync; ResolveConflictAsync`. [E19](#e-e19).

All CREATE/GENERATE symbols, routes and constraints in B/D are **NEW**. Source presence is not runtime proof; do not infer executed results from old evidence prose.

### D. Ordered implementation recipe

1. Implement D12 cursor containing business/device/location/subscriptionHash/epoch/schemaVersion/revision/ordinal. Validate current device permission; a change in scope, retained-history floor or epoch returns RESYNC_REQUIRED with a safe baseline workflow.
2. Materialize baseline pages from one repeatable-read snapshot with its high-water cursor and bounded lease. Include all active permitted masters and stock, even records never changed since feed existed. Do not hold a DB transaction across HTTP page requests.
3. Check page digests/counts and final checkpoint. Retain immutable baseline page bodies for the short lease in the existing PostgreSQL infrastructure; scope leases to authorized device and expire them explicitly.
4. Incremental writes/projections use the same transaction and commit-ordered business revision lock. Every supported deactivation emits an effective tombstone; product archive invalidates all its variants. Stock projections include location; no unauthorized warehouse data is sent.
5. Pagination advances over authorized change positions consistently, supports multi-change revisions, and never silently skips unreadable projections/unknown schema. Pruning requires minimum retained checkpoint policy, separate from immutable financial facts.

Apply the specific D01–D17 policies relevant to this slice. Finish the narrow route/model/client registration in B, record generated migration paths, and preserve a safe stop/fix-forward path before presenting the diff for review.

### E. Focused tests

**NEW file:** `TestQtsBilling/Integration/SyncBaselineTests.cs`. Name the class `SyncBaselineTests` in the directory-derived namespace specified in §6.3.

- **T26.01:** Given a fresh device and masters predating the feed, when the task-specific command or transition is exercised, then baseline contains them all.
- **T26.02:** Given writes while baseline pages are downloaded, when the task-specific command or transition is exercised, then baseline+incremental feed has no gap or duplicate business effect.
- **T26.03:** Given expired cursor, changed location/permission or epoch, when the task-specific command or transition is exercised, then resync is required without exposing another scope.
- **T26.04:** Given product deactivation or warehouse B stock change, when the task-specific command or transition is exercised, then warehouse A cache receives only its correct authorized effect.
- **T26.05:** Given more than one page and a partial revision, when the task-specific command or transition is exercised, then the final cursor resumes exactly after the last validated change.

Reuse existing or earlier-task fixtures. For a mutation, also assert denied tenant/location/permission and injected pre-commit failure leave zero partial financial/audit effects; for a read/UI-only task assert no mutation. Reuse the same fixture rather than duplicating equivalent tests.

### F. Verification

**Status: NOT RUN - SPEC_ONLY.** The following filter becomes executable only after the NEW test class and prerequisites are implemented.

Requires Task 01's isolated PostgreSQL 18/API fixture, .NET 10 SDK and explicitly validated QTS_TEST_POSTGRES_CONNECTION. Fixture DDL/data writes are permitted only in a later authorized test run; never inherit production credentials.

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~TestQtsBilling.Integration.SyncBaselineTests"
```

**PASS:** owning cases and relevant existing regression filters pass, with exact committed-state assertions where applicable; no skipped required prerequisite, new unreviewed dependency, secret leak or `git diff --check` failure. A build or mocked response alone is not financial proof.

Select the checked server or Windows build command in §6.3 only when this task changes that host. Full multi-platform regression belongs in CI/release, not every local edit.

### G. Definition of done

A fresh or expired device obtains a complete consistent native baseline, then a strictly ordered authorized incremental feed. Evidence includes the exact implementation SHA, focused test outcome and minimal reviewed diff. All new constraints/contracts/permissions/host wiring ship together under **G3**; unresolved external prerequisites stay explicit.

No unnecessary dependency or compatibility path remains. Recovery preserves historical facts and pending money. Record remaining blockers; spec completion is not implementation completion or owner production approval.

<a id="task-27"></a>
## Atomic Task 27 - Apply cache, tombstones, conflicts and cursor in one SQLite transaction

### A. Objective

**Priority:** P0. **Requirements:** `R03`, `R10`. **Findings:** `F26`, `F27`, `F28`.
**Dependencies:** [Task 25](#task-25), [Task 26](#task-26) **Integration gate:** G3.

**Current → target:** Cache changes occur before conflict detection, location data is misapplied, product tombstones are ignored and cursor commit is separate. → Each page produces one validated cache/checkpoint transition while immutable pending commercial facts and custody remain unchanged.

**Acceptance:** The target behavior is observable through the owning API/UI/store and every applicable scenario in E passes; release additionally requires the named gate, not just this file diff.

**Non-goals:** No broad cache reset that includes pending tables; no automatic acceptance of stale money or malformed projections.

### B. Scope

| Action | Exact path | Change / exclusion |
|---|---|---|
| MODIFY | `QtsBilling.Persistence.Sqlite/SqliteLocalStore.Sync.cs` | Atomic page validation/application/cursor and real conflict resolution. |
| MODIFY | `QtsBilling.Persistence.Sqlite/Entities/CacheEntities.cs` | Parent product/variant, scope, tax mode and native catalog version. |
| MODIFY | `QtsBilling.Persistence.Sqlite/Entities/SyncConflict.cs` | Original operation/change identities and supervised disposition. |
| GENERATE — NEW | `QtsBilling.Persistence.Sqlite/Migrations/` | NEW AtomicCacheCheckpoint with scope and idempotent change/conflict keys. |
| MODIFY | `QtsBilling.Persistence.Sqlite/LocalStoreDbContext.cs` | Configure new cache/conflict/checkpoint fields with exact native constraints. |
| CREATE — NEW test | `QtsBilling.Persistence.Sqlite.Tests/AtomicSyncPageTests.cs` | Owning focused assertions below; reuse fixtures rather than duplicate a test infrastructure. |

MODIFY existing `QtsBilling.Persistence.Sqlite/LocalStoreDbContext.cs` for the listed native fields/constraints; preserve the checked local migration chain.

All other production paths are excluded except the narrow, explicitly enumerated [D17 integration bindings](#d17). Applied migrations, posted facts and unresolved cash are never edited/deleted by this scope.

### C. Verified anchors and NEW insertions

- **EXISTING / VERIFIED:** [QtsBilling.Persistence.Sqlite/SqliteLocalStore.Sync.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Persistence.Sqlite/SqliteLocalStore.Sync.cs): `ApplyChangesAsync; ApplyProjectionAsync; ApplyTombstoneAsync; ResolveConflictAsync`. [E19](#e-e19).
- **EXISTING / VERIFIED:** [QtsBilling.Persistence.Sqlite/SqliteLocalStore.cs — source lines 340-650](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Persistence.Sqlite/SqliteLocalStore.cs#L340-L650): `GetDueOutboxAsync; MarkOutboxSendingAsync; ApplyReceiptAsync; GetPendingSummaryAsync`. [E21](#e-e21).
- **EXISTING / VERIFIED:** [QtsBilling.Persistence.Sqlite/SqliteLocalStore.cs — source lines 780-1040](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Persistence.Sqlite/SqliteLocalStore.cs#L780-L1040): `SaveDeviceProfileAsync; MapOutcome; ReceiptMatches; cursor helpers`. [E22](#e-e22).

All CREATE/GENERATE symbols, routes and constraints in B/D are **NEW**. Source presence is not runtime proof; do not infer executed results from old evidence prose.

### D. Ordered implementation recipe

1. Validate every page schema, digest, subscription, ordering and stock payload location before mutation. Acquire SqliteWriteGate and one transaction for projection application, tombstones, conflict records and next cursor.
2. Apply authoritative catalog to future sales; do not edit any pending sale line/payload/price. Detect affected pending operations first and create one unique review issue per operation/change when required. Never report a changed tracked row as skipped.
3. Handle all native tombstones. Product deletion/deactivation hides every child variant from new capture; stock applies only to the matching location; unsupported/malformed projections abort the page rather than moving cursor past them.
4. Stage complete baseline cache in scope, validate all pages/high-water, then atomically replace only discardable cache tables and checkpoint. Preserve drafts, cash, outbox, terminal receipts, grant/custody identity and review history.
5. Resolve conflicts with explicit permitted actions and immutable disposition evidence. Catalog refresh cannot choose a local financial value as authoritative. Cash rejection recovery uses Task 29, not KeepLocal toggles. Repeated page/resolution application is idempotent.

Apply the specific D01–D17 policies relevant to this slice. Finish the narrow route/model/client registration in B, record generated migration paths, and preserve a safe stop/fix-forward path before presenting the diff for review.

### E. Focused tests

**NEW file:** `QtsBilling.Persistence.Sqlite.Tests/AtomicSyncPageTests.cs`. Name the class `AtomicSyncPageTests` in the directory-derived namespace specified in §6.3.

- **T27.01:** Given a crash between a projection and cursor write, when the task-specific command or transition is exercised, then neither or both persist.
- **T27.02:** Given stock for another location, when the task-specific command or transition is exercised, then local stock is not overwritten and page validation fails.
- **T27.03:** Given archived product with variants, when the task-specific command or transition is exercised, then none remains sellable after tombstone.
- **T27.04:** Given pending sale and newer catalog, when the task-specific command or transition is exercised, then the cache updates but the sale payload and collected amount remain unchanged with review evidence.
- **T27.05:** Given baseline rebuild or repeated feed page, when the task-specific command or transition is exercised, then pending cash survives and no duplicate conflict is created.

Reuse existing or earlier-task fixtures. For a mutation, also assert denied tenant/location/permission and injected pre-commit failure leave zero partial financial/audit effects; for a read/UI-only task assert no mutation. Reuse the same fixture rather than duplicating equivalent tests.

### F. Verification

**Status: NOT RUN - SPEC_ONLY.** The following filter becomes executable only after the NEW test class and prerequisites are implemented.

Use the EXISTING real-file `LocalStoreHarness`; fixture-owned temporary files only. No server or production DB is needed.

```text
dotnet test QtsBilling.Persistence.Sqlite.Tests/QtsBilling.Persistence.Sqlite.Tests.csproj -c Release --filter "FullyQualifiedName~QtsBilling.Persistence.Sqlite.Tests.AtomicSyncPageTests"
```

**PASS:** owning cases and relevant existing regression filters pass, with exact committed-state assertions where applicable; no skipped required prerequisite, new unreviewed dependency, secret leak or `git diff --check` failure. A build or mocked response alone is not financial proof.

Select the checked server or Windows build command in §6.3 only when this task changes that host. Full multi-platform regression belongs in CI/release, not every local edit.

### G. Definition of done

Each page produces one validated cache/checkpoint transition while immutable pending commercial facts and custody remain unchanged. Evidence includes the exact implementation SHA, focused test outcome and minimal reviewed diff. All new constraints/contracts/permissions/host wiring ship together under **G3**; unresolved external prerequisites stay explicit.

No unnecessary dependency or compatibility path remains. Recovery preserves historical facts and pending money. Record remaining blockers; spec completion is not implementation completion or owner production approval.

<a id="task-28"></a>
## Atomic Task 28 - Make device synchronization single-flight, resumable and scope-safe

### A. Objective

**Priority:** P0. **Requirements:** `R04`, `R09`, `R10`. **Findings:** `F24`, `F25`, `F27`, `F28`.
**Dependencies:** [Task 23](#task-23), [Task 24](#task-24), [Task 25](#task-25), [Task 26](#task-26), [Task 27](#task-27) **Integration gate:** G3.

**Current → target:** Timer/manual sync can overlap; durable Sending/backoff methods are not fully used, and broad HTTP statuses can fabricate terminal rejections. → One coordinator reconciles the original scoped outbox safely after network loss, authorization changes, crash and large feed backlogs.

**Acceptance:** The target behavior is observable through the owning API/UI/store and every applicable scenario in E passes; release additionally requires the named gate, not just this file diff.

**Non-goals:** Extract only platform-neutral lease/retry/page sequencing into QtsBilling.Persistence.Sqlite/Sync/OutboxSyncRunner.cs. The existing MAUI coordinator delegates to it and retains native auth/lifecycle orchestration; tests use the real LocalStoreHarness, TimeProvider and a fake SyncApiClient transport, never linked Windows source or a new framework.

### B. Scope

| Action | Exact path | Change / exclusion |
|---|---|---|
| MODIFY | `QtsBilling.Maui/Services/DeviceSyncCoordinator.cs` | Single-flight push/pull, leased attempts, terminal receipt validation and page walking. |
| MODIFY | `QtsBilling.Maui/Services/DeviceSyncTimer.cs` | Cancellation/backoff and lifecycle-triggered scheduling. |
| MODIFY | `QtsBilling.Maui/Services/DeviceSyncConflictService.cs` | Real resolution commands and cash-custody-aware outcomes. |
| CREATE — NEW | `QtsBilling.Persistence.Sqlite/Sync/OutboxSyncRunner.cs` | Platform-neutral sequencing over the existing store and SyncApiClient contract; delegates native auth/lifecycle to the MAUI host. |
| CREATE — NEW test | `QtsBilling.Persistence.Sqlite.Tests/OutboxSyncRunnerTests.cs` | Owning focused assertions below; reuse fixtures rather than duplicate a test infrastructure. |

All other production paths are excluded except the narrow, explicitly enumerated [D17 integration bindings](#d17). Applied migrations, posted facts and unresolved cash are never edited/deleted by this scope.

### C. Verified anchors and NEW insertions

- **EXISTING / VERIFIED:** [QtsBilling.Maui/Services/DeviceSyncCoordinator.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Maui/Services/DeviceSyncCoordinator.cs): `RunAsync; push/pull; BuildEnvelope`. [E18](#e-e18).
- **EXISTING / VERIFIED:** [QtsBilling.Persistence.Sqlite/SqliteLocalStore.cs — source lines 340-650](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Persistence.Sqlite/SqliteLocalStore.cs#L340-L650): `GetDueOutboxAsync; MarkOutboxSendingAsync; ApplyReceiptAsync; GetPendingSummaryAsync`. [E21](#e-e21).
- **EXISTING / VERIFIED:** [QtsBilling.Persistence.Sqlite/SqliteLocalStore.cs — source lines 780-1040](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Persistence.Sqlite/SqliteLocalStore.cs#L780-L1040): `SaveDeviceProfileAsync; MapOutcome; ReceiptMatches; cursor helpers`. [E22](#e-e22).
- **EXISTING / VERIFIED:** [QtsBilling.Maui/Services/DeviceAccountApiClient.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Maui/Services/DeviceAccountApiClient.cs): `GetSessionAsync`. [E24](#e-e24).

All CREATE/GENERATE symbols, routes and constraints in B/D are **NEW**. Source presence is not runtime proof; do not infer executed results from old evidence prose.

### D. Ordered implementation recipe

1. Use one asynchronous single-flight gate shared by timer/manual/resume triggers. Before sending atomically lease due Pending operations as Sending with attempt count/nextAttempt; expired interrupted leases recover on startup.
2. Build envelopes from stored immutable device/business/location/captured identity, not current settings. Validate current permitted transport context; unresolved another-scope operation remains inaccessible to an ordinary new user but available to authorized recovery.
3. Classify network/timeout/429/5xx as bounded exponential backoff with jitter and cancellation. Treat 401/403 as reauthentication/revocation suspension, not a fabricated permanent business receipt. Persist only Task 24 terminal outcomes.
4. Walk changes/baseline pages until HasMore is false or a bounded run budget is reached; use Task 27 atomic apply/checkpoint. Recheck scope/lease between pages. A partial run reports backlog rather than false complete synchronization.
5. Recover lost acknowledgement by same operation ID and stable receipt; never resubmit changed payload/new ID. Status reports last durable success, held money, queue age, terminal review and next retry; sanitized technical details cannot crash the POS.

Apply the specific D01–D17 policies relevant to this slice. Finish the narrow route/model/client registration in B, record generated migration paths, and preserve a safe stop/fix-forward path before presenting the diff for review.

### E. Focused tests

**NEW file:** `QtsBilling.Persistence.Sqlite.Tests/OutboxSyncRunnerTests.cs`. Name the class `OutboxSyncRunnerTests` in the directory-derived namespace specified in §6.3.

- **T28.01:** Given timer and manual sync together, when the task-specific command or transition is exercised, then each operation has one active lease and there is no duplicate local receipt.
- **T28.02:** Given timeout after server commit, when the task-specific command or transition is exercised, then same-key retry resolves to the original posting.
- **T28.03:** Given repeated transient responses followed by recovery, when the task-specific command or transition is exercised, then retry progresses to Posted rather than inbox conflict.
- **T28.04:** Given 1,001 changes or interrupted baseline, when the task-specific command or transition is exercised, then page walking resumes with no missing or silently skipped rows.
- **T28.05:** Given logout/revocation/scope change mid-run, when the task-specific command or transition is exercised, then capture/push suspends without deleting money or leaking another business cache.

Reuse existing or earlier-task fixtures. For a mutation, also assert denied tenant/location/permission and injected pre-commit failure leave zero partial financial/audit effects; for a read/UI-only task assert no mutation. Reuse the same fixture rather than duplicating equivalent tests.

### F. Verification

**Status: NOT RUN - SPEC_ONLY.** The following filter becomes executable only after the NEW test class and prerequisites are implemented.

Use the EXISTING real-file `LocalStoreHarness`; fixture-owned temporary files only. No server or production DB is needed.

```text
dotnet test QtsBilling.Persistence.Sqlite.Tests/QtsBilling.Persistence.Sqlite.Tests.csproj -c Release --filter "FullyQualifiedName~QtsBilling.Persistence.Sqlite.Tests.OutboxSyncRunnerTests"
```

**PASS:** owning cases and relevant existing regression filters pass, with exact committed-state assertions where applicable; no skipped required prerequisite, new unreviewed dependency, secret leak or `git diff --check` failure. A build or mocked response alone is not financial proof.

The Windows build and a real crash/restart/network-loss run remain required in Task 62; doubles alone do not prove native durability.

Select the checked server or Windows build command in §6.3 only when this task changes that host. Full multi-platform regression belongs in CI/release, not every local edit.

### G. Definition of done

One coordinator reconciles the original scoped outbox safely after network loss, authorization changes, crash and large feed backlogs. Evidence includes the exact implementation SHA, focused test outcome and minimal reviewed diff. All new constraints/contracts/permissions/host wiring ship together under **G3**; unresolved external prerequisites stay explicit.

No unnecessary dependency or compatibility path remains. Recovery preserves historical facts and pending money. Record remaining blockers; spec completion is not implementation completion or owner production approval.

<a id="task-29"></a>
## Atomic Task 29 - Complete offline checkout, provisional receipt and rejected-cash recovery

### A. Objective

**Priority:** P0. **Requirements:** `R05`, `R09`, `R10`. **Findings:** `F12`, `F26`, `F28`.
**Dependencies:** [Task 03](#task-03), [Task 17](#task-17), [Task 23](#task-23), [Task 25](#task-25), [Task 27](#task-27), [Task 28](#task-28) **Integration gate:** G3.

**Current → target:** Offline tax calculation diverges from the server and rejected cash lacks an adequate operational resolution journey. → An authorized cashier can capture correct provisional cash offline, survive a restart and resolve every pending/refused operation without losing custody.

**Acceptance:** The target behavior is observable through the owning API/UI/store and every applicable scenario in E passes; release additionally requires the named gate, not just this file diff.

**Non-goals:** Offline scope is cash capture only. No assumption that disconnected stock is globally available; acceptance remains provisional until the server commits.

### B. Scope

| Action | Exact path | Change / exclusion |
|---|---|---|
| MODIFY | `QtsBilling.Persistence.Sqlite/Pos/LocalPosStore.cs` | Shared amount rules, grant validation and immutable capture snapshots. |
| MODIFY | `QtsBilling.Ui/Pages/Pos.razor.cs` | Exact provisional totals and explicit capture/retry/review state. |
| MODIFY | `QtsBilling.Ui/Components/ProvisionalReceipt.razor` | Original item/price/tax identity, custody and provisional/final distinction. |
| CREATE — NEW | `QtsBilling.Ui/Pages/OfflineReview.razor` | Authorized pending/rejected-cash queue with actionable resolution. |
| CREATE — NEW test | `QtsBilling.Persistence.Sqlite.Tests/OfflineCashJourneyTests.cs` | Owning focused assertions below; reuse fixtures rather than duplicate a test infrastructure. |

Apply only the exact typed-client/host registration and routing bindings in [D17](#d17); no unlisted feature is included. A new page cannot be marked complete before its owning API is wired.

All other production paths are excluded except the narrow, explicitly enumerated [D17 integration bindings](#d17). Applied migrations, posted facts and unresolved cash are never edited/deleted by this scope.

### C. Verified anchors and NEW insertions

- **EXISTING / VERIFIED:** [QtsBilling.Persistence.Sqlite/SqliteLocalStore.cs — source lines 1-290](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Persistence.Sqlite/SqliteLocalStore.cs#L1-L290): `InitializeAsync; CaptureProvisionalCashSaleAsync`. [E20](#e-e20).
- **EXISTING / VERIFIED:** [QtsBilling.Persistence.Sqlite/SqliteLocalStore.cs — source lines 340-650](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Persistence.Sqlite/SqliteLocalStore.cs#L340-L650): `GetDueOutboxAsync; MarkOutboxSendingAsync; ApplyReceiptAsync; GetPendingSummaryAsync`. [E21](#e-e21).
- **EXISTING / VERIFIED:** [QtsBilling.Maui/Services/DeviceLocalPosService.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Maui/Services/DeviceLocalPosService.cs): `CaptureCashSaleAsync`. [E23](#e-e23).
- **EXISTING / VERIFIED:** [QtsBilling.Ui/Pages/Pos.razor.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Ui/Pages/Pos.razor.cs): `LineTax; LineTotal; CaptureAsync`. [E26](#e-e26).

All CREATE/GENERATE symbols, routes and constraints in B/D are **NEW**. Source presence is not runtime proof; do not infer executed results from old evidence prose.

### D. Ordered implementation recipe

1. Use D03 amount calculation with cached tax mode/base-unit conversion/policy version, not hardcoded tax-exclusive arithmetic. Validate signed grant and allowed catalog scope before accepting tender; store original names/SKU/price/tax/discount/grant/session snapshots atomically.
2. Show PROVISIONAL, original reference, exact amount/change, captured timestamp, pending status and no final invoice number. Receipt/reprint reads captured snapshots, never current catalog names; post-acknowledgement display links the server result without replacing original evidence.
3. Keep one operation identity through uncertain capture/response; prevent simultaneous checkout edits while processing. Capture returns success only after SQLite commit; failures leave the cart and tender state available for safe recovery.
4. Review queue distinguishes waiting/retry/auth-suspended/NeedsReview/rejected-held cash. A manager may authorize a linked correction for server acceptance or document actual customer refund; neither changes the original operation nor discards its cash fact. Prevent double physical refund/duplicate recovery.
5. Expose pending age/custody totals and recovery export through authorized UI. Logout, corruption, upgrade, cache resync and device retirement never clear unresolved money; suspend new capture and guide recovery when correctness cannot be established.

Apply the specific D01–D17 policies relevant to this slice. Finish the narrow route/model/client registration in B, record generated migration paths, and preserve a safe stop/fix-forward path before presenting the diff for review.

### E. Focused tests

**NEW file:** `QtsBilling.Persistence.Sqlite.Tests/OfflineCashJourneyTests.cs`. Name the class `OfflineCashJourneyTests` in the directory-derived namespace specified in §6.3.

- **T29.01:** Given mixed inclusive/exclusive items and fractional units, when the task-specific command or transition is exercised, then device/server expected amounts are identical.
- **T29.02:** Given power loss after capture or receipt rendering failure, when the task-specific command or transition is exercised, then one durable operation exists and can be reprinted.
- **T29.03:** Given a rejected paid sale, when the task-specific command or transition is exercised, then the manager sees its held cash and can record one supervised resolution.
- **T29.04:** Given catalog rename/deactivation after capture, when the task-specific command or transition is exercised, then original receipt content is unchanged.
- **T29.05:** Given expired grant, corruption or a second refund attempt, when the task-specific command or transition is exercised, then capture/resolution refuses safely with an accessible explanation.

Reuse existing or earlier-task fixtures. For a mutation, also assert denied tenant/location/permission and injected pre-commit failure leave zero partial financial/audit effects; for a read/UI-only task assert no mutation. Reuse the same fixture rather than duplicating equivalent tests.

### F. Verification

**Status: NOT RUN - SPEC_ONLY.** The following filter becomes executable only after the NEW test class and prerequisites are implemented.

Use the EXISTING real-file `LocalStoreHarness`; fixture-owned temporary files only. No server or production DB is needed.

```text
dotnet test QtsBilling.Persistence.Sqlite.Tests/QtsBilling.Persistence.Sqlite.Tests.csproj -c Release --filter "FullyQualifiedName~QtsBilling.Persistence.Sqlite.Tests.OfflineCashJourneyTests"
```

**PASS:** owning cases and relevant existing regression filters pass, with exact committed-state assertions where applicable; no skipped required prerequisite, new unreviewed dependency, secret leak or `git diff --check` failure. A build or mocked response alone is not financial proof.

Select the checked server or Windows build command in §6.3 only when this task changes that host. Full multi-platform regression belongs in CI/release, not every local edit.

### G. Definition of done

An authorized cashier can capture correct provisional cash offline, survive a restart and resolve every pending/refused operation without losing custody. Evidence includes the exact implementation SHA, focused test outcome and minimal reviewed diff. All new constraints/contracts/permissions/host wiring ship together under **G3**; unresolved external prerequisites stay explicit.

No unnecessary dependency or compatibility path remains. Recovery preserves historical facts and pending money. Record remaining blockers; spec completion is not implementation completion or owner production approval.

<a id="task-30"></a>
## Atomic Task 30 - Create a permission-aware shared application shell and complete paging

### A. Objective

**Priority:** P1. **Requirements:** `R11`, `R17`. **Findings:** `F29`, `F30`, `F43`.
**Dependencies:** [Task 04](#task-04), [Task 06](#task-06), [Task 08](#task-08), [Task 29](#task-29) **Integration gate:** G5.

**Current → target:** The shared presentation covers Customers/local POS, not a complete daily business workspace; list consumers can stop at the first server page. → Users can find authorized workflows and navigate complete data sets without treating navigation visibility as authorization.

**Acceptance:** The target behavior is observable through the owning API/UI/store and every applicable scenario in E passes; release additionally requires the named gate, not just this file diff.

**Non-goals:** No full UI framework rewrite, invented translations or placeholders advertised as completed features.

### B. Scope

| Action | Exact path | Change / exclusion |
|---|---|---|
| MODIFY | `QtsBilling.Ui/Layout/NavMenu.razor` | Grouped permission/capability-aware navigation for actual supported screens. |
| MODIFY | `QtsBilling.Ui/Layout/MainLayout.razor` | Selected business/location, connection/custody status and accessible status region. |
| MODIFY | `QtsBilling.Ui/Pages/Customers.razor.cs` | Server cursor paging, cancellation and stale-response suppression. |
| CREATE — NEW | `QtsBilling.Ui/Pages/Dashboard.razor` | Actionable native home with read-only summaries and unfinished-setup prompts. |
| CREATE — NEW test | `TestQtsBilling/Ui/SharedShellTests.cs` | Owning focused assertions below; reuse fixtures rather than duplicate a test infrastructure. |

Apply only the exact typed-client/host registration and routing bindings in [D17](#d17); no unlisted feature is included. A new page cannot be marked complete before its owning API is wired.

All other production paths are excluded except the narrow, explicitly enumerated [D17 integration bindings](#d17). Applied migrations, posted facts and unresolved cash are never edited/deleted by this scope.

### C. Verified anchors and NEW insertions

- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Infrastructure/QtsIdentity.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Infrastructure/QtsIdentity.cs): `PermissionAuthorizationHandler.HandleRequirementAsync; QtsClaimsPrincipalFactory`. [E04](#e-e04).
- **EXISTING / VERIFIED:** [QtsBilling.Ui/Pages/Pos.razor.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Ui/Pages/Pos.razor.cs): `LineTax; LineTotal; CaptureAsync`. [E26](#e-e26).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Pos/WebLocalPosService.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Pos/WebLocalPosService.cs): `ILocalPosService implementation`. [E27](#e-e27).
- **EXISTING / VERIFIED:** [README.md — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/README.md): `Architecture / status / project descriptions`. [E48](#e-e48).

All CREATE/GENERATE symbols, routes and constraints in B/D are **NEW**. Source presence is not runtime proof; do not infer executed results from old evidence prose.

### D. Ordered implementation recipe

1. Implement role-aware groups: Sales, Purchasing, Stock, Finance and Administration; show only landed routes with applicable permissions. Display selected business/location and a persistent provisional-cash indicator in the native host.
2. Use typed feature interfaces, not service-locator discovery in new pages. Hosts own authentication/render mode/routing/assets; the RCL remains free of persistence and host internals. Online-only features state why they are unavailable while disconnected.
3. Replace first-page-only tables with explicit cursor/next/previous stack and bounded page size. New search/filter cancels the prior request, resets cursor and discards stale responses; selecting/editing a row preserves its version.
4. Provide loading, no-data, filtered-empty, denied, stale-edit, server-down and unexpected-error states with retry actions and correlation ID. Error focus/aria-live status and keyboard tab order must work in browser and WebView2.
5. Home shows setup tasks and meaningful daily sales/stock/AR/AP/cash summaries only as their APIs land; do not invent dashboard figures or completion percentages. Native pending money is visibly excluded from posted revenue.

Apply the specific D01–D17 policies relevant to this slice. Finish the narrow route/model/client registration in B, record generated migration paths, and preserve a safe stop/fix-forward path before presenting the diff for review.

### E. Focused tests

**NEW file:** `TestQtsBilling/Ui/SharedShellTests.cs`. Name the class `SharedShellTests` in the directory-derived namespace specified in §6.3.

- **T30.01:** Given Viewer/Cashier/Accountant, when the task-specific command or transition is exercised, then navigation differs correctly and direct unauthorized URLs still fail server checks.
- **T30.02:** Given 250 customers at a 100-row page limit, when the task-specific command or transition is exercised, then all pages are reachable with stable ordering.
- **T30.03:** Given slow old search returning after new search, when the task-specific command or transition is exercised, then only the newest result is displayed.
- **T30.04:** Given API denial/error/empty data, when the task-specific command or transition is exercised, then the page presents the distinct accessible state, not an empty success.
- **T30.05:** Given native offline mode, when the task-specific command or transition is exercised, then local capture/review remains reachable while online-only controls explain their restriction.

Reuse existing or earlier-task fixtures. For a mutation, also assert denied tenant/location/permission and injected pre-commit failure leave zero partial financial/audit effects; for a read/UI-only task assert no mutation. Reuse the same fixture rather than duplicating equivalent tests.

### F. Verification

**Status: NOT RUN - SPEC_ONLY.** The following filter becomes executable only after the NEW test class and prerequisites are implemented.

Requires the owning APIs, shared UI smoke/Playwright setup and disposable native data. The actual Windows/WebView2 checks remain a separate platform gate.

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~TestQtsBilling.Ui.SharedShellTests"
```

**PASS:** owning cases and relevant existing regression filters pass, with exact committed-state assertions where applicable; no skipped required prerequisite, new unreviewed dependency, secret leak or `git diff --check` failure. A build or mocked response alone is not financial proof.

Select the checked server or Windows build command in §6.3 only when this task changes that host. Full multi-platform regression belongs in CI/release, not every local edit.

### G. Definition of done

Users can find authorized workflows and navigate complete data sets without treating navigation visibility as authorization. Evidence includes the exact implementation SHA, focused test outcome and minimal reviewed diff. All new constraints/contracts/permissions/host wiring ship together under **G5**; unresolved external prerequisites stay explicit.

No unnecessary dependency or compatibility path remains. Recovery preserves historical facts and pending money. Record remaining blockers; spec completion is not implementation completion or owner production approval.

<a id="task-31"></a>
## Atomic Task 31 - Complete customer and supplier master terms and native party identity

### A. Objective

**Priority:** P1. **Requirements:** `R11`, `R12`, `R13`. **Findings:** `F30`, `F34`, `F43`.
**Dependencies:** [Task 04](#task-04), [Task 05](#task-05), [Task 08](#task-08), [Task 11](#task-11) **Integration gate:** G5.

**Current → target:** Basic customer/supplier APIs exist but do not provide a complete trade-credit, contact and open-item context for SMB workflows. → Customers and suppliers can be maintained, deactivated and selected safely, with deterministic terms and accounting-party links.

**Acceptance:** The target behavior is observable through the owning API/UI/store and every applicable scenario in E passes; release additionally requires the named gate, not just this file diff.

**Non-goals:** No automatic party merge, mutable balance CRUD or unrestricted sensitive personal fields.

### B. Scope

| Action | Exact path | Change / exclusion |
|---|---|---|
| MODIFY | `QtsWebApp/QtsWebApp/Features/Customers/CustomerEndpoints.cs` | Versioned active/terms/credit-control updates and safe party reads. |
| MODIFY | `QtsWebApp/QtsWebApp/Features/Purchasing/PurchasingEndpoints.cs` | Supplier terms/status and bounded detail/search extension. |
| MODIFY | `QtsBilling.Persistence.Postgres/Entities/MasterData.cs` | Party code, legal/tax/contact details, payment terms and controlled credit limit. |
| GENERATE — NEW | `QtsBilling.Persistence.Postgres/Migrations/` | NEW PartyTradingTerms with scoped uniqueness and credit-policy fields. |
| CREATE — NEW test | `TestQtsBilling/Integration/PartyMasterTests.cs` | Owning focused assertions below; reuse fixtures rather than duplicate a test infrastructure. |

MODIFY existing `QtsBilling.Persistence.Postgres/BillingDbContext.cs` and the appropriate checked `BillingDbContext.Model.cs` / `BillingDbContext.Model.Financial.cs` only for the listed model; GENERATE designer/snapshot companions with the named new migration.

MODIFY existing `QtsWebApp/QtsWebApp/Program.cs` only for required route/service registration, and `QtsBilling.Contracts/Identity/IdentityDtos.cs` / `QtsWebApp/QtsWebApp/Infrastructure/QtsIdentity.cs` only for this task's explicit D02 policies.

All other production paths are excluded except the narrow, explicitly enumerated [D17 integration bindings](#d17). Applied migrations, posted facts and unresolved cash are never edited/deleted by this scope.

### C. Verified anchors and NEW insertions

- **EXISTING / VERIFIED:** [QtsBilling.Persistence.Postgres/BillingDbContext.Model.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Persistence.Postgres/BillingDbContext.Model.cs): `ConfigureCore; ConfigureMasterData; ConfigureCatalog; ConfigureInventory`. [E06](#e-e06).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Purchasing/PurchasingEndpoints.cs — source lines 1-120](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Purchasing/PurchasingEndpoints.cs#L1-L120): `MapPurchasingEndpoints`. [E28](#e-e28).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Invoices/InvoiceEndpoints.cs — source lines 1-110](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Invoices/InvoiceEndpoints.cs#L1-L110): `MapInvoiceEndpoints`. [E31](#e-e31).

All CREATE/GENERATE symbols, routes and constraints in B/D are **NEW**. Source presence is not runtime proof; do not infer executed results from old evidence prose.

### D. Ordered implementation recipe

1. Reuse current IDs/CRUD and soft-deactivation behavior. Add tenant-unique optional code, legal name/tax identifier, billing/shipping/contact data and native payment terms expressed as due-days with bounded validation.
2. CreditLimit and credit-hold changes require dedicated financial authority and a reason; default no on-account credit until configured. Snapshot agreed terms onto issued documents so later master changes do not alter history.
3. Use If-Match/Version on updates, normalized search fields and scoped uniqueness. Do not require unique phone/email as a universal business identity without the explicit current policy; review duplicates in native preview instead of silently merging parties.
4. Expose party summary/detail contracts including active state and links to open items supplied by Tasks 39/45. Never maintain a mutable customer/supplier balance on the master.
5. Write audit plus appropriate current-device projection in the same transaction. Inactive parties remain resolvable in history but cannot be selected for a new trade without authorized reactivation.

Apply the specific D01–D17 policies relevant to this slice. Finish the narrow route/model/client registration in B, record generated migration paths, and preserve a safe stop/fix-forward path before presenting the diff for review.

### E. Focused tests

**NEW file:** `TestQtsBilling/Integration/PartyMasterTests.cs`. Name the class `PartyMasterTests` in the directory-derived namespace specified in §6.3.

- **T31.01:** Given party terms changed after invoice issue, when the task-specific command or transition is exercised, then the old invoice due date/legal snapshot does not change.
- **T31.02:** Given concurrent master edits, when the task-specific command or transition is exercised, then stale version fails without lost updates.
- **T31.03:** Given a cashier attempting to raise credit limit, when the task-specific command or transition is exercised, then the API denies it.
- **T31.04:** Given a deactivated party, when the task-specific command or transition is exercised, then history resolves but new trading requires reactivation.
- **T31.05:** Given two tenants with equal party codes, when the task-specific command or transition is exercised, then both are valid without cross-business leakage.

Reuse existing or earlier-task fixtures. For a mutation, also assert denied tenant/location/permission and injected pre-commit failure leave zero partial financial/audit effects; for a read/UI-only task assert no mutation. Reuse the same fixture rather than duplicating equivalent tests.

### F. Verification

**Status: NOT RUN - SPEC_ONLY.** The following filter becomes executable only after the NEW test class and prerequisites are implemented.

Requires Task 01's isolated PostgreSQL 18/API fixture, .NET 10 SDK and explicitly validated QTS_TEST_POSTGRES_CONNECTION. Fixture DDL/data writes are permitted only in a later authorized test run; never inherit production credentials.

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~TestQtsBilling.Integration.PartyMasterTests"
```

**PASS:** owning cases and relevant existing regression filters pass, with exact committed-state assertions where applicable; no skipped required prerequisite, new unreviewed dependency, secret leak or `git diff --check` failure. A build or mocked response alone is not financial proof.

Select the checked server or Windows build command in §6.3 only when this task changes that host. Full multi-platform regression belongs in CI/release, not every local edit.

### G. Definition of done

Customers and suppliers can be maintained, deactivated and selected safely, with deterministic terms and accounting-party links. Evidence includes the exact implementation SHA, focused test outcome and minimal reviewed diff. All new constraints/contracts/permissions/host wiring ship together under **G5**; unresolved external prerequisites stay explicit.

No unnecessary dependency or compatibility path remains. Recovery preserves historical facts and pending money. Record remaining blockers; spec completion is not implementation completion or owner production approval.

<a id="task-32"></a>
## Atomic Task 32 - Expose complete native catalog and pricing master workflows

### A. Objective

**Priority:** P1. **Requirements:** `R05`, `R11`. **Findings:** `F13`, `F30`, `F43`.
**Dependencies:** [Task 03](#task-03), [Task 04](#task-04), [Task 05](#task-05), [Task 08](#task-08), [Task 11](#task-11), [Task 26](#task-26) **Integration gate:** G5.

**Current → target:** Several catalog masters exist only as entities, while tax/discount definitions are not a complete controlled pricing-selection journey. → All supported masters are editable through scoped APIs with clear tax/discount semantics and safe category hierarchy.

**Acceptance:** The target behavior is observable through the owning API/UI/store and every applicable scenario in E passes; release additionally requires the named gate, not just this file diff.

**Non-goals:** No promotion engine, coupons, arbitrary formula language or jurisdiction-specific filing integration.

### B. Scope

| Action | Exact path | Change / exclusion |
|---|---|---|
| CREATE — NEW | `QtsWebApp/QtsWebApp/Features/Catalog/CatalogMasterEndpoints.cs` | Units, categories, brands and product-group management. |
| MODIFY | `QtsWebApp/QtsWebApp/Features/Pricing/PricingEndpoints.cs` | Versioned bounded tax/discount master rules and audit/projections. |
| CREATE — NEW | `QtsBilling.Contracts/Catalog/CatalogMasterDtos.cs` | Typed master requests/results and lookup client contract. |
| GENERATE — NEW | `QtsBilling.Persistence.Postgres/Migrations/` | NEW CatalogMasterPolicy indexes/parent constraints where missing. |
| CREATE — NEW test | `TestQtsBilling/Integration/CatalogMasterTests.cs` | Owning focused assertions below; reuse fixtures rather than duplicate a test infrastructure. |

MODIFY existing `QtsWebApp/QtsWebApp/Program.cs` only for required route/service registration, and `QtsBilling.Contracts/Identity/IdentityDtos.cs` / `QtsWebApp/QtsWebApp/Infrastructure/QtsIdentity.cs` only for this task's explicit D02 policies.

All other production paths are excluded except the narrow, explicitly enumerated [D17 integration bindings](#d17). Applied migrations, posted facts and unresolved cash are never edited/deleted by this scope.

### C. Verified anchors and NEW insertions

- **EXISTING / VERIFIED:** [QtsBilling.Persistence.Postgres/BillingDbContext.Model.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Persistence.Postgres/BillingDbContext.Model.cs): `ConfigureCore; ConfigureMasterData; ConfigureCatalog; ConfigureInventory`. [E06](#e-e06).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Sales/SalePosting.cs — source lines 1-310](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Sales/SalePosting.cs#L1-L310): `ValidatePayload; ResolveContextAsync; PriceLines; ValidatePayment`. [E11](#e-e11).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Sync/SyncChangeEndpoints.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Sync/SyncChangeEndpoints.cs): `Change-feed endpoint`. [E17](#e-e17).
- **EXISTING / VERIFIED:** [docs/migration/decisions.md — source lines 1-155](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/docs/migration/decisions.md#L1-L155): `D-01...D-41 and historical decisions`. [E44](#e-e44).

All CREATE/GENERATE symbols, routes and constraints in B/D are **NEW**. Source presence is not runtime proof; do not infer executed results from old evidence prose.

### D. Ordered implementation recipe

1. Add GET/list/detail/create/update/deactivate for units/categories/brands/groups at /api/v1/catalog-masters/{resource}; use explicit typed resource handlers, not arbitrary table CRUD. All writes require catalog management permission and Version.
2. Category hierarchy rejects self/descendant cycles, cross-tenant parents and excessive depth; sibling-name uniqueness is scoped by parent, not a global prohibition on the same label in different branches. Define native maximum depth eight and return a clear validation error.
3. Units declare base dimension and decimal permission; pack conversions are Task 33. Deactivate referenced masters rather than deleting history; selecting inactive values in new documents is refused.
4. Tax fractions use six decimals in [0,1], explicit inclusive/exclusive product mode, effective activation and only one default. No automatic tax-law assertion; issue/checkout snapshots the applied definition.
5. Discounts are simple fixed/percentage definitions with bounded window and explicit policy, no stacking by default. Cashier discounts default disabled; granted thresholds are server enforced. UTC schedules use business-local form conversion and [start,end) boundaries. Persist projection/audit transactionally.

Apply the specific D01–D17 policies relevant to this slice. Finish the narrow route/model/client registration in B, record generated migration paths, and preserve a safe stop/fix-forward path before presenting the diff for review.

### E. Focused tests

**NEW file:** `TestQtsBilling/Integration/CatalogMasterTests.cs`. Name the class `CatalogMasterTests` in the directory-derived namespace specified in §6.3.

- **T32.01:** Given category cycle or foreign parent, when the task-specific command or transition is exercised, then validation and scoped constraints refuse it.
- **T32.02:** Given the same category name under different parents, when the task-specific command or transition is exercised, then both are allowed and selection returns the correct hierarchy.
- **T32.03:** Given two concurrent default-tax changes, when the task-specific command or transition is exercised, then one effective default remains.
- **T32.04:** Given unauthorized, expired or excessive discount, when the task-specific command or transition is exercised, then checkout does not apply it.
- **T32.05:** Given inactive referenced master, when the task-specific command or transition is exercised, then old documents render while new use is refused.

Reuse existing or earlier-task fixtures. For a mutation, also assert denied tenant/location/permission and injected pre-commit failure leave zero partial financial/audit effects; for a read/UI-only task assert no mutation. Reuse the same fixture rather than duplicating equivalent tests.

### F. Verification

**Status: NOT RUN - SPEC_ONLY.** The following filter becomes executable only after the NEW test class and prerequisites are implemented.

Requires Task 01's isolated PostgreSQL 18/API fixture, .NET 10 SDK and explicitly validated QTS_TEST_POSTGRES_CONNECTION. Fixture DDL/data writes are permitted only in a later authorized test run; never inherit production credentials.

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~TestQtsBilling.Integration.CatalogMasterTests"
```

**PASS:** owning cases and relevant existing regression filters pass, with exact committed-state assertions where applicable; no skipped required prerequisite, new unreviewed dependency, secret leak or `git diff --check` failure. A build or mocked response alone is not financial proof.

Select the checked server or Windows build command in §6.3 only when this task changes that host. Full multi-platform regression belongs in CI/release, not every local edit.

### G. Definition of done

All supported masters are editable through scoped APIs with clear tax/discount semantics and safe category hierarchy. Evidence includes the exact implementation SHA, focused test outcome and minimal reviewed diff. All new constraints/contracts/permissions/host wiring ship together under **G5**; unresolved external prerequisites stay explicit.

No unnecessary dependency or compatibility path remains. Recovery preserves historical facts and pending money. Record remaining blockers; spec completion is not implementation completion or owner production approval.

<a id="task-33"></a>
## Atomic Task 33 - Implement explicit variants, sell units, pack conversions and barcode lookup

### A. Objective

**Priority:** P1. **Requirements:** `R05`, `R07`, `R11`. **Findings:** `F13`, `F15`, `F30`.
**Dependencies:** [Task 03](#task-03), [Task 05](#task-05), [Task 12](#task-12), [Task 14](#task-14), [Task 15](#task-15), [Task 32](#task-32) **Integration gate:** G5.

**Current → target:** The current API creates a default variant and edits prices, but does not expose a complete variant and unit-conversion workflow. → Retail/wholesale users can sell each, box, case or measured quantities while stock remains in one explicit base unit.

**Acceptance:** The target behavior is observable through the owning API/UI/store and every applicable scenario in E passes; release additionally requires the named gate, not just this file diff.

**Non-goals:** No assembly/BOM, advanced manufacturing, automatic promotion or new currency support.

### B. Scope

| Action | Exact path | Change / exclusion |
|---|---|---|
| MODIFY | `QtsWebApp/QtsWebApp/Features/Catalog/ProductEndpoints.cs` | Complete product/variant lifecycle and server-authoritative sell-unit selection. |
| MODIFY | `QtsBilling.Persistence.Postgres/Entities/Catalog.cs` | Variant identity, base-unit and per-variant sell-unit conversion/price definitions. |
| MODIFY | `QtsBilling.Contracts/Catalog/ProductDtos.cs` | Native product/variant/sell-unit details, tax mode and versioned lookup contract. |
| GENERATE — NEW | `QtsBilling.Persistence.Postgres/Migrations/` | NEW VariantSellUnits with scoped barcodes and positive exact conversion constraints. |
| CREATE — NEW test | `TestQtsBilling/Integration/SellUnitTests.cs` | Owning focused assertions below; reuse fixtures rather than duplicate a test infrastructure. |

MODIFY existing `QtsBilling.Persistence.Postgres/BillingDbContext.cs` and the appropriate checked `BillingDbContext.Model.cs` / `BillingDbContext.Model.Financial.cs` only for the listed model; GENERATE designer/snapshot companions with the named new migration.

MODIFY existing `QtsWebApp/QtsWebApp/Program.cs` only for required route/service registration, and `QtsBilling.Contracts/Identity/IdentityDtos.cs` / `QtsWebApp/QtsWebApp/Infrastructure/QtsIdentity.cs` only for this task's explicit D02 policies.

All other production paths are excluded except the narrow, explicitly enumerated [D17 integration bindings](#d17). Applied migrations, posted facts and unresolved cash are never edited/deleted by this scope.

### C. Verified anchors and NEW insertions

- **EXISTING / VERIFIED:** [QtsBilling.Persistence.Postgres/BillingDbContext.Model.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Persistence.Postgres/BillingDbContext.Model.cs): `ConfigureCore; ConfigureMasterData; ConfigureCatalog; ConfigureInventory`. [E06](#e-e06).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Sales/SalePosting.cs — source lines 1-310](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Sales/SalePosting.cs#L1-L310): `ValidatePayload; ResolveContextAsync; PriceLines; ValidatePayment`. [E11](#e-e11).
- **EXISTING / VERIFIED:** [QtsBilling.Ui/Pages/Pos.razor.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Ui/Pages/Pos.razor.cs): `LineTax; LineTotal; CaptureAsync`. [E26](#e-e26).

All CREATE/GENERATE symbols, routes and constraints in B/D are **NEW**. Source presence is not runtime proof; do not infer executed results from old evidence prose.

### D. Ordered implementation recipe

1. Keep product/variant identity separate from sell-unit definitions. A variant has one base unit and zero or more named sell units with positive exact BaseQuantityPerUnit and price/barcode; labels such as box/card/bottle are data, not new enum branches.
2. Create/update/deactivate additional variants with scoped SKU/barcode uniqueness and exactly one active default. Refuse unsupported modifier/combo behavior rather than advertising an unimplemented product type.
3. Convert requested sell quantity to base quantity with checked decimal arithmetic at six places. Reject quantities incompatible with the unit fractional policy or representability; snapshot sell unit, factor, price and resulting base quantity on every commercial/stock line.
4. Preserve conversion history: changing a conversion creates a new effective version for future documents; it never changes posted lines or pending offline operations. Stock valuation/COGS uses base quantities; purchasing and returns convert with original snapshot factors.
5. Extend device projections with complete native tax mode/units/version and bounded barcode lookup; no N+1 query per item at checkout. Validate stale price/conversion under the posting transaction.

Apply the specific D01–D17 policies relevant to this slice. Finish the narrow route/model/client registration in B, record generated migration paths, and preserve a safe stop/fix-forward path before presenting the diff for review.

### E. Focused tests

**NEW file:** `TestQtsBilling/Integration/SellUnitTests.cs`. Name the class `SellUnitTests` in the directory-derived namespace specified in §6.3.

- **T33.01:** Given 3 cases of 12 each, when the task-specific command or transition is exercised, then stock changes by 36 base units and price is the selected case price.
- **T33.02:** Given fractional kilogram vs non-fractional each, when the task-specific command or transition is exercised, then only valid base quantities are accepted.
- **T33.03:** Given conversion changed after sale, when the task-specific command or transition is exercised, then refund uses the original conversion and value.
- **T33.04:** Given duplicate barcode in the same tenant or inactive variant, when the task-specific command or transition is exercised, then new checkout is refused.
- **T33.05:** Given the same variant twice through different sell units, when the task-specific command or transition is exercised, then normalized base quantity locks prevent oversell.

Reuse existing or earlier-task fixtures. For a mutation, also assert denied tenant/location/permission and injected pre-commit failure leave zero partial financial/audit effects; for a read/UI-only task assert no mutation. Reuse the same fixture rather than duplicating equivalent tests.

### F. Verification

**Status: NOT RUN - SPEC_ONLY.** The following filter becomes executable only after the NEW test class and prerequisites are implemented.

Requires Task 01's isolated PostgreSQL 18/API fixture, .NET 10 SDK and explicitly validated QTS_TEST_POSTGRES_CONNECTION. Fixture DDL/data writes are permitted only in a later authorized test run; never inherit production credentials.

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~TestQtsBilling.Integration.SellUnitTests"
```

**PASS:** owning cases and relevant existing regression filters pass, with exact committed-state assertions where applicable; no skipped required prerequisite, new unreviewed dependency, secret leak or `git diff --check` failure. A build or mocked response alone is not financial proof.

Select the checked server or Windows build command in §6.3 only when this task changes that host. Full multi-platform regression belongs in CI/release, not every local edit.

### G. Definition of done

Retail/wholesale users can sell each, box, case or measured quantities while stock remains in one explicit base unit. Evidence includes the exact implementation SHA, focused test outcome and minimal reviewed diff. All new constraints/contracts/permissions/host wiring ship together under **G5**; unresolved external prerequisites stay explicit.

No unnecessary dependency or compatibility path remains. Recovery preserves historical facts and pending money. Record remaining blockers; spec completion is not implementation completion or owner production approval.

<a id="task-34"></a>
## Atomic Task 34 - Deliver complete catalog, master and trading-party screens

### A. Objective

**Priority:** P1. **Requirements:** `R11`, `R17`. **Findings:** `F30`, `F43`.
**Dependencies:** [Task 30](#task-30), [Task 31](#task-31), [Task 32](#task-32), [Task 33](#task-33) **Integration gate:** G5.

**Current → target:** Business APIs and schema foundations are not accompanied by usable management screens. → An owner or authorized manager can set up a sellable catalog and trading parties without SQL, API tooling or source edits.

**Acceptance:** The target behavior is observable through the owning API/UI/store and every applicable scenario in E passes; release additionally requires the named gate, not just this file diff.

**Non-goals:** This is a must-ship-together UI slice; four production pages are justified by one complete setup journey. Do not replace the RCL design system.

### B. Scope

| Action | Exact path | Change / exclusion |
|---|---|---|
| CREATE — NEW | `QtsBilling.Ui/Pages/Catalog/Products.razor` | Product list, search, bounded paging and detail entry point. |
| CREATE — NEW | `QtsBilling.Ui/Pages/Catalog/ProductEditor.razor` | Product/variant/unit/tax editor with precise validation and snapshots explanation. |
| CREATE — NEW | `QtsBilling.Ui/Pages/Catalog/Masters.razor` | Supported typed category/unit/brand/group/tax/discount maintenance. |
| CREATE — NEW | `QtsBilling.Ui/Pages/Parties/TradingParties.razor` | Customer/supplier detail, terms, deactivation and account links. |
| CREATE — NEW test | `TestQtsBilling/Ui/CatalogPartyJourneyTests.cs` | Owning focused assertions below; reuse fixtures rather than duplicate a test infrastructure. |

Apply only the exact typed-client/host registration and routing bindings in [D17](#d17); no unlisted feature is included. A new page cannot be marked complete before its owning API is wired.

All other production paths are excluded except the narrow, explicitly enumerated [D17 integration bindings](#d17). Applied migrations, posted facts and unresolved cash are never edited/deleted by this scope.

### C. Verified anchors and NEW insertions

- **EXISTING / VERIFIED:** [QtsBilling.Ui/Pages/Pos.razor.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Ui/Pages/Pos.razor.cs): `LineTax; LineTotal; CaptureAsync`. [E26](#e-e26).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Purchasing/PurchasingEndpoints.cs — source lines 1-120](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Purchasing/PurchasingEndpoints.cs#L1-L120): `MapPurchasingEndpoints`. [E28](#e-e28).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Invoices/InvoiceEndpoints.cs — source lines 1-110](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Invoices/InvoiceEndpoints.cs#L1-L110): `MapInvoiceEndpoints`. [E31](#e-e31).
- **EXISTING / VERIFIED:** [README.md — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/README.md): `Architecture / status / project descriptions`. [E48](#e-e48).

All CREATE/GENERATE symbols, routes and constraints in B/D are **NEW**. Source presence is not runtime proof; do not infer executed results from old evidence prose.

### D. Ordered implementation recipe

1. Add explicit routes /products, /products/new, /products/{id}, /catalog/masters and /suppliers; retain /customers through the existing page and link shared party detail. Consume typed clients created with the owning API tasks.
2. Use a focused general/product tab and explicit variants/sell units/pricing tabs; show base stock unit separately from selling label/pack size. Category selection displays hierarchy with optional descendants, clear inactive/current values and keyboard selection.
3. Bind permissions to available actions, preserve versions and show field errors beside inputs. Unsaved edits, duplicate SKU/barcode and stale conflicts require actionable retry/reload without overwriting another edit.
4. Implement search/paging, add/edit/reactivate/deactivate and related-document drilldown. Master deactivation clearly states historical documents are preserved. Online-only changes are disabled while offline with an explanation.
5. Every page needs empty/loading/denied/error/success states, persistent labels, keyboard focus and responsive layout. No placeholder Save buttons; all tabs in this gate must be connected end to end.

Apply the specific D01–D17 policies relevant to this slice. Finish the narrow route/model/client registration in B, record generated migration paths, and preserve a safe stop/fix-forward path before presenting the diff for review.

### E. Focused tests

**NEW file:** `TestQtsBilling/Ui/CatalogPartyJourneyTests.cs`. Name the class `CatalogPartyJourneyTests` in the directory-derived namespace specified in §6.3.

- **T34.01:** Given a new business, when the task-specific command or transition is exercised, then users can create units/category/product/variant/pack and a customer/supplier entirely through UI.
- **T34.02:** Given stale edit or invalid price/conversion, when the task-specific command or transition is exercised, then the form preserves input and displays server field errors.
- **T34.03:** Given Viewer access, when the task-specific command or transition is exercised, then reads work and edit actions/direct routes are denied appropriately.
- **T34.04:** Given hundreds of products, when the task-specific command or transition is exercised, then searching and subsequent pages expose all matching records.
- **T34.05:** Given keyboard-only usage in browser and Windows, when the task-specific command or transition is exercised, then editors and hierarchy selection are operable with visible focus.

Reuse existing or earlier-task fixtures. For a mutation, also assert denied tenant/location/permission and injected pre-commit failure leave zero partial financial/audit effects; for a read/UI-only task assert no mutation. Reuse the same fixture rather than duplicating equivalent tests.

### F. Verification

**Status: NOT RUN - SPEC_ONLY.** The following filter becomes executable only after the NEW test class and prerequisites are implemented.

Requires the owning APIs, shared UI smoke/Playwright setup and disposable native data. The actual Windows/WebView2 checks remain a separate platform gate.

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~TestQtsBilling.Ui.CatalogPartyJourneyTests"
```

**PASS:** owning cases and relevant existing regression filters pass, with exact committed-state assertions where applicable; no skipped required prerequisite, new unreviewed dependency, secret leak or `git diff --check` failure. A build or mocked response alone is not financial proof.

Select the checked server or Windows build command in §6.3 only when this task changes that host. Full multi-platform regression belongs in CI/release, not every local edit.

### G. Definition of done

An owner or authorized manager can set up a sellable catalog and trading parties without SQL, API tooling or source edits. Evidence includes the exact implementation SHA, focused test outcome and minimal reviewed diff. All new constraints/contracts/permissions/host wiring ship together under **G5**; unresolved external prerequisites stay explicit.

No unnecessary dependency or compatibility path remains. Recovery preserves historical facts and pending money. Record remaining blockers; spec completion is not implementation completion or owner production approval.

<a id="task-35"></a>
## Atomic Task 35 - Enable real online POS checkout in browser and desktop

### A. Objective

**Priority:** P1. **Requirements:** `R05`, `R07`, `R08`, `R11`, `R17`. **Findings:** `F11`, `F12`, `F13`, `F29`, `F30`.
**Dependencies:** [Task 12](#task-12), [Task 13](#task-13), [Task 14](#task-14), [Task 15](#task-15), [Task 17](#task-17), [Task 29](#task-29), [Task 30](#task-30), [Task 33](#task-33), [Task 34](#task-34) **Integration gate:** G5.

**Current → target:** The browser registers a truthful local-capture stub, so its POS is not a usable online sales screen. → Both hosts support online sales through the same authoritative API; Windows additionally supports explicit provisional offline cash mode.

**Acceptance:** The target behavior is observable through the owning API/UI/store and every applicable scenario in E passes; release additionally requires the named gate, not just this file diff.

**Non-goals:** No automatic cross-mode fallback, browser offline database, PSP integration or silent price substitution.

### B. Scope

| Action | Exact path | Change / exclusion |
|---|---|---|
| CREATE — NEW | `QtsBilling.Contracts/Sales/SaleApiClient.cs` | Typed online quote/capture/result/history client. |
| MODIFY | `QtsBilling.Ui/Pages/Pos.razor` | Shared online/offline mode, cart, tender, confirmation and final result UI. |
| MODIFY | `QtsBilling.Ui/Pages/Pos.razor.cs` | Deterministic checkout state machine and native online posting. |
| MODIFY | `QtsWebApp/QtsWebApp/Features/Pos/WebLocalPosService.cs` | Retain explicit local-offline refusal; online service is separate and functional. |
| CREATE — NEW test | `TestQtsBilling/Ui/OnlinePosJourneyTests.cs` | Owning focused assertions below; reuse fixtures rather than duplicate a test infrastructure. |

Apply only the exact typed-client/host registration and routing bindings in [D17](#d17); no unlisted feature is included. A new page cannot be marked complete before its owning API is wired.

All other production paths are excluded except the narrow, explicitly enumerated [D17 integration bindings](#d17). Applied migrations, posted facts and unresolved cash are never edited/deleted by this scope.

### C. Verified anchors and NEW insertions

- **EXISTING / VERIFIED:** [QtsBilling.Ui/Pages/Pos.razor.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Ui/Pages/Pos.razor.cs): `LineTax; LineTotal; CaptureAsync`. [E26](#e-e26).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Pos/WebLocalPosService.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Pos/WebLocalPosService.cs): `ILocalPosService implementation`. [E27](#e-e27).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Sales/SalePosting.cs — source lines 1-310](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Sales/SalePosting.cs#L1-L310): `ValidatePayload; ResolveContextAsync; PriceLines; ValidatePayment`. [E11](#e-e11).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Sales/SalePosting.cs — source lines 310-665](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Sales/SalePosting.cs#L310-L665): `PostAsync / posting transaction and revenue journal`. [E12](#e-e12).

All CREATE/GENERATE symbols, routes and constraints in B/D are **NEW**. Source presence is not runtime proof; do not infer executed results from old evidence prose.

### D. Ordered implementation recipe

1. Add a distinct online feature client rather than pretending the local service supports the browser. Register it in both hosts and route the shared page to the correct declared capability.
2. Implement scan/search product, sell unit/quantity, customer, authorized discount, stock/price review, register/session selection and cash/approved noncash/on-account tender. Show tax/net/gross/change and credit exposure clearly.
3. Use checkout states Editing → Validating → Submitting → Posted, with Conflict/OutcomeUnknown. Retain one scoped operation/key and immutable submission until its outcome is resolved; query/retry original key after timeout.
4. Never switch an uncertain online request into a new offline capture. Only a fresh, explicitly selected offline cash checkout with a valid grant enters Task 29. A changed server price/stock/conversion requires review and a new confirmed command after resolving the old one.
5. On success show final stored receipt/number and history link; clear cart only after known committed result. Lost receipt rendering never triggers another sale. Implement keyboard shortcuts with documented conflicts/focus and no card option.

Apply the specific D01–D17 policies relevant to this slice. Finish the narrow route/model/client registration in B, record generated migration paths, and preserve a safe stop/fix-forward path before presenting the diff for review.

### E. Focused tests

**NEW file:** `TestQtsBilling/Ui/OnlinePosJourneyTests.cs`. Name the class `OnlinePosJourneyTests` in the directory-derived namespace specified in §6.3.

- **T35.01:** Given browser POS, when the task-specific command or transition is exercised, then catalog/search and a cash sale produce a final server receipt rather than NotSupported.
- **T35.02:** Given server commits but response is lost, when the task-specific command or transition is exercised, then retry returns the same sale and the cart is not independently captured offline.
- **T35.03:** Given changed price/stock, when the task-specific command or transition is exercised, then user must review the precise conflict before resubmitting.
- **T35.04:** Given Windows explicit offline mode, when the task-specific command or transition is exercised, then only provisional cash capture is available with a valid grant.
- **T35.05:** Given duplicate clicks/scans/enter key, when the task-specific command or transition is exercised, then quantity intent is clear and submission has one business effect.

Reuse existing or earlier-task fixtures. For a mutation, also assert denied tenant/location/permission and injected pre-commit failure leave zero partial financial/audit effects; for a read/UI-only task assert no mutation. Reuse the same fixture rather than duplicating equivalent tests.

### F. Verification

**Status: NOT RUN - SPEC_ONLY.** The following filter becomes executable only after the NEW test class and prerequisites are implemented.

Requires the owning APIs, shared UI smoke/Playwright setup and disposable native data. The actual Windows/WebView2 checks remain a separate platform gate.

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~TestQtsBilling.Ui.OnlinePosJourneyTests"
```

**PASS:** owning cases and relevant existing regression filters pass, with exact committed-state assertions where applicable; no skipped required prerequisite, new unreviewed dependency, secret leak or `git diff --check` failure. A build or mocked response alone is not financial proof.

Select the checked server or Windows build command in §6.3 only when this task changes that host. Full multi-platform regression belongs in CI/release, not every local edit.

### G. Definition of done

Both hosts support online sales through the same authoritative API; Windows additionally supports explicit provisional offline cash mode. Evidence includes the exact implementation SHA, focused test outcome and minimal reviewed diff. All new constraints/contracts/permissions/host wiring ship together under **G5**; unresolved external prerequisites stay explicit.

No unnecessary dependency or compatibility path remains. Recovery preserves historical facts and pending money. Record remaining blockers; spec completion is not implementation completion or owner production approval.

<a id="task-36"></a>
## Atomic Task 36 - Complete register, sale history, receipt and custody screens

### A. Objective

**Priority:** P1. **Requirements:** `R09`, `R11`, `R14`, `R17`. **Findings:** `F28`, `F30`, `F31`.
**Dependencies:** [Task 17](#task-17), [Task 30](#task-30), [Task 35](#task-35) **Integration gate:** G5.

**Current → target:** Register, receipt and refund APIs have no complete daily operator workbench. → Cashiers and managers can complete a trading day and explain every settled/pending/rejected cash amount.

**Acceptance:** The target behavior is observable through the owning API/UI/store and every applicable scenario in E passes; release additionally requires the named gate, not just this file diff.

**Non-goals:** No specialized printer-driver dependency; standard print and keyboard-wedge barcode support is the base scope.

### B. Scope

| Action | Exact path | Change / exclusion |
|---|---|---|
| CREATE — NEW | `QtsBilling.Ui/Pages/Sales/RegisterSessions.razor` | Open/fund/manage/count/close drawer with unresolved money visibility. |
| CREATE — NEW | `QtsBilling.Ui/Pages/Sales/SalesHistory.razor` | Paged sales/detail, settlement, original receipt and return entry point. |
| CREATE — NEW | `QtsBilling.Ui/Pages/Sales/ReceiptView.razor` | Stored final/provisional receipt display, safe print and reprint evidence. |
| CREATE — NEW test | `TestQtsBilling/Ui/RegisterHistoryTests.cs` | Owning focused assertions below; reuse fixtures rather than duplicate a test infrastructure. |

Apply only the exact typed-client/host registration and routing bindings in [D17](#d17); no unlisted feature is included. A new page cannot be marked complete before its owning API is wired.

All other production paths are excluded except the narrow, explicitly enumerated [D17 integration bindings](#d17). Applied migrations, posted facts and unresolved cash are never edited/deleted by this scope.

### C. Verified anchors and NEW insertions

- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Sales/CashRegisterEndpoints.cs — source lines 409-550](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Sales/CashRegisterEndpoints.cs#L409-L550): `AddMovementAsync`. [E33](#e-e33).
- **EXISTING / VERIFIED:** [QtsBilling.Ui/Pages/Pos.razor.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Ui/Pages/Pos.razor.cs): `LineTax; LineTotal; CaptureAsync`. [E26](#e-e26).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Sales/SalePosting.cs — source lines 310-665](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Sales/SalePosting.cs#L310-L665): `PostAsync / posting transaction and revenue journal`. [E12](#e-e12).

All CREATE/GENERATE symbols, routes and constraints in B/D are **NEW**. Source presence is not runtime proof; do not infer executed results from old evidence prose.

### D. Ordered implementation recipe

1. Use repaired cash-session endpoints for opening/funding, authorized paid-in/out, counted close and variance approval. Distinguish count from ledger adjustment and show why unresolved offline custody prevents final close.
2. Sales list filters business location/date/customer/status/receipt number with stable cursor paging. Details show original line/tax/unit/cost visibility by permission, settlement/open credit and related return/credit history.
3. Receipts use stored document snapshots. Render HTML safely and support browser/WebView print without re-posting; mark copies as reprints where operationally useful without changing original financial content.
4. Gate financial detail and reprint/refund actions by server permissions; cashiers should not see purchase cost/margin or unrestricted company-wide journals. Wire return entry to Task 37 when landed.
5. Show actionable offline, no-open-session, awaiting-reconciliation, stale-version and print-failure states. Cash custody remains visible after a rejected or refunded transaction; no empty table disguises an error.

Apply the specific D01–D17 policies relevant to this slice. Finish the narrow route/model/client registration in B, record generated migration paths, and preserve a safe stop/fix-forward path before presenting the diff for review.

### E. Focused tests

**NEW file:** `TestQtsBilling/Ui/RegisterHistoryTests.cs`. Name the class `RegisterHistoryTests` in the directory-derived namespace specified in §6.3.

- **T36.01:** Given a complete day, when the task-specific command or transition is exercised, then opening funding+sales+paid-in−paid-out−refunds equals expected closing cash.
- **T36.02:** Given rejected-held device cash, when the task-specific command or transition is exercised, then close shows its amount and cannot falsely finalize.
- **T36.03:** Given a reprint, when the task-specific command or transition is exercised, then no sale/payment/movement is created.
- **T36.04:** Given history beyond one page, when the task-specific command or transition is exercised, then filters and drilldowns remain accurate.
- **T36.05:** Given cashier vs accountant permissions, when the task-specific command or transition is exercised, then confidential cost/financial details follow policy in UI and API.

Reuse existing or earlier-task fixtures. For a mutation, also assert denied tenant/location/permission and injected pre-commit failure leave zero partial financial/audit effects; for a read/UI-only task assert no mutation. Reuse the same fixture rather than duplicating equivalent tests.

### F. Verification

**Status: NOT RUN - SPEC_ONLY.** The following filter becomes executable only after the NEW test class and prerequisites are implemented.

Requires the owning APIs, shared UI smoke/Playwright setup and disposable native data. The actual Windows/WebView2 checks remain a separate platform gate.

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~TestQtsBilling.Ui.RegisterHistoryTests"
```

**PASS:** owning cases and relevant existing regression filters pass, with exact committed-state assertions where applicable; no skipped required prerequisite, new unreviewed dependency, secret leak or `git diff --check` failure. A build or mocked response alone is not financial proof.

Select the checked server or Windows build command in §6.3 only when this task changes that host. Full multi-platform regression belongs in CI/release, not every local edit.

### G. Definition of done

Cashiers and managers can complete a trading day and explain every settled/pending/rejected cash amount. Evidence includes the exact implementation SHA, focused test outcome and minimal reviewed diff. All new constraints/contracts/permissions/host wiring ship together under **G5**; unresolved external prerequisites stay explicit.

No unnecessary dependency or compatibility path remains. Recovery preserves historical facts and pending money. Record remaining blockers; spec completion is not implementation completion or owner production approval.

<a id="task-37"></a>
## Atomic Task 37 - Implement capped partial returns, credits and refund settlement

### A. Objective

**Priority:** P1. **Requirements:** `R06`, `R07`, `R08`, `R12`. **Findings:** `F21`, `F30`, `F33`.
**Dependencies:** [Task 10](#task-10), [Task 11](#task-11), [Task 13](#task-13), [Task 15](#task-15), [Task 17](#task-17), [Task 35](#task-35), [Task 36](#task-36) **Integration gate:** G6.

**Current → target:** The current refund surface is full cash-sale refund only, unsuitable for partial retail returns and trade credit. → Each returned quantity and refunded amount is capped against original unreturned/paid facts, with exact original tax and cost.

**Acceptance:** The target behavior is observable through the owning API/UI/store and every applicable scenario in E passes; release additionally requires the named gate, not just this file diff.

**Non-goals:** No arbitrary refund without an original source or speculative card/provider functionality.

### B. Scope

| Action | Exact path | Change / exclusion |
|---|---|---|
| MODIFY | `QtsWebApp/QtsWebApp/Features/Sales/RefundEndpoints.cs` | Line-level partial returns and independent authorized refund settlement. |
| MODIFY | `QtsBilling.Persistence.Postgres/Entities/Sales.cs` | Return line/source cost/quantity and refund allocation identities. |
| CREATE — NEW | `QtsBilling.Contracts/Sales/SalesReturnDtos.cs` | Native line quantities, disposition, credit/refund requests/results. |
| GENERATE — NEW | `QtsBilling.Persistence.Postgres/Migrations/` | NEW PartialSalesReturns removing one-full-refund-only indexes through additive migration. |
| CREATE — NEW test | `TestQtsBilling/Integration/PartialSalesReturnTests.cs` | Owning focused assertions below; reuse fixtures rather than duplicate a test infrastructure. |

MODIFY existing `QtsBilling.Persistence.Postgres/BillingDbContext.cs` and the appropriate checked `BillingDbContext.Model.cs` / `BillingDbContext.Model.Financial.cs` only for the listed model; GENERATE designer/snapshot companions with the named new migration.

MODIFY existing `QtsWebApp/QtsWebApp/Program.cs` only for required route/service registration, and `QtsBilling.Contracts/Identity/IdentityDtos.cs` / `QtsWebApp/QtsWebApp/Infrastructure/QtsIdentity.cs` only for this task's explicit D02 policies.

All other production paths are excluded except the narrow, explicitly enumerated [D17 integration bindings](#d17). Applied migrations, posted facts and unresolved cash are never edited/deleted by this scope.

### C. Verified anchors and NEW insertions

- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Sales/SalePosting.cs — source lines 310-665](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Sales/SalePosting.cs#L310-L665): `PostAsync / posting transaction and revenue journal`. [E12](#e-e12).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Sync/SyncOperationEndpoints.cs — source lines 1-460](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Sync/SyncOperationEndpoints.cs#L1-L460): `ProcessAsync / operation and batch handlers`. [E15](#e-e15).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Invoices/InvoiceEndpoints.cs — source lines 360-650](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Invoices/InvoiceEndpoints.cs#L360-L650): `DeleteAsync; IssueAsync`. [E32](#e-e32).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Sales/CashRegisterEndpoints.cs — source lines 409-550](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Sales/CashRegisterEndpoints.cs#L409-L550): `AddMovementAsync`. [E33](#e-e33).

All CREATE/GENERATE symbols, routes and constraints in B/D are **NEW**. Source presence is not runtime proof; do not infer executed results from old evidence prose.

### D. Ordered implementation recipe

1. Create a return/credit document with original sale/invoice line IDs and positive return quantity. Lock original sources and cumulative allocations; cap quantity, tax, net and total against prior returns, including simultaneous requests.
2. Allocate original rounded discount/tax/value proportionally; the final return consumes the exact remaining original amount. Restock only the approved base quantity and original allocated cost; damaged/non-restockable items require reason and loss accounting.
3. Distinguish commercial credit from physical cash payout. A credit reduces AR or creates customer credit liability; a refund consumes only an available credit/settled entitlement and one permitted tender. No payment beyond original net paid less prior refunds.
4. Use distinct scoped keys for return and payout, one journal/source each, linked drawer movement for physical cash and audit. No provider I/O or card refund without a verified integration.
5. Extend history/UI to select original lines, explain restock/credit/payout, show cumulative remaining entitlement and permission approval. Preserve originals and historical reporting; never flip away the original economic event.

Apply the specific D01–D17 policies relevant to this slice. Finish the narrow route/model/client registration in B, record generated migration paths, and preserve a safe stop/fix-forward path before presenting the diff for review.

### E. Focused tests

**NEW file:** `TestQtsBilling/Integration/PartialSalesReturnTests.cs`. Name the class `PartialSalesReturnTests` in the directory-derived namespace specified in §6.3.

- **T37.01:** Given 2 of 5 units returned, when the task-specific command or transition is exercised, then only the original proportional quantity/tax/cost and entitlement are reversed.
- **T37.02:** Given concurrent returns exceeding remaining quantity, when the task-specific command or transition is exercised, then one is refused without partial stock or cash effect.
- **T37.03:** Given final residual fractional refund, when the task-specific command or transition is exercised, then cumulative amounts equal original totals exactly.
- **T37.04:** Given unpaid invoice credit, when the task-specific command or transition is exercised, then no cash is falsely paid; given paid invoice, then payout cannot exceed available credit.
- **T37.05:** Given damaged goods, when the task-specific command or transition is exercised, then stock is not restored as sellable and the value difference is explicitly accounted.

Reuse existing or earlier-task fixtures. For a mutation, also assert denied tenant/location/permission and injected pre-commit failure leave zero partial financial/audit effects; for a read/UI-only task assert no mutation. Reuse the same fixture rather than duplicating equivalent tests.

### F. Verification

**Status: NOT RUN - SPEC_ONLY.** The following filter becomes executable only after the NEW test class and prerequisites are implemented.

Requires Task 01's isolated PostgreSQL 18/API fixture, .NET 10 SDK and explicitly validated QTS_TEST_POSTGRES_CONNECTION. Fixture DDL/data writes are permitted only in a later authorized test run; never inherit production credentials.

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~TestQtsBilling.Integration.PartialSalesReturnTests"
```

**PASS:** owning cases and relevant existing regression filters pass, with exact committed-state assertions where applicable; no skipped required prerequisite, new unreviewed dependency, secret leak or `git diff --check` failure. A build or mocked response alone is not financial proof.

Select the checked server or Windows build command in §6.3 only when this task changes that host. Full multi-platform regression belongs in CI/release, not every local edit.

### G. Definition of done

Each returned quantity and refunded amount is capped against original unreturned/paid facts, with exact original tax and cost. Evidence includes the exact implementation SHA, focused test outcome and minimal reviewed diff. All new constraints/contracts/permissions/host wiring ship together under **G6**; unresolved external prerequisites stay explicit.

No unnecessary dependency or compatibility path remains. Recovery preserves historical facts and pending money. Record remaining blockers; spec completion is not implementation completion or owner production approval.

<a id="task-38"></a>
## Atomic Task 38 - Connect invoicing to a single stock and revenue recognition source

### A. Objective

**Priority:** P1. **Requirements:** `R06`, `R07`, `R08`, `R12`. **Findings:** `F19`, `F21`, `F32`, `F33`.
**Dependencies:** [Task 03](#task-03), [Task 09](#task-09), [Task 10](#task-10), [Task 11](#task-11), [Task 13](#task-13), [Task 15](#task-15), [Task 31](#task-31), [Task 33](#task-33), [Task 37](#task-37) **Integration gate:** G6.

**Current → target:** Invoice issuing posts AR/revenue without an integrated stock/fulfillment source; draft delete checks are outside the transaction. → Invoice issuance has exactly one economic recognition path, immutable legal/commercial snapshots and no double stock or revenue.

**Acceptance:** The target behavior is observable through the owning API/UI/store and every applicable scenario in E passes; release additionally requires the named gate, not just this file diff.

**Non-goals:** No compatibility mode for old ambiguous invoice payloads; native callers update together, without erasing already posted facts.

### B. Scope

| Action | Exact path | Change / exclusion |
|---|---|---|
| MODIFY | `QtsWebApp/QtsWebApp/Features/Invoices/InvoiceEndpoints.cs` | Draft concurrency, issue revalidation, source recognition and safe correction. |
| MODIFY | `QtsBilling.Persistence.Postgres/Entities/Accounting.cs` | Immutable party/terms/line snapshots and fulfillment/source links. |
| MODIFY | `QtsBilling.Contracts/Invoices/InvoiceDtos.cs` | Native service/direct-goods/source-linked invoice modes and Version. |
| GENERATE — NEW | `QtsBilling.Persistence.Postgres/Migrations/` | NEW InvoiceRecognitionSource with unique fulfillment/invoice linkage and immutable terms. |
| CREATE — NEW test | `TestQtsBilling/Integration/InvoiceRecognitionTests.cs` | Owning focused assertions below; reuse fixtures rather than duplicate a test infrastructure. |

MODIFY existing `QtsBilling.Persistence.Postgres/BillingDbContext.cs` and the appropriate checked `BillingDbContext.Model.cs` / `BillingDbContext.Model.Financial.cs` only for the listed model; GENERATE designer/snapshot companions with the named new migration.

MODIFY existing `QtsWebApp/QtsWebApp/Program.cs` only for required route/service registration, and `QtsBilling.Contracts/Identity/IdentityDtos.cs` / `QtsWebApp/QtsWebApp/Infrastructure/QtsIdentity.cs` only for this task's explicit D02 policies.

All other production paths are excluded except the narrow, explicitly enumerated [D17 integration bindings](#d17). Applied migrations, posted facts and unresolved cash are never edited/deleted by this scope.

### C. Verified anchors and NEW insertions

- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Invoices/InvoiceEndpoints.cs — source lines 1-110](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Invoices/InvoiceEndpoints.cs#L1-L110): `MapInvoiceEndpoints`. [E31](#e-e31).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Invoices/InvoiceEndpoints.cs — source lines 360-650](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Invoices/InvoiceEndpoints.cs#L360-L650): `DeleteAsync; IssueAsync`. [E32](#e-e32).
- **EXISTING / VERIFIED:** [QtsBilling.Persistence.Postgres/BillingDbContext.Model.Financial.cs — source lines 210-445](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Persistence.Postgres/BillingDbContext.Model.Financial.cs#L210-L445): `ConfigureAccounting; ConfigurePurchasing`. [E07](#e-e07).
- **EXISTING / VERIFIED:** [QtsBilling.Persistence.Postgres/Migrations/20261019082426_AddIntegrityGuards.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Persistence.Postgres/Migrations/20261019082426_AddIntegrityGuards.cs): `qts_invoice_lines_draft_guard; qts_inventory_balance_guard; deferred balance triggers`. [E09](#e-e09).

All CREATE/GENERATE symbols, routes and constraints in B/D are **NEW**. Source presence is not runtime proof; do not infer executed results from old evidence prose.

### D. Ordered implementation recipe

1. Add explicit modes: non-stock service; direct goods with fulfillment; invoice presentation of an already posted sale; later order-delivery invoice (Task 42). Reject ambiguous combinations rather than inferring intent from source IDs.
2. For an already recognized sale, create a document representation linked uniquely to its posted facts without another journal/payment. Direct goods issue atomically consumes stock/value and posts AR/revenue/tax/COGS. Service invoices never touch stock.
3. Lock the draft and require Version inside update/delete/issue transactions. Revalidate active scope and authorized current pricing/terms at issue; preserve agreed quotation price only through its approved source. Bulk deletes must include draft/state predicates and DB guards.
4. Snapshot legal business/customer names, address/tax identity, currency, terms, line tax mode/unit factor and issue date. Printing must never read mutable party/catalog values as historical invoice facts.
5. Void/credit/refund reference immutable original recognition and only reverse each effect once. Adopt Task 37 capped line credits, maintain open balance/credit liability, and preserve source idempotency and audit.

Apply the specific D01–D17 policies relevant to this slice. Finish the narrow route/model/client registration in B, record generated migration paths, and preserve a safe stop/fix-forward path before presenting the diff for review.

### E. Focused tests

**NEW file:** `TestQtsBilling/Integration/InvoiceRecognitionTests.cs`. Name the class `InvoiceRecognitionTests` in the directory-derived namespace specified in §6.3.

- **T38.01:** Given invoice generated from a posted sale, when the task-specific command or transition is exercised, then stock, payments and revenue are unchanged.
- **T38.02:** Given direct goods invoice, when the task-specific command or transition is exercised, then exactly one fulfillment/COGS/AR recognition occurs.
- **T38.03:** Given service invoice, when the task-specific command or transition is exercised, then no inventory movement exists.
- **T38.04:** Given concurrent issue vs delete/edit, when the task-specific command or transition is exercised, then only the allowed state transition succeeds.
- **T38.05:** Given party/catalog change after issue, when the task-specific command or transition is exercised, then printed legal and line snapshots remain unchanged.

Reuse existing or earlier-task fixtures. For a mutation, also assert denied tenant/location/permission and injected pre-commit failure leave zero partial financial/audit effects; for a read/UI-only task assert no mutation. Reuse the same fixture rather than duplicating equivalent tests.

### F. Verification

**Status: NOT RUN - SPEC_ONLY.** The following filter becomes executable only after the NEW test class and prerequisites are implemented.

Requires Task 01's isolated PostgreSQL 18/API fixture, .NET 10 SDK and explicitly validated QTS_TEST_POSTGRES_CONNECTION. Fixture DDL/data writes are permitted only in a later authorized test run; never inherit production credentials.

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~TestQtsBilling.Integration.InvoiceRecognitionTests"
```

**PASS:** owning cases and relevant existing regression filters pass, with exact committed-state assertions where applicable; no skipped required prerequisite, new unreviewed dependency, secret leak or `git diff --check` failure. A build or mocked response alone is not financial proof.

Select the checked server or Windows build command in §6.3 only when this task changes that host. Full multi-platform regression belongs in CI/release, not every local edit.

### G. Definition of done

Invoice issuance has exactly one economic recognition path, immutable legal/commercial snapshots and no double stock or revenue. Evidence includes the exact implementation SHA, focused test outcome and minimal reviewed diff. All new constraints/contracts/permissions/host wiring ship together under **G6**; unresolved external prerequisites stay explicit.

No unnecessary dependency or compatibility path remains. Recovery preserves historical facts and pending money. Record remaining blockers; spec completion is not implementation completion or owner production approval.

<a id="task-39"></a>
## Atomic Task 39 - Complete customer open items, collections, advances and credit payout

### A. Objective

**Priority:** P1. **Requirements:** `R06`, `R08`, `R12`, `R15`. **Findings:** `F14`, `F30`, `F33`, `F34`.
**Dependencies:** [Task 11](#task-11), [Task 13](#task-13), [Task 17](#task-17), [Task 31](#task-31), [Task 37](#task-37), [Task 38](#task-38) **Integration gate:** G6.

**Current → target:** Invoice payment allocation exists, but general customer credit sales/advances/credit balances and as-of party reconciliation are incomplete. → Each customer balance can be explained by immutable open items and settlement/credit allocations that tie to the AR control account.

**Acceptance:** The target behavior is observable through the owning API/UI/store and every applicable scenario in E passes; release additionally requires the named gate, not just this file diff.

**Non-goals:** No unreviewed automatic debt write-off, negative cash, foreign-currency revaluation or mutable balance imports.

### B. Scope

| Action | Exact path | Change / exclusion |
|---|---|---|
| CREATE — NEW | `QtsWebApp/QtsWebApp/Features/Receivables/ReceivableEndpoints.cs` | Customer open-item queries and authorized allocation/credit commands. |
| CREATE — NEW | `QtsBilling.Persistence.Postgres/Entities/Receivables.cs` | Open items, settlement allocations and customer advance/credit movements. |
| CREATE — NEW | `QtsBilling.Contracts/Receivables/ReceivableDtos.cs` | Native AR statement, allocation and credit-limit results. |
| GENERATE — NEW | `QtsBilling.Persistence.Postgres/Migrations/` | NEW CustomerOpenItems with scoped source and allocation uniqueness. |
| CREATE — NEW test | `TestQtsBilling/Integration/ReceivableTests.cs` | Owning focused assertions below; reuse fixtures rather than duplicate a test infrastructure. |

MODIFY existing `QtsBilling.Persistence.Postgres/BillingDbContext.cs` and the appropriate checked `BillingDbContext.Model.cs` / `BillingDbContext.Model.Financial.cs` only for the listed model; GENERATE designer/snapshot companions with the named new migration.

MODIFY existing `QtsWebApp/QtsWebApp/Program.cs` only for required route/service registration, and `QtsBilling.Contracts/Identity/IdentityDtos.cs` / `QtsWebApp/QtsWebApp/Infrastructure/QtsIdentity.cs` only for this task's explicit D02 policies.

All other production paths are excluded except the narrow, explicitly enumerated [D17 integration bindings](#d17). Applied migrations, posted facts and unresolved cash are never edited/deleted by this scope.

### C. Verified anchors and NEW insertions

- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Sales/SalePosting.cs — source lines 1-310](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Sales/SalePosting.cs#L1-L310): `ValidatePayload; ResolveContextAsync; PriceLines; ValidatePayment`. [E11](#e-e11).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Sales/SalePosting.cs — source lines 310-665](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Sales/SalePosting.cs#L310-L665): `PostAsync / posting transaction and revenue journal`. [E12](#e-e12).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Invoices/InvoiceEndpoints.cs — source lines 1-110](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Invoices/InvoiceEndpoints.cs#L1-L110): `MapInvoiceEndpoints`. [E31](#e-e31).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Invoices/InvoiceEndpoints.cs — source lines 360-650](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Invoices/InvoiceEndpoints.cs#L360-L650): `DeleteAsync; IssueAsync`. [E32](#e-e32).

All CREATE/GENERATE symbols, routes and constraints in B/D are **NEW**. Source presence is not runtime proof; do not infer executed results from old evidence prose.

### D. Ordered implementation recipe

1. Create one open item per recognized credit sale/invoice, not both for the same economic source. Preserve due date, customer, original amount and journal link; balances derive from allocations/reversals, never a master balance field.
2. Allocate one actual settlement across one or more same-customer/currency/business items under ordered locks. Enforce allocated <= unsettled payment and outstanding item; default explicit oldest-due suggestions require user confirmation.
3. Advance receipts credit customer-advance liability until applied; application clears that liability and AR. Credit notes reduce open AR and any excess becomes a traceable customer credit, with refund payout separately authorized/capped.
4. Enforce configured credit limit/hold at order/sale confirmation against current exposure plus committed unbilled exposure; manager override is amount-bound, reasoned and audited. Do not allow arbitrary client balance inputs.
5. Expose current and as-of statements/ageing with drilldown to original documents and allocations. Settlement reversals reopen the proper items without deleting prior history; drawer/bank classification uses Task 13/17.

Apply the specific D01–D17 policies relevant to this slice. Finish the narrow route/model/client registration in B, record generated migration paths, and preserve a safe stop/fix-forward path before presenting the diff for review.

### E. Focused tests

**NEW file:** `TestQtsBilling/Integration/ReceivableTests.cs`. Name the class `ReceivableTests` in the directory-derived namespace specified in §6.3.

- **T39.01:** Given credit sale and later linked invoice, when the task-specific command or transition is exercised, then one AR item exists and the control account is not doubled.
- **T39.02:** Given one receipt applied to two invoices, when the task-specific command or transition is exercised, then allocated total cannot exceed received cash.
- **T39.03:** Given overpayment, when the task-specific command or transition is exercised, then only explicit advance handling succeeds and its liability is visible.
- **T39.04:** Given simultaneous credit sale/limit check, when the task-specific command or transition is exercised, then aggregate exposure cannot exceed the authorized cap.
- **T39.05:** Given as-of date before a later collection/reversal, when the task-specific command or transition is exercised, then the statement preserves the historical outstanding balance.

Reuse existing or earlier-task fixtures. For a mutation, also assert denied tenant/location/permission and injected pre-commit failure leave zero partial financial/audit effects; for a read/UI-only task assert no mutation. Reuse the same fixture rather than duplicating equivalent tests.

### F. Verification

**Status: NOT RUN - SPEC_ONLY.** The following filter becomes executable only after the NEW test class and prerequisites are implemented.

Requires Task 01's isolated PostgreSQL 18/API fixture, .NET 10 SDK and explicitly validated QTS_TEST_POSTGRES_CONNECTION. Fixture DDL/data writes are permitted only in a later authorized test run; never inherit production credentials.

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~TestQtsBilling.Integration.ReceivableTests"
```

**PASS:** owning cases and relevant existing regression filters pass, with exact committed-state assertions where applicable; no skipped required prerequisite, new unreviewed dependency, secret leak or `git diff --check` failure. A build or mocked response alone is not financial proof.

Select the checked server or Windows build command in §6.3 only when this task changes that host. Full multi-platform regression belongs in CI/release, not every local edit.

### G. Definition of done

Each customer balance can be explained by immutable open items and settlement/credit allocations that tie to the AR control account. Evidence includes the exact implementation SHA, focused test outcome and minimal reviewed diff. All new constraints/contracts/permissions/host wiring ship together under **G6**; unresolved external prerequisites stay explicit.

No unnecessary dependency or compatibility path remains. Recovery preserves historical facts and pending money. Record remaining blockers; spec completion is not implementation completion or owner production approval.

<a id="task-40"></a>
## Atomic Task 40 - Deliver invoice, collection and customer statement screens

### A. Objective

**Priority:** P1. **Requirements:** `R11`, `R12`, `R17`. **Findings:** `F30`, `F33`, `F34`.
**Dependencies:** [Task 30](#task-30), [Task 31](#task-31), [Task 34](#task-34), [Task 37](#task-37), [Task 38](#task-38), [Task 39](#task-39) **Integration gate:** G6.

**Current → target:** Core invoice API operations are not a complete user-facing billing and collection lifecycle. → An authorized user can draft, review, issue, collect, credit and print invoices and explain customer balances entirely through UI.

**Acceptance:** The target behavior is observable through the owning API/UI/store and every applicable scenario in E passes; release additionally requires the named gate, not just this file diff.

**Non-goals:** No PDF library is required: use the checked HTML rendering architecture with safe encoding and standard printing.

### B. Scope

| Action | Exact path | Change / exclusion |
|---|---|---|
| CREATE — NEW | `QtsBilling.Ui/Pages/Invoices/Invoices.razor` | Paged invoice list, source-aware creation and issue/correction actions. |
| CREATE — NEW | `QtsBilling.Ui/Pages/Invoices/InvoiceEditor.razor` | Draft-only edit, immutable issued detail and print. |
| CREATE — NEW | `QtsBilling.Ui/Pages/Receivables/CustomerAccounts.razor` | Open items, collection allocation, advance and credit payout with statement drilldown. |
| CREATE — NEW test | `TestQtsBilling/Ui/InvoiceReceivableJourneyTests.cs` | Owning focused assertions below; reuse fixtures rather than duplicate a test infrastructure. |

Apply only the exact typed-client/host registration and routing bindings in [D17](#d17); no unlisted feature is included. A new page cannot be marked complete before its owning API is wired.

All other production paths are excluded except the narrow, explicitly enumerated [D17 integration bindings](#d17). Applied migrations, posted facts and unresolved cash are never edited/deleted by this scope.

### C. Verified anchors and NEW insertions

- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Invoices/InvoiceEndpoints.cs — source lines 1-110](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Invoices/InvoiceEndpoints.cs#L1-L110): `MapInvoiceEndpoints`. [E31](#e-e31).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Invoices/InvoiceEndpoints.cs — source lines 360-650](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Invoices/InvoiceEndpoints.cs#L360-L650): `DeleteAsync; IssueAsync`. [E32](#e-e32).
- **EXISTING / VERIFIED:** [QtsBilling.Ui/Pages/Pos.razor.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Ui/Pages/Pos.razor.cs): `LineTax; LineTotal; CaptureAsync`. [E26](#e-e26).

All CREATE/GENERATE symbols, routes and constraints in B/D are **NEW**. Source presence is not runtime proof; do not infer executed results from old evidence prose.

### D. Ordered implementation recipe

1. Consume typed invoice/AR clients; expose draft vs issued states and the explicit recognition modes. An issued document shows read-only terms with corrective actions, never a misleading Edit button.
2. Invoice editor supports services/products/sell units, customer, legal/terms snapshot preview and server-computed totals. Source-linked sale/order/delivery choice is validated and clearly prevents duplicate billing.
3. Collection screen shows outstanding items, actual tender/clearing status, proposed allocations and residual advance. Require explicit confirmation before recording money; retain operation identity through outcome-unknown errors.
4. Credit/return/payout UI uses Task 37 caps and permissions, with reason/restock disposition and separate physical payout confirmation. Display customer credit balances rather than silently netting them away.
5. Provide print/reprint, cursor search, due/overdue filters, historical statement as-of date, keyboard labels and error focus. All new pages remain online-only; desktop disconnection never pretends to post an invoice.

Apply the specific D01–D17 policies relevant to this slice. Finish the narrow route/model/client registration in B, record generated migration paths, and preserve a safe stop/fix-forward path before presenting the diff for review.

### E. Focused tests

**NEW file:** `TestQtsBilling/Ui/InvoiceReceivableJourneyTests.cs`. Name the class `InvoiceReceivableJourneyTests` in the directory-derived namespace specified in §6.3.

- **T40.01:** Given a service invoice, when the task-specific command or transition is exercised, then draft→issue→partial collection→final collection works through UI and reconciles.
- **T40.02:** Given a posted sale, when the task-specific command or transition is exercised, then invoice representation creates no extra financial effect.
- **T40.03:** Given over-allocation, stale draft or insufficient permission, when the task-specific command or transition is exercised, then the exact server error is actionable and no duplicate submission occurs.
- **T40.04:** Given customer advance and credit, when the task-specific command or transition is exercised, then allocations/payouts are explicit and remaining balances visible.
- **T40.05:** Given print after party rename or offline server loss, when the task-specific command or transition is exercised, then history remains correct and write actions do not show false success.

Reuse existing or earlier-task fixtures. For a mutation, also assert denied tenant/location/permission and injected pre-commit failure leave zero partial financial/audit effects; for a read/UI-only task assert no mutation. Reuse the same fixture rather than duplicating equivalent tests.

### F. Verification

**Status: NOT RUN - SPEC_ONLY.** The following filter becomes executable only after the NEW test class and prerequisites are implemented.

Requires the owning APIs, shared UI smoke/Playwright setup and disposable native data. The actual Windows/WebView2 checks remain a separate platform gate.

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~TestQtsBilling.Ui.InvoiceReceivableJourneyTests"
```

**PASS:** owning cases and relevant existing regression filters pass, with exact committed-state assertions where applicable; no skipped required prerequisite, new unreviewed dependency, secret leak or `git diff --check` failure. A build or mocked response alone is not financial proof.

Select the checked server or Windows build command in §6.3 only when this task changes that host. Full multi-platform regression belongs in CI/release, not every local edit.

### G. Definition of done

An authorized user can draft, review, issue, collect, credit and print invoices and explain customer balances entirely through UI. Evidence includes the exact implementation SHA, focused test outcome and minimal reviewed diff. All new constraints/contracts/permissions/host wiring ship together under **G6**; unresolved external prerequisites stay explicit.

No unnecessary dependency or compatibility path remains. Recovery preserves historical facts and pending money. Record remaining blockers; spec completion is not implementation completion or owner production approval.

<a id="task-41"></a>
## Atomic Task 41 - Add quotes and sales orders with versioned commitments

### A. Objective

**Priority:** P1. **Requirements:** `R11`, `R12`. **Findings:** `F30`, `F35`.
**Dependencies:** [Task 03](#task-03), [Task 04](#task-04), [Task 05](#task-05), [Task 08](#task-08), [Task 11](#task-11), [Task 31](#task-31), [Task 33](#task-33), [Task 39](#task-39) **Integration gate:** G6.

**Current → target:** Quote, order and delivery commitments are missing from the current runtime and UI. → Wholesale and service staff can agree a priced scope, confirm an order and track fulfillment without premature stock or revenue effects.

**Acceptance:** The target behavior is observable through the owning API/UI/store and every applicable scenario in E passes; release additionally requires the named gate, not just this file diff.

**Non-goals:** No opportunity CRM, contract-generation platform or arbitrary workflow engine.

### B. Scope

| Action | Exact path | Change / exclusion |
|---|---|---|
| CREATE — NEW | `QtsBilling.Persistence.Postgres/Entities/SalesOrders.cs` | Quote/order headers, lines, revisions and agreed terms. |
| CREATE — NEW | `QtsWebApp/QtsWebApp/Features/Orders/OrderEndpoints.cs` | Native quote/order lifecycle and approved conversion. |
| CREATE — NEW | `QtsBilling.Contracts/Orders/OrderDtos.cs` | Versioned quote/order requests, results and client contracts. |
| GENERATE — NEW | `QtsBilling.Persistence.Postgres/Migrations/` | NEW SalesQuotesAndOrders with scoped document numbering and source uniqueness. |
| CREATE — NEW test | `TestQtsBilling/Integration/QuoteOrderTests.cs` | Owning focused assertions below; reuse fixtures rather than duplicate a test infrastructure. |

MODIFY existing `QtsBilling.Persistence.Postgres/BillingDbContext.cs` and the appropriate checked `BillingDbContext.Model.cs` / `BillingDbContext.Model.Financial.cs` only for the listed model; GENERATE designer/snapshot companions with the named new migration.

MODIFY existing `QtsWebApp/QtsWebApp/Program.cs` only for required route/service registration, and `QtsBilling.Contracts/Identity/IdentityDtos.cs` / `QtsWebApp/QtsWebApp/Infrastructure/QtsIdentity.cs` only for this task's explicit D02 policies.

All other production paths are excluded except the narrow, explicitly enumerated [D17 integration bindings](#d17). Applied migrations, posted facts and unresolved cash are never edited/deleted by this scope.

### C. Verified anchors and NEW insertions

- **EXISTING / VERIFIED:** [QtsBilling.Persistence.Postgres/BillingDbContext.Model.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Persistence.Postgres/BillingDbContext.Model.cs): `ConfigureCore; ConfigureMasterData; ConfigureCatalog; ConfigureInventory`. [E06](#e-e06).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Invoices/InvoiceEndpoints.cs — source lines 1-110](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Invoices/InvoiceEndpoints.cs#L1-L110): `MapInvoiceEndpoints`. [E31](#e-e31).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Invoices/InvoiceEndpoints.cs — source lines 360-650](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Invoices/InvoiceEndpoints.cs#L360-L650): `DeleteAsync; IssueAsync`. [E32](#e-e32).

All CREATE/GENERATE symbols, routes and constraints in B/D are **NEW**. Source presence is not runtime proof; do not infer executed results from old evidence prose.

### D. Ordered implementation recipe

1. Implement quotes Draft → Sent → Accepted/Rejected/Expired/Cancelled and orders Draft → Confirmed → PartiallyFulfilled → Fulfilled/Closed, with explicit cancelled remainder. Quote/order creation alone posts neither revenue nor stock.
2. Use D03 snapshots, party terms, business-local dates, document numbering and Version; sent/confirmed terms change only through a versioned amendment, never in-place historical rewriting.
3. Accepted-quote conversion is idempotent and creates at most one intended order conversion; allow partial conversion only if explicit line quantity tracking/caps are implemented in this native scope. Otherwise reject it clearly.
4. Confirmation rechecks current authorization/credit exposure and approved agreed price/expiry. Include tax/unit/conversion snapshots and promised delivery date. Status transitions carry actor/reason/audit.
5. Expose bounded list/detail/amend/cancel routes under /api/v1/quotes and /api/v1/sales-orders; no generic status setter. Task 42 supplies reservation/fulfillment; until then confirmed order cannot masquerade as delivered.

Apply the specific D01–D17 policies relevant to this slice. Finish the narrow route/model/client registration in B, record generated migration paths, and preserve a safe stop/fix-forward path before presenting the diff for review.

### E. Focused tests

**NEW file:** `TestQtsBilling/Integration/QuoteOrderTests.cs`. Name the class `QuoteOrderTests` in the directory-derived namespace specified in §6.3.

- **T41.01:** Given quote or confirmed order only, when the task-specific command or transition is exercised, then ledger and stock are unchanged.
- **T41.02:** Given expired quote or stale amendment, when the task-specific command or transition is exercised, then acceptance is refused.
- **T41.03:** Given duplicate conversion command, when the task-specific command or transition is exercised, then one order with the original agreed snapshots exists.
- **T41.04:** Given unauthorized discount or credit exposure beyond limit, when the task-specific command or transition is exercised, then confirmation fails.
- **T41.05:** Given amendment after partial fulfillment, when the task-specific command or transition is exercised, then fulfilled historical lines remain immutable and only outstanding commitment changes.

Reuse existing or earlier-task fixtures. For a mutation, also assert denied tenant/location/permission and injected pre-commit failure leave zero partial financial/audit effects; for a read/UI-only task assert no mutation. Reuse the same fixture rather than duplicating equivalent tests.

### F. Verification

**Status: NOT RUN - SPEC_ONLY.** The following filter becomes executable only after the NEW test class and prerequisites are implemented.

Requires Task 01's isolated PostgreSQL 18/API fixture, .NET 10 SDK and explicitly validated QTS_TEST_POSTGRES_CONNECTION. Fixture DDL/data writes are permitted only in a later authorized test run; never inherit production credentials.

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~TestQtsBilling.Integration.QuoteOrderTests"
```

**PASS:** owning cases and relevant existing regression filters pass, with exact committed-state assertions where applicable; no skipped required prerequisite, new unreviewed dependency, secret leak or `git diff --check` failure. A build or mocked response alone is not financial proof.

Select the checked server or Windows build command in §6.3 only when this task changes that host. Full multi-platform regression belongs in CI/release, not every local edit.

### G. Definition of done

Wholesale and service staff can agree a priced scope, confirm an order and track fulfillment without premature stock or revenue effects. Evidence includes the exact implementation SHA, focused test outcome and minimal reviewed diff. All new constraints/contracts/permissions/host wiring ship together under **G6**; unresolved external prerequisites stay explicit.

No unnecessary dependency or compatibility path remains. Recovery preserves historical facts and pending money. Record remaining blockers; spec completion is not implementation completion or owner production approval.

<a id="task-42"></a>
## Atomic Task 42 - Add reserved stock, partial deliveries and invoice linkage

### A. Objective

**Priority:** P1. **Requirements:** `R06`, `R07`, `R08`, `R12`. **Findings:** `F21`, `F32`, `F35`, `F36`.
**Dependencies:** [Task 10](#task-10), [Task 11](#task-11), [Task 14](#task-14), [Task 15](#task-15), [Task 38](#task-38), [Task 39](#task-39), [Task 41](#task-41) **Integration gate:** G6.

**Current → target:** There is no complete ordered/reserved/delivered/billed quantity lifecycle linking goods and receivables. → Partial fulfillment cannot oversell, overdeliver, double invoice or lose cost recognition between delivery and billing.

**Acceptance:** The target behavior is observable through the owning API/UI/store and every applicable scenario in E passes; release additionally requires the named gate, not just this file diff.

**Non-goals:** No shipping carrier integration, multi-stage logistics, warehouse robotics or unlimited reservation service.

### B. Scope

| Action | Exact path | Change / exclusion |
|---|---|---|
| CREATE — NEW | `QtsWebApp/QtsWebApp/Features/Orders/FulfillmentService.cs` | Reservation/dispatch/delivery and invoice-source controls. |
| CREATE — NEW | `QtsBilling.Persistence.Postgres/Entities/Fulfillment.cs` | Reservation, delivery and line/source quantity/value identities. |
| CREATE — NEW | `QtsBilling.Contracts/Orders/FulfillmentDtos.cs` | Explicit native reserve/release/deliver/invoice-selection requests. |
| GENERATE — NEW | `QtsBilling.Persistence.Postgres/Migrations/` | NEW OrderFulfillment with unique source allocations and nonnegative available quantities. |
| CREATE — NEW test | `TestQtsBilling/Integration/FulfillmentTests.cs` | Owning focused assertions below; reuse fixtures rather than duplicate a test infrastructure. |

MODIFY existing `QtsBilling.Persistence.Postgres/BillingDbContext.cs` and the appropriate checked `BillingDbContext.Model.cs` / `BillingDbContext.Model.Financial.cs` only for the listed model; GENERATE designer/snapshot companions with the named new migration.

All other production paths are excluded except the narrow, explicitly enumerated [D17 integration bindings](#d17). Applied migrations, posted facts and unresolved cash are never edited/deleted by this scope.

### C. Verified anchors and NEW insertions

- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Inventory/InventoryLedger.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Inventory/InventoryLedger.cs): `ApplyAsync`. [E13](#e-e13).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Purchasing/PurchasingEndpoints.cs — source lines 1020-1170](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Purchasing/PurchasingEndpoints.cs#L1020-L1170): `PostReceiptAsync commit path`. [E30](#e-e30).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Invoices/InvoiceEndpoints.cs — source lines 360-650](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Invoices/InvoiceEndpoints.cs#L360-L650): `DeleteAsync; IssueAsync`. [E32](#e-e32).

All CREATE/GENERATE symbols, routes and constraints in B/D are **NEW**. Source presence is not runtime proof; do not infer executed results from old evidence prose.

### D. Ordered implementation recipe

1. Reserve under the same ordered inventory locks: available = on-hand minus active reservations; reservation is not a physical movement or revenue. New walk-in sales respect other active reservations, and order delivery consumes its own reservation.
2. Delivery line quantities are capped by confirmed outstanding order quantities. Atomically release consumed reservation, issue physical stock/value, write immutable delivery/source links and audit. Cancellation releases only undelivered reservations.
3. For delivery-before-billing use D08 delivered-goods-not-invoiced cost clearing, then transfer that cost to COGS at invoice recognition; the invoice must link unique delivery quantities. Direct goods invoice uses Task 38 instead, never both paths.
4. Allow partial delivery/invoice across multiple commands, with cumulative quantity/value caps and final residual rounding. A retry stores the same delivery result. Out-of-order/duplicate callbacks or manual status changes cannot create effects.
5. No implicit reservation expiry worker is needed initially: explicit expiry/cancellation commands are bounded and audited; expired reservations release atomically before reuse. Offline captures remain provisional and revalidate central availability.

Apply the specific D01–D17 policies relevant to this slice. Finish the narrow route/model/client registration in B, record generated migration paths, and preserve a safe stop/fix-forward path before presenting the diff for review.

### E. Focused tests

**NEW file:** `TestQtsBilling/Integration/FulfillmentTests.cs`. Name the class `FulfillmentTests` in the directory-derived namespace specified in §6.3.

- **T42.01:** Given two orders reserving the last unit, when the task-specific command or transition is exercised, then one reservation succeeds and a walk-in sale cannot oversell it.
- **T42.02:** Given partial delivery and invoice, when the task-specific command or transition is exercised, then delivered/billed/outstanding quantities and cost clearing reconcile.
- **T42.03:** Given duplicate delivery/invoice or simultaneous invoice selection, when the task-specific command or transition is exercised, then source quantities are consumed once.
- **T42.04:** Given cancelled remainder, when the task-specific command or transition is exercised, then only outstanding reservation releases and shipped history remains.
- **T42.05:** Given failure after stock issue, when the task-specific command or transition is exercised, then reservation, delivery, valuation and journal roll back together.

Reuse existing or earlier-task fixtures. For a mutation, also assert denied tenant/location/permission and injected pre-commit failure leave zero partial financial/audit effects; for a read/UI-only task assert no mutation. Reuse the same fixture rather than duplicating equivalent tests.

### F. Verification

**Status: NOT RUN - SPEC_ONLY.** The following filter becomes executable only after the NEW test class and prerequisites are implemented.

Requires Task 01's isolated PostgreSQL 18/API fixture, .NET 10 SDK and explicitly validated QTS_TEST_POSTGRES_CONNECTION. Fixture DDL/data writes are permitted only in a later authorized test run; never inherit production credentials.

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~TestQtsBilling.Integration.FulfillmentTests"
```

**PASS:** owning cases and relevant existing regression filters pass, with exact committed-state assertions where applicable; no skipped required prerequisite, new unreviewed dependency, secret leak or `git diff --check` failure. A build or mocked response alone is not financial proof.

Select the checked server or Windows build command in §6.3 only when this task changes that host. Full multi-platform regression belongs in CI/release, not every local edit.

### G. Definition of done

Partial fulfillment cannot oversell, overdeliver, double invoice or lose cost recognition between delivery and billing. Evidence includes the exact implementation SHA, focused test outcome and minimal reviewed diff. All new constraints/contracts/permissions/host wiring ship together under **G6**; unresolved external prerequisites stay explicit.

No unnecessary dependency or compatibility path remains. Recovery preserves historical facts and pending money. Record remaining blockers; spec completion is not implementation completion or owner production approval.

<a id="task-43"></a>
## Atomic Task 43 - Deliver quotation, order and fulfillment workbenches

### A. Objective

**Priority:** P1. **Requirements:** `R11`, `R12`, `R17`. **Findings:** `F30`, `F35`.
**Dependencies:** [Task 30](#task-30), [Task 34](#task-34), [Task 40](#task-40), [Task 41](#task-41), [Task 42](#task-42) **Integration gate:** G6.

**Current → target:** The project has no user-facing order-to-delivery journey. → Sales and warehouse actors can complete a wholesale or service commitment using the same controlled native workflows.

**Acceptance:** The target behavior is observable through the owning API/UI/store and every applicable scenario in E passes; release additionally requires the named gate, not just this file diff.

**Non-goals:** No customer-facing portal or carrier connection in this release.

### B. Scope

| Action | Exact path | Change / exclusion |
|---|---|---|
| CREATE — NEW | `QtsBilling.Ui/Pages/Orders/Quotes.razor` | Quote list/editor/acceptance and native order conversion. |
| CREATE — NEW | `QtsBilling.Ui/Pages/Orders/SalesOrders.razor` | Confirmed commitment, amendment, reservation and outstanding quantities. |
| CREATE — NEW | `QtsBilling.Ui/Pages/Orders/Deliveries.razor` | Partial delivery selection, remaining entitlement and invoice linkage. |
| CREATE — NEW test | `TestQtsBilling/Ui/OrderFulfillmentJourneyTests.cs` | Owning focused assertions below; reuse fixtures rather than duplicate a test infrastructure. |

Apply only the exact typed-client/host registration and routing bindings in [D17](#d17); no unlisted feature is included. A new page cannot be marked complete before its owning API is wired.

All other production paths are excluded except the narrow, explicitly enumerated [D17 integration bindings](#d17). Applied migrations, posted facts and unresolved cash are never edited/deleted by this scope.

### C. Verified anchors and NEW insertions

- **EXISTING / VERIFIED:** [QtsBilling.Ui/Pages/Pos.razor.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Ui/Pages/Pos.razor.cs): `LineTax; LineTotal; CaptureAsync`. [E26](#e-e26).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Invoices/InvoiceEndpoints.cs — source lines 1-110](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Invoices/InvoiceEndpoints.cs#L1-L110): `MapInvoiceEndpoints`. [E31](#e-e31).
- **EXISTING / VERIFIED:** [README.md — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/README.md): `Architecture / status / project descriptions`. [E48](#e-e48).

All CREATE/GENERATE symbols, routes and constraints in B/D are **NEW**. Source presence is not runtime proof; do not infer executed results from old evidence prose.

### D. Ordered implementation recipe

1. Add typed clients/routes for quote and order lists/details; display draft/sent/accepted/expired/confirmed/partial/closed states without exposing generic status mutation.
2. Editor previews agreed pricing/tax/terms and availability, with Version/amendment review and explicit acceptance/conversion. Documents print using immutable version snapshots.
3. Warehouse view separates on-hand, reserved, available, delivered and remaining quantities; select only permitted location and eligible lines for partial fulfillment.
4. Invoice-from-delivery action shows billed vs unbilled quantities and prevents duplicate selection; route to Task 40 for final billing/collection. Service lines avoid warehouse controls.
5. Provide cancellation reason, reservation release, keyboard selection, stale conflict reload and network outcome-unknown handling. History links quote→order→delivery→invoice→collection/return for audit.

Apply the specific D01–D17 policies relevant to this slice. Finish the narrow route/model/client registration in B, record generated migration paths, and preserve a safe stop/fix-forward path before presenting the diff for review.

### E. Focused tests

**NEW file:** `TestQtsBilling/Ui/OrderFulfillmentJourneyTests.cs`. Name the class `OrderFulfillmentJourneyTests` in the directory-derived namespace specified in §6.3.

- **T43.01:** Given wholesale quote, when the task-specific command or transition is exercised, then accept→order→reserve→partial deliver→invoice→collect is usable through UI.
- **T43.02:** Given non-stock service quote, when the task-specific command or transition is exercised, then conversion/issue works without stock fields.
- **T43.03:** Given stale outstanding quantity, when the task-specific command or transition is exercised, then delivery/invoice fails with a reloadable conflict.
- **T43.04:** Given warehouse-only actor, when the task-specific command or transition is exercised, then pricing overrides/collection are not available.
- **T43.05:** Given cancellation or printing, when the task-specific command or transition is exercised, then no unintended stock/revenue effect occurs.

Reuse existing or earlier-task fixtures. For a mutation, also assert denied tenant/location/permission and injected pre-commit failure leave zero partial financial/audit effects; for a read/UI-only task assert no mutation. Reuse the same fixture rather than duplicating equivalent tests.

### F. Verification

**Status: NOT RUN - SPEC_ONLY.** The following filter becomes executable only after the NEW test class and prerequisites are implemented.

Requires the owning APIs, shared UI smoke/Playwright setup and disposable native data. The actual Windows/WebView2 checks remain a separate platform gate.

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~TestQtsBilling.Ui.OrderFulfillmentJourneyTests"
```

**PASS:** owning cases and relevant existing regression filters pass, with exact committed-state assertions where applicable; no skipped required prerequisite, new unreviewed dependency, secret leak or `git diff --check` failure. A build or mocked response alone is not financial proof.

Select the checked server or Windows build command in §6.3 only when this task changes that host. Full multi-platform regression belongs in CI/release, not every local edit.

### G. Definition of done

Sales and warehouse actors can complete a wholesale or service commitment using the same controlled native workflows. Evidence includes the exact implementation SHA, focused test outcome and minimal reviewed diff. All new constraints/contracts/permissions/host wiring ship together under **G6**; unresolved external prerequisites stay explicit.

No unnecessary dependency or compatibility path remains. Recovery preserves historical facts and pending money. Record remaining blockers; spec completion is not implementation completion or owner production approval.

<a id="task-44"></a>
## Atomic Task 44 - Complete purchase-order amendments, receiving and return commitments

### A. Objective

**Priority:** P1. **Requirements:** `R11`, `R13`. **Findings:** `F21`, `F30`, `F34`.
**Dependencies:** [Task 11](#task-11), [Task 16](#task-16), [Task 31](#task-31), [Task 32](#task-32), [Task 33](#task-33) **Integration gate:** G6.

**Current → target:** PO routes exist, but returns reduce received counters and a complete amendment/outstanding-order journey is not defined. → Purchasing commitments, deliveries and supplier returns remain distinct and measurable throughout their lifecycle.

**Acceptance:** The target behavior is observable through the owning API/UI/store and every applicable scenario in E passes; release additionally requires the named gate, not just this file diff.

**Non-goals:** No unapproved over-delivery tolerance, foreign-currency purchase, or full supplier collaboration portal.

### B. Scope

| Action | Exact path | Change / exclusion |
|---|---|---|
| MODIFY | `QtsWebApp/QtsWebApp/Features/Purchasing/PurchasingEndpoints.cs` | Versioned draft/amendment/remainder-close, supplier terms and receipt caps. |
| MODIFY | `QtsBilling.Contracts/Purchasing/PurchasingDtos.cs` | Native order versions, service/goods distinction and outstanding quantities. |
| MODIFY | `QtsBilling.Persistence.Postgres/Entities/Purchasing.cs` | Cumulative received/returned/cancelled quantities and immutable confirmed revisions. |
| CREATE — NEW test | `TestQtsBilling/Integration/PurchaseCommitmentTests.cs` | Owning focused assertions below; reuse fixtures rather than duplicate a test infrastructure. |

MODIFY existing `QtsBilling.Persistence.Postgres/BillingDbContext.cs` and the appropriate checked `BillingDbContext.Model.cs` / `BillingDbContext.Model.Financial.cs` only for the listed model; GENERATE designer/snapshot companions with the named new migration.

MODIFY existing `QtsWebApp/QtsWebApp/Program.cs` only for required route/service registration, and `QtsBilling.Contracts/Identity/IdentityDtos.cs` / `QtsWebApp/QtsWebApp/Infrastructure/QtsIdentity.cs` only for this task's explicit D02 policies.

All other production paths are excluded except the narrow, explicitly enumerated [D17 integration bindings](#d17). Applied migrations, posted facts and unresolved cash are never edited/deleted by this scope.

### C. Verified anchors and NEW insertions

- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Purchasing/PurchasingEndpoints.cs — source lines 1-120](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Purchasing/PurchasingEndpoints.cs#L1-L120): `MapPurchasingEndpoints`. [E28](#e-e28).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Purchasing/PurchasingEndpoints.cs — source lines 640-1030](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Purchasing/PurchasingEndpoints.cs#L640-L1030): `ConfirmOrderAsync; CancelOrderAsync; PostReceiptAsync`. [E29](#e-e29).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Purchasing/PurchasingEndpoints.cs — source lines 1020-1170](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Purchasing/PurchasingEndpoints.cs#L1020-L1170): `PostReceiptAsync commit path`. [E30](#e-e30).

All CREATE/GENERATE symbols, routes and constraints in B/D are **NEW**. Source presence is not runtime proof; do not infer executed results from old evidence prose.

### D. Ordered implementation recipe

1. Retain current PO creation/placement/receipt APIs and Task 16 journals. Require Version under row lock for draft edits/deletes and confirmed-order amendment; update only unreceived/uninvoiced remainder.
2. Snapshot supplier terms, quantities, unit conversion, price, discount/tax and expected date at placement. A PO is never stock or a liability; actual goods/service acceptance drives receipt accounting.
3. Track ordered, cancelled remainder, cumulative received and returned quantities separately. Returning a delivery does not silently authorize another delivery; replacement requires explicit approved amendment/new order.
4. Provide short-close/cancel remainder with reason, partial receipts and line-linked returns. Distinguish service acceptance from goods movement, and refuse inactive/cross-scope suppliers/locations/variants.
5. Expose outstanding/due receipt queries without N+1 reads. Every state change is audited and idempotent when it can create a commitment/document; defaults remain no over-receipt unless a formal amendment is accepted.

Apply the specific D01–D17 policies relevant to this slice. Finish the narrow route/model/client registration in B, record generated migration paths, and preserve a safe stop/fix-forward path before presenting the diff for review.

### E. Focused tests

**NEW file:** `TestQtsBilling/Integration/PurchaseCommitmentTests.cs`. Name the class `PurchaseCommitmentTests` in the directory-derived namespace specified in §6.3.

- **T44.01:** Given a partial receipt then return, when the task-specific command or transition is exercised, then replacement is not automatically received beyond the approved commitment.
- **T44.02:** Given short-close of remainder, when the task-specific command or transition is exercised, then received history and liability remain while outstanding becomes zero.
- **T44.03:** Given concurrent amendment and receipt, when the task-specific command or transition is exercised, then caps/Version prevent over-receipt.
- **T44.04:** Given a service PO, when the task-specific command or transition is exercised, then acceptance records expense/clearing without stock movement.
- **T44.05:** Given same-key replay or foreign supplier/location, when the task-specific command or transition is exercised, then no duplicate commitment or cross-tenant reference is accepted.

Reuse existing or earlier-task fixtures. For a mutation, also assert denied tenant/location/permission and injected pre-commit failure leave zero partial financial/audit effects; for a read/UI-only task assert no mutation. Reuse the same fixture rather than duplicating equivalent tests.

### F. Verification

**Status: NOT RUN - SPEC_ONLY.** The following filter becomes executable only after the NEW test class and prerequisites are implemented.

Requires Task 01's isolated PostgreSQL 18/API fixture, .NET 10 SDK and explicitly validated QTS_TEST_POSTGRES_CONNECTION. Fixture DDL/data writes are permitted only in a later authorized test run; never inherit production credentials.

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~TestQtsBilling.Integration.PurchaseCommitmentTests"
```

**PASS:** owning cases and relevant existing regression filters pass, with exact committed-state assertions where applicable; no skipped required prerequisite, new unreviewed dependency, secret leak or `git diff --check` failure. A build or mocked response alone is not financial proof.

Select the checked server or Windows build command in §6.3 only when this task changes that host. Full multi-platform regression belongs in CI/release, not every local edit.

### G. Definition of done

Purchasing commitments, deliveries and supplier returns remain distinct and measurable throughout their lifecycle. Evidence includes the exact implementation SHA, focused test outcome and minimal reviewed diff. All new constraints/contracts/permissions/host wiring ship together under **G6**; unresolved external prerequisites stay explicit.

No unnecessary dependency or compatibility path remains. Recovery preserves historical facts and pending money. Record remaining blockers; spec completion is not implementation completion or owner production approval.

<a id="task-45"></a>
## Atomic Task 45 - Implement supplier bills, input tax and AP recognition

### A. Objective

**Priority:** P1. **Requirements:** `R06`, `R08`, `R13`, `R15`. **Findings:** `F16`, `F30`, `F34`, `F38`.
**Dependencies:** [Task 03](#task-03), [Task 05](#task-05), [Task 10](#task-10), [Task 11](#task-11), [Task 13](#task-13), [Task 15](#task-15), [Task 16](#task-16), [Task 31](#task-31), [Task 44](#task-44) **Integration gate:** G6.

**Current → target:** Goods receipts do not yet complete supplier financial recognition, and supplier bills/AP are missing. → Every supplier invoice has an immutable recognized liability matched to goods/service acceptance without adding stock twice.

**Acceptance:** The target behavior is observable through the owning API/UI/store and every applicable scenario in E passes; release additionally requires the named gate, not just this file diff.

**Non-goals:** Input-tax eligibility is configured by the business and requires finance/jurisdiction approval; no statutory filing claims.

### B. Scope

| Action | Exact path | Change / exclusion |
|---|---|---|
| CREATE — NEW | `QtsWebApp/QtsWebApp/Features/Payables/SupplierBillEndpoints.cs` | Receipt-matched bills, credit notes and AP open-item recognition. |
| CREATE — NEW | `QtsBilling.Persistence.Postgres/Entities/Payables.cs` | Supplier bill/line/open item/credit and matched receipt identity. |
| CREATE — NEW | `QtsBilling.Contracts/Payables/SupplierBillDtos.cs` | Native bill/credit/match/variance request and result contracts. |
| GENERATE — NEW | `QtsBilling.Persistence.Postgres/Migrations/` | NEW SupplierBillsAndOpenItems with scoped duplicate invoice and receipt-allocation caps. |
| CREATE — NEW test | `TestQtsBilling/Integration/SupplierBillTests.cs` | Owning focused assertions below; reuse fixtures rather than duplicate a test infrastructure. |

MODIFY existing `QtsBilling.Persistence.Postgres/BillingDbContext.cs` and the appropriate checked `BillingDbContext.Model.cs` / `BillingDbContext.Model.Financial.cs` only for the listed model; GENERATE designer/snapshot companions with the named new migration.

MODIFY existing `QtsWebApp/QtsWebApp/Program.cs` only for required route/service registration, and `QtsBilling.Contracts/Identity/IdentityDtos.cs` / `QtsWebApp/QtsWebApp/Infrastructure/QtsIdentity.cs` only for this task's explicit D02 policies.

All other production paths are excluded except the narrow, explicitly enumerated [D17 integration bindings](#d17). Applied migrations, posted facts and unresolved cash are never edited/deleted by this scope.

### C. Verified anchors and NEW insertions

- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Purchasing/PurchasingEndpoints.cs — source lines 1-120](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Purchasing/PurchasingEndpoints.cs#L1-L120): `MapPurchasingEndpoints`. [E28](#e-e28).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Purchasing/PurchasingEndpoints.cs — source lines 1020-1170](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Purchasing/PurchasingEndpoints.cs#L1020-L1170): `PostReceiptAsync commit path`. [E30](#e-e30).
- **EXISTING / VERIFIED:** [QtsBilling.Persistence.Postgres/BillingDbContext.Model.Financial.cs — source lines 210-445](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Persistence.Postgres/BillingDbContext.Model.Financial.cs#L210-L445): `ConfigureAccounting; ConfigurePurchasing`. [E07](#e-e07).

All CREATE/GENERATE symbols, routes and constraints in B/D are **NEW**. Source presence is not runtime proof; do not infer executed results from old evidence prose.

### D. Ordered implementation recipe

1. Implement draft/review/post/reverse via /api/v1/supplier-bills. Require supplier invoice reference/date/currency and enforce tenant+supplier+normalized reference uniqueness; no arbitrary overwrite of posted bills.
2. For receipt-matched goods, debit GRNI for the approved matched value, recoverable input tax where configured, and explicit authorized price variance; credit AP. The bill never repeats receipt stock movement. Cap invoiced receipt quantity/value under locks.
3. For service/direct expense bills, debit the selected valid operating expense or prepayment account and input tax, credit AP; no goods stock effect. Unmatched stock goods bills are refused until receipt/matching is explicit.
4. Create supplier AP open item with due date, terms and journal reference. Supplier credits reference original billed lines and uncredited amounts; tie returned goods clearing to credits without a second return movement.
5. Preserve legal/supplier/price/tax snapshots and attachment references; changed tax/price policy never reprices old bills. Posting must reconcile bill total and matched variance exactly, with reasoned approval for any nonzero variance.

Apply the specific D01–D17 policies relevant to this slice. Finish the narrow route/model/client registration in B, record generated migration paths, and preserve a safe stop/fix-forward path before presenting the diff for review.

### E. Focused tests

**NEW file:** `TestQtsBilling/Integration/SupplierBillTests.cs`. Name the class `SupplierBillTests` in the directory-derived namespace specified in §6.3.

- **T45.01:** Given GRN followed by matched supplier bill, when the task-specific command or transition is exercised, then stock changes once and GRNI clears to AP/input tax.
- **T45.02:** Given duplicate supplier reference or repeated command, when the task-specific command or transition is exercised, then one bill and AP item exist.
- **T45.03:** Given invoice quantity beyond unbilled receipt, when the task-specific command or transition is exercised, then post fails under concurrent matching.
- **T45.04:** Given supplier credit for a returned line, when the task-specific command or transition is exercised, then AP/return clearing reduce without another stock movement.
- **T45.05:** Given nonrecoverable tax or service bill, when the task-specific command or transition is exercised, then the configured expense/cost classification is used, never fabricated input-tax recovery.

Reuse existing or earlier-task fixtures. For a mutation, also assert denied tenant/location/permission and injected pre-commit failure leave zero partial financial/audit effects; for a read/UI-only task assert no mutation. Reuse the same fixture rather than duplicating equivalent tests.

### F. Verification

**Status: NOT RUN - SPEC_ONLY.** The following filter becomes executable only after the NEW test class and prerequisites are implemented.

Requires Task 01's isolated PostgreSQL 18/API fixture, .NET 10 SDK and explicitly validated QTS_TEST_POSTGRES_CONNECTION. Fixture DDL/data writes are permitted only in a later authorized test run; never inherit production credentials.

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~TestQtsBilling.Integration.SupplierBillTests"
```

**PASS:** owning cases and relevant existing regression filters pass, with exact committed-state assertions where applicable; no skipped required prerequisite, new unreviewed dependency, secret leak or `git diff --check` failure. A build or mocked response alone is not financial proof.

Select the checked server or Windows build command in §6.3 only when this task changes that host. Full multi-platform regression belongs in CI/release, not every local edit.

### G. Definition of done

Every supplier invoice has an immutable recognized liability matched to goods/service acceptance without adding stock twice. Evidence includes the exact implementation SHA, focused test outcome and minimal reviewed diff. All new constraints/contracts/permissions/host wiring ship together under **G6**; unresolved external prerequisites stay explicit.

No unnecessary dependency or compatibility path remains. Recovery preserves historical facts and pending money. Record remaining blockers; spec completion is not implementation completion or owner production approval.

<a id="task-46"></a>
## Atomic Task 46 - Complete supplier payments, advances and credit allocations

### A. Objective

**Priority:** P1. **Requirements:** `R06`, `R08`, `R13`, `R14`. **Findings:** `F14`, `F30`, `F34`.
**Dependencies:** [Task 11](#task-11), [Task 13](#task-13), [Task 17](#task-17), [Task 45](#task-45) **Integration gate:** G6.

**Current → target:** The repository does not expose a complete supplier liability-to-settlement workflow. → AP payments and supplier advances can be recorded, allocated, cleared and reversed without overstating paid cash or erasing obligations.

**Acceptance:** The target behavior is observable through the owning API/UI/store and every applicable scenario in E passes; release additionally requires the named gate, not just this file diff.

**Non-goals:** No automated bank transfer execution, arbitrary supplier balance adjustment or provider-specific compatibility.

### B. Scope

| Action | Exact path | Change / exclusion |
|---|---|---|
| CREATE — NEW | `QtsWebApp/QtsWebApp/Features/Payables/SupplierPaymentEndpoints.cs` | Authorized payment, allocation, clearing and reversal commands. |
| CREATE — NEW | `QtsBilling.Persistence.Postgres/Entities/SupplierSettlements.cs` | Payment/advance/credit allocation identities and immutable transitions. |
| CREATE — NEW | `QtsBilling.Contracts/Payables/SupplierPaymentDtos.cs` | Native payment and supplier statement/open-balance contracts. |
| GENERATE — NEW | `QtsBilling.Persistence.Postgres/Migrations/` | NEW SupplierSettlementAllocations with amount/currency/party/source guards. |
| CREATE — NEW test | `TestQtsBilling/Integration/SupplierSettlementTests.cs` | Owning focused assertions below; reuse fixtures rather than duplicate a test infrastructure. |

MODIFY existing `QtsBilling.Persistence.Postgres/BillingDbContext.cs` and the appropriate checked `BillingDbContext.Model.cs` / `BillingDbContext.Model.Financial.cs` only for the listed model; GENERATE designer/snapshot companions with the named new migration.

MODIFY existing `QtsWebApp/QtsWebApp/Program.cs` only for required route/service registration, and `QtsBilling.Contracts/Identity/IdentityDtos.cs` / `QtsWebApp/QtsWebApp/Infrastructure/QtsIdentity.cs` only for this task's explicit D02 policies.

All other production paths are excluded except the narrow, explicitly enumerated [D17 integration bindings](#d17). Applied migrations, posted facts and unresolved cash are never edited/deleted by this scope.

### C. Verified anchors and NEW insertions

- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Purchasing/PurchasingEndpoints.cs — source lines 1020-1170](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Purchasing/PurchasingEndpoints.cs#L1020-L1170): `PostReceiptAsync commit path`. [E30](#e-e30).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Sales/CashRegisterEndpoints.cs — source lines 409-550](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Sales/CashRegisterEndpoints.cs#L409-L550): `AddMovementAsync`. [E33](#e-e33).
- **EXISTING / VERIFIED:** [QtsBilling.Persistence.Postgres/BillingDbContext.Model.Financial.cs — source lines 210-445](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Persistence.Postgres/BillingDbContext.Model.Financial.cs#L210-L445): `ConfigureAccounting; ConfigurePurchasing`. [E07](#e-e07).

All CREATE/GENERATE symbols, routes and constraints in B/D are **NEW**. Source presence is not runtime proof; do not infer executed results from old evidence prose.

### D. Ordered implementation recipe

1. Pay one or more same-supplier/currency/business AP items under ordered locks; validate actual cash/bank/clearing evidence and actor approval. Post DR AP / CR settled cash/bank, or the appropriate pending-clearing classification.
2. Overpayment is allowed only as explicit supplier advance asset, not a negative AP item. Allocate advance/credit later against open bills with separate immutable allocation rows and appropriate reclassification journal.
3. Prevent allocated amount exceeding settlement or open bill after concurrent actions. Same scoped key/result replays; changed supplier/bills/body conflicts. Payment cancellation/bounce/reversal appends facts and reopens items, not row deletion.
4. Link cash payouts to the actual drawer/session and bank payments to the selected account; no provider call inside the financial transaction. Cards remain unavailable absent an approved integration.
5. Expose current/as-of supplier statement, unapplied advances, due ageing, credit balance and settlement evidence. Sum of AP items reconciles to the configured AP control account.

Apply the specific D01–D17 policies relevant to this slice. Finish the narrow route/model/client registration in B, record generated migration paths, and preserve a safe stop/fix-forward path before presenting the diff for review.

### E. Focused tests

**NEW file:** `TestQtsBilling/Integration/SupplierSettlementTests.cs`. Name the class `SupplierSettlementTests` in the directory-derived namespace specified in §6.3.

- **T46.01:** Given payment across two bills, when the task-specific command or transition is exercised, then allocation cannot exceed paid amount or outstanding balances.
- **T46.02:** Given explicit advance, when the task-specific command or transition is exercised, then supplier-advance asset is recognized and later application clears it correctly.
- **T46.03:** Given cheque bounce or payment reversal, when the task-specific command or transition is exercised, then original history remains and the liability reopens.
- **T46.04:** Given duplicate/concurrent payment command, when the task-specific command or transition is exercised, then no extra bank/drawer outflow is recorded.
- **T46.05:** Given as-of statement before later payment, when the task-specific command or transition is exercised, then historical AP remains correct.

Reuse existing or earlier-task fixtures. For a mutation, also assert denied tenant/location/permission and injected pre-commit failure leave zero partial financial/audit effects; for a read/UI-only task assert no mutation. Reuse the same fixture rather than duplicating equivalent tests.

### F. Verification

**Status: NOT RUN - SPEC_ONLY.** The following filter becomes executable only after the NEW test class and prerequisites are implemented.

Requires Task 01's isolated PostgreSQL 18/API fixture, .NET 10 SDK and explicitly validated QTS_TEST_POSTGRES_CONNECTION. Fixture DDL/data writes are permitted only in a later authorized test run; never inherit production credentials.

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~TestQtsBilling.Integration.SupplierSettlementTests"
```

**PASS:** owning cases and relevant existing regression filters pass, with exact committed-state assertions where applicable; no skipped required prerequisite, new unreviewed dependency, secret leak or `git diff --check` failure. A build or mocked response alone is not financial proof.

Select the checked server or Windows build command in §6.3 only when this task changes that host. Full multi-platform regression belongs in CI/release, not every local edit.

### G. Definition of done

AP payments and supplier advances can be recorded, allocated, cleared and reversed without overstating paid cash or erasing obligations. Evidence includes the exact implementation SHA, focused test outcome and minimal reviewed diff. All new constraints/contracts/permissions/host wiring ship together under **G6**; unresolved external prerequisites stay explicit.

No unnecessary dependency or compatibility path remains. Recovery preserves historical facts and pending money. Record remaining blockers; spec completion is not implementation completion or owner production approval.

<a id="task-47"></a>
## Atomic Task 47 - Deliver procurement and supplier-account workbenches

### A. Objective

**Priority:** P1. **Requirements:** `R11`, `R13`, `R17`. **Findings:** `F30`, `F34`.
**Dependencies:** [Task 30](#task-30), [Task 34](#task-34), [Task 44](#task-44), [Task 45](#task-45), [Task 46](#task-46) **Integration gate:** G6.

**Current → target:** Existing procurement APIs do not provide a complete purchaser/warehouse/accountant user journey. → Users can complete procure-to-pay through the UI and understand why commitments, stock receipts, bills and money are different events.

**Acceptance:** The target behavior is observable through the owning API/UI/store and every applicable scenario in E passes; release additionally requires the named gate, not just this file diff.

**Non-goals:** Three coupled pages form one operator journey; no new portal, workflow engine or batch invoice automation.

### B. Scope

| Action | Exact path | Change / exclusion |
|---|---|---|
| CREATE — NEW | `QtsBilling.Ui/Pages/Purchasing/PurchaseOrders.razor` | PO create/place/amend/outstanding/short-close UI. |
| CREATE — NEW | `QtsBilling.Ui/Pages/Purchasing/GoodsReceipts.razor` | Partial receiving, source-linked returns and printable receipt detail. |
| CREATE — NEW | `QtsBilling.Ui/Pages/Payables/SupplierAccounts.razor` | Bill matching, posting, payment/advance allocation and statement drilldown. |
| CREATE — NEW test | `TestQtsBilling/Ui/ProcureToPayJourneyTests.cs` | Owning focused assertions below; reuse fixtures rather than duplicate a test infrastructure. |

Apply only the exact typed-client/host registration and routing bindings in [D17](#d17); no unlisted feature is included. A new page cannot be marked complete before its owning API is wired.

All other production paths are excluded except the narrow, explicitly enumerated [D17 integration bindings](#d17). Applied migrations, posted facts and unresolved cash are never edited/deleted by this scope.

### C. Verified anchors and NEW insertions

- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Purchasing/PurchasingEndpoints.cs — source lines 1-120](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Purchasing/PurchasingEndpoints.cs#L1-L120): `MapPurchasingEndpoints`. [E28](#e-e28).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Purchasing/PurchasingEndpoints.cs — source lines 640-1030](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Purchasing/PurchasingEndpoints.cs#L640-L1030): `ConfirmOrderAsync; CancelOrderAsync; PostReceiptAsync`. [E29](#e-e29).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Purchasing/PurchasingEndpoints.cs — source lines 1020-1170](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Purchasing/PurchasingEndpoints.cs#L1020-L1170): `PostReceiptAsync commit path`. [E30](#e-e30).
- **EXISTING / VERIFIED:** [README.md — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/README.md): `Architecture / status / project descriptions`. [E48](#e-e48).

All CREATE/GENERATE symbols, routes and constraints in B/D are **NEW**. Source presence is not runtime proof; do not infer executed results from old evidence prose.

### D. Ordered implementation recipe

1. Expose typed clients and routes for PO/order detail, due deliveries, GRN/returns, supplier bills and AP. Separate purchaser, warehouse and accountant permissions rather than one unrestricted administrative screen.
2. PO forms select supplier/location/products/sell units and show agreed terms/tax; placed orders are read-only except explicit amendments. Receiving shows ordered/received/returned/cancelled/outstanding amounts.
3. Bill matching selects actual receipt lines, validates supplier reference and shows price/tax variance requiring approval. Service bills use expense category/account without warehouse fields.
4. Payment view confirms actual tender/clearing and exact allocations/advance, with durable command key. History links PO→receipt→bill→payment/credit/return; print does not create accounting effects.
5. Implement cursor paging, keyboard quantity entry, inaccessible/inactive lookup errors, stale-version recovery and offline-disabled states; no false success for pending bank/cheque money.

Apply the specific D01–D17 policies relevant to this slice. Finish the narrow route/model/client registration in B, record generated migration paths, and preserve a safe stop/fix-forward path before presenting the diff for review.

### E. Focused tests

**NEW file:** `TestQtsBilling/Ui/ProcureToPayJourneyTests.cs`. Name the class `ProcureToPayJourneyTests` in the directory-derived namespace specified in §6.3.

- **T47.01:** Given a goods purchase, when the task-specific command or transition is exercised, then PO→partial GRN→bill→partial payment→return/credit completes through UI and reconciles.
- **T47.02:** Given service purchase, when the task-specific command or transition is exercised, then acceptance/bill/payment works without stock.
- **T47.03:** Given over-receipt or duplicate supplier invoice, when the task-specific command or transition is exercised, then the form shows an actionable conflict.
- **T47.04:** Given warehouse-only staff, when the task-specific command or transition is exercised, then receiving works but bill/payment approval is denied.
- **T47.05:** Given network timeout during payment, when the task-specific command or transition is exercised, then the original result is resolved before another command is permitted.

Reuse existing or earlier-task fixtures. For a mutation, also assert denied tenant/location/permission and injected pre-commit failure leave zero partial financial/audit effects; for a read/UI-only task assert no mutation. Reuse the same fixture rather than duplicating equivalent tests.

### F. Verification

**Status: NOT RUN - SPEC_ONLY.** The following filter becomes executable only after the NEW test class and prerequisites are implemented.

Requires the owning APIs, shared UI smoke/Playwright setup and disposable native data. The actual Windows/WebView2 checks remain a separate platform gate.

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~TestQtsBilling.Ui.ProcureToPayJourneyTests"
```

**PASS:** owning cases and relevant existing regression filters pass, with exact committed-state assertions where applicable; no skipped required prerequisite, new unreviewed dependency, secret leak or `git diff --check` failure. A build or mocked response alone is not financial proof.

Select the checked server or Windows build command in §6.3 only when this task changes that host. Full multi-platform regression belongs in CI/release, not every local edit.

### G. Definition of done

Users can complete procure-to-pay through the UI and understand why commitments, stock receipts, bills and money are different events. Evidence includes the exact implementation SHA, focused test outcome and minimal reviewed diff. All new constraints/contracts/permissions/host wiring ship together under **G6**; unresolved external prerequisites stay explicit.

No unnecessary dependency or compatibility path remains. Recovery preserves historical facts and pending money. Record remaining blockers; spec completion is not implementation completion or owner production approval.

<a id="task-48"></a>
## Atomic Task 48 - Implement controlled stock counts and paired warehouse transfers

### A. Objective

**Priority:** P1. **Requirements:** `R06`, `R07`, `R08`, `R14`. **Findings:** `F18`, `F30`, `F36`.
**Dependencies:** [Task 10](#task-10), [Task 11](#task-11), [Task 14](#task-14), [Task 15](#task-15), [Task 17](#task-17), [Task 33](#task-33), [Task 42](#task-42) **Integration gate:** G6.

**Current → target:** Generic movement enums do not provide a safe complete stock-count or two-location transfer workflow. → An approved count or transfer changes only intended stock and valuation, with complete reasons and balanced financial treatment.

**Acceptance:** The target behavior is observable through the owning API/UI/store and every applicable scenario in E passes; release additionally requires the named gate, not just this file diff.

**Non-goals:** No in-transit logistics, lot/serial valuation, inventory write-off without reason or negative-stock override.

### B. Scope

| Action | Exact path | Change / exclusion |
|---|---|---|
| CREATE — NEW | `QtsWebApp/QtsWebApp/Features/Inventory/StockControlEndpoints.cs` | Count sessions, approved variances and paired transfers. |
| CREATE — NEW | `QtsBilling.Persistence.Postgres/Entities/StockControl.cs` | Count baseline/lines and transfer source/destination quantity/value evidence. |
| CREATE — NEW | `QtsBilling.Contracts/Inventory/StockControlDtos.cs` | Native count/transfer draft, approval and posting contracts. |
| GENERATE — NEW | `QtsBilling.Persistence.Postgres/Migrations/` | NEW StockCountsAndTransfers with source uniqueness and scoped references. |
| CREATE — NEW test | `TestQtsBilling/Integration/StockControlTests.cs` | Owning focused assertions below; reuse fixtures rather than duplicate a test infrastructure. |

MODIFY existing `QtsBilling.Persistence.Postgres/BillingDbContext.cs` and the appropriate checked `BillingDbContext.Model.cs` / `BillingDbContext.Model.Financial.cs` only for the listed model; GENERATE designer/snapshot companions with the named new migration.

MODIFY existing `QtsWebApp/QtsWebApp/Program.cs` only for required route/service registration, and `QtsBilling.Contracts/Identity/IdentityDtos.cs` / `QtsWebApp/QtsWebApp/Infrastructure/QtsIdentity.cs` only for this task's explicit D02 policies.

All other production paths are excluded except the narrow, explicitly enumerated [D17 integration bindings](#d17). Applied migrations, posted facts and unresolved cash are never edited/deleted by this scope.

### C. Verified anchors and NEW insertions

- **EXISTING / VERIFIED:** [QtsBilling.Persistence.Postgres/BillingDbContext.Model.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Persistence.Postgres/BillingDbContext.Model.cs): `ConfigureCore; ConfigureMasterData; ConfigureCatalog; ConfigureInventory`. [E06](#e-e06).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Inventory/InventoryLedger.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Inventory/InventoryLedger.cs): `ApplyAsync`. [E13](#e-e13).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Purchasing/PurchasingEndpoints.cs — source lines 1020-1170](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Purchasing/PurchasingEndpoints.cs#L1020-L1170): `PostReceiptAsync commit path`. [E30](#e-e30).

All CREATE/GENERATE symbols, routes and constraints in B/D are **NEW**. Source presence is not runtime proof; do not infer executed results from old evidence prose.

### D. Ordered implementation recipe

1. Stock counts record location, selected variants, baseline/version/time and observed quantity without immediately changing on-hand. Finalization rechecks movements since baseline; choose a brief count freeze or require recount of changed lines, never silently overwrite intervening sales.
2. Approved count variance calls InventoryLedger/InventoryValuation with configured stock gain/loss accounts and reason. Prevent self-approval where separation of duties is enabled; reject negative physical quantities and unsupported precision.
3. For the base release, transfer is one atomic dispatched-and-received operation between two active locations in the same business. Lock both keys in canonical order, issue source quantity/value and receive the exact same quantity/value; reservations cannot be consumed accidentally.
4. Transfer draft alone changes nothing. Same key/result replays one paired movement group; rejection at destination rolls back source. No generic TransferOut/TransferIn API can bypass the pairing.
5. Audit draft/approval/post actors and retain original count sheets/transfer lines. UI/history query reports count variance and source/destination references; future multi-stage in-transit logistics is explicitly outside this base flow.

Apply the specific D01–D17 policies relevant to this slice. Finish the narrow route/model/client registration in B, record generated migration paths, and preserve a safe stop/fix-forward path before presenting the diff for review.

### E. Focused tests

**NEW file:** `TestQtsBilling/Integration/StockControlTests.cs`. Name the class `StockControlTests` in the directory-derived namespace specified in §6.3.

- **T48.01:** Given transfer of 5 units, when the task-specific command or transition is exercised, then company quantity/value and profit are unchanged while locations update correctly.
- **T48.02:** Given failure on destination or concurrent reverse-direction transfer, when the task-specific command or transition is exercised, then no stranded source decrement occurs.
- **T48.03:** Given a sale after count baseline, when the task-specific command or transition is exercised, then finalization requires consistent reconciliation/recount instead of overwriting stock.
- **T48.04:** Given approved variance, when the task-specific command or transition is exercised, then stock value and gain/loss journal agree exactly.
- **T48.05:** Given unauthorized/self-approved or cross-business transfer, when the task-specific command or transition is exercised, then the command is refused.

Reuse existing or earlier-task fixtures. For a mutation, also assert denied tenant/location/permission and injected pre-commit failure leave zero partial financial/audit effects; for a read/UI-only task assert no mutation. Reuse the same fixture rather than duplicating equivalent tests.

### F. Verification

**Status: NOT RUN - SPEC_ONLY.** The following filter becomes executable only after the NEW test class and prerequisites are implemented.

Requires Task 01's isolated PostgreSQL 18/API fixture, .NET 10 SDK and explicitly validated QTS_TEST_POSTGRES_CONNECTION. Fixture DDL/data writes are permitted only in a later authorized test run; never inherit production credentials.

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~TestQtsBilling.Integration.StockControlTests"
```

**PASS:** owning cases and relevant existing regression filters pass, with exact committed-state assertions where applicable; no skipped required prerequisite, new unreviewed dependency, secret leak or `git diff --check` failure. A build or mocked response alone is not financial proof.

Select the checked server or Windows build command in §6.3 only when this task changes that host. Full multi-platform regression belongs in CI/release, not every local edit.

### G. Definition of done

An approved count or transfer changes only intended stock and valuation, with complete reasons and balanced financial treatment. Evidence includes the exact implementation SHA, focused test outcome and minimal reviewed diff. All new constraints/contracts/permissions/host wiring ship together under **G6**; unresolved external prerequisites stay explicit.

No unnecessary dependency or compatibility path remains. Recovery preserves historical facts and pending money. Record remaining blockers; spec completion is not implementation completion or owner production approval.

<a id="task-49"></a>
## Atomic Task 49 - Deliver inventory visibility, counts, transfers and reorder workbench

### A. Objective

**Priority:** P1. **Requirements:** `R07`, `R11`, `R14`, `R17`. **Findings:** `F30`, `F36`.
**Dependencies:** [Task 30](#task-30), [Task 33](#task-33), [Task 34](#task-34), [Task 44](#task-44), [Task 48](#task-48) **Integration gate:** G6.

**Current → target:** Inventory APIs exist without a complete user-facing stock-control workflow. → Managers can inspect location stock, investigate movement/value differences and replenish through controlled native documents.

**Acceptance:** The target behavior is observable through the owning API/UI/store and every applicable scenario in E passes; release additionally requires the named gate, not just this file diff.

**Non-goals:** No automated purchasing, scanner SDK or warehouse-management-system rewrite.

### B. Scope

| Action | Exact path | Change / exclusion |
|---|---|---|
| CREATE — NEW | `QtsBilling.Ui/Pages/Inventory/Stock.razor` | On-hand/reserved/available and quantity/value ledger drilldown. |
| CREATE — NEW | `QtsBilling.Ui/Pages/Inventory/CountsAndTransfers.razor` | Draft/observe/approve/post controlled stock operations. |
| CREATE — NEW | `QtsBilling.Ui/Pages/Inventory/Reorder.razor` | Low-stock suggestions and explicit draft PO creation. |
| CREATE — NEW test | `TestQtsBilling/Ui/InventoryJourneyTests.cs` | Owning focused assertions below; reuse fixtures rather than duplicate a test infrastructure. |

Apply only the exact typed-client/host registration and routing bindings in [D17](#d17); no unlisted feature is included. A new page cannot be marked complete before its owning API is wired.

All other production paths are excluded except the narrow, explicitly enumerated [D17 integration bindings](#d17). Applied migrations, posted facts and unresolved cash are never edited/deleted by this scope.

### C. Verified anchors and NEW insertions

- **EXISTING / VERIFIED:** [QtsBilling.Persistence.Postgres/BillingDbContext.Model.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Persistence.Postgres/BillingDbContext.Model.cs): `ConfigureCore; ConfigureMasterData; ConfigureCatalog; ConfigureInventory`. [E06](#e-e06).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Inventory/InventoryLedger.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Inventory/InventoryLedger.cs): `ApplyAsync`. [E13](#e-e13).
- **EXISTING / VERIFIED:** [QtsBilling.Ui/Pages/Pos.razor.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Ui/Pages/Pos.razor.cs): `LineTax; LineTotal; CaptureAsync`. [E26](#e-e26).

All CREATE/GENERATE symbols, routes and constraints in B/D are **NEW**. Source presence is not runtime proof; do not infer executed results from old evidence prose.

### D. Ordered implementation recipe

1. Expose scoped location/variant stock lists with search, cursor, base/sell-unit labels, reserved/available and last movement. Cost/value fields require finance/stock-value permission; cashiers see only permitted selling availability.
2. Drill from a balance to immutable movements and their sale/receipt/return/transfer/count sources. Reconciliation differences are visible errors, never hidden or auto-adjusted.
3. Count workflow shows baseline and intervening movement warning, observed quantity and approval reason. Transfer workflow requires both locations and shows paired source/destination posting result.
4. Use configurable per-location reorder point/target quantities to suggest replenishment from available stock and open POs. Creation is an explicit reviewed draft PO; suggestions do not reserve, buy or post stock automatically.
5. Keyboard scanning/count entry, bulk selection with bounded rows, empty/no-permission/stale/offline states and printable count/transfer sheet must work. Offline inventory editing remains unavailable.

Apply the specific D01–D17 policies relevant to this slice. Finish the narrow route/model/client registration in B, record generated migration paths, and preserve a safe stop/fix-forward path before presenting the diff for review.

### E. Focused tests

**NEW file:** `TestQtsBilling/Ui/InventoryJourneyTests.cs`. Name the class `InventoryJourneyTests` in the directory-derived namespace specified in §6.3.

- **T49.01:** Given low stock with an already-open PO, when the task-specific command or transition is exercised, then suggestion avoids blindly duplicating outstanding supply.
- **T49.02:** Given count variance or transfer, when the task-specific command or transition is exercised, then UI completes the approval/posting journey and shows its sources.
- **T49.03:** Given a reconciliation difference, when the task-specific command or transition is exercised, then it is highlighted without a silent fix.
- **T49.04:** Given cost-restricted cashier, when the task-specific command or transition is exercised, then neither UI nor API exposes valuation.
- **T49.05:** Given pagination/filter change, when the task-specific command or transition is exercised, then all authorized warehouse stock remains reachable and scope-correct.

Reuse existing or earlier-task fixtures. For a mutation, also assert denied tenant/location/permission and injected pre-commit failure leave zero partial financial/audit effects; for a read/UI-only task assert no mutation. Reuse the same fixture rather than duplicating equivalent tests.

### F. Verification

**Status: NOT RUN - SPEC_ONLY.** The following filter becomes executable only after the NEW test class and prerequisites are implemented.

Requires the owning APIs, shared UI smoke/Playwright setup and disposable native data. The actual Windows/WebView2 checks remain a separate platform gate.

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~TestQtsBilling.Ui.InventoryJourneyTests"
```

**PASS:** owning cases and relevant existing regression filters pass, with exact committed-state assertions where applicable; no skipped required prerequisite, new unreviewed dependency, secret leak or `git diff --check` failure. A build or mocked response alone is not financial proof.

Select the checked server or Windows build command in §6.3 only when this task changes that host. Full multi-platform regression belongs in CI/release, not every local edit.

### G. Definition of done

Managers can inspect location stock, investigate movement/value differences and replenish through controlled native documents. Evidence includes the exact implementation SHA, focused test outcome and minimal reviewed diff. All new constraints/contracts/permissions/host wiring ship together under **G6**; unresolved external prerequisites stay explicit.

No unnecessary dependency or compatibility path remains. Recovery preserves historical facts and pending money. Record remaining blockers; spec completion is not implementation completion or owner production approval.

<a id="task-50"></a>
## Atomic Task 50 - Complete expense evidence, tax classification and approval

### A. Objective

**Priority:** P1. **Requirements:** `R06`, `R08`, `R14`. **Findings:** `F14`, `F30`, `F34`, `F38`.
**Dependencies:** [Task 03](#task-03), [Task 04](#task-04), [Task 05](#task-05), [Task 08](#task-08), [Task 10](#task-10), [Task 11](#task-11), [Task 13](#task-13), [Task 17](#task-17), [Task 45](#task-45) **Integration gate:** G6.

**Current → target:** Expense posting exists but broader classification/settlement/evidence workflows and screens are missing; default COGS is unsuitable for general operating expenses. → Each expense is classified correctly, approved where required and supported by evidence without creating fictitious paid cash.

**Acceptance:** The target behavior is observable through the owning API/UI/store and every applicable scenario in E passes; release additionally requires the named gate, not just this file diff.

**Non-goals:** No payroll, employee travel-claims platform or jurisdiction-specific deductible-tax certification.

### B. Scope

| Action | Exact path | Change / exclusion |
|---|---|---|
| MODIFY | `QtsWebApp/QtsWebApp/Features/Expenses/ExpenseEndpoints.cs` | Correct operating-account selection, input tax, settlement and approval. |
| MODIFY | `QtsBilling.Contracts/Expenses/ExpenseDtos.cs` | Native draft/approve/post/evidence and explicit paid vs unpaid state. |
| CREATE — NEW | `QtsBilling.Ui/Pages/Expenses/Expenses.razor` | Expense entry/review/post/reversal and evidence attachment. |
| CREATE — NEW test | `TestQtsBilling/Integration/ExpenseControlTests.cs` | Owning focused assertions below; reuse fixtures rather than duplicate a test infrastructure. |

MODIFY existing `QtsWebApp/QtsWebApp/Program.cs` only for required route/service registration, and `QtsBilling.Contracts/Identity/IdentityDtos.cs` / `QtsWebApp/QtsWebApp/Infrastructure/QtsIdentity.cs` only for this task's explicit D02 policies.

Apply only the exact typed-client/host registration and routing bindings in [D17](#d17); no unlisted feature is included. A new page cannot be marked complete before its owning API is wired.

All other production paths are excluded except the narrow, explicitly enumerated [D17 integration bindings](#d17). Applied migrations, posted facts and unresolved cash are never edited/deleted by this scope.

### C. Verified anchors and NEW insertions

- **EXISTING / VERIFIED:** [QtsBilling.Persistence.Postgres/BillingDbContext.Model.Financial.cs — source lines 210-445](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Persistence.Postgres/BillingDbContext.Model.Financial.cs#L210-L445): `ConfigureAccounting; ConfigurePurchasing`. [E07](#e-e07).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Purchasing/PurchasingEndpoints.cs — source lines 1020-1170](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Purchasing/PurchasingEndpoints.cs#L1020-L1170): `PostReceiptAsync commit path`. [E30](#e-e30).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Sales/CashRegisterEndpoints.cs — source lines 409-550](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Sales/CashRegisterEndpoints.cs#L409-L550): `AddMovementAsync`. [E33](#e-e33).
- **EXISTING / VERIFIED:** [docs/migration/decisions.md — source lines 1-155](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/docs/migration/decisions.md#L1-L155): `D-01...D-41 and historical decisions`. [E44](#e-e44).

All CREATE/GENERATE symbols, routes and constraints in B/D are **NEW**. Source presence is not runtime proof; do not infer executed results from old evidence prose.

### D. Ordered implementation recipe

1. Use configured expense-category account and tax recoverability; never default all operating expenses to COGS 5000. Validate active same-business category/account, amount/currency, date/period, supplier and evidence references.
2. Distinguish paid expense from supplier bill/unpaid obligation: settled cash/bank creates payment, unpaid items route to AP. A cheque/reference is not settled bank cash; cards stay disabled absent verified PSP.
3. Provide Draft → Submitted → Approved → Posted where configured approval threshold requires it; otherwise an authorized explicit post still records actor/reason. Approval binds amount/category/evidence/version, so edits invalidate it.
4. One transaction posts expense/input tax, settlement/AP, drawer linkage if cash, journal, audit and idempotency receipt. Correction is a linked reversal/new document; no update/delete of posted expense terms.
5. UI supports receipt attachment through Task 55, field errors, duplicate supplier/reference warning, audit/ledger links, permission-aware post/reverse and bounded list filters. Required evidence policy blocks production posting when unmet.

Apply the specific D01–D17 policies relevant to this slice. Finish the narrow route/model/client registration in B, record generated migration paths, and preserve a safe stop/fix-forward path before presenting the diff for review.

### E. Focused tests

**NEW file:** `TestQtsBilling/Integration/ExpenseControlTests.cs`. Name the class `ExpenseControlTests` in the directory-derived namespace specified in §6.3.

- **T50.01:** Given an office expense, when the task-specific command or transition is exercised, then operating expense/input tax—not COGS—is debited correctly.
- **T50.02:** Given unpaid or uncleared cheque expense, when the task-specific command or transition is exercised, then cash is not falsely reduced as settled.
- **T50.03:** Given an approved expense edited before posting, when the task-specific command or transition is exercised, then reapproval is required.
- **T50.04:** Given repeated post/reverse or rollback, when the task-specific command or transition is exercised, then one complete financial effect exists.
- **T50.05:** Given foreign account/category/evidence, when the task-specific command or transition is exercised, then the request is denied without disclosure.

Reuse existing or earlier-task fixtures. For a mutation, also assert denied tenant/location/permission and injected pre-commit failure leave zero partial financial/audit effects; for a read/UI-only task assert no mutation. Reuse the same fixture rather than duplicating equivalent tests.

### F. Verification

**Status: NOT RUN - SPEC_ONLY.** The following filter becomes executable only after the NEW test class and prerequisites are implemented.

Requires Task 01's isolated PostgreSQL 18/API fixture, .NET 10 SDK and explicitly validated QTS_TEST_POSTGRES_CONNECTION. Fixture DDL/data writes are permitted only in a later authorized test run; never inherit production credentials.

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~TestQtsBilling.Integration.ExpenseControlTests"
```

**PASS:** owning cases and relevant existing regression filters pass, with exact committed-state assertions where applicable; no skipped required prerequisite, new unreviewed dependency, secret leak or `git diff --check` failure. A build or mocked response alone is not financial proof.

Select the checked server or Windows build command in §6.3 only when this task changes that host. Full multi-platform regression belongs in CI/release, not every local edit.

### G. Definition of done

Each expense is classified correctly, approved where required and supported by evidence without creating fictitious paid cash. Evidence includes the exact implementation SHA, focused test outcome and minimal reviewed diff. All new constraints/contracts/permissions/host wiring ship together under **G6**; unresolved external prerequisites stay explicit.

No unnecessary dependency or compatibility path remains. Recovery preserves historical facts and pending money. Record remaining blockers; spec completion is not implementation completion or owner production approval.

<a id="task-51"></a>
## Atomic Task 51 - Implement native bank statements and reconciliation

### A. Objective

**Priority:** P1. **Requirements:** `R06`, `R08`, `R14`, `R15`. **Findings:** `F14`, `F30`, `F34`.
**Dependencies:** [Task 10](#task-10), [Task 11](#task-11), [Task 13](#task-13), [Task 17](#task-17), [Task 39](#task-39), [Task 46](#task-46), [Task 50](#task-50) **Integration gate:** G6.

**Current → target:** Cash summary is not a bank reconciliation or payment-clearing workflow. → An accountant can match actual statement activity to posted settlements and explain every difference without auto-creating money.

**Acceptance:** The target behavior is observable through the owning API/UI/store and every applicable scenario in E passes; release additionally requires the named gate, not just this file diff.

**Non-goals:** No bank API integration, payment initiation, automatic money movement or arbitrary CSV-to-SQL importer.

### B. Scope

| Action | Exact path | Change / exclusion |
|---|---|---|
| CREATE — NEW | `QtsWebApp/QtsWebApp/Features/Banking/BankReconciliationEndpoints.cs` | Bounded CSV intake, matching, clearing and statement finalization. |
| CREATE — NEW | `QtsBilling.Persistence.Postgres/Entities/Banking.cs` | Bank accounts, immutable statement lines, match allocations and reconciliation periods. |
| CREATE — NEW | `QtsBilling.Contracts/Banking/BankDtos.cs` | Explicit native CSV mapping/preview and match/adjustment contracts. |
| GENERATE — NEW | `QtsBilling.Persistence.Postgres/Migrations/` | NEW BankReconciliation with statement/source/match uniqueness. |
| CREATE — NEW | `QtsBilling.Ui/Pages/Banking/BankAccounts.razor` | Account selection and statement source setup; no direct payment execution. |
| CREATE — NEW test | `TestQtsBilling/Integration/BankReconciliationTests.cs` | Owning focused assertions below; reuse fixtures rather than duplicate a test infrastructure. |

MODIFY existing `QtsBilling.Persistence.Postgres/BillingDbContext.cs` and the appropriate checked `BillingDbContext.Model.cs` / `BillingDbContext.Model.Financial.cs` only for the listed model; GENERATE designer/snapshot companions with the named new migration.

MODIFY existing `QtsWebApp/QtsWebApp/Program.cs` only for required route/service registration, and `QtsBilling.Contracts/Identity/IdentityDtos.cs` / `QtsWebApp/QtsWebApp/Infrastructure/QtsIdentity.cs` only for this task's explicit D02 policies.

Apply only the exact typed-client/host registration and routing bindings in [D17](#d17); no unlisted feature is included. A new page cannot be marked complete before its owning API is wired.

All other production paths are excluded except the narrow, explicitly enumerated [D17 integration bindings](#d17). Applied migrations, posted facts and unresolved cash are never edited/deleted by this scope.

### C. Verified anchors and NEW insertions

- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Sales/CashRegisterEndpoints.cs — source lines 409-550](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Sales/CashRegisterEndpoints.cs#L409-L550): `AddMovementAsync`. [E33](#e-e33).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Reporting/ReportEndpoints.cs — source lines 240-550](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Reporting/ReportEndpoints.cs#L240-L550): `StockSummaryAsync; SalesSummaryAsync`. [E35](#e-e35).
- **EXISTING / VERIFIED:** [QtsBilling.Persistence.Postgres/BillingDbContext.Model.Financial.cs — source lines 210-445](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Persistence.Postgres/BillingDbContext.Model.Financial.cs#L210-L445): `ConfigureAccounting; ConfigurePurchasing`. [E07](#e-e07).

All CREATE/GENERATE symbols, routes and constraints in B/D are **NEW**. Source presence is not runtime proof; do not infer executed results from old evidence prose.

### D. Ordered implementation recipe

1. Associate each bank account with a same-business ledger account/currency. Import a native bounded CSV with explicit date/amount/reference columns and preview; hash file/batch, identify lines stably and reject duplicate/replayed statement imports.
2. Match bank lines against posted settlements/clearing using amount/date/reference suggestions, but require human confirmation. Enforce one allocation capacity per bank line and ledger item; many-to-one splits require exact conserved sums.
3. Unmatched fees/interest or cheque clearing/bounce becomes an explicit authorized journal/settlement command via existing services, not a raw ledger patch. Provider feeds and automatic bank transfers are not part of this task.
4. Finalized reconciliation binds opening statement balance + signed movements = closing balance and adjusted ledger balance plus identified outstanding items. Lock finalized lines/matches; correction reopens through audited version/reversal authority.
5. Expose current/as-of outstanding and cleared payments for AR/AP/cash reports. CSV field length/encoding/formula injection controls and tenant evidence access are required.

Apply the specific D01–D17 policies relevant to this slice. Finish the narrow route/model/client registration in B, record generated migration paths, and preserve a safe stop/fix-forward path before presenting the diff for review.

### E. Focused tests

**NEW file:** `TestQtsBilling/Integration/BankReconciliationTests.cs`. Name the class `BankReconciliationTests` in the directory-derived namespace specified in §6.3.

- **T51.01:** Given the same statement imported twice, when the task-specific command or transition is exercised, then no duplicate bank lines exist.
- **T51.02:** Given a line already matched, when the task-specific command or transition is exercised, then concurrent second matching cannot consume it again.
- **T51.03:** Given a cheque later cleared or bounced, when the task-specific command or transition is exercised, then clearing/bank and AR/AP states reconcile.
- **T51.04:** Given unmatched fee, when the task-specific command or transition is exercised, then explicit approved posting—not auto-import—creates its journal.
- **T51.05:** Given finalized statement with a mismatch, when the task-specific command or transition is exercised, then finalization fails and the difference remains visible.

Reuse existing or earlier-task fixtures. For a mutation, also assert denied tenant/location/permission and injected pre-commit failure leave zero partial financial/audit effects; for a read/UI-only task assert no mutation. Reuse the same fixture rather than duplicating equivalent tests.

### F. Verification

**Status: NOT RUN - SPEC_ONLY.** The following filter becomes executable only after the NEW test class and prerequisites are implemented.

Requires Task 01's isolated PostgreSQL 18/API fixture, .NET 10 SDK and explicitly validated QTS_TEST_POSTGRES_CONNECTION. Fixture DDL/data writes are permitted only in a later authorized test run; never inherit production credentials.

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~TestQtsBilling.Integration.BankReconciliationTests"
```

**PASS:** owning cases and relevant existing regression filters pass, with exact committed-state assertions where applicable; no skipped required prerequisite, new unreviewed dependency, secret leak or `git diff --check` failure. A build or mocked response alone is not financial proof.

Select the checked server or Windows build command in §6.3 only when this task changes that host. Full multi-platform regression belongs in CI/release, not every local edit.

### G. Definition of done

An accountant can match actual statement activity to posted settlements and explain every difference without auto-creating money. Evidence includes the exact implementation SHA, focused test outcome and minimal reviewed diff. All new constraints/contracts/permissions/host wiring ship together under **G6**; unresolved external prerequisites stay explicit.

No unnecessary dependency or compatibility path remains. Recovery preserves historical facts and pending money. Record remaining blockers; spec completion is not implementation completion or owner production approval.

<a id="task-52"></a>
## Atomic Task 52 - Deliver accounting, cash/bank and period-close workbenches

### A. Objective

**Priority:** P1. **Requirements:** `R06`, `R14`, `R17`. **Findings:** `F20`, `F30`, `F34`.
**Dependencies:** [Task 30](#task-30), [Task 39](#task-39), [Task 46](#task-46), [Task 48](#task-48), [Task 50](#task-50), [Task 51](#task-51) **Integration gate:** G6.

**Current → target:** Accounting APIs lack a practical accountant-facing workbench and end-of-period control journey. → Accountants can post permitted journals, reconcile cash/bank and close a period with transparent subledger checks.

**Acceptance:** The target behavior is observable through the owning API/UI/store and every applicable scenario in E passes; release additionally requires the named gate, not just this file diff.

**Non-goals:** No spreadsheet-like direct database editing or unexplained suspense entry to force a close.

### B. Scope

| Action | Exact path | Change / exclusion |
|---|---|---|
| CREATE — NEW | `QtsBilling.Ui/Pages/Accounting/Ledger.razor` | Chart, journal entry/reversal and source drilldown. |
| CREATE — NEW | `QtsBilling.Ui/Pages/Accounting/Periods.razor` | Open-period setup, readiness checks and controlled close. |
| CREATE — NEW | `QtsBilling.Ui/Pages/Banking/Reconciliation.razor` | Statement preview/match/unmatched review and finalization. |
| CREATE — NEW test | `TestQtsBilling/Ui/AccountingJourneyTests.cs` | Owning focused assertions below; reuse fixtures rather than duplicate a test infrastructure. |

Apply only the exact typed-client/host registration and routing bindings in [D17](#d17); no unlisted feature is included. A new page cannot be marked complete before its owning API is wired.

All other production paths are excluded except the narrow, explicitly enumerated [D17 integration bindings](#d17). Applied migrations, posted facts and unresolved cash are never edited/deleted by this scope.

### C. Verified anchors and NEW insertions

- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Accounting/LedgerWriter.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Accounting/LedgerWriter.cs): `PostWithinTransactionAsync; AllocateNumberAsync; LocalDate`. [E10](#e-e10).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Invoices/InvoiceEndpoints.cs — source lines 360-650](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Invoices/InvoiceEndpoints.cs#L360-L650): `DeleteAsync; IssueAsync`. [E32](#e-e32).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Sales/CashRegisterEndpoints.cs — source lines 409-550](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Sales/CashRegisterEndpoints.cs#L409-L550): `AddMovementAsync`. [E33](#e-e33).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Reporting/ReportEndpoints.cs — source lines 240-550](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Reporting/ReportEndpoints.cs#L240-L550): `StockSummaryAsync; SalesSummaryAsync`. [E35](#e-e35).

All CREATE/GENERATE symbols, routes and constraints in B/D are **NEW**. Source presence is not runtime proof; do not infer executed results from old evidence prose.

### D. Ordered implementation recipe

1. Use typed clients for accounts/journals/periods/bank workflows. Journal editor validates balanced lines, active scoped accounts, business-local date and explicit memo; source-owned control accounts cannot be bypassed by a generic manual journal without exceptional authorized policy.
2. Posted journal detail is immutable with original/reversal/source links and permission-restricted amounts. No direct account balance editing; account deactivation does not alter history.
3. Period close presents AR/AP/stock/cash/GRNI/GDNI/clearing tie-outs, outstanding unsettled/rejected device cash and relevant unposted documents. A failed required reconciliation blocks close, not a warning users can click past.
4. Bank UI imports/validates statement preview, offers transparent matches, shows unmatched items and routes adjustments to an explicit journal. Closing a statement preserves all match evidence.
5. Keyboard debit/credit entry, error summary focus, date filters, print/export and deliberate irreversible-action confirmations must work. Stale edits and concurrent period close display precise errors and never resubmit as another source.

Apply the specific D01–D17 policies relevant to this slice. Finish the narrow route/model/client registration in B, record generated migration paths, and preserve a safe stop/fix-forward path before presenting the diff for review.

### E. Focused tests

**NEW file:** `TestQtsBilling/Ui/AccountingJourneyTests.cs`. Name the class `AccountingJourneyTests` in the directory-derived namespace specified in §6.3.

- **T52.01:** Given a manual balanced journal, when the task-specific command or transition is exercised, then post→source detail→authorized reversal is usable and balanced.
- **T52.02:** Given period close racing with posting, when the task-specific command or transition is exercised, then the UI reports the one valid outcome without silently changing dates.
- **T52.03:** Given subledger mismatch or unresolved cash, when the task-specific command or transition is exercised, then final close is blocked.
- **T52.04:** Given CSV bank statement, when the task-specific command or transition is exercised, then preview→match→approved adjustment→final reconciliation completes through UI.
- **T52.05:** Given Viewer or Cashier, when the task-specific command or transition is exercised, then unauthorized journals/period closure and sensitive details are denied.

Reuse existing or earlier-task fixtures. For a mutation, also assert denied tenant/location/permission and injected pre-commit failure leave zero partial financial/audit effects; for a read/UI-only task assert no mutation. Reuse the same fixture rather than duplicating equivalent tests.

### F. Verification

**Status: NOT RUN - SPEC_ONLY.** The following filter becomes executable only after the NEW test class and prerequisites are implemented.

Requires the owning APIs, shared UI smoke/Playwright setup and disposable native data. The actual Windows/WebView2 checks remain a separate platform gate.

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~TestQtsBilling.Ui.AccountingJourneyTests"
```

**PASS:** owning cases and relevant existing regression filters pass, with exact committed-state assertions where applicable; no skipped required prerequisite, new unreviewed dependency, secret leak or `git diff --check` failure. A build or mocked response alone is not financial proof.

Select the checked server or Windows build command in §6.3 only when this task changes that host. Full multi-platform regression belongs in CI/release, not every local edit.

### G. Definition of done

Accountants can post permitted journals, reconcile cash/bank and close a period with transparent subledger checks. Evidence includes the exact implementation SHA, focused test outcome and minimal reviewed diff. All new constraints/contracts/permissions/host wiring ship together under **G6**; unresolved external prerequisites stay explicit.

No unnecessary dependency or compatibility path remains. Recovery preserves historical facts and pending money. Record remaining blockers; spec completion is not implementation completion or owner production approval.

<a id="task-53"></a>
## Atomic Task 53 - Repair historical reports and add exact AR/AP and valuation tie-outs

### A. Objective

**Priority:** P0. **Requirements:** `R06`, `R07`, `R15`. **Findings:** `F37`, `F38`.
**Dependencies:** [Task 03](#task-03), [Task 10](#task-10), [Task 13](#task-13), [Task 15](#task-15), [Task 16](#task-16), [Task 17](#task-17), [Task 37](#task-37), [Task 38](#task-38), [Task 39](#task-39), [Task 45](#task-45), [Task 46](#task-46), [Task 48](#task-48), [Task 51](#task-51) **Integration gate:** G6.

**Current → target:** Sales reports filter current Posted status and lose historical sales after refunds; stock quantity is rounded to money scale and valuation sources are incomplete. → Every supported report has explicit time/recognition semantics, exact quantities and reproducible reconciliation to posted facts.

**Acceptance:** The target behavior is observable through the owning API/UI/store and every applicable scenario in E passes; release additionally requires the named gate, not just this file diff.

**Non-goals:** No claim that cash summary is a statutory cash-flow statement; label actual report scope and do not certify jurisdictional compliance.

### B. Scope

| Action | Exact path | Change / exclusion |
|---|---|---|
| MODIFY | `QtsWebApp/QtsWebApp/Features/Reporting/ReportEndpoints.cs` | Historical event-based reporting, business time boundaries and exact quantities. |
| MODIFY | `QtsBilling.Contracts/Reports/ReportDtos.cs` | Gross/returns/net, as-of open items, valuation and reconciliation semantics. |
| CREATE — NEW | `QtsWebApp/QtsWebApp/Features/Reporting/SubledgerReconciliation.cs` | AR/AP/inventory/clearing/cash per-source comparisons. |
| CREATE — NEW test | `TestQtsBilling/Integration/ReportIntegrityTests.cs` | Owning focused assertions below; reuse fixtures rather than duplicate a test infrastructure. |

MODIFY existing `QtsWebApp/QtsWebApp/Program.cs` only for required route/service registration, and `QtsBilling.Contracts/Identity/IdentityDtos.cs` / `QtsWebApp/QtsWebApp/Infrastructure/QtsIdentity.cs` only for this task's explicit D02 policies.

All other production paths are excluded except the narrow, explicitly enumerated [D17 integration bindings](#d17). Applied migrations, posted facts and unresolved cash are never edited/deleted by this scope.

### C. Verified anchors and NEW insertions

- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Reporting/ReportEndpoints.cs — source lines 1-240](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Reporting/ReportEndpoints.cs#L1-L240): `BalanceSheetAsync; ProfitAndLossAsync`. [E34](#e-e34).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Reporting/ReportEndpoints.cs — source lines 240-550](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Reporting/ReportEndpoints.cs#L240-L550): `StockSummaryAsync; SalesSummaryAsync`. [E35](#e-e35).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Accounting/LedgerWriter.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Accounting/LedgerWriter.cs): `PostWithinTransactionAsync; AllocateNumberAsync; LocalDate`. [E10](#e-e10).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Sales/SalePosting.cs — source lines 310-665](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Sales/SalePosting.cs#L310-L665): `PostAsync / posting transaction and revenue journal`. [E12](#e-e12).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Purchasing/PurchasingEndpoints.cs — source lines 1020-1170](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Purchasing/PurchasingEndpoints.cs#L1020-L1170): `PostReceiptAsync commit path`. [E30](#e-e30).

All CREATE/GENERATE symbols, routes and constraints in B/D are **NEW**. Source presence is not runtime proof; do not infer executed results from old evidence prose.

### D. Ordered implementation recipe

1. Use business time-zone/date policy for cutoffs and server-defined [from,toExclusive) ranges. Read all components of a report in one consistent database snapshot; current date/invalid zone cannot silently change the period.
2. Report original sale gross/net/tax at its economic posting date and returns/credits at their own dates. Do not erase original historical activity because current status becomes Reversed; as-of receipts/payments reconstruct settlement events through the cutoff.
3. Keep quantity at six decimals and valuation at authoritative four-decimal ValueDelta, not RoundMoney(quantity) or mutable catalog purchase price. Include rounding/residual/variance accounts explicitly.
4. Add customer/supplier ageing, inventory-value-to-GL, AR/AP control, GRNI/GDNI, drawer/clearing and sales/settlement/open-item reconciliations. Base currency only; no provisional captures in posted revenue, but show separate held-cash operational report.
5. Bound reporting ranges/result sets and use SQL aggregation/indexed filters; return exact totals for the whole query independently of row paging. No silent exclusion of missing ownership/reference/value data; expose a reconciliation error.

Apply the specific D01–D17 policies relevant to this slice. Finish the narrow route/model/client registration in B, record generated migration paths, and preserve a safe stop/fix-forward path before presenting the diff for review.

### E. Focused tests

**NEW file:** `TestQtsBilling/Integration/ReportIntegrityTests.cs`. Name the class `ReportIntegrityTests` in the directory-derived namespace specified in §6.3.

- **T53.01:** Given sale on day 1 and refund on day 10, when the task-specific command or transition is exercised, then day-1 report remains original sale and day-10 shows return separately.
- **T53.02:** Given quantity 0.000001, when the task-specific command or transition is exercised, then stock reports retain all six decimal places.
- **T53.03:** Given posting during a multi-query report, when the task-specific command or transition is exercised, then one consistent snapshot is returned.
- **T53.04:** Given goods receipt→bill→sale→return, when the task-specific command or transition is exercised, then stock value, COGS, GRNI/AP and cash/AR tie out per source.
- **T53.05:** Given as-of ageing before later settlement, when the task-specific command or transition is exercised, then outstanding/due buckets reflect that date rather than current status.

Reuse existing or earlier-task fixtures. For a mutation, also assert denied tenant/location/permission and injected pre-commit failure leave zero partial financial/audit effects; for a read/UI-only task assert no mutation. Reuse the same fixture rather than duplicating equivalent tests.

### F. Verification

**Status: NOT RUN - SPEC_ONLY.** The following filter becomes executable only after the NEW test class and prerequisites are implemented.

Requires Task 01's isolated PostgreSQL 18/API fixture, .NET 10 SDK and explicitly validated QTS_TEST_POSTGRES_CONNECTION. Fixture DDL/data writes are permitted only in a later authorized test run; never inherit production credentials.

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~TestQtsBilling.Integration.ReportIntegrityTests"
```

**PASS:** owning cases and relevant existing regression filters pass, with exact committed-state assertions where applicable; no skipped required prerequisite, new unreviewed dependency, secret leak or `git diff --check` failure. A build or mocked response alone is not financial proof.

Select the checked server or Windows build command in §6.3 only when this task changes that host. Full multi-platform regression belongs in CI/release, not every local edit.

### G. Definition of done

Every supported report has explicit time/recognition semantics, exact quantities and reproducible reconciliation to posted facts. Evidence includes the exact implementation SHA, focused test outcome and minimal reviewed diff. All new constraints/contracts/permissions/host wiring ship together under **G6**; unresolved external prerequisites stay explicit.

No unnecessary dependency or compatibility path remains. Recovery preserves historical facts and pending money. Record remaining blockers; spec completion is not implementation completion or owner production approval.

<a id="task-54"></a>
## Atomic Task 54 - Deliver actionable dashboards, reports and safe exports

### A. Objective

**Priority:** P1. **Requirements:** `R11`, `R15`, `R17`. **Findings:** `F30`, `F37`, `F38`.
**Dependencies:** [Task 30](#task-30), [Task 36](#task-36), [Task 40](#task-40), [Task 47](#task-47), [Task 49](#task-49), [Task 52](#task-52), [Task 53](#task-53) **Integration gate:** G6.

**Current → target:** Existing statement endpoints do not give owners/managers an adequate decision-making UI. → Owners and accountants can inspect real daily performance and reconcile documents, inventory and money without hand-built API queries.

**Acceptance:** The target behavior is observable through the owning API/UI/store and every applicable scenario in E passes; release additionally requires the named gate, not just this file diff.

**Non-goals:** No BI platform, data warehouse, scheduled marketing reports or new PDF dependency.

### B. Scope

| Action | Exact path | Change / exclusion |
|---|---|---|
| CREATE — NEW | `QtsBilling.Ui/Pages/Reporting/Reports.razor` | Statement filters, totals, drilldown and exact source links. |
| MODIFY — NEW in Task 30 | `QtsBilling.Ui/Pages/Dashboard.razor` | Role-specific real daily indicators and reconciliation alerts. |
| CREATE — NEW | `QtsWebApp/QtsWebApp/Features/Reporting/ReportExportEndpoints.cs` | Bounded authorized CSV/HTML exports with safe formatting. |
| CREATE — NEW test | `TestQtsBilling/Ui/ReportingJourneyTests.cs` | Owning focused assertions below; reuse fixtures rather than duplicate a test infrastructure. |

MODIFY existing `QtsWebApp/QtsWebApp/Program.cs` only for required route/service registration, and `QtsBilling.Contracts/Identity/IdentityDtos.cs` / `QtsWebApp/QtsWebApp/Infrastructure/QtsIdentity.cs` only for this task's explicit D02 policies.

Apply only the exact typed-client/host registration and routing bindings in [D17](#d17); no unlisted feature is included. A new page cannot be marked complete before its owning API is wired.

All other production paths are excluded except the narrow, explicitly enumerated [D17 integration bindings](#d17). Applied migrations, posted facts and unresolved cash are never edited/deleted by this scope.

### C. Verified anchors and NEW insertions

- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Reporting/ReportEndpoints.cs — source lines 1-240](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Reporting/ReportEndpoints.cs#L1-L240): `BalanceSheetAsync; ProfitAndLossAsync`. [E34](#e-e34).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Reporting/ReportEndpoints.cs — source lines 240-550](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Reporting/ReportEndpoints.cs#L240-L550): `StockSummaryAsync; SalesSummaryAsync`. [E35](#e-e35).
- **EXISTING / VERIFIED:** [QtsBilling.Ui/Pages/Pos.razor.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Ui/Pages/Pos.razor.cs): `LineTax; LineTotal; CaptureAsync`. [E26](#e-e26).

All CREATE/GENERATE symbols, routes and constraints in B/D are **NEW**. Source presence is not runtime proof; do not infer executed results from old evidence prose.

### D. Ordered implementation recipe

1. Provide sales gross/returns/net and payment/credit split; profit/loss; balance sheet; cash/clearing; stock quantity/value; customer/supplier ageing; outstanding PO/orders and reconciliation differences. Each report states business/currency/date semantics and excludes provisional revenue.
2. Dashboard uses exact aggregates from the same report services, not duplicated arithmetic or fabricated sample cards. Separate pending/rejected-held device cash from settled sales, and show actionable setup/stock/due-payment warnings.
3. Enable source drilldown to immutable documents/journals with permission-aware columns. Date/location/party filters reset paging and total scope; a report error is not an empty successful statement.
4. CSV export uses invariant machine amounts, explicit currency/unit columns, quoted fields and spreadsheet-formula escaping for user text. HTML output encodes dynamic content and supports browser printing; limits and audit prevent bulk unauthorized extraction.
5. Add keyboard-accessible filters/tables, visible loading/progress, cancellation for long queries and retryable errors. Large exports are bounded synchronous reads initially; do not add a job system until measured need is demonstrated.

Apply the specific D01–D17 policies relevant to this slice. Finish the narrow route/model/client registration in B, record generated migration paths, and preserve a safe stop/fix-forward path before presenting the diff for review.

### E. Focused tests

**NEW file:** `TestQtsBilling/Ui/ReportingJourneyTests.cs`. Name the class `ReportingJourneyTests` in the directory-derived namespace specified in §6.3.

- **T54.01:** Given dashboard and corresponding detail report, when the task-specific command or transition is exercised, then totals agree for identical scope/time.
- **T54.02:** Given formula-like customer text, when the task-specific command or transition is exercised, then CSV is safe when opened in a spreadsheet.
- **T54.03:** Given export permission denied or foreign scope, when the task-specific command or transition is exercised, then no data is downloaded.
- **T54.04:** Given report pagination, when the task-specific command or transition is exercised, then total represents the entire filter rather than displayed page.
- **T54.05:** Given pending offline money, when the task-specific command or transition is exercised, then it is visible separately and never included as posted revenue.

Reuse existing or earlier-task fixtures. For a mutation, also assert denied tenant/location/permission and injected pre-commit failure leave zero partial financial/audit effects; for a read/UI-only task assert no mutation. Reuse the same fixture rather than duplicating equivalent tests.

### F. Verification

**Status: NOT RUN - SPEC_ONLY.** The following filter becomes executable only after the NEW test class and prerequisites are implemented.

Requires the owning APIs, shared UI smoke/Playwright setup and disposable native data. The actual Windows/WebView2 checks remain a separate platform gate.

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~TestQtsBilling.Ui.ReportingJourneyTests"
```

**PASS:** owning cases and relevant existing regression filters pass, with exact committed-state assertions where applicable; no skipped required prerequisite, new unreviewed dependency, secret leak or `git diff --check` failure. A build or mocked response alone is not financial proof.

Select the checked server or Windows build command in §6.3 only when this task changes that host. Full multi-platform regression belongs in CI/release, not every local edit.

### G. Definition of done

Owners and accountants can inspect real daily performance and reconcile documents, inventory and money without hand-built API queries. Evidence includes the exact implementation SHA, focused test outcome and minimal reviewed diff. All new constraints/contracts/permissions/host wiring ship together under **G6**; unresolved external prerequisites stay explicit.

No unnecessary dependency or compatibility path remains. Recovery preserves historical facts and pending money. Record remaining blockers; spec completion is not implementation completion or owner production approval.

<a id="task-55"></a>
## Atomic Task 55 - Add authorized document attachments and reliable transactional email delivery

### A. Objective

**Priority:** P1. **Requirements:** `R04`, `R12`, `R13`, `R17`, `R18`. **Findings:** `F06`, `F30`, `F44`.
**Dependencies:** [Task 04](#task-04), [Task 05](#task-05), [Task 06](#task-06), [Task 07](#task-07), [Task 11](#task-11), [Task 38](#task-38), [Task 45](#task-45), [Task 50](#task-50) **Integration gate:** G2 + G6 + G7.

**Current → target:** Password recovery has no production delivery path; operational documents need evidence and safe customer/supplier communication. → Authorized users can attach approved evidence and send invitations/documents with truthful queued/sent/failed status.

**Acceptance:** The target behavior is observable through the owning API/UI/store and every applicable scenario in E passes; release additionally requires the named gate, not just this file diff.

**Non-goals:** No general document-management system, e-signature, IMAP mailbox, rich HTML upload or optional provider platform. SMTP production configuration is BLOCKED_EXTERNAL.

### B. Scope

| Action | Exact path | Change / exclusion |
|---|---|---|
| CREATE — NEW | `QtsWebApp/QtsWebApp/Features/Documents/DocumentEndpoints.cs` | Bounded attachment intake/read/delete policy outside public web assets. |
| CREATE — NEW | `QtsWebApp/QtsWebApp/Infrastructure/Messaging/MessageOutboxWorker.cs` | Bounded SMTP outbox delivery with retries and expiry. |
| CREATE — NEW | `QtsBilling.Persistence.Postgres/Entities/DocumentsAndMessages.cs` | Scoped attachment metadata and durable message delivery state. |
| GENERATE — NEW | `QtsBilling.Persistence.Postgres/Migrations/` | NEW DocumentsAndMessageOutbox with subject scope and deduplication. |
| CREATE — NEW test | `TestQtsBilling/Integration/DocumentsAndDeliveryTests.cs` | Owning focused assertions below; reuse fixtures rather than duplicate a test infrastructure. |

MODIFY existing `QtsBilling.Persistence.Postgres/BillingDbContext.cs` and the appropriate checked `BillingDbContext.Model.cs` / `BillingDbContext.Model.Financial.cs` only for the listed model; GENERATE designer/snapshot companions with the named new migration.

MODIFY existing `QtsWebApp/QtsWebApp/Program.cs` only for required route/service registration, and `QtsBilling.Contracts/Identity/IdentityDtos.cs` / `QtsWebApp/QtsWebApp/Infrastructure/QtsIdentity.cs` only for this task's explicit D02 policies.

All other production paths are excluded except the narrow, explicitly enumerated [D17 integration bindings](#d17). Applied migrations, posted facts and unresolved cash are never edited/deleted by this scope.

### C. Verified anchors and NEW insertions

- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Auth/AuthEndpoints.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Auth/AuthEndpoints.cs): `LoginAsync; RequestPasswordResetAsync; CompletePasswordResetAsync`. [E05](#e-e05).
- **EXISTING / VERIFIED:** [QtsBilling.Persistence.Postgres/BillingDbContext.Model.Financial.cs — source lines 210-445](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Persistence.Postgres/BillingDbContext.Model.Financial.cs#L210-L445): `ConfigureAccounting; ConfigurePurchasing`. [E07](#e-e07).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Invoices/InvoiceEndpoints.cs — source lines 1-110](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Invoices/InvoiceEndpoints.cs#L1-L110): `MapInvoiceEndpoints`. [E31](#e-e31).
- **EXISTING / VERIFIED:** [docs/migration/deployment.md — source lines 1-150](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/docs/migration/deployment.md#L1-L150): `Sections 7 and 9`. [E45](#e-e45).

All CREATE/GENERATE symbols, routes and constraints in B/D are **NEW**. Source presence is not runtime proof; do not infer executed results from old evidence prose.

### D. Ordered implementation recipe

1. Attachments are PDF/PNG/JPEG only initially, maximum 10 MiB each and configured per-business quota; verify signature/type, extension and bounded decode, assign opaque IDs, and store outside webroot with no executable/inline HTML/SVG content. Downloads require same-business subject authorization and safe Content-Disposition.
2. Persist metadata/digest/subject/version in PostgreSQL and stage file bytes safely. Finalize metadata only after durable file placement; recover orphan stages without deleting linked evidence. Posted-document evidence removal is a restricted audited retirement, never hidden destruction.
3. Enrolment/reset and explicit invoice/statement email create an outbox row in the initiating transaction. Deliver via one configured SMTP transport using MailKit 4.18.1; no SMTP/provider I/O inside the financial transaction. Pin the package only in the server.
4. Use bounded retries, one active lease, expiration and unique event/recipient/version identity. Encrypt sensitive reset/invitation payload at rest with reviewed Data Protection and restrict access; never log body/tokens. A late/expired token must not be delivered as a usable invitation.
5. Provider acknowledgement is delivery acceptance, not proof a human received/read it. UI shows queued/sent/failed and controlled retry. SMTP credentials, retention, quotas and actual transport readiness must be verified before G2 enrolment can be released.

Apply the specific D01–D17 policies relevant to this slice. Finish the narrow route/model/client registration in B, record generated migration paths, and preserve a safe stop/fix-forward path before presenting the diff for review.

### E. Focused tests

**NEW file:** `TestQtsBilling/Integration/DocumentsAndDeliveryTests.cs`. Name the class `DocumentsAndDeliveryTests` in the directory-derived namespace specified in §6.3.

- **T55.01:** Given spoofed file type, oversized payload, path traversal or foreign subject, when the task-specific command or transition is exercised, then upload/download is refused.
- **T55.02:** Given a crash between staged file and metadata, when the task-specific command or transition is exercised, then recovery preserves linked files and handles only orphan stages.
- **T55.03:** Given timeout after SMTP acceptance, when the task-specific command or transition is exercised, then retry behavior is documented and message identity retained; no financial event is duplicated.
- **T55.04:** Given expired reset message, when the task-specific command or transition is exercised, then it is not sent with a usable stale token and no token appears in logs.
- **T55.05:** Given document email from another business, when the task-specific command or transition is exercised, then neither attachment nor recipient context leaks.

Reuse existing or earlier-task fixtures. For a mutation, also assert denied tenant/location/permission and injected pre-commit failure leave zero partial financial/audit effects; for a read/UI-only task assert no mutation. Reuse the same fixture rather than duplicating equivalent tests.

### F. Verification

**Status: NOT RUN - SPEC_ONLY.** The following filter becomes executable only after the NEW test class and prerequisites are implemented.

Requires Task 01's isolated PostgreSQL 18/API fixture, .NET 10 SDK and explicitly validated QTS_TEST_POSTGRES_CONNECTION. Fixture DDL/data writes are permitted only in a later authorized test run; never inherit production credentials.

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~TestQtsBilling.Integration.DocumentsAndDeliveryTests"
```

**PASS:** owning cases and relevant existing regression filters pass, with exact committed-state assertions where applicable; no skipped required prerequisite, new unreviewed dependency, secret leak or `git diff --check` failure. A build or mocked response alone is not financial proof.

Select the checked server or Windows build command in §6.3 only when this task changes that host. Full multi-platform regression belongs in CI/release, not every local edit.

### G. Definition of done

Authorized users can attach approved evidence and send invitations/documents with truthful queued/sent/failed status. Evidence includes the exact implementation SHA, focused test outcome and minimal reviewed diff. All new constraints/contracts/permissions/host wiring ship together under **G2 + G6 + G7**; unresolved external prerequisites stay explicit.

No unnecessary dependency or compatibility path remains. Recovery preserves historical facts and pending money. Record remaining blockers; spec completion is not implementation completion or owner production approval.

<a id="task-56"></a>
## Atomic Task 56 - Deliver owner administration, settings, device and audit screens

### A. Objective

**Priority:** P1. **Requirements:** `R03`, `R04`, `R11`, `R17`. **Findings:** `F06`, `F07`, `F10`, `F30`, `F43`.
**Dependencies:** [Task 07](#task-07), [Task 08](#task-08), [Task 23](#task-23), [Task 30](#task-30), [Task 31](#task-31), [Task 32](#task-32), [Task 55](#task-55) **Integration gate:** G2 + G5.

**Current → target:** Owner setup, user/device lifecycle and audit administration remain dependent on development seeding, settings or technical tooling. → An owner can operate a production business and revoke access through supported UI, without SQL or hardcoded development identities.

**Acceptance:** The target behavior is observable through the owning API/UI/store and every applicable scenario in E passes; release additionally requires the named gate, not just this file diff.

**Non-goals:** No cross-tenant superadmin impersonation or undisclosed support bypass.

### B. Scope

| Action | Exact path | Change / exclusion |
|---|---|---|
| CREATE — NEW | `QtsBilling.Ui/Pages/Admin/BusinessSettings.razor` | Guided native setup, location/account/tax/numbering controls. |
| CREATE — NEW | `QtsBilling.Ui/Pages/Admin/UsersAndDevices.razor` | Invitations, roles, current scope, enrolment and revocation. |
| CREATE — NEW | `QtsBilling.Ui/Pages/Admin/Audit.razor` | Scoped filterable audit history and sanitized diagnostic references. |
| CREATE — NEW test | `TestQtsBilling/Ui/OwnerAdministrationTests.cs` | Owning focused assertions below; reuse fixtures rather than duplicate a test infrastructure. |

Apply only the exact typed-client/host registration and routing bindings in [D17](#d17); no unlisted feature is included. A new page cannot be marked complete before its owning API is wired.

All other production paths are excluded except the narrow, explicitly enumerated [D17 integration bindings](#d17). Applied migrations, posted facts and unresolved cash are never edited/deleted by this scope.

### C. Verified anchors and NEW insertions

- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Infrastructure/QtsIdentity.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Infrastructure/QtsIdentity.cs): `PermissionAuthorizationHandler.HandleRequirementAsync; QtsClaimsPrincipalFactory`. [E04](#e-e04).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Auth/AuthEndpoints.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Auth/AuthEndpoints.cs): `LoginAsync; RequestPasswordResetAsync; CompletePasswordResetAsync`. [E05](#e-e05).
- **EXISTING / VERIFIED:** [QtsBilling.Maui/Services/DeviceLocalPosService.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Maui/Services/DeviceLocalPosService.cs): `CaptureCashSaleAsync`. [E23](#e-e23).
- **EXISTING / VERIFIED:** [docs/migration/deployment.md — source lines 1-150](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/docs/migration/deployment.md#L1-L150): `Sections 7 and 9`. [E45](#e-e45).

All CREATE/GENERATE symbols, routes and constraints in B/D are **NEW**. Source presence is not runtime proof; do not infer executed results from old evidence prose.

### D. Ordered implementation recipe

1. Build an onboarding wizard over Task 8 setup APIs: business identity/currency/time zone, periods, mappings, location/register, owner/MFA, native opening preview and readiness. Defaults are explicitly reviewed; no demo password or fictional balances.
2. User page sends invitations, sets active role/location grants and requires stronger authorization for privilege increases. Prevent accidental removal of the last active owner; invalidating access revokes server/native authority with documented offline lag.
3. Device page displays approved device identity, location/register, grant expiry, last sync and unresolved money. Enrol/revoke/retire are explicit commands; retirement cannot delete an unresolved queue or claim immediate offline revocation.
4. Audit page filters actor/action/entity/date/location with stable paging and permission; show sanitized correlation IDs and original document links, never tokens, passwords or unrestricted request payloads.
5. Use accessible confirmations stating exact business/device and consequence; successful role/settings change refreshes current authority. Error/reauthentication/network loss cannot leave UI showing an uncommitted new privilege.

Apply the specific D01–D17 policies relevant to this slice. Finish the narrow route/model/client registration in B, record generated migration paths, and preserve a safe stop/fix-forward path before presenting the diff for review.

### E. Focused tests

**NEW file:** `TestQtsBilling/Ui/OwnerAdministrationTests.cs`. Name the class `OwnerAdministrationTests` in the directory-derived namespace specified in §6.3.

- **T56.01:** Given a clean native business, when the task-specific command or transition is exercised, then setup completes without editing SQL, source or dev seeder.
- **T56.02:** Given last-owner removal or cashier privilege escalation, when the task-specific command or transition is exercised, then the API/UI refuses it.
- **T56.03:** Given revoked device with pending cash, when the task-specific command or transition is exercised, then access is suspended and recovery/custody remains visible.
- **T56.04:** Given an audit viewer, when the task-specific command or transition is exercised, then only permitted business events and masked identifiers are returned.
- **T56.05:** Given expired MFA/reauthentication during a sensitive change, when the task-specific command or transition is exercised, then no mutation occurs until renewed confirmation.

Reuse existing or earlier-task fixtures. For a mutation, also assert denied tenant/location/permission and injected pre-commit failure leave zero partial financial/audit effects; for a read/UI-only task assert no mutation. Reuse the same fixture rather than duplicating equivalent tests.

### F. Verification

**Status: NOT RUN - SPEC_ONLY.** The following filter becomes executable only after the NEW test class and prerequisites are implemented.

Requires the owning APIs, shared UI smoke/Playwright setup and disposable native data. The actual Windows/WebView2 checks remain a separate platform gate.

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~TestQtsBilling.Ui.OwnerAdministrationTests"
```

**PASS:** owning cases and relevant existing regression filters pass, with exact committed-state assertions where applicable; no skipped required prerequisite, new unreviewed dependency, secret leak or `git diff --check` failure. A build or mocked response alone is not financial proof.

Select the checked server or Windows build command in §6.3 only when this task changes that host. Full multi-platform regression belongs in CI/release, not every local edit.

### G. Definition of done

An owner can operate a production business and revoke access through supported UI, without SQL or hardcoded development identities. Evidence includes the exact implementation SHA, focused test outcome and minimal reviewed diff. All new constraints/contracts/permissions/host wiring ship together under **G2 + G5**; unresolved external prerequisites stay explicit.

No unnecessary dependency or compatibility path remains. Recovery preserves historical facts and pending money. Record remaining blockers; spec completion is not implementation completion or owner production approval.

<a id="task-57"></a>
## Atomic Task 57 - Close contract, route, concurrency and dependency integration gaps

### A. Objective

**Priority:** P0. **Requirements:** `R01`, `R03`, `R08`, `R11`, `R17`, `R18`. **Findings:** `F13`, `F21`, `F29`, `F30`, `F42`.
**Dependencies:** [Task 02](#task-02), [Task 06](#task-06), [Task 11](#task-11), [Task 23](#task-23), [Task 24](#task-24), [Task 26](#task-26), [Task 31](#task-31), [Task 32](#task-32), [Task 33](#task-33), [Task 35](#task-35), [Task 38](#task-38), [Task 39](#task-39), [Task 41](#task-41), [Task 42](#task-42), [Task 44](#task-44), [Task 45](#task-45), [Task 46](#task-46), [Task 48](#task-48), [Task 50](#task-50), [Task 51](#task-51), [Task 53](#task-53), [Task 55](#task-55), [Task 56](#task-56) **Integration gate:** G0 + G7.

**Current → target:** An extensive endpoint surface is not backed by a complete contract-to-client and permission/version verification matrix. → Every supported action has one registered native API contract, authorized typed client and UI/host consumer or an explicit API-only purpose.

**Acceptance:** The target behavior is observable through the owning API/UI/store and every applicable scenario in E passes; release additionally requires the named gate, not just this file diff.

**Non-goals:** No speculative code generator or API version layer; add tooling only when it closes a measured drift gap.

### B. Scope

| Action | Exact path | Change / exclusion |
|---|---|---|
| MODIFY | `QtsBilling.Contracts/Serialization/ContractJson.cs` | Strict native serialization and stable decimal/enum/error semantics. |
| MODIFY | `QtsWebApp/QtsWebApp/Infrastructure/ApiSupport.cs` | Consistent ProblemDetails, If-Match/version and boundary validation. |
| CREATE — NEW | `TestQtsBilling/Contracts/EndpointContractTests.cs` | Runtime-discovered endpoint/DTO/permission/client consistency checks. |
| CREATE — NEW | `docs/api/native-contracts.md` | Current native route, error, version and host capability record. |
| CREATE — NEW test | `TestQtsBilling/Contracts/NativeContractMatrixTests.cs` | Owning focused assertions below; reuse fixtures rather than duplicate a test infrastructure. |

All other production paths are excluded except the narrow, explicitly enumerated [D17 integration bindings](#d17). Applied migrations, posted facts and unresolved cash are never edited/deleted by this scope.

### C. Verified anchors and NEW insertions

- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Program.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Program.cs): `Identity registration, cookies, API registration, WebLocalPosService`. [E03](#e-e03).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Sales/SalePosting.cs — source lines 1-310](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Sales/SalePosting.cs#L1-L310): `ValidatePayload; ResolveContextAsync; PriceLines; ValidatePayment`. [E11](#e-e11).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Sync/SyncOperationEndpoints.cs — source lines 1-460](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Sync/SyncOperationEndpoints.cs#L1-L460): `ProcessAsync / operation and batch handlers`. [E15](#e-e15).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Sync/SyncChangeEndpoints.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Sync/SyncChangeEndpoints.cs): `Change-feed endpoint`. [E17](#e-e17).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Pos/WebLocalPosService.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Pos/WebLocalPosService.cs): `ILocalPosService implementation`. [E27](#e-e27).
- **EXISTING / VERIFIED:** [.github/workflows/ci.yml — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/.github/workflows/ci.yml): `build-and-test; build-desktop`. [E41](#e-e41).

All CREATE/GENERATE symbols, routes and constraints in B/D are **NEW**. Source presence is not runtime proof; do not infer executed results from old evidence prose.

### D. Ordered implementation recipe

1. Enumerate runtime endpoint metadata from the test host and bind each route/verb to DTO, permission, scope, error and consumer. Verify all new feature registrations/project references rather than checking class presence alone.
2. Apply native contract conventions: camelCase/string enums, exact decimal JSON, bounded strings/arrays, explicit nullable/clear semantics, Version/If-Match on edits and typed 400/401/403/404/409/412/422/429/503/500. Reject undefined enum integers and malformed cursors.
3. Keep current useful native wire shape where unchanged; breaking native changes update server, Contracts, both hosts and pending-payload handling in one gate. No old-provider/platform routes, adapters or fallback. Unresolved existing cash payloads are drained or migrated losslessly before client-version cutoff.
4. Generate OpenAPI from the runtime using existing host tooling and compare a reviewed native baseline; do not invent a current generated file. If a generator is added, define its exact project/command and outputs as NEW and keep generated code out of manual edits.
5. Enforce positive project/assembly reference boundaries, client bundle secret/dependency scan and client registration. Prove endpoints return real actions rather than NotSupported stubs on declared supported hosts.

Apply the specific D01–D17 policies relevant to this slice. Finish the narrow route/model/client registration in B, record generated migration paths, and preserve a safe stop/fix-forward path before presenting the diff for review.

### E. Focused tests

**NEW file:** `TestQtsBilling/Contracts/NativeContractMatrixTests.cs`. Name the class `NativeContractMatrixTests` in the directory-derived namespace specified in §6.3.

- **T57.01:** Given runtime endpoint enumeration, when the task-specific command or transition is exercised, then every supported route has the expected permission/contract/client mapping.
- **T57.02:** Given malformed enum/cursor/decimal or stale If-Match, when the task-specific command or transition is exercised, then stable typed errors are returned without state change.
- **T57.03:** Given forbidden persistence reference injected into Contracts/RCL/browser, when the task-specific command or transition is exercised, then architecture check fails.
- **T57.04:** Given older pending native cash during a planned native version transition, when the task-specific command or transition is exercised, then readiness blocks data loss and no compatibility fallback is silently added.
- **T57.05:** Given online/offline host capability matrix, when the task-specific command or transition is exercised, then each declared supported operation is callable and each unsupported operation is explicit.

Reuse existing or earlier-task fixtures. For a mutation, also assert denied tenant/location/permission and injected pre-commit failure leave zero partial financial/audit effects; for a read/UI-only task assert no mutation. Reuse the same fixture rather than duplicating equivalent tests.

### F. Verification

**Status: NOT RUN - SPEC_ONLY.** The following filter becomes executable only after the NEW test class and prerequisites are implemented.

Requires .NET 10 SDK and the NEW owning test class. Use pure fixtures/native files; no production service connection.

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~TestQtsBilling.Contracts.NativeContractMatrixTests"
```

**PASS:** owning cases and relevant existing regression filters pass, with exact committed-state assertions where applicable; no skipped required prerequisite, new unreviewed dependency, secret leak or `git diff --check` failure. A build or mocked response alone is not financial proof.

Select the checked server or Windows build command in §6.3 only when this task changes that host. Full multi-platform regression belongs in CI/release, not every local edit.

### G. Definition of done

Every supported action has one registered native API contract, authorized typed client and UI/host consumer or an explicit API-only purpose. Evidence includes the exact implementation SHA, focused test outcome and minimal reviewed diff. All new constraints/contracts/permissions/host wiring ship together under **G0 + G7**; unresolved external prerequisites stay explicit.

No unnecessary dependency or compatibility path remains. Recovery preserves historical facts and pending money. Record remaining blockers; spec completion is not implementation completion or owner production approval.

<a id="task-58"></a>
## Atomic Task 58 - Harden production configuration, backups and lossless recovery

### A. Objective

**Priority:** P0. **Requirements:** `R02`, `R09`, `R16`, `R18`. **Findings:** `F01`, `F02`, `F41`, `F44`.
**Dependencies:** [Task 20](#task-20), [Task 21](#task-21), [Task 25](#task-25), [Task 27](#task-27), [Task 55](#task-55), [Task 57](#task-57) **Integration gate:** G7.

**Current → target:** Logical dump tooling exists, but production recovery evidence is unverified and documentation recommends destructive device recovery. → Operators can restore the actual native deployment and preserve every pending cash record within an approved measured recovery objective.

**Acceptance:** The target behavior is observable through the owning API/UI/store and every applicable scenario in E passes; release additionally requires the named gate, not just this file diff.

**Non-goals:** No actual backup/restore/database mutation during this specification. Real RPO/RTO, storage, keys and recovery operators are external readiness evidence.

### B. Scope

| Action | Exact path | Change / exclusion |
|---|---|---|
| MODIFY | `scripts/backup-postgres.ps1` | Target-bound manifests, checked tool failures and protected backup outputs. |
| MODIFY | `scripts/restore-postgres.ps1` | Explicit isolated target, verified inputs and no implicit destructive override. |
| MODIFY | `scripts/restore-drill.ps1` | Source-consistent native financial, file/key and pending-device recovery checks. |
| MODIFY | `docs/migration/deployment.md` | Replace dangerous wipe/reinitialize guidance with cash-preserving recovery and actual operational requirements. |
| CREATE — NEW test | `TestQtsBilling/Operations/RecoveryControlsTests.cs` | Owning focused assertions below; reuse fixtures rather than duplicate a test infrastructure. |

All other production paths are excluded except the narrow, explicitly enumerated [D17 integration bindings](#d17). Applied migrations, posted facts and unresolved cash are never edited/deleted by this scope.

### C. Verified anchors and NEW insertions

- **EXISTING / VERIFIED:** [scripts/qb30-cutover.ps1 — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/scripts/qb30-cutover.ps1): `Add-Gate and whole cutover flow`. [E40](#e-e40).
- **EXISTING / VERIFIED:** [docs/migration/deployment.md — source lines 1-150](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/docs/migration/deployment.md#L1-L150): `Sections 7 and 9`. [E45](#e-e45).
- **EXISTING / VERIFIED:** [QtsBilling.Persistence.Sqlite/SqliteLocalStore.cs — source lines 1-290](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Persistence.Sqlite/SqliteLocalStore.cs#L1-L290): `InitializeAsync; CaptureProvisionalCashSaleAsync`. [E20](#e-e20).
- **EXISTING / VERIFIED:** [QtsBilling.Persistence.Sqlite/SqliteLocalStore.cs — source lines 340-650](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Persistence.Sqlite/SqliteLocalStore.cs#L340-L650): `GetDueOutboxAsync; MarkOutboxSendingAsync; ApplyReceiptAsync; GetPendingSummaryAsync`. [E21](#e-e21).
- **EXISTING / VERIFIED:** [QtsBilling.Maui/Services/SessionStore.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Maui/Services/SessionStore.cs): `SaveSessionAsync; GetSessionAsync`. [E25](#e-e25).

All CREATE/GENERATE symbols, routes and constraints in B/D are **NEW**. Source presence is not runtime proof; do not infer executed results from old evidence prose.

### D. Ordered implementation recipe

1. Fail Production startup on missing explicit database/TLS/issuer/key/file-store settings; no local development credential fallback. Verify application role cannot DDL/TRUNCATE/disable triggers, and migration/backup identities are separately scoped.
2. Backup PostgreSQL plus attachment manifest/files and Data Protection/OIDC keys under access-controlled retention. Bind environment/database/schema/release/high-water/time/counts/digests to one manifest; test external command exit codes and free-space failures.
3. Logical dump alone does not meet the proposed RPO 15 minutes. Document the actual host-supported PITR/WAL/managed-backup mechanism and measure a restore against approved RTO four hours; otherwise retain BLOCKED_EXTERNAL and publish the real weaker objective, not a false promise.
4. Restore only into explicitly named isolated targets during drills. Reconcile per-entry journals, AR/AP, stock quantity/value, allocations, numbering, native source links, files and required key access from the same snapshot. Actual production restore requires separate authorized maintenance/reconciliation.
5. For Windows corruption/upgrade/logout/retirement, preserve the full SQLite file/WAL/SHM and secure recovery identity; only a verified consistent SQLite backup API/VACUUM INTO snapshot is used for normal upgrades. Never discard pending tables or silently rebuild a corrupt store. Correct all conflicting runbook clauses.

Apply the specific D01–D17 policies relevant to this slice. Finish the narrow route/model/client registration in B, record generated migration paths, and preserve a safe stop/fix-forward path before presenting the diff for review.

### E. Focused tests

**NEW file:** `TestQtsBilling/Operations/RecoveryControlsTests.cs`. Name the class `RecoveryControlsTests` in the directory-derived namespace specified in §6.3.

- **T58.01:** Given corrupt dump/hash/key manifest or nonzero pg_dump/restore exit, when the task-specific command or transition is exercised, then recovery verification fails.
- **T58.02:** Given a production database name in the drill, when the task-specific command or transition is exercised, then the script refuses before destructive access.
- **T58.03:** Given restored financial data with equal total counts but wrong per-document value, when the task-specific command or transition is exercised, then the drill detects it.
- **T58.04:** Given device corruption with unacknowledged cash, when the task-specific command or transition is exercised, then new capture stops and original recovery files are retained.
- **T58.05:** Given restore of attachments and retained keys, when the task-specific command or transition is exercised, then authorized document/session recovery follows the declared procedure.

Reuse existing or earlier-task fixtures. For a mutation, also assert denied tenant/location/permission and injected pre-commit failure leave zero partial financial/audit effects; for a read/UI-only task assert no mutation. Reuse the same fixture rather than duplicating equivalent tests.

### F. Verification

**Status: NOT RUN - SPEC_ONLY.** The following filter becomes executable only after the NEW test class and prerequisites are implemented.

Use explicit fake process/provider adapters or fixture-only PowerShell 7 harnesses. Never execute production admission/restore/deploy as a test side effect.

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~TestQtsBilling.Operations.RecoveryControlsTests"
```

**PASS:** owning cases and relevant existing regression filters pass, with exact committed-state assertions where applicable; no skipped required prerequisite, new unreviewed dependency, secret leak or `git diff --check` failure. A build or mocked response alone is not financial proof.

Select the checked server or Windows build command in §6.3 only when this task changes that host. Full multi-platform regression belongs in CI/release, not every local edit.

### G. Definition of done

Operators can restore the actual native deployment and preserve every pending cash record within an approved measured recovery objective. Evidence includes the exact implementation SHA, focused test outcome and minimal reviewed diff. All new constraints/contracts/permissions/host wiring ship together under **G7**; unresolved external prerequisites stay explicit.

No unnecessary dependency or compatibility path remains. Recovery preserves historical facts and pending money. Record remaining blockers; spec completion is not implementation completion or owner production approval.

<a id="task-59"></a>
## Atomic Task 59 - Add measurable observability, performance limits and retention controls

### A. Objective

**Priority:** P1. **Requirements:** `R03`, `R09`, `R15`, `R18`. **Findings:** `F27`, `F38`, `F44`, `F45`.
**Dependencies:** [Task 24](#task-24), [Task 26](#task-26), [Task 28](#task-28), [Task 53](#task-53), [Task 55](#task-55), [Task 57](#task-57), [Task 58](#task-58) **Integration gate:** G7.

**Current → target:** Successful local demos do not establish bounded production query cost, offline backlog recovery or actionable operational monitoring. → Operators can detect failed financial/sync/delivery/recovery behavior and investigate safely without logging sensitive payloads.

**Acceptance:** The target behavior is observable through the owning API/UI/store and every applicable scenario in E passes; release additionally requires the named gate, not just this file diff.

**Non-goals:** No mandatory external monitoring vendor, distributed cache or search engine without measured justification.

### B. Scope

| Action | Exact path | Change / exclusion |
|---|---|---|
| CREATE — NEW | `QtsWebApp/QtsWebApp/Infrastructure/OperationalTelemetry.cs` | Bounded structured events/metrics and health/readiness signals. |
| MODIFY | `QtsWebApp/QtsWebApp/Features/Health/HealthEndpoints.cs` | Liveness/readiness distinction with schema/dependency state but no secrets. |
| MODIFY | `QtsBilling.Maui/Services/StartupDiagnostics.cs` | Sanitized device readiness, grant and unresolved-cash diagnostics. |
| CREATE — NEW | `docs/operations/observability.md` | Metric meanings, measured budgets, alert response and data retention policy. |
| CREATE — NEW test | `TestQtsBilling/Integration/OperationalTelemetryTests.cs` | Owning focused assertions below; reuse fixtures rather than duplicate a test infrastructure. |

MODIFY existing `QtsWebApp/QtsWebApp/Program.cs` only for required route/service registration, and `QtsBilling.Contracts/Identity/IdentityDtos.cs` / `QtsWebApp/QtsWebApp/Infrastructure/QtsIdentity.cs` only for this task's explicit D02 policies.

All other production paths are excluded except the narrow, explicitly enumerated [D17 integration bindings](#d17). Applied migrations, posted facts and unresolved cash are never edited/deleted by this scope.

### C. Verified anchors and NEW insertions

- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Sync/SyncChangeEndpoints.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Sync/SyncChangeEndpoints.cs): `Change-feed endpoint`. [E17](#e-e17).
- **EXISTING / VERIFIED:** [QtsBilling.Maui/Services/DeviceSyncCoordinator.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Maui/Services/DeviceSyncCoordinator.cs): `RunAsync; push/pull; BuildEnvelope`. [E18](#e-e18).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Reporting/ReportEndpoints.cs — source lines 1-240](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Reporting/ReportEndpoints.cs#L1-L240): `BalanceSheetAsync; ProfitAndLossAsync`. [E34](#e-e34).
- **EXISTING / VERIFIED:** [QtsWebApp/QtsWebApp/Features/Reporting/ReportEndpoints.cs — source lines 240-550](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Reporting/ReportEndpoints.cs#L240-L550): `StockSummaryAsync; SalesSummaryAsync`. [E35](#e-e35).
- **EXISTING / VERIFIED:** [docs/migration/deployment.md — source lines 1-150](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/docs/migration/deployment.md#L1-L150): `Sections 7 and 9`. [E45](#e-e45).

All CREATE/GENERATE symbols, routes and constraints in B/D are **NEW**. Source presence is not runtime proof; do not infer executed results from old evidence prose.

### D. Ordered implementation recipe

1. Use built-in structured logging, Activity and Meter instrumentation before adding exporter dependencies. Measure request latency/error category, DB retries/lock wait, sync backlog/oldest age/rejections/held cash, delivery failure and backup freshness.
2. Health liveness does not query the database; readiness reports unavailable dependencies/schema/configuration generically. Do not leak database names/connection strings, grant tokens or stack traces to anonymous probes.
3. Enforce D14 bounded page/body/range/attachment/export limits and cancellation. Benchmark declared reference workload with actual hardware/network recorded; use SQL plans/query counts to fix specific N+1/unindexed scans rather than adding caches by instinct.
4. Add safe log/audit metadata limits and redaction tests; treat user-supplied correlation IDs as bounded untrusted input. Audit retention is controlled and legally reviewed, never delete posted/custody facts or needed idempotency receipts to improve performance.
5. Define alerts with an owner/action: sustained posting failures, unresolved cash over threshold, queue stuck, baseline expiry loop, signature failure, backup/PITR stale and unauthorized access. Monitoring outage must not recurse into business persistence or alter a successful financial outcome.

Apply the specific D01–D17 policies relevant to this slice. Finish the narrow route/model/client registration in B, record generated migration paths, and preserve a safe stop/fix-forward path before presenting the diff for review.

### E. Focused tests

**NEW file:** `TestQtsBilling/Integration/OperationalTelemetryTests.cs`. Name the class `OperationalTelemetryTests` in the directory-derived namespace specified in §6.3.

- **T59.01:** Given malicious oversized identifiers/secrets, when the task-specific command or transition is exercised, then logs remain bounded and redacted.
- **T59.02:** Given database down/schema mismatch, when the task-specific command or transition is exercised, then readiness fails while liveness remains truthful.
- **T59.03:** Given a growing sync backlog/rejected-held cash, when the task-specific command or transition is exercised, then metrics show age and amount accurately.
- **T59.04:** Given target-load report/checkout, when the task-specific command or transition is exercised, then measured latency/query count is recorded against the proposed budget, not invented.
- **T59.05:** Given logging sink failure, when the task-specific command or transition is exercised, then the original API outcome remains correct and no recursive database logger runs.

Reuse existing or earlier-task fixtures. For a mutation, also assert denied tenant/location/permission and injected pre-commit failure leave zero partial financial/audit effects; for a read/UI-only task assert no mutation. Reuse the same fixture rather than duplicating equivalent tests.

### F. Verification

**Status: NOT RUN - SPEC_ONLY.** The following filter becomes executable only after the NEW test class and prerequisites are implemented.

Requires Task 01's isolated PostgreSQL 18/API fixture, .NET 10 SDK and explicitly validated QTS_TEST_POSTGRES_CONNECTION. Fixture DDL/data writes are permitted only in a later authorized test run; never inherit production credentials.

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~TestQtsBilling.Integration.OperationalTelemetryTests"
```

**PASS:** owning cases and relevant existing regression filters pass, with exact committed-state assertions where applicable; no skipped required prerequisite, new unreviewed dependency, secret leak or `git diff --check` failure. A build or mocked response alone is not financial proof.

Select the checked server or Windows build command in §6.3 only when this task changes that host. Full multi-platform regression belongs in CI/release, not every local edit.

### G. Definition of done

Operators can detect failed financial/sync/delivery/recovery behavior and investigate safely without logging sensitive payloads. Evidence includes the exact implementation SHA, focused test outcome and minimal reviewed diff. All new constraints/contracts/permissions/host wiring ship together under **G7**; unresolved external prerequisites stay explicit.

No unnecessary dependency or compatibility path remains. Recovery preserves historical facts and pending money. Record remaining blockers; spec completion is not implementation completion or owner production approval.

<a id="task-60"></a>
## Atomic Task 60 - Make CI prove PostgreSQL, native import, offline and UI correctness

### A. Objective

**Priority:** P0. **Requirements:** `R01`, `R02`, `R18`. **Findings:** `F39`, `F40`, `F45`.
**Dependencies:** [Task 01](#task-01), [Task 19](#task-19), [Task 20](#task-20), [Task 24](#task-24), [Task 25](#task-25), [Task 27](#task-27), [Task 28](#task-28), [Task 35](#task-35), [Task 37](#task-37), [Task 38](#task-38), [Task 39](#task-39), [Task 42](#task-42), [Task 45](#task-45), [Task 46](#task-46), [Task 48](#task-48), [Task 50](#task-50), [Task 51](#task-51), [Task 53](#task-53), [Task 55](#task-55), [Task 57](#task-57), [Task 58](#task-58), [Task 59](#task-59) **Integration gate:** G7.

**Current → target:** The observed green CI run excludes PostgreSQL/API/import integration and critical operational/security checks. → Every PR and candidate SHA is verified by the applicable complete automated suite, with honest prerequisites and no silent skips.

**Acceptance:** The target behavior is observable through the owning API/UI/store and every applicable scenario in E passes; release additionally requires the named gate, not just this file diff.

**Non-goals:** No claim that current CI already contains these tests. New Postgres and browser checks are mandatory financial assurance, not optional busywork.

### B. Scope

| Action | Exact path | Change / exclusion |
|---|---|---|
| MODIFY | `.github/workflows/ci.yml` | Required domain/native-import/PG/API/SQLite/UI/Windows/security jobs with artifacts. |
| MODIFY | `TestQtsBilling/TestQtsBilling.csproj` | Minimal reviewed test dependencies and exact category/filter classification. |
| MODIFY | `QtsBilling.Ui.Smoke/Program.cs` | Real shared-render/action safety coverage with allowed dependency graph. |
| MODIFY | `scripts/secret-scan.ps1` | Narrow documented fixture allowlist and nonzero failures suitable for CI. |
| CREATE — NEW test | `TestQtsBilling/Operations/CiCoverageTests.cs` | Owning focused assertions below; reuse fixtures rather than duplicate a test infrastructure. |

All other production paths are excluded except the narrow, explicitly enumerated [D17 integration bindings](#d17). Applied migrations, posted facts and unresolved cash are never edited/deleted by this scope.

### C. Verified anchors and NEW insertions

- **EXISTING / VERIFIED:** [.github/workflows/ci.yml — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/.github/workflows/ci.yml): `build-and-test; build-desktop`. [E41](#e-e41).
- **EXISTING / VERIFIED:** [.github/workflows/release.yml — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/.github/workflows/release.yml): `release job`. [E42](#e-e42).
- **EXISTING / VERIFIED:** [QtsBilling.Persistence.Sqlite.Tests/LocalStoreHarness.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Persistence.Sqlite.Tests/LocalStoreHarness.cs): `LocalStoreHarness; TestData`. [E46](#e-e46).
- **EXISTING / VERIFIED:** [QtsBilling.Ui.Smoke/QtsBilling.Ui.Smoke.csproj — source lines 1-20](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Ui.Smoke/QtsBilling.Ui.Smoke.csproj#L1-L20): `OutputType Exe`. [E47](#e-e47).

All CREATE/GENERATE symbols, routes and constraints in B/D are **NEW**. Source presence is not runtime proof; do not infer executed results from old evidence prose.

### D. Ordered implementation recipe

1. Run domain/contracts/native package tests and real temp-file SQLite tests on Linux. Run Task 01 isolated PostgreSQL 18 service with owner/application roles for API/financial/concurrency/native import integration; no production secrets or shared DB.
2. Run shared UI smoke and Playwright 1.63.0 browser flows against the built disposable test deployment; pin browser binaries to the package version. Use a separately categorized Windows job for MAUI build/native callback/platform checks.
3. Add secret/positive-dependency/native-trace checks, package vulnerability review and build warnings agreed as blockers. Scan only narrowly identified public development fixtures; new matching secrets cannot be blanket allowlisted.
4. Publish TRX/logs, failing-case diagnostics and reproducible test seeds/DB version, with secrets removed. Required integration tests fail when prerequisites are absent rather than returning success/skip. Include the new native import project; retire old source-specific test references.
5. Keep local development commands focused by feature/test category. Full regression/platform suite belongs in CI and release-candidate validation, not every atomic local edit. Protect required checks against mutable branch names or workflows that are present but not required.

Apply the specific D01–D17 policies relevant to this slice. Finish the narrow route/model/client registration in B, record generated migration paths, and preserve a safe stop/fix-forward path before presenting the diff for review.

### E. Focused tests

**NEW file:** `TestQtsBilling/Operations/CiCoverageTests.cs`. Name the class `CiCoverageTests` in the directory-derived namespace specified in §6.3.

- **T60.01:** Given intentional inclusive-tax, cross-tenant, receipt-transient or importer-signature regression, when the task-specific command or transition is exercised, then CI fails at its owning test.
- **T60.02:** Given missing PostgreSQL/PowerShell/browser prerequisite, when the task-specific command or transition is exercised, then a required job fails, not skips green.
- **T60.03:** Given injected persistence dependency or secret outside approved fixtures, when the task-specific command or transition is exercised, then CI blocks.
- **T60.04:** Given a clean native candidate, when the task-specific command or transition is exercised, then all selected categories run and artifact results bind the same SHA.
- **T60.05:** Given a docs-only local edit, when the task-specific command or transition is exercised, then developers need not run the full suite, while required remote checks remain intact.

Reuse existing or earlier-task fixtures. For a mutation, also assert denied tenant/location/permission and injected pre-commit failure leave zero partial financial/audit effects; for a read/UI-only task assert no mutation. Reuse the same fixture rather than duplicating equivalent tests.

### F. Verification

**Status: NOT RUN - SPEC_ONLY.** The following filter becomes executable only after the NEW test class and prerequisites are implemented.

Use explicit fake process/provider adapters or fixture-only PowerShell 7 harnesses. Never execute production admission/restore/deploy as a test side effect.

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~TestQtsBilling.Operations.CiCoverageTests"
```

**PASS:** owning cases and relevant existing regression filters pass, with exact committed-state assertions where applicable; no skipped required prerequisite, new unreviewed dependency, secret leak or `git diff --check` failure. A build or mocked response alone is not financial proof.

Select the checked server or Windows build command in §6.3 only when this task changes that host. Full multi-platform regression belongs in CI/release, not every local edit.

### G. Definition of done

Every PR and candidate SHA is verified by the applicable complete automated suite, with honest prerequisites and no silent skips. Evidence includes the exact implementation SHA, focused test outcome and minimal reviewed diff. All new constraints/contracts/permissions/host wiring ship together under **G7**; unresolved external prerequisites stay explicit.

No unnecessary dependency or compatibility path remains. Recovery preserves historical facts and pending money. Record remaining blockers; spec completion is not implementation completion or owner production approval.

<a id="task-61"></a>
## Atomic Task 61 - Promote only tested signed artifacts through explicit release approval

### A. Objective

**Priority:** P0. **Requirements:** `R02`, `R04`, `R18`. **Findings:** `F01`, `F40`, `F44`.
**Dependencies:** [Task 21](#task-21), [Task 23](#task-23), [Task 57](#task-57), [Task 58](#task-58), [Task 60](#task-60) **Integration gate:** G7.

**Current → target:** A v* push publishes freshly built unsigned unpackaged artifacts without a required validation or owner admission dependency. → A release consists of the exact validated server/desktop/import bytes and can be promoted only by an explicitly authorized owner process.

**Acceptance:** The target behavior is observable through the owning API/UI/store and every applicable scenario in E passes; release additionally requires the named gate, not just this file diff.

**Non-goals:** No actual branch-protection change, tag, release, signing, merge or deployment is authorized by this document. Trusted signing and owner codeword are BLOCKED_EXTERNAL.

### B. Scope

| Action | Exact path | Change / exclusion |
|---|---|---|
| MODIFY | `.github/workflows/release.yml` | Same-SHA required validation, protected environment, provenance and artifact promotion. |
| MODIFY | `QtsBilling.Maui/QtsBilling.Maui.csproj` | Release-only signed MSIX settings without embedded certificate secrets. |
| MODIFY | `QtsBilling.Maui/Platforms/Windows/Package.appxmanifest` | Stable package identity/version and supported install/update configuration. |
| CREATE — NEW | `docs/operations/release.md` | Exact release identity, owner confirmation, upgrade and recovery handover. |
| CREATE — NEW test | `TestQtsBilling/Operations/ReleaseManifestTests.cs` | Owning focused assertions below; reuse fixtures rather than duplicate a test infrastructure. |

All other production paths are excluded except the narrow, explicitly enumerated [D17 integration bindings](#d17). Applied migrations, posted facts and unresolved cash are never edited/deleted by this scope.

### C. Verified anchors and NEW insertions

- **EXISTING / VERIFIED:** [.github/workflows/release.yml — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/.github/workflows/release.yml): `release job`. [E42](#e-e42).
- **EXISTING / VERIFIED:** [QtsBilling.sln — source lines 1-34](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.sln#L1-L34): `Solution project graph`. [E01](#e-e01).
- **EXISTING / VERIFIED:** [docs/migration/deployment.md — source lines 1-150](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/docs/migration/deployment.md#L1-L150): `Sections 7 and 9`. [E45](#e-e45).

All CREATE/GENERATE symbols, routes and constraints in B/D are **NEW**. Source presence is not runtime proof; do not infer executed results from old evidence prose.

### D. Ordered implementation recipe

1. Bind release to one commit and immutable artifact manifest; require CI outcomes from that exact SHA, not another branch head or a file saying PASS. Use trusted reusable validation/build jobs or promote their artifacts without rebuilding changed bytes.
2. Restrict version tags/environment approvals and branch protection through separately authorized repository administration. A tag is an event, not production approval. Pin Actions by reviewed commit and toolchain/native package versions; use least workflow permissions.
3. Build signed Windows MSIX with stable application identity, monotonically valid version and trusted certificate from protected secrets/signing service. Keep unpackaged developer build separate; never commit certificate/password/private key.
4. Publish server Windows/Linux and desktop checksums, dependency/software bill of materials, source SHA, schema/API/device compatibility and signing provenance. New native importer is an operator artifact, not exposed in the webroot/client bundle.
5. Upgrade admission requires native pending-cash preservation and Task 21 environment-bound approval. Maintain the last known compatible artifact for safe pre-write rollback; after new financial writes use stop/fix-forward unless a separately reconciled recovery is approved.

Apply the specific D01–D17 policies relevant to this slice. Finish the narrow route/model/client registration in B, record generated migration paths, and preserve a safe stop/fix-forward path before presenting the diff for review.

### E. Focused tests

**NEW file:** `TestQtsBilling/Operations/ReleaseManifestTests.cs`. Name the class `ReleaseManifestTests` in the directory-derived namespace specified in §6.3.

- **T61.01:** Given a tag with failed or absent same-SHA tests, when the task-specific command or transition is exercised, then publication/promotion is blocked.
- **T61.02:** Given artifact bytes changed after validation, when the task-specific command or transition is exercised, then digest/signature checks fail.
- **T61.03:** Given unsigned or wrong-identity Windows package, when the task-specific command or transition is exercised, then the production install gate fails.
- **T61.04:** Given attempted promotion without protected owner approval/codeword, when the task-specific command or transition is exercised, then no deployment occurs.
- **T61.05:** Given valid upgrade with queued cash, when the task-specific command or transition is exercised, then package/update preserves local identity and durable records.

Reuse existing or earlier-task fixtures. For a mutation, also assert denied tenant/location/permission and injected pre-commit failure leave zero partial financial/audit effects; for a read/UI-only task assert no mutation. Reuse the same fixture rather than duplicating equivalent tests.

### F. Verification

**Status: NOT RUN - SPEC_ONLY.** The following filter becomes executable only after the NEW test class and prerequisites are implemented.

Use explicit fake process/provider adapters or fixture-only PowerShell 7 harnesses. Never execute production admission/restore/deploy as a test side effect.

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~TestQtsBilling.Operations.ReleaseManifestTests"
```

**PASS:** owning cases and relevant existing regression filters pass, with exact committed-state assertions where applicable; no skipped required prerequisite, new unreviewed dependency, secret leak or `git diff --check` failure. A build or mocked response alone is not financial proof.

Select the checked server or Windows build command in §6.3 only when this task changes that host. Full multi-platform regression belongs in CI/release, not every local edit.

### G. Definition of done

A release consists of the exact validated server/desktop/import bytes and can be promoted only by an explicitly authorized owner process. Evidence includes the exact implementation SHA, focused test outcome and minimal reviewed diff. All new constraints/contracts/permissions/host wiring ship together under **G7**; unresolved external prerequisites stay explicit.

No unnecessary dependency or compatibility path remains. Recovery preserves historical facts and pending money. Record remaining blockers; spec completion is not implementation completion or owner production approval.

<a id="task-62"></a>
## Atomic Task 62 - Verify Windows lifecycle, browser accessibility and receipt peripherals

### A. Objective

**Priority:** P1. **Requirements:** `R04`, `R09`, `R11`, `R17`, `R18`. **Findings:** `F10`, `F12`, `F28`, `F29`, `F30`, `F44`.
**Dependencies:** [Task 23](#task-23), [Task 28](#task-28), [Task 29](#task-29), [Task 34](#task-34), [Task 35](#task-35), [Task 36](#task-36), [Task 40](#task-40), [Task 43](#task-43), [Task 47](#task-47), [Task 49](#task-49), [Task 52](#task-52), [Task 54](#task-54), [Task 56](#task-56), [Task 61](#task-61) **Integration gate:** G7.

**Current → target:** A successful MAUI compilation does not prove trusted install, callback, suspend/resume, input, printing or user data recovery. → The declared supported Windows/browser platforms complete their real workflows and retain pending cash through lifecycle events.

**Acceptance:** The target behavior is observable through the owning API/UI/store and every applicable scenario in E passes; release additionally requires the named gate, not just this file diff.

**Non-goals:** Real hardware/OS validation cannot be replaced with Linux unit tests. Do not claim accessibility certification from this plan alone.

### B. Scope

| Action | Exact path | Change / exclusion |
|---|---|---|
| CREATE — NEW | `TestQtsBilling/Ui/SupportedHostJourneys.cs` | Shared browser journeys and explicit native manual/automated evidence contracts. |
| MODIFY | `QtsBilling.Maui/MauiProgram.cs` | Only evidenced host lifecycle/registration fixes found by platform verification. |
| MODIFY | `QtsBilling.Maui/App.xaml.cs` | Cancellation/resume/window-close recovery behavior without losing pending cash. |
| CREATE — NEW | `docs/operations/windows-validation.md` | Supported Windows/WebView2 install, callback, scan/print and accessibility checklist. |
| CREATE — NEW test | `TestQtsBilling/Ui/SupportedHostTests.cs` | Owning focused assertions below; reuse fixtures rather than duplicate a test infrastructure. |

All other production paths are excluded except the narrow, explicitly enumerated [D17 integration bindings](#d17). Applied migrations, posted facts and unresolved cash are never edited/deleted by this scope.

### C. Verified anchors and NEW insertions

- **EXISTING / VERIFIED:** [QtsBilling.Maui/Services/DeviceLocalPosService.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Maui/Services/DeviceLocalPosService.cs): `CaptureCashSaleAsync`. [E23](#e-e23).
- **EXISTING / VERIFIED:** [QtsBilling.Maui/Services/DeviceAccountApiClient.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Maui/Services/DeviceAccountApiClient.cs): `GetSessionAsync`. [E24](#e-e24).
- **EXISTING / VERIFIED:** [QtsBilling.Maui/Services/SessionStore.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Maui/Services/SessionStore.cs): `SaveSessionAsync; GetSessionAsync`. [E25](#e-e25).
- **EXISTING / VERIFIED:** [QtsBilling.Ui/Pages/Pos.razor.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Ui/Pages/Pos.razor.cs): `LineTax; LineTotal; CaptureAsync`. [E26](#e-e26).
- **EXISTING / VERIFIED:** [QtsBilling.Ui.Smoke/QtsBilling.Ui.Smoke.csproj — source lines 1-20](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Ui.Smoke/QtsBilling.Ui.Smoke.csproj#L1-L20): `OutputType Exe`. [E47](#e-e47).

All CREATE/GENERATE symbols, routes and constraints in B/D are **NEW**. Source presence is not runtime proof; do not infer executed results from old evidence prose.

### D. Ordered implementation recipe

1. Define support from actual .csproj/manifest and installed supported Windows/WebView2 versions; record tested OS/runtime/build and do not infer Android/iOS/macOS support.
2. Exercise signed clean install, first-run setup, system-browser callback, restart, sleep/resume, network transition, multi-window/process contention and upgrade with pending/rejected cash. Enforce one active store writer/lease policy across application instances.
3. Run real user journeys in browser and WebView2: keyboard-only, visible focus, accessible field names, error announcements, scaling/high contrast and narrow desktop window. Dynamic data changes must render after awaited operations without duplicate submission.
4. Validate keyboard-wedge barcode scanner input, quantity selection, decimal culture and standard printer dialogs/receipt page breaks with declared hardware. Reprint failure/driver unavailability never creates another financial operation.
5. Record platform evidence with sanitized fixture data and before/after cash/queue checks. Fix only demonstrated host issues; a checklist item without actual execution is NOT RUN and blocks the applicable native release.

Apply the specific D01–D17 policies relevant to this slice. Finish the narrow route/model/client registration in B, record generated migration paths, and preserve a safe stop/fix-forward path before presenting the diff for review.

### E. Focused tests

**NEW file:** `TestQtsBilling/Ui/SupportedHostTests.cs`. Name the class `SupportedHostTests` in the directory-derived namespace specified in §6.3.

- **T62.01:** Given signed update with 100 pending operations, when the task-specific command or transition is exercised, then identity/count/amount/fingerprints survive unchanged.
- **T62.02:** Given sleep/resume and concurrent manual sync, when the task-specific command or transition is exercised, then no duplicate push or stuck Sending lease remains.
- **T62.03:** Given keyboard-only checkout and validation failure, when the task-specific command or transition is exercised, then focus and error announcement are usable.
- **T62.04:** Given scanner/print failure, when the task-specific command or transition is exercised, then cart intent remains clear and financial posting occurs once.
- **T62.05:** Given another process opens the device store, when the task-specific command or transition is exercised, then unsafe concurrent custody mutation is prevented.

Reuse existing or earlier-task fixtures. For a mutation, also assert denied tenant/location/permission and injected pre-commit failure leave zero partial financial/audit effects; for a read/UI-only task assert no mutation. Reuse the same fixture rather than duplicating equivalent tests.

### F. Verification

**Status: NOT RUN - SPEC_ONLY.** The following filter becomes executable only after the NEW test class and prerequisites are implemented.

Requires the owning APIs, shared UI smoke/Playwright setup and disposable native data. The actual Windows/WebView2 checks remain a separate platform gate.

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~TestQtsBilling.Ui.SupportedHostTests"
```

**PASS:** owning cases and relevant existing regression filters pass, with exact committed-state assertions where applicable; no skipped required prerequisite, new unreviewed dependency, secret leak or `git diff --check` failure. A build or mocked response alone is not financial proof.

Run the existing Windows-scoped build command, then the signed install/callback/print scenarios documented here on actual approved devices. Browser Playwright runs do not prove WebView2/native callback behavior.

Select the checked server or Windows build command in §6.3 only when this task changes that host. Full multi-platform regression belongs in CI/release, not every local edit.

### G. Definition of done

The declared supported Windows/browser platforms complete their real workflows and retain pending cash through lifecycle events. Evidence includes the exact implementation SHA, focused test outcome and minimal reviewed diff. All new constraints/contracts/permissions/host wiring ship together under **G7**; unresolved external prerequisites stay explicit.

No unnecessary dependency or compatibility path remains. Recovery preserves historical facts and pending money. Record remaining blockers; spec completion is not implementation completion or owner production approval.

<a id="task-63"></a>
## Atomic Task 63 - Rehearse full business journeys, load and recovery before pilot acceptance

### A. Objective

**Priority:** P0. **Requirements:** `R02`, `R06`, `R07`, `R09`, `R12`, `R13`, `R15`, `R18`. **Findings:** `F01`, `F02`, `F38`, `F39`, `F40`, `F44`.
**Dependencies:** [Task 20](#task-20), [Task 21](#task-21), [Task 29](#task-29), [Task 35](#task-35), [Task 36](#task-36), [Task 37](#task-37), [Task 40](#task-40), [Task 43](#task-43), [Task 47](#task-47), [Task 49](#task-49), [Task 50](#task-50), [Task 51](#task-51), [Task 52](#task-52), [Task 53](#task-53), [Task 54](#task-54), [Task 55](#task-55), [Task 56](#task-56), [Task 58](#task-58), [Task 59](#task-59), [Task 60](#task-60), [Task 61](#task-61), [Task 62](#task-62) **Integration gate:** G7.

**Current → target:** The all-DONE summary and successful small fixture run do not demonstrate complete SMB workflows or a real production pilot. → A named candidate meets all applicable automated, operational and human acceptance gates with exact financial and custody evidence.

**Acceptance:** The target behavior is observable through the owning API/UI/store and every applicable scenario in E passes; release additionally requires the named gate, not just this file diff.

**Non-goals:** No real production pilot or cutover execution is performed by this specification. Business acceptance remains an external decision.

### B. Scope

| Action | Exact path | Change / exclusion |
|---|---|---|
| CREATE — NEW | `TestQtsBilling/Acceptance/BusinessJourneyTests.cs` | Cross-module native retail/wholesale/service fixtures and invariant tie-outs. |
| CREATE — NEW | `TestQtsBilling/Acceptance/RecoveryAndLoadTests.cs` | Bounded reference workload and crash/restore/retry scenarios. |
| CREATE — NEW | `docs/operations/pilot-acceptance.md` | Named business/devices/actors/window and independent approval evidence. |
| CREATE — NEW test | `TestQtsBilling/Acceptance/BusinessJourneyTests.cs` | Owning focused assertions below; reuse fixtures rather than duplicate a test infrastructure. |

All other production paths are excluded except the narrow, explicitly enumerated [D17 integration bindings](#d17). Applied migrations, posted facts and unresolved cash are never edited/deleted by this scope.

### C. Verified anchors and NEW insertions

- **EXISTING / VERIFIED:** [scripts/qb30-cutover.ps1 — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/scripts/qb30-cutover.ps1): `Add-Gate and whole cutover flow`. [E40](#e-e40).
- **EXISTING / VERIFIED:** [.github/workflows/ci.yml — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/.github/workflows/ci.yml): `build-and-test; build-desktop`. [E41](#e-e41).
- **EXISTING / VERIFIED:** [docs/migration/deployment.md — source lines 1-150](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/docs/migration/deployment.md#L1-L150): `Sections 7 and 9`. [E45](#e-e45).
- **EXISTING / VERIFIED:** [QtsBilling.Persistence.Sqlite.Tests/LocalStoreHarness.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Persistence.Sqlite.Tests/LocalStoreHarness.cs): `LocalStoreHarness; TestData`. [E46](#e-e46).

All CREATE/GENERATE symbols, routes and constraints in B/D are **NEW**. Source presence is not runtime proof; do not infer executed results from old evidence prose.

### D. Ordered implementation recipe

1. Execute J01-J08 from Summary against isolated native data with two businesses and realistic roles; record each source document and before/after journal/stock/value/AR/AP/cash/receipt equality. Include inclusive tax, pack units, partials, services and boundary dates.
2. Inject failures around every irreversible transaction boundary and unknown commit acknowledgement; exercise parallel last-stock sale, close/post races, duplicate native import, grant revocation, partial resync and rejected cash. Recovery must preserve original identities and amounts.
3. Run the declared reference workload with measured p95/query/lock/backlog numbers and hardware/network recorded. Failing budgets cause a concrete performance task; do not weaken exact financial checks to achieve a latency number.
4. Restore actual representative protected backup/files/keys into isolated infrastructure and prove approved RPO/RTO. Perform signed Windows upgrade/recovery with unacknowledged cash and reconcile all retained queues.
5. Pilot approval names actual business, devices, cashiers/accountant, trading/acceptance window, support owner, abort thresholds and evidence digest. Finance/security/owner independently sign off; untested feature profiles stay disabled and no business approval is inferred from test success.

Apply the specific D01–D17 policies relevant to this slice. Finish the narrow route/model/client registration in B, record generated migration paths, and preserve a safe stop/fix-forward path before presenting the diff for review.

### E. Focused tests

**NEW file:** `TestQtsBilling/Acceptance/BusinessJourneyTests.cs`. Name the class `BusinessJourneyTests` in the directory-derived namespace specified in §6.3.

- **T63.01:** Given each J01-J08 fixture, when the task-specific command or transition is exercised, then the complete UI/API/DB journey and its financial/custody assertions pass.
- **T63.02:** Given any cross-business access or nonzero unexplained financial difference, when the task-specific command or transition is exercised, then candidate acceptance fails.
- **T63.03:** Given load test, when the task-specific command or transition is exercised, then reported numbers are actual measurements and meet the accepted workload budget.
- **T63.04:** Given crash/restore/upgrade, when the task-specific command or transition is exercised, then committed facts and pending money reconcile exactly.
- **T63.05:** Given missing named pilot/finance/security approval, when the task-specific command or transition is exercised, then production admission remains blocked despite green tests.

Reuse existing or earlier-task fixtures. For a mutation, also assert denied tenant/location/permission and injected pre-commit failure leave zero partial financial/audit effects; for a read/UI-only task assert no mutation. Reuse the same fixture rather than duplicating equivalent tests.

### F. Verification

**Status: NOT RUN - SPEC_ONLY.** The following filter becomes executable only after the NEW test class and prerequisites are implemented.

Requires Task 01's isolated PostgreSQL 18/API fixture, .NET 10 SDK and explicitly validated QTS_TEST_POSTGRES_CONNECTION. Fixture DDL/data writes are permitted only in a later authorized test run; never inherit production credentials.

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~TestQtsBilling.Acceptance.BusinessJourneyTests"
```

**PASS:** owning cases and relevant existing regression filters pass, with exact committed-state assertions where applicable; no skipped required prerequisite, new unreviewed dependency, secret leak or `git diff --check` failure. A build or mocked response alone is not financial proof.

Select the checked server or Windows build command in §6.3 only when this task changes that host. Full multi-platform regression belongs in CI/release, not every local edit.

### G. Definition of done

A named candidate meets all applicable automated, operational and human acceptance gates with exact financial and custody evidence. Evidence includes the exact implementation SHA, focused test outcome and minimal reviewed diff. All new constraints/contracts/permissions/host wiring ship together under **G7**; unresolved external prerequisites stay explicit.

No unnecessary dependency or compatibility path remains. Recovery preserves historical facts and pending money. Record remaining blockers; spec completion is not implementation completion or owner production approval.

<a id="task-64"></a>
## Atomic Task 64 - Finalize the native product tree, truthful readiness record and handover

### A. Objective

**Priority:** P0. **Requirements:** `R01`, `R02`, `R16`, `R18`. **Findings:** `F01`, `F02`, `F39`, `F40`, `F41`, `F42`, `F44`, `F45`.
**Dependencies:** [Task 01](#task-01), [Task 02](#task-02), [Task 03](#task-03), [Task 04](#task-04), [Task 05](#task-05), [Task 06](#task-06), [Task 07](#task-07), [Task 08](#task-08), [Task 09](#task-09), [Task 10](#task-10), [Task 11](#task-11), [Task 12](#task-12), [Task 13](#task-13), [Task 14](#task-14), [Task 15](#task-15), [Task 16](#task-16), [Task 17](#task-17), [Task 18](#task-18), [Task 19](#task-19), [Task 20](#task-20), [Task 21](#task-21), [Task 22](#task-22), [Task 23](#task-23), [Task 24](#task-24), [Task 25](#task-25), [Task 26](#task-26), [Task 27](#task-27), [Task 28](#task-28), [Task 29](#task-29), [Task 30](#task-30), [Task 31](#task-31), [Task 32](#task-32), [Task 33](#task-33), [Task 34](#task-34), [Task 35](#task-35), [Task 36](#task-36), [Task 37](#task-37), [Task 38](#task-38), [Task 39](#task-39), [Task 40](#task-40), [Task 41](#task-41), [Task 42](#task-42), [Task 43](#task-43), [Task 44](#task-44), [Task 45](#task-45), [Task 46](#task-46), [Task 47](#task-47), [Task 48](#task-48), [Task 49](#task-49), [Task 50](#task-50), [Task 51](#task-51), [Task 52](#task-52), [Task 53](#task-53), [Task 54](#task-54), [Task 55](#task-55), [Task 56](#task-56), [Task 57](#task-57), [Task 58](#task-58), [Task 59](#task-59), [Task 60](#task-60), [Task 61](#task-61), [Task 62](#task-62), [Task 63](#task-63) **Integration gate:** G7.

**Current → target:** Repository prose still overstates completion and contains retired-platform traces and conflicting recovery policy. → The supported tree and new release are fully native, documentation matches code/evidence, and remaining external approvals are unmistakable.

**Acceptance:** The target behavior is observable through the owning API/UI/store and every applicable scenario in E passes; release additionally requires the named gate, not just this file diff.

**Non-goals:** No history rewrite, external-fork cleanup, production deletion, merge, release or deployment. A specification is not implementation or production authorization.

### B. Scope

| Action | Exact path | Change / exclusion |
|---|---|---|
| MODIFY | `README.md` | Current native setup/features/verified limitations and exact supported commands. |
| MODIFY | `docs/migration/decisions.md` | Truthful superseding native decisions and UTF-8 repair; remove retired-provider/platform literals. |
| MODIFY | `docs/migration/task-status.md` | Evidence-bound capability states, no all-DONE or production-approved overclaim. |
| MODIFY | `docs/migration/deployment.md` | Single consistent production/recovery procedure; no retired-system instructions. |
| CREATE — NEW | `docs/operations/production-readiness.md` | Final native requirements-to-evidence/gates/blockers and signed handover references. |
| CREATE — NEW test | `TestQtsBilling/Architecture/NativeProductReadinessTests.cs` | Owning focused assertions below; reuse fixtures rather than duplicate a test infrastructure. |

All other production paths are excluded except the narrow, explicitly enumerated [D17 integration bindings](#d17). Applied migrations, posted facts and unresolved cash are never edited/deleted by this scope.

### C. Verified anchors and NEW insertions

- **EXISTING / VERIFIED:** [QtsBilling.sln — source lines 1-34](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.sln#L1-L34): `Solution project graph`. [E01](#e-e01).
- **EXISTING / VERIFIED:** [QtsBilling.Migration/SnapshotSource.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Migration/SnapshotSource.cs): `VerifyAsync; ReadDocumentsAsync; InventoryAsync`. [E36](#e-e36).
- **EXISTING / VERIFIED:** [QtsBilling.Migration/Program.cs — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Migration/Program.cs): `ImportAsync; ReconcileAsync; TransformAsync`. [E37](#e-e37).
- **EXISTING / VERIFIED:** [.github/workflows/ci.yml — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/.github/workflows/ci.yml): `build-and-test; build-desktop`. [E41](#e-e41).
- **EXISTING / VERIFIED:** [.github/workflows/release.yml — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/.github/workflows/release.yml): `release job`. [E42](#e-e42).
- **EXISTING / VERIFIED:** [docs/migration/task-status.md — source lines 1-85](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/docs/migration/task-status.md#L1-L85): `QB-06 and QB-23 through QB-30 status claims`. [E43](#e-e43).
- **EXISTING / VERIFIED:** [docs/migration/decisions.md — source lines 1-155](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/docs/migration/decisions.md#L1-L155): `D-01...D-41 and historical decisions`. [E44](#e-e44).
- **EXISTING / VERIFIED:** [docs/migration/deployment.md — source lines 1-150](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/docs/migration/deployment.md#L1-L150): `Sections 7 and 9`. [E45](#e-e45).
- **EXISTING / VERIFIED:** [README.md — whole file; named body inspected](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/README.md): `Architecture / status / project descriptions`. [E48](#e-e48).

All CREATE/GENERATE symbols, routes and constraints in B/D are **NEW**. Source presence is not runtime proof; do not infer executed results from old evidence prose.

### D. Ordered implementation recipe

1. Recheck remote default-branch SHA and compare with this baseline; reassess affected tasks rather than overwriting newer completed work. Verify every scoped file/route/test and mark actual implemented/partial/missing/unverified states from executed evidence.
2. Complete Task 02 removal across tracked names/content/comments/generated artifacts/packages/fixtures/docs/scripts and new release assets, including every enumerated hit and additional discovered matches. Use positive dependency policy; no provider/platform compatibility branch or source importer survives.
3. Preserve generic native financial provenance and applied migration history unless a separately approved reconciled migration explicitly retires it. Removing old product traces does not authorize Git history rewrite, old release deletion, data loss or pending-cash erasure.
4. Repair UTF-8 mojibake and broken references; publish current native architecture, operational roles, exact configuration/commands and actual support scope. Requirements map to task/test/evidence SHA; do not repeat old pass counts as current assurance.
5. Finalize G0-G7 results, owner/finance/security/pilot approval manifest, backup recovery and fix-forward handover. Missing external evidence remains BLOCKED_EXTERNAL. Only a later explicit owner-authorized production process can declare the deployment admitted.

Apply the specific D01–D17 policies relevant to this slice. Finish the narrow route/model/client registration in B, record generated migration paths, and preserve a safe stop/fix-forward path before presenting the diff for review.

### E. Focused tests

**NEW file:** `TestQtsBilling/Architecture/NativeProductReadinessTests.cs`. Name the class `NativeProductReadinessTests` in the directory-derived namespace specified in §6.3.

- **T64.01:** Given tracked candidate and new release contents, when the task-specific command or transition is exercised, then no removed-provider/platform trace or source-format adapter remains.
- **T64.02:** Given links/commands/status descriptions, when the task-specific command or transition is exercised, then they resolve to the current native implementation and evidence.
- **T64.03:** Given old successful run at another SHA, when the task-specific command or transition is exercised, then it cannot satisfy current readiness.
- **T64.04:** Given unresolved money, financial mismatch or missing owner approval, when the task-specific command or transition is exercised, then final readiness refuses production admission.
- **T64.05:** Given a fully evidenced candidate, when the task-specific command or transition is exercised, then the handover identifies exact tested bytes, schema, native data and recovery procedure without executing deployment.

Reuse existing or earlier-task fixtures. For a mutation, also assert denied tenant/location/permission and injected pre-commit failure leave zero partial financial/audit effects; for a read/UI-only task assert no mutation. Reuse the same fixture rather than duplicating equivalent tests.

### F. Verification

**Status: NOT RUN - SPEC_ONLY.** The following filter becomes executable only after the NEW test class and prerequisites are implemented.

Requires .NET 10 SDK and the NEW owning test class. Use pure fixtures/native files; no production service connection.

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~TestQtsBilling.Architecture.NativeProductReadinessTests"
```

**PASS:** owning cases and relevant existing regression filters pass, with exact committed-state assertions where applicable; no skipped required prerequisite, new unreviewed dependency, secret leak or `git diff --check` failure. A build or mocked response alone is not financial proof.

Select the checked server or Windows build command in §6.3 only when this task changes that host. Full multi-platform regression belongs in CI/release, not every local edit.

### G. Definition of done

The supported tree and new release are fully native, documentation matches code/evidence, and remaining external approvals are unmistakable. Evidence includes the exact implementation SHA, focused test outcome and minimal reviewed diff. All new constraints/contracts/permissions/host wiring ship together under **G7**; unresolved external prerequisites stay explicit.

No unnecessary dependency or compatibility path remains. Recovery preserves historical facts and pending money. Record remaining blockers; spec completion is not implementation completion or owner production approval.


<a id="traceability"></a>
## 6. Traceability, verification, rollout and blockers

### 6.1 Requirement → task → test → acceptance

Every primary test file in the matrix below is **NEW**, except the existing fixtures reused by those tests. Tests listed in E are scenarios to implement, not evidence that they have run. The task-specific objective and assertions define acceptance; G0–G7 govern release coupling.

| Requirement | Tasks | Representative focused tests | Acceptance |
|---|---|---|---|
| R01 | [Task 01](#task-01), [Task 02](#task-02), [Task 18](#task-18), [Task 57](#task-57), [Task 60](#task-60), [Task 64](#task-64) | `TestQtsBilling/Architecture/NativeBoundaryTests.cs` (T02.01–04); `TestQtsBilling/DataImport/NativePackageTests.cs` (T18.01–05) | Exact requirement in §1 requirement matrix + owning E assertions; release gate(s) in task index. |
| R02 | [Task 01](#task-01), [Task 58](#task-58), [Task 60](#task-60), [Task 61](#task-61), [Task 63](#task-63), [Task 64](#task-64) | `TestQtsBilling/Operations/RecoveryControlsTests.cs` (T58.01–05); `TestQtsBilling/Operations/ReleaseManifestTests.cs` (T61.01–05) | Exact requirement in §1 requirement matrix + owning E assertions; release gate(s) in task index. |
| R03 | [Task 04](#task-04), [Task 05](#task-05), [Task 06](#task-06), [Task 07](#task-07), [Task 08](#task-08), [Task 22](#task-22), [Task 23](#task-23), [Task 24](#task-24), [Task 26](#task-26), [Task 27](#task-27), [Task 56](#task-56), [Task 57](#task-57), [Task 59](#task-59) | `TestQtsBilling/Integration/BusinessAuthorizationTests.cs` (T04.01–05); `TestQtsBilling/Integration/TenantConstraintTests.cs` (T05.01–04); `TestQtsBilling/Integration/OperationalTelemetryTests.cs` (T59.01–05) | Exact requirement in §1 requirement matrix + owning E assertions; release gate(s) in task index. |
| R04 | [Task 04](#task-04), [Task 06](#task-06), [Task 07](#task-07), [Task 22](#task-22), [Task 23](#task-23), [Task 28](#task-28), [Task 55](#task-55), [Task 56](#task-56), [Task 61](#task-61), [Task 62](#task-62) | `TestQtsBilling/Integration/BusinessAuthorizationTests.cs` (T04.01–05); `TestQtsBilling/Integration/HttpBoundaryTests.cs` (T06.01–05); `TestQtsBilling/Ui/SupportedHostTests.cs` (T62.01–05) | Exact requirement in §1 requirement matrix + owning E assertions; release gate(s) in task index. |
| R05 | [Task 03](#task-03), [Task 08](#task-08), [Task 12](#task-12), [Task 13](#task-13), [Task 29](#task-29), [Task 32](#task-32), [Task 33](#task-33), [Task 35](#task-35) | `TestQtsBilling/ValueObjects/CommercialAmountsTests.cs` (T03.01–05); `TestQtsBilling/Integration/BusinessSetupTests.cs` (T08.01–05); `TestQtsBilling/Ui/OnlinePosJourneyTests.cs` (T35.01–05) | Exact requirement in §1 requirement matrix + owning E assertions; release gate(s) in task index. |
| R06 | [Task 05](#task-05), [Task 08](#task-08), [Task 09](#task-09), [Task 10](#task-10), [Task 11](#task-11), [Task 12](#task-12), [Task 13](#task-13), [Task 15](#task-15), [Task 16](#task-16), [Task 17](#task-17), [Task 19](#task-19), [Task 20](#task-20), [Task 37](#task-37), [Task 38](#task-38), [Task 39](#task-39), [Task 42](#task-42), [Task 45](#task-45), [Task 46](#task-46), [Task 48](#task-48), [Task 50](#task-50), [Task 51](#task-51), [Task 52](#task-52), [Task 53](#task-53), [Task 63](#task-63) | `TestQtsBilling/Integration/TenantConstraintTests.cs` (T05.01–04); `TestQtsBilling/Integration/BusinessSetupTests.cs` (T08.01–05); `TestQtsBilling/Integration/ReportIntegrityTests.cs` (T53.01–05) | Exact requirement in §1 requirement matrix + owning E assertions; release gate(s) in task index. |
| R07 | [Task 09](#task-09), [Task 14](#task-14), [Task 15](#task-15), [Task 16](#task-16), [Task 19](#task-19), [Task 33](#task-33), [Task 35](#task-35), [Task 37](#task-37), [Task 38](#task-38), [Task 42](#task-42), [Task 48](#task-48), [Task 49](#task-49), [Task 53](#task-53), [Task 63](#task-63) | `TestQtsBilling/Integration/PostedInvariantTests.cs` (T09.01–05); `TestQtsBilling/Integration/InventoryConcurrencyTests.cs` (T14.01–05); `TestQtsBilling/Integration/ReportIntegrityTests.cs` (T53.01–05) | Exact requirement in §1 requirement matrix + owning E assertions; release gate(s) in task index. |
| R08 | [Task 03](#task-03), [Task 06](#task-06), [Task 09](#task-09), [Task 10](#task-10), [Task 11](#task-11), [Task 12](#task-12), [Task 14](#task-14), [Task 16](#task-16), [Task 17](#task-17), [Task 24](#task-24), [Task 35](#task-35), [Task 37](#task-37), [Task 38](#task-38), [Task 39](#task-39), [Task 42](#task-42), [Task 45](#task-45), [Task 46](#task-46), [Task 48](#task-48), [Task 50](#task-50), [Task 51](#task-51), [Task 57](#task-57) | `TestQtsBilling/ValueObjects/CommercialAmountsTests.cs` (T03.01–05); `TestQtsBilling/Integration/HttpBoundaryTests.cs` (T06.01–05); `TestQtsBilling/Integration/BankReconciliationTests.cs` (T51.01–05) | Exact requirement in §1 requirement matrix + owning E assertions; release gate(s) in task index. |
| R09 | [Task 17](#task-17), [Task 23](#task-23), [Task 25](#task-25), [Task 28](#task-28), [Task 29](#task-29), [Task 36](#task-36), [Task 58](#task-58), [Task 59](#task-59), [Task 62](#task-62), [Task 63](#task-63) | `TestQtsBilling/Integration/DrawerCustodyTests.cs` (T17.01–05); `TestQtsBilling/Integration/DeviceGrantTests.cs` (T23.01–05); `TestQtsBilling/Ui/SupportedHostTests.cs` (T62.01–05) | Exact requirement in §1 requirement matrix + owning E assertions; release gate(s) in task index. |
| R10 | [Task 22](#task-22), [Task 23](#task-23), [Task 24](#task-24), [Task 25](#task-25), [Task 26](#task-26), [Task 27](#task-27), [Task 28](#task-28), [Task 29](#task-29) | `TestQtsBilling/Integration/NativeAuthorizationTests.cs` (T22.01–05); `TestQtsBilling/Integration/DeviceGrantTests.cs` (T23.01–05); `QtsBilling.Persistence.Sqlite.Tests/OfflineCashJourneyTests.cs` (T29.01–05) | Exact requirement in §1 requirement matrix + owning E assertions; release gate(s) in task index. |
| R11 | [Task 08](#task-08), [Task 30](#task-30), [Task 31](#task-31), [Task 32](#task-32), [Task 33](#task-33), [Task 34](#task-34), [Task 35](#task-35), [Task 36](#task-36), [Task 40](#task-40), [Task 41](#task-41), [Task 43](#task-43), [Task 44](#task-44), [Task 47](#task-47), [Task 49](#task-49), [Task 54](#task-54), [Task 56](#task-56), [Task 57](#task-57), [Task 62](#task-62) | `TestQtsBilling/Integration/BusinessSetupTests.cs` (T08.01–05); `TestQtsBilling/Ui/SharedShellTests.cs` (T30.01–05); `TestQtsBilling/Ui/SupportedHostTests.cs` (T62.01–05) | Exact requirement in §1 requirement matrix + owning E assertions; release gate(s) in task index. |
| R12 | [Task 13](#task-13), [Task 31](#task-31), [Task 37](#task-37), [Task 38](#task-38), [Task 39](#task-39), [Task 40](#task-40), [Task 41](#task-41), [Task 42](#task-42), [Task 43](#task-43), [Task 55](#task-55), [Task 63](#task-63) | `TestQtsBilling/Integration/PaymentTruthTests.cs` (T13.01–05); `TestQtsBilling/Integration/PartyMasterTests.cs` (T31.01–05); `TestQtsBilling/Integration/DocumentsAndDeliveryTests.cs` (T55.01–05) | Exact requirement in §1 requirement matrix + owning E assertions; release gate(s) in task index. |
| R13 | [Task 16](#task-16), [Task 31](#task-31), [Task 44](#task-44), [Task 45](#task-45), [Task 46](#task-46), [Task 47](#task-47), [Task 55](#task-55), [Task 63](#task-63) | `TestQtsBilling/Integration/PurchasingReceiptPostingTests.cs` (T16.01–05); `TestQtsBilling/Integration/PartyMasterTests.cs` (T31.01–05); `TestQtsBilling/Integration/DocumentsAndDeliveryTests.cs` (T55.01–05) | Exact requirement in §1 requirement matrix + owning E assertions; release gate(s) in task index. |
| R14 | [Task 10](#task-10), [Task 13](#task-13), [Task 36](#task-36), [Task 46](#task-46), [Task 48](#task-48), [Task 49](#task-49), [Task 50](#task-50), [Task 51](#task-51), [Task 52](#task-52) | `TestQtsBilling/Integration/PeriodPostingTests.cs` (T10.01–05); `TestQtsBilling/Integration/PaymentTruthTests.cs` (T13.01–05); `TestQtsBilling/Ui/AccountingJourneyTests.cs` (T52.01–05) | Exact requirement in §1 requirement matrix + owning E assertions; release gate(s) in task index. |
| R15 | [Task 15](#task-15), [Task 39](#task-39), [Task 45](#task-45), [Task 51](#task-51), [Task 53](#task-53), [Task 54](#task-54), [Task 59](#task-59), [Task 63](#task-63) | `TestQtsBilling/Integration/InventoryValuationTests.cs` (T15.01–05); `TestQtsBilling/Integration/ReceivableTests.cs` (T39.01–05); `TestQtsBilling/Integration/OperationalTelemetryTests.cs` (T59.01–05) | Exact requirement in §1 requirement matrix + owning E assertions; release gate(s) in task index. |
| R16 | [Task 02](#task-02), [Task 18](#task-18), [Task 19](#task-19), [Task 20](#task-20), [Task 21](#task-21), [Task 58](#task-58), [Task 64](#task-64) | `TestQtsBilling/Architecture/NativeBoundaryTests.cs` (T02.01–04); `TestQtsBilling/DataImport/NativePackageTests.cs` (T18.01–05); `TestQtsBilling/Operations/RecoveryControlsTests.cs` (T58.01–05) | Exact requirement in §1 requirement matrix + owning E assertions; release gate(s) in task index. |
| R17 | [Task 21](#task-21), [Task 30](#task-30), [Task 34](#task-34), [Task 35](#task-35), [Task 36](#task-36), [Task 40](#task-40), [Task 43](#task-43), [Task 47](#task-47), [Task 49](#task-49), [Task 52](#task-52), [Task 54](#task-54), [Task 55](#task-55), [Task 56](#task-56), [Task 57](#task-57), [Task 62](#task-62) | `TestQtsBilling/Operations/ProductionAdmissionTests.cs` (T21.01–05); `TestQtsBilling/Ui/SharedShellTests.cs` (T30.01–05); `TestQtsBilling/Ui/SupportedHostTests.cs` (T62.01–05) | Exact requirement in §1 requirement matrix + owning E assertions; release gate(s) in task index. |
| R18 | [Task 01](#task-01), [Task 07](#task-07), [Task 20](#task-20), [Task 21](#task-21), [Task 55](#task-55), [Task 57](#task-57), [Task 58](#task-58), [Task 59](#task-59), [Task 60](#task-60), [Task 61](#task-61), [Task 62](#task-62), [Task 63](#task-63), [Task 64](#task-64) | `TestQtsBilling/Integration/AccountEnrolmentTests.cs` (T07.01–05); `TestQtsBilling/DataImport/ReconciliationTests.cs` (T20.01–05); `TestQtsBilling/Ui/SupportedHostTests.cs` (T62.01–05) | Exact requirement in §1 requirement matrix + owning E assertions; release gate(s) in task index. |

### 6.2 Verification ledger — observed versus not executed

| Check | Evidence/status |
|---|---|
| Remote branch at final verification | **VERIFIED:** GitHub `master` still points to `5910493a67b6d5342d83bf6509c1c3e484a3e908`. This does not verify the user's local clean working tree. |
| Current source/architecture | **VERIFIED static review:** solution, project/package metadata, core financial/auth/sync/device/import paths, current workflow manifests and linked evidence windows. This is not exhaustive runtime or penetration testing. |
| Root repository instruction file | Root `AGENTS.md` returned 404. Recheck policy at implementation SHA; absence is not permission to disregard owner safety requirements. |
| Actual remote CI run | **VERIFIED observed:** [CI run 39311366902](https://github.com/nirzaf/QtsBilling/actions/runs/39311366902), same SHA, successful Windows desktop build and Linux server/domain/SQLite jobs. No new test was started here. |
| “78 passing tests” claim | **UNVERIFIED as a complete independent result:** supplied by the user/agent. The checked CI manifest does not run the source importer tests or PostgreSQL/API financial scenarios. Do not carry the count forward as a production quality score. |
| “18 cutover gates, 0 failed” claim | **UNVERIFIED and insufficient:** supplied result. Static gate/script defects F01–F05 undermine the assurance; future fail-closed tests are required. |
| Branch protection | Branch metadata returns `protected:false`, empty required status contexts. Repository-wide rulesets/protected-environment administration were not independently established; verify and configure through approved administration before release. |
| Local builds/unit/integration/browser/MAUI runtime tests | **NOT RUN - SPEC_ONLY.** No dotnet executable in the authoring runtime; no application or database-changing commands were executed. |
| New native import/signature/cutover controls | **NOT RUN - SPEC_ONLY; NEW design.** Nothing in the specification has been implemented. |
| Real production data, backing files and financial balances | **UNVERIFIED / BLOCKED_EXTERNAL.** No actual target/source/native package or cash records were inspected. |
| Backup/PITR/restore/signing/pilot | **UNVERIFIED / BLOCKED_EXTERNAL.** A script or prior demo text is not a current restore/signing/pilot result. |
| Artifact quality checks | Performed only on this generated Markdown: UTF-8/nonempty, six sections, 64 contiguous A–G tasks, dependency order, requirement/finding references and internal anchors. These are document checks, not software tests. |

### 6.3 Commands and prerequisites

All following application commands are **NOT RUN - SPEC_ONLY**. Run only in a later authorized implementation/test workspace, against fixture-owned databases where relevant. Existing supported command shapes were checked against project files and `.github/workflows/ci.yml`; new class filters name tests this specification creates.

| Use | Exact command or binding | Pass condition |
|---|---|---|
| Server/browser/library build | `dotnet build QtsWebApp/QtsWebApp/QtsWebApp.csproj -c Release` | Build succeeds without forbidden browser persistence edges; no production connection or automatic migration is used. |
| Focused existing pure tests | `dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~TestQtsBilling.ValueObjects"` | Applicable pure value-object tests pass; select the separate Contracts filter when contracts changed. No database is required for those existing cases. |
| Focused new financial test example | `dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~TestQtsBilling.Integration.PosPostingTests"` | Disposable PostgreSQL/HTTP fixture starts safely and all owning assertions pass; prerequisite failure is not skipped success. |
| Focused local-store test example | `dotnet test QtsBilling.Persistence.Sqlite.Tests/QtsBilling.Persistence.Sqlite.Tests.csproj -c Release --filter "FullyQualifiedName~TerminalReceiptAndCashTests"` | Real temporary SQLite file/restart tests pass with exact custody totals. |
| Existing shared UI smoke executable | `dotnet run --project QtsBilling.Ui.Smoke/QtsBilling.Ui.Smoke.csproj -c Release` | Render/action/dependency assertions pass. This project is an executable, not an xUnit suite. |
| Windows host build | `dotnet build QtsBilling.Maui/QtsBilling.Maui.csproj -c Release` | Run on approved Windows SDK/workload environment; then perform native runtime/install checks separately. |
| Native onboarding tool after Task 18 | `dotnet build QtsBilling.DataImport/QtsBilling.DataImport.csproj -c Release` | New native tool compiles without old-provider/source-format dependencies. No apply/import command is implicit in build. |
| Secret scan | `pwsh -NoProfile -File scripts/secret-scan.ps1` | No unapproved finding; documented public fixture allowlist remains narrow. |
| Diff hygiene | `git diff --check` | No whitespace/conflict artifacts. Does not prove correctness or authorize a commit. |
| Full regression | The implemented `.github/workflows/ci.yml` required jobs | All applicable domain/native-import/PG/API/SQLite/browser/Windows/security checks at the candidate SHA pass; focused local development remains the default. |

All NEW test namespaces follow their exact directory: `TestQtsBilling.Integration`, `.Contracts`, `.DataImport`, `.Device`, `.Ui`, `.Operations`, `.Architecture`, `.Acceptance`; the separate local-store project uses `QtsBilling.Persistence.Sqlite.Tests`. Do not add assertions merely to preserve a numerical test count. Reuse `PostgresApiFixture` from Task 01 and the existing real-file `LocalStoreHarness`; choose one meaningful race/rollback fixture per changed invariant, parameterized for closely related cases.

**Migration generation in later development:** restore the repository's checked EF tool manifest; use `dotnet ef migrations add <logical-name> --project QtsBilling.Persistence.Postgres/QtsBilling.Persistence.Postgres.csproj` or the SQLite equivalent with its checked design-time factory. `<logical-name>` is the exact named new migration in the owning task; generated timestamp/designer/snapshot filenames are recorded after generation. Do not run a production `database update` as a task verification step. Apply migrations only inside disposable fixture databases for tests, then through separately authorized reviewed deployment SQL/bundles.

**Fresh contexts and tests.** Runtime financial tests must assert the committed database state from a fresh context; a tracked object does not prove commit. Unknown-outcome tests deliberately interrupt acknowledgement, then resolve the same receipt. Database invariants are tested using the restricted application role as well as HTTP. Financial tests cannot be replaced by EF InMemory, a mocked `SaveChanges` call or a “request returned 200” assertion.

### 6.4 Release and rollback sequence

| Stage | Required evidence | Stop/recovery rule |
|---|---|---|
| 1 — Baseline and isolate | Pinned source/known task scope, safe fixture, current native data/cash inventory | Block unsafe cutover entry; do not mutate any actual deployment. |
| 2 — Add schema and runtime controls | Reviewed additive migrations, static/fixture data validation, repaired P0 tests | Do not edit applied migration history, drop constraints or repair records silently. |
| 3 — Native identity and offline transition | Approved issuer/keys, device binding, lossless queue upgrade and scoped native contracts | Drain or explicitly reconcile current pending payloads before version cutoff. No backward provider adapter and no cash erasure. |
| 4 — Functional candidate | G1–G6 journey evidence and complete UI for enabled profiles | Incomplete product profiles remain disabled/not advertised. API existence alone is insufficient. |
| 5 — Production candidate build | Same-SHA CI, signed/digested artifacts, SBOM/versions, protected approval | A tag alone cannot publish or deploy an unverified candidate. |
| 6 — Staging/import/recovery rehearsal | Native package/report verification, exact financial tie-outs, actual backup/keys/files/PITR drill | Every failed/missing/stale check aborts. Non-waivable financial discrepancies stop admission. |
| 7 — Named pilot | Actual business/devices/actors/window, finance/security acceptance and operational support | Pilot observations are recorded; unresolved severe defect means stop/fix-forward, not “accept all exceptions.” |
| 8 — Owner-authorized production | Exact environment/build/schema/evidence manifest plus designated confirmation codeword | Separate authorization required. No action in this document performs or implies this approval. |

Before any authorized production mutation, capture a verified backup and native financial/custody checkpoint. A binary/configuration rollback is acceptable only before incompatible writes and only if schema/device data remain compatible. After new financial writes, stop accepting new commands, preserve queues/artifacts/logs, reconcile the uncertain operations and use fix-forward. Actual restore/data correction has its own approved change window and exact post-restore reconciliation; do not downgrade into old payload/schema interpretation or a retired datastore.

### 6.5 Material blockers and next action

| ID | Type / blocked gate | Impact and required next action |
|---|---|---|
| B01 | BLOCKED_EXTERNAL — G4/G7 | Real production topology, target identifiers, active users/devices and current data/cash are unknown. Operator supplies a read-only native inventory and identifies the exact proposed environment. |
| B02 | BLOCKED_DECISION — G1/G6/G7 | Existing stock valuation/opening balances and any prior incorrect posting are unverified. Finance approves native moving-average opening values and source-linked corrections; no silent conversion from a different accounting method. |
| B03 | BLOCKED_DECISION — G1/G6/G7 | Actual currency settlement scale, tax/recoverability, invoice series/calendar and legal record retention require finance/jurisdiction review. Configure exact supported rules; do not infer them from the user's location. |
| B04 | BLOCKED_EXTERNAL — G2/G3/G7 | Trusted issuer URL/keys, MFA/bootstrap custody, SMTP transport and native callback/signing configuration are not provided/validated. Establish protected key and delivery ownership, then execute real enrolment/recovery. |
| B05 | BLOCKED_DECISION — G3/G7 | Business must accept provisional offline stock and bounded revocation lag, proposed eight-hour grant and cash-recovery/closing process. Unsupported offline actions remain disabled. |
| B06 | BLOCKED_EXTERNAL — G7 | Real encrypted device/storage protection, file/key backups and PITR mechanism are unverified. Demonstrate restore against approved RPO/RTO and retained pending cash; script presence is not proof. |
| B07 | BLOCKED_EXTERNAL — G7 | Required CI/ruleset/environment protection and trusted Windows signing authority need approved repository/operator configuration. Do not infer protection from a green workflow file. |
| B08 | BLOCKED_EXTERNAL — G7 | Pilot business, device list, cashiers/accountant, dates, acceptance thresholds/support and owner sign-off are not supplied. Complete Task 63 with actual named participants. |
| B09 | BLOCKED_EXTERNAL — G4/G7 | Any existing native input exceptions require exact reviewed exception IDs and bound report approval. Financial imbalance, unowned rows, duplicated effects and missing cash are non-waivable. |
| B10 | BLOCKED_EXTERNAL — production execution only | The designated QtsBilling irreversible-action confirmation codeword has not been provided in this review. Do not invent it or reuse another project's codeword. Obtain it only when the actual protected action is ready. |
| B11 | BLOCKED_EXTERNAL — optional history purge | “All traces” is fully scoped to current supported tree/new artifacts here. Git history, historical releases/backups and external forks need separate owner-authorized administration; no history rewrite is performed or assumed. |
| B12 | BLOCKED_CONTEXT — conditional per future task | If the implementation SHA differs, required code disappears or existing data violates assumed constraints, stop only the affected task, cite the changed evidence and re-plan it. This document is grounded in the reviewed SHA, not a guarantee about future branches. |

These blockers do not require speculative frameworks or prevent implementation against safe native fixtures. They prevent an unsupported claim that the real production deployment is admitted. No unanswered question is silently treated as approval.

### 6.6 Minimal new dependencies and support boundaries

Keep current .NET 10/C# 14, EF Core 10/PostgreSQL 18/Npgsql, SQLite, MudBlazor and Windows MAUI. No Angular/React/Node backend, microservices, Redis, message broker, extra source database or generic data-access rewrite is proposed.

| Dependency | Status and bounded purpose |
|---|---|
| Microsoft.AspNetCore.Mvc.Testing 10.0.13 | NEW test-only dependency aligned to the checked host patch, for real HTTP test-host behavior. |
| OpenIddict 7.7.2 applicable server/client/EF/ASP.NET Core/OS packages | NEW, required to complete the requested native PKCE identity flow. Official stable release checked 10 October 2026; select only needed package modules and no Quartz integration unless genuinely required. |
| MailKit 4.18.1 | NEW server-only SMTP transport for the missing reset/invitation/document delivery path. No mailbox/IMAP feature is implied. |
| Microsoft.Playwright 1.63.0 | NEW test-only browser journey verification, with matching pinned browser runtime. Native Windows lifecycle still needs actual platform checks. |
| Other packages | Reuse existing dependencies. Future security patch updates need current authoritative verification and targeted compatibility tests; repository version is not a statement that it is the latest secure patch. |

This release covers ordinary retail, wholesale/distribution and service billing/accounting within the native base-currency model. Payroll/HR, manufacturing/MRP, statutory e-invoicing/tax filing, multi-currency consolidation, regulated/pharmacy traceability, advanced lot/serial/expiry workflows, restaurant kitchen orchestration, ecommerce/customer portals, marketing/loyalty, and additional operating systems are not falsely declared complete. Add them only as separately scoped, evidenced requests. Refuse selection of unsupported product/payment/accounting modes rather than letting a schema enum advertise a nonexistent workflow.

### 6.7 Source evidence register

Source windows below refer to actual **repository source lines**, not the connector's JSON wrapper line 2. A whole-file entry means that file was read and the named symbol/body inspected; no narrow line number is fabricated. All links are pinned to the reviewed commit. Folder/project presence was checked through the source tree; **presence-only evidence never proves implementation or runtime success**. For an affected task, read its referenced body again before editing and update the exact code line anchors in its implementation PR.

<a id="e-e01"></a>
#### E01 — QtsBilling.sln
[Source lines 1-34](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.sln#L1-L34). **Anchor:** `Solution project graph`. **VERIFIED observation:** Thirteen project entries, including the web, shared UI, Windows host, both persistence providers and the snapshot-import projects; former desktop/service projects are absent from this solution.

<a id="e-e02"></a>
#### E02 — QtsBilling.Persistence.Postgres/QtsBilling.Persistence.Postgres.csproj
[Source lines 1-22](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Persistence.Postgres/QtsBilling.Persistence.Postgres.csproj#L1-L22). **Anchor:** `PackageReference / TargetFramework`. **VERIFIED observation:** net10.0, C# 14, EF/Identity 10.0.13 and Npgsql EF provider 10.0.3.

<a id="e-e03"></a>
#### E03 — QtsWebApp/QtsWebApp/Program.cs
[Whole file read; symbol/body checked](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Program.cs). **Anchor:** `Identity registration, cookies, API registration, WebLocalPosService`. **VERIFIED observation:** Cookie-authenticated host, optional Data Protection path and registered business APIs; no OIDC server/token validation registration was found in the inspected host.

<a id="e-e04"></a>
#### E04 — QtsWebApp/QtsWebApp/Infrastructure/QtsIdentity.cs
[Whole file read; symbol/body checked](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Infrastructure/QtsIdentity.cs). **Anchor:** `PermissionAuthorizationHandler.HandleRequirementAsync; QtsClaimsPrincipalFactory`. **VERIFIED observation:** Policies consume sign-in claims; the handler does not reload current active membership, business or location grants. Claims choose an active membership rather than an explicit business-selection workflow.

<a id="e-e05"></a>
#### E05 — QtsWebApp/QtsWebApp/Features/Auth/AuthEndpoints.cs
[Whole file read; symbol/body checked](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Auth/AuthEndpoints.cs). **Anchor:** `LoginAsync; RequestPasswordResetAsync; CompletePasswordResetAsync`. **VERIFIED observation:** Production reset requests generate a token and return acceptance without a delivery implementation in this handler; account-setup status is checked before password authentication.

<a id="e-e06"></a>
#### E06 — QtsBilling.Persistence.Postgres/BillingDbContext.Model.cs
[Whole file read; symbol/body checked](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Persistence.Postgres/BillingDbContext.Model.cs). **Anchor:** `ConfigureCore; ConfigureMasterData; ConfigureCatalog; ConfigureInventory`. **VERIFIED observation:** Single-column master/device foreign keys coexist with correct composite stock foreign keys. Stock has an on_hand >= 0 check.

<a id="e-e07"></a>
#### E07 — QtsBilling.Persistence.Postgres/BillingDbContext.Model.Financial.cs
[Source lines 210-445](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Persistence.Postgres/BillingDbContext.Model.Financial.cs#L210-L445). **Anchor:** `ConfigureAccounting; ConfigurePurchasing`. **VERIFIED observation:** Account parents, invoice customer/location/source, period/reversal and several expense references use single-column FKs. Journal lines have several stronger composite FKs.

<a id="e-e08"></a>
#### E08 — QtsBilling.Persistence.Postgres/BillingDbContext.cs
[Whole file read; symbol/body checked](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Persistence.Postgres/BillingDbContext.cs). **Anchor:** `SaveChanges guards and AppendOnlyTypes`. **VERIFIED observation:** Context-level immutability and balance checks exist; they are not equivalent to direct-SQL protection of all posted headers and state transitions.

<a id="e-e09"></a>
#### E09 — QtsBilling.Persistence.Postgres/Migrations/20261019082426_AddIntegrityGuards.cs
[Whole file read; symbol/body checked](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Persistence.Postgres/Migrations/20261019082426_AddIntegrityGuards.cs). **Anchor:** `qts_invoice_lines_draft_guard; qts_inventory_balance_guard; deferred balance triggers`. **VERIFIED observation:** Invoice-line trigger covers UPDATE/DELETE, not INSERT, and checks only the selected parent; inventory deferred trigger compares captured NEW against final movement sum. Applied migration must remain unchanged.

<a id="e-e10"></a>
#### E10 — QtsWebApp/QtsWebApp/Features/Accounting/LedgerWriter.cs
[Whole file read; symbol/body checked](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Accounting/LedgerWriter.cs). **Anchor:** `PostWithinTransactionAsync; AllocateNumberAsync; LocalDate`. **VERIFIED observation:** Missing period is accepted; matching period is read without a closing lock. Source precheck and row-locked number allocation exist. Invalid time zone silently falls back to UTC.

<a id="e-e11"></a>
#### E11 — QtsWebApp/QtsWebApp/Features/Sales/SalePosting.cs
[Source lines 1-310](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Sales/SalePosting.cs#L1-L310). **Anchor:** `ValidatePayload; ResolveContextAsync; PriceLines; ValidatePayment`. **VERIFIED observation:** Tax-inclusive line totals, catalog-price validation, arbitrary line discounts and catalog purchase-price costing; payment validation includes customer-credit and reference-only external methods.

<a id="e-e12"></a>
#### E12 — QtsWebApp/QtsWebApp/Features/Sales/SalePosting.cs
[Source lines 310-665](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Sales/SalePosting.cs#L310-L665). **Anchor:** `PostAsync / posting transaction and revenue journal`. **VERIFIED observation:** Posting credits subtotal minus discount as revenue and credits tax separately; this conflicts with inclusive line totals. Payment is recorded confirmed, including credit sales. Sales/stock/journal/audit are otherwise grouped transactionally.

<a id="e-e13"></a>
#### E13 — QtsWebApp/QtsWebApp/Features/Inventory/InventoryLedger.cs
[Whole file read; symbol/body checked](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Inventory/InventoryLedger.cs). **Anchor:** `ApplyAsync`. **VERIFIED observation:** FOR UPDATE on an existing balance; a missing balance is added as a tracked entity rather than a concurrency-safe database upsert. Writes a stock projection as part of the calling transaction.

<a id="e-e14"></a>
#### E14 — QtsWebApp/QtsWebApp/Features/Sync/SyncTransaction.cs
[Whole file read; symbol/body checked](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Sync/SyncTransaction.cs). **Anchor:** `RunAsync`. **VERIFIED observation:** Execution-strategy transaction wrapper does not itself discard/reload tracked state between retries.

<a id="e-e15"></a>
#### E15 — QtsWebApp/QtsWebApp/Features/Sync/SyncOperationEndpoints.cs
[Source lines 1-460](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Sync/SyncOperationEndpoints.cs#L1-L460). **Anchor:** `ProcessAsync / operation and batch handlers`. **VERIFIED observation:** Device/body scope and replay handling; several early business rejections return receipts without storing them. Concurrent uniqueness recovery does not consistently compare the winning fingerprint.

<a id="e-e16"></a>
#### E16 — QtsWebApp/QtsWebApp/Features/Sync/SyncOperationEndpoints.cs
[Source lines 460-633](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Sync/SyncOperationEndpoints.cs#L460-L633). **Anchor:** `TryReadSale; NeedsReviewAsync; rejected receipt handling`. **VERIFIED observation:** Payload parser and rejection helper do not establish the same complete validation and durable outcome path as online posting.

<a id="e-e17"></a>
#### E17 — QtsWebApp/QtsWebApp/Features/Sync/SyncChangeEndpoints.cs
[Whole file read; symbol/body checked](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Sync/SyncChangeEndpoints.cs). **Anchor:** `Change-feed endpoint`. **VERIFIED observation:** Business-scoped feed and permission filtering exist; device/location-bound baseline, retention floor and a complete resync protocol are not established by this endpoint.

<a id="e-e18"></a>
#### E18 — QtsBilling.Maui/Services/DeviceSyncCoordinator.cs
[Whole file read; symbol/body checked](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Maui/Services/DeviceSyncCoordinator.cs). **Anchor:** `RunAsync; push/pull; BuildEnvelope`. **VERIFIED observation:** Cursor update is separate from cache application; coordinator does not use the full durable sending/backoff protocol and can synthesize terminal failures from broad HTTP categories.

<a id="e-e19"></a>
#### E19 — QtsBilling.Persistence.Sqlite/SqliteLocalStore.Sync.cs
[Whole file read; symbol/body checked](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Persistence.Sqlite/SqliteLocalStore.Sync.cs). **Anchor:** `ApplyChangesAsync; ApplyProjectionAsync; ApplyTombstoneAsync; ResolveConflictAsync`. **VERIFIED observation:** Projection mutates tracked rows before conflict detection; only customer tombstones are applied; stock payload location is not used to validate destination; resolution only records a label/time.

<a id="e-e20"></a>
#### E20 — QtsBilling.Persistence.Sqlite/SqliteLocalStore.cs
[Source lines 1-290](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Persistence.Sqlite/SqliteLocalStore.cs#L1-L290). **Anchor:** `InitializeAsync; CaptureProvisionalCashSaleAsync`. **VERIFIED observation:** Real write gate, checked integer capture, schema/integrity guards and pre-upgrade snapshots already exist. Preserve them.

<a id="e-e21"></a>
#### E21 — QtsBilling.Persistence.Sqlite/SqliteLocalStore.cs
[Source lines 340-650](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Persistence.Sqlite/SqliteLocalStore.cs#L340-L650). **Anchor:** `GetDueOutboxAsync; MarkOutboxSendingAsync; ApplyReceiptAsync; GetPendingSummaryAsync`. **VERIFIED observation:** TransientRetry is stored in the single terminal inbox slot; later different receipts conflict. Queue queries are not partitioned here. Pending cash totals exclude rejected cash.

<a id="e-e22"></a>
#### E22 — QtsBilling.Persistence.Sqlite/SqliteLocalStore.cs
[Source lines 780-1040](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Persistence.Sqlite/SqliteLocalStore.cs#L780-L1040). **Anchor:** `SaveDeviceProfileAsync; MapOutcome; ReceiptMatches; cursor helpers`. **VERIFIED observation:** Device context can change without a pending-money guard; transient receipt maps to Pending but remains in inbox; receipt equality compares result/number/revision.

<a id="e-e23"></a>
#### E23 — QtsBilling.Maui/Services/DeviceLocalPosService.cs
[Whole file read; symbol/body checked](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Maui/Services/DeviceLocalPosService.cs). **Anchor:** `CaptureCashSaleAsync`. **VERIFIED observation:** Cached session permissions and local location/register settings authorize offline capture; no server-signed expiring offline grant is validated here.

<a id="e-e24"></a>
#### E24 — QtsBilling.Maui/Services/DeviceAccountApiClient.cs
[Whole file read; symbol/body checked](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Maui/Services/DeviceAccountApiClient.cs). **Anchor:** `GetSessionAsync`. **VERIFIED observation:** Returns cached session on transport failure and on ApiException; no bounded offline authorization check is implemented here.

<a id="e-e25"></a>
#### E25 — QtsBilling.Maui/Services/SessionStore.cs
[Whole file read; symbol/body checked](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Maui/Services/SessionStore.cs). **Anchor:** `SaveSessionAsync; GetSessionAsync`. **VERIFIED observation:** SecureStorage contains cookies/session JSON; no expiry enforcement on the cached session read.

<a id="e-e26"></a>
#### E26 — QtsBilling.Ui/Pages/Pos.razor.cs
[Whole file read; symbol/body checked](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Ui/Pages/Pos.razor.cs). **Anchor:** `LineTax; LineTotal; CaptureAsync`. **VERIFIED observation:** Always adds tax, uses unrounded products, and consumes only local POS feature service. Operation identity is retained until acceptance; keep that useful foundation.

<a id="e-e27"></a>
#### E27 — QtsWebApp/QtsWebApp/Features/Pos/WebLocalPosService.cs
[Whole file read; symbol/body checked](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Pos/WebLocalPosService.cs). **Anchor:** `ILocalPosService implementation`. **VERIFIED observation:** Browser catalog returns empty and capture returns NotSupported. This is a truthful offline stub, not a working online checkout.

<a id="e-e28"></a>
#### E28 — QtsWebApp/QtsWebApp/Features/Purchasing/PurchasingEndpoints.cs
[Source lines 1-120](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Purchasing/PurchasingEndpoints.cs#L1-L120). **Anchor:** `MapPurchasingEndpoints`. **VERIFIED observation:** Supplier CRUD and purchase-order/receipt/return routes exist; do not recreate these completed surfaces.

<a id="e-e29"></a>
#### E29 — QtsWebApp/QtsWebApp/Features/Purchasing/PurchasingEndpoints.cs
[Source lines 640-1030](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Purchasing/PurchasingEndpoints.cs#L640-L1030). **Anchor:** `ConfirmOrderAsync; CancelOrderAsync; PostReceiptAsync`. **VERIFIED observation:** Purchase orders do not post stock; receipt/return key hashes cover body rather than route/kind, and received quantity is checked under order lock.

<a id="e-e30"></a>
#### E30 — QtsWebApp/QtsWebApp/Features/Purchasing/PurchasingEndpoints.cs
[Source lines 1020-1170](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Purchasing/PurchasingEndpoints.cs#L1020-L1170). **Anchor:** `PostReceiptAsync commit path`. **VERIFIED observation:** Goods receipt/return writes document, inventory, received quantity and audit, but no corresponding inventory/clearing journal in this transaction; receipt listing performs per-document reads.

<a id="e-e31"></a>
#### E31 — QtsWebApp/QtsWebApp/Features/Invoices/InvoiceEndpoints.cs
[Source lines 1-110](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Invoices/InvoiceEndpoints.cs#L1-L110). **Anchor:** `MapInvoiceEndpoints`. **VERIFIED observation:** Draft, issue, void, payments, full credit-note and HTML print routes exist.

<a id="e-e32"></a>
#### E32 — QtsWebApp/QtsWebApp/Features/Invoices/InvoiceEndpoints.cs
[Source lines 360-650](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Invoices/InvoiceEndpoints.cs#L360-L650). **Anchor:** `DeleteAsync; IssueAsync`. **VERIFIED observation:** Draft delete check happens before transactional bulk delete; issue posts a new receivable/revenue journal, with no stock/fulfilment integration in the inspected issue path.

<a id="e-e33"></a>
#### E33 — QtsWebApp/QtsWebApp/Features/Sales/CashRegisterEndpoints.cs
[Source lines 409-550](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Sales/CashRegisterEndpoints.cs#L409-L550). **Anchor:** `AddMovementAsync`. **VERIFIED observation:** Manual paid-in/out has neither request idempotency receipt nor counter-account journal; it commits drawer movement and audit only.

<a id="e-e34"></a>
#### E34 — QtsWebApp/QtsWebApp/Features/Reporting/ReportEndpoints.cs
[Source lines 1-240](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Reporting/ReportEndpoints.cs#L1-L240). **Anchor:** `BalanceSheetAsync; ProfitAndLossAsync`. **VERIFIED observation:** Real derived reports exist; dates default to UTC and account metadata is separately loaded.

<a id="e-e35"></a>
#### E35 — QtsWebApp/QtsWebApp/Features/Reporting/ReportEndpoints.cs
[Source lines 240-550](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsWebApp/QtsWebApp/Features/Reporting/ReportEndpoints.cs#L240-L550). **Anchor:** `StockSummaryAsync; SalesSummaryAsync`. **VERIFIED observation:** Stock quantities use 4-decimal money rounding despite 6-decimal stock; sales reports filter current Posted status, so later refunds can alter historical sales. Queries do not share a report snapshot.

<a id="e-e36"></a>
#### E36 — QtsBilling.Migration/SnapshotSource.cs
[Whole file read; symbol/body checked](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Migration/SnapshotSource.cs). **Anchor:** `VerifyAsync; ReadDocumentsAsync; InventoryAsync`. **VERIFIED observation:** Manifest paths are used directly; no root confinement, recursive credential rejection or authenticated provenance. Documents are reopened after hashing and all loaded in memory.

<a id="e-e37"></a>
#### E37 — QtsBilling.Migration/Program.cs
[Whole file read; symbol/body checked](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Migration/Program.cs). **Anchor:** `ImportAsync; ReconcileAsync; TransformAsync`. **VERIFIED observation:** Import does not verify transformed files; missing files are skipped; reconciliation does not enforce count/totals equality; signed timestamp/body differs from separately emitted report timestamp.

<a id="e-e38"></a>
#### E38 — QtsBilling.Migration/PostgresImporter.cs
[Whole file read; symbol/body checked](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Migration/PostgresImporter.cs). **Anchor:** `ImportAsync; UpsertAsync; WriteIdentifierMapAsync`. **VERIFIED observation:** Raw per-row upsert and separate mapping write; no aggregate transaction; dynamic identifiers, cross-mapping reference traversal, version/timestamp reset and insufficient map-conflict verification.

<a id="e-e39"></a>
#### E39 — QtsBilling.Migration/ReconciliationReport.cs
[Whole file read; symbol/body checked](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Migration/ReconciliationReport.cs). **Anchor:** `BuildBody; Sign; Compare`. **VERIFIED observation:** HMAC/hash construction exists, but no verification routine here; Compare is not used for monetary totals by CLI reconciliation.

<a id="e-e40"></a>
#### E40 — scripts/qb30-cutover.ps1
[Whole file read; symbol/body checked](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/scripts/qb30-cutover.ps1). **Anchor:** `Add-Gate and whole cutover flow`. **VERIFIED observation:** Caller supplies both codewords; ErrorActionPreference=Continue; failed gates do not immediately stop. Hash/HMAC format is mistaken for verification; skipped imports can pass; environment targets are not bound consistently.

<a id="e-e41"></a>
#### E41 — .github/workflows/ci.yml
[Whole file read; symbol/body checked](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/.github/workflows/ci.yml). **Anchor:** `build-and-test; build-desktop`. **VERIFIED observation:** Web/library build, domain/contracts tests, SQLite tests, Windows MAUI build. No PostgreSQL/API or migration test/secret-scan gate in this manifest.

<a id="e-e42"></a>
#### E42 — .github/workflows/release.yml
[Whole file read; symbol/body checked](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/.github/workflows/release.yml). **Anchor:** `release job`. **VERIFIED observation:** Tag-driven rebuild/publish of server and unpackaged desktop, ZIP SHA256 sums and GitHub release. No dependency on a successful tested-artifact job, production approval or package-signature validation.

<a id="e-e43"></a>
#### E43 — docs/migration/task-status.md
[Source lines 1-85](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/docs/migration/task-status.md#L1-L85). **Anchor:** `QB-06 and QB-23 through QB-30 status claims`. **VERIFIED observation:** Partial master-data work remains alongside broad DONE claims; local historical evidence is not current runtime proof.

<a id="e-e44"></a>
#### E44 — docs/migration/decisions.md
[Source lines 1-155](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/docs/migration/decisions.md#L1-L155). **Anchor:** `D-01...D-41 and historical decisions`. **VERIFIED observation:** Current platform choices; stale assertions about complete isolation/payment confirmation and source-era policy; some text encoding is broken.

<a id="e-e45"></a>
#### E45 — docs/migration/deployment.md
[Source lines 1-150](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/docs/migration/deployment.md#L1-L150). **Anchor:** `Sections 7 and 9`. **VERIFIED observation:** Contradictory statements describe local-store rebuild/wipe despite durable pending cash; logical backup and key-ring operations need stricter production proof.

<a id="e-e46"></a>
#### E46 — QtsBilling.Persistence.Sqlite.Tests/LocalStoreHarness.cs
[Whole file read; symbol/body checked](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Persistence.Sqlite.Tests/LocalStoreHarness.cs). **Anchor:** `LocalStoreHarness; TestData`. **VERIFIED observation:** Reusable real temp-file SQLite fixture, reopen/raw SQL helpers; tests must not be replaced with in-memory provider tests.

<a id="e-e47"></a>
#### E47 — QtsBilling.Ui.Smoke/QtsBilling.Ui.Smoke.csproj
[Source lines 1-20](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/QtsBilling.Ui.Smoke/QtsBilling.Ui.Smoke.csproj#L1-L20). **Anchor:** `OutputType Exe`. **VERIFIED observation:** Existing shared-UI smoke is a console executable, not an xUnit test project.

<a id="e-e48"></a>
#### E48 — README.md
[Whole file read; symbol/body checked](https://github.com/nirzaf/QtsBilling/blob/5910493a67b6d5342d83bf6509c1c3e484a3e908/README.md). **Anchor:** `Architecture / status / project descriptions`. **VERIFIED observation:** Platform documentation is broad and partly stale relative to executable code; source takes precedence.


### 6.8 Official technical references checked for version-sensitive decisions

These references explain platform mechanics; the financial policies, workload numbers and release gates in this specification are explicitly NEW design decisions, not copied claims of certification.

| Reference | What it supports |
|---|---|
| [EF Core connection resiliency](https://learn.microsoft.com/en-us/ef/core/miscellaneous/connection-resiliency) | Execute the whole user transaction through the execution strategy, account for failed commit acknowledgement and rebuild/verify state before retry. |
| [ASP.NET Core antiforgery](https://learn.microsoft.com/en-us/aspnet/core/security/anti-request-forgery?view=aspnetcore-10.0) | Explicit protection for cookie-authenticated state changes; middleware presence alone is not evidence that arbitrary JSON endpoints validate request tokens. |
| [PostgreSQL 18 constraints](https://www.postgresql.org/docs/18/ddl-constraints.html) and [explicit locking](https://www.postgresql.org/docs/18/explicit-locking.html) | Composite references, declarative constraints, row locks and deadlock-aware ordering. Application invariants still need complete transactions/tests. |
| [SQLite online backup](https://www.sqlite.org/backup.html) | Consistent database backup rather than assuming an arbitrary live file copy captures a WAL database safely. |
| [OpenIddict 7.7.2 release](https://github.com/openiddict/openiddict-core/releases/tag/7.7.2) | Stable release metadata published 10 October 2026, checked during this review. |
| [OpenIddict PKCE](https://documentation.openiddict.com/configuration/proof-key-for-code-exchange.html) and [native OS integration](https://documentation.openiddict.com/integrations/operating-systems) | Standards-based code/PKCE policy and native system-browser integration rather than desktop cookie copying. |
| [MAUI Windows CLI publishing](https://learn.microsoft.com/en-us/dotnet/maui/windows/deployment/publish-cli?view=net-maui-10.0) | Windows-specific project publishing, packaging and signing prerequisites; build success alone is not install/upgrade evidence. |
| [MailKit official package](https://www.nuget.org/packages/MailKit/4.18.1) | Reviewed SMTP package baseline, not an endorsement of unrestricted email scope. |
| [Microsoft.Playwright official package](https://www.nuget.org/packages/Microsoft.Playwright/1.63.0) | Reviewed browser automation baseline, used only in verification. |

Access/review date: 19 October 2026. Reverify security/support advisories when implementation occurs; do not silently upgrade the whole stack as part of an unrelated task.

### 6.9 Final acceptance rule

The product is a production **candidate**, not “production ready,” until every applicable critical defect is reproduced then fixed with executed evidence, the enabled SMB journeys are complete through UI, all financial/stock/custody equalities pass, the supported tree/new artifacts are native-only, and the named deployment's independent owner/finance/security/pilot/backup/signing prerequisites are satisfied.

A green build, a historical test count, a complete task-status table, a checksum-shaped string or this specification does not substitute for those conditions. No repository, database, release or production operation was changed by this review.
