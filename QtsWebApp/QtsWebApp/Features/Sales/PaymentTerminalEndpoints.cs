using System.Globalization;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Identity;
using QtsBilling.Contracts.Sales;
using QtsBilling.Persistence.Postgres;
using QtsWebApp.Features.Accounting;
using QtsWebApp.Infrastructure;

namespace QtsWebApp.Features.Sales;

/// <summary>Server-side creation of provider intents; capture verification occurs during payment posting.</summary>
public static class PaymentTerminalEndpoints
{
    private const string IdempotencyHeader = "Idempotency-Key";

    public static IEndpointRouteBuilder MapPaymentTerminalEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/pos/card-intents", CreateIntentAsync)
            .WithTags("POS")
            .RequireAuthorization(Permissions.SalesPost);
        return app;
    }

    private static async Task<IResult> CreateIntentAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        IPaymentTerminalProvider provider,
        HttpContext http,
        CreateTerminalPaymentIntentRequest request,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId)
            || !Guid.TryParse(principal.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var userId))
            return BusinessScope.MissingMembership();

        var idempotencyKey = http.Request.Headers[IdempotencyHeader].ToString().Trim();
        if (idempotencyKey.Length is 0 or > 64)
            return ApiResults.ValidationProblem("An idempotency key is required.",
                new Dictionary<string, string[]> { [IdempotencyHeader] = ["Send a client-generated key of 1-64 characters."] });

        if (request.LocationId == Guid.Empty || request.Amount <= 0 || LedgerWriter.Round(request.Amount) != request.Amount
            || string.IsNullOrWhiteSpace(request.Currency) || request.Currency.Trim().Length != 3)
            return ApiResults.ValidationProblem("The terminal-intent payload is invalid.",
                new Dictionary<string, string[]> { ["request"] = ["Provide a location, positive amount with at most 4 decimal places, and three-letter currency."] });

        if (!await BusinessScope.CanAccessLocationAsync(db, userId, businessId, request.LocationId, cancellationToken).ConfigureAwait(false))
            return ApiResults.Problem(StatusCodes.Status403Forbidden, ApiErrorCodes.Forbidden, "The membership cannot access this location.");

        var businessCurrency = await db.Businesses.AsNoTracking()
            .Where(row => row.Id == businessId)
            .Select(row => row.BaseCurrency)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (businessCurrency is null) return BusinessScope.MissingMembership();
        if (!string.Equals(businessCurrency, request.Currency.Trim(), StringComparison.OrdinalIgnoreCase))
            return ApiResults.ValidationProblem("The terminal-intent currency is invalid.",
                new Dictionary<string, string[]> { ["currency"] = [$"Use the business currency {businessCurrency}."] });

        try
        {
            var intent = await provider.CreateIntentAsync(
                businessId,
                request.LocationId,
                LedgerWriter.Round(request.Amount),
                businessCurrency,
                idempotencyKey,
                cancellationToken).ConfigureAwait(false);

            if (intent is null || string.IsNullOrWhiteSpace(intent.PspReference) || intent.PspReference.Length > 128
                || intent.LocationId != request.LocationId || LedgerWriter.Round(intent.Amount) != LedgerWriter.Round(request.Amount)
                || !string.Equals(intent.Currency, businessCurrency, StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(intent.Status) || intent.Status.Length > 64
                || intent.NextActionUrl is { } nextActionUrl
                    && (!Uri.TryCreate(nextActionUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps))
                return ApiResults.Problem(StatusCodes.Status502BadGateway, ApiErrorCodes.PaymentUnconfirmed,
                    "The payment terminal returned an invalid intent.");

            return Results.Ok(intent with { Amount = LedgerWriter.Round(intent.Amount), Currency = businessCurrency });
        }
        catch (PaymentTerminalUnavailableException)
        {
            return PaymentDisabled();
        }
    }

    public static async Task<TerminalCaptureValidation> VerifyCaptureAsync(
        IPaymentTerminalProvider provider,
        Guid businessId,
        Guid locationId,
        decimal expectedAmount,
        string currency,
        string? pspReference,
        TerminalCaptureEnvelope? capture,
        CancellationToken cancellationToken)
    {
        var reference = pspReference?.Trim();
        if (string.IsNullOrWhiteSpace(reference) || reference.Length > 128
            || capture is null || string.IsNullOrWhiteSpace(capture.SignatureHeader)
            || capture.SignatureHeader.Length > 2048 || string.IsNullOrWhiteSpace(capture.RawPayload)
            || System.Text.Encoding.UTF8.GetByteCount(capture.RawPayload) > 64 * 1024)
            return new TerminalCaptureValidation(null, CaptureUnconfirmed());

        TerminalCaptureVerification verification;
        try
        {
            verification = await provider.VerifyCaptureAsync(
                businessId,
                reference,
                capture.SignatureHeader,
                capture.RawPayload,
                cancellationToken).ConfigureAwait(false);
        }
        catch (PaymentTerminalUnavailableException)
        {
            return new TerminalCaptureValidation(null, PaymentDisabled());
        }

        var brand = verification.CardBrand?.Trim();
        var lastFour = verification.CardLast4;
        if (!verification.Verified
            || !string.Equals(verification.PspReference, reference, StringComparison.Ordinal)
            || verification.LocationId != locationId
            || LedgerWriter.Round(verification.Amount) != LedgerWriter.Round(expectedAmount)
            || !string.Equals(verification.Currency, currency, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(brand) || brand.Length > 32
            || lastFour is not { Length: 4 } || lastFour.Any(character => !char.IsAsciiDigit(character)))
            return new TerminalCaptureValidation(null, CaptureUnconfirmed());

        return new TerminalCaptureValidation(
            verification with { PspReference = reference, Currency = currency, CardBrand = brand, CardLast4 = lastFour },
            null);
    }

    public static IResult PaymentDisabled()
        => ApiResults.Problem(StatusCodes.Status409Conflict, ApiErrorCodes.PaymentMethodDisabled,
            "Card payments are disabled.", "No verified PSP or card-terminal provider is configured.");

    private static IResult CaptureUnconfirmed()
        => ApiResults.Problem(StatusCodes.Status409Conflict, ApiErrorCodes.PaymentUnconfirmed,
            "The card payment is not confirmed.", "Provide a signed provider capture matching this business, location, amount and currency.");

    public sealed record TerminalCaptureValidation(TerminalCaptureVerification? Verification, IResult? Error);
}
