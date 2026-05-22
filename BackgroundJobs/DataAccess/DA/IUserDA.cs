using CareerPanda.DataAccess.Entities.Cp;
using CareerPanda.Framework;

namespace CareerPanda.DataAccess.DA;

public interface IUserDA
{
    Task<FrameworkResponse> GetUserByEmailAsync(string email);

    Task<FrameworkResponse> GetUserByIdAsync(int userId);

    Task<FrameworkResponse> RegisterUserAsync(CpUser user);

    Task<FrameworkResponse> UpdatePasswordAsync(int userId, string passwordHash);

    Task<FrameworkResponse> GetOrCreateGoogleUserAsync(CpUser user);
}
