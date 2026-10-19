namespace QtsWebApp.Infrastructure;

/// <summary>
/// Sensitive-data redaction (QB-14): values written to logs or audit summaries are masked so a log file,
/// a backup or an audit export can never leak a credential, a contact detail in full or a token. Masking
/// keeps enough shape for correlation ("o***@example.test") without being reversible.
/// </summary>
public static class Redaction
{
    public const string Masked = "[redacted]";

    /// <summary>Keeps the first character of the local part and the domain: <c>o***@example.test</c>.</summary>
    public static string Email(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return Masked;
        }

        var trimmed = email.Trim();
        var at = trimmed.LastIndexOf('@');

        if (at <= 0 || at == trimmed.Length - 1)
        {
            return trimmed.Length <= 2 ? Masked : $"{trimmed[0]}***";
        }

        return $"{trimmed[0]}***{trimmed[at..]}";
    }

    /// <summary>Keeps only the last four digits: <c>***1234</c>.</summary>
    public static string Phone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
        {
            return Masked;
        }

        var digits = new string(phone.Where(char.IsDigit).ToArray());

        return digits.Length <= 4 ? Masked : $"***{digits[^4..]}";
    }

    /// <summary>Any opaque token/key: the value itself is never kept.</summary>
    public static string Secret(string? value)
        => string.IsNullOrEmpty(value) ? Masked : Masked;
}
