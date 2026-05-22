namespace CareerPanda.BL.Services;

public interface ISessionService
{
    Task InvalidateSessionAsync(string sessionId, TimeSpan? ttl = null);

    Task<bool> IsSessionInvalidatedAsync(string sessionId);
}
