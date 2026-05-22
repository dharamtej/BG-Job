namespace CareerPanda.Framework.Configuration;

public class Crypto
{
    public string Key { get; set; } = string.Empty;

    public string IV { get; set; } = string.Empty;

    public string HashAlgorithem { get; set; } = "HMACSHA512";

    public string EncryptionAlgorithem { get; set; } = "AES";
}
