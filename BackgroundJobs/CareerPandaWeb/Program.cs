using System.Text;
using System.Threading.RateLimiting;
using CareerPanda.BL.Background;
using CareerPanda.BL.Background.Handlers;
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
var postgresConnection = ConnectionStringResolver.Resolve(configuration);

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
builder.Services.AddHttpClient("TheMuse",  c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddHttpClient("USAJobs",  c => c.Timeout = TimeSpan.FromSeconds(30));

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

builder.Services.AddSingleton<IBackgroundJobQueue>(sp =>
    new BackgroundJobQueue(config.BackgroundJobsConfig.QueueCapacity));
builder.Services.AddSingleton<JobExecutionService>();
builder.Services.AddSingleton<IJobHandler, DefaultJobHandler>();

// ── Job Fetch Handlers (one per category) ─────────────────────────────────
builder.Services.AddSingleton<IJobHandler, AllJobsJobHandler>();
builder.Services.AddSingleton<IJobHandler, StartupJobsJobHandler>();
builder.Services.AddSingleton<IJobHandler, UniversityJobsJobHandler>();
builder.Services.AddSingleton<IJobHandler, NonProfitJobsJobHandler>();
builder.Services.AddSingleton<IJobHandler, ContractJobsJobHandler>();
builder.Services.AddSingleton<IJobHandler, H1BJobsJobHandler>();
builder.Services.AddSingleton<IJobHandler, PrimeVendorJobsJobHandler>();
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

builder.Services.AddHsts(options =>
{
    options.MaxAge = TimeSpan.FromDays(365);
    options.IncludeSubDomains = true;
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

if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
    app.UseHsts();
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseRateLimiter();
app.UseCors("AllowAll");
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers().RequireRateLimiting("api");
app.MapHealthChecks("/health");

app.Run();

public partial class Program { }
