using CareerPanda.BL.Models;
using CareerPanda.BL.Services;
using CareerPanda.BL.Util;
using CareerPanda.DataAccess.DA;
using CareerPanda.DataAccess.Entities.Cp;
using CareerPanda.Framework;
using CareerPanda.Framework.Configuration;
using CareerPanda.Framework.Mail;
using CareerPanda.Framework.Security;
using CareerPanda.Framework.Util;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace CareerPanda.BL.Logic;

public class LoginBL
{
    private readonly IUserDA _userDa;
    private readonly IGoogleAuthService _googleAuthService;
    private readonly ISessionService _sessionService;
    private readonly IRefreshTokenService _refreshTokenService;
    private readonly IPasswordResetService _passwordResetService;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IMailService _mailService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly Config _config;
    private readonly ILogger<LoginBL> _logger;

    public LoginBL(
        IUserDA userDa,
        IGoogleAuthService googleAuthService,
        ISessionService sessionService,
        IRefreshTokenService refreshTokenService,
        IPasswordResetService passwordResetService,
        IPasswordHasher passwordHasher,
        IMailService mailService,
        IHttpClientFactory httpClientFactory,
        Config config,
        ILogger<LoginBL> logger)
    {
        _userDa = userDa;
        _googleAuthService = googleAuthService;
        _sessionService = sessionService;
        _refreshTokenService = refreshTokenService;
        _passwordResetService = passwordResetService;
        _passwordHasher = passwordHasher;
        _mailService = mailService;
        _httpClientFactory = httpClientFactory;
        _config = config;
        _logger = logger;
    }

    public async Task<FrameworkResponse> RegisterAsync(CpUser user)
    {
        var response = new FrameworkResponse { Status = Status.Failed };
        if (user == null || string.IsNullOrWhiteSpace(user.Email) || string.IsNullOrWhiteSpace(user.Password))
        {
            response.Message = "LoginId and Password are required.";
            return response;
        }

        user.Email = user.Email.Trim();
        user.Password = _passwordHasher.Hash(user.Password);
        user.LoginProvider = "Internal";

        response = await _userDa.RegisterUserAsync(user);
        if (response.Status == Status.Success && response.Entity is CpUser registered)
            return await IssueTokenAsync(registered);

        response.Message ??= "Registration failed.";
        return response;
    }

    public async Task<FrameworkResponse> CheckLoginAsync(CpUser user)
    {
        var response = new FrameworkResponse { Status = Status.Failed };
        if (user == null)
            return response;

        switch (_config.CareerPandaSettingsConfig.AuthenticationType)
        {
            case "External":
                return await CheckExternalLoginAsync(user);
            case "Internal":
            default:
                response = await _userDa.GetUserByEmailAsync(user.Email);
                if (response.Status == Status.Success && response.Entity is CpUser dbUser &&
                    !string.IsNullOrEmpty(dbUser.Password) &&
                    _passwordHasher.Verify(user.Password ?? string.Empty, dbUser.Password))
                {
                    dbUser.LastLoggedIn = DateTime.UtcNow;
                    return await IssueTokenAsync(dbUser);
                }

                response.Message = "Incorrect username or password.";
                response.Status = Status.Failed;
                return response;
        }
    }

    public async Task<FrameworkResponse> GoogleLoginAsync(CpUser user)
    {
        var response = new FrameworkResponse { Status = Status.Failed };

        if (string.IsNullOrWhiteSpace(user.IdToken))
        {
            response.Message = "Google ID token is required.";
            return response;
        }

        var googleUser = await _googleAuthService.ValidateIdTokenAsync(user.IdToken);
        if (googleUser == null)
        {
            response.Message = "Invalid Google credentials.";
            return response;
        }

        response = await _userDa.GetOrCreateGoogleUserAsync(googleUser);
        if (response.Status == Status.Success && response.Entity is CpUser dbUser)
            return await IssueTokenAsync(dbUser, LoginProvider.Google.ToString());

        response.Message ??= "Unable to sign in with Google.";
        return response;
    }

    public async Task<FrameworkResponse> RefreshTokenAsync(RefreshTokenRequest request)
    {
        var response = new FrameworkResponse { Status = Status.Failed };
        if (string.IsNullOrWhiteSpace(request?.RefreshToken))
        {
            response.Message = "Refresh token is required.";
            return response;
        }

        var data = await _refreshTokenService.ValidateAsync(request.RefreshToken);
        if (data == null)
        {
            response.Message = "Invalid or expired refresh token.";
            return response;
        }

        if (await _sessionService.IsSessionInvalidatedAsync(data.SessionId))
        {
            response.Message = "Session has been revoked.";
            return response;
        }

        if (!int.TryParse(data.UserId, out var userId))
        {
            response.Message = "Invalid user in refresh token.";
            return response;
        }

        var userResponse = await _userDa.GetUserByIdAsync(userId);
        if (userResponse.Status != Status.Success || userResponse.Entity is not CpUser dbUser)
        {
            response.Message = "User not found.";
            return response;
        }

        await _refreshTokenService.RevokeAsync(request.RefreshToken);
        return await IssueTokenAsync(dbUser, dbUser.LoginProvider, data.SessionId);
    }

