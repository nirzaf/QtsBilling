namespace QtsModels;

/// <summary>
/// Provides access to MongoDB connection strings sourced from the environment.
/// Secrets are never stored in source control — configure them through
/// environment variables (or your secret manager of choice).
/// See the "Configuration" section of the README for details.
/// </summary>
public static class GlobalConstants
{
    /// <summary>Environment variable holding the primary/remote MongoDB connection string.</summary>
    public const string RemoteConnectionStringVariable = "QTS_MONGODB_REMOTE";

    /// <summary>Environment variable holding the optional local MongoDB connection string.</summary>
    public const string LocalConnectionStringVariable = "QTS_MONGODB_LOCAL";

    private const string DefaultLocalConnectionString = "mongodb://localhost:27017/qts-Billing";

    /// <summary>
    /// Returns the primary (remote/cloud) MongoDB connection string read from the
    /// <c>QTS_MONGODB_REMOTE</c> environment variable. For local-only development you may
    /// point this variable at a local MongoDB instance.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the environment variable is not configured.
    /// </exception>
    public static string GetRemoteConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable(RemoteConnectionStringVariable);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"The '{RemoteConnectionStringVariable}' environment variable is not set. " +
                "Configure it with your MongoDB connection string before running the application " +
                "(see the 'Configuration' section of the README).");
        }

        return connectionString;
    }

    /// <summary>
    /// Returns the local MongoDB connection string read from the <c>QTS_MONGODB_LOCAL</c>
    /// environment variable, falling back to a localhost default when it is not set.
    /// </summary>
    public static string GetLocalConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable(LocalConnectionStringVariable);

        return string.IsNullOrWhiteSpace(connectionString)
            ? DefaultLocalConnectionString
            : connectionString;
    }
}
