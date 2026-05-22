using CareerPanda.DataAccess.Entities.Cp;
using CareerPanda.Framework;
using CareerPanda.Framework.Configuration;
using Google.Apis.Auth;
using Microsoft.Extensions.Logging;

namespace CareerPanda.BL.Services;

public class GoogleAuthService : IGoogleAuthService
{
    private readonly GoogleAuthConfig _config;
    private readonly ILogger<GoogleAuthService> _logger;

    public GoogleAuthService(Config config, ILogger<GoogleAuthService> logger)
    {
        _config = config.GoogleAuthConfig;
        _logger = logger;
    }

    public async Task<CpUser?> ValidateIdTokenAsync(string idToken, CancellationToken cancellationToken = default)
    {
        if (!_config.Enabled || string.IsNullOrWhiteSpace(_config.ClientId))
        {
            _logger.LogWarning("Google authentication is not configured.");
            return null;
        }

        try
        {
            var settings = new GoogleJsonWebSignature.ValidationSettings
            {
                Audience = new[] { _config.ClientId }
            };

            var payload = await GoogleJsonWebSignature.ValidateAsync(idToken, settings);

            return new CpUser
            {
                Email = payload.Email,
                FirstName = payload.GivenName ?? string.Empty,
                LastName = payload.FamilyName ?? string.Empty,
                IsGoogle = true,
                LoginProvider = "Google"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Google ID token validation failed.");
            return null;
        }
    }
}
