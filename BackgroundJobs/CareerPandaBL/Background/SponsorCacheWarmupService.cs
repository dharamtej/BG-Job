// CareerPandaBL/Background/SponsorCacheWarmupService.cs
// Primes the H1B sponsor list in Redis on app startup.
// Called once by Program.cs; all job handlers then read from Redis for 24 hrs.
using CareerPanda.DataAccess.DA;
using CareerPanda.Framework.Cache;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CareerPanda.BL.Background;

public class SponsorCacheWarmupService
{
    internal const string CacheKey = "h1b:sponsors:names";
    internal static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);

    private readonly ICacheService _cache;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SponsorCacheWarmupService> _logger;

    public SponsorCacheWarmupService(
        ICacheService cache,
        IServiceScopeFactory scopeFactory,
        ILogger<SponsorCacheWarmupService> logger)
    {
        _cache        = cache;
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    public async Task WarmUpAsync(CancellationToken ct = default)
    {
        var cached = await _cache.GetAsync<List<string>>(CacheKey, ct);
        if (cached is { Count: > 0 })
        {
            _logger.LogInformation("[Startup] H1B sponsor list already in Redis ({Count} entries)", cached.Count);
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var da    = scope.ServiceProvider.GetRequiredService<IJobFetchDA>();
        var names = await da.GetH1BSponsorNamesAsync(ct);
        if (names.Count > 0)
            await _cache.SetAsync(CacheKey, names, CacheTtl, ct);

        _logger.LogInformation("[Startup] Loaded {Count} H1B sponsors from DB into Redis", names.Count);
    }
}
