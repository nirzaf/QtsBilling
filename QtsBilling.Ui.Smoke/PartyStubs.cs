using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Purchasing;

// The party workbench surfaces (specification task 15) as the shared screens inject them: bounded pages, the
// server's cursor echoed back, and a count of the reads so a prerender that asks for data is visible as a failure
// rather than a page that happens to look right.

namespace QtsBilling.Ui.Smoke;

internal sealed class StubSupplierApiClient : ISupplierApiClient
{
    /// <summary>Reads the screen made, with the page size and cursor it asked for.</summary>
    public List<(string? Search, string? Cursor, int PageSize, bool? IncludeInactive)> Reads { get; } = [];

    public Task<PagedResult<SupplierDto>> GetSuppliersAsync(
        string? search = null,
        string? cursor = null,
        int pageSize = 25,
        bool? includeInactive = null,
        CancellationToken cancellationToken = default)
    {
        Reads.Add((search, cursor, pageSize, includeInactive));

        IReadOnlyList<SupplierDto> page =
        [
            Sample("Wholesale Dairy Co"),
            Sample("Old Town Stationers"),
        ];

        return Task.FromResult(new PagedResult<SupplierDto>(page, NextCursor: null, pageSize));
    }

    public Task<SupplierDto> GetSupplierAsync(Guid id, CancellationToken cancellationToken = default)
        => Task.FromResult(Sample("Wholesale Dairy Co", id));

    public Task<SupplierDto> CreateSupplierAsync(SaveSupplierRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(Sample(request.Name));

    public Task<SupplierDto> UpdateSupplierAsync(Guid id, SaveSupplierRequest request, long version, CancellationToken cancellationToken = default)
        => Task.FromResult(Sample(request.Name, id));

    public Task DeactivateSupplierAsync(Guid id, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    private static SupplierDto Sample(string name, Guid? id = null)
        => new(id ?? Guid.CreateVersion7(), name, "orders@example.test", "+974 5550 0000", "Warehouse road 12", true, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
}
