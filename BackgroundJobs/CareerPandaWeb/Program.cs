using System.Text;
using System.Threading.RateLimiting;
using CareerPanda.BL.Background;
using CareerPanda.BL.Background.Handlers;
using Hangfire;
using Hangfire.Dashboard;
using Hangfire.PostgreSql;
using CareerPanda.BL.Logic;
using CareerPanda.BL.Services;
using CareerPanda.DataAccess.DA;
using CareerPanda.DataAccess.PostgreSQL;
using CareerPanda.Framework;
using CareerPanda.Framework.Cache;
using CareerPanda.Framework.Configuration;
using CareerPanda.Framework.Logger;
using CareerPanda.Framework.Mail;
using CareerPanda.Framework.Security;
using CareerPanda.Framework.Storage;
using CareerPanda.Framework.Util;
using CareerPanda.Web.Configuration;
using CareerPanda.Web.Middleware;
using CareerPanda.Web.Security;
using Npgsql;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

var configuration = builder.Configuration;
// Convert postgresql:// URI → Npgsql key=value format.
// Railway provides a URI; Npgsql 6.x (pulled in by Hangfire.PostgreSql) rejects URI format.
var postgresConnection = ToNpgsqlKeyValue(ConnectionStringResolver.Resolve(configuration));

// Ensure ApplicationContext sees the resolved connection (env / user secrets override appsettings).
configuration.GetSection("Connection")["Connection"] = postgresConnection;
ApplicationContext.Initialize(configuration);
Config config = ApplicationContext.Config;

builder.Services.AddSingleton<IConfiguration>(configuration);
builder.Services.AddSingleton(config);
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient("ExternalAuth");

// Named HTTP clients for job-fetch handlers
builder.Services.AddHttpClient("JSearch",  c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddHttpClient("Adzuna", c =>
{
    c.Timeout = TimeSpan.FromSeconds(30);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
    c.DefaultRequestHeaders.Add("Accept", "application/json");
});
builder.Services.AddHttpClient("TheMuse",  c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddHttpClient("USAJobs",  c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddHttpClient("Wikipedia", c =>
{
    c.Timeout = TimeSpan.FromSeconds(10);
    // Wikipedia API requires a descriptive User-Agent identifying the application
    c.DefaultRequestHeaders.UserAgent.ParseAdd("CareerPanda/1.0 (careerpanda.com)");
});
builder.Services.AddHttpClient("RemoteOK", c =>
{
    c.Timeout = TimeSpan.FromSeconds(30);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("CareerPanda/1.0 JobAggregator");
    c.DefaultRequestHeaders.Add("Accept", "application/json");
});
builder.Services.AddHttpClient("Jobicy", c =>
{
    c.Timeout = TimeSpan.FromSeconds(30);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("CareerPanda/1.0 JobAggregator");
    c.DefaultRequestHeaders.Add("Accept", "application/json");
});
builder.Services.AddHttpClient("Arbeitnow", c =>
{
    c.Timeout = TimeSpan.FromSeconds(30);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("CareerPanda/1.0 JobAggregator");
    c.DefaultRequestHeaders.Add("Accept", "application/json");
});

builder.Services.AddSingleton<IPasswordHasher, PasswordHasher>();
builder.Services.AddSingleton<JobCancellationRegistry>();

if (config.RedisConfig.Enabled)
    builder.Services.AddSingleton<ICacheService, RedisCacheService>();
else
    builder.Services.AddSingleton<ICacheService, InMemoryCacheService>();

builder.Services.AddDbContext<CareerPandaDbContext>(options =>
    options.UseNpgsql(postgresConnection));

var databaseLoggingEnabled = config.LoggingDatabaseConfig.Enabled;
if (databaseLoggingEnabled)
{
    builder.Services.AddDbContext<ApplicationLogDbContext>(options =>
        options.UseNpgsql(postgresConnection));
}

if (config.CareerPandaSettingsConfig.DBProvider.Equals("PostgreSQL", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddScoped<IUserDA, UserDAPostgres>();
    builder.Services.AddScoped<IBackgroundTaskDA, BackgroundTaskDAPostgres>();
    builder.Services.AddScoped<IJobFetchDA, JobFetchDAPostgres>();
}

// ── Hangfire — job scheduling ─────────────────────────────────────────────
builder.Services.AddHangfire(cfg => cfg
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UsePostgreSqlStorage(o => o.UseNpgsqlConnection(postgresConnection)));
builder.Services.AddHangfireServer(o => o.WorkerCount = 2);
builder.Services.AddSingleton<JobSchedulerService>();
builder.Services.AddSingleton<SponsorCacheWarmupService>();

builder.Services.AddSingleton<IBackgroundJobQueue>(sp =>
    new BackgroundJobQueue(config.BackgroundJobsConfig.QueueCapacity));
builder.Services.AddSingleton<JobExecutionService>();
builder.Services.AddSingleton<IJobHandler, DefaultJobHandler>();

// ── Job Fetch Handlers (one per category) ─────────────────────────────────
builder.Services.AddSingleton<IJobHandler, AllJobsJobHandler>();
builder.Services.AddSingleton<IJobHandler, AdzunaJobsJobHandler>();
builder.Services.AddSingleton<IJobHandler, StartupJobsJobHandler>();
builder.Services.AddSingleton<IJobHandler, GovernmentJobsJobHandler>();
builder.Services.AddSingleton<IJobHandler, NonProfitJobsJobHandler>();
builder.Services.AddSingleton<IJobHandler, ContractJobsJobHandler>();
builder.Services.AddSingleton<IJobHandler, H1BJobsJobHandler>();
builder.Services.AddSingleton<IJobHandler, H1BSponsorEnrichmentJobHandler>();
builder.Services.AddSingleton<IJobHandler, PrimeVendorJobsJobHandler>();
builder.Services.AddSingleton<IJobHandler, RemoteOkJobsJobHandler>();
builder.Services.AddSingleton<IJobHandler, JobicyJobsJobHandler>();
builder.Services.AddSingleton<IJobHandler, GreenhouseJobsJobHandler>();
// builder.Services.AddSingleton<IJobHandler, ArbeitnowJobsJobHandler>(); // Disabled: European board, not US jobs
builder.Services.AddHostedService<BackgroundJobWorker>();

builder.Services.AddScoped<LoginBL>();
builder.Services.AddScoped<JobBL>();
builder.Services.AddScoped<JobFetchBL>();
builder.Services.AddScoped<FileBL>();
builder.Services.AddScoped<IGoogleAuthService, GoogleAuthService>();
builder.Services.AddScoped<ISessionService, SessionService>();
builder.Services.AddScoped<IRefreshTokenService, RefreshTokenService>();
builder.Services.AddScoped<IPasswordResetService, PasswordResetService>();
builder.Services.AddScoped<IFileStorageService, FileStorageService>();
builder.Services.AddScoped<IMailService, MailKitMailService>();
if (databaseLoggingEnabled)
    builder.Services.AddSingleton<ILoggerProvider, DbLoggerProvider>();

builder.Services.AddHealthChecks()
    .AddNpgSql(postgresConnection, name: "postgresql");

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("api", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.User.Identity?.Name ?? httpContext.Connection.RemoteIpAddress?.ToString() ?? "anon",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 100,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 10
            }));
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Request.Path.Value ?? "global",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 200,
                Window = TimeSpan.FromMinutes(1)
            }));
});

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        var symmetricKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(config.Crypto.Key));

        options.IncludeErrorDetails = true;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            IssuerSigningKey = symmetricKey,
            ValidAudience = UtilityManager.JwtAudience,
            ValidIssuer = UtilityManager.JwtIssuer,
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ValidateLifetime = true,
            ValidateAudience = true,
            ValidateIssuer = true,
            ValidateIssuerSigningKey = true,
            ClockSkew = TimeSpan.Zero
        };

        options.EventsType = typeof(RevokedSessionJwtBearerEvents);
    });

