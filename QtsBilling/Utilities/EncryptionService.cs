using System.Security.Cryptography;
using static System.Text.Encoding;
using Convert = System.Convert;

namespace QtsBilling.Utilities;

public static class EncryptionService
{
    // generate a custom method to encrypt any string (not AES method) come up with new way to encrypt any string
    public static string EncryptThis(this string textToEncrypt)
    {
        // convert the text to a byte array
        byte[] plainTextBytes = UTF8.GetBytes(textToEncrypt);
        // encrypt the data
        using Aes aes = Aes.Create();
        aes.Key = "Encryption Key"u8.ToArray();
        aes.IV = "IV 4"u8.ToArray();
        using MemoryStream ms = new();
        using CryptoStream cs = new(ms, aes.CreateEncryptor(), CryptoStreamMode.Write);
        cs.Write(plainTextBytes, 0, plainTextBytes.Length);
        cs.Close();
        return Convert.ToBase64String(ms.ToArray());
    }
     
    
    
    // generate a AES decryption method to decrypt any string
    public static string DecryptThis(this string textToDecrypt)
    {
         // convert the text to a byte array
        byte[] plainTextBytes = Convert.FromBase64String(textToDecrypt);
        // decrypt the data
        using Aes aes = Aes.Create();
        aes.Key = "FTSZZyi6yDKj/m6ud3mPVL7zoxvygq+/QN1u7YBrrJQ="u8.ToArray();
        aes.IV = "/XnYLjJ3pzDRIjiPqDtLAA=="u8.ToArray();
        using MemoryStream ms = new();
        using CryptoStream cs = new(ms, aes.CreateDecryptor(), CryptoStreamMode.Write);
        cs.Write(plainTextBytes, 0, plainTextBytes.Length);
        cs.Close();
        return UTF8.GetString(ms.ToArray());
    }
    
    //generate a AES Key and IV
    public static void GenerateKeyAndIV()
    {
        using Aes aes = Aes.Create();
        aes.GenerateKey();
        aes.GenerateIV();
        Console.WriteLine(@"Key: " + Convert.ToBase64String(aes.Key));
        Console.WriteLine(@"IV: " + Convert.ToBase64String(aes.IV));
        
        // copy the key and IV to clipboard
        Clipboard.SetText(Convert.ToBase64String(aes.Key));
        Clipboard.SetText(Convert.ToBase64String(aes.IV));
    }
}