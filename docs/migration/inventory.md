# QB-00 — Source, Data and Deployment Inventory (Gate G0)

> **Status: PARTIAL — live-data inventory is UNVERIFIED.**
> No live MongoDB instance, production database, workstation or backup was inspected.
> Per specification §1.3, items that could not be observed are recorded as **UNVERIFIED** and
> production cutover must not be attempted until they are resolved.
>
> Produced: 2026-10-19 · Branch: `migration/pg-sqlite-maui`

## 1. Source baseline

| Item | Value |
|---|---|
| Local repository commit | `9a7c09371d090e0b463446ece913c24594821e03` — *"Initial commit: open-source release (secrets removed, env-based config)"* |
| Branch at inventory time | `master` → implementation branch `migration/pg-sqlite-maui` |
| Specification baseline | `nirzaf/QtsBilling@952c9b26a23af165dc42e6c83de99ed5dc8922c1` (**not present in this local clone**) |
| Divergence reconciliation | The local clone is a single squashed commit of the open-source release. It contains the same application code the specification audited, with two deltas: (a) secrets removed — MongoDB/encryption values now come from environment variables; (b) the solution has already been upgraded to .NET 10 / C# 14 (see `docs/migration/decisions.md` D-15). Every F-xx finding in §6 below was re-verified against this local tree. |
| `AGENTS.md` / repository policy | None present (re-checked). `.github/` contains only issue/PR templates; **no CI workflow exists yet** (QB-26). |

## 2. Current solution / project graph

| Project | TFM (current) | Kind | References | Disposition (§1.1) |
|---|---|---|---|---|
| `QtsModels` | `net10.0` | Library | `MongoDB.Bson` | Keep domain concepts; strip Mongo types (QB-02/QB-29) |
| `QtsServices` | `net10.0` | Library | `MongoDB.Bson`, `MongoDB.Driver`, `QtsModels` | Keep semantics; move persistence behind EF (QB-03…QB-13) |
| `QtsBilling` | `net10.0-windows` | WinForms `WinExe` | `QtsServices`, `QtsModels` | **Delete at authorized cleanup (QB-29)**; replace by `QtsBilling.Maui` (QB-18) |
| `QtsWebApp/QtsWebApp` | `net10.0-windows` | ASP.NET Core host (Blazor Web App) | **`QtsBilling` (WinForms!)**, `QtsWebApp.Client`, `MudBlazor`, `…WebAssembly.Server` | Retain as server/API host; retarget `net10.0`, drop WinForms ref (QB-01) |
| `QtsWebApp/QtsWebApp.Client` | `net10.0-windows` | Blazor WebAssembly | **`QtsServices` (Mongo)**, `MudBlazor`, `Blazored.LocalStorage` | Thin HTTP-only client; remove DB dependency (QB-01 interim / QB-15 final) |
| `TestQtsBilling` | `net10.0` | xUnit | `QtsModels`, `QtsServices`, `Bogus`, `Moq`, `MongoDB.*` | Replace with domain/PostgreSQL/SQLite/API tests (QB-26) |

**Architectural defect confirmed (spec §1.1):** the web host references the WinForms executable; the browser project references the Mongo-backed service assembly. Both edges are removed by QB-01/QB-15.

## 3. Runtime and deployment variants (code-level observation)

| Variant | Entry point | Database topology observed in code |
|---|---|---|
| Desktop (WinForms) | `QtsBilling/Program.cs` → `UIForms/Customers` | Indirect via `QtsServices` |
| Web server | `QtsWebApp/QtsWebApp/Program.cs` (Blazor Web App, `InteractiveAuto`) | Indirect via `QtsServices`; no API endpoints, no auth |
| Web client (WASM) | `QtsWebApp.Client/Program.cs` — registers `CustomerService` **in the browser**, plus `AddBlazoredLocalStorage`, `AddMudServices`, `AddLocalization` | Attempts direct DB access from browser (F-19) |
| Tests | `TestQtsBilling/*` | Hits configured Mongo (local and remote) directly |

**Split connection topology (F-02):** every service constructs its own `MongoClient` in its constructor.

| Connection string | Services |
|---|---|
| `QTS_MONGODB_LOCAL` (`GetLocalConnectionString`) | `CustomerService`, `InventoryManagementService`, `SystemUtilitiesService` (local side) |
| `QTS_MONGODB_REMOTE` (`GetRemoteConnectionString`) | `AccountingService`, `BalanceSheetService`, `DiscountService`, `ExceptionService`, `ExpenseService`, `InvoiceService`, `JournalEntryService`, `PosService`, `ProductCategoryService`, `ProductGroupService`, `PurchaseOrderService`, `TaxService`, `UserManagementService`, `SystemUtilitiesService` (cloud side) |

