using QtsWebApp.Infrastructure;
using Xunit;

namespace TestQtsBilling.Integration;

public sealed class OperationalMetricsTests(PostgresApiFixture fixture) : IClassFixture<PostgresApiFixture>
{
    [Fact]
    public async Task Prometheus_scrape_exposes_the_required_business_metrics_and_low_cardinality_tags()
    {
        BillingMetrics.RecordSyncOperation("posted", "pos.sale.capture.v1");
        BillingMetrics.RecordPosSalePostingDuration(12.5);
        BillingMetrics.RecordAccountingPeriodLockWait(3.5);
        BillingMetrics.RecordIdempotencyReplay("invoice-payment", "replayed");

        using var client = fixture.Factory.CreateClient();
        using var response = await client.GetAsync("/metrics");
        var content = await response.Content.ReadAsStringAsync();

        response.EnsureSuccessStatusCode();
        Assert.Contains("qts_sync_operations_total", content, StringComparison.Ordinal);
        Assert.Contains("operation_type=\"pos.sale.capture.v1\"", content, StringComparison.Ordinal);
        Assert.Contains("outcome=\"posted\"", content, StringComparison.Ordinal);
        Assert.Contains("qts_pos_sale_posting_duration_ms", content, StringComparison.Ordinal);
        Assert.Contains("qts_accounting_period_lock_wait_ms", content, StringComparison.Ordinal);
        Assert.Contains("qts_idempotency_replays_total", content, StringComparison.Ordinal);
        Assert.Contains("scope=\"invoice-payment\"", content, StringComparison.Ordinal);
        Assert.Contains("result=\"replayed\"", content, StringComparison.Ordinal);
    }
}
