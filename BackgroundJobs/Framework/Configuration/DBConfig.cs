namespace CareerPanda.Framework.Configuration;

public class DBConfig
{
    public string Connection { get; set; } = string.Empty;

    public string Database { get; set; } = string.Empty;

    /// <summary>MongoDB connection string (reserved for future use).</summary>
    public string? MongoConnection { get; set; }

    public string? MongoDatabase { get; set; }
}