`GlobalConstants.GetLocalConnectionString()` defaults to `mongodb://localhost:27017/qts-Billing`; `GetRemoteConnectionString()` throws when `QTS_MONGODB_REMOTE` is unset. Database name used by all services: `qtsBilling`.

**Deployment facts: UNVERIFIED.** No production/staging/local MongoDB instance was reachable from this environment (a configured Atlas cluster was unreachable at inventory time), therefore per-environment collection lists, document counts, versions, indexes, BSON type usage, data volume and backup state are **unknown**. Environment variable names present in the code: `QTS_MONGODB_REMOTE`, `QTS_MONGODB_LOCAL`, `QTS_ENCRYPTION_KEY`, `QTS_ENCRYPTION_IV` (+ defaults in `.env.example`).

## 4. Collection inventory (code-level — live counts UNVERIFIED)

Collections referenced by inspected code, their owning service and connection side. **Document counts and existence in each environment are UNVERIFIED.**

| Mongo collection | Model | Owner service(s) | Connection | Classification | Target aggregate (§6.2) |
|---|---|---|---|---|---|
| `Customers` | `Customer` | `CustomerService` | local | active | `customers` |
| `Products` | `Product` | `InventoryManagementService`, `PosService` | **local + remote** | active (conflict F-02) | `products` / `product_variants` |
| `Inventory` | `Inventory` | `InventoryManagementService` | local | active (F-12) | `inventory_balances`, `inventory_movements` |
| `Suppliers` | `Supplier` | `InventoryManagementService` | local | active | `suppliers` |
| `Accounts` | `Account` | `AccountingService` | remote | active | `accounts` |
| `Transactions` | `Transaction` | `AccountingService` | remote | active (unbalanced allowed F-03) | `journal_entries` / `journal_lines` |
| `JournalEntries` | `Transaction` (same model!) | `JournalEntryService` | remote | active (duplicate ledger F-04) | same canonical tables, source namespace |
| `Sales` | `Sale` | `PosService` | remote | active (non-atomic F-01) | `sales` / `sale_lines` |
| `Invoices` | `Invoice` | `InvoiceService` | remote | active (embedded products F-10) | `invoices` / `invoice_lines` / `invoice_sequences` |
| `PurchaseOrders` | `PurchaseOrder` | `PurchaseOrderService` | remote | active (embedded products F-10) | `purchase_orders` / lines / receipts |
| `BalanceSheets` | `BalanceSheet` | `BalanceSheetService` | remote | manual CRUD (F-11) | archived snapshot metadata only |
| `Discounts` | `Discount` | `DiscountService` | remote | active | `discounts` |
| `Taxes` | `Tax` | `TaxService` | remote | active (double F-18) | `tax_rates` |
| `Expenses` | `Expense` | `ExpenseService` | remote | active | `expenses` |
| `ProductCategories` | `ProductCategory` | `ProductCategoryService` | remote | active (overlaps `Category` model) | `categories` |
| `ProductGroups` | `ProductGroup` | `ProductGroupService` | remote | active | `product_groups` |
| `Users` | `User` | `UserManagementService` | remote | active (plaintext F-06) | Identity + `business_memberships` |
| `Logs` | `Log` | `ExceptionService` | remote | active (recursive failure F-17) | `audit_events` / server logs |
| `SystemConfigurations` | `SystemConfiguration` | `SystemUtilitiesService` | remote | active | configuration metadata |
| `Backups` | `Backup` | `SystemUtilitiesService` | remote | metadata only (F-16) | new backup subsystem (QB-14/QB-27) |

**Additional collections may exist in production** (created by earlier versions, tests or manual operations). They must be discovered by the QB-23 read-only tool; §6.2 rules apply (no unclassified nonempty collection may be silently discarded).

## 5. Model disposition

24 model types are referenced by services (active/persisted per code): `Account, Backup, BalanceSheet, Customer, Discount, Expense, Inventory, Invoice, Log, Product, ProductCategory, ProductGroup, PurchaseOrder, Receipt, Sale, SaleItem, Supplier, System, SystemConfiguration, Tax, TaxRate, Transaction, User`.

