namespace QtsBilling.Ui.Smoke;

/// <summary>
/// Positive dependency policy for the shared Razor class library (specification task 02). The RCL may
/// consume the platform, MudBlazor and the provider-neutral contracts/value models; anything else is
/// outside the approved graph. This is an allow-list, not a list of known bad assembly names.
/// </summary>
public static class SharedUiDependencyPolicy
{
    private static readonly string[] AllowedPrefixes =
    [
        "System",
        "Microsoft",
        "netstandard",
        "MudBlazor",
        "QtsBilling.Contracts",
        "QtsModels",
    ];

    public static bool IsAllowed(string assemblyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyName);

        return AllowedPrefixes.Any(prefix =>
            assemblyName.Equals(prefix, StringComparison.OrdinalIgnoreCase)
            || assemblyName.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase));
    }

    public static string[] Disallowed(IEnumerable<string> assemblyNames)
    {
        ArgumentNullException.ThrowIfNull(assemblyNames);

        return assemblyNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Where(name => !IsAllowed(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
