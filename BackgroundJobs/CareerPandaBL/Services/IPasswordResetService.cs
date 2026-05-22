namespace CareerPanda.BL.Services;

public interface IPasswordResetService
{
    Task<string> CreateResetTokenAsync(string userId, CancellationToken cancellationToken = default);

    Task<string?> ValidateResetTokenAsync(string token, CancellationToken cancellationToken = default);

    Task InvalidateResetTokenAsync(string token, CancellationToken cancellationToken = default);
}
