namespace CareerPanda.BL.Services;

public interface IRefreshTokenService
{
    Task<string> CreateAsync(string userId, string sessionId, CancellationToken cancellationToken = default);

    Task<RefreshTokenData?> ValidateAsync(string refreshToken, CancellationToken cancellationToken = default);

    Task RevokeAsync(string refreshToken, CancellationToken cancellationToken = default);
}

public class RefreshTokenData
{
    public string UserId { get; set; } = string.Empty;

    public string SessionId { get; set; } = string.Empty;
}
