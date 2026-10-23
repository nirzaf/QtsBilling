using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Accounting;
using QtsBilling.Contracts.Administration;
using QtsBilling.Contracts.Inventory;
using QtsBilling.Contracts.Diagnostics;
using QtsBilling.Contracts.Reports;
using QtsBilling.Contracts.Sync;
using QtsBilling.Ui.Smoke;

// The administration surfaces (specification task 14) as the shared screens inject them: fixed local answers and
// a count of the calls made, so the smoke can prove a screen does not reach the server during a prerender and can
// still be rendered by a host that has none.

namespace QtsBilling.Ui.Smoke;

/// <summary>Branch and business setup, with the readiness answer a real host would give an incomplete business.</summary>
internal sealed class StubBusinessSetupApiClient : IBusinessSetupApiClient
{
    public int Calls { get; private set; }

    public Guid BranchId { get; } = Guid.NewGuid();

    public Task<BusinessSettingsDto> GetBusinessSettingsAsync(CancellationToken cancellationToken = default)
    {
        Calls++;

        return Task.FromResult(new BusinessSettingsDto(
            Guid.NewGuid(),
            "Smoke Trading",
            "Smoke Trading FZ-LLC",
            "Asia/Qatar",
            "QAR",
            FiscalYearStartMonth: 1,
            Version: 3,
            SettlementScale: 2,
            new NumberingSettingsDto("INV-", 5),
            [new BusinessLocationDto(BranchId, "Main counter", "MAIN", "Airport road", true, 1, "Asia/Qatar"),
             new BusinessLocationDto(Guid.NewGuid(), "Second counter", "TWO", null, false, 2, null)],
            new BusinessSetupCompletenessDto(
                IsReadyToPost: false,
                ["No cash register is open at a branch this account may use."],
                HasPostedFacts: true)));
    }

    public Task<BusinessSettingsDto> SaveBusinessSettingsAsync(SaveBusinessSettingsRequest request, CancellationToken cancellationToken = default)
        => GetBusinessSettingsAsync(cancellationToken);

    public Task<IReadOnlyList<BusinessLocationDto>> ListLocationsAsync(CancellationToken cancellationToken = default)
    {
        Calls++;

        return Task.FromResult<IReadOnlyList<BusinessLocationDto>>([new BusinessLocationDto(BranchId, "Main counter", "MAIN", "Airport road", true, 1, "Asia/Qatar")]);
    }

    public Task<BusinessLocationDto> CreateLocationAsync(CreateLocationRequest request, CancellationToken cancellationToken = default)
        => ListLocationsAsync(cancellationToken).ContinueWith(page => page.Result[0], cancellationToken);

    public Task<BusinessLocationDto> UpdateLocationAsync(Guid locationId, UpdateLocationRequest request, CancellationToken cancellationToken = default)
        => ListLocationsAsync(cancellationToken).ContinueWith(page => page.Result[0], cancellationToken);

    public Task<BusinessLocationDto> DeactivateLocationAsync(Guid locationId, DeactivateLocationRequest request, CancellationToken cancellationToken = default)
        => ListLocationsAsync(cancellationToken).ContinueWith(page => page.Result[0], cancellationToken);
}

/// <summary>Accounts, including the last-owner shape the screen has to warn about before it is attempted.</summary>
internal sealed class StubAdministrationUserApiClient : IAdministrationUserApiClient
{
    public int Calls { get; private set; }

    public Guid SoleOwnerId { get; } = Guid.NewGuid();

    public Task<AdministrationUserPage> ListUsersAsync(
        string? search = null,
        string? cursor = null,
        int? pageSize = null,
        CancellationToken cancellationToken = default)
    {
        Calls++;

        IReadOnlyList<AdministrationUserDto> rows =
        [
            new AdministrationUserDto(SoleOwnerId, "owner@smoke.test", "Sole Owner", "Owner", true, false, true, 1m, [], 4, DateTimeOffset.UtcNow.AddHours(-2)),
            new AdministrationUserDto(Guid.NewGuid(), "cashier@smoke.test", "Night Cashier", "Cashier", true, true, false, 0m, [Guid.NewGuid()], 2, null),
        ];

        if (!string.IsNullOrWhiteSpace(search))
        {
            rows = [.. rows.Where(row => row.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase)
                || row.Email.Contains(search, StringComparison.OrdinalIgnoreCase))];
        }

