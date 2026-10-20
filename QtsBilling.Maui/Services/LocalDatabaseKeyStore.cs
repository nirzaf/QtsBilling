using System.Security.Cryptography;

namespace QtsBilling.Maui.Services;

/// <summary>Stores the SQLCipher key as a Windows DPAPI blob tied to the current Windows user.</summary>
internal static class LocalDatabaseKeyStore
{
    public static string LoadOrCreateHexKey()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The device-local database key store requires Windows DPAPI.");
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new InvalidOperationException("The Windows local application data directory is unavailable.");
        }

        var directory = Path.Combine(localAppData, "QtsBilling");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "local.key");

        if (File.Exists(path))
        {
            return ReadHexKey(path);
        }

        var key = RandomNumberGenerator.GetBytes(32);
        try
        {
            var protectedKey = ProtectedData.Protect(key, optionalEntropy: null, DataProtectionScope.CurrentUser);
            var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    stream.Write(protectedKey);
                    stream.Flush(flushToDisk: true);
                }

                try
                {
                    File.Move(tempPath, path);
                }
                catch (IOException) when (File.Exists(path))
                {
                    // Another app instance won key creation. Its DPAPI blob is authoritative.
                    return ReadHexKey(path);
                }

                return Convert.ToHexString(key);
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
                CryptographicOperations.ZeroMemory(protectedKey);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static string ReadHexKey(string path)
    {
        var protectedKey = File.ReadAllBytes(path);
        byte[]? key = null;
        try
        {
            key = ProtectedData.Unprotect(protectedKey, optionalEntropy: null, DataProtectionScope.CurrentUser);
            if (key.Length != 32)
            {
                throw new CryptographicException("The protected local database key has an invalid length.");
            }

            return Convert.ToHexString(key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedKey);
            if (key is not null)
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }
    }
}
