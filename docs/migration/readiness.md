# Native readiness ledger

**This is the honest record of what is implemented, what is verified and what is still missing.** It
follows the *SMB Completion and Production Hardening* specification: a status value describes the
stated capability, never the whole module, and a "done" task document is not a substitute for this
distinction. The ledger is updated with every change of the implementation.

| Item | Value |
|---|---|
| Repository | `nirzaf/QtsBilling` |
| Branch | `master` |
| Implementation base | the commit that carries this file (the SHA is recorded in the commit message) |
| Ledger date | 2026-10-19 |
| Specification | `QtsBilling_Atomic_Implementation_Spec.md` (64 atomic tasks, gates G0â€“G7) |

## 1. The gates

| Gate | Scope | Status | What is still missing |
|---|---|---|---|
| **G0** baseline/safety | the verified source, the test harness, the cleanup | **IN PROGRESS** | the harness exists (`TestQtsBilling/Integration/`), the retired-provider trace cleanup and the native import replacement are not done yet (tasks 02, 18â€“21) |
| **G1** money and data integrity | the exact arithmetic, the database invariants, the periods, the retry/stock/value/settlement/drawer fixes | **IN PROGRESS** | the commercial contract, the inclusive-tax posting and the entry-level balance assertions are done (QB-31, QB-32); the valuation, the settlement subledger, the period locking, the discount approval and the drawer custody are not |
| **G2** identity and onboarding | the current authorization, the composite scope, the secure web/native identity, the settings | **NOT STARTED** | the permission policy still trusts the cached claims; the composite foreign keys are partial; the native PKCE flow does not exist |
| **G3** offline safety | the device grants, the durable outcomes, the atomic cache/cursor, the coordinator, the cash custody | **NOT STARTED** | no signed offline grant, the SQLite inbox still stores a transient retry terminally, the cache and the cursor are not committed atomically |
| **G4** native data and admission | the native import, the reconciliation, the fail-closed readiness | **NOT STARTED** | the import CLI is still the source-specific one; the admission is not fail-closed (task 21) |
| **G5** daily retail usability | the shared shell, the masters, the online point of sale, the registers | **NOT STARTED** | the shared UI still supplies the customers page and the local point of sale only |
| **G6** SMB workflows | the orders, the receivables/payables, the procurement, the expenses, the stock control, the banking, the accounting and the reports | **NOT STARTED** | the APIs for the orders, the supplier bills, the quotes, the reservations, the stock counts, the bank statements are missing |
| **G7** production candidate | the CI, the signed artifacts, the backup recovery, the pilot | **NOT STARTED** | the release promotion is not gated by a tested artifact; the Windows artifact is not signed; the pilot has not run |

## 2. The requirements

`IMPLEMENTED` = the stated behaviour exists and was exercised. `PARTIAL` = a part of it exists.
`MISSING` = it does not exist. `UNVERIFIED` = it exists but no evidence proves it.