builder.Services.AddScoped<RevokedSessionJwtBearerEvents>();

builder.Services.AddControllersWithViews()
    .AddJsonOptions(opts => opts.JsonSerializerOptions.PropertyNamingPolicy = null);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "CareerPanda API", Version = "v1" });
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "JWT Authorization header using the Bearer scheme.",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT"
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
            },
            Array.Empty<string>()
        }
    });
});

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
        policy.WithOrigins(
                config.DeploymentURLsConfig.UIURL.TrimEnd('/'),
                "http://localhost:4200",
                "http://localhost:5280")
            .AllowAnyMethod()
            .AllowAnyHeader()
            .AllowCredentials());
});

var app = builder.Build();

if (databaseLoggingEnabled)
{
    try
    {
        using var scope = app.Services.CreateScope();
        var logDb = scope.ServiceProvider.GetRequiredService<ApplicationLogDbContext>();
        await logDb.Database.EnsureCreatedAsync();
    }
    catch (Npgsql.PostgresException ex) when (ex.SqlState == "28P01")
    {
        throw new InvalidOperationException(
            "PostgreSQL password authentication failed. Update your connection string via User Secrets or " +
            "CAREERPANDA_DB_CONNECTION (Railway password may have changed).", ex);
    }
    catch (Npgsql.PostgresException ex) when (ex.SqlState == "42P01")
    {
        throw new InvalidOperationException(
            "Table public.application_logs is missing. Run tools/sql/create_application_logs.sql on the database.", ex);
    }
}

// ── Register all persisted schedules with Hangfire on startup ─────────────
using (var startupScope = app.Services.CreateScope())
{
    var scheduler = startupScope.ServiceProvider.GetRequiredService<JobSchedulerService>();
    await scheduler.RegisterAllAsync();
}

// ── Warm up H1B sponsor list in Redis on startup ───────────────────────────
await app.Services.GetRequiredService<SponsorCacheWarmupService>().WarmUpAsync();

// Railway terminates SSL at the proxy — container only receives plain HTTP on $PORT.
// UseHttpsRedirection inside the container causes "failed to determine https port" and hangs requests.

app.UseSwagger();
app.UseSwaggerUI();

app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseRateLimiter();
app.UseCors("AllowAll");
app.UseAuthentication();
app.UseAuthorization();

app.UseHangfireDashboard("/hangfire", new DashboardOptions
{
    Authorization = new IDashboardAuthorizationFilter[0]
});
app.MapControllers().RequireRateLimiting("api");
app.MapHealthChecks("/health");

app.Run();

// Converts postgresql://user:pass@host:port/db → Host=host;Port=port;Database=db;Username=user;Password=pass
// Hangfire.PostgreSql's UseNpgsqlConnection rejects URI-format strings.
static string ToNpgsqlKeyValue(string cs)
{
    if (!cs.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase) &&
        !cs.StartsWith("postgres://",   StringComparison.OrdinalIgnoreCase))
        return cs;

    var uri      = new Uri(cs);
    var userInfo = uri.UserInfo.Split(':', 2);
    var user     = Uri.UnescapeDataString(userInfo[0]);
    var password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : string.Empty;
    var database = uri.AbsolutePath.TrimStart('/');
    var port     = uri.Port > 0 ? uri.Port : 5432;

    return $"Host={uri.Host};Port={port};Database={database};Username={user};Password={password};SSL Mode=Prefer;Trust Server Certificate=true";
}

public partial class Program { }
