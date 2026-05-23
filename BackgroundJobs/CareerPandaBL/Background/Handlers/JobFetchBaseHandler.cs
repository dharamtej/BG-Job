// CareerPandaBL/Background/Handlers/JobFetchBaseHandler.cs
// Abstract base for all 7 fetch handlers.
// Handles: InputPayload parsing → FetchRun row creation → page loop →
//          BulkUpsert → incremental stats → progress reporting → completion.
using System.Text.Json;
using CareerPanda.DataAccess.DA;
using CareerPanda.DataAccess.Entities.Api;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CareerPanda.BL.Background.Handlers;

public abstract class JobFetchBaseHandler : IJobHandler
{
    public abstract string JobType { get; }
    protected abstract string JobCategory { get; }
    protected abstract string ApiSource { get; }

    /// <summary>Milliseconds to wait between page fetches. Override per handler to respect rate limits.</summary>
    protected virtual int InterPageDelayMs => 600;

    private readonly IServiceScopeFactory _scopeFactory;
    protected readonly ILogger Logger;

    protected JobFetchBaseHandler(IServiceScopeFactory scopeFactory, ILogger logger)
    {
        _scopeFactory = scopeFactory;
        Logger        = logger;
    }

    // ── IJobHandler entry point ──────────────────────────────────────────────
    public virtual async Task ExecuteAsync(
        JobWorkRequest request,
        IJobProgressReporter progress,
        CancellationToken cancellationToken)
    {
        var input = ParseInput(request.InputPayload);

        using var scope   = _scopeFactory.CreateScope();
        var fetchDa = scope.ServiceProvider.GetRequiredService<IJobFetchDA>();

        // Create the fetch-run statistics row (id = same as background task id)
        var run = new ApiJobFetchRun
        {
            Id               = request.JobId,
            BackgroundTaskId = request.JobId,
            JobCategory      = JobCategory,
            ApiSource        = ApiSource,
            Status           = "Running",
            StartedAt        = DateTime.UtcNow,
            HoursBack        = input.HoursBack,
            MaxPages         = input.MaxPages,
            SearchQuery      = input.SearchQuery,
            LocationFilter   = input.Location,
            CreatedById      = request.UserId
        };
        await fetchDa.CreateFetchRunAsync(run);

        int totalFetched = 0, totalInserted = 0, totalUpdated = 0,
            totalSkipped = 0, totalErrors  = 0, pagesFetched  = 0;

        try
        {
            for (int page = 1; page <= input.MaxPages; page++)
            {
                if (cancellationToken.IsCancellationRequested) break;

                Logger.LogInformation("[{T}] Fetching page {P}/{M}", JobType, page, input.MaxPages);

                List<ApiRawJob> jobs;
                try
                {
                    jobs = await FetchPageAsync(page, input, run.Id, cancellationToken);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "[{T}] Page {P} fetch failed", JobType, page);
                    totalErrors++;
                    continue;
                }

                if (jobs.Count == 0)
                {
                    Logger.LogInformation("[{T}] No more results at page {P}", JobType, page);
                    break;
                }

                pagesFetched++;
                totalFetched += jobs.Count;

                var (ins, upd, err) = await fetchDa.BulkUpsertRawJobsAsync(jobs, cancellationToken);
                totalInserted += ins;
                totalUpdated  += upd;
                totalErrors   += err;
                totalSkipped  += jobs.Count - ins - upd - err;

                // Persist live stats after every page
                await fetchDa.UpdateFetchRunStatsAsync(
                    run.Id, totalFetched, totalInserted, totalUpdated,
                    totalSkipped, totalErrors, pagesFetched);

                int pct = (int)((double)page / input.MaxPages * 90);
                await progress.ReportProgressAsync(pct,
                    $"Page {page}/{input.MaxPages} — Inserted: {totalInserted}, Updated: {totalUpdated}");

                // Polite rate-limit delay between pages
                await Task.Delay(InterPageDelayMs, cancellationToken);
            }

            await fetchDa.CompleteFetchRunAsync(run.Id, "Completed");
        }
        catch (OperationCanceledException)
        {
            await fetchDa.CompleteFetchRunAsync(run.Id, "Cancelled", "Cancelled by user.");
            throw;
        }
        catch (Exception ex)
        {
            await fetchDa.CompleteFetchRunAsync(run.Id, "Failed", ex.Message);
            throw;
        }

        Logger.LogInformation(
            "[{T}] Done — Fetched={F} Inserted={I} Updated={U} Skipped={S} Errors={E}",
            JobType, totalFetched, totalInserted, totalUpdated, totalSkipped, totalErrors);
    }

    // ── Implemented per handler ──────────────────────────────────────────────
    protected abstract Task<List<ApiRawJob>> FetchPageAsync(
        int page, JobFetchInput input, string fetchRunId, CancellationToken ct);

    // ── Shared helpers ────────────────────────────────────────────────────────
    protected static JobFetchInput ParseInput(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return new JobFetchInput();
        try { return JsonSerializer.Deserialize<JobFetchInput>(payload) ?? new JobFetchInput(); }
        catch { return new JobFetchInput(); }
    }

    protected static DateTime? ParsePostDate(string? raw) =>
        !string.IsNullOrWhiteSpace(raw) && DateTime.TryParse(raw, out var dt)
            ? dt.ToUniversalTime() : null;

    protected static int? ParseHoursBack(DateTime? postDate) =>
        postDate.HasValue ? (int)(DateTime.UtcNow - postDate.Value).TotalHours : null;

    protected static bool ContainsAny(string? text, params string[] keywords) =>
        !string.IsNullOrWhiteSpace(text) &&
        keywords.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));
}
