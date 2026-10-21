# Native readiness ledger

**This is the honest record of what is implemented, what is verified and what is still missing.** It
follows the *SMB Completion and Production Hardening* specification: a status value describes the
stated capability, never the whole module, and a "done" task document is not a substitute for this
distinction. The ledger is updated with every change of the implementation.

| Item | Value |
|---|---|
| Repository | `nirzaf/QtsBilling` |
| Branch | `master` |
| Implementation base | `master` after the Task 02 cleanup commit (the exact SHA is recorded in the commit message) |
| Ledger date | 2026-10-21 |
| Specification | `QtsBilling_Atomic_Implementation_Spec.md` (64 atomic tasks, gates G0â€“G7) |

## 1. The gates

| Gate | Scope | Status | What is still missing |
|---|---|---|---|
| **G0** baseline/safety | the verified source, the test harness, the cleanup | **PARTIAL - code complete, production not exercised** | Task 02 removed the source-format project/tests and replaced the smoke blacklist with a positive dependency allowlist; Tasks 18-21 are implemented and the full 200-test PostgreSQL suite passes. Production evidence and the owner gate remain outside this repository. |
| **G1** money and data integrity | the exact arithmetic, the database invariants, the periods, the retry/stock/value/settlement/drawer fixes | **IN PROGRESS** | the commercial contract, the inclusive-tax posting and the entry-level balance assertions are done (QB-31, QB-32); the valuation, the settlement subledger, the period locking, the discount approval and the drawer custody are not |
| **G2** identity and onboarding | the current authorization, the composite scope, the secure web/native identity, the settings | **NOT STARTED** | the permission policy still trusts the cached claims; the composite foreign keys are partial; the native PKCE flow does not exist |
| **G3** offline safety | the device grants, the durable outcomes, the atomic cache/cursor, the coordinator, the cash custody | **NOT STARTED** | no signed offline grant, the SQLite inbox still stores a transient retry terminally, the cache and the cursor are not committed atomically |
| **G4** native data and admission | the native import, the reconciliation, the fail-closed readiness | **IMPLEMENTED - production NOT EXERCISED** | `QtsBilling.DataImport` validates, imports and reconciles the signed native package; `scripts/qb30-cutover.ps1` has fail-closed read-only admission and ordered execution behind a signed manifest. The real production target, owner codeword and business approvals have not been exercised. |
| **G5** daily retail usability | the shared shell, the masters, the online point of sale, the registers | **NOT STARTED** | the shared UI still supplies the customers page and the local point of sale only |
| **G6** SMB workflows | the orders, the receivables/payables, the procurement, the expenses, the stock control, the banking, the accounting and the reports | **NOT STARTED** | the APIs for the orders, the supplier bills, the quotes, the reservations, the stock counts, the bank statements are missing |
| **G7** production candidate | the CI, the signed artifacts, the backup recovery, the pilot | **NOT STARTED** | the release promotion is not gated by a tested artifact; the Windows artifact is not signed; the pilot has not run |

## 2. The requirements

`IMPLEMENTED` = the stated behaviour exists and was exercised. `PARTIAL` = a part of it exists.
`MISSING` = it does not exist. `UNVERIFIED` = it exists but no evidence proves it.