| ID | Status | Evidence and what is missing |
|---|---|---|
| R01 current-source truth and removal | PARTIAL | The runtime has no retired-provider dependency. The source-specific import project and the historical provider traces in the documentation are still there (task 02). |
| R02 safe production admission | PARTIAL | The cutover script has a codeword gate and a checklist, but the codeword is compared as a string and the admission is not environment-bound (task 21). |
| R03 identity and tenant/location isolation | PARTIAL | The fixture proves a cross-business write is refused (T01.02). The permission policy still reads the cached claims instead of the current membership, and the composite foreign keys are partial (tasks 04, 05). |
| R04 secure web/native sessions | PARTIAL | The web host has the same-origin cookie policy and the typed 401/403. The native flow is missing: no authorization code with PKCE, no system browser (task 22). |
| R05 exact pricing and payment truth | PARTIAL | **QB-31**: one shared commercial calculation for the server, the browser and the device; the inclusive-tax journal now balances (verified on PostgreSQL). Missing: the discount approval limits, the settlement scale in the business settings, the settlement subledger (tasks 08, 12, 13). |
| R06 financial immutability | PARTIAL | The journal entry balances per entry, the posted lines carry their tax composition, and the posted headers are protected in the context. Missing: the database-level protection of every posted document and the state transitions (task 09). |
| R07 stock and cost authority | PARTIAL | The stock quantities tie to the immutable movements (the database guard proves it). The valuation is still the catalog purchase price, not a perpetual average (task 15). |
| R08 retry and concurrency correctness | PARTIAL | The per-aggregate receipts and `ProcessedOperation` exist. Missing: the receipt for every command, the `409 SAME_KEY_DIFFERENT_PAYLOAD` rule, the unknown-outcome handling (task 11). |
| R09 safe offline money | PARTIAL | The provisional cash and the durable local rows exist. Missing: the durable terminal outcomes, the cash custody states, the recovery (tasks 24, 25, 29). |
| R10 correct synchronization | PARTIAL | The commit-ordered feed exists. Missing: the device-bound baseline, the epoch/floor, the atomic cache and cursor commit (tasks 26, 27). |
| R11 usable masters and point of sale | MISSING | Only the customers page and the local point of sale exist; the online browser checkout is still a stub (tasks 30, 34, 35). |
| R12 order to cash | MISSING | The quotes, the orders, the reservations, the partial deliveries and the receivables do not exist (tasks 38â€“43). |
| R13 procure to pay | MISSING | The purchase order and the receipt exist; the supplier bills, the input tax, the payables and the credits do not (tasks 45, 46). |
| R14 operational accounting | PARTIAL | The expenses and the periods exist. Missing: the counts, the paired transfers, the bank reconciliation, the close workflow (tasks 48, 51, 52). |
| R15 trustworthy reporting | PARTIAL | The balance sheet and the income statement read the authoritative journal. Missing: the exact receivables/payables tie-outs, the valuation reports, the six-decimal quantities (tasks 53, 54). |
| R16 native onboarding and import | MISSING | The import is still the source-specific one; the typed native package does not exist (tasks 18â€“20). |
| R17 UI and document quality | PARTIAL | The receipt prints the historical snapshot and names an included tax (QB-31). Missing: the shared shell, the paging, the accessibility, the safe exports (tasks 30, 54). |
| R18 operability and delivery | PARTIAL | **QB-32**: the CI now runs the real PostgreSQL integration suite. Missing: the release promotion gate, the signed artifact, the restore drill, the pilot evidence (tasks 58, 60, 61, 63). |

## 3. The critical reproductions

A case is `EXERCISED` only when it ran against the real stack and its result is recorded.

| Case | Status | Where |
|---|---|---|
| A tax-inclusive line posts a balanced journal entry (F11) | **EXERCISED** | `docs/migration/tasks/QB-31.md` â€” a 115 at 15% line posted against PostgreSQL: DR 115 / CR 100 / CR 15, the entry balance 0.0000 |
| The device and the server compute the same tax (F12) | **EXERCISED** | `TestQtsBilling/ValueObjects/CommercialAmountsTests.cs` (the shared function) and the device cache now carries the tax mode |
| The harness cannot touch a production database (F39/T01.01) | **EXERCISED** | `TestQtsBilling/Integration/HarnessSafetyTests.cs` â€” the production-shaped connection, the non-allowlisted host and the production connection string are all refused before a privileged connection |
| A cross-business write is refused and changes nothing (T01.02) | **EXERCISED** | `HarnessSafetyTests.T01_02` â€” the owner of business A posting a line of business B is refused; both businesses keep their exact financial state |
| An exception before the commit leaves no partial effect (T01.03) | **EXERCISED** | `HarnessSafetyTests.T01_03` â€” the audit row is written and abandoned with the transaction; the financial state is byte-identical afterwards |
| A global cancellation never hides a broken entry (T01.04) | **EXERCISED** | `HarnessSafetyTests.T01_04` â€” the per-entry assertion fails even with a compensating entry elsewhere |
| The cutover gates are trustworthy (F01, F05) | **NOT EXERCISED** | task 21 is not done; the current script is rehearsed but not fail-closed |
| The permission policy reads the current membership (F07) | **NOT EXERCISED** | task 04 is not done |
| The offline grant is signed and bounded (F10) | **NOT EXERCISED** | task 23 is not done |
| The settlement subledger exists (F14) | **PARTIALLY EXERCISED** | QB-33: a customer credit no longer creates a confirmed payment and a cheque is no longer bank money (three cases against the real PostgreSQL). The open-item subledger behind the receivable balance is still missing (task 13, the remaining part) |
| The perpetual valuation is real (F15) | **NOT EXERCISED** | task 15 is not done |
| The receipt posts a journal (F16) | **NOT EXERCISED** | task 16 is not done |
| The device baseline and the atomic cache commit (F27) | **NOT EXERCISED** | tasks 26, 27 are not done |
| The release promotion is gated (F40) | **NOT EXERCISED** | task 61 is not done |

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
