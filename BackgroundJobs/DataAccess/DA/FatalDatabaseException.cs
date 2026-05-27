namespace CareerPanda.DataAccess.DA;

/// <summary>
/// Thrown when the database is in a state where further work cannot succeed
/// (disk full, out of memory, connection lost, admin shutdown, etc.).
/// Job handlers MUST NOT swallow this — let it propagate so the run aborts.
/// </summary>
public sealed class FatalDatabaseException : Exception
{
    public string? SqlState { get; }

    public FatalDatabaseException(string message, string? sqlState, Exception inner)
        : base(message, inner)
    {
        SqlState = sqlState;
    }
}
