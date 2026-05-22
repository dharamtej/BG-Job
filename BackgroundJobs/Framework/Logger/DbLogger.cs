using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CareerPanda.Framework.Logger;

public class DbLogger : ILogger
{
    private readonly IServiceProvider _serviceProvider;
    private readonly string _categoryName;

    public DbLogger(IServiceProvider serviceProvider, string categoryName)
    {
        _serviceProvider = serviceProvider;
        _categoryName = categoryName;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;

        var logEntry = ApplicationLogEntry.Create(
            logLevel,
            formatter(state, exception),
            exception?.ToString(),
            _categoryName,
            ApplicationContext.UserId,
            ApplicationContext.CorrelationId);

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationLogDbContext>();
            dbContext.Logs.Add(logEntry);
            dbContext.SaveChanges();
        }
        catch
        {
            // Avoid recursive logging failures breaking the app.
        }
    }
}