        return Task.FromResult(new AdministrationUserPage(rows, NextCursor: null, pageSize ?? 25));
    }

    public Task<CreateAdministrationUserResultDto> CreateUserAsync(CreateAdministrationUserRequest request, CancellationToken cancellationToken = default)
    {
        Calls++;

        return Task.FromResult(new CreateAdministrationUserResultDto(
            new AdministrationUserDto(Guid.NewGuid(), request.Email, request.DisplayName, request.Role, true, true,
                request.AllLocations, request.MaxDiscountRate ?? 0m, request.LocationIds ?? [], 1, null),
            new SetupTicketDto(Guid.NewGuid(), "smoke-handover-code", "Setup", DateTimeOffset.UtcNow.AddMinutes(30))));
    }

    public Task<AdministrationUserDto> UpdateMembershipAsync(Guid userId, UpdateMembershipRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("the smoke render does not mutate authority");

    public Task<AdministrationUserDto> DisableUserAsync(Guid userId, DeactivateUserRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("the smoke render does not mutate authority");

    public Task<AdministrationUserDto> ActivateUserAsync(Guid userId, ActivateUserRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("the smoke render does not mutate authority");

    public Task<SetupTicketDto> IssueSetupTicketAsync(Guid userId, IssueSetupTicketRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("the smoke render does not issue a secret");
}

/// <summary>Two terminals: one settled, one holding cash and a review case, which is the shape the rules must explain.</summary>
internal sealed class StubDeviceAdministrationApiClient : IDeviceAdministrationApiClient
{
    public int Calls { get; private set; }

    public DeviceInfoDto Settled { get; } = new(
        Guid.NewGuid(), "Front till", "windows", "Active", Guid.NewGuid(), "Main counter", "1.0.0", 1,
        DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow.AddMinutes(-4), null, null, null, 12, 0, 0m, 5);

    public DeviceInfoDto Holding { get; } = new(
        Guid.NewGuid(), "Market stall till", "windows", "Active", Guid.NewGuid(), "Second counter", "1.0.0", 1,
        DateTimeOffset.UtcNow.AddDays(-9), DateTimeOffset.UtcNow.AddHours(-20), null, null, null, 7, 2, 240.75m, 3);

    public Task<IReadOnlyList<DeviceInfoDto>> ListDevicesAsync(CancellationToken cancellationToken = default)
    {
        Calls++;

        return Task.FromResult<IReadOnlyList<DeviceInfoDto>>([Settled, Holding]);
    }

    public Task<DeviceInfoDto> GetDeviceAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        Calls++;

        return Task.FromResult(deviceId == Settled.DeviceId ? Settled : Holding);
    }

    public Task<CreateEnrolmentChallengeResponse> CreateEnrolmentChallengeAsync(Guid locationId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("the smoke render never opens a code");

    public Task<DeviceLifecycleResponse> RevokeAsync(Guid deviceId, RevokeDeviceRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("the smoke render never revokes a terminal");

    public Task<DeviceReassignmentClearanceDto> IssueClearanceAsync(Guid deviceId, IssueReassignmentClearanceRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("the smoke render never records a clearance");

    public Task<DeviceLifecycleResponse> ReassignAsync(Guid deviceId, ReassignDeviceRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("the smoke render never moves a terminal");

    public Task<DeviceLifecycleResponse> RetireAsync(Guid deviceId, RetireDeviceRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("the smoke render never retires a terminal");
}

/// <summary>A chart of accounts with the wrong-kind trap the mapping save is expected to refuse.</summary>
internal sealed class StubAccountingApiClient : IAccountingApiClient
{
    public int Calls { get; private set; }

    public AccountDto CashAccount { get; } = new(
        Guid.NewGuid(), "1010", "Cash on hand", AccountKind.Asset, null, true, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    public AccountDto RevenueAccount { get; } = new(
        Guid.NewGuid(), "4000", "Sales revenue", AccountKind.Revenue, null, true, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    public Task<IReadOnlyList<AccountDto>> ListAccountsAsync(
        string? search = null,
        bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        Calls++;

        IReadOnlyList<AccountDto> rows = [CashAccount, RevenueAccount];

        if (!string.IsNullOrWhiteSpace(search))
        {
            rows = [.. rows.Where(account => account.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                || account.Code.Contains(search, StringComparison.OrdinalIgnoreCase))];
        }

        return Task.FromResult(rows);
    }

    public Task<BusinessAccountingSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        Calls++;

        return Task.FromResult(new BusinessAccountingSettingsDto(
            SettlementScale: 2,
            CashAccount.Id, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), RevenueAccount.Id,
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));
    }

    public Task<BusinessAccountingSettingsDto> SaveSettingsAsync(
        SaveBusinessAccountingSettingsRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("the smoke render never writes the mapping");

    /// <summary>How often an accounting screen tried to change the books. The smoke must never see this grow.</summary>
    public int Writes { get; private set; }

    public Task<AccountDto> CreateAccountAsync(SaveAccountRequest request, CancellationToken cancellationToken = default)
    {
        Writes++;
        return Task.FromResult(CashAccount);
    }

    public Task<AccountDto> UpdateAccountAsync(Guid accountId, UpdateAccountRequest request, CancellationToken cancellationToken = default)
    {
        Writes++;
        return Task.FromResult(CashAccount);
    }

    public Task<IReadOnlyList<AccountingPeriodDto>> ListPeriodsAsync(CancellationToken cancellationToken = default)
    {
        Calls++;
        IReadOnlyList<AccountingPeriodDto> rows = [SamplePeriod];
        return Task.FromResult(rows);
    }

    public Task<AccountingPeriodDto> CreatePeriodAsync(SaveAccountingPeriodRequest request, CancellationToken cancellationToken = default)
    {
        Writes++;
        return Task.FromResult(SamplePeriod);
    }

    public Task<PeriodCloseEvidenceDto> GetPeriodCloseEvidenceAsync(Guid periodId, CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult(SampleEvidence);
    }

    public Task<AccountingPeriodDto> ClosePeriodAsync(
        Guid periodId, CloseAccountingPeriodRequest request, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        Writes++;
        return Task.FromResult(SamplePeriod);
    }

    public Task<FiscalYearEvidenceDto> GetYearCloseEvidenceAsync(int fiscalYear, CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult(SampleYearEvidence(fiscalYear));
    }

    public Task<FiscalYearCloseDto> CloseYearAsync(int fiscalYear, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        Writes++;
        return Task.FromResult(new FiscalYearCloseDto(fiscalYear, new DateOnly(fiscalYear, 12, 31), "JE-2026-0001", 0m, 1));
    }

    public Task<PagedResult<JournalEntrySummaryDto>> ListJournalsAsync(
        string? cursor = null,
        int? pageSize = null,
        string? status = null,
        string? origin = null,
        DateOnly? from = null,
        DateOnly? to = null,
        CancellationToken cancellationToken = default)
    {
        Calls++;
        IReadOnlyList<JournalEntrySummaryDto> rows = [SampleSummary];
        return Task.FromResult(new PagedResult<JournalEntrySummaryDto>(rows, null, pageSize ?? 25));
    }

    public Task<JournalEntryDto> GetJournalAsync(Guid entryId, CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult(SampleEntry);
    }

    public Task<JournalEntryDto> PostJournalAsync(
        PostJournalEntryRequest request, string? idempotencyKey = null, CancellationToken cancellationToken = default)
    {
        Writes++;
        return Task.FromResult(SampleEntry);
    }

    public Task<JournalEntryDto> ReverseJournalAsync(
        Guid entryId, ReverseJournalRequest request, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        Writes++;
        return Task.FromResult(SampleEntry);
    }

    public Task<TrialBalanceDto> GetTrialBalanceAsync(
        DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult(new TrialBalanceDto(from, to, 100m, 100m, [SampleTrialRow]));
    }

    public static AccountingPeriodDto SamplePeriod { get; } = new(
        Guid.NewGuid(), "March 2026", new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), AccountingPeriodState.Open, 3);

    public static JournalEntrySummaryDto SampleSummary { get; } = new(
        Guid.NewGuid(), "JE-2026-0007", new DateOnly(2026, 3, 31), JournalEntryState.Posted, JournalOrigin.Manual,
        100m, 100m, "accrual", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    public static TrialBalanceRowDto SampleTrialRow { get; } = new(
        Guid.NewGuid(), "1010", "Cash on hand", AccountKind.Asset, 100m, 0m, 100m);

    public static JournalEntryDto SampleEntry { get; } = new(
        SampleSummary.Id, SampleSummary.EntryNumber, SampleSummary.EntryDate, JournalEntryState.Posted,
        JournalOrigin.Manual, null, "QAR", 100m, 100m, "accrual", DateTimeOffset.UtcNow, Guid.NewGuid(), null, 2,
        DateTimeOffset.UtcNow,
        [
            new JournalLineDto(Guid.NewGuid(), 1, Guid.NewGuid(), "1010", "Cash on hand", 100m, 0m, "QAR", null, null),
            new JournalLineDto(Guid.NewGuid(), 2, Guid.NewGuid(), "4000", "Sales revenue", 0m, 100m, "QAR", null, null),
        ]);

    public static PeriodCloseEvidenceDto SampleEvidence { get; } = new(
        SamplePeriod, new DateOnly(2026, 4, 1), "Asia/Qatar", 2, true, 100m, 100m, 4, 0m, 40m, 25m, 60m, 12m, 7m, 100m, 1, 0, 0,
        [
            new PeriodOutstandingDto("open-sessions", "Register sessions still open", 1, 0m, true,
                "A terminal holding uncounted cash keeps custody open (D07). Close the session, or carry its obligation forward under a named reason."),
            new PeriodOutstandingDto("receivable", "Receivables owed to the business", 0, 40m, false,
                "An open receivable is carried across the boundary; the customer statement is what proves it."),
        ],
        true, false, null, [SampleTrialRow]);

    public static FiscalYearEvidenceDto SampleYearEvidence(int fiscalYear) => new(
        fiscalYear, new DateOnly(fiscalYear, 1, 1), new DateOnly(fiscalYear, 12, 31), new DateOnly(fiscalYear + 1, 1, 1),
        "Asia/Qatar", 2, 12, 1, 0, true, false, true, "3000", null, 900m, 400m, 500m,
        [new PeriodOutstandingDto("retained-earnings", "Retained earnings account", 1, 0m, true,
            "The transfer closes revenue and expense into an equity account.")],
        []);
}

/// <summary>
/// The report statements, as the manager's screens inject them: fixed local answers, a count of the reads, and an
/// export the smoke can be shown without a device writing a file.
/// </summary>
internal sealed class StubReportApiClient : IReportApiClient
{
    public int Calls { get; private set; }

    public Task<ReceivableAgingDto> GetReceivableAgingAsync(DateOnly? asOf = null, CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult(new ReceivableAgingDto(
            Guid.NewGuid(), asOf ?? new DateOnly(2026, 10, 22), "QAR",
            [new ReceivableAgingRowDto(Guid.NewGuid(), "Harbour Supplies", 250m, 400m, 120m, 0m, 90m, 860m)],
            860m, 860m, 0m, true, false));
    }

    public Task<PayableAgingDto> GetPayableAgingAsync(DateOnly? asOf = null, CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult(new PayableAgingDto(
            Guid.NewGuid(), asOf ?? new DateOnly(2026, 10, 22), "QAR",
            [new PayableAgingRowDto(Guid.NewGuid(), "Gulf Wholesale", 500m, 0m, 0m, 210m, 0m, 710m)],
            710m, 710m, 40m, 750m, 710m, -40m, false, false));
    }

    public Task<TaxSummaryDto> GetTaxSummaryAsync(DateOnly? from = null, DateOnly? to = null, CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult(new TaxSummaryDto(
            Guid.NewGuid(), from ?? new DateOnly(2026, 10, 1), to ?? new DateOnly(2026, 10, 22), "QAR",
            [new TaxSummaryRowDto(0.05m, "Inclusive", 150m, 40m, 110m)], 150m, 40m, 110m, false));
    }

    public Task<ZReportDto> GetZReportAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult(new ZReportDto(
            Guid.NewGuid(), sessionId, Guid.NewGuid(), "Main counter", Guid.NewGuid(), "QAR", "Open",
            DateTimeOffset.UtcNow, null, 500m, 12, 2_400m, 100m, 110m, 2_290m, 2_400m,
            [new PaymentMethodTotalDto("Cash", 9, 1_800m)], 1, 60m, [new PaymentMethodTotalDto("Cash", 1, 60m)],
            0m, 0m, 2_340m, null, null, false));
    }

    public Task<BalanceSheetDto> GetBalanceSheetAsync(DateOnly? asOf = null, string? currency = null, CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult(new BalanceSheetDto(
            Guid.NewGuid(), asOf ?? new DateOnly(2026, 10, 22), "QAR",
            [new StatementAccountDto(Guid.NewGuid(), "1000", "Cash on hand", 2_340m),
             new StatementAccountDto(Guid.NewGuid(), "1100", "Accounts receivable", 860m)],
            [new StatementAccountDto(Guid.NewGuid(), "2000", "Accounts payable", 710m)],
            [new StatementAccountDto(Guid.NewGuid(), "3000", "Retained earnings", 1_200m)],
            3_200m, 710m, 1_200m, 1_290m, 3_200m, true, false));
    }

    public Task<ProfitAndLossDto> GetProfitAndLossAsync(DateOnly? from = null, DateOnly? to = null, string? currency = null, CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult(new ProfitAndLossDto(
            Guid.NewGuid(), from ?? new DateOnly(2026, 10, 1), to ?? new DateOnly(2026, 10, 22), "QAR",
            [new StatementAccountDto(Guid.NewGuid(), "4000", "Sales revenue", 2_290m)],
            [new StatementAccountDto(Guid.NewGuid(), "5000", "Cost of goods sold", 1_000m)],
            2_290m, 1_000m, 1_290m, false));
    }

    public Task<StockSummaryDto> GetStockSummaryAsync(DateOnly? asOf = null, Guid? locationId = null, string? currency = null, CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult(new StockSummaryDto(
            Guid.NewGuid(), asOf ?? new DateOnly(2026, 10, 22), "QAR",
            [new StockSummaryRowDto(Guid.NewGuid(), "Main counter", Guid.NewGuid(), "Rice 25kg", "RICE-25", 12.5m, 1_000m, DateTimeOffset.UtcNow)],
            12.5m, 1_000m, false));
    }

    public Task<CashSummaryDto> GetCashSummaryAsync(DateOnly? from = null, DateOnly? to = null, string? currency = null, CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult(new CashSummaryDto(
            Guid.NewGuid(), from ?? new DateOnly(2026, 10, 1), to ?? new DateOnly(2026, 10, 22), "QAR",
            [new CashSummaryRowDto(Guid.NewGuid(), "1000", "Cash on hand", 500m, 1_800m, 0m, 2_300m)],
            500m, 1_800m, 0m, 2_300m, false));
    }

    public Task<SalesSummaryDto> GetSalesSummaryAsync(DateOnly? from = null, DateOnly? to = null, string? currency = null, CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult(new SalesSummaryDto(
            Guid.NewGuid(), from ?? new DateOnly(2026, 10, 1), to ?? new DateOnly(2026, 10, 22), "QAR",
            12, 2_200m, 100m, 110m, 2_210m, [new PaymentMethodTotalDto("Cash", 9, 1_800m)], false));
    }

    public Task<ReportExport> ExportAsync(ReportRequest request, CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult(new ReportExport(
            ReportApiClient.DefaultFileName(request.Kind),
            System.Text.Encoding.UTF8.GetBytes("\"Section\",\"Item\",\"Field\",\"Label\",\"Value\"\n")));
    }
}

/// <summary>
/// The audit feed and the instance's own diagnostics, as the operator's screens inject them: fixed local answers and
/// a count of the reads, so the smoke can prove a screen reads nothing before it is asked to.
/// </summary>
internal sealed class StubOperationsApiClient : IOperationsApiClient
{
    public int Calls { get; private set; }

    public int BundleRequests { get; private set; }

    public Task<PagedResult<QtsBilling.Contracts.Audit.AuditEventDto>> ListAuditEventsAsync(
        string? entityType = null,
        string? action = null,
        Guid? userId = null,
        DateOnly? from = null,
        DateOnly? to = null,
        string? cursor = null,
        int? pageSize = null,
        CancellationToken cancellationToken = default)
    {
        Calls++;
        IReadOnlyList<QtsBilling.Contracts.Audit.AuditEventDto> rows =
        [
            new QtsBilling.Contracts.Audit.AuditEventDto(
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, "invoice", Guid.NewGuid(), "posted",
                DateTimeOffset.UtcNow, "0f2c7c10-0000-4000-8000-000000000001", """{"note":"posted"}"""),
        ];
        return Task.FromResult(new PagedResult<QtsBilling.Contracts.Audit.AuditEventDto>(rows, null, pageSize ?? 50));
    }

    public Task<DiagnosticsDto> GetDiagnosticsAsync(CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult(new DiagnosticsDto(
            new StoreDiagnosticsDto(true, 4, "PostgreSQL 18.6", "20261022214334_AddBankReconciliationControl", 67, DateTimeOffset.UtcNow),
            new CapacityDiagnosticsDto("/var/lib/postgresql", 64_000_000_000, 18_000_000_000, 71.9, 90, false, true),
            new BackupEvidenceDto(true, "backups", "qtsbilling-20261020-160853.dump.json", DateTimeOffset.UtcNow.AddHours(-9), 246_654, "FD23", 9.0, 26, false, "no restore verification record was found in this directory"),
            new DeviceFleetDiagnosticsDto(2, 0, 0, 1, 24, ["Counter 3"], 1, 12),
            new SyncDiagnosticsDto(41_207, 12, 3, 1, 2, 1, "INSUFFICIENT_STOCK", DateTimeOffset.UtcNow.AddHours(-1), "0f2c7c10-0000-4000-8000-000000000002"),
            new KeyExpiryDiagnosticsDto(false, null, null, null, 30, true, "no signing certificate is configured on this instance"),
            7,
            [new DiagnosticsAlertDto("operations-rejected", "critical", "1 captured operation was refused in the last 24 hours, newest code INSUFFICIENT_STOCK",
                "A refused capture means money or stock a terminal took that the ledger has not accepted.",
                "Open the audit feed, find the operation, and resolve it with the terminal's operator.",
                "0f2c7c10-0000-4000-8000-000000000002")]));
    }

    public Task<SupportBundleDto> CreateSupportBundleAsync(string reason, CancellationToken cancellationToken = default)
    {
        BundleRequests++;
        return Task.FromResult(new SupportBundleDto(
            "qts-support-bundle-20261023-010000-abc12345.json", 4_096, DateTimeOffset.UtcNow, reason,
            ["diagnostics", "recent audit entries (summaries scrubbed)"],
            ["connection strings and any password inside them", "bearer tokens, refresh tokens and enrolment codes"]));
    }
}

/// <summary>A host that can put a file in the operator's hands, and remembers whether it was asked to.</summary>
internal sealed class StubLocalFileOutput : ILocalFileOutput
{
    public int Saves { get; private set; }

    public StoredFile? Last { get; private set; }

    public bool CanSaveFiles => true;

    public Task<bool> SaveAsync(StoredFile file, CancellationToken cancellationToken = default)
    {
        Saves++;
        Last = file;
        return Task.FromResult(true);
    }
}

/// <summary>The report statements the manager's screens read, and the local file the export writes.</summary>
internal static class ReportSmokeSamples
{
    public static byte[] Csv => System.Text.Encoding.UTF8.GetBytes("\"Section\",\"Item\",\"Field\",\"Label\",\"Value\"\n");
}
