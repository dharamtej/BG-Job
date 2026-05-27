// DataAccess/Models/JobStatsModels.cs
// Read-model DTOs for the job-management dashboard's high-level view.
// Returned by IJobFetchDA aggregate queries; serialized straight to the UI.
namespace CareerPanda.DataAccess.Models;

/// <summary>Counts of raw_jobs broken down by each classification flag.</summary>
public class ClassificationCounts
{
    public int H1BSponsored  { get; set; }
    public int Sponsored     { get; set; }
    public int W2            { get; set; }
    public int C2C           { get; set; }
    public int ContractJob   { get; set; }
    public int FreelanceJob  { get; set; }
    public int PrimeVendor   { get; set; }
    public int Staffing      { get; set; }
    public int StartupJob    { get; set; }
    public int NonProfitJob  { get; set; }
    public int UniversityJob { get; set; }
}

/// <summary>Latest fetch-run summary for one handler/category.</summary>
public class LatestRunSummary
{
    public string  RunId           { get; set; } = string.Empty;
    public string  Status          { get; set; } = string.Empty;
    public DateTime? StartedAt      { get; set; }
    public DateTime? CompletedAt    { get; set; }
    public int?     DurationSeconds { get; set; }
    public int      TotalFetched    { get; set; }
    public int      TotalInserted   { get; set; }
    public int      TotalUpdated    { get; set; }
    public int      TotalSkipped    { get; set; }
    public int      TotalErrors     { get; set; }
    /// <summary>For ATS handlers this is companies/sites processed; for paged APIs it's pages fetched.</summary>
    public int      UnitsProcessed  { get; set; }
    public string?  ErrorMessage    { get; set; }
}

/// <summary>Per-handler roll-up: how many jobs/companies this source produced, its
/// classification breakdown, and the status of its most recent run.</summary>
public class HandlerStats
{
    public string Source            { get; set; } = string.Empty;
    public int    TotalJobs         { get; set; }
    public int    ActiveJobs        { get; set; }
    public int    DistinctCompanies { get; set; }
    public ClassificationCounts Classifications { get; set; } = new();
    public LatestRunSummary?     LatestRun       { get; set; }
}

/// <summary>Top-level dashboard summary across every source.</summary>
public class JobStatsOverview
{
    public int TotalJobs            { get; set; }
    public int ActiveJobs          { get; set; }
    public int TotalCompanies       { get; set; }
    public int NewCompaniesInWindow { get; set; }
    public int NewCompanyWindowHours { get; set; }
    public int DistinctSources      { get; set; }
    public ClassificationCounts Classifications { get; set; } = new();
    /// <summary>Job count per source, for quick bar-chart use.</summary>
    public Dictionary<string, int> JobsBySource { get; set; } = new();
}
