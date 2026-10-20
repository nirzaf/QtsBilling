namespace QtsBilling.Contracts.Inventory;

/// <summary>Stock movement kind (mirrors the stored enum; serialized camelCase).</summary>
public enum StockMovementKind
{
    Opening,
    PurchaseReceipt,
    Sale,
    SaleReversal,
    PurchaseReturn,
    StockAdjustment,
    TransferIn,
    TransferOut,
}

public sealed record BusinessLocationDto(Guid Id, string Name, string? Code, string? Address, bool IsActive, long Version);

public sealed record InventoryBalanceDto(
    Guid Id,
    Guid LocationId,
    string LocationName,
    Guid VariantId,
    string VariantName,
    string? Sku,
    string? Barcode,
    decimal OnHand,
    DateTimeOffset LastMovementAtUtc,
    long Version);

public sealed record InventoryMovementDto(
    Guid Id,
    Guid LocationId,
    string LocationName,
    Guid VariantId,
    StockMovementKind MovementType,
    decimal QuantityDelta,
    decimal UnitCost,
    string ReferenceType,
    Guid ReferenceId,
    DateTimeOffset OccurredAtUtc,
    string? Memo,
    DateTimeOffset CreatedAtUtc);

/// <summary>
/// A single stock movement command. The signed <paramref name="QuantityDelta"/> must match the movement
/// kind (receipts positive, issues negative) and the (reference type, reference id, kind) triple is the
/// idempotency key: replaying it can never double-count.
/// </summary>
public sealed record RecordMovementRequest(
    Guid LocationId,
    Guid VariantId,
    StockMovementKind MovementType,
    decimal QuantityDelta,
    decimal UnitCost,
    string ReferenceType,
    Guid ReferenceId,
   string? Memo);

/// <summary>Balance-versus-movement reconciliation; mismatches are always a defect (the database forbids them).</summary>
public sealed record InventoryReconciliationDto(
    int MismatchCount,
    decimal TotalOnHand,
    decimal TotalMovementSum,
    IReadOnlyList<InventoryReconciliationRowDto> Rows);

public sealed record InventoryReconciliationRowDto(
    Guid LocationId,
    Guid VariantId,
    decimal OnHand,
    decimal MovementSum);

public enum StockTransferState
{
    Draft,
    Dispatched,
    Received,
}

public sealed record StockTransferLineRequest(Guid VariantId, decimal Quantity);

public sealed record CreateStockTransferRequest(
    Guid SourceLocationId,
    Guid DestinationLocationId,
    IReadOnlyList<StockTransferLineRequest> Lines,
    string? Note);

public sealed record StockTransferLineDto(
    Guid Id,
    int LineNo,
    Guid VariantId,
    string VariantName,
    string? Sku,
    decimal Quantity,
    decimal? UnitCostAtDispatch,
    decimal? ValueAtDispatch);

public sealed record StockTransferSummaryDto(
    Guid Id,
    string TransferNumber,
    StockTransferState Status,
    Guid SourceLocationId,
    string SourceLocationName,
    Guid DestinationLocationId,
    string DestinationLocationName,
    string Currency,
    decimal TotalQuantity,
    decimal? TotalValue,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? DispatchedAtUtc,
    DateTimeOffset? ReceivedAtUtc);

public sealed record StockTransferDetailDto(
    Guid Id,
    string TransferNumber,
    StockTransferState Status,
    Guid SourceLocationId,
    string SourceLocationName,
    Guid DestinationLocationId,
    string DestinationLocationName,
    string Currency,
    string? Note,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? DispatchedAtUtc,
    DateTimeOffset? ReceivedAtUtc,
    Guid? DispatchJournalEntryId,
    Guid? ReceiveJournalEntryId,
    IReadOnlyList<StockTransferLineDto> Lines,
    decimal TotalQuantity,
    decimal? TotalValue);

public enum StockCountState
{
    Open,
    Submitted,
    Approved,
    Cancelled,
}

public sealed record CreateStockCountRequest(Guid LocationId, string? Note);

public sealed record StockCountLineRequest(Guid VariantId, decimal CountedQuantity);

public sealed record RecordStockCountLinesRequest(IReadOnlyList<StockCountLineRequest> Lines);

public sealed record StockCountLineDto(
    Guid Id,
    Guid VariantId,
    string VariantName,
    string? Sku,
    decimal? ExpectedQuantity,
    decimal? ExpectedUnitCost,
    decimal? CountedQuantity,
    decimal? VarianceQuantity,
    decimal? VarianceValue);

public sealed record StockCountSummaryDto(
    Guid Id,
    string CountNumber,
    StockCountState Status,
    Guid LocationId,
    string LocationName,
    DateTimeOffset SnapshotAtUtc,
    DateTimeOffset? SubmittedAtUtc,
    DateTimeOffset? ApprovedAtUtc,
    int LineCount,
    int CountedLineCount);

public sealed record StockCountDetailDto(
    Guid Id,
    string CountNumber,
    StockCountState Status,
    Guid LocationId,
    string LocationName,
    DateTimeOffset SnapshotAtUtc,
    DateTimeOffset? SubmittedAtUtc,
    DateTimeOffset? ApprovedAtUtc,
    Guid? JournalEntryId,
    string? Note,
    IReadOnlyList<StockCountLineDto> Lines);
