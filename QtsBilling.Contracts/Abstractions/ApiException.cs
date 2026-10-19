namespace QtsBilling.Contracts.Abstractions;

/// <summary>
/// Structured API failure carrying the stable <see cref="Code"/> from
/// <see cref="ApiErrorCodes"/> so callers can act on the cause instead of parsing text.
/// </summary>
public sealed class ApiException : Exception
{
    public ApiException(int statusCode, string code, string message, IReadOnlyDictionary<string, string[]>? errors = null)
        : base(message)
    {
        StatusCode = statusCode;
        Code = code;
        Errors = errors ?? new Dictionary<string, string[]>();
    }

    public int StatusCode { get; }

    public string Code { get; }

    public IReadOnlyDictionary<string, string[]> Errors { get; }

    public bool IsNotFound => StatusCode == 404;

    public bool IsConflict => StatusCode == 409;

    public bool IsForbidden => StatusCode == 403;

    public bool IsUnauthenticated => StatusCode == 401;
}
