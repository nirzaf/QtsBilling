using System.Security.Cryptography;
using static System.Text.Encoding;
using Convert = System.Convert;

namespace QtsServices.UtilityServices;

/// <summary>
/// AES helpers for encrypting and decrypting sensitive strings.
/// The key and initialization vector (IV) are read from environment variables so that
/// no secret material is ever committed to source control. Generate a Base64 key/IV pair
/// with <see cref="GenerateKeyAndIV"/> and store the values in the
/// <c>QTS_ENCRYPTION_KEY</c> and <c>QTS_ENCRYPTION_IV</c> environment variables.
/// See the "Configuration" section of the README for details.
/// </summary>
public static class EncryptionService
{
    /// <summary>Environment variable holding the Base64-encoded AES key.</summary>
    public const string KeyVariable = "QTS_ENCRYPTION_KEY";

    /// <summary>Environment variable holding the Base64-encoded AES initialization vector.</summary>
    public const string IvVariable = "QTS_ENCRYPTION_IV";

    /// <summary>Encrypts the supplied text using the configured AES key and IV.</summary>
    public static string EncryptThis(this string textToEncrypt)
    {
        byte[] plainTextBytes = UTF8.GetBytes(textToEncrypt);
        using Aes aes = Aes.Create();
        aes.Key = GetKey();
        aes.IV = GetIv();
        using MemoryStream ms = new();
        using CryptoStream cs = new(ms, aes.CreateEncryptor(), CryptoStreamMode.Write);
        cs.Write(plainTextBytes, 0, plainTextBytes.Length);
        cs.Close();
        return Convert.ToBase64String(ms.ToArray());
    }

    /// <summary>Decrypts the supplied Base64 cipher text using the configured AES key and IV.</summary>
    public static string DecryptThis(this string textToDecrypt)
    {
        byte[] cipherTextBytes = Convert.FromBase64String(textToDecrypt);
        using Aes aes = Aes.Create();
        aes.Key = GetKey();
        aes.IV = GetIv();
        using MemoryStream ms = new();
        using CryptoStream cs = new(ms, aes.CreateDecryptor(), CryptoStreamMode.Write);
        cs.Write(cipherTextBytes, 0, cipherTextBytes.Length);
        cs.Close();
        return UTF8.GetString(ms.ToArray());
    }

    /// <summary>
    /// Prints a freshly generated Base64 AES key and IV, ready to be copied into your
    /// <c>QTS_ENCRYPTION_KEY</c> and <c>QTS_ENCRYPTION_IV</c> environment variables.
    /// </summary>
    public static void GenerateKeyAndIV()
    {
        using Aes aes = Aes.Create();
        aes.GenerateKey();
        aes.GenerateIV();
        Console.WriteLine($"{KeyVariable}={Convert.ToBase64String(aes.Key)}");
        Console.WriteLine($"{IvVariable}={Convert.ToBase64String(aes.IV)}");
    }

    private static byte[] GetKey() => Convert.FromBase64String(RequireVariable(KeyVariable));

    private static byte[] GetIv() => Convert.FromBase64String(RequireVariable(IvVariable));

    private static string RequireVariable(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"The '{name}' environment variable is not set. Generate a value with " +
                "EncryptionService.GenerateKeyAndIV() and configure it " +
                "(see the 'Configuration' section of the README).");
        }

        return value;
    }
}
