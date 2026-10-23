using System.Globalization;
using QtsBilling.Contracts.Serialization;

namespace QtsBilling.Contracts.Reports;

/// <summary>Which statement is being asked for. The words are the contract's; the route is built from them.</summary>
public enum ReportKind
{
    ReceivableAging,
    PayableAging,
    TaxSummary,
    BalanceSheet,
    ProfitAndLoss,
    StockSummary,
    CashSummary,
    SalesSummary,
    ZReport,
}

/// <summary>
/// One report question, in the terms a manager uses: which statement, over what window, at which branch, in which
/// currency. A caller never builds a route and never types an identifier it could not read on a screen, so an export
/// cannot be coaxed into naming another business's document (specification R07).
/// </summary>
public sealed record ReportRequest(
    ReportKind Kind,
    DateOnly? AsOf = null,
    DateOnly? From = null,
    DateOnly? To = null,
    Guid? LocationId = null,
    string? Currency = null,
    Guid? SessionId = null);

/// <summary>
/// The typed read side of the reporting API (specification task 33). One client, one set of routes, shared by the
/// browser, the desktop shell and the export - so a figure on a screen and a figure in a CSV can never come from
/// different arithmetic. Every statement is derived from posted facts on the server; this type reads them.
/// </summary>
public interface IReportApiClient
{
    Task<ReceivableAgingDto> GetReceivableAgingAsync(DateOnly? asOf = null, CancellationToken cancellationToken = default);

    Task<PayableAgingDto> GetPayableAgingAsync(DateOnly? asOf = null, CancellationToken cancellationToken = default);

    Task<TaxSummaryDto> GetTaxSummaryAsync(DateOnly? from = null, DateOnly? to = null, CancellationToken cancellationToken = default);

    Task<ZReportDto> GetZReportAsync(Guid sessionId, CancellationToken cancellationToken = default);

    Task<BalanceSheetDto> GetBalanceSheetAsync(DateOnly? asOf = null, string? currency = null, CancellationToken cancellationToken = default);

    Task<ProfitAndLossDto> GetProfitAndLossAsync(
        DateOnly? from = null, DateOnly? to = null, string? currency = null, CancellationToken cancellationToken = default);

    Task<StockSummaryDto> GetStockSummaryAsync(
        DateOnly? asOf = null, Guid? locationId = null, string? currency = null, CancellationToken cancellationToken = default);

    Task<CashSummaryDto> GetCashSummaryAsync(
        DateOnly? from = null, DateOnly? to = null, string? currency = null, CancellationToken cancellationToken = default);

    Task<SalesSummaryDto> GetSalesSummaryAsync(
        DateOnly? from = null, DateOnly? to = null, string? currency = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// The same statement as a local CSV file, produced by the server from the same posted facts. The returned name is
    /// the one the server offered, so a saved file reads as the report it holds.
    /// </summary>
    Task<ReportExport> ExportAsync(ReportRequest request, CancellationToken cancellationToken = default);
}

/// <summary>One exported report: the bytes and the name the server gave them.</summary>
public sealed record ReportExport(string FileName, byte[] Content)
{
    public int Bytes => Content.Length;
}

/// <inheritdoc cref="IReportApiClient" />
public sealed class ReportApiClient : IReportApiClient
{
    public const string RoutePrefix = "/api/v1/reports";

    private readonly HttpClient _http;

    public ReportApiClient(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    public Task<ReceivableAgingDto> GetReceivableAgingAsync(DateOnly? asOf = null, CancellationToken cancellationToken = default)
        => ReadAsync<ReceivableAgingDto>(Build(ReportKind.ReceivableAging, asOf: asOf), cancellationToken);

    public Task<PayableAgingDto> GetPayableAgingAsync(DateOnly? asOf = null, CancellationToken cancellationToken = default)
        => ReadAsync<PayableAgingDto>(Build(ReportKind.PayableAging, asOf: asOf), cancellationToken);

    public Task<TaxSummaryDto> GetTaxSummaryAsync(
        DateOnly? from = null, DateOnly? to = null, CancellationToken cancellationToken = default)
        => ReadAsync<TaxSummaryDto>(Build(ReportKind.TaxSummary, from: from, to: to), cancellationToken);

    public Task<ZReportDto> GetZReportAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("A Z report names the register session it closes.", nameof(sessionId));
        }

        return ReadAsync<ZReportDto>($"{RoutePrefix}/z-report/{sessionId:D}", cancellationToken);
    }

    public Task<BalanceSheetDto> GetBalanceSheetAsync(
        DateOnly? asOf = null, string? currency = null, CancellationToken cancellationToken = default)
        => ReadAsync<BalanceSheetDto>(Build(ReportKind.BalanceSheet, asOf: asOf, currency: currency), cancellationToken);

    public Task<ProfitAndLossDto> GetProfitAndLossAsync(
        DateOnly? from = null, DateOnly? to = null, string? currency = null, CancellationToken cancellationToken = default)
        => ReadAsync<ProfitAndLossDto>(Build(ReportKind.ProfitAndLoss, from: from, to: to, currency: currency), cancellationToken);

    public Task<StockSummaryDto> GetStockSummaryAsync(
        DateOnly? asOf = null, Guid? locationId = null, string? currency = null, CancellationToken cancellationToken = default)
        => ReadAsync<StockSummaryDto>(Build(ReportKind.StockSummary, asOf: asOf, locationId: locationId, currency: currency), cancellationToken);

