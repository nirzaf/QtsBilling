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
