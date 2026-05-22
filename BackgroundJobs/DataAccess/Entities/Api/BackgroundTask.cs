using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using CareerPanda.Framework.Util;

namespace CareerPanda.DataAccess.Entities.Api;

/// <summary>API async/background processing tasks. Table must be added via migration (cp.api_background_tasks).</summary>
[Table("api_background_tasks", Schema = "cp")]
public class BackgroundTask
{
    [Key]
    [Column("id")]
    public string Id { get; set; } = string.Empty;

    [Column("name")]
    public string Name { get; set; } = string.Empty;

    [Column("description")]
    public string? Description { get; set; }

    [Column("job_type")]
    public string JobType { get; set; } = "Default";

    [Column("status")]
    public JobStatus Status { get; set; } = JobStatus.Pending;

    [Column("progress_percent")]
    public int ProgressPercent { get; set; }

    [Column("started_at")]
    public DateTime? StartedAt { get; set; }

    [Column("completed_at")]
    public DateTime? CompletedAt { get; set; }

    [Column("result_payload")]
    public string? ResultPayload { get; set; }

    [Column("error_message")]
    public string? ErrorMessage { get; set; }

    [Column("created_by_id")]
    public string? CreatedById { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
