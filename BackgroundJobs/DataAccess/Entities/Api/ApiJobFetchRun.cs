// DataAccess/Entities/Api/ApiJobFetchRun.cs
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CareerPanda.DataAccess.Entities.Api;

[Table("job_fetch_runs", Schema = "api")]
public class ApiJobFetchRun
{
    [Key]
    [Column("id")]
    public string Id { get; set; } = string.Empty;

    [Column("background_task_id")]
    public string BackgroundTaskId { get; set; } = string.Empty;

    /// <summary>AllJobs | StartupJobs | UniversityJobs | NonProfitJobs | ContractJobs | H1BJobs | PrimeVendorJobs</summary>
    [Column("job_category")]
    public string JobCategory { get; set; } = string.Empty;

    /// <summary>JSearch | USAJobs | TheMuse</summary>
    [Column("api_source")]
    public string? ApiSource { get; set; }

    /// <summary>Running | Completed | Failed | Cancelled</summary>
    [Column("status")]
    public string Status { get; set; } = "Running";

    [Column("started_at")]
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;

    [Column("completed_at")]
    public DateTime? CompletedAt { get; set; }

    [Column("duration_seconds")]
    public int? DurationSeconds { get; set; }

    // ── Statistics ──────────────────────────────────────────────────────────
    [Column("total_fetched")]
    public int TotalFetched { get; set; }

    [Column("total_inserted")]
    public int TotalInserted { get; set; }

    [Column("total_updated")]
    public int TotalUpdated { get; set; }

    [Column("total_skipped")]
    public int TotalSkipped { get; set; }

    [Column("total_errors")]
    public int TotalErrors { get; set; }

    [Column("pages_fetched")]
    public int PagesFetched { get; set; }

    // ── Input parameters snapshot ───────────────────────────────────────────
    [Column("hours_back")]
    public int? HoursBack { get; set; }

    [Column("max_pages")]
    public int? MaxPages { get; set; }

    [Column("search_query")]
    public string? SearchQuery { get; set; }

    [Column("location_filter")]
    public string? LocationFilter { get; set; }

    [Column("error_message")]
    public string? ErrorMessage { get; set; }

    [Column("created_by_id")]
    public string? CreatedById { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
