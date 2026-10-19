namespace QtsBilling.Contracts.Abstractions;

/// <summary>
/// Stable, machine-readable error codes carried in ProblemDetails <c>code</c> members
/// (specification §7.1). Clients must branch on these codes, never on localized text.
/// </summary>
public static class ApiErrorCodes
{
    // Generic request handling
    public const string ValidationFailed = "VALIDATION_FAILED";
    public const string PasswordResetRequired = "PASSWORD_RESET_REQUIRED";
    public const string InvalidCredentials = "INVALID_CREDENTIALS";
    public const string Unauthenticated = "UNAUTHENTICATED";
    public const string Forbidden = "FORBIDDEN";
    public const string NotFound = "NOT_FOUND";
    public const string Conflict = "CONFLICT";
    public const string RateLimited = "RATE_LIMITED";
    public const string InternalError = "INTERNAL_ERROR";

    // Idempotency / sync
    public const string IdempotencyKeyReused = "IDEMPOTENCY_KEY_REUSED";
    public const string SameKeyDifferentPayload = "SAME_KEY_DIFFERENT_PAYLOAD";
    public const string UnsupportedPayloadVersion = "UNSUPPORTED_PAYLOAD_VERSION";
    public const string DeviceRevoked = "DEVICE_REVOKED";
    public const string DeviceNotEnrolled = "DEVICE_NOT_ENROLLED";
    public const string ResyncRequired = "RESYNC_REQUIRED";
    public const string TransientRetry = "TRANSIENT_RETRY";

    // Business outcomes
    public const string InsufficientStock = "INSUFFICIENT_STOCK";
    public const string PaymentUnconfirmed = "PAYMENT_UNCONFIRMED";
    public const string InvalidPrice = "INVALID_PRICE";
    public const string PermissionDenied = "PERMISSION_DENIED";
    public const string NeedsReview = "NEEDS_REVIEW";
    public const string AccountingPeriodClosed = "ACCOUNTING_PERIOD_CLOSED";
    public const string UnbalancedJournal = "UNBALANCED_JOURNAL";
    public const string DuplicatePhone = "DUPLICATE_PHONE";
    public const string DuplicateSku = "DUPLICATE_SKU";
    public const string DuplicateInvoiceNumber = "DUPLICATE_INVOICE_NUMBER";
    public const string OptimisticConcurrencyFailure = "CONCURRENCY_CONFLICT";
    public const string StoreUnavailable = "STORE_UNAVAILABLE";
}
