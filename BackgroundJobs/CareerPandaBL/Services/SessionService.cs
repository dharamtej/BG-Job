using CareerPanda.Framework.Cache;
using CareerPanda.Framework.Configuration;

namespace CareerPanda.BL.Services;

public class SessionService : ISessionService
{
    private const string LogoutPrefix = "logout:session:";
    private readonly ICacheService _cache;
    private readonly double _sessionTimeoutMinutes;

    public SessionService(ICacheService cache, Config config)
    {
        _cache = cache;
        _sessionTimeoutMinutes = double.TryParse(config.SessionConfig.Timeout, out var t) ? t : 60;
    }

    public async Task InvalidateSessionAsync(string sessionId, TimeSpan? ttl = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return;

        var expiry = ttl ?? TimeSpan.FromMinutes(_sessionTimeoutMinutes);
        await _cache.SetAsync($"{LogoutPrefix}{sessionId}", true, expiry);
    }

    public async Task<bool> IsSessionInvalidatedAsync(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return false;

        return await _cache.ExistsAsync($"{LogoutPrefix}{sessionId}");
    }
}
