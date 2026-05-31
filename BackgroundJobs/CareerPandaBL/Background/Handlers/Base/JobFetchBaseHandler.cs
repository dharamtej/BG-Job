// CareerPandaBL/Background/Handlers/JobFetchBaseHandler.cs
// Abstract base for all 7 fetch handlers.
// Handles: InputPayload parsing → FetchRun row creation → page loop →
//          BulkUpsert → incremental stats → progress reporting → completion.
using System.Text.Json;
using CareerPanda.DataAccess.DA;
using CareerPanda.DataAccess.Entities.Api;
using CareerPanda.Framework.Cache;
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

    protected readonly IServiceScopeFactory _scopeFactory;
    protected readonly ICacheService _cache;
    protected readonly ILogger Logger;

    protected JobFetchBaseHandler(IServiceScopeFactory scopeFactory, ICacheService cache, ILogger logger)
    {
        _scopeFactory = scopeFactory;
        _cache        = cache;
        Logger        = logger;
    }

    private static readonly string[] LegalSuffixes =
        ["INCORPORATED", "CORPORATION", "LIMITED", "INC", "LLC", "CORP", "LTD", "CO", "LP", "LLP", "PLLC", "PC"];

    protected async Task<HashSet<string>> LoadSponsorsAsync(CancellationToken ct)
    {
        var cached = await _cache.GetAsync<List<string>>(SponsorCacheWarmupService.CacheKey, ct);
        if (cached is { Count: > 0 }) return BuildSponsorSet(cached);

        // Cache miss (TTL expired) — reload from DB and re-cache for 24 hrs
        using var scope = _scopeFactory.CreateScope();
        var da    = scope.ServiceProvider.GetRequiredService<IJobFetchDA>();
        var names = await da.GetH1BSponsorNamesAsync(ct);
        await _cache.SetAsync(SponsorCacheWarmupService.CacheKey, names, SponsorCacheWarmupService.CacheTtl, ct);
        Logger.LogInformation("[{T}] Reloaded {Count} H1B sponsors from DB into cache", JobType, names.Count);
        return BuildSponsorSet(names);
    }

    protected static HashSet<string> BuildSponsorSet(List<string> names)
    {
        var set = new HashSet<string>(names.Count * 2, StringComparer.OrdinalIgnoreCase);
        foreach (var name in names) { set.Add(name); set.Add(NormalizeCompanyName(name)); }
        return set;
    }

    protected static string NormalizeCompanyName(string name)
    {
        var upper    = name.ToUpperInvariant();
        var stripped = System.Text.RegularExpressions.Regex.Replace(upper, @"[^\w\s]", " ");
        var parts    = stripped.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int count    = parts.Length;
        while (count > 1 && LegalSuffixes.Contains(parts[count - 1])) count--;
        return string.Join(' ', parts, 0, count);
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

                var (ins, upd, err) = await fetchDa.BulkUpsertRawJobsAsync(ApplyGate(jobs, Logger, $"[{JobType}]"), cancellationToken);
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

    // Delegates to JobFetchHelpers so all handlers (base and standalone) share one implementation.
    // Delegates to JobValidationGate.FilterValid — convenience wrapper for base-handler subclasses.
    protected static List<ApiRawJob> ApplyGate(IEnumerable<ApiRawJob> jobs, ILogger? logger = null, string? tag = null)
        => JobValidationGate.FilterValid(jobs, logger, tag);

    protected static bool    ContainsAny(string? text, params string[] keywords) => JobFetchHelpers.ContainsAny(text, keywords);
    protected static string? NormalizeSalaryPeriod(string? raw)                  => JobFetchHelpers.NormalizeSalaryPeriod(raw);
    protected static string? NormalizeJobLevel(string? raw)                      => JobFetchHelpers.NormalizeJobLevel(raw);
    protected static string? NormalizeState(string? raw)                         => JobFetchHelpers.NormalizeState(raw);
    protected static string? StripHtml(string? html)                             => JobFetchHelpers.StripHtml(html);
    protected static string? BuildSalaryRangeText(decimal? min, decimal? max, string? period)
        => JobFetchHelpers.BuildSalaryRangeText(min, max, period);
    protected static Task<System.Text.Json.JsonElement> ReadJsonAsync(System.Net.Http.HttpContent content, CancellationToken ct = default)
        => JobFetchHelpers.ReadJsonAsync(content, ct);
}
