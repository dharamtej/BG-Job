using CareerPanda.BL.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace CareerPanda.Web.Security;

public class RevokedSessionJwtBearerEvents : JwtBearerEvents
{
    private readonly ISessionService _sessionService;

    public RevokedSessionJwtBearerEvents(ISessionService sessionService)
    {
        _sessionService = sessionService;
        OnTokenValidated = ValidateSessionAsync;
    }

    private async Task ValidateSessionAsync(TokenValidatedContext context)
    {
        var sessionId = context.Principal?.Claims
            .FirstOrDefault(c => c.Type == "SessionId")?.Value;

        if (!string.IsNullOrEmpty(sessionId) &&
            await _sessionService.IsSessionInvalidatedAsync(sessionId))
        {
            context.Fail("Session has been revoked. Please login again.");
        }
    }
}
