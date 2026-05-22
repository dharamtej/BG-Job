namespace CareerPanda.Framework.Configuration;

public class CareerPandaSettingsConfig
{
    /// <summary>PostgreSQL or MongoDB (MongoDB reserved for future).</summary>
    public string DBProvider { get; set; } = "PostgreSQL";

    /// <summary>Internal or External.</summary>
    public string AuthenticationType { get; set; } = "Internal";

    public string ExternalAuthUrl { get; set; } = string.Empty;

    public string AppCode { get; set; } = "CP";
}
