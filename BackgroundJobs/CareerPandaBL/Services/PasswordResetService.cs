using CareerPanda.Framework.Cache;
using CareerPanda.Framework.Configuration;

namespace CareerPanda.BL.Services;

public class PasswordResetService : IPasswordResetService
{
    private const string Prefix = "pwdreset:";
    private readonly ICacheService _cache;
    private readonly AuthConfig _authConfig;

    public PasswordResetService(ICacheService cache, Config config)
    {
        _cache = cache;
        _authConfig = config.AuthConfig;
    }

    public async Task<string> CreateResetTokenAsync(string userId, CancellationToken cancellationToken = default)
    {
        var token = Guid.NewGuid().ToString("N");
        await _cache.SetAsync(Prefix + token, userId,
            TimeSpan.FromMinutes(_authConfig.PasswordResetExpiryMinutes), cancellationToken);
        return token;
    }

    public Task<string?> ValidateResetTokenAsync(string token, CancellationToken cancellationToken = default) =>
        _cache.GetAsync<string>(Prefix + token, cancellationToken);

    public Task InvalidateResetTokenAsync(string token, CancellationToken cancellationToken = default) =>
        _cache.RemoveAsync(Prefix + token, cancellationToken);
}
