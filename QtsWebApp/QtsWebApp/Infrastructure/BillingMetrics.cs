using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace QtsWebApp.Infrastructure;

/// <summary>Low-cardinality business metrics exposed to Prometheus through OpenTelemetry.</summary>
public static class BillingMetrics
{
    public const string MeterName = "QtsBilling.Server";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> SyncOperations = Meter.CreateCounter<long>(
        "qts_sync_operations",
        description: "Offline sync operations processed by outcome and operation type.");

    private static readonly Histogram<double> PosSalePostingDuration = Meter.CreateHistogram<double>(
        "qts_pos_sale_posting_duration_ms",
        unit: "ms",
        description: "Elapsed time spent posting a POS sale.");

    private static readonly Histogram<double> AccountingPeriodLockWait = Meter.CreateHistogram<double>(
        "qts_accounting_period_lock_wait_ms",
        unit: "ms",
        description: "Elapsed time waiting to acquire the accounting period share lock.");

    private static readonly Counter<long> IdempotencyReplays = Meter.CreateCounter<long>(
        "qts_idempotency_replays",
        description: "Idempotency key replays and payload conflicts.");

    public static void RecordSyncOperation(string outcome, string? operationType)
    {
        var boundedType = string.Equals(operationType, "pos.sale.capture.v1", StringComparison.Ordinal)
            ? "pos.sale.capture.v1"
            : "unsupported";
        var tags = new TagList
        {
            { "outcome", outcome },
            { "operation_type", boundedType },
        };

        SyncOperations.Add(1, tags);
    }

    public static void RecordPosSalePostingDuration(double elapsedMilliseconds)
        => PosSalePostingDuration.Record(Math.Max(0, elapsedMilliseconds));

    public static void RecordAccountingPeriodLockWait(double elapsedMilliseconds)
        => AccountingPeriodLockWait.Record(Math.Max(0, elapsedMilliseconds));

    public static void RecordIdempotencyReplay(string scope, string result)
    {
        var tags = new TagList
        {
            { "scope", scope },
            { "result", result },
        };

        IdempotencyReplays.Add(1, tags);
    }
}
