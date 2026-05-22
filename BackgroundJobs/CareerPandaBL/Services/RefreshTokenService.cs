using CareerPanda.Framework.Cache;
using CareerPanda.Framework.Configuration;

namespace CareerPanda.BL.Services;

public class RefreshTokenService : IRefreshTokenService
{
    private const string Prefix = "refresh:";
    private readonly ICacheService _cache;
    private readonly AuthConfig _authConfig;

    public RefreshTokenService(ICacheService cache, Config config)
    {
        _cache = cache;
        _authConfig = config.AuthConfig;
    }

    public async Task<string> CreateAsync(string userId, string sessionId, CancellationToken cancellationToken = default)
    {
        var token = Guid.NewGuid().ToString("N");
        var data = new RefreshTokenData { UserId = userId, SessionId = sessionId };
        await _cache.SetAsync(Prefix + token, data, TimeSpan.FromDays(_authConfig.RefreshTokenExpiryDays), cancellationToken);
        return token;
    }

    public Task<RefreshTokenData?> ValidateAsync(string refreshToken, CancellationToken cancellationToken = default) =>
        _cache.GetAsync<RefreshTokenData>(Prefix + refreshToken, cancellationToken);

    public Task RevokeAsync(string refreshToken, CancellationToken cancellationToken = default) =>
        _cache.RemoveAsync(Prefix + refreshToken, cancellationToken);
}