    public async Task<FrameworkResponse> ForgotPasswordAsync(ForgotPasswordRequest request)
    {
        var response = new FrameworkResponse
        {
            Status = Status.Success,
            Message = "If the account exists, reset instructions were sent."
        };

        if (string.IsNullOrWhiteSpace(request?.LoginId))
        {
            response.Message = "LoginId is required.";
            response.Status = Status.Failed;
            return response;
        }

        var userResponse = await _userDa.GetUserByEmailAsync(request.LoginId);
        if (userResponse.Status != Status.Success || userResponse.Entity is not CpUser user)
            return response;

        var token = await _passwordResetService.CreateResetTokenAsync(user.Id.ToString());
        var resetUrl = $"{_config.DeploymentURLsConfig.UIURL.TrimEnd('/')}/reset-password?token={token}";

        try
        {
            await _mailService.SendAsync(
                user.Email,
                "CareerPanda password reset",
                $"<p>Reset your password: <a href=\"{resetUrl}\">{resetUrl}</a></p><p>Token: {token}</p>");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send reset email; token logged for support.");
        }

        return response;
    }

    public async Task<FrameworkResponse> ResetPasswordAsync(ResetPasswordRequest request)
    {
        var response = new FrameworkResponse { Status = Status.Failed };

        if (string.IsNullOrWhiteSpace(request?.Token) || string.IsNullOrWhiteSpace(request.NewPassword))
        {
            response.Message = "Token and NewPassword are required.";
            return response;
        }

        var userIdStr = await _passwordResetService.ValidateResetTokenAsync(request.Token);
        if (string.IsNullOrEmpty(userIdStr) || !int.TryParse(userIdStr, out var userId))
        {
            response.Message = "Invalid or expired reset token.";
            return response;
        }

        var hash = _passwordHasher.Hash(request.NewPassword);
        response = await _userDa.UpdatePasswordAsync(userId, hash);
        if (response.Status == Status.Success)
        {
            await _passwordResetService.InvalidateResetTokenAsync(request.Token);
            response.Message = "Password updated successfully.";
        }

        return response;
    }

    public async Task<FrameworkResponse> LogoutAsync(string sessionId, string? refreshToken = null)
    {
        await _sessionService.InvalidateSessionAsync(sessionId);
        if (!string.IsNullOrWhiteSpace(refreshToken))
            await _refreshTokenService.RevokeAsync(refreshToken);

        return new FrameworkResponse { Status = Status.Success, Message = "Logged out successfully." };
    }

    private async Task<FrameworkResponse> CheckExternalLoginAsync(CpUser user)
    {
        var response = new FrameworkResponse { Status = Status.Failed };
        var baseUrl = _config.CareerPandaSettingsConfig.ExternalAuthUrl;

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            response.Message = "External authentication URL is not configured.";
            return response;
        }

        try
        {
            var client = _httpClientFactory.CreateClient("ExternalAuth");
            var path = $"{baseUrl}?custcode={user.CustCode}&orgcode={user.OrgCode}" +
                       $"&loginname={Uri.EscapeDataString(user.Email)}" +
                       $"&appcode={_config.CareerPandaSettingsConfig.AppCode}" +
                       $"&password={Uri.EscapeDataString(user.Password ?? string.Empty)}";

            var httpResponse = await client.GetAsync(path);
            if (!httpResponse.IsSuccessStatusCode)
            {
                response.Message = "Incorrect username or password.";
                return response;
            }

            var result = await httpResponse.Content.ReadAsStringAsync();
            var apiResponse = JsonConvert.DeserializeObject<ExternalAuthResponse>(result);

            if (apiResponse?.status == "S001")
            {
                user.Id = 0;
                return await IssueTokenAsync(user);
            }

            response.Message = apiResponse?.status switch
            {
                "S002" => "Incorrect username or password",
                "FU001" => "Incorrect username",
                "FC001" => "Incorrect customer code",
                "FO001" => "Incorrect organization code",
                "FP001" => "Incorrect password",
                "FS001" => "Incorrect app code",
                _ => "Authentication failed."
            };
        }
        catch (Exception ex)
        {
            response.Message = ex.Message;
            _logger.LogError(ex, "External login failed.");
        }

        return response;
    }

    private async Task<FrameworkResponse> IssueTokenAsync(CpUser user, string? loginProvider = null, string? existingSessionId = null)
    {
        var sessionId = existingSessionId ?? Guid.NewGuid().ToString();
        var timeout = double.TryParse(_config.SessionConfig.Timeout, out var t) ? t : 60;
        var userId = user.Id > 0 ? user.Id.ToString() : user.Email;
        var roleId = user.RoleId?.ToString() ?? "1";

        var accessToken = UtilityManager.GenerateToken(
            _config.Crypto.Key,
            user.Email,
            userId,
            roleId,
            user.RoleCode,
            timeout,
            sessionId,
            loginProvider ?? user.LoginProvider);

        var refreshToken = await _refreshTokenService.CreateAsync(userId, sessionId);

        return new FrameworkResponse
        {
            Status = Status.Success,
            Message = accessToken,
            Response = refreshToken,
            Entity = UserSanitizer.Sanitize(user)
        };
    }
}
