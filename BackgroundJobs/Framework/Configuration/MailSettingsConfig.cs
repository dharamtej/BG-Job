namespace CareerPanda.Framework.Configuration;

public class MailSettingsConfig
{
    public string EMailProvider { get; set; } = "mailkit";

    public string EMailId { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 587;

    public bool EnableSSL { get; set; }
}
