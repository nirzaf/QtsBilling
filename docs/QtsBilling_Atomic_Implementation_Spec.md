# QtsBilling — Self-Hosted SMB Completion and Dependency-Reduction Specification

**SPEC_ONLY | One integrated completion programme | Principal .NET architecture**

<a id="summary"></a>
## 1. Summary, product scope and measurable acceptance

### 1.1 Decision

**Retain the existing .NET modular monolith, PostgreSQL authority, Blazor/MudBlazor UI, Windows MAUI client, local OpenIddict identity server and encrypted SQLite outbox. Complete the missing business workflows and remove compulsory public-network services. Do not replace proven libraries with handwritten security or database code.**

The target is a useful, locally operated application for **small retailers, wholesalers/distributors and straightforward service businesses**. A new business must be able to install, initialise, trade, invoice, purchase, collect/pay money, control stock, close its books, back up and recover without a cloud account, internet connection, email/SMS provider, payment gateway, container registry or GitHub availability. Network access to the business's own server is still required for authoritative online operations. A disconnected terminal has a deliberately narrower cash-only provisional mode.

“Complete in a single go” means **one bounded implementation specification, one dependency-ordered task graph and one integrated release candidate**, not a single unreviewed commit, a fabricated claim of perfection, or permission to bypass tests/owner approval. Execute the tasks in order without asking for a new specification at each stage. Preserve completed capabilities; only implement the missing portions named here. Business-specific facts may hold the production gate without stopping safe development against synthetic fixtures.

### 1.2 What “no external dependency” means

| Dependency class | Target rule | What it does not mean |
|---|---|---|
| Mandatory external hosted services | **None for core operation**: no SaaS identity, remote database, public CDN, mandatory SMTP/SMS, bank-feed service, hosted PDF renderer, remote licensing or cloud telemetry | No claim that a real card-network payment can be processed without a payment provider |
| Local infrastructure | PostgreSQL 18, supported Windows/Linux server OS, local storage, trusted local HTTPS, browser/WebView2, OS printing and owner-managed backups remain explicit prerequisites | SQLite does not become a central ledger; hardware, power, local networking and trust cannot be eliminated |
| Software libraries | Retain pinned .NET 10/EF Core/Npgsql, MudBlazor, Identity/OpenIddict and SQLCipher dependencies; redistribute approved binaries with licences | “No SaaS” is not “zero third-party code” or “rewrite cryptography” |
| Installation and recovery | Offline release kit contains approved prerequisites, application binaries, DB tooling, checksums, trusted signer metadata, scripts and local help | No bootstrap download is allowed during the disconnected acceptance run |
| Development/build | Connected dependency acquisition is an optional maintenance step; a sealed build kit supports restore/build/test from approved local packages and tools | Ordinary online CI can remain convenient, but cannot be the only way to verify/build/recover |
| Updates | Explicit operator-imported, verified update package; expiry/patch warnings are local | Do not silently stop financial operations because an internet version check fails; do not claim an unpatched system stays secure forever |
| Human/governance | Real opening balances, finance settings, custody, key ownership and production approval remain necessary | Owner approval is not a vendor dependency and must not be automated away |

