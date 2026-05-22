using CareerPanda.DataAccess.Entities.Cp;

namespace CareerPanda.BL.Services;

public interface IGoogleAuthService
{
    Task<CpUser?> ValidateIdTokenAsync(string idToken, CancellationToken cancellationToken = default);
}