| ID | Status | Evidence and what is missing |
|---|---|---|
| R01 current-source truth and removal | IMPLEMENTED (current tree) | Task 02 removed the source-format project and its tests, retired the mapping sample and neutralised the current documentation. `NativeBoundaryTests` walks the tracked tree, scans project/package edges, enforces a positive project graph and proves a source-era manifest is rejected rather than coerced. The implementation specification is an input document and is excluded from the product-tree scan; Git history is not rewritten. |
| R02 safe production admission | IMPLEMENTED, production NOT EXERCISED | Task 21 replaces the caller-supplied codeword pair with a signed environment approval manifest and one owner codeword checked as its SHA-256. Admission is read-only; execution re-checks every evidence hash immediately before the first write and is fail-closed. The controlled-double harness passes 8/8. Still missing: the signed artifact promotion, the protected environment and the owner approval identity (tasks 60, 61). |
| R03 identity and tenant/location isolation | PARTIAL | The fixture proves a cross-business write is refused (T01.02) and the permission comes from the **current** membership (QB-36). The composite tenant foreign keys are scoped and verified but not yet applied (QB-40); the location scope is still missing (task 23). |
| R04 secure web/native sessions | PARTIAL | The web host has the same-origin cookie policy and the typed 401/403. The native flow is missing: no authorization code with PKCE, no system browser (task 22). |
| R05 exact pricing and payment truth | PARTIAL | **QB-31**: one shared commercial calculation for the server, the browser and the device; the inclusive-tax journal now balances (verified on PostgreSQL). Missing: the discount approval limits, the settlement scale in the business settings, the settlement subledger (tasks 08, 12, 13). |
| R06 financial immutability | DONE (verified on PostgreSQL) | **QB-43**: the composed invoice total freezes at issue, lines cannot be added, changed, moved or deleted on an issued invoice, a posted sale, payment or journal entry cannot be rewritten or deleted, no document can be rewound to draft, and `TRUNCATE` cannot erase append-only history. The guards are database triggers, so they hold for an operator session too. |
| R07 stock and cost authority | PARTIAL | The stock quantities tie to the immutable movements, the balance row is race-safe, negative stock is prohibited (QB-37) and the value of a movement is the authority for the cost of goods sold (QB-38). Still missing: the returned-stock cost rule, the transfers and the approved opening valuation of the historical stock. |
| R08 retry and concurrency correctness | PARTIAL | The per-aggregate receipts and `ProcessedOperation` exist. Missing: the receipt for every command, the `409 SAME_KEY_DIFFERENT_PAYLOAD` rule, the unknown-outcome handling (task 11). |
| R09 safe offline money | PARTIAL | The provisional cash and the durable local rows exist. Missing: the durable terminal outcomes, the cash custody states, the recovery (tasks 24, 25, 29). |
| R10 correct synchronization | PARTIAL | The commit-ordered feed exists. Missing: the device-bound baseline, the epoch/floor, the atomic cache and cursor commit (tasks 26, 27). |
| R11 usable masters and point of sale | MISSING | Only the customers page and the local point of sale exist; the online browser checkout is still a stub (tasks 30, 34, 35). |
| R12 order to cash | MISSING | The quotes, the orders, the reservations, the partial deliveries and the receivables do not exist (tasks 38â€“43). |
| R13 procure to pay | MISSING | The purchase order and the receipt exist; the supplier bills, the input tax, the payables and the credits do not (tasks 45, 46). |
| R14 operational accounting | PARTIAL | **QB-44**: the periods exist, a posting requires exactly one open covering period, posting and closing serialize on the period row, periods cannot overlap and a closed period cannot be reopened by an ordinary update. Missing: the counts, the paired transfers, the bank reconciliation (tasks 51, 52). |
| R15 trustworthy reporting | PARTIAL | The balance sheet and the income statement read the authoritative journal, and the cost of goods sold is now the movement value (QB-38). Missing: the exact receivables/payables tie-outs, the valuation reports, the six-decimal quantities (tasks 53, 54). |
| R16 native onboarding and import | IMPLEMENTED, production acceptance NOT EXERCISED | Tasks 18-20: a signed native package is validated without a database; the typed writer imports masters and approved openings atomically with immutable external links; the reconciliation report uses one canonical body, exact per-resource/aggregate/party/ledger/control comparisons and separately signed owner approvals bound to the report digest. Real PostgreSQL tests cover rollback, rerun, conflicts, openings and exact reconciliation. Missing: the real business package, approved historical valuation (B02) and owner-signed production exception decisions. |
| R17 UI and document quality | PARTIAL | The receipt prints the historical snapshot and names an included tax (QB-31). Missing: the shared shell, the paging, the accessibility, the safe exports (tasks 30, 54). |
| R18 operability and delivery | PARTIAL | **QB-32**: the CI runs the real PostgreSQL integration suite; **Task 21**: it also runs the signed admission harness with controlled doubles, and a missing PowerShell 7 fails rather than skips. Missing: the release promotion gate, the signed artifact, the restore drill, the pilot evidence (tasks 58, 60, 61, 63). |

## 3. The critical reproductions

A case is `EXERCISED` only when it ran against the real stack and its result is recorded.

