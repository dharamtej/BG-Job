namespace CareerPanda.BL.Models;

public class TokenRequest
{
    public string Email { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;
}

public class TokenResponse
{
    public string AccessToken { get; set; } = string.Empty;

    public string TokenType { get; set; } = "Bearer";

    public int ExpiresInMinutes { get; set; }

    public string? RefreshToken { get; set; }

    public string Email { get; set; } = string.Empty;

    public string UserId { get; set; } = string.Empty;
}

public class RefreshTokenRequest
{
    public string RefreshToken { get; set; } = string.Empty;
}

public class ForgotPasswordRequest
{
    public string LoginId { get; set; } = string.Empty;
}

public class ResetPasswordRequest
{
    public string Token { get; set; } = string.Empty;

    public string NewPassword { get; set; } = string.Empty;
}