**Current-source evidence:** [S01](#evidence-s01), [S02](#evidence-s02), [S10](#evidence-s10), [S12](#evidence-s12), [S14](#evidence-s14), [S15](#evidence-s15), [S18](#evidence-s18), [S21](#evidence-s21).

### 1.3 Supported scope and explicit exclusions

| Included in the single release | Completion boundary |
|---|---|
| Business/branch, users, roles, devices and accounting setup | Full owner-managed lifecycle and safe first-owner setup; no development seeder dependency |
| Customers, suppliers, products/variants, units, categories, brands, tax and discount masters | Searchable, paginated shared screens plus authenticated APIs; exact base-unit quantities |
| Retail POS | Cash sale, suspended in-memory cart, stable retry, posted/provisional receipts, session custody, sales history and permitted partial refunds |
| Wholesale order-to-cash | Quote → accepted quote/order → reservation → partial delivery → invoice → receipt/allocation → return/credit; do not recognise stock/revenue twice |
| Simple service invoicing | Explicit service lines, drafts, issue, terms, payment/credit and print; no stock side effect |
| Procurement and expenses | Supplier → PO → partial receipt/return → supplier bill/credit → payment; expense category/tax/posting/reversal |
| Inventory/accounting | Moving-average valuation already implemented, transfers, blind counts, journal review/reversal, periods/year close, bank CSV reconciliation |
| Reporting | Posted-fact balance sheet, P&L, trial balance, AR/AP aging, tax/stock/sales/cash/Z reports, safe CSV and drill-through |
| Local documents and operations | HTML/OS print, small evidence attachments on owned disk, local audit/diagnostics, offline install/update/backup/restore and recovery |
| Native offline | Windows MAUI cash capture with bounded signed grant, encrypted durable queue, recoverable conflicts and controlled reconciliation |

**Not part of this release:** integrated card acquiring; automatic bank feeds; e-commerce marketplace/shipping integrations; payroll/HR; manufacturing/BOM/MRP; regulated pharmacy/lot/serial traceability; multi-currency FX/consolidation; loyalty/coupon/promotion engines; arbitrary pack-to-base-unit conversions; mobile/macOS/Linux MAUI ports; a document-management platform; statutory e-invoicing certification. Products can use explicit stock variants and exact quantities in their configured base unit; do not advertise unsupported conversion/serial/expiry workflows. These are exclusions, not unfinished work hidden behind “all done”.

### 1.4 Actors and acceptance journeys

Owner administers businesses, scoped memberships, locations, keys/devices and recovery. Manager controls registers, exceptional discounts/refunds and counts. Cashier sells/receipts within assigned locations. Storekeeper responsibilities use the existing configurable permission model, without inventing an unverified current role. Accountant posts, reconciles, allocates and closes periods. Viewer reads permitted reports. One identity can have different authority in different businesses; business-owner authority is never instance-wide administrator authority.

| Requirement | Measurable release acceptance |
|---|---|
| R01 Local autonomy | On clean supported hosts with public egress denied and no cloud accounts, install the offline kit, restart, sign in, trade, print, run reports and back up; capture zero application-initiated public-resource dependencies |
| R02 Trustworthy source/schema | Current EF model matches generated migrations; clean install and prior-native-schema upgrade pass; no applied migration rewritten; status claims link to the candidate SHA and evidence |
| R03 Usable administration | First owner, business/settings, users, location assignment, recovery ticket and device revoke are operable without SQL, SMTP or development mode |
| R04 Authority | Every sensitive command/read/feed validates current user, business, location and device as relevant; hostile cross-tenant IDs, stale roles and revoked devices produce no unauthorised effect |
| R05 Exact financial recognition | Every posted journal balances individually; stock quantity/value tie to movements; service/direct-goods/sale-representation/delivery invoices each have exactly one economic recognition source |
| R06 Safe money/retry | Concurrent identical requests have one business effect; changed payload under one key is a stable conflict; unknown outcome preserves the original key/payload until resolved |
| R07 Daily retail/wholesale/service use | Every included workflow is reachable from a role-appropriate shared UI; no required record ID is obtained from SQL or developer tools |
| R08 Procure/pay/control | Bills, credits, receipts, expenses, transfers/counts and customer/supplier balances reconcile without silent repair; closed periods are not rewritten |
| R09 Offline durability | Capture → kill/restart → reconnect → duplicate push → conflict/refund recovery preserves every unit of cash; provisional cash never appears as posted revenue |
| R10 Feed correctness | Stable scoped baseline plus epoch/floor/cursor; cache+cursor commit atomically; corrupt/unknown payload cannot advance the cursor; no pending-money table is wiped during resync |
| R11 Local documents/data | Print/reprint on both hosts uses immutable authorised snapshots; attachments/import/CSV are bounded, isolated and backed up; no remote renderer/storage needed |
| R12 Recovery | Restore PostgreSQL, document storage and required keys on an isolated host; reconcile financial controls and reconcile device replays after restore; demonstrate declared RPO/RTO |
| R13 Supply-chain/release | Build and verification callable locally; exact released binaries tested after packaging; signed manifests/signatures validated offline; no GitHub/registry dependency at install/use |
| R14 Native cleanup | No retired runtime/provider dependencies, compatibility clients or source-specific import CLI in active solution/releases; immutable applied DB history is not destructively rewritten |
| R15 Honest completion | All tasks and integration gates meet pass conditions; unresolved production inputs remain explicit; no assertion that source presence or an old pass count proves production readiness |

Recommended **test envelope**, not measured performance: 20 concurrent online terminals, 100,000 variants, 1,000,000 posted sale lines, 10,000 pending operations/device, warm-cache catalog search p95 ≤ 500 ms and ordinary local-LAN cash posting p95 ≤ 1 s on a documented 4-vCPU/16-GB server with SSD. Use this as a reproducible engineering gate; revise the envelope explicitly if the agreed deployment differs. Local capture must acknowledge only after durable commit, irrespective of latency. Recommended recovery target: RPO ≤ 15 minutes and RTO ≤ 4 hours, proved against that envelope, not claimed from a backup filename.

<a id="gaps"></a>
## 2. Current-state/gap matrix and evidence limitations

### 2.1 Work to preserve

PostgreSQL/EF Core, current membership checks, composite tenant keys, local OpenIddict/PKCE, immutable journal/document guards, exact commercial amounts, moving-average inventory value, sale/refund/invoice/purchasing APIs, quotes/orders/fulfilment, open items, reports, the encrypted SQLite queue, atomic page+cursor method and the MAUI coordinator are existing assets. Their existence is **VERIFIED in source**; their complete deployed behaviour is **UNVERIFIED in this review**. Do not re-create them because an older readiness document says “not started”.

Existing customer/POS screens remain. The new invoice list/editor and typed invoice/payment client are **PARTIAL**, not missing. The latest commit adds source mappings as well as UI: the missing migration must be addressed before declaring the screens deployable. Other business-management UI remains absent from the inspected page inventory. Existing backend tests are retained and extended, not replaced with success-only mocks.

### 2.2 Verified issues, scoped gaps and architecture changes

| ID | Evidence/status | Current observation → required outcome | Tasks |
|---|---|---|---|
| F01 | VERIFIED delta; runtime impact INFERENCE / MISSING migration | New invoice fields/indexes in model, but latest 24-file diff has no migration or model-snapshot change. Prove and repair native schema parity before deployment; do not suppress pending-model warnings | 01 |
| F02 | VERIFIED / PARTIAL | Editor puts `Status == Draft` issue action inside `Status != Draft` branch; draft cannot reach it | 02 |
| F03 | VERIFIED / PARTIAL | Editor labels tax as percent, independently divides by 100 for preview, but submits the entered rate unchanged; preview is labelled server-calculated although local | 02, 03 |
| F04 | VERIFIED / PARTIAL | Editor exits for a user lacking edit permission before loading a readable invoice; always submits Service mode; customer options append on parameter changes; print uses relative web origin | 02, 24 |
| F05 | VERIFIED code; case impact to reproduce / PARTIAL | Partial-credit calculation explicitly selects Exclusive mode despite persisted invoice tax-mode/revenue snapshots; DTO line omits those snapshot fields | 03 |
| F06 | VERIFIED / PARTIAL | Production reset creates token and returns Accepted without a delivery implementation in the inspected method | 05, 07, 14 |
| F07 | VERIFIED / PARTIAL | Data Protection path is optional in startup; cookie policy is SameAsRequest; metrics middleware is before authentication; SameSite comments overstate CSRF coverage | 09, 34, 41 |
| F08 | VERIFIED / MISSING autonomy | Browser shell requests Google Fonts; disconnected appearance/runtime resource dependency remains | 10 |
| F09 | VERIFIED / PARTIAL | Direct PostgreSQL backup mode still calls Docker unconditionally; tooling portability is incomplete | 40 |
| F10 | VERIFIED / PARTIAL | Provisioner requires AdminPassword for all Psql actions but CI/Release Verify omit it. Host-run job also passes `postgres` while test connections use `localhost`; job 118922887861 confirms hostname resolution failure | 01, 44 |
| F11 | VERIFIED / PARTIAL | Release verifies one web build then separately rebuilds OS assets; no signing/offline installer or final-artifact acceptance in inspected workflow | 41–45 |
| F12 | VERIFIED / PARTIAL | SDK/workloads/actions/service-image versions float; package acquisition/apt/GitHub are part of normal workflow; sealed local build path absent | 11, 42, 44 |
| F13 | VERIFIED / MISSING UI/API slice | No complete business/user/device lifecycle administration surface in route/page inventory; development seed is not production onboarding | 05–08, 14 |
| F14 | VERIFIED / PARTIAL | Most business APIs exist; product/master/procurement/order/stock/banking/accounting/report management pages are not present | 13–34 |
| F15 | VERIFIED / PARTIAL | Feed checks only a future revision; no baseline/epoch/retention-floor contract. Customer projection filter reads a claim rather than proving its current permission | 04, 35 |
| F16 | VERIFIED / PARTIAL | Local sync applies projection before conflict decision; resolver marks resolution without the documented application step; null payloads can be skipped while cursor advances | 36 |
| F17 | VERIFIED / IMPLEMENTED core, UNVERIFIED platform | Atomic page+cursor method and SQLCipher already exist; full native grant/keys/restart/custody verification is not demonstrated here | 37, 38, 45 |
| F18 | VERIFIED / PARTIAL | Source-specific Migration and native DataImport coexist; old prose and contracts contradict a native-only completion claim | 12, 43, 46 |
| F19 | VERIFIED / PARTIAL | README/task/readiness/deployment statuses disagree with source, including identity, UI, encryption and sync progress | 46 |
| F20 | VERIFIED / EXCLUDED provider feature | Terminal provider is disabled by default. Do not make card acquiring a core completion blocker or simulate successful capture | 20, 42 |
| F21 | VERIFIED / PARTIAL | New invoice format hardcodes two display decimals and uses machine-local dates; use effective business settlement scale/time zone without changing historical amounts | 02, 24, 33 |
| F22 | INFERENCE / verification required | Accounting settings coexist with fixed account-code constants; prove each posting honours effective configured control accounts and source ownership | 03, 25, 27, 32 |
| F23 | RECOMMENDATION / NEW | No compulsory cloud attachment storage, SMTP or public PKI: provide local, operator-controlled substitutes with security rather than skipping the workflow | 07, 39–43 |
| F24 | VERIFIED source inventory / MISSING completion evidence | API-only capability is not a finished operator workflow; new screens need real host interactions, finance tie-outs, offline network and accessibility tests | 13–34, 45 |
| F25 | VERIFIED source / PARTIAL | New InvoiceEditor injects IInventoryApiClient, but inspected server Program registers no such client. Server-rendered activation must be verified and wired, not only WASM/MAUI compilation | 02, 13 |
| F26 | VERIFIED model boundary / PARTIAL | Native OpenItem represents opening AR/AP, while current PaymentAllocation targets invoices only. Complete explicit opening-item settlement rather than assuming native openings are already collectible/payable | 12, 25, 27, 33 |

Evidence for F01–F05 and F25: [S03](#evidence-s03), [S04](#evidence-s04), [S05](#evidence-s05), [S06](#evidence-s06), [S07](#evidence-s07), [S08](#evidence-s08) plus the [checked commit comparison](https://github.com/nirzaf/QtsBilling/compare/9c069c8b056f9078ce10bf1106dc5a34b891216e...400b1cd67b519920a99bcfdd9c4d65b1af88609f). F06–F12: [S09](#evidence-s09), [S01](#evidence-s01), [S02](#evidence-s02), [S10](#evidence-s10), [S12](#evidence-s12), [S13](#evidence-s13), [S14](#evidence-s14), [S15](#evidence-s15), [S25](#evidence-s25). F13–F24: [E02](#evidence-e02), [E04](#evidence-e04), [E18](#evidence-e18), [E21](#evidence-e21), [E22](#evidence-e22), [E23](#evidence-e23), [E25](#evidence-e25), [E26](#evidence-e26), [E27](#evidence-e27), [S16](#evidence-s16), [S17](#evidence-s17), [S18](#evidence-s18), [S20](#evidence-s20), [S22](#evidence-s22), [E35](#evidence-e35), [E36](#evidence-e36), [E43](#evidence-e43), [E44](#evidence-e44).

F25 additionally uses [S02](#evidence-s02), [S04](#evidence-s04).

**Limits:** the tree and changed implementation files were read through GitHub; unchanged sources from the prior review were re-established by the one-commit comparison. A bulk archive download was unavailable, but connector source access worked. No claim is made that every line of the repository has been audited, that no additional defect exists, or that a remote CI result was produced by this review. Only exact source-supported findings are labelled VERIFIED. Neither live business data nor installed Windows/printer/backup environments were inspected.

<a id="decisions"></a>
## 3. Architecture decisions and binding implementation contracts


Additional current model anchors: [S28](#evidence-s28), [S29](#evidence-s29), [S30](#evidence-s30), [S31](#evidence-s31). Existing location grants and enrollment challenges are already present. Do not rebuild them from stale task descriptions.

<a id="d01"></a>
### D01 — Self-hosted topology and ownership (EXISTING retained; deployment NEW)

The existing web host is the modular monolith: API, Identity/OpenIddict, domain orchestration, transactional audit and sync. Keep feature ownership under `QtsWebApp/QtsWebApp/Features/` and cross-cutting infrastructure under `Infrastructure/`. PostgreSQL owns committed business facts. `QtsBilling.Contracts` owns typed wire contracts, clients and canonical serialization. `QtsBilling.Ui` owns shared presentation, not EF or secrets. Browser traffic is same-origin HTTP; MAUI uses an authenticated API plus encrypted SQLite. Do not add a microservice, Redis, a message broker, a generic repository framework or another authoritative datastore.

Support two documented installation profiles: **SingleHost** (Windows PC hosting PostgreSQL + ASP.NET Core + MAUI/browser) and **LocalNetwork** (Windows or Linux server hosting PostgreSQL + ASP.NET Core, Windows terminals and browsers over LAN). They run the same binaries/business logic. A local server outage is distinct from an internet outage: the browser stops authoritative writes honestly; an already enrolled MAUI device may capture bounded provisional cash. Do not run a second authoritative database on each till.

`LocalCore` is a **NEW deployment profile name**, not an existing configuration switch. It disallows compulsory public-network calls. Its allowlist contains the configured owned API origin, owned database/backup endpoints and loopback browser callback only. External HTTP images, scripts, fonts, telemetry exporters and update checks are absent or explicitly disabled. Merely having a documentation URL in a ProblemDetails type or README is not a runtime network dependency.

<a id="d02"></a>
### D02 — Dependency budget and offline installation (NEW)

Keep existing .NET 10, EF Core/Npgsql, MudBlazor, OpenIddict, SQLCipher and exact-value helpers. A new dependency requires a concrete need, license review, pinned transitive closure and an offline artifact. Allow the official .NET Windows service integration package only if needed by the selected service-host implementation; allow a single **test-only** browser automation dependency, Microsoft.Playwright, for actual web interaction/no-egress tests. Its browser binaries must be preloaded in the test kit. No new production service dependency is justified by this specification.

Ship a release-kit manifest containing SHA-256, version, RID, license/notices, dependency inventory and signature for every executable/prerequisite. Include the supported PostgreSQL server/client tools and WebView2 offline prerequisite appropriate to the Windows profile, subject to redistribution terms. OS/service-manager/browser dependencies remain explicit. .NET self-contained publication includes the .NET runtime, not every native OS prerequisite; WebView2 can be installed offline but still needs an update/servicing policy. Do not claim one EXE eliminates these dependencies. [W1](#source-w1), [W2](#source-w2).

Separate **maintenance acquisition** (an authorised connected machine obtains reviewed packages, SDK/workload/browser packs and vulnerability information) from **sealed build/install/use** (no public sources available). Capture exact SDK/workload versions and package locks, including the RID-specific native libraries, and export a local NuGet feed rather than relying on a developer's warm cache. Offline vulnerability status must state the advisory snapshot date; lack of internet must not be presented as a clean current vulnerability scan. GitHub workflows may remain optional conveniences but must call the same local scripts.

<a id="d03"></a>
### D03 — Local trust, bootstrap and recovery (NEW around existing identity)

Use the existing local Identity/OpenIddict authorization-code + PKCE flow. No cloud identity tenant, SMS, CAPTCHA service or SMTP is mandatory. Trust the server using an owner-controlled internal CA, or an explicitly distributed pinned server certificate with a documented renewal procedure. Never use certificate-validation bypasses. DNS may be local; the certificate SAN, native issuer and configured API origin must match. TLS trust, OIDC signing/encryption, offline-grant signing, Data Protection and release-signing keys have separate purposes and access/rotation rules. Local production keys persist across restarts. The OpenIddict library supports locally configured credentials; this is not an external IdP requirement. [W3](#source-w3).

First-owner creation is an **operator-only bootstrap command** against a migrated, empty application instance. It holds a database advisory lock, refuses an already initialised instance, obtains secrets through protected interactive input, establishes the first owner/business/location and records a bootstrap audit event atomically. No public unauthenticated setup route, default password or hidden development seeder is permitted in production. The owner confirms base currency, settlement scale, fiscal calendar, time zone, numbering, tax treatment and chart/control accounts before the first posting.

For additional users, a current owner creates an account/membership and issues a short-lived one-time setup ticket after recent reauthentication. Display/print the ticket only once in an authorised response with no-store headers; store only a verifier and metadata, never log the secret. The recipient chooses their own password. Anonymous reset requests always return the same generic response; they cannot obtain tokens. Administrator-mediated recovery uses the same channel and records actor/reason. Last-owner recovery uses a physically controlled offline operator procedure with instance-bound recovery material and audit, not a universal bypass. Revoke outstanding tickets and active sessions as appropriate after reset/deactivation.

<a id="d04"></a>
### D04 — API, authorization and compatibility boundaries (EXISTING + NEW)

Preserve current request shapes and `/api/v1` semantics except explicitly scoped fixes. Retiring a database/provider does **not** require breaking unrelated clients. No old-provider compatibility layer or automatic source-format conversion is retained. If the new sync protocol is incompatible, version the payload explicitly and return a machine-readable upgrade-required outcome; never misinterpret an old queued cash operation. Historical local data must remain recoverable even when a client version is unsupported.

For every route: authenticate → load current active user/membership/business → authorise action and location/device → validate referenced tenant keys → validate state/version → acquire ordered locks → commit facts/audit/receipt/feed → return. UI visibility is not authorization. Resolve read/feed permissions from the same current authority, not cached claims. Tenant IDs in payloads are never accepted as proof. Reject inaccessible resources without revealing another business's details. Operations requiring a role not represented in current permissions add a narrowly named permission, not Owner bypasses.

Cookie mutations require explicit anti-forgery validation with same-origin controls, including login/logout/business switching and relevant native authorization POSTs. Bearer-only requests use their native security model and must not inherit a cookie principal. Production cookies are Secure/HttpOnly, Data Protection paths and keys are mandatory, and forwarded host/origin inputs are allowlisted when a proxy is used. Place telemetry endpoints behind management authorization or a separately bound loopback/private management listener. Log no tokens, reset tickets, full connection strings or sensitive payment data.

<a id="d05"></a>
### D05 — Commercial calculation, recognition and control accounts (EXISTING + corrective NEW)

Use the shared commercial calculator: decimal money at the configured storage/settlement scale, quantity up to six decimals, tax rates as **fractions**. UI percentage 15 converts once to 0.15 at the boundary and back once on load. Preserve tax inclusion mode, revenue base, rounded tax, discounts, unit/stock policy and historical cost per line. A client preview is labelled an estimate until server validation returns the committed values. Only the implemented moving-average valuation is included in LocalCore. Initialise a new business with that method explicitly: the existing Business enum/default is not proof of FIFO/LIFO support. An existing incompatible method label requires finance-reviewed classification before trade; never silently recost history. Unknown business tax/legal policy is not guessed from a country name. The native demo uses explicitly labelled synthetic settings; production requires recorded finance confirmation.

| Source | Required single economic effect |
|---|---|
| Service invoice | Issue records receivable/revenue/output-tax; no inventory movement. Service lines cannot secretly consume a stock-tracked variant. |
| Direct-goods invoice | Issue records revenue/tax/receivable and consumes inventory/value exactly once in that transaction. |
| POS sale representation | Invoice copies the sale's immutable snapshots and its source relationship; it does not repeat revenue, payment, receivable or stock postings. |
| Delivered order invoice | Delivery has already moved stock into the delivered-not-invoiced control account. Invoicing recognises revenue/receivable and releases the exact delivered cost from that account, without consuming stock twice. |
| Credit/refund | Reference original line/source; cumulative quantity/value cannot exceed that source. Use original tax mode/cost, and give the final partial reversal the exact remaining rounding residual. Physical return, damaged write-off and financial credit are explicit, non-duplicating effects. |
| Goods receipt/supplier bill | Receipt debits inventory and credits GRNI; supplier bill clears the linked GRNI, records input tax/approved price variance and payable. A PO alone posts neither stock nor finance. |
| Unsettled money | Customer credit creates AR, not confirmed cash. Cheque/transfer is pending until operator-confirmed clearance; payment allocation cannot allocate unsettled or already allocated funds. Customer deposits remain a liability until applied/refunded. |

Read the effective `BusinessAccountingSettings` control-account IDs for each posting; validate account type/activity/tenant. Numeric code constants may seed defaults but must not override saved mappings. Reports use posted journals/open items/movements and tie out by business/currency/date. Posting needs one covering open period. Existing posted facts are immutable; changes use credit/reversal or a newly approved opening, never rewriting a past price or balance.

<a id="d06"></a>
### D06 — Concurrency, transaction and retry contracts (EXISTING retained; gaps corrected)

Reuse `LedgerWriter`, `InventoryLedger`, `SalePosting`, `SyncTransaction` and `IdempotencyExecutor`; do not nest a second transaction inside an existing caller-owned transaction. Document the common lock order before modifying it: command/device receipt → current aggregate/register/period as required by its existing writer → ordered variant/location balance rows → scoped number/revision counters. Align competing paths to that order and test any differences; do not blindly introduce a contradictory global order. Stock has no negative escape hatch in the core profile.

Every money/stock/state-changing command has a client-generated idempotency key bound to business, command kind and aggregate identity plus the canonical payload fingerprint. Same key/same request returns the stored outcome; same key/different request is `409 SAME_KEY_DIFFERENT_PAYLOAD`. A missing key is rejected by the endpoint rather than relying on the executor's optional-key fallback. Store command result and effect atomically. Precondition failures may leave no receipt only when the response explicitly establishes no effect; an authenticated, scoped terminal business rejection gets a durable operation receipt. A global authentication failure is not misrepresented as a processed business operation; the device retains it locally for authorised recovery. On timeout/transport loss, keep the exact key/body and resolve/retry it; no “try again” button creates a fresh sale. Provider/filesystem/process I/O stays outside database transactions.

<a id="d07"></a>
### D07 — Physical cash, offline grants and rejected-operation custody (corrective NEW)

A cash drawer has one active session and known location/operator custody. Core online cash requires a register/session; genuine back-office cash uses a separately named and authorised cash account/session, not an invisible registerless shortcut. Closing must distinguish server-posted cash, device-held provisional cash, pending rejection/refund cases and counted cash. Prevent final settlement while assigned devices have unresolved cash, or require a manager-authorised carry-forward record naming each operation and amount. Never write an unexplained balancing journal to hide a discrepancy.

Preserve the existing signed offline-grant format and its bounded lifetime (current default eight hours, maximum 24 hours). Grant validation binds user/device/business/location/register session/currency/permission/revision and trusted key. Revocation during disconnection cannot be known instantly; stop new capture at grant expiry or clock rollback, disclose bounded lag, and deny revoked work on reconnection without deleting physically received money. No automatic extension from a cached session. A rejected sale remains a durable recovery case with amount, cashier and reason until an authorised resolution records return of cash, corrected linked command, or another documented disposition. It is never relabelled a posted sale merely to empty the queue.

<a id="d08"></a>
### D08 — Snapshot baseline and incremental sync (NEW protocol around existing code)

Extend contracts with `epoch`, `schemaVersion`, `permissionRevision`, `retentionFloor` and cursor `(revision, ordinal)`. Bind a baseline token to enrolled device, business and allowed locations, not just a user cookie. Capture an immutable baseline at a proven database snapshot/revision boundary; paginate that snapshot without mixing later writes, then resume the change feed from its watermark. Use a bounded server-side snapshot/export job in existing host/owned storage only if the data size requires it; no separate cloud or queue service. A changed permission scope, restored database epoch or cursor below the retention floor requires a new baseline. Until retention is implemented, retain all feed rows and explicitly advertise the unpruned floor.

Server payload serialization errors fail the page with a stable corruption code and diagnostic correlation; they do not become null projections. The device validates the entire page and scope before committing. Apply cache changes, conflict records and cursor in one SQLite transaction using the already-existing atomic method. Detect conflicts **before** mutating cache rows. A chosen server value updates only the cache; captured cash payloads remain immutable. “Keep local” means retain the original queued command for server adjudication, never make SQLite authoritative. Resync replaces cache tables only after a complete baseline; provisional sales, cash, receipts, outbox and recovery cases are never wiped.

<a id="d09"></a>
### D09 — Operator UI, native printing and documents (NEW completion around existing pages)

Keep the customer/POS/invoice pages and fix them. Add a small, permission-aware navigation shell for sales, purchasing, inventory, finance and administration; no dashboard designer or portal rewrite. Both hosts consume the same feature interfaces. All lists have server-side bounded paging/search, cancel obsolete loads and expose loading/empty/error/forbidden/stale-version states. References use searchable selectors, never SQL-obtained GUIDs. Business context is visible on every transactional screen. Keyboard entry/scanning, focus, validation associations, zoom and labelled controls are mandatory; no new languages/platforms are invented.

An issued document prints from authorised immutable snapshots, not live catalog/customer data. The browser can use the same-origin print endpoint; MAUI must fetch with its bearer client and use a host-owned print/export adapter. Never put access tokens in a URL or navigate WebView2 to a bearer-protected relative API route as if it were an authenticated browser. HTML receipts/invoices and the OS print-to-PDF facility are sufficient core output; no remote PDF renderer or new PDF SDK is required.

A narrow local evidence attachment feature supports invoice/expense/purchase documents only: bounded PDF/JPEG/PNG, inspected media type, random storage key, tenant/document metadata, no executable/HTML/SVG content, no remote URL fetch and attachment download disposition. Store bytes in an ACL-protected owned directory outside webroot, stage the file before the metadata transaction, and clean unreferenced staging files safely. Never overwrite a posted document's original evidence; replace with a new version linked to it. Back up bytes and metadata together. No content is sent to a remote scanning or storage service.

<a id="d10"></a>
### D10 — Native data only; no compulsory historical source (EXISTING replacement completed)

Make `QtsBilling.DataImport` the sole import boundary. Support signed, bounded native packages for masters and approved openings; preserve its authenticated manifest and transactional writer. Ordinary master CSV import is previewed and transformed into this native pipeline, with explicit reject rows and no silent correction. Retire `QtsBilling.Migration` and its source-format assumptions only after required reconciliation/valuation tests are moved. Remove retired provider/desktop names and compatibility paths from active code, docs, configuration and new release artifacts.

Do **not** edit applied EF migrations, delete posted financial/audit history, rewrite Git history, purge backups or remove old published releases in this implementation. Historical provenance and already-applied migration identifiers are not runtime compatibility. Any demand for literal removal from those immutable archives is a separately authorised administration operation; it cannot safely be inferred from this planning request. A new native installation requires no historical snapshot. An existing business requires a finance-approved native opening and exact AR/AP/inventory/control reconciliation, not a fabricated empty opening.

<a id="d11"></a>
### D11 — Local operations, recovery and release (NEW completion)

Use native PostgreSQL tooling and OS service/task scheduling as the default; Docker is optional development packaging only. Schedule encrypted/access-controlled local backups to a separate owner-controlled volume with tested off-machine/offline copies. A dump is useful but does not itself provide point-in-time recovery; the recommended 15-minute RPO needs a tested base-backup/WAL archive chain. A standalone recovery run restores the database, attachments, public trust metadata and required private keys with correct ACLs. Device queues may replay after restore; preserve operation identities and restore epoch rules to prevent double money. [W4](#source-w4).

A local PowerShell release entrypoint builds, verifies and packages the exact artifacts; the GitHub workflow is an adapter, not the only way to produce a release. Ship signed manifests and locally trusted Windows packages/binaries where appropriate. The owner may use an internally trusted signing authority on managed machines; do not require a public signing service or an online timestamp server for the disconnected core path. Describe certificate expiry, local revocation distribution and re-sign/update requirements honestly. Test the produced server and desktop package, not merely equivalent source, and promote by digest without rebuilding.

Admission has distinct **read-only check** and **authorised apply** modes bound to instance/environment/schema/artifact hashes. A new-business path records approved zero openings; an existing-business path requires a native package and reconciled report. A rejected or omitted required input cannot be turned into a successful skipped gate. No in-place rollback after new financial writes: stop writes and fix forward, or perform an explicitly approved restore plus reconciliation of every post-backup/device operation. The deployer supplies keys, storage, business facts and approval; the code cannot manufacture them.

<a id="d12"></a>
### D12 — Task execution contract and minimal change policy

Tasks below are the residual completion work, not a replay of QB-00 onward. Numbering is local to this specification; do not mark a historical QB task complete merely because a new task has the same number. Each task identifies exact existing or proposed file paths. A path labelled NEW does not claim a current symbol exists. EF-generated filenames are the sole exception to a preassigned timestamp: the task fixes the migration class/name and directory, and requires recording the actual generated timestamp/designer/snapshot paths in the change ledger before review.

Most tasks change one to three production files plus tests. An indivisible API/contract/UI/host-registration or schema/guard migration may exceed that limit only within the named integration gate; list each file and keep the diff scoped. A page and its API must not be advertised before their permissions, transaction and tests ship together. Use existing fixtures; new tests target changed behaviour rather than multiplying happy-path cases. Full regression/platform/disconnected acceptance occurs at the integrated candidate gate, not after every local edit. All commands in this document are instructions for later authorised implementation, **NOT RUN — SPEC_ONLY** here.

<a id="d13"></a>
### D13 — Exact NEW contract/schema bindings for completion tasks

The following are **proposed additions**, not claims about existing endpoints/tables. Names are fixed to keep implementation agents aligned. Reuse an existing field/entity when its semantics already match; record any name collision before coding and keep one authority. All new business-owned references use `(Id, BusinessId)` composite keys; GUID IDs, UTC timestamps, DateOnly business dates, decimal amounts and version concurrency follow the current repository conventions.

| Tasks | NEW or extended contract | Required data/invariants and migration name |
|---|---|---|
| 05 | Operator CLI `--bootstrap-instance`; no public bootstrap HTTP endpoint | `InstanceBootstrap` singleton: InstanceId, State, SchemaVersion, FirstOwnerUserId, InitialisedAtUtc, OpeningMode, ApprovedOpeningManifestHash. Guard one completed bootstrap with DB uniqueness/advisory lock; `AddLocalBootstrapState` if no equivalent marker exists. Owner password goes through Identity, never this table. |
| 06 | `GET/PUT /api/v1/business-settings`; `POST /api/v1/locations`; `PUT /api/v1/locations/{id}`; `POST /api/v1/locations/{id}/deactivate` | `SaveBusinessSettingsRequest(ExpectedVersion, Name, LegalName, TimeZone, BaseCurrency, SettlementScale, FiscalYearStartMonth, NumberingSettings)` delegates financial-account fields to existing accounting settings. Reject post-fact changes to structural currency/precision; version and audit all accepted changes. |
| 07 | `/api/v1/administration/users` list/create; `PUT /{id}/membership`; `POST /{id}/disable`, `/activate`, `/setup-ticket`; `POST /api/v1/auth/setup-ticket/complete` | `SetupTicket`: Id, BusinessId, UserId, Purpose, VerifierHash, IssuerUserId, ExpiresAtUtc, ConsumedAtUtc, RevokedAtUtc, Version. Secret ≥128 random bits, suggested TTL30 minutes, single use; recent issuer reauthentication ≤5 minutes. Completion body `(TicketId, Secret, NewPassword)` contains no caller-chosen target user. Validate ticket then use Identity password reset APIs atomically. `AddLocalSetupTickets`. |
| 07 | Membership edit | Existing BusinessMembership/BusinessMembershipLocation store Role, IsActive, AllLocations, MaxDiscountRate and AuthorizationRevision. Explicitly set the default new cashier discount allowance to zero, not the current 100% field default. Increment AuthorizationRevision on authority changes. Enforce current-role delegation and last-owner rules. |
| 08 | `GET /api/v1/devices`, `GET /{id}`, `POST /{id}/revoke`, `/reassign`, `/retire` | Existing challenge/registration/grant remains. New retirement metadata only if needed: reason, requested/completed UTC, actor, unresolved-custody reference. Reassignment requires an original-scope money disposition, never changing operation ownership in place. |
| 17 | `POST /api/v1/products/{id}/variants`; `POST /{id}/deactivate`; variant deactivate under its parent | `CreateVariantRequest(Name, Sku, Barcode, SellingPrice, PurchasePrice, ExpectedProductVersion)`. Reuse default/active fields and current unique constraints. No on-hand field, no arbitrary client tenant. |
| 19, 37 | NEW `/api/v1/pos/registers/{id}/sessions/{sessionId}/custody-transfers`; operation recovery under `/api/v1/sync/operations/{operationId}/resolutions` | `CashCustodyTransfer`: business/session/device/operation identifiers, amount/currency, source/receiving custodian, reason, state, actor/time and linked reconciliation event. Unique active disposition per device operation; immutable amount. `AddCashCustodyResolutions`. Existing posted drawer movements remain the cash ledger, not a parallel mutable balance table. |
| 25 | `GET /api/v1/customers/{id}/statement`; `POST /api/v1/payments/{id}/refund-unallocated`; existing allocate route extended | `AllocationTarget(InvoiceId?, OpenItemId?, Amount)` has exactly one non-null target. Existing PaymentAllocation gains nullable OpenItemId and nullable InvoiceId, positive amount, same business/party/currency. Add append-only correction/refund references only when needed; `AddOpeningItemAllocations`/`AddCustomerDepositDispositions`. Never patch OpenItem.OriginalAmount. |
| 27 | `POST /api/v1/purchasing/bills/{id}/credits`; `GET /api/v1/suppliers/{id}/statement`; existing supplier-payments extended | `SupplierCredit`/lines: original bill/line, optional source purchase-return line, quantity, original base/tax/value, reason, journal reference, command key/fingerprint, immutable issued time. Cumulative credits cannot exceed bill/return eligibility; supplier refund/application is explicit. `AddSupplierCreditNotes`; supplier allocation has exactly one bill or opening-payable target. |
| 31 | `POST /api/v1/banking/statement-lines/{id}/unmatch`; `POST /api/v1/banking/statements/{id}/finalise` and `/reopen` | ExpectedVersion, reason, command key. Preserve append-only match/correction events; finalised statement totals immutable. `AddBankReconciliationEvents` only for missing event/state fields. Posting a fee uses existing journal writer, not mutation of an imported line. |
| 35 | `POST /api/v1/sync/baselines`; `GET /api/v1/sync/baselines/{id}/pages/{page}`; existing changes route extended | `SyncEpoch`, retention floor, schema version and authority revision; `SyncBaseline` bound to device/business/locations plus watermark/expiry/state; immutable paged projections and page hashes. Stage within one consistent DB snapshot, then Ready. `AddScopedSyncBaselines`. Limit active snapshots/device and page/resource sizes. |
| 35–38 | Wire page/envelope extensions | Page includes BaselineId/Epoch/SchemaVersion/AuthorityRevision/NextRevision/NextOrdinal/HasMore/ResyncRequired. Operation identity and monetary payload stay immutable. Unknown required projection/schema fails the page. Unsupported queued schema produces explicit recoverable upgrade/review, not reinterpretation. |
| 39 | `POST /api/v1/documents`, `GET /api/v1/documents/{id}/content`; list versions by allowed aggregate | `DocumentEvidence`: Id, BusinessId, constrained EntityKind/EntityId, StorageKey, OriginalName, MediaType, Length, Sha256, PriorVersionId?, actor/time, retention status. Suggested limits: 20MiB/file, 20 active files/document; PDF/JPEG/PNG only. `AddLocalDocumentEvidence`; files outside webroot with owned ACL. |

All new mutation endpoints return the existing ProblemDetails envelope: stable `code`, `traceId` and field errors without SQL/stack/secrets. Use existing suitable codes; NEW codes are added centrally to `QtsBilling.Contracts/Abstractions/ApiErrorCodes.cs` only if needed: `SETUP_REQUIRED`, `TICKET_INVALID`, `UNRESOLVED_CASH`, `SYNC_SCOPE_CHANGED`, `SYNC_PAYLOAD_INVALID`, `DOCUMENT_REJECTED`, `INSTANCE_MISMATCH`. Keep current 400/401/403/404/409/413/503 conventions and test each new boundary. Do not invent a new generic error framework.

For new settings/ticket/custody/baseline/document migrations, exact generated paths are recorded at authoring under `QtsBilling.Persistence.Postgres/Migrations/` with their designer plus model snapshot; SQLite additions use its separate Migrations directory/history. Extend database guards for immutable financial metadata and indexes/FKs before exposing UI actions. Existing databases are upgraded, not dropped to make the schema pass.

<a id="task-index"></a>
## 4. Dependency-ordered task index and must-ship-together gates

Run the numbered order as a safe default; a task may start earlier only after its explicit dependencies are accepted. No parallel agent is required. P0 means a release blocker, not permission to ship a partial money workflow. The early Task 01 repair restores the verification prerequisite; Task 44 integrates the final pipeline rather than repeating that fix. These are **46 new residual tasks**, not a claim that 46 historical tasks are missing.

| Gate | Includes | Release condition |
|---|---|---|
| G0 — Baseline correctness | 01–02 | Model/schema match; baseline CI can reach its test database; invoice screens activate and draft issue/tax inputs work. |
| G1 — Authoritative rules | 03–04 | Original recognition/cost/tax and current scopes are proven; posted facts immutable. |
| G2 — Local autonomy foundation | 05–11 | Bootstrap/recovery/device controls/security/local assets and sealed dependencies work without hosted services. |
| G3 — Native data boundary | 12 | Native import/reconciliation retained; source-specific runtime retired without destroying audit history. |
| G4 — Daily retail | 13–21 | Shared shell, administration, parties/catalog, registers/POS/refunds are integrated across hosts. |
| G5 — SMB workflows | 22–34 and 39 | Order-to-cash, procure-to-pay, stock/accounting/banking/reporting and local evidence are operator-complete. |
| G6 — Offline safety | 35–38 | Baseline/feed/conflict/receipt/cash/grant recovery is proven, including actual Windows behavior. |
| G7 — Release and recovery | 40–46 | Native install/recovery, exact-artifact release, final tests and honest owner admission. |

Inseparable integration bundles: Task 01 migration + guards + model + upgrade tests; Tasks 06–09 admin contracts + current authorization + server registration; Tasks 14–34 relevant page + typed client + all host registrations; Tasks 19/37 physical custody + server/device recovery; Tasks 35–38 wire protocol + server + SQLite + native coordinator; Tasks 40–45 recovery + installer + signed final-artifact checks. Do not expose a navigation action whose corresponding bundle is incomplete.

| Task | Priority / gate | Primary checked or NEW path | Depends on |
|---|---|---|---|
| [T01](#task-01) — Restore invoice model, migration and invariant parity | P0 / G0 | `QtsBilling.Persistence.Postgres/Entities/Accounting.cs` | None |
| [T02](#task-02) — Repair the existing invoice editor and list regressions | P0 / G0 | `QtsBilling.Ui/Pages/Invoices/InvoiceEditor.razor` | [T01](#task-01) |
| [T03](#task-03) — Complete invoice recognition, original-tax credits and configured posting controls | P0 / G1 | `QtsWebApp/QtsWebApp/Features/Invoices/InvoiceEndpoints.cs` | [T01](#task-01) |
| [T04](#task-04) — Close current tenant, location and device authorization gaps | P0 / G1 | `QtsWebApp/QtsWebApp/Infrastructure/QtsIdentity.cs` | [T01](#task-01) |
| [T05](#task-05) — Add operator-only local instance bootstrap | P0 / G2 | `QtsWebApp/QtsWebApp/Infrastructure/LocalBootstrap.cs` | [T01](#task-01), [T03](#task-03), [T04](#task-04) |
| [T06](#task-06) — Expose business and location setup with financial configuration guards | P1 / G2 | `QtsWebApp/QtsWebApp/Features/Administration/BusinessEndpoints.cs` | [T05](#task-05) |
| [T07](#task-07) — Implement local user administration and secure setup/recovery tickets | P0 / G2 | `QtsWebApp/QtsWebApp/Features/Administration/UserEndpoints.cs` | [T05](#task-05), [T04](#task-04) |
| [T08](#task-08) — Complete device lifecycle administration without losing pending cash | P0 / G2 | `QtsWebApp/QtsWebApp/Features/Devices/DeviceEndpoints.cs` | [T06](#task-06), [T07](#task-07) |
| [T09](#task-09) — Harden local production sessions, keys and management endpoints | P0 / G2 | `QtsWebApp/QtsWebApp/Program.cs` | [T04](#task-04), [T07](#task-07) |
| [T10](#task-10) — Eliminate public-network runtime assets and prove local rendering | P1 / G2 | `QtsWebApp/QtsWebApp/Components/App.razor` | [T02](#task-02) |
| [T11](#task-11) — Pin and inventory a reproducible offline build dependency set | P1 / G2 | `scripts/prepare-build-kit.ps1` | None |
| [T12](#task-12) — Finish native onboarding and retire the source-specific import chain | P1 / G3 | `QtsBilling.DataImport/Program.cs` | [T01](#task-01), [T03](#task-03), [T05](#task-05) |
| [T13](#task-13) — Complete the shared application shell and visible business context | P1 / G4 | `QtsBilling.Ui/Layout/MainLayout.razor` | [T04](#task-04), [T06](#task-06), [T07](#task-07), [T09](#task-09) |
| [T14](#task-14) — Add business, user and device administration screens | P1 / G4 | `QtsBilling.Ui/Pages/Administration/BusinessSettings.razor` | [T06](#task-06), [T07](#task-07), [T08](#task-08), [T13](#task-13) |
| [T15](#task-15) — Finish customer and supplier workbenches with real paging | P1 / G4 | `QtsBilling.Ui/Pages/Customers.razor` | [T13](#task-13) |
| [T16](#task-16) — Expose master-data, tax and discount configuration in the shared UI | P1 / G4 | `QtsBilling.Ui/Pages/Catalog/MasterData.razor` | [T13](#task-13) |
| [T17](#task-17) — Complete product and variant maintenance APIs and concurrency | P1 / G4 | `QtsWebApp/QtsWebApp/Features/Catalog/ProductEndpoints.cs` | [T01](#task-01), [T04](#task-04), [T16](#task-16) |
| [T18](#task-18) — Build the product and variant workbench | P1 / G4 | `QtsBilling.Ui/Pages/Catalog/Products.razor` | [T17](#task-17), [T16](#task-16) |
| [T19](#task-19) — Complete register custody, pending-cash close rules and register UI | P0 / G4 | `QtsWebApp/QtsWebApp/Features/Sales/CashRegisterEndpoints.cs` | [T03](#task-03), [T04](#task-04), [T08](#task-08) |
| [T20](#task-20) — Finish web and desktop checkout with stable unknown-outcome recovery | P0 / G4 | `QtsBilling.Ui/Pages/Pos.razor` | [T02](#task-02), [T18](#task-18), [T19](#task-19) |
| [T21](#task-21) — Add sales history and controlled partial-refund workflows | P1 / G4 | `QtsBilling.Ui/Pages/Sales/Sales.razor` | [T19](#task-19), [T20](#task-20) |
| [T22](#task-22) — Expose quotation and sales-order lifecycles | P1 / G5 | `QtsBilling.Ui/Pages/Orders/Quotes.razor` | [T15](#task-15), [T18](#task-18), [T13](#task-13) |
| [T23](#task-23) — Complete reservation, delivery and fulfillment workbench | P1 / G5 | `QtsBilling.Ui/Pages/Orders/Fulfillment.razor` | [T22](#task-22) |
| [T24](#task-24) — Complete invoice settlement, credits and authenticated printing UI | P1 / G5 | `QtsBilling.Ui/Pages/Invoices/InvoiceEditor.razor` | [T02](#task-02), [T03](#task-03), [T15](#task-15), [T19](#task-19) |
| [T25](#task-25) — Finish customer statements, deposit application and cash-credit recovery | P1 / G5 | `QtsWebApp/QtsWebApp/Features/Invoices/PaymentEndpoints.cs` | [T03](#task-03), [T24](#task-24) |
| [T26](#task-26) — Build the purchase-order, receiving and returns workbench | P1 / G5 | `QtsBilling.Ui/Pages/Purchasing/PurchaseOrders.razor` | [T15](#task-15), [T18](#task-18) |
| [T27](#task-27) — Complete supplier bills, credits, payments and payable workbench | P1 / G5 | `QtsWebApp/QtsWebApp/Features/Purchasing/SupplierBillEndpoints.cs` | [T03](#task-03), [T26](#task-26) |
| [T28](#task-28) — Expose expenses, categories, tax and reversal workflows | P1 / G5 | `QtsBilling.Ui/Pages/Expenses/Expenses.razor` | [T03](#task-03), [T15](#task-15), [T13](#task-13) |
| [T29](#task-29) — Expose inventory overview and valued inter-location transfers | P1 / G5 | `QtsBilling.Ui/Pages/Inventory/Stock.razor` | [T18](#task-18), [T19](#task-19) |
| [T30](#task-30) — Expose blind stock counts, approvals and valuation evidence | P1 / G5 | `QtsBilling.Ui/Pages/Inventory/StockCounts.razor` | [T29](#task-29), [T03](#task-03) |
| [T31](#task-31) — Complete local bank-statement reconciliation and corrections | P1 / G5 | `QtsWebApp/QtsWebApp/Features/Banking/BankingEndpoints.cs` | [T03](#task-03), [T25](#task-25), [T27](#task-27) |
| [T32](#task-32) — Complete accounting, period and year-close screens | P1 / G5 | `QtsBilling.Ui/Pages/Accounting/Accounts.razor` | [T03](#task-03), [T13](#task-13) |
| [T33](#task-33) — Complete reports, local exports and the management dashboard | P1 / G5 | `QtsWebApp/QtsWebApp/Features/Reporting/ReportEndpoints.cs` | [T25](#task-25), [T27](#task-27), [T28](#task-28), [T30](#task-30), [T31](#task-31), [T32](#task-32) |
| [T34](#task-34) — Provide local audit, health and operational diagnostics | P1 / G5 | `QtsBilling.Ui/Pages/Administration/Audit.razor` | [T09](#task-09), [T13](#task-13) |
| [T35](#task-35) — Add device-scoped baselines, epochs and retention-safe change feeds | P0 / G6 | `QtsWebApp/QtsWebApp/Features/Sync/SyncChangeEndpoints.cs` | [T04](#task-04), [T08](#task-08) |
| [T36](#task-36) — Fix SQLite conflict ordering and atomic cache outcome semantics | P0 / G6 | `QtsBilling.Persistence.Sqlite/SqliteLocalStore.Sync.cs` | [T35](#task-35) |
| [T37](#task-37) — Finish durable sync outcomes and rejected-cash recovery | P0 / G6 | `QtsWebApp/QtsWebApp/Features/Sync/SyncOperationEndpoints.cs` | [T19](#task-19), [T35](#task-35), [T36](#task-36) |
| [T38](#task-38) — Prove native identity, signed grants, encryption and key recovery on Windows | P0 / G6 | `QtsBilling.Maui/Services/OfflineGrantService.cs` | [T08](#task-08), [T09](#task-09), [T35](#task-35), [T36](#task-36), [T37](#task-37) |
| [T39](#task-39) — Add bounded local document evidence and offline-safe document output | P1 / G5 | `QtsWebApp/QtsWebApp/Features/Documents/DocumentEndpoints.cs` | [T24](#task-24), [T28](#task-28) |
| [T40](#task-40) — Make backup, restore and recovery genuinely Docker-free | P0 / G7 | `scripts/backup-postgres.ps1` | [T11](#task-11), [T12](#task-12), [T39](#task-39) |
| [T41](#task-41) — Package secure offline SingleHost and LocalNetwork installations | P1 / G7 | `scripts/install-local.ps1` | [T05](#task-05), [T09](#task-09), [T10](#task-10), [T11](#task-11), [T40](#task-40) |
| [T42](#task-42) — Create a local release pipeline and signed offline distribution kit | P1 / G7 | `scripts/build-release.ps1` | [T11](#task-11), [T38](#task-38), [T40](#task-40), [T41](#task-41) |
| [T43](#task-43) — Complete native admission, updates and honest rollback controls | P0 / G7 | `scripts/qb30-cutover.ps1` | [T01](#task-01), [T12](#task-12), [T38](#task-38), [T40](#task-40), [T42](#task-42) |
| [T44](#task-44) — Repair CI invocation defects and align automated checks with local release | P0 / G7 | `.github/workflows/ci.yml` | [T01](#task-01), [T09](#task-09), [T11](#task-11), [T12](#task-12), [T38](#task-38), [T42](#task-42), [T43](#task-43) |
| [T45](#task-45) — Run integrated business, disconnected and recovery acceptance on the candidate | P0 / G7 | `TestQtsBilling/Ui/BrowserFixture.cs` | [T14](#task-14), [T15](#task-15), [T16](#task-16), [T18](#task-18), [T20](#task-20), [T21](#task-21), [T22](#task-22), [T23](#task-23), [T24](#task-24), [T25](#task-25), [T26](#task-26), [T27](#task-27), [T28](#task-28), [T29](#task-29), [T30](#task-30), [T31](#task-31), [T32](#task-32), [T33](#task-33), [T34](#task-34), [T38](#task-38), [T39](#task-39), [T40](#task-40), [T41](#task-41), [T42](#task-42), [T43](#task-43), [T44](#task-44) |
| [T46](#task-46) — Reconcile documentation, scope and owner-controlled production admission | P1 / G7 | `docs/migration/readiness.md` | [T45](#task-45) |

<a id="verification-contract"></a>
### 4.1 Verification prerequisites and truthfulness contract

All commands below are **NOT RUN — SPEC_ONLY** in this review. Run from the repository root in an isolated implementation checkout. Confirm the current remote SHA and applicable AGENTS instructions before each change; rebase evidence when it differs from this document. Never discard local changes. `git diff --check` is a later whitespace check, not proof of financial correctness.

**Domain/contract tests:** restored pinned .NET SDK/packages and no production credentials. **PostgreSQL/API tests:** PostgreSQL 18 on an explicit allowlisted disposable test server, both `QTS_TEST_POSTGRES_CONNECTION` and `QTS_TEST_POSTGRES_RUNTIME_CONNECTION` configured to distinct owner/runtime roles, fixture-generated `qts_test_…` databases with ownership markers. Require nonzero matching tests; a filter selecting zero tests is a failure. **SQLite tests:** temp-file stores and synthetic keys, never a user's real device file. **Browser tests:** the owned fixture host + disposable DB + cached test browser; Production-profile security/no-egress is checked again on the packaged candidate. **Native checks:** supported Windows, pinned MAUI workload/WebView2, locally trusted certificates and a disposable/pilot data directory.

Existing build/check entrypoints verified in repository manifests:

```text
dotnet build QtsWebApp/QtsWebApp/QtsWebApp.csproj -c Release
dotnet build QtsBilling.Maui/QtsBilling.Maui.csproj -c Release
dotnet run --project QtsBilling.Ui.Smoke/QtsBilling.Ui.Smoke.csproj -c Release
pwsh -NoProfile -File scripts/secret-scan.ps1
git diff --check
```

The MAUI command is Windows-only. Full existing test-project runs are CI/release gates, not mandatory after every local task. Per-task commands use narrow filters. Commands for NEW scripts/tests become available only when the named task creates them; they are proposed interfaces, not claims those files already exist. Every verifier must fail if a prerequisite, tool or required assertion is missing; report NOT RUN/BLOCKED rather than a false pass.

**Schema rule:** generate migrations with the restored project tool and inspect SQL; applying any migration/test fixture is outside this review. In later authorised implementation, migrate only a marked disposable database for development and a separately approved production target for deployment. Record generated timestamp paths rather than inventing them in advance.

<a id="atomic-tasks"></a>
## 5. Atomic tasks

<a id="task-01"></a>
## Atomic Task 01 - Restore invoice model, migration and invariant parity

### A. Objective

**P0 · G0 · Requirements R02, R05, R15.** Dependencies: None.

**Current → target:** Invoice model fields/indexes changed at the baseline without a corresponding migration/snapshot in the checked commit. **Target:** A clean database and an upgrade from the previous migration both match the current model, preserve existing issued documents and reject illegal rewrites.

**Non-goals:** no unrelated architecture rewrite, new mandatory hosted service, historical repricing or production action. The task is limited to the scope below and the binding decisions in Section 3. Acceptance is the task-specific test outcomes plus G.

### B. Scope

- READ-ONLY `QtsBilling.Persistence.Postgres/Entities/Accounting.cs` and `QtsBilling.Persistence.Postgres/BillingDbContext.Model.Financial.cs`; inspect every newly mapped property, not only RecognitionMode.
- GENERATE `QtsBilling.Persistence.Postgres/Migrations/<generated-timestamp>_CompleteInvoiceRecognitionSnapshots.cs`, its `.Designer.cs`, and MODIFY `QtsBilling.Persistence.Postgres/Migrations/BillingDbContextModelSnapshot.cs` using EF. The timestamp is generator-owned and must be recorded; never alter an applied migration.
- MODIFY `TestQtsBilling/Integration/MigrationUpgradeTests.cs`; CREATE `TestQtsBilling/Integration/ModelParityTests.cs`.
- BASELINE GATE MODIFY `.github/workflows/ci.yml`, `.github/workflows/release.yml`, and `scripts/provision-postgres-roles.ps1` only for the confirmed host-run hostname and Verify credential-condition defects. Full local release integration remains Task 44.

### C. Verified anchors and NEW insertion points

[S03](#evidence-s03), [S06](#evidence-s06), [S24](#evidence-s24), [E48](#evidence-e48), [E49](#evidence-e49). These refer to the pinned baseline. Existing code is reused; all CREATE paths, new DTOs/routes and generated migration names in this task are **NEW**, not already-implemented capabilities. Read the complete owning method and its callers before changing the anchored slice.

### D. Ordered recipe

1. Repair the current verification prerequisite first: host-run CI reaches its PostgreSQL service through localhost/mapped port, not the container-only hostname; Psql Verify does not require an unused administrator password. Preserve distinct DDL/runtime roles. The inspected CI job failed at this exact hostname lookup, so later skipped tests are not evidence.
2. Compare runtime model to the last committed model snapshot and migration head. Generate one additive migration for all unapplied invoice model differences, including new header/line fields, source links and filtered unique indexes. Do not suppress pending-model-change warnings.
3. Make upgrade backfills deterministic from existing immutable source rows. Preserve missing historical names as explicitly unknown; never fabricate them from today’s customer record. Detect duplicate source-sale links before adding uniqueness and stop with counts/IDs rather than delete or merge records.
4. Extend posted-document database guards to freeze the new recognition, source and snapshot fields. Permit only legitimate lifecycle transitions; make each source invoice exclusive so retries cannot repeat economic recognition.
5. Test fresh migration, upgrade with posted/service/direct-goods/sale-linked data, schema comparison and raw-SQL tampering. Generate and review migration SQL without applying it to a real business.

### E. Focused tests

**Files:** MODIFY `TestQtsBilling/Integration/MigrationUpgradeTests.cs`; CREATE `TestQtsBilling/Integration/ModelParityTests.cs`.

- **T01.01** — Given the prior schema with issued invoices, when upgraded, then original totals/line composition/IDs remain identical and new nullable snapshots are honestly classified.
- **T01.02** — Given duplicate source links or unsupported historical classification, when migrating, then the migration refuses with an actionable preflight report and leaves the previous schema/data recoverable.
- **T01.03** — Given a clean database migrated to head, when comparing the EF model, then no unapplied model change remains; changing new snapshots on an issued invoice is refused.

Reuse the named fixture and only add cases needed for these changed behaviors. Retain applicable existing tenant, rollback, immutable-history and precision regressions; do not replace them with response-status-only mocks. UI tests also verify focus/labels and error recovery for the affected actions.

### F. Verification

**NOT RUN — SPEC_ONLY.** Apply the [verification prerequisites](#verification-contract).

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~ModelParityTests|FullyQualifiedName~MigrationUpgradeTests"
```

Additional later check: `dotnet ef migrations has-pending-model-changes --project QtsBilling.Persistence.Postgres --context BillingDbContext`; prerequisite: restored pinned dotnet-ef tool and buildable project. PASS means exit success with no model delta.

**PASS:** the listed tests/checks actually execute and satisfy every expected outcome, applicable host build succeeds, `git diff --check` is clean, and no new secret/server assembly leaks into client assets. Record command, environment, SHA, test count and result. Full suite and packaged-platform proof occur at G7; no pass is claimed here.

### G. Definition of done

Model/snapshot/schema parity is demonstrated on both paths; legal source invoice creation succeeds and posted history is unchanged.

**Recovery/rollback:** Before any live application, take a verified backup. Test Down/Up only on disposable databases; after financial writes use forward repair, never an automatic destructive downgrade.

**Change discipline:** minimal owning-module diff; no unnecessary production dependency; source/contract/schema/UI/tests remain aligned. An unmet test or owner-controlled prerequisite stays explicitly blocked. Specification completion is not implementation completion.

---

<a id="task-02"></a>
## Atomic Task 02 - Repair the existing invoice editor and list regressions

### A. Objective

**P0 · G0 · Requirements R05, R07, R11.** Dependencies: [T01](#task-01).

**Current → target:** The new pages exist, but draft issuing is unreachable; tax percentage, read-only access, mode preservation and asynchronous loading are inconsistent. **Target:** An authorised operator can view, create, edit and issue a correctly calculated draft; readers cannot edit and the editor does not change its economic mode.

**Non-goals:** no unrelated architecture rewrite, new mandatory hosted service, historical repricing or production action. The task is limited to the scope below and the binding decisions in Section 3. Acceptance is the task-specific test outcomes plus G.

### B. Scope

- MODIFY `QtsBilling.Ui/Pages/Invoices/InvoiceEditor.razor` and `QtsBilling.Ui/Pages/Invoices/Invoices.razor`.
- READ-ONLY `QtsBilling.Contracts/Pos/CommercialAmountCalculator.cs` and `QtsBilling.Contracts/Invoices/InvoiceDtos.cs`; use their verified contracts, extending DTOs only under Task 03.
- CREATE `TestQtsBilling/Ui/InvoiceEditorTests.cs`; MODIFY `QtsBilling.Ui.Smoke/Program.cs` for render branches.
- MODIFY `QtsWebApp/QtsWebApp/Program.cs` to register the existing `IInventoryApiClient`/`InventoryApiClient` with the server LocalApiHandler; verify existing browser/MAUI registrations rather than adding duplicates.
- CREATE `TestQtsBilling/Ui/BrowserFixture.cs` and MODIFY `TestQtsBilling/TestQtsBilling.csproj` for the approved test-only browser dependency; the fixture launches an owned loopback host against a marked disposable database, never a production URL.

### C. Verified anchors and NEW insertion points

[S04](#evidence-s04), [S05](#evidence-s05), [S06](#evidence-s06), [S07](#evidence-s07), [S27](#evidence-s27). These refer to the pinned baseline. Existing code is reused; all CREATE paths, new DTOs/routes and generated migration names in this task are **NEW**, not already-implemented capabilities. Read the complete owning method and its callers before changing the anchored slice.

### D. Ordered recipe

1. Verify component activation in server prerender/InteractiveServer, WASM and MAUI. Register the editor’s existing IInventoryApiClient in the server host; the current Program does not register it. Add a host-activation test before diagnosing this as a browser-only rendering problem.
2. Move draft issue action into the draft branch with current issue permission, disabled during save/issue. Load an invoice for any authorised reader; independently disable edit controls for non-managers instead of returning before fetching.
3. Keep form tax in explicit percent units and convert exactly once into the fraction DTO. Reuse the shared calculation for the preview, label it estimated, and display the effective settlement scale. Preserve RecognitionMode and source references on updates; only create Service invoices through this ad-hoc form until a mode-specific flow is available.
4. Clear customer options on context changes, replace the first-200-only selector with server search/cursor loading, and default dates using the business clock. Cancel or sequence list loads so obsolete responses cannot replace new filters. Reset state when route ID or business changes.
5. Keep the posted snapshot read-only. Carry ExpectedVersion for edits and retain entered values on 409 for explicit reload/review. Deletion needs confirmation; printing is routed through the host adapter completed in Task 24.

### E. Focused tests

**Files:** CREATE `TestQtsBilling/Ui/InvoiceEditorTests.cs`; MODIFY `QtsBilling.Ui.Smoke/Program.cs`.

- **T02.01** — Given a draft and issue permission, when opening it, then Issue is visible and posts once; a reader sees details without an editable form.
- **T02.02** — Given a 15 percent input, when saved and reloaded, then the server receives 0.15 and the UI shows 15; totals agree with the shared rounding rules.
- **T02.03** — Given delayed search responses or a route/business switch, when the older request completes, then it cannot overwrite the current list/customer choices.
- **T02.04** — Given an existing non-Service mode, when editing permitted draft terms, then its mode/source cannot silently become Service.
- **T02.05** — Given each hosting mode, when resolving and rendering InvoiceEditor, then all feature clients resolve through the correct authenticated host boundary; no DB client is registered in WASM.

Reuse the named fixture and only add cases needed for these changed behaviors. Retain applicable existing tenant, rollback, immutable-history and precision regressions; do not replace them with response-status-only mocks. UI tests also verify focus/labels and error recovery for the affected actions.

### F. Verification

**NOT RUN — SPEC_ONLY.** Apply the [verification prerequisites](#verification-contract).

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~InvoiceEditorTests"
```

Render smoke: `dotnet run --project QtsBilling.Ui.Smoke/QtsBilling.Ui.Smoke.csproj -c Release`. This does not substitute for browser interaction at Task 45. The NEW browser fixture owns its loopback host and marked PostgreSQL database, uses a preinstalled test browser, records its actual origin, and terminates only its own process. Testing-only HTTP is permitted solely for loopback fixture checks; Production-profile HTTPS and native trust are exercised at Task 45.

**PASS:** the listed tests/checks actually execute and satisfy every expected outcome, applicable host build succeeds, `git diff --check` is clean, and no new secret/server assembly leaks into client assets. Record command, environment, SHA, test count and result. Full suite and packaged-platform proof occur at G7; no pass is claimed here.

### G. Definition of done

Draft issue is reachable, reader/edit permissions are distinct, fraction/percent round-trip is exact and no first-page-only selector prevents choosing a valid customer.

**Recovery/rollback:** Revert the UI patch if necessary; retain the forward schema and all already posted invoices. Never reverse a document automatically because the UI is rolled back.

**Change discipline:** minimal owning-module diff; no unnecessary production dependency; source/contract/schema/UI/tests remain aligned. An unmet test or owner-controlled prerequisite stays explicitly blocked. Specification completion is not implementation completion.

---

<a id="task-03"></a>
## Atomic Task 03 - Complete invoice recognition, original-tax credits and configured posting controls

### A. Objective

**P0 · G1 · Requirements R05, R06, R08.** Dependencies: [T01](#task-01).

**Current → target:** Recognition modes and snapshots exist, but credit calculation explicitly selects Exclusive and the line DTO omits tax composition. **Target:** Each invoice/source lifecycle and every partial credit uses its original tax/cost composition and configured control accounts exactly once.

**Non-goals:** no unrelated architecture rewrite, new mandatory hosted service, historical repricing or production action. The task is limited to the scope below and the binding decisions in Section 3. Acceptance is the task-specific test outcomes plus G.

### B. Scope

- MODIFY `QtsWebApp/QtsWebApp/Features/Invoices/InvoiceEndpoints.cs`, `QtsBilling.Contracts/Invoices/InvoiceDtos.cs`, and `QtsWebApp/QtsWebApp/Features/Orders/FulfillmentService.cs` only for affected recognition bindings.
- READ-ONLY `QtsWebApp/QtsWebApp/Features/Accounting/LedgerWriter.cs`, `QtsWebApp/QtsWebApp/Features/Inventory/InventoryLedger.cs`, `QtsWebApp/QtsWebApp/Features/Sales/SalePosting.cs`, and `QtsBilling.Persistence.Postgres/Entities/Accounting.cs`; reuse, not duplicate.
- MODIFY `TestQtsBilling/Integration/PartialCreditNoteTests.cs`, `TestQtsBilling/Integration/FulfillmentTests.cs`; CREATE `TestQtsBilling/Integration/InvoiceRecognitionTests.cs`.

### C. Verified anchors and NEW insertion points

[S03](#evidence-s03), [S06](#evidence-s06), [S07](#evidence-s07), [S08](#evidence-s08), [S27](#evidence-s27), [E25](#evidence-e25), [E51](#evidence-e51). These refer to the pinned baseline. Existing code is reused; all CREATE paths, new DTOs/routes and generated migration names in this task are **NEW**, not already-implemented capabilities. Read the complete owning method and its callers before changing the anchored slice.

### D. Ordered recipe

1. Expose immutable TaxMode and RevenueBaseAmount in invoice line responses; preserve request wire compatibility while making accepted modes explicit. Replace hardcoded Exclusive credit arithmetic with original snapshot proportions and final-credit residual allocation.
2. Implement the D05 recognition matrix through existing writers. Sale-representation invoice is presentation only; delivery invoice releases previously captured delivered value; Service never consumes stock. For credits choose the original economic source and reject a second reversal of already refunded source quantities. Require an identified active customer for invoices that create an outstanding receivable; anonymous cash documents use the sale-representation path rather than unowned AR.
3. Resolve the business’s configured account IDs and settlement precision. Validate all required account types/activity before posting. Where fixed codes are only seed defaults, leave them alone; where execution ignores saved mappings, replace that lookup within the affected path and add a targeted regression.
4. Require scoped idempotency/aggregate identity for issue, representation and credit; lock the original invoice/source rows and cumulative quantities together. No provider or print I/O occurs in the transaction.

### E. Focused tests

**Files:** CREATE `TestQtsBilling/Integration/InvoiceRecognitionTests.cs`; MODIFY `TestQtsBilling/Integration/PartialCreditNoteTests.cs`; MODIFY `TestQtsBilling/Integration/FulfillmentTests.cs`.

- **T03.01** — Given an inclusive-tax invoice and three partial credits, when the last remaining quantity is credited, then total reversed base/tax/cost equals the original exactly.
- **T03.02** — Given one sale or delivery, when invoice creation is replayed concurrently, then revenue/stock/payment/control balances are recognised once.
- **T03.03** — Given nondefault valid control-account mappings, when service/direct-goods/delivery posting occurs, then journals use those IDs; an inactive/wrong-tenant mapping rejects the whole operation.
- **T03.04** — Given a previously refunded sale representation, when credited through the invoice, then no second physical or financial reversal is possible.

Reuse the named fixture and only add cases needed for these changed behaviors. Retain applicable existing tenant, rollback, immutable-history and precision regressions; do not replace them with response-status-only mocks. UI tests also verify focus/labels and error recovery for the affected actions.

### F. Verification

**NOT RUN — SPEC_ONLY.** Apply the [verification prerequisites](#verification-contract).

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~InvoiceRecognitionTests|FullyQualifiedName~PartialCreditNoteTests|FullyQualifiedName~FulfillmentTests"
```

**PASS:** the listed tests/checks actually execute and satisfy every expected outcome, applicable host build succeeds, `git diff --check` is clean, and no new secret/server assembly leaks into client assets. Record command, environment, SHA, test count and result. Full suite and packaged-platform proof occur at G7; no pass is claimed here.

### G. Definition of done

Per-document and per-entry recognition ties out for all four supported modes; no current catalog repricing, double stock consumption or double credit remains.

**Recovery/rollback:** No repair of already posted incorrect facts is automatic. Produce an exception listing and an approved compensating-entry procedure; preserve the original facts.

**Change discipline:** minimal owning-module diff; no unnecessary production dependency; source/contract/schema/UI/tests remain aligned. An unmet test or owner-controlled prerequisite stays explicitly blocked. Specification completion is not implementation completion.

---

<a id="task-04"></a>
## Atomic Task 04 - Close current tenant, location and device authorization gaps

### A. Objective

**P0 · G1 · Requirements R04, R09, R10.** Dependencies: [T01](#task-01).

**Current → target:** Current membership authorization and composite FKs exist; not every route/feed has proven current location/device scope. **Target:** A complete route-to-permission/scope matrix is enforced server-side for list/detail/write/print/export/sync paths.

**Non-goals:** no unrelated architecture rewrite, new mandatory hosted service, historical repricing or production action. The task is limited to the scope below and the binding decisions in Section 3. Acceptance is the task-specific test outcomes plus G.

### B. Scope

- MODIFY `QtsWebApp/QtsWebApp/Infrastructure/QtsIdentity.cs` and `QtsWebApp/QtsWebApp/Features/Sync/SyncChangeEndpoints.cs`.
- READ-ONLY route registration in `QtsWebApp/QtsWebApp/Program.cs` and each existing Features endpoint; record exact missing call sites in the task diff before changing only those endpoints.
- MODIFY `TestQtsBilling/Integration/BusinessAuthorizationTests.cs`; CREATE `TestQtsBilling/Integration/LocationScopeTests.cs`.

### C. Verified anchors and NEW insertion points

[S01](#evidence-s01), [S02](#evidence-s02), [S16](#evidence-s16), [E05](#evidence-e05), [E10](#evidence-e10), [E04](#evidence-e04). These refer to the pinned baseline. Existing code is reused; all CREATE paths, new DTOs/routes and generated migration names in this task are **NEW**, not already-implemented capabilities. Read the complete owning method and its callers before changing the anchored slice.

### D. Ordered recipe

1. Build an explicit matrix of all mapped routes, current action permission, business ownership, location scope and device requirement. Include print and reports, not only POSTs. Do not infer coverage from a RequireAuthorization attribute.
2. Reuse current membership resolution for projection permissions as well as endpoint admission. Filter list/query results by accessible locations before paging; validate individual references and join keys inside the transaction where state can change.
3. For device requests validate enrollment, status and location binding on every push/pull/grant request; user-held business permissions do not make an arbitrary device trusted. Scope inaccessible detail lookups consistently without revealing foreign data.
4. Add matrix-driven negative tests with two businesses, two locations and role/revocation changes while a session/token remains alive. Preserve composite database FK backstops.

### E. Focused tests

**Files:** MODIFY `TestQtsBilling/Integration/BusinessAuthorizationTests.cs`; CREATE `TestQtsBilling/Integration/LocationScopeTests.cs`.

- **T04.01** — Given an active session whose membership is revoked, when any sensitive route/feed is invoked, then current authority denies it without a refresh requirement.
- **T04.02** — Given a location-limited cashier, when listing or printing another location’s sale/invoice, then no foreign rows or document body are returned.
- **T04.03** — Given a revoked or copied device ID, when pushing/pulling or requesting a grant, then no operation is applied and pending local cash remains recoverable.

Reuse the named fixture and only add cases needed for these changed behaviors. Retain applicable existing tenant, rollback, immutable-history and precision regressions; do not replace them with response-status-only mocks. UI tests also verify focus/labels and error recovery for the affected actions.

### F. Verification

**NOT RUN — SPEC_ONLY.** Apply the [verification prerequisites](#verification-contract).

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~BusinessAuthorizationTests|FullyQualifiedName~LocationScopeTests"
```

**PASS:** the listed tests/checks actually execute and satisfy every expected outcome, applicable host build succeeds, `git diff --check` is clean, and no new secret/server assembly leaks into client assets. Record command, environment, SHA, test count and result. Full suite and packaged-platform proof occur at G7; no pass is claimed here.

### G. Definition of done

Every included route has a documented, tested current-authority boundary; no UI-only or stale-claim-only security decision remains.

**Recovery/rollback:** Fail closed on configuration/scope mismatch. Roll back only the faulty application change, not tenant constraints or membership audit history.

**Change discipline:** minimal owning-module diff; no unnecessary production dependency; source/contract/schema/UI/tests remain aligned. An unmet test or owner-controlled prerequisite stays explicitly blocked. Specification completion is not implementation completion.

---

<a id="task-05"></a>
## Atomic Task 05 - Add operator-only local instance bootstrap

### A. Objective

**P0 · G2 · Requirements R01, R02, R03, R05.** Dependencies: [T01](#task-01), [T03](#task-03), [T04](#task-04).

**Current → target:** Development seeding exists; a safe local production first-owner/business initialisation is not an operator-complete workflow. **Target:** A migrated empty instance can be securely initialised without development mode, an external source, an email server or a cloud account.

**Non-goals:** no unrelated architecture rewrite, new mandatory hosted service, historical repricing or production action. The task is limited to the scope below and the binding decisions in Section 3. Acceptance is the task-specific test outcomes plus G.

### B. Scope

- CREATE `QtsWebApp/QtsWebApp/Infrastructure/LocalBootstrap.cs`; MODIFY `QtsWebApp/QtsWebApp/Program.cs` to dispatch an explicit operator CLI mode before starting the web listener.
- READ-ONLY `QtsWebApp/QtsWebApp/Features/Auth/DevDataSeeder.cs` for reusable constants, not production invocation; use Identity and existing entities/writers.
- CREATE `TestQtsBilling/Integration/LocalBootstrapTests.cs`.
- MODIFY `QtsBilling.Persistence.Postgres/Entities/Core.cs`, `QtsBilling.Persistence.Postgres/BillingDbContext.Model.cs`, and `QtsBilling.Persistence.Postgres/BillingDbContext.cs` for the NEW bootstrap marker; GENERATE the additive `AddLocalBootstrapState` migration/designer/snapshot.

### C. Verified anchors and NEW insertion points

[S01](#evidence-s01), [S02](#evidence-s02), [E58](#evidence-e58), [S18](#evidence-s18), [E25](#evidence-e25). These refer to the pinned baseline. Existing code is reused; all CREATE paths, new DTOs/routes and generated migration names in this task are **NEW**, not already-implemented capabilities. Read the complete owning method and its callers before changing the anchored slice.

### D. Ordered recipe

1. Define NEW CLI mode `--bootstrap-instance` with an explicit instance ID/config path and interactive protected password input; reject secrets in printed command lines and production config containing known demo values. No browser setup endpoint is added.
2. Acquire a bootstrap advisory lock; refuse any pre-existing owner/initialised marker or nonempty financial state. In one transaction create owner, business membership, primary location, base currency/time zone/fiscal settings and validated control chart. Store a bootstrap completion marker/audit with no secrets. Set the supported moving-average method explicitly rather than inheriting the Business entity’s FIFO default.
3. Require selection of NewBusiness with explicit zero-opening acknowledgement or PendingNativeOpening. The latter cannot post until Task 12 supplies approved native openings. Do not invoke the development seeder or silently invent historical balances.
4. On crash roll back the incomplete transaction. A second invocation reports AlreadyInitialised without resetting credentials or changing business settings. Native public-client registration remains idempotent under its own approved configuration.

### E. Focused tests

**Files:** CREATE `TestQtsBilling/Integration/LocalBootstrapTests.cs`.

- **T05.01** — Given a migrated empty production-profile DB and no internet, when bootstrap succeeds, then the owner signs in and completes native setup without a demo credential.
- **T05.02** — Given two simultaneous bootstrap processes, when both attempt creation, then exactly one owner/bootstrap marker exists.
- **T05.03** — Given a populated or wrong-instance DB, when bootstrap runs, then it refuses with no writes; interrupted bootstrap leaves no partial owner/chart.

Reuse the named fixture and only add cases needed for these changed behaviors. Retain applicable existing tenant, rollback, immutable-history and precision regressions; do not replace them with response-status-only mocks. UI tests also verify focus/labels and error recovery for the affected actions.

### F. Verification

**NOT RUN — SPEC_ONLY.** Apply the [verification prerequisites](#verification-contract).

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~LocalBootstrapTests"
```

**PASS:** the listed tests/checks actually execute and satisfy every expected outcome, applicable host build succeeds, `git diff --check` is clean, and no new secret/server assembly leaks into client assets. Record command, environment, SHA, test count and result. Full suite and packaged-platform proof occur at G7; no pass is claimed here.

### G. Definition of done

The blank-instance journey is documented and executable with local resources; bootstrap is closed permanently after success.

**Recovery/rollback:** Do not provide a force-reset bootstrap flag. Recover a failed empty attempt by its transaction; recover an existing instance only through the audited recovery procedure.

**Change discipline:** minimal owning-module diff; no unnecessary production dependency; source/contract/schema/UI/tests remain aligned. An unmet test or owner-controlled prerequisite stays explicitly blocked. Specification completion is not implementation completion.

---

<a id="task-06"></a>
## Atomic Task 06 - Expose business and location setup with financial configuration guards

### A. Objective

**P1 · G2 · Requirements R03, R04, R05, R07.** Dependencies: [T05](#task-05).

**Current → target:** Business/location/settings entities and read endpoints exist, but setup and ongoing administration lack a complete operator-facing API. **Target:** Owners manage business identity, locations, time zone, fiscal/settlement/numbering settings without mutable historical meaning.

**Non-goals:** no unrelated architecture rewrite, new mandatory hosted service, historical repricing or production action. The task is limited to the scope below and the binding decisions in Section 3. Acceptance is the task-specific test outcomes plus G.

### B. Scope

- CREATE `QtsWebApp/QtsWebApp/Features/Administration/BusinessEndpoints.cs` and `QtsBilling.Contracts/Administration/BusinessDtos.cs`.
- MODIFY `QtsWebApp/QtsWebApp/Program.cs` for registration; READ-ONLY `QtsWebApp/QtsWebApp/Features/Accounting/AccountingEndpoints.cs` for existing accounting-settings routes.
- CREATE `TestQtsBilling/Integration/BusinessSetupTests.cs`; any genuinely new persistent settings require a separate additive EF migration in this gate, with exact generated files recorded.

### C. Verified anchors and NEW insertion points

[E10](#evidence-e10), [E25](#evidence-e25), [S21](#evidence-s21), [S03](#evidence-s03). These refer to the pinned baseline. Existing code is reused; all CREATE paths, new DTOs/routes and generated migration names in this task are **NEW**, not already-implemented capabilities. Read the complete owning method and its callers before changing the anchored slice.

### D. Ordered recipe

1. Add NEW owner-scoped `/api/v1/business-settings` GET/PUT and location create/update/deactivate commands under `/api/v1/locations`; reuse existing list route instead of mapping a duplicate.
2. Require ExpectedVersion, action permission, bounded names/address/zone and active control-account mappings. Keep base currency/settlement scale/fiscal identity immutable once posted facts depend on them; changes need a separately designed effective-date transition, not this general PUT.
3. Location deactivation refuses open drawers, unsettled transfers/orders or assigned pending offline authority. Emit audit and change-feed projection in the same transaction. Never delete a referenced location.
4. Expose setup completeness and precise blocking reasons so UI can guide the owner. Defaults may populate a draft configuration but do not imply legal/tax certification.

### E. Focused tests

**Files:** CREATE `TestQtsBilling/Integration/BusinessSetupTests.cs`.

- **T06.01** — Given a new business, when valid local settings and locations are saved, then posting prerequisites become complete and versions advance.
- **T06.02** — Given existing posted facts, when changing currency/precision or deleting a used location, then the command refuses without repricing history.
- **T06.03** — Given foreign account/location references or a stale version, when saving settings, then 403/409/validation is stable and no feed/audit partial write occurs.

Reuse the named fixture and only add cases needed for these changed behaviors. Retain applicable existing tenant, rollback, immutable-history and precision regressions; do not replace them with response-status-only mocks. UI tests also verify focus/labels and error recovery for the affected actions.

### F. Verification

**NOT RUN — SPEC_ONLY.** Apply the [verification prerequisites](#verification-contract).

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~BusinessSetupTests"
```

**PASS:** the listed tests/checks actually execute and satisfy every expected outcome, applicable host build succeeds, `git diff --check` is clean, and no new secret/server assembly leaks into client assets. Record command, environment, SHA, test count and result. Full suite and packaged-platform proof occur at G7; no pass is claimed here.

### G. Definition of done

No normal business/location setup requires SQL; immutable financial configuration boundaries are explicit and tested.

**Recovery/rollback:** Disable new configuration commands on regression; keep already referenced settings/location rows and apply corrections through audited updates.

**Change discipline:** minimal owning-module diff; no unnecessary production dependency; source/contract/schema/UI/tests remain aligned. An unmet test or owner-controlled prerequisite stays explicitly blocked. Specification completion is not implementation completion.

---

<a id="task-07"></a>
## Atomic Task 07 - Implement local user administration and secure setup/recovery tickets

### A. Objective

**P0 · G2 · Requirements R01, R03, R04.** Dependencies: [T05](#task-05), [T04](#task-04).

**Current → target:** Identity verifies passwords, but production reset generates an undelivered token and full membership administration is absent. **Target:** Owners create/disable users, assign scoped memberships and issue safe one-time handoff tickets without SMTP/SMS.

**Non-goals:** no unrelated architecture rewrite, new mandatory hosted service, historical repricing or production action. The task is limited to the scope below and the binding decisions in Section 3. Acceptance is the task-specific test outcomes plus G.

### B. Scope

- CREATE `QtsWebApp/QtsWebApp/Features/Administration/UserEndpoints.cs` and `QtsBilling.Contracts/Administration/UserDtos.cs`; MODIFY `QtsWebApp/QtsWebApp/Features/Auth/AuthEndpoints.cs`.
- MODIFY `QtsWebApp/QtsWebApp/Program.cs` only for route registration. Add NEW setup-ticket metadata to `QtsBilling.Persistence.Postgres/Entities/Core.cs` with model configuration and an additive migration; use Identity token/password APIs.
- CREATE `TestQtsBilling/Integration/UserLifecycleTests.cs`.
- INTEGRATION MODIFY `QtsBilling.Contracts/Identity/IdentityDtos.cs`, `QtsBilling.Contracts/Clients/AccountApiClient.cs`, `QtsWebApp/QtsWebApp.Client/Pages/Login.razor`, and `QtsWebApp/QtsWebApp.Client/Pages/Login.razor.cs` for explicit local-ticket completion; native users complete setup through the same owned-server browser flow.

### C. Verified anchors and NEW insertion points

[S09](#evidence-s09), [S01](#evidence-s01), [E05](#evidence-e05). These refer to the pinned baseline. Existing code is reused; all CREATE paths, new DTOs/routes and generated migration names in this task are **NEW**, not already-implemented capabilities. Read the complete owning method and its callers before changing the anchored slice.

### D. Ordered recipe

1. Add NEW users/memberships list/create/update/disable and owner-issued setup/recovery-ticket endpoints under `/api/v1/administration/users`. Require current users.manage authority, recent reauthentication, ExpectedVersion, reason and tenant-limited target membership.
2. Generate high-entropy single-use tickets with user/business/purpose/expiry binding, retain only a hashed verifier and consumed/revoked metadata. Return the secret once to an authorised owner, no-store; handoff is local print/in person. Never return it from anonymous reset, telemetry or audit.
3. Complete setup using Identity password validation plus atomic consumption. Rate-limit failures; invalidate other tickets/sessions and refresh-token chains on reset/disable. Prevent disabling/demoting the last active owner; prevent a manager assigning authority they do not hold.
4. Replace the misleading production reset-delivery path with a generic acknowledgement plus an explicit local-administrator recovery workflow. Offline emergency owner recovery requires operator-held instance-bound recovery material and a visible audit trail.

### E. Focused tests

**Files:** CREATE `TestQtsBilling/Integration/UserLifecycleTests.cs`.

- **T07.01** — Given a valid issued ticket, when redeemed twice or concurrently, then one password setup succeeds and all subsequent use fails.
- **T07.02** — Given an anonymous reset request for known/unknown accounts, when returned, then responses reveal neither existence nor token; no secret appears in captured logs.
- **T07.03** — Given the last owner or another tenant’s user, when a manager disables or escalates them, then the operation is denied.
- **T07.04** — Given a disabled account with an existing cookie/token, when invoking an API, then current authorization refuses it.

Reuse the named fixture and only add cases needed for these changed behaviors. Retain applicable existing tenant, rollback, immutable-history and precision regressions; do not replace them with response-status-only mocks. UI tests also verify focus/labels and error recovery for the affected actions.

### F. Verification

**NOT RUN — SPEC_ONLY.** Apply the [verification prerequisites](#verification-contract).

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~UserLifecycleTests"
```

**PASS:** the listed tests/checks actually execute and satisfy every expected outcome, applicable host build succeeds, `git diff --check` is clean, and no new secret/server assembly leaks into client assets. Record command, environment, SHA, test count and result. Full suite and packaged-platform proof occur at G7; no pass is claimed here.

### G. Definition of done

Local account provisioning/recovery works without email, raw passwords are never retrievable, and escalation/last-owner safeguards hold.

**Recovery/rollback:** Revoke outstanding new tickets before rolling back the flow. Retain audit metadata and do not reactivate disabled users automatically.

**Change discipline:** minimal owning-module diff; no unnecessary production dependency; source/contract/schema/UI/tests remain aligned. An unmet test or owner-controlled prerequisite stays explicitly blocked. Specification completion is not implementation completion.

---

<a id="task-08"></a>
## Atomic Task 08 - Complete device lifecycle administration without losing pending cash

### A. Objective

**P0 · G2 · Requirements R03, R04, R09.** Dependencies: [T06](#task-06), [T07](#task-07).

**Current → target:** Enrollment challenges and signed grants exist, but operational listing/revocation/reassignment and pending-money retirement are incomplete. **Target:** An owner can enroll, inspect, revoke and retire a terminal with explicit cash custody and key/grant consequences.

**Non-goals:** no unrelated architecture rewrite, new mandatory hosted service, historical repricing or production action. The task is limited to the scope below and the binding decisions in Section 3. Acceptance is the task-specific test outcomes plus G.

### B. Scope

- MODIFY `QtsWebApp/QtsWebApp/Features/Devices/DeviceEndpoints.cs`, `QtsBilling.Contracts/Sync/SyncDeviceDtos.cs`, and `QtsBilling.Maui/Services/DeviceIdentity.cs` only for required lifecycle state.
- READ-ONLY existing `QtsBilling.Contracts/Identity/OfflineGrant.cs` and device registration model; add migration only for missing retirement/custody metadata.
- MODIFY `TestQtsBilling/Integration/DeviceGrantTests.cs`; CREATE `TestQtsBilling/Integration/DeviceLifecycleTests.cs`.

### C. Verified anchors and NEW insertion points

[E04](#evidence-e04), [E32](#evidence-e32), [E47](#evidence-e47), [S23](#evidence-s23). These refer to the pinned baseline. Existing code is reused; all CREATE paths, new DTOs/routes and generated migration names in this task are **NEW**, not already-implemented capabilities. Read the complete owning method and its callers before changing the anchored slice.

### D. Ordered recipe

1. Extend existing `/api/v1/devices` with list/detail/revoke and deliberate reassignment/retirement commands; reuse the one-time enrollment challenge, do not replace it with a static code.
2. Bind device to business/location and grant session. Revocation blocks fresh grants and server writes immediately; mark pending operations for manager review rather than delete or silently accept them.
3. Reassignment requires drain/reconciliation or an explicit unresolved-cash custody record; the old identity/scope cannot be reused to reinterpret stored operations. Preserve queued immutable operation IDs under their original scope.
4. Expose last contact, app/schema version, grant expiry, outstanding operation/cash counts and pending retirement. Destructive local key/data wipe is a separately approved terminal-retirement action after export/reconciliation, not logout.

### E. Focused tests

**Files:** MODIFY `TestQtsBilling/Integration/DeviceGrantTests.cs`; CREATE `TestQtsBilling/Integration/DeviceLifecycleTests.cs`.

- **T08.01** — Given an enrolled terminal, when revoked, then grant renewal/push/pull refuse it while existing local operations remain readable for recovery.
- **T08.02** — Given pending cash and a requested reassignment, when no custody resolution exists, then reassignment is blocked.
- **T08.03** — Given a consumed/expired enrollment challenge or foreign location, when enrolling, then no device is created.

Reuse the named fixture and only add cases needed for these changed behaviors. Retain applicable existing tenant, rollback, immutable-history and precision regressions; do not replace them with response-status-only mocks. UI tests also verify focus/labels and error recovery for the affected actions.

### F. Verification

**NOT RUN — SPEC_ONLY.** Apply the [verification prerequisites](#verification-contract).

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~DeviceGrantTests|FullyQualifiedName~DeviceLifecycleTests"
```

**PASS:** the listed tests/checks actually execute and satisfy every expected outcome, applicable host build succeeds, `git diff --check` is clean, and no new secret/server assembly leaks into client assets. Record command, environment, SHA, test count and result. Full suite and packaged-platform proof occur at G7; no pass is claimed here.

### G. Definition of done

The lifecycle is operable without direct database edits; revocation never masquerades as money deletion.

**Recovery/rollback:** Keep revoked/retired identities and their operation records. Correct bad assignments through a fresh authorised lifecycle event, not identity reuse.

**Change discipline:** minimal owning-module diff; no unnecessary production dependency; source/contract/schema/UI/tests remain aligned. An unmet test or owner-controlled prerequisite stays explicitly blocked. Specification completion is not implementation completion.

---

<a id="task-09"></a>
## Atomic Task 09 - Harden local production sessions, keys and management endpoints

### A. Objective

**P0 · G2 · Requirements R01, R03, R04, R13.** Dependencies: [T04](#task-04), [T07](#task-07).

**Current → target:** Local OIDC exists; production Data Protection persistence is optional, cookies use SameAsRequest and metrics precede authorization. **Target:** Production fails closed on trust/key misconfiguration and all cookie/native/management surfaces have an explicit security boundary.

**Non-goals:** no unrelated architecture rewrite, new mandatory hosted service, historical repricing or production action. The task is limited to the scope below and the binding decisions in Section 3. Acceptance is the task-specific test outcomes plus G.

### B. Scope

- MODIFY `QtsWebApp/QtsWebApp/Program.cs`; CREATE `QtsWebApp/QtsWebApp/Infrastructure/LocalSecurityPolicy.cs`.
- MODIFY `QtsBilling.Contracts/Serialization/ApiHttp.cs` and host HTTP handlers only where needed to send anti-forgery tokens for cookie clients; do not add tokens to native bearer-only calls.
- CREATE `TestQtsBilling/Integration/LocalSecurityPolicyTests.cs`; MODIFY `TestQtsBilling/Integration/NativeAuthorizationTests.cs`.

### C. Verified anchors and NEW insertion points

[S01](#evidence-s01), [S02](#evidence-s02), [S09](#evidence-s09), [E03](#evidence-e03). These refer to the pinned baseline. Existing code is reused; all CREATE paths, new DTOs/routes and generated migration names in this task are **NEW**, not already-implemented capabilities. Read the complete owning method and its callers before changing the anchored slice.

### D. Ordered recipe

1. Validate required owned origin, HTTPS certificate, persistent Data Protection directory, OIDC credentials and grant keys before binding production listeners. Require safe ACLs and no public/demo secrets; do not silently generate ephemeral production keys.
2. Make cookies Secure/HttpOnly and validate explicit anti-forgery tokens for unsafe cookie-authenticated requests. Preserve PKCE and native callback validation. Add same-origin/host checks and a narrow forwarded-header trust list only for a configured local proxy. Add a NEW same-origin GET /api/v1/auth/csrf-token endpoint with no-store response and the standard ASP.NET Core anti-forgery cookie; typed cookie clients send the request token in X-QTS-CSRF. Native bearer-only clients do not fetch or send this cookie token.
3. Move metrics behind a management policy or loopback listener and after exception/correlation handling; no remote collector is required. Use a restrictive resource policy that permits required local Blazor/WebSocket/WebView resources without blanket disabling security headers.
4. Document local key backup/rotation overlap, separate purposes and restart survival. Authentication errors remain typed and redacted. Rate limiting must not leak account existence or be bypassable with arbitrary untrusted headers.

### E. Focused tests

**Files:** CREATE `TestQtsBilling/Integration/LocalSecurityPolicyTests.cs`; MODIFY `TestQtsBilling/Integration/NativeAuthorizationTests.cs`.

- **T09.01** — Given Production with missing keys or an untrusted origin, when starting, then startup refuses before serving business data.
- **T09.02** — Given a cookie mutation without valid anti-forgery proof or from another origin, when invoked, then no effect occurs; valid same-origin and bearer-only native flows succeed.
- **T09.03** — Given an anonymous client, when requesting metrics, then no management data is disclosed; authorised diagnostics remain local.
- **T09.04** — Given a restart with persistent keys, when using a valid session/native refresh flow, then expected validity is preserved without exposing credentials.

Reuse the named fixture and only add cases needed for these changed behaviors. Retain applicable existing tenant, rollback, immutable-history and precision regressions; do not replace them with response-status-only mocks. UI tests also verify focus/labels and error recovery for the affected actions.

### F. Verification

**NOT RUN — SPEC_ONLY.** Apply the [verification prerequisites](#verification-contract).

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~LocalSecurityPolicyTests|FullyQualifiedName~NativeAuthorizationTests"
```

**PASS:** the listed tests/checks actually execute and satisfy every expected outcome, applicable host build succeeds, `git diff --check` is clean, and no new secret/server assembly leaks into client assets. Record command, environment, SHA, test count and result. Full suite and packaged-platform proof occur at G7; no pass is claimed here.

### G. Definition of done

Security is self-hosted, not disabled for convenience; cold production startup, CSRF and metrics boundary tests pass.

**Recovery/rollback:** Keep old decryption/verification keys for the approved overlap. Never roll back by enabling HTTP, disabling validation or deleting the key ring.

**Change discipline:** minimal owning-module diff; no unnecessary production dependency; source/contract/schema/UI/tests remain aligned. An unmet test or owner-controlled prerequisite stays explicitly blocked. Specification completion is not implementation completion.

---

<a id="task-10"></a>
## Atomic Task 10 - Eliminate public-network runtime assets and prove local rendering

### A. Objective

**P1 · G2 · Requirements R01, R11, R15.** Dependencies: [T02](#task-02).

**Current → target:** The web shell fetches Google Fonts; MAUI assets are local but disable zoom and contain malformed error text. **Target:** Cold browser and desktop rendering use owned/local resources only and remain readable with public networking blocked.

**Non-goals:** no unrelated architecture rewrite, new mandatory hosted service, historical repricing or production action. The task is limited to the scope below and the binding decisions in Section 3. Acceptance is the task-specific test outcomes plus G.

### B. Scope

- MODIFY `QtsWebApp/QtsWebApp/Components/App.razor`, `QtsWebApp/QtsWebApp/wwwroot/app.css`, and `QtsBilling.Maui/wwwroot/index.html`.
- READ-ONLY `QtsBilling.Maui/wwwroot/app.css`, shared CSS and document renderers; change only confirmed remote-resource references. Prefer system font stacks rather than adding redistributable font files.
- CREATE `TestQtsBilling/Ui/NoExternalAssetsTests.cs`; CREATE `scripts/verify-runtime-assets.ps1` (source/manifest scan, no network).

### C. Verified anchors and NEW insertion points

[S10](#evidence-s10), [S11](#evidence-s11), [E38](#evidence-e38), [S04](#evidence-s04). These refer to the pinned baseline. Existing code is reused; all CREATE paths, new DTOs/routes and generated migration names in this task are **NEW**, not already-implemented capabilities. Read the complete owning method and its callers before changing the anchored slice.

### D. Ordered recipe

1. Remove the Google Fonts stylesheet and use a consistent system/local font stack. Resolve all UI scripts/styles/icons/help to packaged assets. Explicit user-entered links may remain links, but no automatic fetch/preview of external images is allowed.
2. Correct MAUI error text and permit accessible zoom; test responsive forms at 200% and 400% without hidden actions or clipped money.
3. Scan rendered HTML/CSS/JS and document templates for active external imports. Do not fail on harmless license/documentation links; fail on executable/loadable remote resources.
4. Run cold-cache browser/native navigation with public DNS and egress denied, capturing attempted connections as well as visible success. Include login, POS, invoices, print and error paths.

### E. Focused tests

**Files:** CREATE `TestQtsBilling/Ui/NoExternalAssetsTests.cs`.

- **T10.01** — Given an empty browser cache and no public internet, when loading all core screens, then no external asset request occurs and the interface is usable.
- **T10.02** — Given a remote @import/script/image introduced into a template, when the asset check runs, then it fails with the exact path.
- **T10.03** — Given keyboard navigation and enlarged text, when using invoice/POS actions, then all controls remain reachable and labelled.

Reuse the named fixture and only add cases needed for these changed behaviors. Retain applicable existing tenant, rollback, immutable-history and precision regressions; do not replace them with response-status-only mocks. UI tests also verify focus/labels and error recovery for the affected actions.

### F. Verification

**NOT RUN — SPEC_ONLY.** Apply the [verification prerequisites](#verification-contract).

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~NoExternalAssetsTests"
```

NEW static check, available after this task: `pwsh -NoProfile -File scripts/verify-runtime-assets.ps1`. Runtime network proof is also required at Task 45.

**PASS:** the listed tests/checks actually execute and satisfy every expected outcome, applicable host build succeeds, `git diff --check` is clean, and no new secret/server assembly leaks into client assets. Record command, environment, SHA, test count and result. Full suite and packaged-platform proof occur at G7; no pass is claimed here.

### G. Definition of done

Zero mandatory public resource requests are observed; no unnecessary font dependency is introduced.

**Recovery/rollback:** Revert styling independently of business logic; do not restore the external font URL as a recovery workaround.

**Change discipline:** minimal owning-module diff; no unnecessary production dependency; source/contract/schema/UI/tests remain aligned. An unmet test or owner-controlled prerequisite stays explicitly blocked. Specification completion is not implementation completion.

---

<a id="task-11"></a>
## Atomic Task 11 - Pin and inventory a reproducible offline build dependency set

### A. Objective

**P1 · G2 · Requirements R01, R02, R13.** Dependencies: None.

**Current → target:** Project package versions exist, but SDK/workloads/actions/images float and normal CI acquires tools from public sources. **Target:** A documented build kit restores and builds from an approved local source with complete dependency/license provenance.

**Non-goals:** no unrelated architecture rewrite, new mandatory hosted service, historical repricing or production action. The task is limited to the scope below and the binding decisions in Section 3. Acceptance is the task-specific test outcomes plus G.

### B. Scope

- CREATE `global.json`, `NuGet.Offline.Config`, and `scripts/prepare-build-kit.ps1`; GENERATE project-local `packages.lock.json` files for every retained .csproj after Task 12 retires obsolete projects.
- READ-ONLY `dotnet-tools.json`, all retained .csproj files and `.github/workflows/ci.yml`; pin only demonstrated version gaps, do not upgrade major versions speculatively.
- CREATE `TestQtsBilling/Operations/BuildKit.Tests.ps1`; record dependency and redistribution decisions in NEW `docs/operations/dependencies.md`.

### C. Verified anchors and NEW insertion points

[S25](#evidence-s25), [S14](#evidence-s14), [S15](#evidence-s15), [S18](#evidence-s18), [E35](#evidence-e35). These refer to the pinned baseline. Existing code is reused; all CREATE paths, new DTOs/routes and generated migration names in this task are **NEW**, not already-implemented capabilities. Read the complete owning method and its callers before changing the anchored slice.

### D. Ordered recipe

1. Inventory direct/transitive NuGet and native dependencies by RID, SDK, workload, tools, browser-test binaries and licences. Separate production, development-only and optional operations dependencies. Retain reviewed existing framework/library versions unless a documented compatibility/security issue requires a change.
2. Pin the SDK and workload set, enable locked package restore, and collect required .nupkg/native packs into a local feed with hashes. The offline NuGet config clears public sources; no fallback to an ambient user config/global cache is allowed in acceptance.
3. Provide an authorised connected acquisition script distinct from an offline verify-only mode. Generate a manifest/SBOM-style inventory with sources, hashes, licence obligations, advisory-snapshot date and allowed redistribution. Include existing SQLCipher native binaries and notices; do not substitute cryptography.
4. Prove restore/build with an empty package cache and only the sealed kit. Rebuild on the supported Windows/Linux hosts; signature/timestamp differences are reported rather than claiming bit-for-bit identity without evidence.

### E. Focused tests

**Files:** CREATE `TestQtsBilling/Operations/BuildKit.Tests.ps1`.

- **T11.01** — Given a clean build user with no public network, when restoring from the kit, then all required packages/tools resolve without undeclared sources.
- **T11.02** — Given a changed package hash, missing workload/native runtime or unreviewed licence, when verifying the kit, then the build admission fails.
- **T11.03** — Given the same source/lock manifests, when producing artifacts, then their provenance and actual dependency versions are recorded.

Reuse the named fixture and only add cases needed for these changed behaviors. Retain applicable existing tenant, rollback, immutable-history and precision regressions; do not replace them with response-status-only mocks. UI tests also verify focus/labels and error recovery for the affected actions.

### F. Verification

**NOT RUN — SPEC_ONLY.** Apply the [verification prerequisites](#verification-contract).

```text
pwsh -NoProfile -File TestQtsBilling/Operations/BuildKit.Tests.ps1
```

NEW checks: `pwsh -NoProfile -File TestQtsBilling/Operations/BuildKit.Tests.ps1`; `dotnet restore QtsWebApp/QtsWebApp/QtsWebApp.csproj --locked-mode --configfile NuGet.Offline.Config` after lock files and local feed are installed. These are not executable prerequisites of this review.

**PASS:** the listed tests/checks actually execute and satisfy every expected outcome, applicable host build succeeds, `git diff --check` is clean, and no new secret/server assembly leaks into client assets. Record command, environment, SHA, test count and result. Full suite and packaged-platform proof occur at G7; no pass is claimed here.

### G. Definition of done

The dependency set is explicit and locally obtainable; no claim of zero third-party code or current online vulnerability status is made.

**Recovery/rollback:** Retain the previous sealed kit and manifests. Revoke a compromised kit by its digest and issue a new reviewed set; do not silently refresh packages during install.

**Change discipline:** minimal owning-module diff; no unnecessary production dependency; source/contract/schema/UI/tests remain aligned. An unmet test or owner-controlled prerequisite stays explicitly blocked. Specification completion is not implementation completion.

---

<a id="task-12"></a>
## Atomic Task 12 - Finish native onboarding and retire the source-specific import chain

### A. Objective

**P1 · G3 · Requirements R02, R05, R12, R14.** Dependencies: [T01](#task-01), [T03](#task-03), [T05](#task-05).

**Current → target:** Native package validation/writing exists alongside the source-specific Migration CLI and its tests. **Target:** Only native import remains in the active solution and release; clean installations need no historical source, while real openings reconcile exactly.

**Non-goals:** no unrelated architecture rewrite, new mandatory hosted service, historical repricing or production action. The task is limited to the scope below and the binding decisions in Section 3. Acceptance is the task-specific test outcomes plus G.

### B. Scope

- MODIFY `QtsBilling.DataImport/Program.cs`, `QtsBilling.DataImport/NativeImportWriter.cs`, and `QtsBilling.sln`; READ-ONLY `QtsBilling.DataImport/NativePackageReader.cs` and `QtsBilling.DataImport/NativeImportContracts.cs`.
- MODIFY `TestQtsBilling/DataImport/NativeImportWriterTests.cs`; CREATE `TestQtsBilling/DataImport/NativeReconciliationTests.cs` to retain relevant valuation/replay checks before removal.
- RETIRE in the later authorised change: all tracked files under `QtsBilling.Migration/` and `QtsBilling.Migration.Tests/`, plus their solution/workflow references. Do not delete DB rows, applied migrations, Git history or backups. Record the exact tracked-file deletion list in the PR.
- MODIFY `QtsBilling.Persistence.Postgres/Entities/Platform.cs`, `QtsBilling.Persistence.Postgres/BillingDbContext.cs`, and `QtsBilling.Persistence.Postgres/BillingDbContext.Model.Financial.cs` to retire source-specific runtime mapping; GENERATE `RetireSourceSpecificImportMetadata` forward migration/designer/snapshot, not edits to old migrations.

### C. Verified anchors and NEW insertion points

[S18](#evidence-s18), [E43](#evidence-e43), [E44](#evidence-e44), [E52](#evidence-e52), [S21](#evidence-s21), [S29](#evidence-s29). These refer to the pinned baseline. Existing code is reused; all CREATE paths, new DTOs/routes and generated migration names in this task are **NEW**, not already-implemented capabilities. Read the complete owning method and its callers before changing the anchored slice.

### D. Ordered recipe

1. Inventory callers/tests of the old CLI. Move genuinely useful native reconciliation/valuation assertions to TestQtsBilling before removing the obsolete projects. No adapter that accepts old provider-specific formats is created.
2. Complete the existing native apply report: bind target instance/business, batch, schema/artifact hashes, approved opening date, source manifest and exact control totals. Verify signatures/hashes before any DB write and again before applying staged bytes.
3. Reconcile row counts and per-entry debit/credit, AR/AP open items, inventory quantity/value and mapped control accounts inside the import transaction; fail any unexplained difference or unmapped required row. Rerun the same approved batch deterministically; a changed body under that batch is a conflict.
4. For NewBusiness record a signed zero-opening acknowledgement, not a fabricated migration report. Rewrite active provider-specific docs/examples to native concepts; update commands with no empty-success or SkipImport bypass. Preserve immutable technical history under D10.
5. Remove the unused LegacyIdentifierMap runtime type/DbSet/configuration after native ExternalRecordLink usage is verified. Preserve any populated identity-map evidence by a reviewed forward move/rename into owner-only `audit_archive.import_identity_provenance` with generic source_kind/source_key names, unchanged values and row-count/hash proof. Do not accept an EF-generated DROP TABLE that destroys provenance. Application roles have no write access to that archive; old migration files remain immutable.

### E. Focused tests

**Files:** MODIFY `TestQtsBilling/DataImport/NativeImportWriterTests.cs`; CREATE `TestQtsBilling/DataImport/NativeReconciliationTests.cs`.

- **T12.01** — Given a valid signed native package, when applied twice, then one set of master/opening facts exists and the second result is a replay.
- **T12.02** — Given tampered bytes, path traversal, an unknown field containing credentials or mismatched AR/AP/stock controls, when applying, then no partial financial facts remain.
- **T12.03** — Given a new business with no historical source, when admitted, then explicit zero openings suffice; an existing business cannot use this path to hide balances.

Reuse the named fixture and only add cases needed for these changed behaviors. Retain applicable existing tenant, rollback, immutable-history and precision regressions; do not replace them with response-status-only mocks. UI tests also verify focus/labels and error recovery for the affected actions.

### F. Verification

**NOT RUN — SPEC_ONLY.** Apply the [verification prerequisites](#verification-contract).

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~NativeImportWriterTests|FullyQualifiedName~NativeReconciliationTests|FullyQualifiedName~NativePackageTests"
```

This cleanup gate may touch several project/reference files inseparably. Task 44 must update CI references in the same release candidate; no intermediate broken solution is releasable.

**PASS:** the listed tests/checks actually execute and satisfy every expected outcome, applicable host build succeeds, `git diff --check` is clean, and no new secret/server assembly leaks into client assets. Record command, environment, SHA, test count and result. Full suite and packaged-platform proof occur at G7; no pass is claimed here.

### G. Definition of done

Native import and exact reconciliation are retained; active solution/releases contain no source-specific migration CLI or retired desktop/provider runtime.

**Recovery/rollback:** Removal is a reviewed source change, not authorisation to rewrite history. Keep a verified pre-import database backup; reverse a failed transaction, never repair mismatches by dropping evidence.

**Change discipline:** minimal owning-module diff; no unnecessary production dependency; source/contract/schema/UI/tests remain aligned. An unmet test or owner-controlled prerequisite stays explicitly blocked. Specification completion is not implementation completion.

---

<a id="task-13"></a>
## Atomic Task 13 - Complete the shared application shell and visible business context

### A. Objective

**P1 · G4 · Requirements R03, R04, R07, R11.** Dependencies: [T04](#task-04), [T06](#task-06), [T07](#task-07), [T09](#task-09).

**Current → target:** Navigation is sparse and the web home is a placeholder; business APIs far exceed available management pages. **Target:** Both hosts provide discoverable role-appropriate navigation, session/business/location context and consistent actionable error states.

**Non-goals:** no unrelated architecture rewrite, new mandatory hosted service, historical repricing or production action. The task is limited to the scope below and the binding decisions in Section 3. Acceptance is the task-specific test outcomes plus G.

### B. Scope

- MODIFY `QtsBilling.Ui/Layout/MainLayout.razor`, `QtsBilling.Ui/Layout/NavMenu.razor`, and `QtsWebApp/QtsWebApp.Client/Pages/Home.razor`.
- READ-ONLY `QtsBilling.Maui/Components/Pages/Home.razor` and `QtsBilling.Maui/Services/MauiNavContributor.cs`; expose the same business landing interface without duplicating financial logic.
- CREATE `TestQtsBilling/Ui/ApplicationShellTests.cs`; MODIFY `QtsBilling.Ui.Smoke/Program.cs`.

### C. Verified anchors and NEW insertion points

[E39](#evidence-e39), [E40](#evidence-e40), [E50](#evidence-e50), [E57](#evidence-e57), [S05](#evidence-s05), [E05](#evidence-e05). These refer to the pinned baseline. Existing code is reused; all CREATE paths, new DTOs/routes and generated migration names in this task are **NEW**, not already-implemented capabilities. Read the complete owning method and its callers before changing the anchored slice.

### D. Ordered recipe

1. Group navigation into Sales, Purchasing, Inventory, Finance and Administration. Show only routes shipped by their gate and permitted by the current session; provide clear disabled/unsupported state rather than dead links.
2. Add an always-visible business/location selector using current session/membership APIs. Changing context clears view caches/drafts only after confirmation; it never discards queued cash or alters the scope of an in-flight command.
3. Replace placeholder home with setup completeness, pending work and links; load financial KPIs only through Task 33 reports. Show offline/provisional/sync status distinct from server-posted totals.
4. Centralise presentational loading, empty, 401 reauthentication, 403, 409 review and retry/unknown-outcome states using existing feature interfaces. Keep hosts responsible for assets/render mode. Test keyboard focus and zoom.

### E. Focused tests

**Files:** CREATE `TestQtsBilling/Ui/ApplicationShellTests.cs`; MODIFY `QtsBilling.Ui.Smoke/Program.cs`.

- **T13.01** — Given each existing role, when signing in, then only permitted usable routes are discoverable and direct forbidden routes remain server-denied.
- **T13.02** — Given an in-flight sale or pending offline cash, when switching business, then scope is not changed silently and cash data survives.
- **T13.03** — Given offline/expired-session/server-error states, when navigating, then the UI offers honest recovery without a false success or blank dashboard.

Reuse the named fixture and only add cases needed for these changed behaviors. Retain applicable existing tenant, rollback, immutable-history and precision regressions; do not replace them with response-status-only mocks. UI tests also verify focus/labels and error recovery for the affected actions.

### F. Verification

**NOT RUN — SPEC_ONLY.** Apply the [verification prerequisites](#verification-contract).

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~ApplicationShellTests"
```

**PASS:** the listed tests/checks actually execute and satisfy every expected outcome, applicable host build succeeds, `git diff --check` is clean, and no new secret/server assembly leaks into client assets. Record command, environment, SHA, test count and result. Full suite and packaged-platform proof occur at G7; no pass is claimed here.

### G. Definition of done

A business operator can discover all included modules and always identify the active context; no management workflow starts from a raw GUID.

**Recovery/rollback:** Navigation can be rolled back independently; retain current authorization and pending-operation state.

**Change discipline:** minimal owning-module diff; no unnecessary production dependency; source/contract/schema/UI/tests remain aligned. An unmet test or owner-controlled prerequisite stays explicitly blocked. Specification completion is not implementation completion.

---

<a id="task-14"></a>
## Atomic Task 14 - Add business, user and device administration screens

### A. Objective

**P1 · G4 · Requirements R03, R04, R07.** Dependencies: [T06](#task-06), [T07](#task-07), [T08](#task-08), [T13](#task-13).

**Current → target:** The administration APIs in this plan need usable local workflows, not a requirement to operate them with curl. **Target:** Owners complete setup, manage memberships and handle enrollment/recovery/retirement from the shared UI.

**Non-goals:** no unrelated architecture rewrite, new mandatory hosted service, historical repricing or production action. The task is limited to the scope below and the binding decisions in Section 3. Acceptance is the task-specific test outcomes plus G.

### B. Scope

- CREATE `QtsBilling.Ui/Pages/Administration/BusinessSettings.razor`, `QtsBilling.Ui/Pages/Administration/Users.razor`, and `QtsBilling.Ui/Pages/Administration/Devices.razor` in that directory.
- CREATE `QtsBilling.Contracts/Administration/AdministrationApiClient.cs`; MODIFY registration in `QtsWebApp/QtsWebApp/Program.cs`, `QtsWebApp/QtsWebApp.Client/Program.cs`, `QtsBilling.Maui/MauiProgram.cs` as one cross-host integration gate.
- CREATE `TestQtsBilling/Ui/AdministrationTests.cs`.
- INTEGRATION MODIFY only where needed: `QtsBilling.Ui/Layout/NavMenu.razor`, `QtsWebApp/QtsWebApp/Program.cs`, `QtsWebApp/QtsWebApp.Client/Program.cs`, and `QtsBilling.Maui/MauiProgram.cs` for the named page/client; no unrelated host changes.

### C. Verified anchors and NEW insertion points

[E04](#evidence-e04), [E05](#evidence-e05), [S09](#evidence-s09), [S01](#evidence-s01). These refer to the pinned baseline. Existing code is reused; all CREATE paths, new DTOs/routes and generated migration names in this task are **NEW**, not already-implemented capabilities. Read the complete owning method and its callers before changing the anchored slice.

### D. Ordered recipe

1. Implement validated business/location/settings forms, account-mapping selectors and setup warnings. Read-only/blocked historical settings explain why they cannot be changed.
2. Provide user invite/setup ticket, password recovery, scoped role/location assignments and deactivate/reactivate actions with current-permission checks and recent reauthentication. Show a handoff secret once; no copy of it remains in persistent UI state or browser storage.
3. Provide device enrollment challenge/expiry, current scope, revoke and retirement confirmation. Display pending cash and recovery prerequisites; do not offer a routine wipe button.
4. Wire the typed client in all three hosts, support server validation/409s, and update navigation only when the corresponding API/task gate is complete.

### E. Focused tests

**Files:** CREATE `TestQtsBilling/Ui/AdministrationTests.cs`.

- **T14.01** — Given an owner on a disconnected LAN, when creating a cashier and handing over a setup ticket, then that cashier sets a password and signs in without email.
- **T14.02** — Given a manager lacking users.manage, when opening administration directly, then neither sensitive details nor writes are available.
- **T14.03** — Given a device with unresolved money, when selecting retire/reassign, then the UI explains and enforces the required custody resolution.

Reuse the named fixture and only add cases needed for these changed behaviors. Retain applicable existing tenant, rollback, immutable-history and precision regressions; do not replace them with response-status-only mocks. UI tests also verify focus/labels and error recovery for the affected actions.

### F. Verification

**NOT RUN — SPEC_ONLY.** Apply the [verification prerequisites](#verification-contract).

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~AdministrationTests"
```

**PASS:** the listed tests/checks actually execute and satisfy every expected outcome, applicable host build succeeds, `git diff --check` is clean, and no new secret/server assembly leaks into client assets. Record command, environment, SHA, test count and result. Full suite and packaged-platform proof occur at G7; no pass is claimed here.

### G. Definition of done

Business setup, user recovery and device lifecycle can be completed without database tools; ticket secrecy and last-owner protections are visible and verified.

**Recovery/rollback:** Hide broken administration routes rather than relaxing authorization; issued tickets may be revoked and reissued with audit.

**Change discipline:** minimal owning-module diff; no unnecessary production dependency; source/contract/schema/UI/tests remain aligned. An unmet test or owner-controlled prerequisite stays explicitly blocked. Specification completion is not implementation completion.

---

<a id="task-15"></a>
## Atomic Task 15 - Finish customer and supplier workbenches with real paging

### A. Objective

**P1 · G4 · Requirements R04, R07, R08.** Dependencies: [T13](#task-13).

**Current → target:** Customer CRUD UI exists; suppliers are API-only and customer paging/search must not depend on a first-page subset. **Target:** Operators manage/search/deactivate parties and reach their statements and transactions without losing history.

**Non-goals:** no unrelated architecture rewrite, new mandatory hosted service, historical repricing or production action. The task is limited to the scope below and the binding decisions in Section 3. Acceptance is the task-specific test outcomes plus G.

### B. Scope

- MODIFY `QtsBilling.Ui/Pages/Customers.razor` and `QtsBilling.Ui/Pages/Customers.razor.cs`; CREATE `QtsBilling.Ui/Pages/Suppliers.razor`.
- CREATE `QtsBilling.Contracts/Purchasing/SupplierApiClient.cs`; register in each host using the existing HTTP/auth convention.
- CREATE `TestQtsBilling/Ui/PartyWorkbenchTests.cs`; READ-ONLY customer/supplier endpoints for existing validation and version behavior.
- INTEGRATION MODIFY only where needed: `QtsBilling.Ui/Layout/NavMenu.razor`, `QtsWebApp/QtsWebApp/Program.cs`, `QtsWebApp/QtsWebApp.Client/Program.cs`, and `QtsBilling.Maui/MauiProgram.cs` for the named page/client; no unrelated host changes.

### C. Verified anchors and NEW insertion points

[E06](#evidence-e06), [E22](#evidence-e22), [E40](#evidence-e40), [E21](#evidence-e21). These refer to the pinned baseline. Existing code is reused; all CREATE paths, new DTOs/routes and generated migration names in this task are **NEW**, not already-implemented capabilities. Read the complete owning method and its callers before changing the anchored slice.

### D. Ordered recipe

1. Use server cursors/search and cancel stale requests; distinguish no matching results from transport errors. Load arbitrary valid parties through bounded selectors rather than fetch-all or first-100/200 lists.
2. Expose existing required fields plus credit/terms settings only where actually supported by contracts; add a narrow versioned DTO/API field change if needed, with scoped validation and explicit path recorded.
3. Keep deactivation non-destructive; show historical balances/links to Task 25/27 statements when available. Block duplicate business keys deterministically and present stale edits for review.
4. Reuse customer dialogs/styles instead of building a generic CRUD framework. Provide keyboard-friendly forms and explicit permissions in both hosts.

### E. Focused tests

**Files:** CREATE `TestQtsBilling/Ui/PartyWorkbenchTests.cs`.

- **T15.01** — Given more records than one page, when searching/paging/editing the last record, then it remains reachable and no duplicate/missing rows are introduced.
- **T15.02** — Given a referenced party, when deactivated, then historical documents still render its snapshots and new use is rejected according to policy.
- **T15.03** — Given simultaneous edits or a foreign party ID, when saving, then the UI preserves input and reports a conflict/denial rather than overwriting another change.

Reuse the named fixture and only add cases needed for these changed behaviors. Retain applicable existing tenant, rollback, immutable-history and precision regressions; do not replace them with response-status-only mocks. UI tests also verify focus/labels and error recovery for the affected actions.

### F. Verification

**NOT RUN — SPEC_ONLY.** Apply the [verification prerequisites](#verification-contract).

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~PartyWorkbenchTests"
```

**PASS:** the listed tests/checks actually execute and satisfy every expected outcome, applicable host build succeeds, `git diff --check` is clean, and no new secret/server assembly leaks into client assets. Record command, environment, SHA, test count and result. Full suite and packaged-platform proof occur at G7; no pass is claimed here.

### G. Definition of done

Customer/supplier maintenance and transaction navigation are usable at realistic catalog sizes without deleting historical references.

**Recovery/rollback:** Revert presentation safely; retain deactivated records and versions, not hard deletes.

**Change discipline:** minimal owning-module diff; no unnecessary production dependency; source/contract/schema/UI/tests remain aligned. An unmet test or owner-controlled prerequisite stays explicitly blocked. Specification completion is not implementation completion.

---

<a id="task-16"></a>
## Atomic Task 16 - Expose master-data, tax and discount configuration in the shared UI

### A. Objective

**P1 · G4 · Requirements R05, R07.** Dependencies: [T13](#task-13).

**Current → target:** Units, categories, brands, groups, taxes and discounts have scoped APIs; management screens are missing. **Target:** The owner/catalog operator can maintain these records with clear exact-value and effective-window rules.

**Non-goals:** no unrelated architecture rewrite, new mandatory hosted service, historical repricing or production action. The task is limited to the scope below and the binding decisions in Section 3. Acceptance is the task-specific test outcomes plus G.

### B. Scope

- CREATE `QtsBilling.Ui/Pages/Catalog/MasterData.razor`, `QtsBilling.Ui/Pages/Pricing/Taxes.razor`, and `QtsBilling.Ui/Pages/Pricing/Discounts.razor`.
- CREATE `QtsBilling.Contracts/Catalog/MasterDataApiClient.cs` and `QtsBilling.Contracts/Pricing/PricingApiClient.cs`; register in all hosts.
- CREATE `TestQtsBilling/Ui/MasterDataWorkbenchTests.cs`; READ-ONLY `QtsWebApp/QtsWebApp/Features/Catalog/MasterDataEndpoints.cs` and `QtsWebApp/QtsWebApp/Features/Pricing/PricingEndpoints.cs` under the server.
- INTEGRATION MODIFY only where needed: `QtsBilling.Ui/Layout/NavMenu.razor`, `QtsWebApp/QtsWebApp/Program.cs`, `QtsWebApp/QtsWebApp.Client/Program.cs`, and `QtsBilling.Maui/MauiProgram.cs` for the named page/client; no unrelated host changes.

### C. Verified anchors and NEW insertion points

[E08](#evidence-e08), [E09](#evidence-e09), [S27](#evidence-s27). These refer to the pinned baseline. Existing code is reused; all CREATE paths, new DTOs/routes and generated migration names in this task are **NEW**, not already-implemented capabilities. Read the complete owning method and its callers before changing the anchored slice.

### D. Ordered recipe

1. Render category hierarchy with cycle/parent validation, brand/group lists and existing unit decimal rules. Do not add arbitrary conversion/bundle engines as part of this task.
2. Tax forms show percentage but send decimal fractions, inclusive/exclusive semantics and effective business settings. Default-tax changes stay transactional; posted line snapshots never change.
3. Discount forms expose the supported fixed/percentage/value/window scope only. Use UTC persistence and explicit business-zone display, enforce limits and show whether a definition is active; do not invent promotion stacking.
4. Provide bounded search/paging, deactivate-with-reference explanation and exact server validation. Changes emit existing audit/feed behavior; verify that the UI can see the committed result after a reload.

### E. Focused tests

**Files:** CREATE `TestQtsBilling/Ui/MasterDataWorkbenchTests.cs`.

- **T16.01** — Given a 15% tax, when created and selected, then it stores 0.15 and historical invoices remain unchanged after a later edit.
- **T16.02** — Given a category cycle, invalid discount interval or excessive percentage, when saving, then the server refuses and field errors are shown.
- **T16.03** — Given a role without pricing permission, when loading or posting a pricing change, then it is denied regardless of hidden controls.

Reuse the named fixture and only add cases needed for these changed behaviors. Retain applicable existing tenant, rollback, immutable-history and precision regressions; do not replace them with response-status-only mocks. UI tests also verify focus/labels and error recovery for the affected actions.

### F. Verification

**NOT RUN — SPEC_ONLY.** Apply the [verification prerequisites](#verification-contract).

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~MasterDataWorkbenchTests"
```

**PASS:** the listed tests/checks actually execute and satisfy every expected outcome, applicable host build succeeds, `git diff --check` is clean, and no new secret/server assembly leaks into client assets. Record command, environment, SHA, test count and result. Full suite and packaged-platform proof occur at G7; no pass is claimed here.

### G. Definition of done

All retained master/pricing concepts have a usable and exact operator configuration path, without speculative pricing machinery.

**Recovery/rollback:** Deactivate mistaken definitions and correct future configuration; never recalculate posted documents.

**Change discipline:** minimal owning-module diff; no unnecessary production dependency; source/contract/schema/UI/tests remain aligned. An unmet test or owner-controlled prerequisite stays explicitly blocked. Specification completion is not implementation completion.

---

<a id="task-17"></a>
## Atomic Task 17 - Complete product and variant maintenance APIs and concurrency

### A. Objective

**P1 · G4 · Requirements R04, R05, R06, R07.** Dependencies: [T01](#task-01), [T04](#task-04), [T16](#task-16).

**Current → target:** Product creation supplies a default variant and update routes exist; additional-variant creation and a complete archive lifecycle need completion. **Target:** Simple products, explicit variants and non-stock services have bounded, versioned tenant-safe maintenance routes.

**Non-goals:** no unrelated architecture rewrite, new mandatory hosted service, historical repricing or production action. The task is limited to the scope below and the binding decisions in Section 3. Acceptance is the task-specific test outcomes plus G.

### B. Scope

- MODIFY `QtsWebApp/QtsWebApp/Features/Catalog/ProductEndpoints.cs`, `QtsBilling.Contracts/Catalog/ProductDtos.cs`, and `QtsBilling.Contracts/Catalog/ProductApiClient.cs`.
- READ-ONLY `QtsBilling.Persistence.Postgres/Entities/Catalog.cs` and `QtsBilling.Persistence.Postgres/BillingDbContext.Model.cs`; add an additive migration only for a demonstrated missing invariant.
- CREATE `TestQtsBilling/Integration/ProductLifecycleTests.cs`.

### C. Verified anchors and NEW insertion points

[E07](#evidence-e07), [E08](#evidence-e08), [S26](#evidence-s26). These refer to the pinned baseline. Existing code is reused; all CREATE paths, new DTOs/routes and generated migration names in this task are **NEW**, not already-implemented capabilities. Read the complete owning method and its callers before changing the anchored slice.

### D. Ordered recipe

1. Add NEW variant-create and explicit product/variant deactivate operations using current IDs and business scope; preserve existing default-variant creation. Validate SKU/barcode uniqueness with database backstops and exactly one active default where required.
2. Carry ExpectedVersion or existing If-Match semantics consistently for edits. Validate category/unit/brand/group/tax references and active status; prevent changing stock/unit semantics after movements without a separately controlled conversion.
3. Do not expose inventory quantity as an editable product field. Use existing adjustments/counts/openings; selling price changes affect future documents/cache revisions only.
4. Emit catalog projection/tombstone and audit in the same transaction. Ensure ambiguous retries of variant creation cannot create duplicate variants; use the existing command receipt pattern.

### E. Focused tests

**Files:** CREATE `TestQtsBilling/Integration/ProductLifecycleTests.cs`.

- **T17.01** — Given an existing product, when two variants with the same SKU/barcode are created concurrently, then exactly one succeeds.
- **T17.02** — Given a stale edit or foreign master reference, when saving, then no product/feed partial change occurs.
- **T17.03** — Given historical stock/documents, when changing units or archiving, then protected semantics/history remain intact and future selection follows active status.

Reuse the named fixture and only add cases needed for these changed behaviors. Retain applicable existing tenant, rollback, immutable-history and precision regressions; do not replace them with response-status-only mocks. UI tests also verify focus/labels and error recovery for the affected actions.

### F. Verification

**NOT RUN — SPEC_ONLY.** Apply the [verification prerequisites](#verification-contract).

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~ProductLifecycleTests"
```

**PASS:** the listed tests/checks actually execute and satisfy every expected outcome, applicable host build succeeds, `git diff --check` is clean, and no new secret/server assembly leaks into client assets. Record command, environment, SHA, test count and result. Full suite and packaged-platform proof occur at G7; no pass is claimed here.

### G. Definition of done

Supported catalog lifecycle is complete through API with versions, uniqueness and no mutable stock authority.

**Recovery/rollback:** Keep referenced variants and deactivate rather than remove them. Roll back new endpoints without rewriting old transaction snapshots.

**Change discipline:** minimal owning-module diff; no unnecessary production dependency; source/contract/schema/UI/tests remain aligned. An unmet test or owner-controlled prerequisite stays explicitly blocked. Specification completion is not implementation completion.

---

<a id="task-18"></a>
## Atomic Task 18 - Build the product and variant workbench

### A. Objective

**P1 · G4 · Requirements R07, R11.** Dependencies: [T17](#task-17), [T16](#task-16).

**Current → target:** The typed catalog exists but a business operator cannot complete catalog maintenance from current navigation. **Target:** Users search, create, edit and archive products/variants and inspect stock through dedicated shared screens.

**Non-goals:** no unrelated architecture rewrite, new mandatory hosted service, historical repricing or production action. The task is limited to the scope below and the binding decisions in Section 3. Acceptance is the task-specific test outcomes plus G.

### B. Scope

- CREATE `QtsBilling.Ui/Pages/Catalog/Products.razor` and `QtsBilling.Ui/Pages/Catalog/ProductEditor.razor`; MODIFY `QtsBilling.Ui/Layout/NavMenu.razor`.
- READ-ONLY `QtsBilling.Contracts/Catalog/ProductApiClient.cs`; register already-created clients where missing in host startup only.
- CREATE `TestQtsBilling/Ui/ProductWorkbenchTests.cs`.
- INTEGRATION MODIFY only where needed: `QtsBilling.Ui/Layout/NavMenu.razor`, `QtsWebApp/QtsWebApp/Program.cs`, `QtsWebApp/QtsWebApp.Client/Program.cs`, and `QtsBilling.Maui/MauiProgram.cs` for the named page/client; no unrelated host changes.

### C. Verified anchors and NEW insertion points

[E07](#evidence-e07), [E38](#evidence-e38), [E08](#evidence-e08). These refer to the pinned baseline. Existing code is reused; all CREATE paths, new DTOs/routes and generated migration names in this task are **NEW**, not already-implemented capabilities. Read the complete owning method and its callers before changing the anchored slice.

### D. Ordered recipe

1. Provide server-side search/filter/cursor paging by name/SKU/barcode/status and a complete editor for supported stock/service variants. Select master references by name with bounded searches, not hidden IDs.
2. Show unit, tax mode, sale price, costing method and active status clearly; price preview uses shared calculation. Read stock by location but route stock corrections to stock-control workflows.
3. Handle required/invalid/duplicate/stale state inline; preserve input after a 409, then require review/reload. Confirm archive and disclose references/remaining stock.
4. Support keyboard barcode entry with no camera/cloud dependency. Make successful mutations refresh relevant selectors and show the committed version.

### E. Focused tests

**Files:** CREATE `TestQtsBilling/Ui/ProductWorkbenchTests.cs`.

- **T18.01** — Given a new stock item and service item, when created through the UI, then both are selectable in their permitted sales/invoice flows.
- **T18.02** — Given duplicate barcode or concurrent edit, when saving, then the operator sees a specific recoverable error without lost input.
- **T18.03** — Given a large catalog and keyboard-only navigation, when searching and editing, then bounded requests and correct focus are maintained.

Reuse the named fixture and only add cases needed for these changed behaviors. Retain applicable existing tenant, rollback, immutable-history and precision regressions; do not replace them with response-status-only mocks. UI tests also verify focus/labels and error recovery for the affected actions.

### F. Verification

**NOT RUN — SPEC_ONLY.** Apply the [verification prerequisites](#verification-contract).

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~ProductWorkbenchTests"
```

**PASS:** the listed tests/checks actually execute and satisfy every expected outcome, applicable host build succeeds, `git diff --check` is clean, and no new secret/server assembly leaks into client assets. Record command, environment, SHA, test count and result. Full suite and packaged-platform proof occur at G7; no pass is claimed here.

### G. Definition of done

Catalog maintenance is practical on both hosts and never edits authoritative on-hand quantity directly.

**Recovery/rollback:** Hide a faulty editor while retaining API/history; no catalog reset or bulk reimport is a rollback strategy.

**Change discipline:** minimal owning-module diff; no unnecessary production dependency; source/contract/schema/UI/tests remain aligned. An unmet test or owner-controlled prerequisite stays explicitly blocked. Specification completion is not implementation completion.

---

<a id="task-19"></a>
## Atomic Task 19 - Complete register custody, pending-cash close rules and register UI

### A. Objective

**P0 · G4 · Requirements R05, R06, R07, R09.** Dependencies: [T03](#task-03), [T04](#task-04), [T08](#task-08).

**Current → target:** Register/session APIs exist; physical offline cash and closing custody need an integrated policy and usable management screen. **Target:** Every cash receipt/refund belongs to known custody, closing cannot hide unresolved money, and operators can run drawers without API tools.

**Non-goals:** no unrelated architecture rewrite, new mandatory hosted service, historical repricing or production action. The task is limited to the scope below and the binding decisions in Section 3. Acceptance is the task-specific test outcomes plus G.

### B. Scope

- MODIFY `QtsWebApp/QtsWebApp/Features/Sales/CashRegisterEndpoints.cs` and `QtsBilling.Contracts/Sales/PosCashDtos.cs`; CREATE `QtsBilling.Ui/Pages/Sales/Registers.razor`.
- READ-ONLY `QtsBilling.Persistence.Postgres/Entities/Sales.cs`; add NEW custody/carry-forward records and additive model migration only for the D07 states not representable today.
- CREATE `TestQtsBilling/Integration/CashCustodyTests.cs` and `TestQtsBilling/Ui/RegisterWorkbenchTests.cs`.

### C. Verified anchors and NEW insertion points

[E14](#evidence-e14), [E33](#evidence-e33), [E47](#evidence-e47), [E27](#evidence-e27). These refer to the pinned baseline. Existing code is reused; all CREATE paths, new DTOs/routes and generated migration names in this task are **NEW**, not already-implemented capabilities. Read the complete owning method and its callers before changing the anchored slice.

### D. Ordered recipe

1. Expose open/current/history/cash-in/cash-out/close in one shared drawer workbench. Validate current location/role, reason, currency and session ownership.
2. Require idempotency for open/movement/close commands; lock session and cash-control records consistently with sale/refund paths. Compute expected posted cash from immutable movements; never accept a client-supplied expected total.
3. Track device provisional custody separately. Final close requires confirmed device drain/reconciliation, or an authorised carry-forward document naming each unresolved operation/amount and receiving custodian. Missing last-contact evidence is not proof of zero pending cash.
4. Post a reviewed cash over/short journal only for actual counted discrepancy, using configured accounts and the current open period. Never net rejected offline sales out of the discrepancy silently.

### E. Focused tests

**Files:** CREATE `TestQtsBilling/Integration/CashCustodyTests.cs`; CREATE `TestQtsBilling/Ui/RegisterWorkbenchTests.cs`.

- **T19.01** — Given a session and simultaneous sale/close, when racing, then either the sale is included or refused, never lost from expected cash.
- **T19.02** — Given unresolved offline cash or an unreachable assigned device, when closing, then final settlement blocks or requires an explicit audited carry-forward.
- **T19.03** — Given duplicate close/movement requests, when replayed, then drawer and journals change once; a changed body under the key conflicts.

Reuse the named fixture and only add cases needed for these changed behaviors. Retain applicable existing tenant, rollback, immutable-history and precision regressions; do not replace them with response-status-only mocks. UI tests also verify focus/labels and error recovery for the affected actions.

### F. Verification

**NOT RUN — SPEC_ONLY.** Apply the [verification prerequisites](#verification-contract).

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~CashCustodyTests|FullyQualifiedName~RegisterWorkbenchTests"
```

**PASS:** the listed tests/checks actually execute and satisfy every expected outcome, applicable host build succeeds, `git diff --check` is clean, and no new secret/server assembly leaks into client assets. Record command, environment, SHA, test count and result. Full suite and packaged-platform proof occur at G7; no pass is claimed here.

### G. Definition of done

Physical cash, posted cash and provisional custody can be reconciled without hiding offline liabilities; drawer operations are usable.

**Recovery/rollback:** Never reopen a settled session by editing status. Correct with a new audited session/custody transfer and linked accounting adjustment.

**Change discipline:** minimal owning-module diff; no unnecessary production dependency; source/contract/schema/UI/tests remain aligned. An unmet test or owner-controlled prerequisite stays explicitly blocked. Specification completion is not implementation completion.

---

<a id="task-20"></a>
## Atomic Task 20 - Finish web and desktop checkout with stable unknown-outcome recovery

### A. Objective

**P0 · G4 · Requirements R01, R05, R06, R07, R09.** Dependencies: [T02](#task-02), [T18](#task-18), [T19](#task-19).

**Current → target:** Shared online/offline POS and typed orchestration exist; completion requires operator recovery, current context and the no-provider core boundary. **Target:** Cash checkout is a complete repeat-safe workflow; credit and manually cleared methods remain truthful; card acquiring is explicitly unavailable in LocalCore.

**Non-goals:** no unrelated architecture rewrite, new mandatory hosted service, historical repricing or production action. The task is limited to the scope below and the binding decisions in Section 3. Acceptance is the task-specific test outcomes plus G.

### B. Scope

- MODIFY `QtsBilling.Ui/Pages/Pos.razor`, `QtsBilling.Ui/Pages/Pos.razor.cs`, and `QtsWebApp/QtsWebApp.Client/Services/WebPosCheckoutOrchestrator.cs`.
- READ-ONLY `QtsBilling.Maui/Services/DeviceLocalPosService.cs`, `QtsWebApp/QtsWebApp/Features/Sales/SalePosting.cs`, and `QtsWebApp/QtsWebApp/Features/Sales/PaymentTerminalEndpoints.cs`; patch only an evidenced integration gap, not the shared posting engine wholesale.
- MODIFY `TestQtsBilling/Contracts/WebPosCheckoutOrchestratorTests.cs`; CREATE `TestQtsBilling/Ui/CheckoutJourneyTests.cs`.

### C. Verified anchors and NEW insertion points

[E38](#evidence-e38), [E13](#evidence-e13), [E14](#evidence-e14), [E17](#evidence-e17), [E33](#evidence-e33), [S27](#evidence-s27). These refer to the pinned baseline. Existing code is reused; all CREATE paths, new DTOs/routes and generated migration names in this task are **NEW**, not already-implemented capabilities. Read the complete owning method and its callers before changing the anchored slice.

### D. Ordered recipe

1. Use current business/location/open drawer, live or explicitly cached catalog, exact server-validated prices and permitted discount limits. Show provisional vs posted status at all times and use a new operation ID only for a genuinely new sale.
2. Persist a pending online-attempt envelope safely for page refresh/restart without tokens or PAN; retain exact key/body until resolved. On timeout show “outcome unknown”, allow only same-attempt retry/status resolution, and prevent a second payment for the same attempt.
3. Reuse immutable server receipt/line snapshots after success. Cash tender/change is exact at business settlement scale. Customer credit and cheque/transfer require the existing non-confirmed settlement semantics, never a mocked successful payment.
4. LocalCore hides acquiring controls and rejects direct card-intent/capture requests with PAYMENT_METHOD_DISABLED; no PSP setup is required to finish the project. Optional future adapters cannot weaken this core test.

### E. Focused tests

**Files:** MODIFY `TestQtsBilling/Contracts/WebPosCheckoutOrchestratorTests.cs`; CREATE `TestQtsBilling/Ui/CheckoutJourneyTests.cs`.

- **T20.01** — Given a committed sale whose response is lost, when the user reloads/retries, then the same sale/number/payment/stock/journal is returned exactly once.
- **T20.02** — Given a price change, insufficient stock or unauthorised discount, when checkout posts, then nothing commits and the operator must review explicitly.
- **T20.03** — Given no public internet but a local server, when completing a cash sale, then receipt and stock update work; with server down only entitled desktop provisional capture is enabled.
- **T20.04** — Given a forged card reference in LocalCore, when submitted directly, then no confirmed payment or sale is created.

Reuse the named fixture and only add cases needed for these changed behaviors. Retain applicable existing tenant, rollback, immutable-history and precision regressions; do not replace them with response-status-only mocks. UI tests also verify focus/labels and error recovery for the affected actions.

### F. Verification

**NOT RUN — SPEC_ONLY.** Apply the [verification prerequisites](#verification-contract).

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~WebPosCheckoutOrchestratorTests|FullyQualifiedName~CheckoutJourneyTests"
```

**PASS:** the listed tests/checks actually execute and satisfy every expected outcome, applicable host build succeeds, `git diff --check` is clean, and no new secret/server assembly leaks into client assets. Record command, environment, SHA, test count and result. Full suite and packaged-platform proof occur at G7; no pass is claimed here.

### G. Definition of done

Checkout handles success, rejection and unknown outcomes without duplicate money or a compulsory third-party service.

**Recovery/rollback:** Retain unresolved attempts across an application rollback; resolve them against the authoritative server rather than clearing browser/device storage.

**Change discipline:** minimal owning-module diff; no unnecessary production dependency; source/contract/schema/UI/tests remain aligned. An unmet test or owner-controlled prerequisite stays explicitly blocked. Specification completion is not implementation completion.

---

<a id="task-21"></a>
## Atomic Task 21 - Add sales history and controlled partial-refund workflows

### A. Objective

**P1 · G4 · Requirements R05, R06, R07, R08.** Dependencies: [T19](#task-19), [T20](#task-20).

**Current → target:** Sale/receipt/partial-refund endpoints exist but there is no complete operator history/refund workbench. **Target:** Cashiers/managers can find a posted sale, reprint its original receipt and process permitted partial refunds with visible stock/cash effects.

**Non-goals:** no unrelated architecture rewrite, new mandatory hosted service, historical repricing or production action. The task is limited to the scope below and the binding decisions in Section 3. Acceptance is the task-specific test outcomes plus G.

### B. Scope

- CREATE `QtsBilling.Ui/Pages/Sales/Sales.razor` and `QtsBilling.Ui/Pages/Sales/SaleDetail.razor`; MODIFY `QtsBilling.Contracts/Sales/SaleApiClient.cs` only for missing typed refund/history calls.
- READ-ONLY `QtsWebApp/QtsWebApp/Features/Sales/RefundEndpoints.cs` and `QtsWebApp/QtsWebApp/Features/Sales/SaleEndpoints.cs`; reuse current cumulative refund protections.
- CREATE `TestQtsBilling/Ui/SalesRefundJourneyTests.cs`; MODIFY `TestQtsBilling/Integration/PartialRefundTests.cs` for any newly exposed edge.
- INTEGRATION MODIFY only where needed: `QtsBilling.Ui/Layout/NavMenu.razor`, `QtsWebApp/QtsWebApp/Program.cs`, `QtsWebApp/QtsWebApp.Client/Program.cs`, and `QtsBilling.Maui/MauiProgram.cs` for the named page/client; no unrelated host changes.

### C. Verified anchors and NEW insertion points

[E13](#evidence-e13), [E15](#evidence-e15), [S27](#evidence-s27), [E14](#evidence-e14). These refer to the pinned baseline. Existing code is reused; all CREATE paths, new DTOs/routes and generated migration names in this task are **NEW**, not already-implemented capabilities. Read the complete owning method and its callers before changing the anchored slice.

### D. Ordered recipe

1. Provide bounded search by number/date/customer/location with detail snapshots, payment state and refund history. Never display a provisional local capture as a posted server sale.
2. Expose remaining refundable line quantity and explicit restock/damaged-write-off dispositions, reason and eligible drawer. The server is authoritative for remaining amounts and original cost; client totals are previews.
3. Use one stable key/body per refund attempt and require explicit confirmation of cash payout. Coordinate duplicate/refund-versus-credit source protection from Task 03.
4. Keep receipt reprints immutable and mark copies/refunded state without changing original prices/tax. Denied, insufficient drawer cash, closed period and stale cumulative-quantity outcomes are actionable.

### E. Focused tests

**Files:** CREATE `TestQtsBilling/Ui/SalesRefundJourneyTests.cs`; MODIFY `TestQtsBilling/Integration/PartialRefundTests.cs`.

- **T21.01** — Given a sale already partially refunded, when refunding the remainder, then cumulative cash/tax/cost equals the original and no excess quantity is possible.
- **T21.02** — Given two operators refunding the same remaining line, when racing, then one valid result commits and no double cash payout is recorded.
- **T21.03** — Given a cashier without sales.refund, when opening the action or calling its endpoint, then the refund is refused while permitted history remains readable.

Reuse the named fixture and only add cases needed for these changed behaviors. Retain applicable existing tenant, rollback, immutable-history and precision regressions; do not replace them with response-status-only mocks. UI tests also verify focus/labels and error recovery for the affected actions.

### F. Verification

**NOT RUN — SPEC_ONLY.** Apply the [verification prerequisites](#verification-contract).

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~SalesRefundJourneyTests|FullyQualifiedName~PartialRefundTests"
```

**PASS:** the listed tests/checks actually execute and satisfy every expected outcome, applicable host build succeeds, `git diff --check` is clean, and no new secret/server assembly leaks into client assets. Record command, environment, SHA, test count and result. Full suite and packaged-platform proof occur at G7; no pass is claimed here.

### G. Definition of done

Sales history, reprint and partial cash refund are usable, permissioned and financially reconciled.

**Recovery/rollback:** Refunds already committed are immutable; correction is a linked new authorised transaction, not deletion or resetting refunded quantities.

**Change discipline:** minimal owning-module diff; no unnecessary production dependency; source/contract/schema/UI/tests remain aligned. An unmet test or owner-controlled prerequisite stays explicitly blocked. Specification completion is not implementation completion.

---

<a id="task-22"></a>
## Atomic Task 22 - Expose quotation and sales-order lifecycles

### A. Objective

**P1 · G5 · Requirements R05, R06, R07.** Dependencies: [T15](#task-15), [T18](#task-18), [T13](#task-13).

**Current → target:** Quotes/orders and their state transitions exist in API and typed clients; user workflows remain missing. **Target:** Sales staff can create, send-mark, accept/reject, amend and convert quotes, then confirm or cancel outstanding orders without email dependence.

**Non-goals:** no unrelated architecture rewrite, new mandatory hosted service, historical repricing or production action. The task is limited to the scope below and the binding decisions in Section 3. Acceptance is the task-specific test outcomes plus G.

### B. Scope

- CREATE `QtsBilling.Ui/Pages/Orders/Quotes.razor`, `QtsBilling.Ui/Pages/Orders/QuoteEditor.razor`, and `QtsBilling.Ui/Pages/Orders/SalesOrders.razor` in that directory.
- READ-ONLY `QtsBilling.Contracts/Orders/OrderApiClients.cs` and `QtsWebApp/QtsWebApp/Features/Orders/OrderEndpoints.cs`; register clients in hosts only where missing.
- CREATE `TestQtsBilling/Ui/OrderJourneyTests.cs`; retain `TestQtsBilling/Integration/QuoteOrderTests.cs`.
- INTEGRATION MODIFY only where needed: `QtsBilling.Ui/Layout/NavMenu.razor`, `QtsWebApp/QtsWebApp/Program.cs`, `QtsWebApp/QtsWebApp.Client/Program.cs`, and `QtsBilling.Maui/MauiProgram.cs` for the named page/client; no unrelated host changes.

### C. Verified anchors and NEW insertion points

[E18](#evidence-e18), [E54](#evidence-e54), [S06](#evidence-s06). These refer to the pinned baseline. Existing code is reused; all CREATE paths, new DTOs/routes and generated migration names in this task are **NEW**, not already-implemented capabilities. Read the complete owning method and its callers before changing the anchored slice.

### D. Ordered recipe

1. Build bounded list/detail/editor views with searchable party/catalog selection, business dates, terms, expiry, discounts and exact quote preview. Status transitions display available actions and required reason/expected version.
2. “Send” means an audited issued/communicated status and local printable/downloadable quote; it does not claim an email was delivered. Record optional manual delivery note without a remote service.
3. Convert an accepted quote to one order with stable idempotency and immutable quoted terms. Confirm/amend/cancel only allowable remaining quantities; show credit-limit or approval refusal without bypass.
4. Link confirmed orders to Task 23 fulfillment and Task 24 invoices. Quoting/ordering does not move physical stock or recognise revenue.

### E. Focused tests

**Files:** CREATE `TestQtsBilling/Ui/OrderJourneyTests.cs`; READ-ONLY `TestQtsBilling/Integration/QuoteOrderTests.cs`.

- **T22.01** — Given an accepted quote, when converted twice, then only one order exists with preserved terms and no journal/stock movement.
- **T22.02** — Given an expired/rejected quote or stale amendment, when converting/confirming, then the UI presents the precise refusal.
- **T22.03** — Given no email or internet service, when issuing/communicating a quote locally, then printable output and lifecycle remain useful without fabricated delivery success.

Reuse the named fixture and only add cases needed for these changed behaviors. Retain applicable existing tenant, rollback, immutable-history and precision regressions; do not replace them with response-status-only mocks. UI tests also verify focus/labels and error recovery for the affected actions.

### F. Verification

**NOT RUN — SPEC_ONLY.** Apply the [verification prerequisites](#verification-contract).

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~OrderJourneyTests|FullyQualifiedName~QuoteOrderTests"
```

**PASS:** the listed tests/checks actually execute and satisfy every expected outcome, applicable host build succeeds, `git diff --check` is clean, and no new secret/server assembly leaks into client assets. Record command, environment, SHA, test count and result. Full suite and packaged-platform proof occur at G7; no pass is claimed here.

### G. Definition of done

The quote-to-order lifecycle is complete and local; states and commitments are not confused with posted sales.

**Recovery/rollback:** Preserve issued quote/order versions; cancellation/amendment is explicit rather than editing confirmed terms in place.

**Change discipline:** minimal owning-module diff; no unnecessary production dependency; source/contract/schema/UI/tests remain aligned. An unmet test or owner-controlled prerequisite stays explicitly blocked. Specification completion is not implementation completion.

---

<a id="task-23"></a>
## Atomic Task 23 - Complete reservation, delivery and fulfillment workbench

### A. Objective

**P1 · G5 · Requirements R05, R06, R07, R08.** Dependencies: [T22](#task-22).

**Current → target:** Reservation/delivery/invoice-from-delivery APIs exist, including recent source-recognition changes, but require integrated UI and boundary tests. **Target:** Warehouse/sales operators reserve, partially deliver, release and invoice quantities without double inventory or revenue recognition.

**Non-goals:** no unrelated architecture rewrite, new mandatory hosted service, historical repricing or production action. The task is limited to the scope below and the binding decisions in Section 3. Acceptance is the task-specific test outcomes plus G.

### B. Scope

- CREATE `QtsBilling.Ui/Pages/Orders/Fulfillment.razor`; MODIFY `QtsBilling.Contracts/Orders/FulfillmentApiClient.cs` only for missing view fields/calls.
- READ-ONLY `QtsWebApp/QtsWebApp/Features/Orders/FulfillmentService.cs` and `QtsBilling.Contracts/Orders/FulfillmentDtos.cs`; remediate only demonstrated quantity/version/recognition gaps.
- MODIFY `TestQtsBilling/Integration/FulfillmentTests.cs`; CREATE `TestQtsBilling/Ui/FulfillmentJourneyTests.cs`.
- INTEGRATION MODIFY only where needed: `QtsBilling.Ui/Layout/NavMenu.razor`, `QtsWebApp/QtsWebApp/Program.cs`, `QtsWebApp/QtsWebApp.Client/Program.cs`, and `QtsBilling.Maui/MauiProgram.cs` for the named page/client; no unrelated host changes.

### C. Verified anchors and NEW insertion points

[E18](#evidence-e18), [E11](#evidence-e11), [S03](#evidence-s03), [S07](#evidence-s07). These refer to the pinned baseline. Existing code is reused; all CREATE paths, new DTOs/routes and generated migration names in this task are **NEW**, not already-implemented capabilities. Read the complete owning method and its callers before changing the anchored slice.

### D. Ordered recipe

1. Show ordered, reserved, delivered, released and invoiced quantities per line plus available stock by allowed location. Separate reservation commitment from dispatch and financial recognition.
2. Expose reserve/release/deliver using current version and stable keys. A partial delivery consumes original reservation/stock/value atomically; cancelled remainder cannot be delivered later.
3. Create a delivery-derived invoice through the existing source route and navigate to the invoice workbench. Preserve original quantity/cost/revenue snapshots and the single-recognition rule of Task 03.
4. Handle stock races, expired reservations, inaccessible location and closed-period failures with a reload/review action, not silent quantity substitution. Print a local delivery note from the committed snapshot.

### E. Focused tests

**Files:** MODIFY `TestQtsBilling/Integration/FulfillmentTests.cs`; CREATE `TestQtsBilling/Ui/FulfillmentJourneyTests.cs`.

- **T23.01** — Given an order delivered in parts, when each part is invoiced, then total invoiced quantity never exceeds delivery and inventory is consumed only at delivery.
- **T23.02** — Given a reservation release racing a delivery, when both run, then one consistent remaining quantity and value results.
- **T23.03** — Given duplicate delivery/invoice commands, when replayed, then the original documents are returned without a second movement or journal.

Reuse the named fixture and only add cases needed for these changed behaviors. Retain applicable existing tenant, rollback, immutable-history and precision regressions; do not replace them with response-status-only mocks. UI tests also verify focus/labels and error recovery for the affected actions.

### F. Verification

**NOT RUN — SPEC_ONLY.** Apply the [verification prerequisites](#verification-contract).

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~FulfillmentTests|FullyQualifiedName~FulfillmentJourneyTests"
```

**PASS:** the listed tests/checks actually execute and satisfy every expected outcome, applicable host build succeeds, `git diff --check` is clean, and no new secret/server assembly leaks into client assets. Record command, environment, SHA, test count and result. Full suite and packaged-platform proof occur at G7; no pass is claimed here.

### G. Definition of done

An operator can fulfill an order partially and invoice it with exact stock/control-account tie-outs and no hidden developer steps.

**Recovery/rollback:** Never roll back dispatched stock through UI deletion. Release unfulfilled commitment or use an explicit linked return/reversal process.

**Change discipline:** minimal owning-module diff; no unnecessary production dependency; source/contract/schema/UI/tests remain aligned. An unmet test or owner-controlled prerequisite stays explicitly blocked. Specification completion is not implementation completion.

---

<a id="task-24"></a>
## Atomic Task 24 - Complete invoice settlement, credits and authenticated printing UI

### A. Objective

**P1 · G5 · Requirements R05, R06, R07, R11.** Dependencies: [T02](#task-02), [T03](#task-03), [T15](#task-15), [T19](#task-19).

**Current → target:** Invoice list/editor and client are present; settlement/credit/void and native printing are not a complete usable workbench. **Target:** Users can manage the full supported invoice lifecycle, inspect history and print securely on browser and MAUI.

**Non-goals:** no unrelated architecture rewrite, new mandatory hosted service, historical repricing or production action. The task is limited to the scope below and the binding decisions in Section 3. Acceptance is the task-specific test outcomes plus G.

### B. Scope

- MODIFY `QtsBilling.Ui/Pages/Invoices/InvoiceEditor.razor` and `QtsBilling.Contracts/Invoices/InvoiceApiClient.cs`; CREATE `QtsBilling.Ui/Pages/Invoices/InvoiceActions.razor`.
- CREATE `QtsBilling.Contracts/Abstractions/IDocumentOutput.cs`, `QtsWebApp/QtsWebApp.Client/Services/WebDocumentOutput.cs`, and `QtsBilling.Maui/Services/NativeDocumentOutput.cs`; register them in their hosts as one print integration gate.
- CREATE `TestQtsBilling/Ui/InvoiceLifecycleJourneyTests.cs` and `TestQtsBilling/Contracts/DocumentOutputTests.cs`.
- INTEGRATION MODIFY only where needed: `QtsBilling.Ui/Layout/NavMenu.razor`, `QtsWebApp/QtsWebApp/Program.cs`, `QtsWebApp/QtsWebApp.Client/Program.cs`, and `QtsBilling.Maui/MauiProgram.cs` for the named page/client; no unrelated host changes.

### C. Verified anchors and NEW insertion points

[S04](#evidence-s04), [S05](#evidence-s05), [S06](#evidence-s06), [S07](#evidence-s07), [S08](#evidence-s08), [E21](#evidence-e21). These refer to the pinned baseline. Existing code is reused; all CREATE paths, new DTOs/routes and generated migration names in this task are **NEW**, not already-implemented capabilities. Read the complete owning method and its callers before changing the anchored slice.

### D. Ordered recipe

1. Provide authorised issue/void/payment/clear/bounce/partial-credit actions only for valid states. Show paid, credited, unallocated and remaining due separately, with source sale/order links and immutable event history.
2. Use current ExpectedVersion and stable keys for all financial actions. Payment/cash confirmation is explicit; unconfirmed cheque/transfer does not reduce settled balance. Credits select original remaining line quantities and stock disposition.
3. Replace relative native print links with IDocumentOutput: fetch authorised snapshot HTML through the host’s authenticated client, then print/export using local facilities. Never append bearer tokens to URLs or leave sensitive temporary files world-readable.
4. Use business-zone dates and settlement-scale display while preserving stored precision. UI defaults must not overwrite captured party/source/tax fields. Enforce keyboard, focus and unsaved-change confirmation.

### E. Focused tests

**Files:** CREATE `TestQtsBilling/Ui/InvoiceLifecycleJourneyTests.cs`; CREATE `TestQtsBilling/Contracts/DocumentOutputTests.cs`.

- **T24.01** — Given an issued invoice, when part-paid then credited, then UI totals agree with server open items and the original issued fields remain immutable.
- **T24.02** — Given the MAUI host with bearer authentication, when printing, then the correct authorised document prints locally without token leakage or external requests.
- **T24.03** — Given a lost payment response, when retrying from the action dialog, then the original key/body is preserved and no second payment is created.
- **T24.04** — Given a foreign invoice URL or expired session, when printing, then no document content is written to disk or displayed.

Reuse the named fixture and only add cases needed for these changed behaviors. Retain applicable existing tenant, rollback, immutable-history and precision regressions; do not replace them with response-status-only mocks. UI tests also verify focus/labels and error recovery for the affected actions.

### F. Verification

**NOT RUN — SPEC_ONLY.** Apply the [verification prerequisites](#verification-contract).

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~InvoiceLifecycleJourneyTests|FullyQualifiedName~DocumentOutputTests"
```

**PASS:** the listed tests/checks actually execute and satisfy every expected outcome, applicable host build succeeds, `git diff --check` is clean, and no new secret/server assembly leaks into client assets. Record command, environment, SHA, test count and result. Full suite and packaged-platform proof occur at G7; no pass is claimed here.

### G. Definition of done

Every included invoice action is operable and prints immutable content on both hosts; no cloud renderer or duplicate settlement is needed.

**Recovery/rollback:** Retain issued snapshots and action receipts. A print-adapter rollback must not roll back financial documents or expose tokens through a fallback URL.

**Change discipline:** minimal owning-module diff; no unnecessary production dependency; source/contract/schema/UI/tests remain aligned. An unmet test or owner-controlled prerequisite stays explicitly blocked. Specification completion is not implementation completion.

---

<a id="task-25"></a>
## Atomic Task 25 - Finish customer statements, deposit application and cash-credit recovery

### A. Objective

**P1 · G5 · Requirements R05, R06, R07, R08.** Dependencies: [T03](#task-03), [T24](#task-24).

**Current → target:** Open items, customer receipts and allocation exist; complete statement/deposit/refund workflows and cross-source reconciliation need completion. **Target:** Receivables, unapplied deposits and customer credits have an explicit operator lifecycle and reconcile to the configured AR/deposit controls.

**Non-goals:** no unrelated architecture rewrite, new mandatory hosted service, historical repricing or production action. The task is limited to the scope below and the binding decisions in Section 3. Acceptance is the task-specific test outcomes plus G.

### B. Scope

- MODIFY `QtsWebApp/QtsWebApp/Features/Invoices/PaymentEndpoints.cs` and `QtsBilling.Contracts/Invoices/InvoiceDtos.cs`; CREATE `QtsBilling.Ui/Pages/Finance/Receivables.razor`.
- READ-ONLY `QtsBilling.Persistence.Postgres/Entities/OpenItem.cs` and existing payment/allocation entities. Add NEW immutable application/refund metadata only where missing, with an additive migration.
- CREATE `TestQtsBilling/Integration/CustomerSettlementTests.cs` and `TestQtsBilling/Ui/ReceivablesJourneyTests.cs`.
- INTEGRATION MODIFY only where needed: `QtsBilling.Ui/Layout/NavMenu.razor`, `QtsWebApp/QtsWebApp/Program.cs`, `QtsWebApp/QtsWebApp.Client/Program.cs`, and `QtsBilling.Maui/MauiProgram.cs` for the named page/client; no unrelated host changes.
- MODIFY `QtsBilling.Persistence.Postgres/Entities/Sales.cs` PaymentAllocation and `QtsBilling.Persistence.Postgres/BillingDbContext.Model.Financial.cs` with an additive `AddOpeningItemAllocations` migration: nullable InvoiceId/OpenItemId with exactly-one-target check and composite tenant FKs; retain prior invoice allocations intact.

### C. Verified anchors and NEW insertion points

[E21](#evidence-e21), [E52](#evidence-e52), [S06](#evidence-s06), [E27](#evidence-e27), [S30](#evidence-s30), [S31](#evidence-s31). These refer to the pinned baseline. Existing code is reused; all CREATE paths, new DTOs/routes and generated migration names in this task are **NEW**, not already-implemented capabilities. Read the complete owning method and its callers before changing the anchored slice.

### D. Ordered recipe

1. Expose NEW customer statement/open-items read routes with as-of filtering and current balances. Extend current receipt/allocation commands instead of introducing a second receivables ledger.
2. Support native opening receivables explicitly: extend the existing allocation target to exactly one InvoiceId or OpenItemId, validate OpenItem.Kind/party/currency, and derive its remaining amount from allocations. Do not copy an opening into a fake invoice or count it twice in AR reports.
3. Allocate only cleared/confirmed funds under locks on receipt, invoice and open item; preserve partial allocations and remaining funds. A deposit is liability money until applied, not revenue and not negative AR disguised as a paid invoice.
4. Provide authorised linked deposit/credit refund with reason, original remaining amount, current cash drawer or confirmed bank settlement and exact journal reversal. Prevent refund and allocation consuming the same funds concurrently.
5. Tie statement totals to immutable transaction events and configured control accounts; document source-sale representation treatment so the customer is not charged twice.

### E. Focused tests

**Files:** CREATE `TestQtsBilling/Integration/CustomerSettlementTests.cs`; CREATE `TestQtsBilling/Ui/ReceivablesJourneyTests.cs`.

- **T25.01** — Given an unapplied cash deposit, when partly allocated then refunded, then liability, AR, cash and remaining deposit reconcile exactly.
- **T25.02** — Given simultaneous allocation/refund or replay, when executed, then no over-allocation, duplicate cash or negative unexplained remainder occurs.
- **T25.03** — Given an uncleared cheque, when allocating, then the attempt is refused until legitimate clearing; bouncing restores the proper open balance.
- **T25.04** — Given an as-of date before a later receipt, when viewing the statement, then the historical balance excludes that later receipt.
- **T25.05** — Given an imported opening receivable, when partly settled, then its remaining amount and AR control reconcile without creating a duplicate invoice.

Reuse the named fixture and only add cases needed for these changed behaviors. Retain applicable existing tenant, rollback, immutable-history and precision regressions; do not replace them with response-status-only mocks. UI tests also verify focus/labels and error recovery for the affected actions.

### F. Verification

**NOT RUN — SPEC_ONLY.** Apply the [verification prerequisites](#verification-contract).

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~CustomerSettlementTests|FullyQualifiedName~ReceivablesJourneyTests"
```

**PASS:** the listed tests/checks actually execute and satisfy every expected outcome, applicable host build succeeds, `git diff --check` is clean, and no new secret/server assembly leaks into client assets. Record command, environment, SHA, test count and result. Full suite and packaged-platform proof occur at G7; no pass is claimed here.

### G. Definition of done

AR/deposit statements, allocation and refunds reconcile per customer and to GL; no bank feed or acquiring service is required.

**Recovery/rollback:** Allocation/refund corrections are new linked events with journals; do not delete a posted receipt or mutate its original amount.

**Change discipline:** minimal owning-module diff; no unnecessary production dependency; source/contract/schema/UI/tests remain aligned. An unmet test or owner-controlled prerequisite stays explicitly blocked. Specification completion is not implementation completion.

---

<a id="task-26"></a>
## Atomic Task 26 - Build the purchase-order, receiving and returns workbench

### A. Objective

**P1 · G5 · Requirements R05, R06, R07, R08.** Dependencies: [T15](#task-15), [T18](#task-18).

**Current → target:** Supplier/PO/receipt/return APIs are implemented, but procurement is not yet a complete UI workflow. **Target:** Purchasing staff can order, receive in parts, return permitted quantities and navigate to supplier billing.

**Non-goals:** no unrelated architecture rewrite, new mandatory hosted service, historical repricing or production action. The task is limited to the scope below and the binding decisions in Section 3. Acceptance is the task-specific test outcomes plus G.

### B. Scope

- CREATE `QtsBilling.Ui/Pages/Purchasing/PurchaseOrders.razor`, `QtsBilling.Ui/Pages/Purchasing/PurchaseOrderEditor.razor`, and `QtsBilling.Ui/Pages/Purchasing/Receiving.razor` in that directory.
- CREATE `QtsBilling.Contracts/Purchasing/PurchasingApiClient.cs` over existing DTO/routes; register in all hosts.
- CREATE `TestQtsBilling/Ui/PurchasingJourneyTests.cs`; retain `TestQtsBilling/Integration/PurchasingReceiptPostingTests.cs`.
- INTEGRATION MODIFY only where needed: `QtsBilling.Ui/Layout/NavMenu.razor`, `QtsWebApp/QtsWebApp/Program.cs`, `QtsWebApp/QtsWebApp.Client/Program.cs`, and `QtsBilling.Maui/MauiProgram.cs` for the named page/client; no unrelated host changes.

### C. Verified anchors and NEW insertion points

[E22](#evidence-e22), [E23](#evidence-e23), [E11](#evidence-e11), [E52](#evidence-e52). These refer to the pinned baseline. Existing code is reused; all CREATE paths, new DTOs/routes and generated migration names in this task are **NEW**, not already-implemented capabilities. Read the complete owning method and its callers before changing the anchored slice.

### D. Ordered recipe

1. Expose draft/order/cancel/history with supplier/location/catalog selectors and supported cost/discount/tax snapshots. PO confirmation is a commitment, not a stock receipt.
2. Receiving displays ordered/received/returned/outstanding quantities, costs and a stable operation key. Partial receipts/returns use existing row-lock and cumulative validation, recording reference/reason.
3. Show the resulting GRN/return and journal/control links, then route to Task 27 bill matching. No editable product stock or manually overwritten QuantityReceived field is exposed.
4. Print a local purchase/receipt document; retain exact attempted receipt after an ambiguous response and retry it, rather than create another delivery.

### E. Focused tests

**Files:** CREATE `TestQtsBilling/Ui/PurchasingJourneyTests.cs`; READ-ONLY `TestQtsBilling/Integration/PurchasingReceiptPostingTests.cs`.

- **T26.01** — Given a PO, when ordered but not received, then stock and journal remain unchanged.
- **T26.02** — Given split deliveries and a receipt replay, when received, then stock/GRNI changes only by accepted quantities and each receipt posts once.
- **T26.03** — Given an over-receipt/return, foreign supplier/location or stale draft, when submitted, then the whole request is refused with actionable UI errors.

Reuse the named fixture and only add cases needed for these changed behaviors. Retain applicable existing tenant, rollback, immutable-history and precision regressions; do not replace them with response-status-only mocks. UI tests also verify focus/labels and error recovery for the affected actions.

### F. Verification

**NOT RUN — SPEC_ONLY.** Apply the [verification prerequisites](#verification-contract).

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~PurchasingJourneyTests|FullyQualifiedName~PurchasingReceiptPostingTests"
```

**PASS:** the listed tests/checks actually execute and satisfy every expected outcome, applicable host build succeeds, `git diff --check` is clean, and no new secret/server assembly leaks into client assets. Record command, environment, SHA, test count and result. Full suite and packaged-platform proof occur at G7; no pass is claimed here.

### G. Definition of done

The procurement journey is operable and financially distinguishes PO, receipt, return and bill.

**Recovery/rollback:** Cancel remaining commitments or post linked returns; never erase already received quantities or historical cost snapshots.

**Change discipline:** minimal owning-module diff; no unnecessary production dependency; source/contract/schema/UI/tests remain aligned. An unmet test or owner-controlled prerequisite stays explicitly blocked. Specification completion is not implementation completion.

---

<a id="task-27"></a>
## Atomic Task 27 - Complete supplier bills, credits, payments and payable workbench

### A. Objective

**P1 · G5 · Requirements R05, R06, R07, R08.** Dependencies: [T03](#task-03), [T26](#task-26).

**Current → target:** Bill issue and supplier payments exist; a full payable/credit lifecycle and its UI are incomplete. **Target:** Operators match receipts to bills, issue controlled supplier credits, settle payables and reconcile GRNI/AP/input tax without duplicate stock effects.

**Non-goals:** no unrelated architecture rewrite, new mandatory hosted service, historical repricing or production action. The task is limited to the scope below and the binding decisions in Section 3. Acceptance is the task-specific test outcomes plus G.

### B. Scope

- MODIFY `QtsWebApp/QtsWebApp/Features/Purchasing/SupplierBillEndpoints.cs` and `QtsBilling.Contracts/Purchasing/PurchasingDtos.cs`; CREATE `QtsBilling.Ui/Pages/Purchasing/Payables.razor`.
- Add NEW supplier-credit header/line/application state in `QtsBilling.Persistence.Postgres/Entities/Purchasing.cs`, matching model configuration and generated additive migration as one bill-credit gate.
- MODIFY `TestQtsBilling/Integration/SupplierBillPostingTests.cs`; CREATE `TestQtsBilling/Integration/SupplierCreditTests.cs` and `TestQtsBilling/Ui/PayablesJourneyTests.cs`.
- INTEGRATION MODIFY only where needed: `QtsBilling.Ui/Layout/NavMenu.razor`, `QtsWebApp/QtsWebApp/Program.cs`, `QtsWebApp/QtsWebApp.Client/Program.cs`, and `QtsBilling.Maui/MauiProgram.cs` for the named page/client; no unrelated host changes.

### C. Verified anchors and NEW insertion points

[E23](#evidence-e23), [E22](#evidence-e22), [E25](#evidence-e25), [E52](#evidence-e52), [S30](#evidence-s30). These refer to the pinned baseline. Existing code is reused; all CREATE paths, new DTOs/routes and generated migration names in this task are **NEW**, not already-implemented capabilities. Read the complete owning method and its callers before changing the anchored slice.

### D. Ordered recipe

1. Use existing bill/receipt links and immutable received-value snapshots. Bill issue validates unbilled quantity, supplier reference uniqueness and input tax, clearing exact GRNI and posting approved price variance to configured accounts.
2. Support native opening payables through a typed OpenItem target on supplier payment allocation, with exactly-one-bill-or-opening checks and party/currency/tenant validation. Reuse existing payment/allocation entities; never fabricate a supplier bill merely to pay an opening balance.
3. Add NEW `/api/v1/purchasing/bills/{id}/credits` with line-level remaining quantity/value, reason, source return where relevant and required key. A credit tied to an already posted purchase return must not remove stock a second time. Remaining supplier credit is shown separately and has an explicit control-account treatment, not an unexplained negative payable.
4. Complete payment allocation to open bills/credits with settled-method truth, date/period checks and current role/location scope. Lock bill/payment/credit cumulative amounts; replay returns the original result.
5. Provide supplier statements, bill matching, payment history and credit UI. Reconcile AP, GRNI, input tax, price variance and inventory movements before declaring a bill paid/closed.

### E. Focused tests

**Files:** MODIFY `TestQtsBilling/Integration/SupplierBillPostingTests.cs`; CREATE `TestQtsBilling/Integration/SupplierCreditTests.cs`; CREATE `TestQtsBilling/Ui/PayablesJourneyTests.cs`.

- **T27.01** — Given a partial receipt and a supplier bill at a changed approved price, when issued, then GRNI clears only that receipt and variance/input tax/AP balance correctly.
- **T27.02** — Given a returned delivery already reducing stock, when its bill is credited, then stock is not reduced twice.
- **T27.03** — Given concurrent payments/credits on one remaining payable, when executed, then remaining balance cannot be over-consumed and every entry balances.
- **T27.04** — Given a paid bill credit, when viewed, then the supplier credit balance is explicit and not silently written off or marked as incoming cash.
- **T27.05** — Given an imported opening payable, when paid or credited, then its remaining amount and AP control reconcile without a fake supplier bill.

Reuse the named fixture and only add cases needed for these changed behaviors. Retain applicable existing tenant, rollback, immutable-history and precision regressions; do not replace them with response-status-only mocks. UI tests also verify focus/labels and error recovery for the affected actions.

### F. Verification

**NOT RUN — SPEC_ONLY.** Apply the [verification prerequisites](#verification-contract).

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~SupplierBillPostingTests|FullyQualifiedName~SupplierCreditTests|FullyQualifiedName~PayablesJourneyTests"
```

**PASS:** the listed tests/checks actually execute and satisfy every expected outcome, applicable host build succeeds, `git diff --check` is clean, and no new secret/server assembly leaks into client assets. Record command, environment, SHA, test count and result. Full suite and packaged-platform proof occur at G7; no pass is claimed here.

### G. Definition of done

Procure-to-pay including credit corrections is complete and reconciles to authoritative stock and finance.

**Recovery/rollback:** Posted bills/credits/payments are corrected through linked reversing/applying events; no deletion or legacy import rollback is allowed.

**Change discipline:** minimal owning-module diff; no unnecessary production dependency; source/contract/schema/UI/tests remain aligned. An unmet test or owner-controlled prerequisite stays explicitly blocked. Specification completion is not implementation completion.

---

<a id="task-28"></a>
## Atomic Task 28 - Expose expenses, categories, tax and reversal workflows

### A. Objective

**P1 · G5 · Requirements R05, R06, R07, R11.** Dependencies: [T03](#task-03), [T15](#task-15), [T13](#task-13).

**Current → target:** Expense/category/tax snapshot and reversal APIs exist; normal operator entry and evidence navigation are missing. **Target:** Users enter properly classified expenses, verify tax/payment status, attach local evidence and reverse mistakes without rewriting history.

**Non-goals:** no unrelated architecture rewrite, new mandatory hosted service, historical repricing or production action. The task is limited to the scope below and the binding decisions in Section 3. Acceptance is the task-specific test outcomes plus G.

### B. Scope

- CREATE `QtsBilling.Ui/Pages/Expenses/Expenses.razor` and `QtsBilling.Ui/Pages/Expenses/ExpenseEditor.razor`; CREATE `QtsBilling.Contracts/Expenses/ExpenseApiClient.cs`.
- READ-ONLY `QtsWebApp/QtsWebApp/Features/Expenses/ExpenseEndpoints.cs` and `QtsBilling.Contracts/Expenses/ExpenseDtos.cs`; add only missing typed fields/calls.
- CREATE `TestQtsBilling/Ui/ExpenseJourneyTests.cs`; local attachment hooks become active only with Task 39.
- INTEGRATION MODIFY only where needed: `QtsBilling.Ui/Layout/NavMenu.razor`, `QtsWebApp/QtsWebApp/Program.cs`, `QtsWebApp/QtsWebApp.Client/Program.cs`, and `QtsBilling.Maui/MauiProgram.cs` for the named page/client; no unrelated host changes.

### C. Verified anchors and NEW insertion points

[E24](#evidence-e24), [E09](#evidence-e09), [E25](#evidence-e25). These refer to the pinned baseline. Existing code is reused; all CREATE paths, new DTOs/routes and generated migration names in this task are **NEW**, not already-implemented capabilities. Read the complete owning method and its callers before changing the anchored slice.

### D. Ordered recipe

1. Provide categories tied to valid expense accounts and an editor for date/location/supplier/description/exact amount/tax treatment/payment method. Show computed net/input-tax/settlement values before confirmation.
2. Use a stable key and existing one-balanced-entry writer. Distinguish recording an externally paid expense from actually acquiring a card payment; LocalCore never calls a PSP or stores PAN/CVV.
3. Expose immutable posted detail and explicit reasoned reversal; allow only one economic reversal per original and show the resulting journal. Closed-period correction uses an allowed current-period action.
4. Offer local evidence upload/history after Task 39, with pending-file status separate from posting success. Report unsupported tax/settlement configurations rather than guessing.

### E. Focused tests

**Files:** CREATE `TestQtsBilling/Ui/ExpenseJourneyTests.cs`.

- **T28.01** — Given a tax-bearing expense, when posted, then expense/input-tax/cash or clearing totals reconcile at configured precision.
- **T28.02** — Given a timeout and replay, when retrying, then one expense/journal exists.
- **T28.03** — Given an already reversed expense or forbidden date/location, when reversed/posted, then a clear refusal leaves history unchanged.

Reuse the named fixture and only add cases needed for these changed behaviors. Retain applicable existing tenant, rollback, immutable-history and precision regressions; do not replace them with response-status-only mocks. UI tests also verify focus/labels and error recovery for the affected actions.

### F. Verification

**NOT RUN — SPEC_ONLY.** Apply the [verification prerequisites](#verification-contract).

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~ExpenseJourneyTests"
```

**PASS:** the listed tests/checks actually execute and satisfy every expected outcome, applicable host build succeeds, `git diff --check` is clean, and no new secret/server assembly leaks into client assets. Record command, environment, SHA, test count and result. Full suite and packaged-platform proof occur at G7; no pass is claimed here.

### G. Definition of done

Expense classification, posting, evidence linkage and correction are usable without direct SQL or external services.

**Recovery/rollback:** A failed attachment can be retried independently; never erase a posted expense because evidence transfer failed.

**Change discipline:** minimal owning-module diff; no unnecessary production dependency; source/contract/schema/UI/tests remain aligned. An unmet test or owner-controlled prerequisite stays explicitly blocked. Specification completion is not implementation completion.

---

<a id="task-29"></a>
## Atomic Task 29 - Expose inventory overview and valued inter-location transfers

### A. Objective

**P1 · G5 · Requirements R05, R06, R07, R08.** Dependencies: [T18](#task-18), [T19](#task-19).

**Current → target:** Balances/movements/reconciliation and valued transfer APIs exist; location stock control needs UI completion. **Target:** Stock operators inspect quantity/value and perform dispatch/receipt with explicit in-transit custody.

**Non-goals:** no unrelated architecture rewrite, new mandatory hosted service, historical repricing or production action. The task is limited to the scope below and the binding decisions in Section 3. Acceptance is the task-specific test outcomes plus G.

### B. Scope

- CREATE `QtsBilling.Ui/Pages/Inventory/Stock.razor` and `QtsBilling.Ui/Pages/Inventory/Transfers.razor`; MODIFY `QtsBilling.Contracts/Inventory/InventoryApiClient.cs` only for missing view calls.
- READ-ONLY `QtsWebApp/QtsWebApp/Features/Inventory/StockTransferEndpoints.cs` and `QtsWebApp/QtsWebApp/Features/Inventory/InventoryLedger.cs`.
- CREATE `TestQtsBilling/Ui/StockTransferJourneyTests.cs`; retain `TestQtsBilling/Integration/StockTransferTests.cs`.
- INTEGRATION MODIFY only where needed: `QtsBilling.Ui/Layout/NavMenu.razor`, `QtsWebApp/QtsWebApp/Program.cs`, `QtsWebApp/QtsWebApp.Client/Program.cs`, and `QtsBilling.Maui/MauiProgram.cs` for the named page/client; no unrelated host changes.

### C. Verified anchors and NEW insertion points

[E10](#evidence-e10), [E11](#evidence-e11), [E51](#evidence-e51). These refer to the pinned baseline. Existing code is reused; all CREATE paths, new DTOs/routes and generated migration names in this task are **NEW**, not already-implemented capabilities. Read the complete owning method and its callers before changing the anchored slice.

### D. Ordered recipe

1. Show on-hand/reserved/available/in-transit quantities and authoritative value at allowed locations; identify provisional device estimates separately. Support bounded SKU/barcode/date/reference search and movement drill-down.
2. Create transfer drafts with source/destination/variants; dispatch and receive are explicit controlled transitions with keys, versions, actor and immutable cost. Both endpoints must authorise the relevant locations.
3. Keep transit value in its configured control account until received; do not fake immediate receipt for a remote offline location. Provide discrepancy handling through a reviewed adjustment/custody event, not editable transfer history.
4. Expose reconciliation differences as exceptions; never add a “repair balances” button that overwrites movement-backed values.

### E. Focused tests

**Files:** CREATE `TestQtsBilling/Ui/StockTransferJourneyTests.cs`; READ-ONLY `TestQtsBilling/Integration/StockTransferTests.cs`.

- **T29.01** — Given a transfer dispatched but not received, when reporting, then source stock is reduced and value remains in transit exactly once.
- **T29.02** — Given repeated receive commands or a second operator, when received, then destination quantity/value and transit clearing occur once.
- **T29.03** — Given a location-restricted user, when attempting the other end of a transfer, then permission is enforced and no foreign stock is disclosed.

Reuse the named fixture and only add cases needed for these changed behaviors. Retain applicable existing tenant, rollback, immutable-history and precision regressions; do not replace them with response-status-only mocks. UI tests also verify focus/labels and error recovery for the affected actions.

### F. Verification

**NOT RUN — SPEC_ONLY.** Apply the [verification prerequisites](#verification-contract).

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~StockTransferJourneyTests|FullyQualifiedName~StockTransferTests"
```

**PASS:** the listed tests/checks actually execute and satisfy every expected outcome, applicable host build succeeds, `git diff --check` is clean, and no new secret/server assembly leaks into client assets. Record command, environment, SHA, test count and result. Full suite and packaged-platform proof occur at G7; no pass is claimed here.

### G. Definition of done

Stock and transfers can be operated and reconciled with explicit custody and exact value preservation.

**Recovery/rollback:** Use linked return/adjustment transitions for a bad dispatch; never delete movement rows or restore a mutable Product.Quantity field.

**Change discipline:** minimal owning-module diff; no unnecessary production dependency; source/contract/schema/UI/tests remain aligned. An unmet test or owner-controlled prerequisite stays explicitly blocked. Specification completion is not implementation completion.

---

<a id="task-30"></a>
## Atomic Task 30 - Expose blind stock counts, approvals and valuation evidence

### A. Objective

**P1 · G5 · Requirements R05, R07, R08.** Dependencies: [T29](#task-29), [T03](#task-03).

**Current → target:** Count capture/approval and moving-average valuation exist in backend; an operator-safe counting and variance UI is absent. **Target:** Users count without seeing the expected number, managers approve controlled variances, and historical stock value is traceable.

**Non-goals:** no unrelated architecture rewrite, new mandatory hosted service, historical repricing or production action. The task is limited to the scope below and the binding decisions in Section 3. Acceptance is the task-specific test outcomes plus G.

### B. Scope

- CREATE `QtsBilling.Ui/Pages/Inventory/StockCounts.razor` and `QtsBilling.Ui/Pages/Inventory/StockCountEditor.razor`; extend `QtsBilling.Contracts/Inventory/InventoryApiClient.cs` only as needed.
- READ-ONLY `QtsWebApp/QtsWebApp/Features/Inventory/StockCountEndpoints.cs` and existing valuation writer; no new costing method is introduced.
- CREATE `TestQtsBilling/Ui/StockCountJourneyTests.cs`; retain `TestQtsBilling/Integration/StockCountTests.cs` and `TestQtsBilling/Integration/InventoryValuationTests.cs`.
- INTEGRATION MODIFY only where needed: `QtsBilling.Ui/Layout/NavMenu.razor`, `QtsWebApp/QtsWebApp/Program.cs`, `QtsWebApp/QtsWebApp.Client/Program.cs`, and `QtsBilling.Maui/MauiProgram.cs` for the named page/client; no unrelated host changes.

### C. Verified anchors and NEW insertion points

[E12](#evidence-e12), [E55](#evidence-e55), [E10](#evidence-e10), [E25](#evidence-e25). These refer to the pinned baseline. Existing code is reused; all CREATE paths, new DTOs/routes and generated migration names in this task are **NEW**, not already-implemented capabilities. Read the complete owning method and its callers before changing the anchored slice.

### D. Ordered recipe

1. Provide scoped count creation, blind line entry/import and save/resume. Do not reveal expected quantity through hidden DOM or an unrestricted detail response to a blind-count role.
2. Manager approval shows snapshot/current movements/variance, reason and exact financial effect. If stock moved after count snapshot, use the existing controlled conflict policy or require recount; never silently adjust against stale on-hand.
3. Post an approved variance through the existing inventory and ledger writers with configured shrinkage account and stable key. No negative stock; positive adjustments need explicit authoritative valuation.
4. Offer a valuation trail to receipt/opening/consumption movement values. A stock opening without approved value blocks consumption rather than borrowing today’s purchase price.

### E. Focused tests

**Files:** CREATE `TestQtsBilling/Ui/StockCountJourneyTests.cs`; READ-ONLY `TestQtsBilling/Integration/StockCountTests.cs`; READ-ONLY `TestQtsBilling/Integration/InventoryValuationTests.cs`.

- **T30.01** — Given a blind count, when a counter loads it, then expected quantities are not disclosed while authorised review can see variance.
- **T30.02** — Given intervening sales during counting, when approving, then the expected/current-state conflict is resolved explicitly and no stale adjustment is applied.
- **T30.03** — Given repeated approval, when retried, then one variance movement/journal exists; zero final stock has zero residual value.

Reuse the named fixture and only add cases needed for these changed behaviors. Retain applicable existing tenant, rollback, immutable-history and precision regressions; do not replace them with response-status-only mocks. UI tests also verify focus/labels and error recovery for the affected actions.

### F. Verification

**NOT RUN — SPEC_ONLY.** Apply the [verification prerequisites](#verification-contract).

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~StockCountJourneyTests|FullyQualifiedName~StockCountTests|FullyQualifiedName~InventoryValuationTests"
```

**PASS:** the listed tests/checks actually execute and satisfy every expected outcome, applicable host build succeeds, `git diff --check` is clean, and no new secret/server assembly leaks into client assets. Record command, environment, SHA, test count and result. Full suite and packaged-platform proof occur at G7; no pass is claimed here.

### G. Definition of done

Counting/variance approval is operational, separates roles and preserves the movement-ledger valuation authority.

**Recovery/rollback:** Cancel unapproved counts; reverse an approved variance with a new authorised transaction rather than editing count history.

**Change discipline:** minimal owning-module diff; no unnecessary production dependency; source/contract/schema/UI/tests remain aligned. An unmet test or owner-controlled prerequisite stays explicitly blocked. Specification completion is not implementation completion.

---

<a id="task-31"></a>
## Atomic Task 31 - Complete local bank-statement reconciliation and corrections

### A. Objective

**P1 · G5 · Requirements R05, R06, R07, R08, R11.** Dependencies: [T03](#task-03), [T25](#task-25), [T27](#task-27).

**Current → target:** Bank account/CSV statement import and match API exist; reconciliation review/correction and UI are incomplete. **Target:** Accountants import local statements, match bank movements, record legitimate fees and close reconciliations without a bank-feed provider.

**Non-goals:** no unrelated architecture rewrite, new mandatory hosted service, historical repricing or production action. The task is limited to the scope below and the binding decisions in Section 3. Acceptance is the task-specific test outcomes plus G.

### B. Scope

- MODIFY `QtsWebApp/QtsWebApp/Features/Banking/BankingEndpoints.cs` and `QtsBilling.Contracts/Banking/BankingDtos.cs`; CREATE `QtsBilling.Ui/Pages/Finance/Banking.razor`.
- READ-ONLY `QtsBilling.Contracts/Banking/BankingApiClient.cs` and `QtsBilling.Persistence.Postgres/Entities/Banking.cs`; extend typed calls and add auditable reconciliation/correction records only if absent.
- MODIFY `TestQtsBilling/Integration/BankingEndpointsTests.cs`; CREATE `TestQtsBilling/Ui/BankingJourneyTests.cs`.
- INTEGRATION MODIFY only where needed: `QtsBilling.Ui/Layout/NavMenu.razor`, `QtsWebApp/QtsWebApp/Program.cs`, `QtsWebApp/QtsWebApp.Client/Program.cs`, and `QtsBilling.Maui/MauiProgram.cs` for the named page/client; no unrelated host changes.

### C. Verified anchors and NEW insertion points

[E26](#evidence-e26), [E25](#evidence-e25), [E27](#evidence-e27). These refer to the pinned baseline. Existing code is reused; all CREATE paths, new DTOs/routes and generated migration names in this task are **NEW**, not already-implemented capabilities. Read the complete owning method and its callers before changing the anchored slice.

### D. Ordered recipe

1. Keep CSV/local-file import with bounded size/row/date/amount parsing, duplicate-file detection and explicit preview. No OAuth/open-banking account or remote download is required.
2. Show opening + statement movements = closing balance and ledger candidate matches. Match must enforce account/currency/sign/amount/source uniqueness and location/business authority; an arbitrary string reference is not bank-settlement proof.
3. Add NEW reasoned unmatch/correction and finalise/reopen-reconciliation commands with ExpectedVersion and stable keys. Preserve prior match history; reopening a reconciliation does not reopen a closed accounting period.
4. Record bank fees/interest only as reviewed journal commands through existing posting rules and configured accounts. Unmatched lines remain visible exceptions, not auto-written-off differences.

### E. Focused tests

**Files:** MODIFY `TestQtsBilling/Integration/BankingEndpointsTests.cs`; CREATE `TestQtsBilling/Ui/BankingJourneyTests.cs`.

- **T31.01** — Given an imported statement, when matched and finalised, then statement arithmetic and booked-bank controls reconcile exactly.
- **T31.02** — Given duplicate files/lines, two matches to one journal line or an amount mismatch, when imported/matched, then duplicates/inconsistent matches are refused.
- **T31.03** — Given a mistaken match, when corrected with permission/reason, then prior match evidence is retained and no historic journal is mutated.

Reuse the named fixture and only add cases needed for these changed behaviors. Retain applicable existing tenant, rollback, immutable-history and precision regressions; do not replace them with response-status-only mocks. UI tests also verify focus/labels and error recovery for the affected actions.

### F. Verification

**NOT RUN — SPEC_ONLY.** Apply the [verification prerequisites](#verification-contract).

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~BankingEndpointsTests|FullyQualifiedName~BankingJourneyTests"
```

**PASS:** the listed tests/checks actually execute and satisfy every expected outcome, applicable host build succeeds, `git diff --check` is clean, and no new secret/server assembly leaks into client assets. Record command, environment, SHA, test count and result. Full suite and packaged-platform proof occur at G7; no pass is claimed here.

### G. Definition of done

Bank reconciliation is useful with manually supplied statements and auditable corrections; no automatic bank provider is a prerequisite.

**Recovery/rollback:** Reopen reconciliation via an authorised event and correct future entries; retain original uploaded statement digest and parsed rows.

**Change discipline:** minimal owning-module diff; no unnecessary production dependency; source/contract/schema/UI/tests remain aligned. An unmet test or owner-controlled prerequisite stays explicitly blocked. Specification completion is not implementation completion.

---

<a id="task-32"></a>
## Atomic Task 32 - Complete accounting, period and year-close screens

### A. Objective

**P1 · G5 · Requirements R05, R07, R08.** Dependencies: [T03](#task-03), [T13](#task-13).

**Current → target:** Chart/settings/journal/period/year-close APIs are implemented but the accounting operator surface is incomplete. **Target:** Accountants maintain the chart, post/reverse journals and close periods/years with visible reconciliation prerequisites.

**Non-goals:** no unrelated architecture rewrite, new mandatory hosted service, historical repricing or production action. The task is limited to the scope below and the binding decisions in Section 3. Acceptance is the task-specific test outcomes plus G.

### B. Scope

- CREATE `QtsBilling.Ui/Pages/Accounting/Accounts.razor`, `QtsBilling.Ui/Pages/Accounting/Journals.razor`, and `QtsBilling.Ui/Pages/Accounting/Periods.razor`.
- CREATE `QtsBilling.Contracts/Accounting/AccountingApiClient.cs` over existing DTOs/routes; register in all hosts. Do not introduce a second ledger writer.
- CREATE `TestQtsBilling/Ui/AccountingJourneyTests.cs`; retain `TestQtsBilling/Integration/PeriodPostingTests.cs` and `TestQtsBilling/Integration/FiscalYearCloseTests.cs`.
- INTEGRATION MODIFY only where needed: `QtsBilling.Ui/Layout/NavMenu.razor`, `QtsWebApp/QtsWebApp/Program.cs`, `QtsWebApp/QtsWebApp.Client/Program.cs`, and `QtsBilling.Maui/MauiProgram.cs` for the named page/client; no unrelated host changes.

### C. Verified anchors and NEW insertion points

[E25](#evidence-e25), [E48](#evidence-e48), [S03](#evidence-s03), [S26](#evidence-s26). These refer to the pinned baseline. Existing code is reused; all CREATE paths, new DTOs/routes and generated migration names in this task are **NEW**, not already-implemented capabilities. Read the complete owning method and its callers before changing the anchored slice.

### D. Ordered recipe

1. Expose chart/configured-control mappings with active/type/hierarchy validation and read-only history. Journal entry supports exact debit/credit lines and prevents client balance shortcuts.
2. Post with effective business date/current open period and stable command key. Reversal selects the original entry and a valid correction date/reason; no edit/delete action is shown for posted lines.
3. Period close/year close displays reconciliation evidence: AR/AP/stock/transit/deposits/cash/open device cases and outstanding journals. Reuse existing serialization between close and posting; a UI confirmation does not bypass server gates.
4. Link trial balance/general-ledger detail and drill-through source documents. Clearly distinguish a financial statement from a simple cash summary and a fiscal close from application shutdown.

### E. Focused tests

**Files:** CREATE `TestQtsBilling/Ui/AccountingJourneyTests.cs`; READ-ONLY `TestQtsBilling/Integration/PeriodPostingTests.cs`; READ-ONLY `TestQtsBilling/Integration/FiscalYearCloseTests.cs`.

- **T32.01** — Given an unbalanced journal or a missing/closed period, when posting, then the server refuses atomically and fields remain reviewable.
- **T32.02** — Given posting racing period close, when both execute, then the posted entry belongs to an allowed open period or is refused, never silently backdated.
- **T32.03** — Given year close replay, when retried, then one retained-earnings transfer exists and reports reconcile before/after close.

Reuse the named fixture and only add cases needed for these changed behaviors. Retain applicable existing tenant, rollback, immutable-history and precision regressions; do not replace them with response-status-only mocks. UI tests also verify focus/labels and error recovery for the affected actions.

### F. Verification

**NOT RUN — SPEC_ONLY.** Apply the [verification prerequisites](#verification-contract).

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~AccountingJourneyTests|FullyQualifiedName~PeriodPostingTests|FullyQualifiedName~FiscalYearCloseTests"
```

**PASS:** the listed tests/checks actually execute and satisfy every expected outcome, applicable host build succeeds, `git diff --check` is clean, and no new secret/server assembly leaks into client assets. Record command, environment, SHA, test count and result. Full suite and packaged-platform proof occur at G7; no pass is claimed here.

### G. Definition of done

Routine accounting and close are operator-complete, controlled and derived from posted facts.

**Recovery/rollback:** Do not undo a close through raw row edits. Use the permitted audited correction process, retaining all closing entries and evidence.

**Change discipline:** minimal owning-module diff; no unnecessary production dependency; source/contract/schema/UI/tests remain aligned. An unmet test or owner-controlled prerequisite stays explicitly blocked. Specification completion is not implementation completion.

---

<a id="task-33"></a>
## Atomic Task 33 - Complete reports, local exports and the management dashboard

### A. Objective

**P1 · G5 · Requirements R05, R07, R08, R11.** Dependencies: [T25](#task-25), [T27](#task-27), [T28](#task-28), [T30](#task-30), [T31](#task-31), [T32](#task-32).

**Current → target:** AR/AP/tax/Z/balance sheet/P&L/stock/cash/sales reports exist; dashboard/drill-through/UI and historical tie-out coverage require completion. **Target:** Managers can answer daily financial/stock questions from precise, scoped posted facts with local export and no BI service.

**Non-goals:** no unrelated architecture rewrite, new mandatory hosted service, historical repricing or production action. The task is limited to the scope below and the binding decisions in Section 3. Acceptance is the task-specific test outcomes plus G.

### B. Scope

- MODIFY `QtsWebApp/QtsWebApp/Features/Reporting/ReportEndpoints.cs` and `QtsWebApp/QtsWebApp/Features/Reporting/CsvReportWriter.cs`; CREATE `QtsBilling.Ui/Pages/Reporting/Reports.razor`.
- CREATE `QtsBilling.Contracts/Reports/ReportApiClient.cs`; integrate a small dashboard view in the Task 13 home shell using those report contracts.
- CREATE `TestQtsBilling/Integration/ReportTieOutTests.cs` and `TestQtsBilling/Ui/ReportJourneyTests.cs`.
- INTEGRATION MODIFY only where needed: `QtsBilling.Ui/Layout/NavMenu.razor`, `QtsWebApp/QtsWebApp/Program.cs`, `QtsWebApp/QtsWebApp.Client/Program.cs`, and `QtsBilling.Maui/MauiProgram.cs` for the named page/client; no unrelated host changes.

### C. Verified anchors and NEW insertion points

[E27](#evidence-e27), [E25](#evidence-e25), [E52](#evidence-e52), [E39](#evidence-e39). These refer to the pinned baseline. Existing code is reused; all CREATE paths, new DTOs/routes and generated migration names in this task are **NEW**, not already-implemented capabilities. Read the complete owning method and its callers before changing the anchored slice.

### D. Ordered recipe

1. Expose existing report filters/date ranges/location/business/currency with clear as-of semantics. Historical aging and balances must use immutable allocations/credits/clearing events effective at the report date, not simply current mutable BalanceDue.
2. Tie AR/AP to open-item and GL controls; inventory to movement value and stock balances; sales to recognition sources; cash to settled movements and drawer custody. Display unresolved differences rather than silently exclude them.
3. Add bounded drill-down and safe local CSV/print export: preserve six-decimal quantities and exact money, invariant machine columns with human labels, neutralise spreadsheet formula injection and bound result size. Do not add a spreadsheet/PDF service.
4. Populate home with a few permission-appropriate KPIs and actionable overdue/pending/stock alerts. Clearly label posted vs provisional and stale data; use no externally loaded charts/fonts.

### E. Focused tests

**Files:** CREATE `TestQtsBilling/Integration/ReportTieOutTests.cs`; CREATE `TestQtsBilling/Ui/ReportJourneyTests.cs`.

- **T33.01** — Given partial payments/credits after the as-of date, when running aging, then only events effective by that date affect historical balances.
- **T33.02** — Given a retail/wholesale purchase-to-sale/refund fixture, when reporting, then every subledger/control total and inventory value reconciles.
- **T33.03** — Given an export containing formula-like names and fractional quantities, when opened as CSV, then it is safe and numerically exact.
- **T33.04** — Given another business/location filter or unbounded range, when queried, then scope/size controls hold without information leakage.

Reuse the named fixture and only add cases needed for these changed behaviors. Retain applicable existing tenant, rollback, immutable-history and precision regressions; do not replace them with response-status-only mocks. UI tests also verify focus/labels and error recovery for the affected actions.

### F. Verification

**NOT RUN — SPEC_ONLY.** Apply the [verification prerequisites](#verification-contract).

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~ReportTieOutTests|FullyQualifiedName~ReportJourneyTests"
```

**PASS:** the listed tests/checks actually execute and satisfy every expected outcome, applicable host build succeeds, `git diff --check` is clean, and no new secret/server assembly leaks into client assets. Record command, environment, SHA, test count and result. Full suite and packaged-platform proof occur at G7; no pass is claimed here.

### G. Definition of done

Reports are trustworthy enough for daily management and reconciliation, not just visually plausible totals.

**Recovery/rollback:** Disable a faulty export/report without altering source facts; correct report logic and reproduce comparisons against the same frozen data.

**Change discipline:** minimal owning-module diff; no unnecessary production dependency; source/contract/schema/UI/tests remain aligned. An unmet test or owner-controlled prerequisite stays explicitly blocked. Specification completion is not implementation completion.

---

<a id="task-34"></a>
## Atomic Task 34 - Provide local audit, health and operational diagnostics

### A. Objective

**P1 · G5 · Requirements R04, R07, R12.** Dependencies: [T09](#task-09), [T13](#task-13).

**Current → target:** Append-only audit, health and metrics exist, but operator diagnostics must be self-contained and securely accessible. **Target:** Owners diagnose business changes, failed jobs, backup freshness and sync health without cloud logging or remote support access.

**Non-goals:** no unrelated architecture rewrite, new mandatory hosted service, historical repricing or production action. The task is limited to the scope below and the binding decisions in Section 3. Acceptance is the task-specific test outcomes plus G.

### B. Scope

- CREATE `QtsBilling.Ui/Pages/Administration/Audit.razor` and `QtsBilling.Ui/Pages/Administration/Diagnostics.razor`; MODIFY `QtsWebApp/QtsWebApp/Features/Health/HealthEndpoints.cs` for minimal liveness/readiness separation if needed.
- READ-ONLY `QtsWebApp/QtsWebApp/Features/Audit/AuditEndpoints.cs`, `QtsWebApp/QtsWebApp/Infrastructure/BillingMetrics.cs` and `QtsWebApp/QtsWebApp/Infrastructure/Redaction.cs`; use current typed feed and local structured logs.
- CREATE `TestQtsBilling/Integration/LocalDiagnosticsTests.cs`; retain `TestQtsBilling/Integration/OperationalMetricsTests.cs`.
- INTEGRATION MODIFY only where needed: `QtsBilling.Ui/Layout/NavMenu.razor`, `QtsWebApp/QtsWebApp/Program.cs`, `QtsWebApp/QtsWebApp.Client/Program.cs`, and `QtsBilling.Maui/MauiProgram.cs` for the named page/client; no unrelated host changes.

### C. Verified anchors and NEW insertion points

[E28](#evidence-e28), [S02](#evidence-s02), [S22](#evidence-s22), [E31](#evidence-e31). These refer to the pinned baseline. Existing code is reused; all CREATE paths, new DTOs/routes and generated migration names in this task are **NEW**, not already-implemented capabilities. Read the complete owning method and its callers before changing the anchored slice.

### D. Ordered recipe

1. Expose audit search/filter/cursor detail by actor/action/document/correlation within current scope. Do not allow editing audit records or displaying secret payloads.
2. Show readiness, last successful backup/restore verification, database/schema version, disk capacity thresholds, local job status, device pending/rejected totals and certificate/key expiry warnings. Read only local configured evidence.
3. Separate anonymous minimal liveness from authorised dependency diagnostics. Local logs rotate with bounded retention and safe export; failures in the log sink must not recurse into the database or fabricate operation success.
4. Provide a redacted support bundle generated locally only on owner request; exclude keys, tokens, passwords, raw customer documents and full connection strings. No automatic upload is implemented.

### E. Focused tests

**Files:** CREATE `TestQtsBilling/Integration/LocalDiagnosticsTests.cs`; READ-ONLY `TestQtsBilling/Integration/OperationalMetricsTests.cs`.

- **T34.01** — Given a failed sync/backup or nearly full disk, when viewing diagnostics, then an actionable local warning appears with a correlation reference.
- **T34.02** — Given an anonymous or non-audit role, when requesting diagnostics/audit/export, then sensitive state is denied.
- **T34.03** — Given secret-shaped values in errors, when generating a support bundle, then redaction tests prove they are absent.

Reuse the named fixture and only add cases needed for these changed behaviors. Retain applicable existing tenant, rollback, immutable-history and precision regressions; do not replace them with response-status-only mocks. UI tests also verify focus/labels and error recovery for the affected actions.

### F. Verification

**NOT RUN — SPEC_ONLY.** Apply the [verification prerequisites](#verification-contract).

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~LocalDiagnosticsTests|FullyQualifiedName~OperationalMetricsTests"
```

**PASS:** the listed tests/checks actually execute and satisfy every expected outcome, applicable host build succeeds, `git diff --check` is clean, and no new secret/server assembly leaks into client assets. Record command, environment, SHA, test count and result. Full suite and packaged-platform proof occur at G7; no pass is claimed here.

### G. Definition of done

Basic operations and audit are observable locally and remain functional without a telemetry collector.

**Recovery/rollback:** Keep audit facts and local logs under retention policy; do not delete them to clear a diagnostic warning.

**Change discipline:** minimal owning-module diff; no unnecessary production dependency; source/contract/schema/UI/tests remain aligned. An unmet test or owner-controlled prerequisite stays explicitly blocked. Specification completion is not implementation completion.

---

<a id="task-35"></a>
## Atomic Task 35 - Add device-scoped baselines, epochs and retention-safe change feeds

### A. Objective

**P0 · G6 · Requirements R04, R09, R10.** Dependencies: [T04](#task-04), [T08](#task-08).

**Current → target:** The incremental feed has only revision/ordinal and future-cursor checks; corrupt JSON becomes null and no complete baseline protocol exists. **Target:** Devices can initialise/resynchronise from a consistent authorised snapshot and detect restores, permission changes and pruned history safely.

**Non-goals:** no unrelated architecture rewrite, new mandatory hosted service, historical repricing or production action. The task is limited to the scope below and the binding decisions in Section 3. Acceptance is the task-specific test outcomes plus G.

### B. Scope

- MODIFY `QtsWebApp/QtsWebApp/Features/Sync/SyncChangeEndpoints.cs` and `QtsBilling.Contracts/Sync/SyncDtos.cs`; CREATE `QtsWebApp/QtsWebApp/Features/Sync/SyncBaselineEndpoints.cs`.
- Add NEW baseline/epoch/floor metadata in `QtsBilling.Persistence.Postgres/Entities/Platform.cs` with model configuration and an additive migration; register the new slice in server Program.
- CREATE `TestQtsBilling/Integration/SyncBaselineTests.cs`.
- MODIFY `QtsBilling.Contracts/Sync/SyncApiClient.cs` and `QtsBilling.Maui/Services/DeviceSyncCoordinator.cs` for the versioned baseline/feed calls; this protocol integration must ship with Tasks 36–38.

### C. Verified anchors and NEW insertion points

[S16](#evidence-s16), [E29](#evidence-e29), [E30](#evidence-e30), [E47](#evidence-e47), [S17](#evidence-s17). These refer to the pinned baseline. Existing code is reused; all CREATE paths, new DTOs/routes and generated migration names in this task are **NEW**, not already-implemented capabilities. Read the complete owning method and its callers before changing the anchored slice.

### D. Ordered recipe

1. Add NEW `/api/v1/sync/baselines` create and paged-read endpoints for enrolled devices. Capture current allowed business/locations, schema, permission revision, epoch and high-watermark in one PostgreSQL REPEATABLE READ snapshot with bounded immutable staged pages. Complete all staging before marking a baseline Ready; do not hold a DB transaction across client HTTP requests.
2. Extend changes requests/responses with epoch/schema/permission revision/floor. Validate current device and membership on every page. Scope revocation or restore epoch mismatch invalidates the baseline; a new scope cannot reuse a previous broader cursor.
3. Fail corrupt stored projections as a typed feed error; never substitute null and advance. Validate cursor ordinal semantics and advertise next cursor after any safely skipped unauthorised projections so clients do not loop indefinitely.
4. Retain change rows until a documented retention policy and floor advancement transaction exist. A floor/gap/restored epoch returns RESYNC_REQUIRED; baseline pages expire with bounded cleanup. Keep all pending device money outside the rebuild contract.

### E. Focused tests

**Files:** CREATE `TestQtsBilling/Integration/SyncBaselineTests.cs`.

- **T35.01** — Given concurrent catalog writes during baseline construction, when applying baseline then feed, then every allowed committed row appears once at the correct version with no missing interval.
- **T35.02** — Given a revoked device, changed location/permission revision or restored epoch, when requesting the next page, then the old cursor is refused and no broader data is returned.
- **T35.03** — Given corrupted JSON or an unsupported schema, when reading changes, then the page fails and the device cursor is not advanced.
- **T35.04** — Given a cursor below the retention floor, when requesting changes, then an explicit resync outcome replaces silent truncation.

Reuse the named fixture and only add cases needed for these changed behaviors. Retain applicable existing tenant, rollback, immutable-history and precision regressions; do not replace them with response-status-only mocks. UI tests also verify focus/labels and error recovery for the affected actions.

### F. Verification

**NOT RUN — SPEC_ONLY.** Apply the [verification prerequisites](#verification-contract).

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~SyncBaselineTests"
```

**PASS:** the listed tests/checks actually execute and satisfy every expected outcome, applicable host build succeeds, `git diff --check` is clean, and no new secret/server assembly leaks into client assets. Record command, environment, SHA, test count and result. Full suite and packaged-platform proof occur at G7; no pass is claimed here.

### G. Definition of done

Initial sync, permission changes, pruning and restore all have a deterministic lossless protocol; no cloud synchronization service is required.

**Recovery/rollback:** Do not prune old feed history during first rollout. Protocol-incompatible clients stop sync and retain queues; restore the previous application only while its schema/protocol remains compatible.

**Change discipline:** minimal owning-module diff; no unnecessary production dependency; source/contract/schema/UI/tests remain aligned. An unmet test or owner-controlled prerequisite stays explicitly blocked. Specification completion is not implementation completion.

---

<a id="task-36"></a>
## Atomic Task 36 - Fix SQLite conflict ordering and atomic cache outcome semantics

### A. Objective

**P0 · G6 · Requirements R09, R10.** Dependencies: [T35](#task-35).

**Current → target:** Atomic page+cursor already exists, but projection mutation precedes conflict detection and resolution only stamps a row. **Target:** A page either applies validated authorised cache changes/conflict decisions/cursor together or changes nothing; resolution performs its advertised action.

**Non-goals:** no unrelated architecture rewrite, new mandatory hosted service, historical repricing or production action. The task is limited to the scope below and the binding decisions in Section 3. Acceptance is the task-specific test outcomes plus G.

### B. Scope

- MODIFY `QtsBilling.Persistence.Sqlite/SqliteLocalStore.Sync.cs`, `QtsBilling.Persistence.Sqlite/Entities/SyncConflict.cs`, and `QtsBilling.Maui/Services/DeviceSyncConflictService.cs`.
- MODIFY `QtsBilling.Persistence.Sqlite.Tests/AtomicSyncCommitTests.cs` and `QtsBilling.Persistence.Sqlite.Tests/ReceiptAndSyncStateTests.cs`; add an additive SQLite migration only for new conflict metadata, never rewrite applied migrations.

### C. Verified anchors and NEW insertion points

[S17](#evidence-s17), [E53](#evidence-e53), [E31](#evidence-e31). These refer to the pinned baseline. Existing code is reused; all CREATE paths, new DTOs/routes and generated migration names in this task are **NEW**, not already-implemented capabilities. Read the complete owning method and its callers before changing the anchored slice.

### D. Ordered recipe

1. Split projection validation/affected-key inspection from mutation. Detect pending-operation conflicts before applying a server value or tombstone; capture genuine before/after values, not an already overwritten LocalValue.
2. Validate every page’s scope, schema, ordered cursor and payload. Unknown/corrupt required projections fail the complete page. Do not count null or malformed data as safely skipped while advancing the cursor.
3. Resolve UseServer by applying the selected validated server projection and recording the resolution in one write-gated SQLite transaction. KeepCapturedCommand preserves immutable money payloads for server review; rename misleading “local authoritative” wording.
4. Reuse ApplyChangesAndAdvanceCursorAsync for normal pages. Baseline replacement swaps only scoped cache rows after full verification, preserving all pending sales/cash/outbox/receipts/conflicts. Test process interruption around commit.
5. Scope conflict/pending reads and resolution commands to the active authorised business/device/custodian; preserving another business’s queued money does not authorise the current cashier to inspect it. An owner recovery workflow may access it only through explicit authority.

### E. Focused tests

**Files:** MODIFY `QtsBilling.Persistence.Sqlite.Tests/AtomicSyncCommitTests.cs`; MODIFY `QtsBilling.Persistence.Sqlite.Tests/ReceiptAndSyncStateTests.cs`.

- **T36.01** — Given a pending sale and a changed product, when pulling, then the conflict captures the pre-change value and the queued sale is unchanged.
- **T36.02** — Given UseServer resolution, when committed, then the cache actually changes and the resolution records actor/time; repeated resolution is idempotent.
- **T36.03** — Given a malformed second projection or a crash before commit, when restarting, then neither partial cache changes nor an advanced cursor survive.
- **T36.04** — Given baseline replacement, when completed, then pending cash/outbox IDs and exact amounts are byte-equivalent to before.

Reuse the named fixture and only add cases needed for these changed behaviors. Retain applicable existing tenant, rollback, immutable-history and precision regressions; do not replace them with response-status-only mocks. UI tests also verify focus/labels and error recovery for the affected actions.

### F. Verification

**NOT RUN — SPEC_ONLY.** Apply the [verification prerequisites](#verification-contract).

```text
dotnet test QtsBilling.Persistence.Sqlite.Tests/QtsBilling.Persistence.Sqlite.Tests.csproj -c Release --filter "FullyQualifiedName~AtomicSyncCommitTests|FullyQualifiedName~ReceiptAndSyncStateTests"
```

**PASS:** the listed tests/checks actually execute and satisfy every expected outcome, applicable host build succeeds, `git diff --check` is clean, and no new secret/server assembly leaks into client assets. Record command, environment, SHA, test count and result. Full suite and packaged-platform proof occur at G7; no pass is claimed here.

### G. Definition of done

Conflict detection precedes mutation, each resolution has a real tested effect and no malformed page can silently lose a change.

**Recovery/rollback:** Back up encrypted local DB before its schema upgrade. On an unknown schema or integrity failure stop capture/sync and preserve the file/key; never rebuild durable pending tables.

**Change discipline:** minimal owning-module diff; no unnecessary production dependency; source/contract/schema/UI/tests remain aligned. An unmet test or owner-controlled prerequisite stays explicitly blocked. Specification completion is not implementation completion.

---

<a id="task-37"></a>
## Atomic Task 37 - Finish durable sync outcomes and rejected-cash recovery

### A. Objective

**P0 · G6 · Requirements R06, R09, R10.** Dependencies: [T19](#task-19), [T35](#task-35), [T36](#task-36).

**Current → target:** Outbox, processed-operation receipts and coordinator exist; terminal/transient/unknown outcomes and physical cash resolution must be proved end to end. **Target:** Every captured operation reaches a durable posted or explicitly resolved recovery state without money loss, double posting or a terminalised transient failure.

**Non-goals:** no unrelated architecture rewrite, new mandatory hosted service, historical repricing or production action. The task is limited to the scope below and the binding decisions in Section 3. Acceptance is the task-specific test outcomes plus G.

### B. Scope

- MODIFY `QtsWebApp/QtsWebApp/Features/Sync/SyncOperationEndpoints.cs`, `QtsBilling.Maui/Services/DeviceSyncCoordinator.cs`, and `QtsBilling.Persistence.Sqlite/SqliteLocalStore.cs`.
- MODIFY shared `QtsBilling.Ui/Pages/Pos.razor` only for pending/recovery actions; extend `QtsBilling.Contracts/Sync/SyncDtos.cs` and local entities additively where required by explicit state fields.
- MODIFY `QtsBilling.Persistence.Sqlite.Tests/ReceiptAndSyncStateTests.cs`; CREATE `TestQtsBilling/Integration/SyncCashRecoveryTests.cs`.

### C. Verified anchors and NEW insertion points

[E29](#evidence-e29), [E31](#evidence-e31), [E33](#evidence-e33), [S17](#evidence-s17), [S26](#evidence-s26). These refer to the pinned baseline. Existing code is reused; all CREATE paths, new DTOs/routes and generated migration names in this task are **NEW**, not already-implemented capabilities. Read the complete owning method and its callers before changing the anchored slice.

### D. Ordered recipe

1. Define operation states Pending/InFlight/RetryDue/Posted/NeedsReview/RefundRecorded/Resolved without collapsing physical cash and financial posting. A transient retry/timeout never becomes a permanent inbox receipt that suppresses future attempts.
2. Claim one due operation per device scope, with durable backoff/attempt metadata and bounded single-flight coordination. Store terminal outcome and sale effect together server-side; apply receipt and corresponding local state together. On unknown commit outcome replay the exact key/body.
3. Add an authorised recovery action for rejected cash: record actual cash returned with custody reference, or create a corrected new operation linked to the rejected one after explicit operator confirmation. The old body remains immutable and cannot be “edited then retried” under the same key.
4. Device revocation, expired grant, new price, stock rejection and closed drawer produce readable recovery cases. Queue clearing, logout, catalog rebuild and app upgrades cannot delete unresolved money.

### E. Focused tests

**Files:** CREATE `TestQtsBilling/Integration/SyncCashRecoveryTests.cs`; MODIFY `QtsBilling.Persistence.Sqlite.Tests/ReceiptAndSyncStateTests.cs`.

- **T37.01** — Given server commit followed by lost response and repeated pushes, when reconnected, then exactly one sale/journal/stock effect and one durable receipt exist.
- **T37.02** — Given transient database/network failure, when retried later, then the operation can still post and no terminal rejection was fabricated.
- **T37.03** — Given cash collected but a rejected operation, when refunded/repaired, then physical custody and linked recovery facts account for the exact amount.
- **T37.04** — Given a killed process during receipt application, when restarted, then local sale/cash/outbox/receipt states are consistent.

Reuse the named fixture and only add cases needed for these changed behaviors. Retain applicable existing tenant, rollback, immutable-history and precision regressions; do not replace them with response-status-only mocks. UI tests also verify focus/labels and error recovery for the affected actions.

### F. Verification

**NOT RUN — SPEC_ONLY.** Apply the [verification prerequisites](#verification-contract).

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~SyncCashRecoveryTests"
```

Also run focused SQLite receipt/outbox tests in their existing project. Cross-store exactly-once delivery is not claimed; the invariant is one authoritative business effect per stable identity.

**PASS:** the listed tests/checks actually execute and satisfy every expected outcome, applicable host build succeeds, `git diff --check` is clean, and no new secret/server assembly leaks into client assets. Record command, environment, SHA, test count and result. Full suite and packaged-platform proof occur at G7; no pass is claimed here.

### G. Definition of done

At-least-once delivery produces one business effect, with all rejected money explicitly recoverable rather than discarded.

**Recovery/rollback:** Retain old payload schemas/receipts long enough to resolve outstanding operations or provide an explicit supported export/recovery path. Do not infer approval to purge local queues.

**Change discipline:** minimal owning-module diff; no unnecessary production dependency; source/contract/schema/UI/tests remain aligned. An unmet test or owner-controlled prerequisite stays explicitly blocked. Specification completion is not implementation completion.

---

<a id="task-38"></a>
## Atomic Task 38 - Prove native identity, signed grants, encryption and key recovery on Windows

### A. Objective

**P0 · G6 · Requirements R03, R04, R09, R12.** Dependencies: [T08](#task-08), [T09](#task-09), [T35](#task-35), [T36](#task-36), [T37](#task-37).

**Current → target:** PKCE, signed grants, SQLCipher and device adapters exist; historical docs admit native SecureStorage/capture verification gaps. **Target:** A real installed Windows client completes trusted local sign-in, bounded offline capture, restart/recovery and safe key rotation without public services.

**Non-goals:** no unrelated architecture rewrite, new mandatory hosted service, historical repricing or production action. The task is limited to the scope below and the binding decisions in Section 3. Acceptance is the task-specific test outcomes plus G.

### B. Scope

- MODIFY `QtsBilling.Maui/Services/OfflineGrantService.cs`, `QtsBilling.Maui/Services/SessionStore.cs`, and `QtsBilling.Maui/Services/LocalDatabaseKeyStore.cs` only for defects reproduced by the native checks.
- READ-ONLY `QtsBilling.Maui/Services/PkceLoopbackAuthenticator.cs`, `QtsBilling.Maui/Services/OidcTokenClient.cs`, `QtsBilling.Maui/Services/DeviceLocalPosService.cs`, and `QtsBilling.Persistence.Sqlite/SqliteDatabaseEncryption.cs`; retain existing implementations.
- CREATE `TestQtsBilling/Native/WindowsAcceptance.md` and `scripts/verify-windows-device.ps1`; MODIFY `QtsBilling.Persistence.Sqlite.Tests/SqliteEncryptionTests.cs`.

### C. Verified anchors and NEW insertion points

[E03](#evidence-e03), [E32](#evidence-e32), [E33](#evidence-e33), [E35](#evidence-e35), [E36](#evidence-e36), [E47](#evidence-e47), [S23](#evidence-s23). These refer to the pinned baseline. Existing code is reused; all CREATE paths, new DTOs/routes and generated migration names in this task are **NEW**, not already-implemented capabilities. Read the complete owning method and its callers before changing the anchored slice.

### D. Ordered recipe

1. Run the actual MAUI application under a standard Windows account with locally trusted HTTPS and no public network. Exercise system-browser PKCE callback, code/refresh replay rejection, current-membership revocation and restart persistence; compilation is not acceptance.
2. Before every offline cash capture validate pinned signature, exact device/business/location/register-session/currency scope, permission revision and expiry. Test clock rollback and revoked-device reconnect; never bypass expired grants to continue trading.
3. Demonstrate SQLCipher cold open, wrong/missing key refusal, safe schema upgrade with queued money and restart after interruption. Package a deliberate key recovery/escrow procedure protected by the owner; ordinary logout preserves pending DB and keys needed for recovery.
4. Define planned issuer/grant/TLS key rotation with a signed/trusted transition and overlap bounded by grant lifetime. Unexpected pin changes stop new capture. Record redacted native evidence with exact artifact hash, OS/WebView2 version and outcomes.

### E. Focused tests

**Files:** CREATE `TestQtsBilling/Native/WindowsAcceptance.md`; CREATE `scripts/verify-windows-device.ps1`; MODIFY `QtsBilling.Persistence.Sqlite.Tests/SqliteEncryptionTests.cs`.

- **T38.01** — Given an installed client and disconnected internet, when signing in through the local server then capturing offline, then both flows succeed within a valid grant.
- **T38.02** — Given wrong device/session/key, expired grant or clock rollback, when capturing, then no provisional money is acknowledged.
- **T38.03** — Given a crash/schema upgrade/key recovery while 100 pending sales exist, when restarted, then all operations and cash remain recoverable exactly once.
- **T38.04** — Given a planned rotation and an unexpected pin replacement, when reconnecting, then only the approved transition succeeds.

Reuse the named fixture and only add cases needed for these changed behaviors. Retain applicable existing tenant, rollback, immutable-history and precision regressions; do not replace them with response-status-only mocks. UI tests also verify focus/labels and error recovery for the affected actions.

### F. Verification

**NOT RUN — SPEC_ONLY.** Apply the [verification prerequisites](#verification-contract).

Use the explicit platform/release check below; do not report a .NET unit-test filter as native execution.

Later Windows commands: `dotnet build QtsBilling.Maui/QtsBilling.Maui.csproj -c Release`; NEW `pwsh -NoProfile -File scripts/verify-windows-device.ps1 -Mode LocalCore`. The script must distinguish automated assertions from operator-observed GUI steps and fail an unattended run that lacks required evidence.

**PASS:** the listed tests/checks actually execute and satisfy every expected outcome, applicable host build succeeds, `git diff --check` is clean, and no new secret/server assembly leaks into client assets. Record command, environment, SHA, test count and result. Full suite and packaged-platform proof occur at G7; no pass is claimed here.

### G. Definition of done

Real Windows/native evidence closes the platform gap; successful server-only tests are not substituted for device capture/security behavior.

**Recovery/rollback:** Preserve encrypted files and required key material; freeze new capture on failed trust recovery. No auto-wipe, plaintext fallback or certificate bypass is permitted.

**Change discipline:** minimal owning-module diff; no unnecessary production dependency; source/contract/schema/UI/tests remain aligned. An unmet test or owner-controlled prerequisite stays explicitly blocked. Specification completion is not implementation completion.

---

<a id="task-39"></a>
## Atomic Task 39 - Add bounded local document evidence and offline-safe document output

### A. Objective

**P1 · G5 · Requirements R04, R07, R11, R12.** Dependencies: [T24](#task-24), [T28](#task-28).

**Current → target:** HTML receipt/invoice rendering exists; document evidence has no complete owned-storage/operator workflow. **Target:** Core documents can be printed/exported and supporting files retained locally with tenant-safe access, version history and backup integration.

**Non-goals:** no unrelated architecture rewrite, new mandatory hosted service, historical repricing or production action. The task is limited to the scope below and the binding decisions in Section 3. Acceptance is the task-specific test outcomes plus G.

### B. Scope

- CREATE `QtsWebApp/QtsWebApp/Features/Documents/DocumentEndpoints.cs`, `QtsBilling.Contracts/Documents/DocumentDtos.cs`, and `QtsBilling.Ui/Components/DocumentEvidence.razor`.
- Add NEW document metadata in `QtsBilling.Persistence.Postgres/Entities/Platform.cs`, configuration and an additive migration; register owned storage/endpoint/client in current hosts.
- CREATE `TestQtsBilling/Integration/LocalDocumentTests.cs` and `TestQtsBilling/Ui/DocumentJourneyTests.cs`.
- INTEGRATION MODIFY only where needed: `QtsBilling.Ui/Layout/NavMenu.razor`, `QtsWebApp/QtsWebApp/Program.cs`, `QtsWebApp/QtsWebApp.Client/Program.cs`, and `QtsBilling.Maui/MauiProgram.cs` for the named page/client; no unrelated host changes.

### C. Verified anchors and NEW insertion points

[S04](#evidence-s04), [S07](#evidence-s07), [E24](#evidence-e24), [S22](#evidence-s22). These refer to the pinned baseline. Existing code is reused; all CREATE paths, new DTOs/routes and generated migration names in this task are **NEW**, not already-implemented capabilities. Read the complete owning method and its callers before changing the anchored slice.

### D. Ordered recipe

1. Define NEW document evidence endpoints scoped to an allowed invoice/expense/purchase aggregate. Accept bounded PDF/JPEG/PNG bytes with type inspection, size/count limits, random storage keys and no path/URL supplied by the client. Reject executable/HTML/SVG and remote fetches.
2. Stage bytes in a private owned directory outside webroot; compute hash and verify complete write before committing metadata/audit. Link staged key in a short DB transaction and clean orphaned staging files with a bounded local job. No file/process I/O is held inside financial transactions.
3. Download through current aggregate authorization with safe filename/content disposition and no inline active document execution. Replace through a new linked version; retain posted evidence history and explicit retention policy.
4. Integrate evidence UI with existing invoice/expense/purchase pages and Task 24 print adapter. Local output must not load remote assets, embed credentials or rely on a hosted converter. Include evidence inventory/hash manifest in backups.

### E. Focused tests

**Files:** CREATE `TestQtsBilling/Integration/LocalDocumentTests.cs`; CREATE `TestQtsBilling/Ui/DocumentJourneyTests.cs`.

- **T39.01** — Given a valid local evidence file, when attached and retrieved, then only the authorised tenant/aggregate receives matching bytes/hash.
- **T39.02** — Given traversal, content-type spoofing, oversized data, active HTML or another tenant ID, when uploaded/downloaded, then no executable/public file is created.
- **T39.03** — Given a crash between file staging and metadata commit, when cleanup runs, then no referenced evidence is lost and only verified orphans are removed.
- **T39.04** — Given an issued invoice, when evidence is replaced, then the old version and audit remain accessible under retention rules.

Reuse the named fixture and only add cases needed for these changed behaviors. Retain applicable existing tenant, rollback, immutable-history and precision regressions; do not replace them with response-status-only mocks. UI tests also verify focus/labels and error recovery for the affected actions.

### F. Verification

**NOT RUN — SPEC_ONLY.** Apply the [verification prerequisites](#verification-contract).

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~LocalDocumentTests|FullyQualifiedName~DocumentJourneyTests"
```

**PASS:** the listed tests/checks actually execute and satisfy every expected outcome, applicable host build succeeds, `git diff --check` is clean, and no new secret/server assembly leaks into client assets. Record command, environment, SHA, test count and result. Full suite and packaged-platform proof occur at G7; no pass is claimed here.

### G. Definition of done

Documents/evidence are local, bounded, authorisation-protected and included in recovery; no storage/rendering SaaS is required.

**Recovery/rollback:** Disable uploads on regression while preserving referenced files/metadata. Never delete a posted document’s evidence to restore UI availability.

**Change discipline:** minimal owning-module diff; no unnecessary production dependency; source/contract/schema/UI/tests remain aligned. An unmet test or owner-controlled prerequisite stays explicitly blocked. Specification completion is not implementation completion.

---

<a id="task-40"></a>
## Atomic Task 40 - Make backup, restore and recovery genuinely Docker-free

### A. Objective

**P0 · G7 · Requirements R01, R02, R12.** Dependencies: [T11](#task-11), [T12](#task-12), [T39](#task-39).

**Current → target:** The direct backup path still invokes Docker; restore/drill policy and actual device/key/document recovery must be aligned. **Target:** Native PostgreSQL tools plus owner-controlled storage provide verifiable backup, PITR and full-instance recovery without containers/cloud.

**Non-goals:** no unrelated architecture rewrite, new mandatory hosted service, historical repricing or production action. The task is limited to the scope below and the binding decisions in Section 3. Acceptance is the task-specific test outcomes plus G.

### B. Scope

- MODIFY `scripts/backup-postgres.ps1`, `scripts/restore-postgres.ps1`, and `scripts/restore-drill.ps1`.
- CREATE `scripts/configure-local-recovery.ps1` for explicit WAL/archive/retention configuration and NEW `docs/operations/recovery.md`.
- CREATE `TestQtsBilling/Operations/NativeRecovery.Tests.ps1`; read exact native tool parameters from the packaged PostgreSQL version.

### C. Verified anchors and NEW insertion points

[S12](#evidence-s12), [S13](#evidence-s13), [S22](#evidence-s22), [E60](#evidence-e60). These refer to the pinned baseline. Existing code is reused; all CREATE paths, new DTOs/routes and generated migration names in this task are **NEW**, not already-implemented capabilities. Read the complete owning method and its callers before changing the anchored slice.

### D. Ordered recipe

1. Make Native the explicit default mode. In Native mode never probe/call Docker, including cleanup/version metadata; use packaged pg_dump/pg_restore/psql and validate every exit code. Docker mode, if retained, is an optional clearly selected adapter.
2. Keep passwords out of process arguments/logs; use protected credential files or scoped environment handling with cleanup and restricted ACLs. Bind backup manifest to instance/database/schema/artifact/tool version and actual bytes, not supplied labels alone.
3. Back up PostgreSQL, attachments, key material and trust/recovery metadata with separate access controls. Implement tested base-backup/WAL archival and retention to meet the declared RPO; pg_dump remains a portable snapshot, not a PITR claim.
4. Restore only to an explicitly named isolated target by default. Demonstrate financial/open-item/stock/attachment hash tie-outs and device replay reconciliation after epoch change. Production restore is a distinct authorised operation with a pre-restore copy and write freeze.
5. Schedule backups locally, alert on failures/full disk/expired retention, and test recovery from a second physical/off-machine copy. Never delete the sole good backup while pruning.

### E. Focused tests

**Files:** CREATE `TestQtsBilling/Operations/NativeRecovery.Tests.ps1`.

- **T40.01** — Given no Docker executable or internet, when native backup and isolated restore run, then no Docker invocation occurs and the recovered state reconciles.
- **T40.02** — Given altered dump/manifest, wrong environment or missing WAL segment/key/file, when restoring, then admission fails before overwriting a target.
- **T40.03** — Given a failure after the latest base backup, when recovering to a known point, then measured data loss/recovery time meet the declared targets.
- **T40.04** — Given pending device operations around recovery, when reconnecting, then epoch handling and stable operation identities prevent duplicate posted money.

Reuse the named fixture and only add cases needed for these changed behaviors. Retain applicable existing tenant, rollback, immutable-history and precision regressions; do not replace them with response-status-only mocks. UI tests also verify focus/labels and error recovery for the affected actions.

### F. Verification

**NOT RUN — SPEC_ONLY.** Apply the [verification prerequisites](#verification-contract).

```text
pwsh -NoProfile -File TestQtsBilling/Operations/NativeRecovery.Tests.ps1
```

NEW harness command: `pwsh -NoProfile -File TestQtsBilling/Operations/NativeRecovery.Tests.ps1`. It must create only allowlisted disposable targets and record all external tool exits; production restore requires separate approval.

**PASS:** the listed tests/checks actually execute and satisfy every expected outcome, applicable host build succeeds, `git diff --check` is clean, and no new secret/server assembly leaks into client assets. Record command, environment, SHA, test count and result. Full suite and packaged-platform proof occur at G7; no pass is claimed here.

### G. Definition of done

A complete owned-storage recovery drill, not just a successful dump command, proves the declared RPO/RTO and financial consistency.

**Recovery/rollback:** Keep originals and old backups until the restore is accepted. Never automatically roll back to a retired database or discard post-backup money.

**Change discipline:** minimal owning-module diff; no unnecessary production dependency; source/contract/schema/UI/tests remain aligned. An unmet test or owner-controlled prerequisite stays explicitly blocked. Specification completion is not implementation completion.

---

<a id="task-41"></a>
## Atomic Task 41 - Package secure offline SingleHost and LocalNetwork installations

### A. Objective

**P1 · G7 · Requirements R01, R02, R03, R12, R13.** Dependencies: [T05](#task-05), [T09](#task-09), [T10](#task-10), [T11](#task-11), [T40](#task-40).

**Current → target:** Unpackaged ZIP publication exists; installation depends on manual runtime/database/trust setup and online tool acquisition. **Target:** A non-developer can install/start/recover supported owned-server profiles from a verified offline kit.

**Non-goals:** no unrelated architecture rewrite, new mandatory hosted service, historical repricing or production action. The task is limited to the scope below and the binding decisions in Section 3. Acceptance is the task-specific test outcomes plus G.

### B. Scope

- CREATE `scripts/install-local.ps1`, `scripts/install-linux.sh`, and `scripts/uninstall-local.ps1` with distinct non-destructive defaults.
- MODIFY `QtsBilling.Maui/QtsBilling.Maui.csproj` and `QtsBilling.Maui/Platforms/Windows/Package.appxmanifest` only for actual packaging/trust needs; service integration changes stay in server Program/host project if required.
- CREATE `TestQtsBilling/Operations/OfflineInstall.Tests.ps1` and NEW `docs/operations/offline-install.md`.

### C. Verified anchors and NEW insertion points

[S18](#evidence-s18), [S25](#evidence-s25), [S15](#evidence-s15), [S01](#evidence-s01), [S02](#evidence-s02), [E35](#evidence-e35). These refer to the pinned baseline. Existing code is reused; all CREATE paths, new DTOs/routes and generated migration names in this task are **NEW**, not already-implemented capabilities. Read the complete owning method and its callers before changing the anchored slice.

### D. Ordered recipe

1. Verify signed manifest/trust out of band before running the kit. Install approved local PostgreSQL/tools and WebView2 prerequisites offline; no implicit winget/apt/curl/bootstrap download. Record OS/native prerequisites and refuse unsupported hosts clearly.
2. Create dedicated least-privilege server/DB accounts, private data/keys/documents/log directories and OS services with restart policy. Database migration credentials are not granted to the runtime. Do not open PostgreSQL to every client; only server/operator roles connect.
3. Configure owned HTTPS origin, certificate SAN/trust, OIDC/grant/DP keys and firewall rules for loopback or explicit LAN peers. Integrate Task 05 bootstrap and local backup scheduling; never set Production to Development to get an owner.
4. Provide signed Windows desktop package or a reviewed unpackaged installer with internally trusted signer. Uninstall removes binaries/services only by default and explicitly preserves business data, keys and pending-device store.
5. Test cold install under standard-client privileges and administrative installer privileges separately. LocalNetwork Linux server and Windows client use the same contracts; no mobile/macOS scope is added.

### E. Focused tests

**Files:** CREATE `TestQtsBilling/Operations/OfflineInstall.Tests.ps1`.

- **T41.01** — Given a clean supported Windows machine and offline kit, when SingleHost is installed, then owner setup and a cash sale work with public network denied.
- **T41.02** — Given a clean Linux server/Windows terminal LAN, when installed, then local HTTPS/native sign-in and browser workflows operate without public DNS/identity services.
- **T41.03** — Given wrong signer/hash, missing prerequisite or weak directory permissions, when installing, then failure is explicit before live data is touched.
- **T41.04** — Given uninstall/update on a terminal with pending cash, when performed, then durable data/key retention is enforced.

Reuse the named fixture and only add cases needed for these changed behaviors. Retain applicable existing tenant, rollback, immutable-history and precision regressions; do not replace them with response-status-only mocks. UI tests also verify focus/labels and error recovery for the affected actions.

### F. Verification

**NOT RUN — SPEC_ONLY.** Apply the [verification prerequisites](#verification-contract).

```text
pwsh -NoProfile -File TestQtsBilling/Operations/OfflineInstall.Tests.ps1
```

Proposed installer commands are NEW and must expose dry-run/verify modes. Run acceptance only on disposable VMs or an explicitly approved pilot host, not this review environment.

**PASS:** the listed tests/checks actually execute and satisfy every expected outcome, applicable host build succeeds, `git diff --check` is clean, and no new secret/server assembly leaks into client assets. Record command, environment, SHA, test count and result. Full suite and packaged-platform proof occur at G7; no pass is claimed here.

### G. Definition of done

Both supported deployment profiles are reproducible offline and never require developer tooling or demo credentials at runtime.

**Recovery/rollback:** Stop services, keep previous binaries and preserve all data/keys. Uninstall is never a database/local-queue wipe; destructive retirement needs separate authorisation.

**Change discipline:** minimal owning-module diff; no unnecessary production dependency; source/contract/schema/UI/tests remain aligned. An unmet test or owner-controlled prerequisite stays explicitly blocked. Specification completion is not implementation completion.

---

<a id="task-42"></a>
## Atomic Task 42 - Create a local release pipeline and signed offline distribution kit

### A. Objective

**P1 · G7 · Requirements R01, R02, R13.** Dependencies: [T11](#task-11), [T38](#task-38), [T40](#task-40), [T41](#task-41).

**Current → target:** Release is GitHub-specific and rebuilds final artifacts after a different verification build. **Target:** The same local command builds/verifies/packages a candidate and promotes its exact signed digest, with GitHub optional.

**Non-goals:** no unrelated architecture rewrite, new mandatory hosted service, historical repricing or production action. The task is limited to the scope below and the binding decisions in Section 3. Acceptance is the task-specific test outcomes plus G.

### B. Scope

- CREATE `scripts/build-release.ps1`, `scripts/verify-release.ps1`, and `scripts/sign-release.ps1`.
- MODIFY `.github/workflows/release.yml` to call the local pipeline; READ-ONLY retained .csproj/RID settings and `scripts/verify-wasm-bundle.ps1`.
- CREATE `TestQtsBilling/Operations/ReleaseKit.Tests.ps1`; record local signature/trust and offline maintenance procedure in NEW `docs/operations/releases.md`.

### C. Verified anchors and NEW insertion points

[S14](#evidence-s14), [S15](#evidence-s15), [S25](#evidence-s25), [E35](#evidence-e35). These refer to the pinned baseline. Existing code is reused; all CREATE paths, new DTOs/routes and generated migration names in this task are **NEW**, not already-implemented capabilities. Read the complete owning method and its callers before changing the anchored slice.

### D. Ordered recipe

1. Build the retained solution components using the sealed SDK/workload/feed. Produce Windows/Linux server and Windows desktop outputs once, plus native import/operator tools, DB migrations, dependencies, licences, operator docs and recovery scripts.
2. Generate a manifest binding source SHA, schema/protocol/client version ranges, RID, dependency inventory and every file hash. Sign with the owner’s local release authority. Store no signing key in source or artifact.
3. Run verification against the exact packaged binaries under LocalCore, including installer/DB schema/native smoke/WASM scan and no public network. Promotion copies verified bytes without another build; signature addition must be included in final verification.
4. Allow local folder/removable-media distribution. GitHub release upload is an optional adapter after success, not necessary for install/use/update. Avoid mandatory external timestamp/signing service; document internal trust/expiry and offline update validation.

### E. Focused tests

**Files:** CREATE `TestQtsBilling/Operations/ReleaseKit.Tests.ps1`.

- **T42.01** — Given the sealed build kit with GitHub unavailable, when building/signing/verifying, then an installable complete release kit is produced locally.
- **T42.02** — Given one altered artifact or mismatched schema/client manifest, when verifying/promoting, then promotion refuses it.
- **T42.03** — Given a candidate that passed source tests but whose packaged desktop fails startup, when verifying release, then the release is blocked.

Reuse the named fixture and only add cases needed for these changed behaviors. Retain applicable existing tenant, rollback, immutable-history and precision regressions; do not replace them with response-status-only mocks. UI tests also verify focus/labels and error recovery for the affected actions.

### F. Verification

**NOT RUN — SPEC_ONLY.** Apply the [verification prerequisites](#verification-contract).

```text
pwsh -NoProfile -File TestQtsBilling/Operations/ReleaseKit.Tests.ps1
```

NEW later entrypoints: `pwsh -NoProfile -File scripts/build-release.ps1 -Profile LocalCore`; `pwsh -NoProfile -File scripts/verify-release.ps1 -Profile LocalCore`. Final parameters must require a candidate manifest path and refuse ambient production targets.

**PASS:** the listed tests/checks actually execute and satisfy every expected outcome, applicable host build succeeds, `git diff --check` is clean, and no new secret/server assembly leaks into client assets. Record command, environment, SHA, test count and result. Full suite and packaged-platform proof occur at G7; no pass is claimed here.

### G. Definition of done

The shipped package is the tested package, with an offline-verifiable trust chain and no compulsory CI-hosting/distribution service.

**Recovery/rollback:** Withdraw a bad artifact by digest and distribute a newly signed candidate; never silently replace bytes under an existing approved digest.

**Change discipline:** minimal owning-module diff; no unnecessary production dependency; source/contract/schema/UI/tests remain aligned. An unmet test or owner-controlled prerequisite stays explicitly blocked. Specification completion is not implementation completion.

---

<a id="task-43"></a>
## Atomic Task 43 - Complete native admission, updates and honest rollback controls

### A. Objective

**P0 · G7 · Requirements R02, R05, R09, R12, R13, R14.** Dependencies: [T01](#task-01), [T12](#task-12), [T38](#task-38), [T40](#task-40), [T42](#task-42).

**Current → target:** A cutover script exists but remains coupled to historical tooling and cannot substitute for a complete native install/update admission. **Target:** Read-only admission and authorised apply support new/existing businesses, compatible device queues and recoverable schema/application updates.

**Non-goals:** no unrelated architecture rewrite, new mandatory hosted service, historical repricing or production action. The task is limited to the scope below and the binding decisions in Section 3. Acceptance is the task-specific test outcomes plus G.

### B. Scope

- MODIFY `scripts/qb30-cutover.ps1` and `scripts/qb39-cutover-safety.ps1`; CREATE `scripts/update-local.ps1`.
- READ-ONLY `QtsBilling.DataImport/Program.cs`, release manifest and existing backup/restore entrypoints; no call to retired Migration tooling remains.
- CREATE `TestQtsBilling/Operations/NativeAdmission.Tests.ps1`.

### C. Verified anchors and NEW insertion points

[E60](#evidence-e60), [E43](#evidence-e43), [S18](#evidence-s18), [S12](#evidence-s12), [S15](#evidence-s15). These refer to the pinned baseline. Existing code is reused; all CREATE paths, new DTOs/routes and generated migration names in this task are **NEW**, not already-implemented capabilities. Read the complete owning method and its callers before changing the anchored slice.

### D. Ordered recipe

1. Bind admission to installation/DB identity, source/target schema, release digest, key/trust metadata, backup/restore evidence, business approvals and device payload-version compatibility. Compare confirmation codeword securely and never print it.
2. Provide explicit NewBusiness and ExistingBusiness modes. NewBusiness requires approved zero-opening attestation; ExistingBusiness requires the signed native package/report and exact reconciled controls. An absent required input fails; do not replace a gate with SkipImport success.
3. ReadOnlyCheck performs no import, migration, bootstrap, enrollment, backup creation or service restart. Apply requires owner approval and maintenance lock/write drain, then executes reviewed forward schema/atomic native data steps and postchecks.
4. Preserve pending desktop operations during client updates and refuse downgrades that cannot read their schema/payload. Roll application binaries back only when data/schema compatibility permits. After committed business writes, prefer fix-forward; any restore must reconcile later cash/device operations explicitly.
5. Document stop conditions and operator handover: immutable journal balance per entry, inventory quantity/value, AR/AP/deposits/transit, scoped owners, numbers, cleared-vs-pending payments, attachment/key availability and absence of retired runtime dependencies.

### E. Focused tests

**Files:** CREATE `TestQtsBilling/Operations/NativeAdmission.Tests.ps1`.

- **T43.01** — Given read-only admission, when run against a prepared environment, then DB/files/services remain unchanged while missing prerequisites fail visibly.
- **T43.02** — Given wrong codeword/environment/package/backup or unresolved finance exception, when Apply is requested, then execution stops before writes.
- **T43.03** — Given a new empty business without historical data, when approved, then admission succeeds through the zero-opening path without bypassing financial setup.
- **T43.04** — Given queued device money during an incompatible downgrade, when updating, then the updater refuses rather than deleting/converting operations silently.

Reuse the named fixture and only add cases needed for these changed behaviors. Retain applicable existing tenant, rollback, immutable-history and precision regressions; do not replace them with response-status-only mocks. UI tests also verify focus/labels and error recovery for the affected actions.

### F. Verification

**NOT RUN — SPEC_ONLY.** Apply the [verification prerequisites](#verification-contract).

```text
pwsh -NoProfile -File TestQtsBilling/Operations/NativeAdmission.Tests.ps1
```

NEW safety test command: `pwsh -NoProfile -File TestQtsBilling/Operations/NativeAdmission.Tests.ps1`. Existing `scripts/qb39-cutover-safety.ps1` remains a required adapted regression, not stale proof of the new path.

**PASS:** the listed tests/checks actually execute and satisfy every expected outcome, applicable host build succeeds, `git diff --check` is clean, and no new secret/server assembly leaks into client assets. Record command, environment, SHA, test count and result. Full suite and packaged-platform proof occur at G7; no pass is claimed here.

### G. Definition of done

Native installation/update admission is fail-closed and independent of a legacy source; rollback never sacrifices unreconciled money.

**Recovery/rollback:** Follow the documented stop/fix-forward/restore decision tree. Production restore, destructive retirement and release promotion still require their explicit owner authorisation.

**Change discipline:** minimal owning-module diff; no unnecessary production dependency; source/contract/schema/UI/tests remain aligned. An unmet test or owner-controlled prerequisite stays explicitly blocked. Specification completion is not implementation completion.

---

<a id="task-44"></a>
## Atomic Task 44 - Repair CI invocation defects and align automated checks with local release

### A. Objective

**P0 · G7 · Requirements R02, R05, R13, R15.** Dependencies: [T01](#task-01), [T09](#task-09), [T11](#task-11), [T12](#task-12), [T38](#task-38), [T42](#task-42), [T43](#task-43).

**Current → target:** CI/Release use host-run service settings inconsistently, Verify lacks a required argument, and fixture can fall back to owner privileges. **Target:** Optional GitHub CI runs the same fail-closed local checks with correct PostgreSQL topology and restricted runtime credentials.

**Non-goals:** no unrelated architecture rewrite, new mandatory hosted service, historical repricing or production action. The task is limited to the scope below and the binding decisions in Section 3. Acceptance is the task-specific test outcomes plus G.

### B. Scope

- MODIFY `.github/workflows/ci.yml`, `.github/workflows/release.yml`, and `scripts/provision-postgres-roles.ps1`.
- MODIFY `TestQtsBilling/Integration/PostgresApiFixture.cs` to require a distinct validated runtime role for restricted-role acceptance; retain a clearly named owner-only migration fixture for schema tests.
- CREATE `TestQtsBilling/Operations/CiContract.Tests.ps1`; extend `TestQtsBilling/Integration/ModelParityTests.cs` from Task 01.

### C. Verified anchors and NEW insertion points

[S13](#evidence-s13), [S14](#evidence-s14), [S15](#evidence-s15), [S24](#evidence-s24). These refer to the pinned baseline. Existing code is reused; all CREATE paths, new DTOs/routes and generated migration names in this task are **NEW**, not already-implemented capabilities. Read the complete owning method and its callers before changing the anchored slice.

### D. Ordered recipe

1. Verify the minimal hostname/Verify-argument fixes shipped in Task 01 remain intact: host-run jobs use mapped localhost/port; only container jobs use service DNS. Preserve separate privileged Provision and runtime-only Verify parameter contracts in the local pipeline.
2. Remove obsolete Migration test references after Task 12. Call local build/verify scripts and pin actions/images/toolchains to reviewed identities. Acquisition may occur in CI maintenance setup, but disconnected acceptance must use the sealed kit.
3. Run real PG integration tests as DML-only qts_app and migrations as qts_migrator. Missing runtime connection/failed assertions/skipped required tests fail the gate; never count fallback-owner execution as least-privilege proof.
4. Add model-drift/clean+upgrade invariants, asset/WASM/secret scans and the Windows native acceptance evidence gate. Verify final release artifacts and preserve test reports keyed to exact SHA/digest. Keep focused local filters; full regression belongs here/release.
5. Record current run results rather than copying historical 78/181 counts. Repository protection/required-review configuration is an owner administrative gate, not something this read-only specification changes.

### E. Focused tests

**Files:** CREATE `TestQtsBilling/Operations/CiContract.Tests.ps1`; MODIFY `TestQtsBilling/Integration/PostgresApiFixture.cs`; MODIFY `TestQtsBilling/Integration/ModelParityTests.cs`.

- **T44.01** — Given Psql Verify with only runtime credentials, when invoked, then SELECT and denied-DDL assertions run without requiring an admin password.
- **T44.02** — Given missing runtime connection or unmigrated model, when CI runs, then it fails rather than silently using owner permissions or ignoring schema drift.
- **T44.03** — Given a failing native/disconnected/final-artifact check, when publishing, then no release promotion runs.
- **T44.04** — Given the local verifier and GitHub adapter, when run on the same candidate, then they apply the same required gate list.

Reuse the named fixture and only add cases needed for these changed behaviors. Retain applicable existing tenant, rollback, immutable-history and precision regressions; do not replace them with response-status-only mocks. UI tests also verify focus/labels and error recovery for the affected actions.

### F. Verification

**NOT RUN — SPEC_ONLY.** Apply the [verification prerequisites](#verification-contract).

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~ModelParityTests|FullyQualifiedName~HarnessSafetyTests"
```

Additional NEW static/script regression: `pwsh -NoProfile -File TestQtsBilling/Operations/CiContract.Tests.ps1`. Do not install a database, run hosted jobs or modify repository protections during this specification review.

**PASS:** the listed tests/checks actually execute and satisfy every expected outcome, applicable host build succeeds, `git diff --check` is clean, and no new secret/server assembly leaks into client assets. Record command, environment, SHA, test count and result. Full suite and packaged-platform proof occur at G7; no pass is claimed here.

### G. Definition of done

CI topology, privilege and artifact verification are correct and executable locally; no green status comes from missing/skipped required proof.

**Recovery/rollback:** Disable promotion on verifier regression; do not bypass checks to restore a green badge. Keep old signed artifact available only if its schema/data compatibility remains valid.

**Change discipline:** minimal owning-module diff; no unnecessary production dependency; source/contract/schema/UI/tests remain aligned. An unmet test or owner-controlled prerequisite stays explicitly blocked. Specification completion is not implementation completion.

---

<a id="task-45"></a>
## Atomic Task 45 - Run integrated business, disconnected and recovery acceptance on the candidate

### A. Objective

**P0 · G7 · Requirements R01, R02, R03, R04, R05, R06, R07, R08, R09, R10, R11, R12, R13.** Dependencies: [T14](#task-14), [T15](#task-15), [T16](#task-16), [T18](#task-18), [T20](#task-20), [T21](#task-21), [T22](#task-22), [T23](#task-23), [T24](#task-24), [T25](#task-25), [T26](#task-26), [T27](#task-27), [T28](#task-28), [T29](#task-29), [T30](#task-30), [T31](#task-31), [T32](#task-32), [T33](#task-33), [T34](#task-34), [T38](#task-38), [T39](#task-39), [T40](#task-40), [T41](#task-41), [T42](#task-42), [T43](#task-43), [T44](#task-44).

**Current → target:** Individual code/tests and old evidence do not demonstrate a complete autonomous release candidate. **Target:** One exact candidate passes the full scoped business lifecycle, failure/security/no-egress matrix and measured recovery on supported hosts.

**Non-goals:** no unrelated architecture rewrite, new mandatory hosted service, historical repricing or production action. The task is limited to the scope below and the binding decisions in Section 3. Acceptance is the task-specific test outcomes plus G.

### B. Scope

- MODIFY `TestQtsBilling/Ui/BrowserFixture.cs` from Task 02 and CREATE `TestQtsBilling/Ui/LocalCoreJourneysTests.cs`; reuse real API/PG fixtures and the test-only offline-cached browser dependency approved in Task 11.
- CREATE `scripts/accept-local-core.ps1`; MODIFY `TestQtsBilling/Native/WindowsAcceptance.md` from Task 38 and record candidate results in NEW `docs/operations/acceptance-ledger.md`.
- READ-ONLY all business code except targeted fixes raised as explicit failed acceptance cases; no opportunistic refactors.

### C. Verified anchors and NEW insertion points

[S24](#evidence-s24), [S14](#evidence-s14), [S15](#evidence-s15), [E53](#evidence-e53), [E54](#evidence-e54), [S04](#evidence-s04), [S17](#evidence-s17). These refer to the pinned baseline. Existing code is reused; all CREATE paths, new DTOs/routes and generated migration names in this task are **NEW**, not already-implemented capabilities. Read the complete owning method and its callers before changing the anchored slice.

### D. Ordered recipe

1. Execute J01–J10 below against the packaged candidate under restricted runtime credentials, with public egress blocked and cold caches. Record attempted network destinations, exact hashes, host versions, fixture sizes and assertions.
2. Drive actual browser interactions for all new screens and actual installed Windows native sign-in/capture/print/recovery. An HTML render smoke or WebApplicationFactory test is not native/printer proof.
3. Inject timeout-after-commit, duplicate concurrent requests, rollback, stale versions, clock/grant expiry, revocation, full disk, corrupt projection, interrupted schema/receipt update and server restore. Assert per-entry finance and per-operation cash/state, not only total net balance.
4. Measure the proposed performance envelope, keyboard/zoom/a11y and recovery targets. Any unmet target produces a named failing case or explicitly approved reduced deployment envelope; no silent exclusion.
5. Promote only the tested digest when all required cases pass. Optional excluded-provider tests must assert disabled behavior; they cannot turn core tests into skips.

### E. Focused tests

**Files:** CREATE `TestQtsBilling/Ui/LocalCoreJourneysTests.cs`; CREATE `scripts/accept-local-core.ps1`; MODIFY `TestQtsBilling/Native/WindowsAcceptance.md`.

- **T45.01** — Given the exact release candidate, when retail and wholesale purchase/order/delivery/invoice/refund journeys run, then UI results, GL/subledgers, stock/value and cash custody reconcile.
- **T45.02** — Given blocked public network and cold installation, when owner setup, reset, native login, sales, reports, print and recovery run, then no mandatory external request exists.
- **T45.03** — Given each injected crash/replay/revocation case, when recovered, then every acknowledged unit of cash is posted once or remains a visible unresolved/recovered case.
- **T45.04** — Given an altered candidate binary after tests, when promoting, then digest verification refuses it.

Reuse the named fixture and only add cases needed for these changed behaviors. Retain applicable existing tenant, rollback, immutable-history and precision regressions; do not replace them with response-status-only mocks. UI tests also verify focus/labels and error recovery for the affected actions.

### F. Verification

**NOT RUN — SPEC_ONLY.** Apply the [verification prerequisites](#verification-contract).

```text
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~LocalCoreJourneysTests"
```

Full regression and both platform package checks run once per candidate in CI/release. The later NEW acceptance entrypoint is `pwsh -NoProfile -File scripts/accept-local-core.ps1 -Manifest <candidate-manifest> -Profile LocalCore`; replace the manifest argument with the actual verified file path.

**PASS:** the listed tests/checks actually execute and satisfy every expected outcome, applicable host build succeeds, `git diff --check` is clean, and no new secret/server assembly leaks into client assets. Record command, environment, SHA, test count and result. Full suite and packaged-platform proof occur at G7; no pass is claimed here.

### G. Definition of done

The bounded project is an integrated candidate with verifiable evidence; no assertion of production readiness rests on source presence or a prior test count.

**Recovery/rollback:** On any required failure do not release; retain diagnostic fixtures/artifact metadata and fix only the reproduced issue. No automatic production mutation is part of acceptance.

**Change discipline:** minimal owning-module diff; no unnecessary production dependency; source/contract/schema/UI/tests remain aligned. An unmet test or owner-controlled prerequisite stays explicitly blocked. Specification completion is not implementation completion.

---

<a id="task-46"></a>
## Atomic Task 46 - Reconcile documentation, scope and owner-controlled production admission

### A. Objective

**P1 · G7 · Requirements R12, R13, R14, R15.** Dependencies: [T45](#task-45).

**Current → target:** Historical README/readiness/deployment/task claims contradict current source and do not constitute pilot/production approval. **Target:** One current capability/operations record accurately distinguishes implemented, verified, excluded and owner-blocked work, enabling an approved local pilot.

**Non-goals:** no unrelated architecture rewrite, new mandatory hosted service, historical repricing or production action. The task is limited to the scope below and the binding decisions in Section 3. Acceptance is the task-specific test outcomes plus G.

### B. Scope

- MODIFY `README.md`, `docs/migration/readiness.md`, `docs/migration/task-status.md`, `docs/migration/deployment.md`, and `docs/migration/decisions.md` with scoped current-state corrections.
- CREATE `docs/operations/capabilities.md` and `docs/operations/pilot-handover.md`; link the current local install/recovery/dependency/release runbooks.
- CREATE `TestQtsBilling/Operations/DocumentationContract.Tests.ps1` to check referenced routes/commands/files and active retired-provider/platform trace rules.

### C. Verified anchors and NEW insertion points

[S19](#evidence-s19), [S20](#evidence-s20), [S21](#evidence-s21), [S22](#evidence-s22), [S23](#evidence-s23), [E46](#evidence-e46). These refer to the pinned baseline. Existing code is reused; all CREATE paths, new DTOs/routes and generated migration names in this task are **NEW**, not already-implemented capabilities. Read the complete owning method and its callers before changing the anchored slice.

### D. Ordered recipe

1. Replace blanket DONE/production-ready assertions with per-capability source, UI, test/candidate hash and verification state. Mark card acquiring/remote feeds/vertical ERP modules explicitly excluded from LocalCore, not unfinished compulsory dependencies.
2. Remove retired provider/desktop references and source-specific examples from active supported docs after Task 12. Preserve immutable migration history and any legally required financial audit; historical Git/releases/backups purge remains separately authorised.
3. Publish local operator procedures for setup, user/device recovery, daily till closure, procurement, finance close, backup/restore and offline rejected cash. Each command must match implemented scripts and be tested on the named platform.
4. Prepare pilot acceptance record naming business/location/devices/cashiers, finance-approved settings/openings, trust/key custodian, backup target, acceptance window, rollback/fix-forward owner and explicit production codeword. Without these, label Production admission BLOCKED_EXTERNAL, not software incomplete or approved.
5. Complete the final traceability ledger for every R requirement and acceptance journey. Freeze the candidate and record approval separately; this task does not itself deploy, merge or start a pilot.

### E. Focused tests

**Files:** CREATE `TestQtsBilling/Operations/DocumentationContract.Tests.ps1`.

- **T46.01** — Given current source/routes/scripts, when documentation checks run, then no broken supported path/command or stale compulsory SaaS instruction remains.
- **T46.02** — Given absent pilot owner, opening valuation, backup recovery evidence or codeword, when generating handover, then production admission remains explicitly blocked.
- **T46.03** — Given all signed acceptance/owner inputs, when reviewing the release record, then the exact candidate, exclusions and recovery responsibilities are unambiguous.

Reuse the named fixture and only add cases needed for these changed behaviors. Retain applicable existing tenant, rollback, immutable-history and precision regressions; do not replace them with response-status-only mocks. UI tests also verify focus/labels and error recovery for the affected actions.

### F. Verification

**NOT RUN — SPEC_ONLY.** Apply the [verification prerequisites](#verification-contract).

```text
pwsh -NoProfile -File TestQtsBilling/Operations/DocumentationContract.Tests.ps1
```

NEW validation: `pwsh -NoProfile -File TestQtsBilling/Operations/DocumentationContract.Tests.ps1`. Final production actions still follow issue → branch → focused checks → PR → full CI/review → explicit owner codeword where irreversible.

**PASS:** the listed tests/checks actually execute and satisfy every expected outcome, applicable host build succeeds, `git diff --check` is clean, and no new secret/server assembly leaks into client assets. Record command, environment, SHA, test count and result. Full suite and packaged-platform proof occur at G7; no pass is claimed here.

### G. Definition of done

One truthful local-product scope and readiness record is handed over; code completion is distinguished from owner approval and real production execution.

**Recovery/rollback:** Correct documentation without rewriting audit/history or asserting a retroactive approval. Withdraw an outdated readiness statement when evidence changes.

**Change discipline:** minimal owning-module diff; no unnecessary production dependency; source/contract/schema/UI/tests remain aligned. An unmet test or owner-controlled prerequisite stays explicitly blocked. Specification completion is not implementation completion.

---

<a id="traceability"></a>
## 6. Traceability, production gates, blockers and verification ledger

### 6.1 Requirement → task → test → acceptance

Each test ID below is specified in the corresponding task's E section. These are required tests, **not executed results**. All task scenarios are the minimum focused set; add a case only for a newly discovered regression, not a cosmetic coverage percentage.

| Requirement | Owning tasks | Focused test references | Acceptance |
|---|---|---|---|
| R01 | [T05](#task-05), [T07](#task-07), [T09](#task-09), [T10](#task-10), [T11](#task-11), [T20](#task-20), [T40](#task-40), [T41](#task-41), [T42](#task-42), [T45](#task-45) | T05.01–03; T07.01–04; T09.01–04; T10.01–03; and E sections of all linked tasks | Cold install, sign-in/reset, trade, print/report and recovery operate with public network denied. |
| R02 | [T01](#task-01), [T05](#task-05), [T11](#task-11), [T12](#task-12), [T40](#task-40), [T41](#task-41), [T42](#task-42), [T43](#task-43), [T44](#task-44), [T45](#task-45) | T01.01–03; T05.01–03; T11.01–03; T12.01–03; and E sections of all linked tasks | Schema/model match, explicit bootstrap/import modes and no bypassed admission gate. |
| R03 | [T05](#task-05), [T06](#task-06), [T07](#task-07), [T08](#task-08), [T09](#task-09), [T13](#task-13), [T14](#task-14), [T38](#task-38), [T41](#task-41), [T45](#task-45) | T05.01–03; T06.01–03; T07.01–04; T08.01–03; and E sections of all linked tasks | Local owner/user/device lifecycle usable without SQL or a hosted messaging/identity provider. |
| R04 | [T04](#task-04), [T06](#task-06), [T07](#task-07), [T08](#task-08), [T09](#task-09), [T13](#task-13), [T14](#task-14), [T15](#task-15), [T17](#task-17), [T34](#task-34), [T35](#task-35), [T38](#task-38), [T39](#task-39), [T45](#task-45) | T04.01–03; T06.01–03; T07.01–04; T08.01–03; and E sections of all linked tasks | Current tenant/location/device/action scope and anti-forgery boundaries hold on reads and writes. |
| R05 | [T01](#task-01), [T02](#task-02), [T03](#task-03), [T05](#task-05), [T06](#task-06), [T12](#task-12), [T16](#task-16), [T17](#task-17), [T19](#task-19), [T20](#task-20), [T21](#task-21), [T22](#task-22), [T23](#task-23), [T24](#task-24), [T25](#task-25), [T26](#task-26), [T27](#task-27), [T28](#task-28), [T29](#task-29), [T30](#task-30), [T31](#task-31), [T32](#task-32), [T33](#task-33), [T43](#task-43), [T44](#task-44), [T45](#task-45) | T01.01–03; T02.01–05; T03.01–04; T05.01–03; and E sections of all linked tasks | Exact original pricing/tax/value and individually balanced journal/source recognition. |
| R06 | [T03](#task-03), [T17](#task-17), [T19](#task-19), [T20](#task-20), [T21](#task-21), [T22](#task-22), [T23](#task-23), [T24](#task-24), [T25](#task-25), [T26](#task-26), [T27](#task-27), [T28](#task-28), [T29](#task-29), [T31](#task-31), [T37](#task-37), [T45](#task-45) | T03.01–04; T17.01–03; T19.01–03; T20.01–04; and E sections of all linked tasks | Repeated/unknown-outcome commands do not duplicate effects; changed-key bodies conflict. |
| R07 | [T02](#task-02), [T06](#task-06), [T13](#task-13), [T14](#task-14), [T15](#task-15), [T16](#task-16), [T17](#task-17), [T18](#task-18), [T19](#task-19), [T20](#task-20), [T21](#task-21), [T22](#task-22), [T23](#task-23), [T24](#task-24), [T25](#task-25), [T26](#task-26), [T27](#task-27), [T28](#task-28), [T29](#task-29), [T30](#task-30), [T31](#task-31), [T32](#task-32), [T33](#task-33), [T34](#task-34), [T39](#task-39), [T45](#task-45) | T02.01–05; T06.01–03; T13.01–03; T14.01–03; and E sections of all linked tasks | Every included journey has a role-appropriate usable shared UI; no required raw IDs. |
| R08 | [T03](#task-03), [T15](#task-15), [T21](#task-21), [T23](#task-23), [T25](#task-25), [T26](#task-26), [T27](#task-27), [T29](#task-29), [T30](#task-30), [T31](#task-31), [T32](#task-32), [T33](#task-33), [T45](#task-45) | T03.01–04; T15.01–03; T21.01–03; T23.01–03; and E sections of all linked tasks | PO/receipt/bill/credit/expense/count/transfer/statement/period states reconcile. |
| R09 | [T04](#task-04), [T08](#task-08), [T19](#task-19), [T20](#task-20), [T35](#task-35), [T36](#task-36), [T37](#task-37), [T38](#task-38), [T43](#task-43), [T45](#task-45) | T04.01–03; T08.01–03; T19.01–03; T20.01–04; and E sections of all linked tasks | Every acknowledged unit of provisional cash survives failure or has an auditable disposition. |
| R10 | [T04](#task-04), [T35](#task-35), [T36](#task-36), [T37](#task-37), [T45](#task-45) | T04.01–03; T35.01–04; T36.01–04; T37.01–04; and E sections of all linked tasks | Baseline/feed/epoch/scope and atomic cache/cursor semantics have no silent gaps. |
| R11 | [T02](#task-02), [T10](#task-10), [T13](#task-13), [T18](#task-18), [T24](#task-24), [T28](#task-28), [T31](#task-31), [T33](#task-33), [T39](#task-39), [T45](#task-45) | T02.01–05; T10.01–03; T13.01–03; T18.01–03; and E sections of all linked tasks | Authorised local documents, evidence, safe exact exports and printing work on both hosts. |
| R12 | [T12](#task-12), [T34](#task-34), [T38](#task-38), [T39](#task-39), [T40](#task-40), [T41](#task-41), [T43](#task-43), [T45](#task-45), [T46](#task-46) | T12.01–03; T34.01–03; T38.01–04; T39.01–04; and E sections of all linked tasks | Isolated complete restore and device replay meet the declared recovery targets. |
| R13 | [T09](#task-09), [T11](#task-11), [T41](#task-41), [T42](#task-42), [T43](#task-43), [T44](#task-44), [T45](#task-45), [T46](#task-46) | T09.01–04; T11.01–03; T41.01–04; T42.01–03; and E sections of all linked tasks | Sealed dependencies, locally executable pipeline and exact signed artifact verification. |
| R14 | [T12](#task-12), [T43](#task-43), [T46](#task-46) | T12.01–03; T43.01–04; T46.01–03 | Retired active provider/platform/import paths removed without rewriting immutable history. |
| R15 | [T01](#task-01), [T10](#task-10), [T44](#task-44), [T46](#task-46) | T01.01–03; T10.01–03; T44.01–04; T46.01–03 | Source, executed evidence, exclusions and real owner/production gates are distinguished. |

<a id="journeys"></a>
### 6.2 Integrated acceptance journeys

Execute against synthetic fixtures first and the exact packaged candidate at Task 45. Production business data is not a test fixture. A screenshot or a successful HTTP status alone is insufficient: inspect committed financial facts with the existing FinancialAssertions helpers and fresh contexts.

| Journey | Tasks | End-to-end proof |
|---|---|---|
| J01 — Local first use and recovery | [T05](#task-05), [T06](#task-06), [T07](#task-07), [T08](#task-08), [T09](#task-09), [T14](#task-14), [T41](#task-41) | Install from verified media with public egress blocked; initialise a new business, hand off a cashier setup ticket, authenticate browser/native, switch only to permitted context and recover an account without SMTP. No demo/default password works. |
| J02 — A complete retail day | [T15](#task-15), [T16](#task-16), [T17](#task-17), [T18](#task-18), [T19](#task-19), [T20](#task-20), [T21](#task-21), [T24](#task-24), [T33](#task-33) | Create masters/catalog, open drawer, sell cash with tax/discount, lose a response and replay, print, partially refund, reconcile posted/provisional/count/variance and close. Stock/value, journals and cash tie out by individual source. |
| J03 — Wholesale order to cash | [T22](#task-22), [T23](#task-23), [T24](#task-24), [T25](#task-25), [T29](#task-29), [T33](#task-33) | Quote, record manual communication, accept/convert, reserve, deliver in parts, invoice from each delivery once, collect/allocate and credit remaining eligible amounts. Source quantities/costs and AR/DNI/revenue agree. |
| J04 — Procure to pay | [T15](#task-15), [T26](#task-26), [T27](#task-27), [T28](#task-28), [T33](#task-33) | Supplier/PO, split receipt, GRNI, bill/input tax/approved variance, supplier payment and partial return/credit. PO itself moves no stock or money; receipt and bill each have distinct recognition. |
| J05 — Service business and customer deposits | [T24](#task-24), [T25](#task-25), [T28](#task-28), [T31](#task-31), [T32](#task-32) | Create a service invoice, take unapplied deposit, allocate in parts, clear or bounce an external transfer, refund legitimate remaining credit and post an expense. Service work never consumes stock and deposits are not premature revenue. |
| J06 — Stock control and custody | [T29](#task-29), [T30](#task-30), [T33](#task-33) | Dispatch/receive a valued transfer, perform a blind count with intervening movement, approve a variance and inspect valuation. No negative stock, lost in-transit value, guessed opening cost or silent repair. |
| J07 — Finance and historical reporting | [T25](#task-25), [T27](#task-27), [T31](#task-31), [T32](#task-32), [T33](#task-33) | Settle imported opening AR/AP as well as normal documents, reconcile a local statement, run historical aging/GL/tax/statements, close period/year and retry a close. All subledgers reconcile and historical dates exclude later events. |
| J08 — Disconnected terminal failure/recovery | [T19](#task-19), [T35](#task-35), [T36](#task-36), [T37](#task-37), [T38](#task-38) | Enrol and grant, pull a baseline, capture provisional cash offline, kill/restart, update catalog, reject/retry/duplicate push and resolve actual cash. Corruption/revocation/expiry/scope changes cannot erase queues or advance over unapplied data. |
| J09 — Offline install, update and full restore | [T11](#task-11), [T12](#task-12), [T39](#task-39), [T40](#task-40), [T41](#task-41), [T42](#task-42), [T43](#task-43), [T44](#task-44) | Build from sealed inputs, verify a signed package, install without downloads, import native openings or attest zero openings, back up, restore to an isolated host, upgrade with pending devices and reconcile replays. Measure declared RPO/RTO. |
| J10 — Security, accessibility and release evidence | [T04](#task-04), [T09](#task-09), [T10](#task-10), [T13](#task-13), [T34](#task-34), [T38](#task-38), [T44](#task-44), [T45](#task-45), [T46](#task-46) | Run current-role/location/device attacks, CSRF, asset-egress checks, redaction, zoom/keyboard, exact packaged native/browser workflows and performance envelope. Zero mandatory public endpoint, no unexecuted required case marked pass, candidate bound to its digest. |

### 6.3 Rollout and rollback sequence

**Development:** refresh the baseline without discarding changes, read repository instructions, create an issue/branch for the bounded slice, inspect exact anchors, implement minimal owning-module changes, run focused tests and update its evidence. Integrate through reviewed PRs and full candidate CI. No autonomous merge, destructive operation or production action is implied by this document.

**Candidate:** complete all G0–G7 gates using synthetic data and restricted DB credentials. Generate package/schema/protocol manifests, perform disconnected installs and real Windows acceptance, restore the complete local instance and measure financial/performance/recovery controls. Retain the exact verified candidate digest and all unresolved exceptions. Do not release early because a subset of screens works.

**Pilot:** an owner supplies one named business/site, devices/cashiers, approved finance settings/opening data, key custodian, backup target and acceptance window. Capture baseline counts/control totals and drain or explicitly inventory pending device cash. Run ReadOnlyCheck before any write. Record the authorised plan and codeword separately from this file; never include the codeword in source, artifacts or logs.

**Production apply:** maintenance/write drain → fresh verified backup and recoverable key/file copies → instance/artifact/schema/approval validation → reviewed forward migration/native opening → integrity checks → limited operator smoke → explicit owner release to trade. No provider network call is needed. Existing queues are reconciled, not discarded. An unresolved required gate stops the run before the next write.

**Rollback:** before writes, restore previous compatible binaries/config and stop safely. After schema changes, application rollback is allowed only if its compatibility was demonstrated. After new business writes, fix forward or perform an explicitly approved point-in-time/full restore followed by reconciliation of all post-backup online and device operations. Never downgrade SQLite by deleting a queue, rewrite issued invoices, silently repair GL/stock, or revert to a retired datastore. Retain incident/audit evidence.

### 6.4 Blockers and decisions that code cannot fabricate

| ID / type | Impact | Required action / safe development default |
|---|---|---|
| B01 — BLOCKED_EXTERNAL: real operating environment | Cannot certify local OS/printer/TLS/network/storage/backup or make a production run | Use isolated synthetic SingleHost/LocalNetwork fixtures; owner names production machines, paths, trust, capacity and operators for pilot. |
| B02 — BLOCKED_EXTERNAL: existing business opening facts | Stock value, AR/AP, deposits and previous postings are unverified | New synthetic businesses use explicit zero openings. Existing business must provide finance-approved native package/control totals; no silent valuation inference. |
| B03 — BLOCKED_DECISION only when required rules are absent/conflicting | Cannot certify legal tax, settlement scale, fiscal/numbering/retention or unsupported costing | Provide validated configuration and labelled demonstration values. Existing incompatible costing labels or required jurisdictional integration stop that production profile; do not invent a rule or claim statutory compliance. |
| B04 — BLOCKED_EXTERNAL: trust/signing/recovery custody | No trusted offline installer, production native identity or recoverable encrypted data without keys/custodian | Generate/store production keys under the local owner’s controlled process, configure local trust and prove rotation/recovery. No public CA/SaaS is mandatory, but a trusted key is. |
| B05 — BLOCKED_EXTERNAL: real Windows/offline/printer acceptance | Source and a successful MAUI build are not native runtime proof | Execute Task 38/45 on a supported Windows host with exact package and owned certificates. Do not substitute a server test. |
| B06 — BLOCKED_EXTERNAL: pilot/production approval | Cannot start trading on real data or issue irreversible operations | Name business/devices/cashiers/window/thresholds and obtain the designated user codeword for the specific operation; never reuse approval from another project. |
| B07 — Separate authorised archive administration | Literal removal from Git history, old releases/forks/backups or immutable applied migration text cannot be safely assumed | This plan removes active runtime/docs/new-release dependencies. Any archive/history purge has its own reviewed retention, backup and irreversible-action approval; it is not required for LocalCore operation. |
| B08 — BLOCKED_CONTEXT only if implementation source changes/inaccessible | Checked signatures/paths may drift after this SHA | Re-read the changed owners and update anchors/dependencies before implementing. Current connector access succeeded; do not label known code unavailable merely because bulk archive download failed. |
| B09 — BLOCKED_ARTIFACT_OUTPUT | No valid specification file | Regenerate only the required UTF-8 Markdown and validate structure/links before offering it. |

These are not compulsory external **service** dependencies. They are real business, hardware, trust and authorisation inputs. Safe development can proceed without production secrets; real production admission cannot.

### 6.5 Verification ledger for this review

| Check | Status / evidence |
|---|---|
| Remote default branch, SHA and commit date | VERIFIED through GitHub; end-of-review branch recheck still pointed to `400b1cd67b519920a99bcfdd9c4d65b1af88609f`. |
| Current delta vs prior reviewed baseline | VERIFIED: one commit, 24 changed/added files; changed invoice implementation reread. No migration/snapshot in that delta. |
| Root AGENTS.md | Checked, 404 at reviewed SHA. Recheck nested instructions at implementation. |
| Current owning code, project graph, packages, relevant migrations, docs, workflows | Inspected through connector; precise source anchors below. Unchanged prior anchors reused only after commit comparison established those files were unchanged. Not every repository line is claimed reviewed. |
| Current remote CI | Run `39508354410`: MAUI build job `118922887282` **success**; Linux build/test job `118922887861` **failure** during role provisioning. Log at 2026-10-20T22:29:43Z reports inability to resolve hostname `postgres`; later server/WASM build, migrations, API/SQLite/migration tests and cutover safety were **skipped**. This is observed CI evidence, not a run executed by this review. |
| Remote secret scan within that job | Observed success before provisioning failure; it does not prove all later builds/tests or production secrets are safe. |
| Application build/unit/API/SQLite/native/UI tests performed by this review | **NOT RUN — SPEC_ONLY.** No passing test total is asserted. |
| Live production database/device/cash/backup validation | **NOT RUN — no production access or approval.** |
| Internet-free install/use/recovery/performance proof | **NOT RUN — acceptance work specified, not demonstrated.** |
| Repository writes/commits/PRs/merges/releases/deployments | **None performed.** |
| Specification file validation | UTF-8 decoding, nonempty file, 46 sequential tasks, A–G coverage, acyclic backward dependencies, R01–R15 coverage and internal anchor links checked by generation validator. This validates the document, not the software. |

Remote run evidence: [CI run](https://github.com/nirzaf/QtsBilling/actions/runs/39508354410), [failed Linux job](https://github.com/nirzaf/QtsBilling/actions/runs/39508354410/job/118922887861), [Windows build job](https://github.com/nirzaf/QtsBilling/actions/runs/39508354410/job/118922887282). The observed role-provisioning failure corroborates F10. The schema/UI/security findings remain source-grounded; their runtime reproducers are required tasks, not claimed already executed.

<a id="source-catalogue"></a>
### 6.6 Commit-pinned source catalogue

**Fresh** means read at this SHA in this review. **Revalidated unchanged** means the prior reviewed source was retained only after comparing the single new commit and confirming that path was not changed. Repository documentation is lower authority than current code; a historical “DONE” or test count is not reused as present runtime proof.

<a id="evidence-e02"></a>
**E02 — Revalidated unchanged.** [`QtsWebApp/QtsWebApp/Features/Auth/AuthEndpoints.cs`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/QtsWebApp/QtsWebApp/Features/Auth/AuthEndpoints.cs#L26-L33) — Cookie sign-in, switch business, password setup/reset, session.

<a id="evidence-e03"></a>
**E03 — Revalidated unchanged.** [`QtsWebApp/QtsWebApp/Features/Auth/NativeAuthorizationEndpoints.cs`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/QtsWebApp/QtsWebApp/Features/Auth/NativeAuthorizationEndpoints.cs#L42-L94) — Native authorization code, PKCE and client registration.

<a id="evidence-e04"></a>
**E04 — Revalidated unchanged.** [`QtsWebApp/QtsWebApp/Features/Devices/DeviceEndpoints.cs`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/QtsWebApp/QtsWebApp/Features/Devices/DeviceEndpoints.cs#L27-L33) — Device challenges, enrollment and offline grants; no list/revoke endpoint.

<a id="evidence-e05"></a>
**E05 — Revalidated unchanged.** [`QtsBilling.Contracts/Identity/IdentityDtos.cs`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/QtsBilling.Contracts/Identity/IdentityDtos.cs#L39-L93) — Permission names and role names.

<a id="evidence-e06"></a>
**E06 — Revalidated unchanged.** [`QtsWebApp/QtsWebApp/Features/Customers/CustomerEndpoints.cs`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/QtsWebApp/QtsWebApp/Features/Customers/CustomerEndpoints.cs#L30-L35) — Customer API and deactivation.

<a id="evidence-e07"></a>
**E07 — Revalidated unchanged.** [`QtsWebApp/QtsWebApp/Features/Catalog/ProductEndpoints.cs`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/QtsWebApp/QtsWebApp/Features/Catalog/ProductEndpoints.cs#L30-L36) — Product/variant API routes.

<a id="evidence-e08"></a>
**E08 — Revalidated unchanged.** [`QtsWebApp/QtsWebApp/Features/Catalog/MasterDataEndpoints.cs`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/QtsWebApp/QtsWebApp/Features/Catalog/MasterDataEndpoints.cs#L25-L63) — Business-scoped master-data CRUD.

<a id="evidence-e09"></a>
**E09 — Revalidated unchanged.** [`QtsWebApp/QtsWebApp/Features/Pricing/PricingEndpoints.cs`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/QtsWebApp/QtsWebApp/Features/Pricing/PricingEndpoints.cs#L26-L40) — Tax and discount CRUD.

<a id="evidence-e10"></a>
**E10 — Revalidated unchanged.** [`QtsWebApp/QtsWebApp/Features/Inventory/InventoryEndpoints.cs`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/QtsWebApp/QtsWebApp/Features/Inventory/InventoryEndpoints.cs#L29-L37) — Balances, movements, locations and reconciliation.

<a id="evidence-e11"></a>
**E11 — Revalidated unchanged.** [`QtsWebApp/QtsWebApp/Features/Inventory/StockTransferEndpoints.cs`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/QtsWebApp/QtsWebApp/Features/Inventory/StockTransferEndpoints.cs#L25-L30) — Stock transfer states.

<a id="evidence-e12"></a>
**E12 — Revalidated unchanged.** [`QtsWebApp/QtsWebApp/Features/Inventory/StockCountEndpoints.cs`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/QtsWebApp/QtsWebApp/Features/Inventory/StockCountEndpoints.cs#L25-L33) — Stock count capture, approval and cancellation.

<a id="evidence-e13"></a>
**E13 — Revalidated unchanged.** [`QtsWebApp/QtsWebApp/Features/Sales/SaleEndpoints.cs`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/QtsWebApp/QtsWebApp/Features/Sales/SaleEndpoints.cs#L30-L35) — POS posting, history, details, receipts.

<a id="evidence-e14"></a>
**E14 — Revalidated unchanged.** [`QtsWebApp/QtsWebApp/Features/Sales/CashRegisterEndpoints.cs`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/QtsWebApp/QtsWebApp/Features/Sales/CashRegisterEndpoints.cs#L29-L38) — Cash register/session operations.

<a id="evidence-e15"></a>
**E15 — Revalidated unchanged.** [`QtsWebApp/QtsWebApp/Features/Sales/RefundEndpoints.cs`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/QtsWebApp/QtsWebApp/Features/Sales/RefundEndpoints.cs#L32-L36) — Full and partial refunds.

<a id="evidence-e16"></a>
**E16 — Revalidated unchanged.** [`QtsWebApp/QtsWebApp/Features/Sales/PaymentTerminalEndpoints.cs`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/QtsWebApp/QtsWebApp/Features/Sales/PaymentTerminalEndpoints.cs#L20-L23) — Payment terminal intent endpoint.

<a id="evidence-e17"></a>
**E17 — Revalidated unchanged.** [`QtsWebApp/QtsWebApp/Features/Sales/PaymentTerminalEndpoints.cs`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/QtsWebApp/QtsWebApp/Features/Sales/PaymentTerminalEndpoints.cs#L136-L139) — No verified PSP; card capture disabled.

<a id="evidence-e18"></a>
**E18 — Revalidated unchanged.** [`QtsWebApp/QtsWebApp/Features/Orders/OrderEndpoints.cs`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/QtsWebApp/QtsWebApp/Features/Orders/OrderEndpoints.cs#L31-L54) — Quotes and sales order lifecycle.

<a id="evidence-e21"></a>
**E21 — Revalidated unchanged.** [`QtsWebApp/QtsWebApp/Features/Invoices/PaymentEndpoints.cs`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/QtsWebApp/QtsWebApp/Features/Invoices/PaymentEndpoints.cs#L23-L26) — Customer receipt and allocation.

<a id="evidence-e22"></a>
**E22 — Revalidated unchanged.** [`QtsWebApp/QtsWebApp/Features/Purchasing/PurchasingEndpoints.cs`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/QtsWebApp/QtsWebApp/Features/Purchasing/PurchasingEndpoints.cs#L52-L71) — Suppliers and PO receipts/returns.

<a id="evidence-e23"></a>
**E23 — Revalidated unchanged.** [`QtsWebApp/QtsWebApp/Features/Purchasing/SupplierBillEndpoints.cs`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/QtsWebApp/QtsWebApp/Features/Purchasing/SupplierBillEndpoints.cs#L26-L32) — Supplier bills and supplier payment API.

<a id="evidence-e24"></a>
**E24 — Revalidated unchanged.** [`QtsWebApp/QtsWebApp/Features/Expenses/ExpenseEndpoints.cs`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/QtsWebApp/QtsWebApp/Features/Expenses/ExpenseEndpoints.cs#L38-L51) — Expense categories, posting and reversal.

<a id="evidence-e25"></a>
**E25 — Revalidated unchanged.** [`QtsWebApp/QtsWebApp/Features/Accounting/AccountingEndpoints.cs`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/QtsWebApp/QtsWebApp/Features/Accounting/AccountingEndpoints.cs#L29-L53) — Chart, settings, journals, periods, fiscal close and trial balance.

<a id="evidence-e26"></a>
**E26 — Revalidated unchanged.** [`QtsWebApp/QtsWebApp/Features/Banking/BankingEndpoints.cs`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/QtsWebApp/QtsWebApp/Features/Banking/BankingEndpoints.cs#L26-L31) — Bank account, statement upload and line matching.

<a id="evidence-e27"></a>
**E27 — Revalidated unchanged.** [`QtsWebApp/QtsWebApp/Features/Reporting/ReportEndpoints.cs`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/QtsWebApp/QtsWebApp/Features/Reporting/ReportEndpoints.cs#L28-L38) — AR/AP aging, tax, Z, balance sheet, P&L, stock, cash, sales.

<a id="evidence-e28"></a>
**E28 — Revalidated unchanged.** [`QtsWebApp/QtsWebApp/Features/Audit/AuditEndpoints.cs`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/QtsWebApp/QtsWebApp/Features/Audit/AuditEndpoints.cs#L21-L23) — Audit event feed.

<a id="evidence-e29"></a>
**E29 — Revalidated unchanged.** [`QtsWebApp/QtsWebApp/Features/Sync/SyncOperationEndpoints.cs`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/QtsWebApp/QtsWebApp/Features/Sync/SyncOperationEndpoints.cs#L40-L43) — Idempotent device push and batch.

<a id="evidence-e30"></a>
**E30 — Revalidated unchanged.** [`QtsWebApp/QtsWebApp/Features/Sync/SyncChangeEndpoints.cs`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/QtsWebApp/QtsWebApp/Features/Sync/SyncChangeEndpoints.cs#L27-L29) — Commit ordered sync feed.

<a id="evidence-e31"></a>
**E31 — Revalidated unchanged.** [`QtsBilling.Maui/Services/DeviceSyncCoordinator.cs`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/QtsBilling.Maui/Services/DeviceSyncCoordinator.cs#L43-L60) — Device push, pull and status.

<a id="evidence-e32"></a>
**E32 — Revalidated unchanged.** [`QtsBilling.Maui/Services/OfflineGrantService.cs`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/QtsBilling.Maui/Services/OfflineGrantService.cs#L31-L110) — Device-side capture grant validation/pinning.

<a id="evidence-e33"></a>
**E33 — Revalidated unchanged.** [`QtsBilling.Maui/Services/DeviceLocalPosService.cs`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/QtsBilling.Maui/Services/DeviceLocalPosService.cs#L42-L113) — Offline cash capture and local outbox.

<a id="evidence-e35"></a>
**E35 — Revalidated unchanged.** [`QtsBilling.Persistence.Sqlite/QtsBilling.Persistence.Sqlite.csproj`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/QtsBilling.Persistence.Sqlite/QtsBilling.Persistence.Sqlite.csproj#L11-L25) — SQLCipher and EF Core SQLite references.

<a id="evidence-e36"></a>
**E36 — Revalidated unchanged.** [`QtsBilling.Persistence.Sqlite/SqliteDatabaseEncryption.cs`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/QtsBilling.Persistence.Sqlite/SqliteDatabaseEncryption.cs#L10-L114) — Encrypted local database preparation.

<a id="evidence-e38"></a>
**E38 — Revalidated unchanged.** [`QtsBilling.Ui/Pages/Pos.razor`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/QtsBilling.Ui/Pages/Pos.razor#L1-L80) — Working cash POS view: online and offline branches.

<a id="evidence-e39"></a>
**E39 — Revalidated unchanged.** [`QtsWebApp/QtsWebApp.Client/Pages/Home.razor`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/QtsWebApp/QtsWebApp.Client/Pages/Home.razor#L1-L8) — Web home is a hello-world placeholder.

<a id="evidence-e40"></a>
**E40 — Revalidated unchanged.** [`QtsBilling.Ui/Pages/Customers.razor`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/QtsBilling.Ui/Pages/Customers.razor#L1-L93) — Customer CRUD interface.

<a id="evidence-e41"></a>
**E41 — Revalidated unchanged.** [`.github/workflows/ci.yml`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/.github/workflows/ci.yml#L68-L126) — CI builds web and Windows MAUI, executes targeted suites.

<a id="evidence-e42"></a>
**E42 — Revalidated unchanged.** [`.github/workflows/release.yml`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/.github/workflows/release.yml#L1-L164) — Tag release verification and ZIP artifacts.

<a id="evidence-e43"></a>
**E43 — Revalidated unchanged.** [`QtsBilling.DataImport/NativeImportWriter.cs`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/QtsBilling.DataImport/NativeImportWriter.cs#L24-L83) — Signed native import package and transactional apply.

<a id="evidence-e44"></a>
**E44 — Revalidated unchanged.** [`QtsBilling.Migration/Program.cs`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/QtsBilling.Migration/Program.cs#L1-L17) — Hash-verified historical snapshot import/transform/reconcile CLI.

<a id="evidence-e45"></a>
**E45 — Revalidated unchanged.** [`docs/migration/task-status.md`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/docs/migration/task-status.md#L1-L57) — Task status log (not authoritative if contradicted by code).

<a id="evidence-e46"></a>
**E46 — Revalidated unchanged.** [`docs/migration/readiness.md`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/docs/migration/readiness.md#L1-L45) — Readiness ledger contains stale missing claims.

<a id="evidence-e47"></a>
**E47 — Revalidated unchanged.** [`QtsBilling.Contracts/Identity/OfflineGrant.cs`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/QtsBilling.Contracts/Identity/OfflineGrant.cs#L9-L34) — Signed offline grant constraints.

<a id="evidence-e48"></a>
**E48 — Revalidated unchanged.** [`QtsBilling.Persistence.Postgres/Migrations/20261020145535_AccountingPeriodAndPostingIntegrity.cs`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/QtsBilling.Persistence.Postgres/Migrations/20261020145535_AccountingPeriodAndPostingIntegrity.cs) — Period and posting constraints migration.

<a id="evidence-e49"></a>
**E49 — Revalidated unchanged.** [`QtsBilling.Persistence.Postgres/Migrations/20261020214218_AddOrderFulfillment.cs`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/QtsBilling.Persistence.Postgres/Migrations/20261020214218_AddOrderFulfillment.cs) — Order fulfillment schema.

<a id="evidence-e50"></a>
**E50 — Revalidated unchanged.** [`QtsBilling.Maui/Services/MauiNavContributor.cs`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/QtsBilling.Maui/Services/MauiNavContributor.cs#L10-L16) — MAUI registers shared POS navigation.

<a id="evidence-e51"></a>
**E51 — Revalidated unchanged.** [`QtsWebApp/QtsWebApp/Features/Sales/SalePosting.cs`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/QtsWebApp/QtsWebApp/Features/Sales/SalePosting.cs) — Unified transactional sale posting.

<a id="evidence-e52"></a>
**E52 — Revalidated unchanged.** [`QtsBilling.Persistence.Postgres/Entities/OpenItem.cs`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/QtsBilling.Persistence.Postgres/Entities/OpenItem.cs) — Open-item receivable/payable data model.

<a id="evidence-e53"></a>
**E53 — Revalidated unchanged.** [`QtsBilling.Persistence.Sqlite.Tests/AtomicSyncCommitTests.cs`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/QtsBilling.Persistence.Sqlite.Tests/AtomicSyncCommitTests.cs) — Device-side transaction tests.

<a id="evidence-e54"></a>
**E54 — Revalidated unchanged.** [`TestQtsBilling/Integration/QuoteOrderTests.cs`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/TestQtsBilling/Integration/QuoteOrderTests.cs) — Quote/order integration tests.

<a id="evidence-e55"></a>
**E55 — Revalidated unchanged.** [`TestQtsBilling/Integration/StockCountTests.cs`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/TestQtsBilling/Integration/StockCountTests.cs) — Stock count integration tests.

<a id="evidence-e57"></a>
**E57 — Revalidated unchanged.** [`QtsBilling.Maui/Components/Pages/Home.razor`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/QtsBilling.Maui/Components/Pages/Home.razor#L1-L61) — Desktop home is device-oriented, not a management dashboard.

<a id="evidence-e58"></a>
**E58 — Revalidated unchanged.** [`QtsWebApp/QtsWebApp/Features/Auth/DevDataSeeder.cs`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/QtsWebApp/QtsWebApp/Features/Auth/DevDataSeeder.cs) — Development seeding, not production business onboarding.

<a id="evidence-e60"></a>
**E60 — Revalidated unchanged.** [`scripts/qb30-cutover.ps1`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/scripts/qb30-cutover.ps1) — Gated cutover script exists; production run not observed.

<a id="evidence-s01"></a>
**S01 — Fresh.** [`QtsWebApp/QtsWebApp/Program.cs`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/QtsWebApp/QtsWebApp/Program.cs#L1-L220) — Current .NET host, local OpenIddict server, authentication, local validation and rate limiting.

<a id="evidence-s02"></a>
**S02 — Fresh.** [`QtsWebApp/QtsWebApp/Program.cs`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/QtsWebApp/QtsWebApp/Program.cs#L220-L412) — Cookie settings, optional Data Protection persistence, disabled terminal provider, metrics and current API/client registration.

<a id="evidence-s03"></a>
**S03 — Fresh.** [`QtsBilling.Persistence.Postgres/BillingDbContext.Model.Financial.cs`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/QtsBilling.Persistence.Postgres/BillingDbContext.Model.Financial.cs#L475-L610) — Invoice recognition/snapshot mappings and new source-sale uniqueness; pair with commit comparison for missing generated migration.

<a id="evidence-s04"></a>
**S04 — Fresh.** [`QtsBilling.Ui/Pages/Invoices/InvoiceEditor.razor`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/QtsBilling.Ui/Pages/Invoices/InvoiceEditor.razor#L1-L207) — New editor: unreachable draft issue branch, service-mode save, tax-percent preview, edit-permission early return, first-page customer selector and relative print link.

<a id="evidence-s05"></a>
**S05 — Fresh.** [`QtsBilling.Ui/Pages/Invoices/Invoices.razor`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/QtsBilling.Ui/Pages/Invoices/Invoices.razor#L1-L213) — Existing invoice list, status/search/cursor controls; extend rather than recreate.

<a id="evidence-s06"></a>
**S06 — Fresh.** [`QtsBilling.Contracts/Invoices/InvoiceDtos.cs`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/QtsBilling.Contracts/Invoices/InvoiceDtos.cs#L1-L170) — Recognition modes, expected version, invoice party snapshots, payments and allocation contracts.

<a id="evidence-s07"></a>
**S07 — Fresh.** [`QtsWebApp/QtsWebApp/Features/Invoices/InvoiceEndpoints.cs`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/QtsWebApp/QtsWebApp/Features/Invoices/InvoiceEndpoints.cs#L40-L90) — Current invoice and payment routes, including POST /api/v1/invoices/from-sale.

<a id="evidence-s08"></a>
**S08 — Fresh.** [`QtsWebApp/QtsWebApp/Features/Invoices/InvoiceEndpoints.cs`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/QtsWebApp/QtsWebApp/Features/Invoices/InvoiceEndpoints.cs#L2440-L2615) — Partial-credit arithmetic explicitly uses Exclusive mode; invoice-detail projection omits line tax mode and revenue base.

<a id="evidence-s09"></a>
**S09 — Fresh.** [`QtsWebApp/QtsWebApp/Features/Auth/AuthEndpoints.cs`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/QtsWebApp/QtsWebApp/Features/Auth/AuthEndpoints.cs#L115-L245) — Business switch and production reset branch: token created, warning logged, Accepted returned without delivery implementation in this method.

<a id="evidence-s10"></a>
**S10 — Fresh.** [`QtsWebApp/QtsWebApp/Components/App.razor`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/QtsWebApp/QtsWebApp/Components/App.razor#L1-L26) — Host-owned assets include the Google Fonts URL; other inspected runtime assets are local.

<a id="evidence-s11"></a>
**S11 — Fresh.** [`QtsBilling.Maui/wwwroot/index.html`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/QtsBilling.Maui/wwwroot/index.html#L1-L31) — MAUI local web assets, disabled browser zoom and malformed dismiss text.

<a id="evidence-s12"></a>
**S12 — Fresh.** [`scripts/backup-postgres.ps1`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/scripts/backup-postgres.ps1#L11-L82) — Direct-connection mode still executes Docker probe, cleanup and tool-version commands.

<a id="evidence-s13"></a>
**S13 — Fresh.** [`scripts/provision-postgres-roles.ps1`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/scripts/provision-postgres-roles.ps1#L8-L120) — Psql mode demands AdminPassword even for Verify; local psql and Docker branches, runtime role grants.

<a id="evidence-s14"></a>
**S14 — Fresh.** [`.github/workflows/ci.yml`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/.github/workflows/ci.yml#L1-L127) — Current Linux PostgreSQL test jobs, hostname/role-check arguments, MAUI build and remote tool downloads.

<a id="evidence-s15"></a>
**S15 — Fresh.** [`.github/workflows/release.yml`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/.github/workflows/release.yml#L1-L164) — Verification precedes release; final OS artifacts are rebuilt separately, unpackaged and published through GitHub CLI.

<a id="evidence-s16"></a>
**S16 — Fresh.** [`QtsWebApp/QtsWebApp/Features/Sync/SyncChangeEndpoints.cs`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/QtsWebApp/QtsWebApp/Features/Sync/SyncChangeEndpoints.cs#L25-L156) — Incremental feed has no baseline/epoch/floor contract; customer filter uses a principal claim; invalid JSON becomes null.

<a id="evidence-s17"></a>
**S17 — Fresh.** [`QtsBilling.Persistence.Sqlite/SqliteLocalStore.Sync.cs`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/QtsBilling.Persistence.Sqlite/SqliteLocalStore.Sync.cs#L18-L185) — Atomic page+cursor method already exists; projection is applied before conflict decision, null payloads are skipped, resolution only marks a row.

<a id="evidence-s18"></a>
**S18 — Fresh.** [`QtsBilling.sln`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/QtsBilling.sln#L5-L32) — Fourteen current solution projects include both native DataImport and source-specific Migration projects; no former desktop/service project.

<a id="evidence-s19"></a>
**S19 — Fresh.** [`README.md`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/README.md#L47-L120) — Architecture documentation and feature claims, not runtime proof.

<a id="evidence-s20"></a>
**S20 — Fresh.** [`docs/migration/task-status.md`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/docs/migration/task-status.md#L1-L60) — Mixed historical completion claims and admitted native/import/pilot gaps; source supersedes stale task statements.

<a id="evidence-s21"></a>
**S21 — Fresh.** [`docs/migration/decisions.md`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/docs/migration/decisions.md#L1-L38) — Existing PostgreSQL authority, Windows desktop, local-cache boundary and no silent repair decisions.

<a id="evidence-s22"></a>
**S22 — Fresh.** [`docs/migration/deployment.md`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/docs/migration/deployment.md#L1-L105) — Operational configuration and backup policy; portions predate current identity/encryption implementation.

<a id="evidence-s23"></a>
**S23 — Fresh.** [`docs/migration/tasks/QB-42.md`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/docs/migration/tasks/QB-42.md#L1-L55) — Historical native-grant verification limits. Some listed server gaps were subsequently implemented; do not reopen them from this document alone.

<a id="evidence-s24"></a>
**S24 — Fresh.** [`TestQtsBilling/Integration/PostgresApiFixture.cs`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/TestQtsBilling/Integration/PostgresApiFixture.cs#L1-L190) — Real PostgreSQL/API fixture, marked disposable databases, current runtime-role option and upgrade helper.

<a id="evidence-s25"></a>
**S25 — Fresh.** [`QtsWebApp/QtsWebApp/QtsWebApp.csproj`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/QtsWebApp/QtsWebApp/QtsWebApp.csproj#L1-L32) — Pinned .NET/EF packages, OpenIddict 7.7.2 and optional Prometheus exporter prerelease dependency.

<a id="evidence-s26"></a>
**S26 — Fresh.** [`QtsWebApp/QtsWebApp/Infrastructure/IdempotencyExecutor.cs`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/QtsWebApp/QtsWebApp/Infrastructure/IdempotencyExecutor.cs#L12-L104) — Existing transaction+advisory-lock command receipt executor; missing keys bypass receipt behavior, so command endpoints must enforce them.

<a id="evidence-s27"></a>
**S27 — Fresh.** [`QtsWebApp/QtsWebApp/Features/Sales/SalePosting.cs`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/QtsWebApp/QtsWebApp/Features/Sales/SalePosting.cs#L19-L125) — Shared POS/sync validation and scoped context; exact money/quantity limits and tax fractions.

<a id="evidence-s28"></a>
**S28 — Fresh.** [`QtsBilling.Persistence.Postgres/Entities/Core.cs`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/QtsBilling.Persistence.Postgres/Entities/Core.cs#L8-L136) — Existing Business/User/Membership/location grants/enrollment challenge/device fields; reuse them rather than adding parallel membership tables.

<a id="evidence-s29"></a>
**S29 — Fresh.** [`QtsBilling.Persistence.Postgres/Entities/Platform.cs`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/QtsBilling.Persistence.Postgres/Entities/Platform.cs#L3-L145) — Existing command receipts, processed operations, revision/change rows and source-specific identifier-map entity.

<a id="evidence-s30"></a>
**S30 — Fresh.** [`QtsBilling.Persistence.Postgres/Entities/OpenItem.cs`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/QtsBilling.Persistence.Postgres/Entities/OpenItem.cs#L5-L34) — Native opening receivable/payable: immutable original amount, party and journal source.

<a id="evidence-s31"></a>
**S31 — Fresh.** [`QtsBilling.Persistence.Postgres/Entities/Sales.cs`](https://github.com/nirzaf/QtsBilling/blob/400b1cd67b519920a99bcfdd9c4d65b1af88609f/QtsBilling.Persistence.Postgres/Entities/Sales.cs#L86-L158) — Payment holds customer/unallocated/clearance fields; PaymentAllocation currently points only to an InvoiceId.


### 6.7 Primary external documentation used for the deployment decisions

These explain product prerequisites and recovery mechanisms, not QtsBilling implementation status. Accessed during this review; keep future version-sensitive choices pinned to the implemented toolchain.

<a id="source-w1"></a>
**W1 — .NET deployment.** Microsoft documents self-contained deployment and remaining native prerequisites: [Deploy .NET applications](https://learn.microsoft.com/en-us/dotnet/core/deploying/). This supports the decision to bundle a tested runtime/prerequisite set rather than promise a dependency-free executable.

<a id="source-w2"></a>
**W2 — WebView2 offline distribution.** Microsoft documents offline runtime distribution options: [Distribute a WebView2 application and runtime](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution). The MAUI Windows kit still needs a managed WebView2 prerequisite and maintenance strategy.

<a id="source-w3"></a>
**W3 — OpenIddict credentials.** The project documentation describes signing/encryption credentials: [Encryption and signing credentials](https://documentation.openiddict.com/configuration/encryption-and-signing-credentials.html). Local ownership of keys is compatible with self-hosted identity; a cloud identity provider is not required by this architecture.

<a id="source-w4"></a>
**W4 — PostgreSQL recovery.** PostgreSQL 18 documentation describes base backups and continuous WAL archiving: [Continuous archiving and point-in-time recovery](https://www.postgresql.org/docs/18/continuous-archiving.html). A logical dump alone does not establish the proposed point-in-time RPO.

---

**Completion boundary:** this specification defines a finite, self-hosted SMB product and the residual work to finish it. It is not an implementation, executed acceptance report, universal ERP promise or production approval. No public service is mandatory in the target core; explicit local infrastructure, reviewed software dependencies and owner-controlled trust/finance remain necessary.
