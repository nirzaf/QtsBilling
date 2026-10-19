using System.Text;

namespace QtsBilling.Persistence.Postgres;

/// <summary>
/// Deterministic snake_case naming for tables and columns (specification §3.1 naming conventions).
/// Applied after all explicit configuration so provider-generated names (keys, indexes, foreign keys)
/// are derived from the final snake_case names.
/// </summary>
public static class SnakeCaseNaming
{
    /// <summary>Converts a PascalCase/camelCase identifier to snake_case (e.g. <c>PhoneNormalized</c> → <c>phone_normalized</c>).</summary>
    public static string ToSnakeCase(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return name;
        }

        var builder = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var current = name[i];
            if (char.IsUpper(current))
            {
                var previous = i > 0 ? name[i - 1] : '\0';
                var next = i + 1 < name.Length ? name[i + 1] : '\0';

                var boundary = i > 0
                    && previous != '_'
                    && (!char.IsUpper(previous)
                        || (char.IsUpper(previous) && next != '\0' && char.IsLower(next)));

                if (boundary)
                {
                    builder.Append('_');
                }

                builder.Append(char.ToLowerInvariant(current));
            }
            else
            {
                builder.Append(current);
            }
        }

        return builder.ToString();
    }
}
