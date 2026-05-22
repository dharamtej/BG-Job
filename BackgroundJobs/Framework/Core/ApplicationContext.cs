using CareerPanda.Framework.Configuration;
using Microsoft.Extensions.Configuration;

namespace CareerPanda.Framework;

public class ApplicationContext
{
    private static Config? _config;

    public static Config Config
    {
        get
        {
            if (_config == null)
                throw new InvalidOperationException("ApplicationContext not initialized. Call Initialize() at startup.");
            return _config;
        }
    }

    public static string UserId { get; set; } = string.Empty;

    public static string CorrelationId { get; set; } = string.Empty;

    public static void Initialize(IConfiguration configuration)
    {
        _config = BindConfig(configuration);
    }

    public static Config BindConfig(IConfiguration configuration) => new()
    {
        DBConnfig = configuration.GetSection("Connection").Get<DBConfig>() ?? new DBConfig(),
        FileConfig = configuration.GetSection("FileConfig").Get<FileConfig>() ?? new FileConfig(),
        SessionConfig = configuration.GetSection("Session").Get<SessionConfig>() ?? new SessionConfig(),
        Crypto = configuration.GetSection("Crypto").Get<Crypto>() ?? new Crypto(),
        TimeZoneConfig = configuration.GetSection("TimeZone").Get<TimeZoneConfig>() ?? new TimeZoneConfig(),
        MailSettingsConfig = configuration.GetSection("MailSettings").Get<MailSettingsConfig>() ?? new MailSettingsConfig(),
        DeploymentURLsConfig = configuration.GetSection("DeploymentURLs").Get<DeploymentURLsConfig>() ?? new DeploymentURLsConfig(),
        AWSSettingsConfig = configuration.GetSection("AWSSettings").Get<AWSSettingsConfig>() ?? new AWSSettingsConfig(),
        CareerPandaSettingsConfig = configuration.GetSection("CareerPandaSettings").Get<CareerPandaSettingsConfig>() ?? new CareerPandaSettingsConfig(),
        AzureSettingsConfig = configuration.GetSection("AzureSettings").Get<AzureSettingsConfig>() ?? new AzureSettingsConfig(),
        UploadSourceConfig = configuration.GetSection("UploadSource").Get<UploadSourceConfig>() ?? new UploadSourceConfig(),
        RedisConfig = configuration.GetSection("Redis").Get<RedisConfig>() ?? new RedisConfig(),
        GoogleAuthConfig = configuration.GetSection("GoogleAuth").Get<GoogleAuthConfig>() ?? new GoogleAuthConfig(),
        BackgroundJobsConfig = configuration.GetSection("BackgroundJobs").Get<BackgroundJobsConfig>() ?? new BackgroundJobsConfig(),
        AuthConfig = configuration.GetSection("Auth").Get<AuthConfig>() ?? new AuthConfig(),
        LoggingDatabaseConfig = configuration.GetSection("Logging:Database").Get<LoggingDatabaseConfig>() ?? new LoggingDatabaseConfig()
    };
}
