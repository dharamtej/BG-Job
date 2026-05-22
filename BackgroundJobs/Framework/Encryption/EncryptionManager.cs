using System.Security.Cryptography;
using System.Text;
using CareerPanda.Framework.Configuration;

namespace CareerPanda.Framework.Encryption;

public static class EncryptionManager
{
    public static string Encrypt(string plainText, Crypto crypto)
    {
        using var aes = Aes.Create();
        aes.Key = DeriveKey(crypto.Key, 32);
        aes.IV = DeriveKey(crypto.IV + crypto.Key, 16);
        using var encryptor = aes.CreateEncryptor();
        var plain = Encoding.UTF8.GetBytes(plainText);
        var cipher = encryptor.TransformFinalBlock(plain, 0, plain.Length);
        return Convert.ToBase64String(cipher);
    }

    public static string Decrypt(string cipherText, Crypto crypto)
    {
        using var aes = Aes.Create();
        aes.Key = DeriveKey(crypto.Key, 32);
        aes.IV = DeriveKey(crypto.IV + crypto.Key, 16);
        using var decryptor = aes.CreateDecryptor();
        var cipher = Convert.FromBase64String(cipherText);
        var plain = decryptor.TransformFinalBlock(cipher, 0, cipher.Length);
        return Encoding.UTF8.GetString(plain);
    }

    public static string CreateHash(string data, string key) =>
        Convert.ToBase64String(HMACSHA512.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(data)));

    public static bool CompareHash(string data, string hash, string key) =>
        CreateHash(data, key) == hash;

    private static byte[] DeriveKey(string input, int size)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        if (hash.Length == size)
            return hash;
        return hash.Take(size).ToArray();
    }
}