62 model types have **no service-level reference** (may still be nested inside referenced models or used only by the legacy UI; they are *not* evidence of implemented workflows): `AccountTransaction, AccountType, ActivityLog, Barcode, BaseEntity, Booking, Brand, Business, BusinessLocation, CashRegister, CashRegisterTransaction, Category, Contact, Currency, CustomerGroup, DashboardConfiguration, DiscountVariation, DocumentAndNote, EntryType, ExpenseCategory, FinancialReport, GroupSubTax, InvoiceLayout, InvoiceScheme, Ledger, Media, ModelHasPermission, ModelHasRole, Models, Notification, NotificationTemplate, PasswordReset, PaymentMethodType, Permission, Printer, ProductLocation, ProductRack, ProductVariation, PurchaseLine, ReferenceCount, ResProductModifierSet, ResTable, Role, RoleHasPermission, SellingPriceGroup, SellLineWarranty, Session, StockAdjustmentLine, StockAdjustmentTemp, TransactionEntry, TransactionPayment, TransactionSellLine, TransactionSellLinesPurchaseLine, TypeOfService, Unit, UserContactAccess, Variation, VariationGroupPrice, VariationLocationDetail, VariationTemplate, VariationValueTemplate, Warranty`.

> `TransactionPayment` (with `CardNumber`/`CardSecurity`) and `TransactionEntry` are nested in `Transaction`/`Sale` flows and **are** material for migration and for the card-data remediation (F-07).

## 6. Finding verification against this snapshot

| ID | Status | Local evidence |
|---|---|---|
| F-01 | Confirmed | `QtsServices/PosService.cs` L28 `UpdateInventory(sale.Items); return true;` — independent writes, no transaction/stock check |
| F-02 | Confirmed | `InventoryManagementService.cs` L16 local vs `PosService.cs` L14 remote, both writing `Products` |
| F-03 | Confirmed | `AccountingService.cs` L38 `RecordTransaction` inserts without debit/credit validation |
| F-04 | Confirmed | `AccountingService` (`Transactions`) and `JournalEntryService` (`JournalEntries`) both use `QtsModels.Transaction` |
| F-05 | Confirmed | `SystemUtilitiesService.cs` L30/49/68/87 `SyncAllCollections`/`SyncBidirectionally` (`ReplaceOne`, `InsertManyAsync`), no revisions/tombstones/idempotency |
| F-06 | Confirmed | `UserManagementService.cs` L22–28 `Login` filters on `Email` **and** raw `Password` |
| F-07 | Confirmed | `QtsModels/TransactionPayment.cs` L26 `CardNumber`, L36 `CardSecurity` (CVV) |
| F-08 | Confirmed (mitigated by env vars, defect remains) | `QtsServices/UtilityServices/EncryptionService.cs` AES-CBC with deployment-wide key+IV from env; no nonce per value, no AEAD tag; duplicate helper in `QtsBilling/Utilities/EncryptionService.cs` |
| F-09 | Confirmed | `PosService.cs` L59–65 `HandlePayment` returns `true` unconditionally; L75 `GenerateReceipt` stub |
| F-10 | Confirmed | `QtsModels/Invoice.cs` L14 `List<Product> Products`; `PurchaseOrder.cs` L14 `List<Product> Items` |
| F-11 | Confirmed | `BalanceSheetService` CRUD on manually supplied `BalanceSheet` |
| F-12 | Confirmed | `InventoryManagementService.cs` L123–124 upsert + `ModifiedCount > 0` |
| F-13 | Confirmed | `int BusinessId` etc. in models vs ObjectId-string IDs (e.g. `Product.BusinessId`, `User.BusinessId`) |
| F-14 | Confirmed | `CustomerService.EnsureUniquePhoneNumber()` fire-and-forget index creation; unordered paging |
| F-15 | Confirmed | `DeleteOne` operations across sales/master/financial services (no referential guard) |
| F-16 | Confirmed | `SystemUtilitiesService.cs` L146/162 backup metadata only; restore returns success without restoring |
| F-17 | Confirmed | `ExceptionService` ctor queries remote Mongo and is invoked from catch blocks (e.g. `CustomerService.PagedCustomersAsync`); observed to surface as a 500 when the remote cluster is unreachable |
| F-18 | Confirmed | `QtsModels/Tax.cs` L13 `public double Rate` |
| F-19 | Confirmed | `QtsWebApp.Client/Program.cs` registers `CustomerService` (Mongo) in the browser |
| F-20 | Confirmed | `TestQtsBilling/TestPosServicesMethods.cs` fabricates product IDs; sync tests assert "no exception" |
| F-21 | Confirmed | `Business.AccountingMethod` (fifo/lifo/avco) with no costing implementation in services |
| F-22 | Confirmed | Tenant boundary present on `User`/`Account`/`Category` only |

