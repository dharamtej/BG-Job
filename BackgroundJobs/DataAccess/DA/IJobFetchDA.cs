// DataAccess/DA/IJobFetchDA.cs
using CareerPanda.DataAccess.Entities.Api;
using CareerPanda.DataAccess.Models;
using CareerPanda.Framework;

namespace CareerPanda.DataAccess.DA;

public interface IJobFetchDA
{
    // ── Dashboard stats ───────────────────────────────────────────────────────
    /// <summary>Top-level counts: totals, classification breakdown, jobs-by-source.</summary>
    Task<JobStatsOverview> GetStatsOverviewAsync(int newCompanyWindowHours, CancellationToken cancellationToken = default);

    /// <summary>Per-handler (source) roll-up incl. classification counts and latest run.</summary>
    Task<List<HandlerStats>> GetStatsByHandlerAsync(CancellationToken cancellationToken = default);

    /// <summary>Board-token health (valid/invalid/empty/unknown) per ATS source.</summary>
    Task<List<TokenStatusCounts>> GetTokenStatsAsync(CancellationToken cancellationToken = default);

    // ── Company enrichment ────────────────────────────────────────────────────
    /// <summary>Page through companies (ordered by id) for enrichment. Returns up to batchSize rows with id &gt; afterId.</summary>
    Task<List<ApiCompany>> GetCompaniesForEnrichmentAsync(int afterId, int batchSize, CancellationToken cancellationToken = default);

    /// <summary>A representative job apply/board URL for a company (used as career_page hint).</summary>
    Task<string?> GetSampleCompanyUrlAsync(int ourCompanyId, CancellationToken cancellationToken = default);

    /// <summary>Update only the enrichment columns; null arguments keep the existing value (COALESCE).</summary>
    Task UpdateCompanyEnrichmentAsync(int id, string? companyType, int? companySize, string? aboutCompany,
        string? website, string? careerPage, string? logoUrl, CancellationToken cancellationToken = default);

    // ── Fetch Run ───────────────────────────────────────────────────────────
    Task<FrameworkResponse> CreateFetchRunAsync(ApiJobFetchRun run);

    Task<FrameworkResponse> UpdateFetchRunStatsAsync(
        string runId,
        int totalFetched,
        int totalInserted,
        int totalUpdated,
        int totalSkipped,
        int totalErrors,
        int pagesFetched);

    Task<FrameworkResponse> CompleteFetchRunAsync(
        string runId,
        string status,
        string? errorMessage = null);

    Task<FrameworkResponse> GetFetchRunAsync(string runId);

    /// <summary>
    /// Marks any fetch_run rows still in "Running" state as "Failed" on app startup.
    /// In-process jobs do not survive an app restart, so leftover "Running" rows
    /// are orphans that would otherwise block reporting and re-runs.
    /// </summary>
    Task<int> RecoverOrphanedFetchRunsAsync(CancellationToken cancellationToken = default);

    Task<FrameworkResponse> GetFetchRunsAsync(
        int pageNumber,
        int pageSize,
        string? jobCategory = null,
        string? status = null);

    // ── Job Role Queries ─────────────────────────────────────────────────────
    Task<List<string>> GetActiveJobRoleQueriesAsync(CancellationToken cancellationToken = default);

    // ── Greenhouse ──────────────────────────────────────────────────────────
    Task<List<ApiGreenhouseBoardToken>> GetValidGreenhouseTokensAsync(CancellationToken cancellationToken = default);
    Task UpdateGreenhouseTokenStatusAsync(int id, string status, short httpCode, int jobCount, CancellationToken cancellationToken = default);

    // ── Lever ───────────────────────────────────────────────────────────────
    Task<List<ApiLeverBoardToken>> GetActiveLeverTokensAsync(CancellationToken cancellationToken = default);
    Task UpdateLeverTokenStatusAsync(int id, string status, short httpCode, int jobCount, CancellationToken cancellationToken = default);

    // ── Workday ─────────────────────────────────────────────────────────────
    Task<List<ApiWorkdayBoardToken>> GetActiveWorkdayTokensAsync(CancellationToken cancellationToken = default);
    Task UpdateWorkdayTokenStatusAsync(int id, string status, short httpCode, int jobCount, CancellationToken cancellationToken = default);

    // ── Ashby ───────────────────────────────────────────────────────────────
    Task<List<ApiAshbyBoardToken>> GetActiveAshbyTokensAsync(CancellationToken cancellationToken = default);
    Task UpdateAshbyTokenStatusAsync(int id, string status, short httpCode, int jobCount, CancellationToken cancellationToken = default);

    // ── BambooHR ────────────────────────────────────────────────────────────
    Task<List<ApiBambooHrBoardToken>> GetActiveBambooHrTokensAsync(CancellationToken cancellationToken = default);
    Task UpdateBambooHrTokenStatusAsync(int id, string status, short httpCode, int jobCount, CancellationToken cancellationToken = default);

    // ── iCIMS ───────────────────────────────────────────────────────────────
    Task<List<ApiIcimsBoardToken>> GetActiveIcimsTokensAsync(CancellationToken cancellationToken = default);
    Task UpdateIcimsTokenStatusAsync(int id, string status, short httpCode, int jobCount, CancellationToken cancellationToken = default);

    // ── Recruitee ───────────────────────────────────────────────────────────
    Task<List<ApiRecruiteeBoardToken>> GetActiveRecruiteeTokensAsync(CancellationToken cancellationToken = default);
    Task UpdateRecruiteeTokenStatusAsync(int id, string status, short httpCode, int jobCount, CancellationToken cancellationToken = default);

    // ── H1B Sponsors ────────────────────────────────────────────────────────
    Task<List<string>> GetH1BSponsorNamesAsync(CancellationToken cancellationToken = default);
    Task<List<ApiH1bSponsor>> GetUnenrichedSponsorsAsync(int batchSize, CancellationToken cancellationToken = default);
    Task UpdateSponsorNormalizedNameAsync(int id, string normalizedName, CancellationToken cancellationToken = default);

    // ── Raw Jobs (upsert) ───────────────────────────────────────────────────
    Task<(bool isNew, int rawJobId)> UpsertRawJobAsync(ApiRawJob job);

    Task<(int inserted, int updated, int errors)> BulkUpsertRawJobsAsync(
        IEnumerable<ApiRawJob> jobs,
        CancellationToken cancellationToken);
}
