// DataAccess/DA/IJobFetchDA.cs
using CareerPanda.DataAccess.Entities.Api;
using CareerPanda.Framework;

namespace CareerPanda.DataAccess.DA;

public interface IJobFetchDA
{
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

    Task<FrameworkResponse> GetFetchRunsAsync(
        int pageNumber,
        int pageSize,
        string? jobCategory = null,
        string? status = null);

    // ── Job Role Queries ─────────────────────────────────────────────────────
    Task<List<string>> GetActiveJobRoleQueriesAsync(CancellationToken cancellationToken = default);

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
