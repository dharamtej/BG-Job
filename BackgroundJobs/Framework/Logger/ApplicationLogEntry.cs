using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.Extensions.Logging;

namespace CareerPanda.Framework.Logger;

[Table("application_logs", Schema = "public")]
public class ApplicationLogEntry
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("timestamp")]
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    [Column("level")]
    public string Level { get; set; } = string.Empty;

    [Column("message")]
    public string Message { get; set; } = string.Empty;

    [Column("details")]
    public string? Details { get; set; }

    [Column("source")]
    public string Source { get; set; } = string.Empty;

    [Column("user_id")]
    public string? UserId { get; set; }

    [Column("correlation_id")]
    public string? CorrelationId { get; set; }

    public static ApplicationLogEntry Create(
        LogLevel logLevel,
        string message,
        string? details,
        string source,
        string? userId,
        string? correlationId) =>
        new()
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTime.UtcNow,
            Level = logLevel.ToString(),
            Message = message,
            Details = details,
            Source = source,
            UserId = userId,
            CorrelationId = correlationId
        };
}
