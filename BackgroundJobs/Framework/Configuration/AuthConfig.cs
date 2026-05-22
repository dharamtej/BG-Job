namespace CareerPanda.Framework.Configuration;

public class AuthConfig
{
    public int RefreshTokenExpiryDays { get; set; } = 7;

    public int PasswordResetExpiryMinutes { get; set; } = 60;
}
