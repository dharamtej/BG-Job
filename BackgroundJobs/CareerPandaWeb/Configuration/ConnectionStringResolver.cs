namespace CareerPanda.Web.Configuration;

public static class ConnectionStringResolver
{
    private const string EnvVarName = "CAREERPANDA_DB_CONNECTION";

    public static string Resolve(IConfiguration configuration)
    {
        var fromEnv = Environment.GetEnvironmentVariable(EnvVarName);
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return fromEnv.Trim();

        var fromConfig = configuration.GetSection("Connection:Connection").Value;
        if (IsUsable(fromConfig))
            return fromConfig!;

        throw new InvalidOperationException(
            $"PostgreSQL is not configured. Set the Railway password using one of:\n" +
            $"  1. User Secrets (recommended): dotnet user-secrets set \"Connection:Connection\" \"<npgsql-connection-string>\" --project CareerPandaWeb\n" +
            $"  2. Environment variable: {EnvVarName}\n" +
            $"Do not put real passwords in appsettings.json.");
    }

    private static bool IsUsable(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !value.Contains("YOUR_PASSWORD", StringComparison.OrdinalIgnoreCase) &&
        !value.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase);
}