### Remediation status (updated 2026-10-19, after QB-06/QB-15)

| Finding | Status | Evidence |
|---|---|---|
| F-06 | **Remediated in the web host** — ASP.NET Core Identity (salted hashes, lockout); the raw-password `UserManagementService.Login` is no longer referenced by any host and is removed with the legacy stack (QB-29). | `docs/migration/tasks/QB-04.md` |
| F-14 | **Remediated** — `customers.phone_normalized` unique per business (database index + API pre-check, `DUPLICATE_PHONE` 409); keyset paging replaces unordered offsets. | `docs/migration/tasks/QB-06.md` |
| F-19 | **Remediated for the browser surface** — the WebAssembly bundle no longer contains Mongo/Npgsql/EF/QtsServices assets; pages use the typed API client. | `docs/migration/tasks/QB-15.md` |
| F-22 | **Remediated for the migrated tables** — explicit `business_id` on every row, composite `(id, business_id)` keys/FKs, tenant id taken from the authenticated principal (`404` on foreign ids). | QB-03 live guard table, QB-06 evidence |
| F-01…F-05, F-07…F-13, F-15…F-18, F-20, F-21 | Open — schema/guards are in place where applicable (QB-03); service and UI remediation lands with QB-07…QB-14. | `docs/migration/task-status.md` |

## 7. Active UI workflows (code-level)

- Web: `Home` (static), `Customers` (search, paged table, add dialog, delete with confirm), MudBlazor layout/nav. All data access runs in-process against `QtsServices` (`InteractiveAuto` renders server-side first, then WASM).
- Desktop (WinForms): single customer form (`UIForms/Customers`).
- There is **no code-backed evidence** of implemented POS/accounting/purchasing/inventory UI; those are service-only (spec §8.1).
- No authenticated session, no authorization checks anywhere (no auth middleware, no login UI).

## 8. Unknowns and blockers (require owners; block later gates)

| # | Unknown | Impact | Required before |
|---|---|---|---|
| U-01 | Live Mongo collections, document counts, duplicates, BSON types, per-environment topology | Cutover, reconciliation | G5 (QB-23/24/25) |
| U-02 | Business/tenant ownership of every legacy record; multi-business or single-business | Tenant mapping, F-13/F-22 resolution | G5 |
| U-03 | `int` ↔ ObjectId reference meanings with no verifiable external key | FK reconstruction | G5 |
| U-04 | Identity provider choice & legacy user contactability for password reset | QB-04, F-06 remediation | G1/G5 |
| U-05 | Statutory invoice numbering/series and tax-compliance requirements | QB-11 | G2 |
| U-06 | Supported stock costing method (FIFO/LIFO/AVCO) | COGS, inventory valuation | G2 (blocking if unsupported) |
| U-07 | Currency/business base currency set | Money model, reporting | G2 |
| U-08 | Windows fleet versions, WebView2/MSIX prerequisites, BitLocker policy | MAUI deployment, SQLite confidentiality | G3/G6 |
| U-09 | Offline grant TTL acceptance (default ≤24 h) and offline price-tolerance policy | QB-19/20/22 | G4 |
| U-10 | Backup retention & legacy-data destruction policy; card-data incident assessment owner | QB-14/25/27 | G6 |
| U-11 | Pending unsynced data on workstations at freeze | Cutover completeness | G5/G7 |
| U-12 | Whether the remote Atlas cluster in use is production — containing plaintext passwords/CVV — and who owns that remediation | Security P0 | Immediately at cutover planning |

## 9. Environment observations for implementation

- .NET SDK `10.0.401`; runtimes `Microsoft.NETCore.App`/`Microsoft.AspNetCore.App`/`WindowsDesktop.App` 10.0.12.
- No .NET MAUI workload installed (`maui-windows` available) — required by QB-18.
- Docker CLI present but engine **not running**; no host `psql`/`pg_ctl`. A PostgreSQL 18 instance must be provided (Docker Desktop start or approved install) before QB-03 integration verification and QB-26 CI integration tests.
- Network access to NuGet works; the configured remote Atlas cluster is unreachable from this environment (observed connection timeout), so legacy-data work cannot start here.