    public Task<CashSummaryDto> GetCashSummaryAsync(
        DateOnly? from = null, DateOnly? to = null, string? currency = null, CancellationToken cancellationToken = default)
        => ReadAsync<CashSummaryDto>(Build(ReportKind.CashSummary, from: from, to: to, currency: currency), cancellationToken);

    public Task<SalesSummaryDto> GetSalesSummaryAsync(
        DateOnly? from = null, DateOnly? to = null, string? currency = null, CancellationToken cancellationToken = default)
        => ReadAsync<SalesSummaryDto>(Build(ReportKind.SalesSummary, from: from, to: to, currency: currency), cancellationToken);

    public async Task<ReportExport> ExportAsync(ReportRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var path = Route(request, includeFormat: true);
        using var response = await _http.GetAsync(path, cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        return new ReportExport(FileName(response, request.Kind), bytes);
    }

    // ------------------------------------------------------------------ internals

    private static string Build(
        ReportKind kind, DateOnly? asOf = null, DateOnly? from = null, DateOnly? to = null, Guid? locationId = null, string? currency = null)
        => Route(new ReportRequest(kind, asOf, from, to, locationId, currency), includeFormat: false);

    /// <summary>
    /// The one place a report route is assembled. Names, not identifiers, decide which statement is asked for, and an
    /// optional parameter is only ever sent when it has a value, so a caller cannot reach a route the server does not
    /// publish by handing this type a string.
    /// </summary>
    private static string Route(ReportRequest request, bool includeFormat)
    {
        var segments = request.Kind switch
        {
            ReportKind.ReceivableAging => "ar-aging",
            ReportKind.PayableAging => "ap-aging",
            ReportKind.TaxSummary => "tax-summary",
            ReportKind.BalanceSheet => "balance-sheet",
            ReportKind.ProfitAndLoss => "profit-and-loss",
            ReportKind.StockSummary => "stock-summary",
            ReportKind.CashSummary => "cash-summary",
            ReportKind.SalesSummary => "sales-summary",
            ReportKind.ZReport when request.SessionId is { } session && session != Guid.Empty
                => $"z-report/{session:D}",
            ReportKind.ZReport => throw new ArgumentException(
                "A Z report names the register session it closes, chosen by name on the screen - never a typed identifier.",
                nameof(request)),
            _ => throw new ArgumentException($"Unknown report kind '{request.Kind}'.", nameof(request)),
        };

        var query = new List<string>(5);
        if (includeFormat)
        {
            query.Add("format=csv");
        }

        if (request.AsOf is { } asOf)
        {
            query.Add($"asOf={asOf.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}");
        }

        if (request.From is { } from)
        {
            query.Add($"from={from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}");
        }

        if (request.To is { } to)
        {
            query.Add($"to={to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}");
        }

        if (request.LocationId is { } location)
        {
            query.Add($"locationId={location:D}");
        }

        if (!string.IsNullOrWhiteSpace(request.Currency))
        {
            query.Add($"currency={Uri.EscapeDataString(request.Currency.Trim())}");
        }

        var path = $"{RoutePrefix}/{segments}";
        return query.Count == 0 ? path : $"{path}?{string.Join('&', query)}";
    }

    /// <summary>
    /// The file name the server offered. It is read from the header rather than guessed, so a saved export carries the
    /// name of the statement it holds - and falls back to that same name when a host strips the header.
    /// </summary>
    private static string FileName(HttpResponseMessage response, ReportKind kind)
    {
        if (response.Headers.TryGetValues("Content-Disposition", out var values))
        {
            foreach (var value in values)
            {
                const string Marker = "filename=";
                var start = value.IndexOf(Marker, StringComparison.OrdinalIgnoreCase);
                if (start < 0)
                {
                    continue;
                }

                var name = value[(start + Marker.Length)..].Trim().Trim('"');
                var end = name.IndexOf('"', StringComparison.Ordinal);
                if (end >= 0)
                {
                    name = name[..end];
                }

                if (!string.IsNullOrWhiteSpace(name))
                {
                    return name;
                }
            }
        }

        return DefaultFileName(kind);
    }

    /// <summary>The name the server would have used, so a saved file still says which statement it holds.</summary>
    public static string DefaultFileName(ReportKind kind) => kind switch
    {
        ReportKind.ReceivableAging => "ar-aging.csv",
        ReportKind.PayableAging => "ap-aging.csv",
        ReportKind.TaxSummary => "tax-summary.csv",
        ReportKind.BalanceSheet => "balance-sheet.csv",
        ReportKind.ProfitAndLoss => "profit-and-loss.csv",
        ReportKind.StockSummary => "stock-summary.csv",
        ReportKind.CashSummary => "cash-summary.csv",
        ReportKind.SalesSummary => "sales-summary.csv",
        ReportKind.ZReport => "z-report.csv",
        _ => "report.csv",
    };

    private async Task<T> ReadAsync<T>(string path, CancellationToken cancellationToken)
        where T : class
    {
        using var response = await _http.GetAsync(path, cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
        return await ApiHttp.ReadRequiredAsync<T>(response, cancellationToken).ConfigureAwait(false);
    }
}
