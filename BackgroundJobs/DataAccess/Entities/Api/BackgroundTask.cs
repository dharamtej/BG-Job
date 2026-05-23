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

    // ── Schedule ──────────────────────────────────────────────────────────────
    /// <summary>None | Daily | Interval</summary>
    [Column("schedule_type")]
    public string? ScheduleType { get; set; }

    /// <summary>Used when ScheduleType=Daily. Time of day in UTC (e.g., 02:00:00).</summary>
    [Column("schedule_daily_time")]
    public TimeSpan? ScheduleDailyTime { get; set; }

    /// <summary>Used when ScheduleType=Interval. How often to run, in hours (e.g., 6, 12, 24).</summary>
    [Column("schedule_interval_hours")]
    public int? ScheduleIntervalHours { get; set; }

    [Column("next_run_at")]
    public DateTime? NextRunAt { get; set; }

    [Column("last_scheduled_run_at")]
    public DateTime? LastScheduledRunAt { get; set; }
}
