namespace QtsBilling.Contracts.Abstractions;

/// <summary>
/// Cursor-ordered paging request. Cursors are opaque strings produced by the server
/// (stable sort key, never a bare offset) per specification §3.3/§7.1.
/// </summary>
public sealed record PagingRequest
{
    public const int MinPageSize = 1;
    public const int MaxPageSize = 200;
    public const int DefaultPageSize = 50;

    /// <summary>Opaque cursor returned by a previous page. Null requests the first page.</summary>
    public string? Cursor { get; init; }

    /// <summary>Requested page size; clamped to [1, 200] by <see cref="EffectivePageSize"/>.</summary>
    public int PageSize { get; init; } = DefaultPageSize;

    public int EffectivePageSize => Math.Clamp(PageSize, MinPageSize, MaxPageSize);
}

/// <summary>A page of results with an opaque forward cursor.</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, string? NextCursor, int PageSize)
{
    public bool HasMore => NextCursor is not null;

    public static PagedResult<T> Empty(int pageSize) => new([], null, pageSize);
}
