// CareerPandaBL/Background/Handlers/JobFetchInput.cs
namespace CareerPanda.BL.Background.Handlers;

public record JobFetchInput
{
    /// <summary>How many hours back to fetch jobs. Default 720 (30 days → date_posted=month).</summary>
    public int HoursBack { get; set; } = 720;

    /// <summary>
    /// Maximum API pages to fetch. For AllJobs this is ignored when no SearchQuery is set —
    /// it runs every role query from md.job_roles automatically.
    /// </summary>
    public int MaxPages { get; set; } = 10;

    /// <summary>
    /// For AllJobs: how many JSearch pages to fetch per role query (default 1).
    /// JSearch pagination degrades after page 2-3 so keep this at 1-2.
    /// </summary>
    public int PagesPerQuery { get; set; } = 1;

    /// <summary>Optional additional keyword filter sent to the API.</summary>
    public string? SearchQuery { get; set; }

    /// <summary>
    /// Location filter. Omit (null) to expand across all 50 US states.
    /// Pass any explicit value (e.g. "United States", "Texas") for a single-location search.
    /// </summary>
    public string? Location { get; set; }

    /// <summary>
    /// Adzuna-only: when set to "contract" or "permanent", appends &amp;contract_type=… to the API
    /// query. Used to fetch contract-only sweeps for C2C/1099/Contract-to-Hire coverage.
    /// Null (default) = no filter (current behaviour preserved).
    /// </summary>
    public string? ContractType { get; set; }
}
