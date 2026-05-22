using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CareerPanda.Framework.Logger;

[ProviderAlias("Database")]
public class DbLoggerProvider : ILoggerProvider
{
    private readonly IServiceProvider _serviceProvider;

    public DbLoggerProvider(IServiceProvider serviceProvider, IOptions<string>? options = null)
    {
        _serviceProvider = serviceProvider;
    }

    public ILogger CreateLogger(string categoryName) =>
        new DbLogger(_serviceProvider, categoryName);

    public void Dispose()
    {
    }
}