| Case | Status | Where |
|---|---|---|
| A tax-inclusive line posts a balanced journal entry (F11) | **EXERCISED** | `docs/migration/tasks/QB-31.md` â€” a 115 at 15% line posted against PostgreSQL: DR 115 / CR 100 / CR 15, the entry balance 0.0000 |
| The device and the server compute the same tax (F12) | **EXERCISED** | `TestQtsBilling/ValueObjects/CommercialAmountsTests.cs` (the shared function) and the device cache now carries the tax mode |
| An existing tax-inclusive sale keeps its composition after the schema upgrade | **EXERCISED** | TestQtsBilling/Integration/MigrationUpgradeTests.cs - the rows of the previous schema (a tax-inclusive sale with distinct product and variant ids) are written first and the migration runs on them; the composition is derived from the line's own numbers |
| The harness cannot touch a production database (F39/T01.01) | **EXERCISED** | `TestQtsBilling/Integration/HarnessSafetyTests.cs` â€” the production-shaped connection, the non-allowlisted host and the production connection string are all refused before a privileged connection |
| A cross-business write is refused and changes nothing (T01.02) | **EXERCISED** | `HarnessSafetyTests.T01_02` â€” the owner of business A posting a line of business B is refused; both businesses keep their exact financial state |
| An exception before the commit leaves no partial effect (T01.03) | **EXERCISED** | `HarnessSafetyTests.T01_03` â€” the audit row is written and abandoned with the transaction; the financial state is byte-identical afterwards |
| A global cancellation never hides a broken entry (T01.04) | **EXERCISED** | `HarnessSafetyTests.T01_04` â€” the per-entry assertion fails even with a compensating entry elsewhere |
| The cutover admission and execution are fail-closed (F01, F02, F05) | **EXERCISED WITH CONTROLLED DOUBLES; PRODUCTION NOT EXERCISED** | Task 21: `ProductionAdmissionTests` runs eight scenarios through `scripts/Test-ProductionAdmission.ps1`; admission performs 44 gates, 0 mutations, while a wrong server/database, tampered report hash, missing device, missing signed manifest or nonzero import stops before the next effect. A valid execution performs backup, host, import, reconcile, verify, identity reset, smoke and final integrity exactly once in order. No production target was touched. |
| The Windows client authenticates with a standards-based native flow (F07, F09) | **EXERCISED** | QB-41: a real browser session obtains an authorization code and exchanges it with a PKCE verifier at the real host; the access token reaches the API on a cookie-free client; a wrong verifier and a replayed code are refused; an unregistered redirect is refused; and a token whose membership was just revoked is authenticated but forbidden, because the permission handler reads the current membership. The refresh token is single-use and its replay is refused. |
| Offline capture requires a server-signed grant bound to the device (F10) | **NOT EXERCISED** | QB-42: the server half is EXERCISED against real PostgreSQL - issuance requires sales.post and an active device, a revoked device is 409, a cross-business location is 400 and 23503, the published key verifies the grant, and copied-device/expired/forged/clock-rollback grants are refused (DeviceGrantTests, OfflineGrantTests). The MAUI capture gate now requires the stored grant and pins the key, but no test drives SecureStorage or the MAUI host; the one-time enrolment challenge and device-side register-session binding also remain. |
| The permission policy reads the current membership (F07) | **EXERCISED** | QB-36: a revoked membership is refused with 403 while the session stays alive; a viewer is refused on a sale; the owner keeps the authority |
| The offline grant is signed and bounded (F10) | **NOT EXERCISED** | task 23 is not done |
| The settlement subledger exists (F14) | **PARTIALLY EXERCISED** | QB-33: a customer credit no longer creates a confirmed payment and a cheque is no longer bank money (three cases against the real PostgreSQL). The open-item subledger behind the receivable balance is still missing (task 13, the remaining part) |
| The stock balance is race-safe and negative stock is prohibited (F17, F18) | **EXERCISED** | QB-37: two concurrent movements on a variant without a balance produce one row and the correct total; a negative movement is refused and changes nothing |
| The composite tenant foreign keys exist (F08) | **EXERCISED** | QB-40: all six waves are live. The catalog test reads the database and finds **zero** single-column foreign keys to business-owned tables (the identity references to users/oles stay single-column by decision D-70). One raw-SQL cross-tenant row per wave is refused by PostgreSQL with 23503. T05_every_wave_can_be_reverted_and_reapplied runs all six Down methods and then all six Up methods again on a disposable database. The existing data was verified consistent before the conversion (0 cross-tenant rows). |
| The perpetual valuation is real (F15) | **EXERCISED** | QB-38: a receipt of 10 at 60 carries the value 600, a sale with a catalog price of 250 books a cost of 240, the final depletion leaves a value of zero and unvalued stock is refused |
| The receipt posts a journal (F16) | **EXERCISED** | QB-35: a receipt of 10 pieces at 60 posts DR inventory 600 / CR goods received not invoiced 600, leaves the payables untouched and balances on every entry |
| The device baseline and the atomic cache commit (F27) | **NOT EXERCISED** | tasks 26, 27 are not done |
| The release promotion is gated (F40) | **NOT EXERCISED** | task 61 is not done |
| The native onboarding package is authenticated and bounded (F42) | **EXERCISED** | Task 18: `NativePackageTests` proves a missing, altered, undeclared, traversal, absolute, case-colliding or linked resource is refused before a target write; a nested credential or card field is rejected without its value appearing in the report; a mutation after validation blocks the apply-time digest; and a preview names the exact resources, batch and target business. Pure file fixtures, no database. |
| The native import is atomic, idempotent and tenant-bound (F42/T19) | **EXERCISED on real PostgreSQL** | Task 19: `NativeImportWriterTests` 6/6 against a disposable PostgreSQL: an interrupted import leaves no domain row or link; a rerun reuses targets and leaves the journal and stock unchanged; foreign business, changed body and native-key collision are refused; openings tie to the GL, stock movement/balance and party open items; a mid-way database rejection leaves no partial rows; a control-total mismatch is refused before commit. |
| The reconciliation is exact and independently approved (F42/T20) | **EXERCISED on real PostgreSQL for the import/report; production approval NOT EXERCISED** | Task 20: `ReconciliationTests` 5/5 prove canonical body/timestamp tampering, wrong totals/tenant, stale or unrelated approvals, unsigned/unknown-key reports and authenticated round-trips are refused; `T20_06` imports a real package, builds the exact report, verifies digest/HMAC and reproduces the same canonical bytes on rerun. Owner signatures are separately verified and bound to `reportSha256`; financial exceptions are non-waivable. |
| A posted document cannot be rewritten (F17, F19, F32) | **EXERCISED** | QB-43: `PostedInvariantTests` drives the real host and raw SQL against PostgreSQL. An issued invoice refuses a header total change, a line change, a line insert, a line move to a draft (the old parent is checked), and a delete; a posted sale and its confirmed payment refuse amount changes and deletes; a posted journal entry refuses header, date and line changes; an empty or stale-total draft cannot be issued; `TRUNCATE audit_events` and `TRUNCATE journal_entries CASCADE` are refused. The legal issue and the sale/payment postings are driven through the API in the same tests. |
| Posting always belongs to exactly one open period (F20) | **EXERCISED** | QB-44: `PeriodPostingTests` proves a date before every period is refused with `ACCOUNTING_PERIOD_MISSING`, a posting after the period is closed is refused with `ACCOUNTING_PERIOD_CLOSED`, closing twice is refused, an overlapping period insert and a closed-to-open update are refused by the database, and a posting blocks while a close holds the period row and completes after the lock is released. |

## 4. The external blockers

These cannot be closed by code; they need an owner, an operator or a finance decision.

| ID | Blocker | Needed for |
|---|---|---|
| B01 | The real production topology, the active users, the devices and the current data are unknown | the native import, the cutover |
| B02 | The existing stock valuation and any prior incorrect posting are unverified | the valuation, the period opening |
| B03 | The currency settlement scale, the tax rules, the numbering and the retention need a finance decision | the settings, the documents |
| B04 | The trusted issuer, the keys, the SMTP transport and the device configuration are not provided | the native identity, the delivery |
| B05 | The business must accept the provisional offline stock, the bounded revocation lag and the cash recovery | the offline pilot |
| B06 | The encrypted device storage, the file/key backup and the point-in-time recovery are unverified | the recovery drill |
| B07 | The repository protection, the required checks and the Windows signing authority are not configured | the release |
| B08 | The pilot business, the devices, the cashiers, the dates and the acceptance thresholds are not supplied | the pilot |
| B09 | The real native import exceptions need reviewed exception identifiers and a bound report approval | the admission |
| B10 | The irreversible-action confirmation codeword has not been provided | the production write |
| B11 | The history purge (the Git history, the old releases, the archived backups, the forks) is a separate administration | the trace cleanup |
