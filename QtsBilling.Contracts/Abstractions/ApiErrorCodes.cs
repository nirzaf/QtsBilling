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
    public const string PaymentMethodDisabled = "PAYMENT_METHOD_DISABLED";
    public const string InvalidPrice = "INVALID_PRICE";
    public const string CashRegisterNotOpen = "CASH_REGISTER_NOT_OPEN";
    public const string AlreadyRefunded = "ALREADY_REFUNDED";
    public const string RefundNotSupported = "REFUND_NOT_SUPPORTED";
    public const string AlreadyReversed = "ALREADY_REVERSED";
    public const string CreditLimitExceeded = "CREDIT_LIMIT_EXCEEDED";
    public const string PurchaseOrderNotDraft = "PURCHASE_ORDER_NOT_DRAFT";
    public const string PurchaseOrderNotOpen = "PURCHASE_ORDER_NOT_OPEN";
    public const string OverReceipt = "OVER_RECEIPT";
    public const string PermissionDenied = "PERMISSION_DENIED";
    public const string NeedsReview = "NEEDS_REVIEW";
    public const string AccountingPeriodClosed = "ACCOUNTING_PERIOD_CLOSED";

    public const string AccountingPeriodMissing = "ACCOUNTING_PERIOD_MISSING";

    public const string AccountingPeriodOverlap = "ACCOUNTING_PERIOD_OVERLAP";
    public const string UnbalancedJournal = "UNBALANCED_JOURNAL";
    public const string DuplicatePhone = "DUPLICATE_PHONE";
    public const string DuplicateSku = "DUPLICATE_SKU";
    public const string DuplicateName = "DUPLICATE_NAME";
    public const string DuplicateInvoiceNumber = "DUPLICATE_INVOICE_NUMBER";
    public const string InvoiceNotDraft = "INVOICE_NOT_DRAFT";
    public const string InvoiceNotIssued = "INVOICE_NOT_ISSUED";
    public const string InvoiceHasPayments = "INVOICE_HAS_PAYMENTS";
    public const string InvoiceAlreadyCredited = "INVOICE_ALREADY_CREDITED";
    public const string OptimisticConcurrencyFailure = "CONCURRENCY_CONFLICT";
    public const string StoreUnavailable = "STORE_UNAVAILABLE";

    /// <summary>The stock carries no value, so its cost cannot be determined (specification D06).</summary>
    public const string ValuationRequired = "VALUATION_REQUIRED";
}
