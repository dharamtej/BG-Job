namespace CareerPanda.Framework.Configuration;

public class Config
{
    public DBConfig DBConnfig { get; set; } = new();

    public FileConfig FileConfig { get; set; } = new();

    public TimeZoneConfig TimeZoneConfig { get; set; } = new();

    public DeploymentURLsConfig DeploymentURLsConfig { get; set; } = new();

    public MailSettingsConfig MailSettingsConfig { get; set; } = new();

    public Crypto Crypto { get; set; } = new();

    public SessionConfig SessionConfig { get; set; } = new();

    public CareerPandaSettingsConfig CareerPandaSettingsConfig { get; set; } = new();

    public AWSSettingsConfig AWSSettingsConfig { get; set; } = new();

    public AzureSettingsConfig AzureSettingsConfig { get; set; } = new();

    public UploadSourceConfig UploadSourceConfig { get; set; } = new();

    public RedisConfig RedisConfig { get; set; } = new();

    public GoogleAuthConfig GoogleAuthConfig { get; set; } = new();

    public BackgroundJobsConfig BackgroundJobsConfig { get; set; } = new();

    public AuthConfig AuthConfig { get; set; } = new();

    public LoggingDatabaseConfig LoggingDatabaseConfig { get; set; } = new();
}
