using QtsBilling.Contracts.Accounting;
using QtsBilling.Contracts.Administration;
using QtsBilling.Contracts.Inventory;
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
}
