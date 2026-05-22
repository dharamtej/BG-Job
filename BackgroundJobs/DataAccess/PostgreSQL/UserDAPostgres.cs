using CareerPanda.DataAccess.DA;
using CareerPanda.DataAccess.Entities.Cp;
using CareerPanda.Framework;
using CareerPanda.Framework.Util;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CareerPanda.DataAccess.PostgreSQL;

public class UserDAPostgres : IUserDA
{
    private readonly CareerPandaDbContext _context;
    private readonly ILogger<UserDAPostgres> _logger;

    public UserDAPostgres(ILogger<UserDAPostgres> logger, CareerPandaDbContext context)
    {
        _logger = logger;
        _context = context;
    }

    public async Task<FrameworkResponse> GetUserByEmailAsync(string email)
    {
        var response = new FrameworkResponse { Status = Status.Failed };
        try
        {
            var user = await _context.Users
                .AsNoTracking()
                .FirstOrDefaultAsync(u => u.Email.ToLower() == email.ToLower() && u.IsActive);
            if (user != null)
            {
                response.Status = Status.Success;
                response.Entity = user;
            }
        }
        catch (Exception ex)
        {
            response.Message = ex.Message;
            _logger.LogError(ex, "GetUserByEmail failed");
        }
        return response;
    }

    public async Task<FrameworkResponse> GetUserByIdAsync(int userId)
    {
        var response = new FrameworkResponse { Status = Status.Failed };
        try
        {
            var user = await _context.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId && u.IsActive);
            if (user != null)
            {
                response.Status = Status.Success;
                response.Entity = user;
            }
        }
        catch (Exception ex)
        {
            response.Message = ex.Message;
            _logger.LogError(ex, "GetUserById failed");
        }
        return response;
    }

    public async Task<FrameworkResponse> RegisterUserAsync(CpUser user)
    {
        var response = new FrameworkResponse { Status = Status.Failed };
        try
        {
            var exists = await _context.Users.AnyAsync(u => u.Email.ToLower() == user.Email.ToLower());
            if (exists)
            {
                response.Message = "User already exists.";
                return response;
            }

            user.CreatedAt = DateTime.UtcNow;
            user.UpdatedOn = DateTime.UtcNow;
            user.IsActive = true;
            if (user.RoleId == null)
                user.RoleId = 1;

            _context.Users.Add(user);
            await _context.SaveChangesAsync();
            response.Status = Status.Success;
            response.Entity = user;
        }
        catch (Exception ex)
        {
            response.Message = ex.Message;
            _logger.LogError(ex, "RegisterUser failed");
        }
        return response;
    }

    public async Task<FrameworkResponse> UpdatePasswordAsync(int userId, string passwordHash)
    {
        var response = new FrameworkResponse { Status = Status.Failed };
        try
        {
            var user = await _context.Users.FindAsync(userId);
            if (user == null)
            {
                response.Message = "User not found.";
                return response;
            }

            user.Password = passwordHash;
            user.UpdatedOn = DateTime.UtcNow;
            await _context.SaveChangesAsync();
            response.Status = Status.Success;
            response.Entity = user;
        }
        catch (Exception ex)
        {
            response.Message = ex.Message;
            _logger.LogError(ex, "UpdatePassword failed");
        }
        return response;
    }

    public async Task<FrameworkResponse> GetOrCreateGoogleUserAsync(CpUser user)
    {
        var response = new FrameworkResponse { Status = Status.Failed };
        try
        {
            var dbUser = await _context.Users
                .FirstOrDefaultAsync(u => u.Email.ToLower() == user.Email.ToLower());

            if (dbUser == null)
            {
                user.CreatedAt = DateTime.UtcNow;
                user.UpdatedOn = DateTime.UtcNow;
                user.IsActive = true;
                user.IsGoogle = true;
                user.RoleId ??= 1;
                user.Password ??= string.Empty;
                _context.Users.Add(user);
                await _context.SaveChangesAsync();
                dbUser = user;
            }
            else
            {
                dbUser.IsGoogle = true;
                dbUser.UpdatedOn = DateTime.UtcNow;
                await _context.SaveChangesAsync();
            }

            response.Status = Status.Success;
            response.Entity = dbUser;
        }
        catch (Exception ex)
        {
            response.Message = ex.Message;
            _logger.LogError(ex, "GetOrCreateGoogleUser failed");
        }
        return response;
    }
}
