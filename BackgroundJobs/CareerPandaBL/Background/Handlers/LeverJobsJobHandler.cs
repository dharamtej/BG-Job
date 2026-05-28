// CareerPandaBL/Background/Handlers/LeverJobsJobHandler.cs
// SOURCE : Lever Job Board API (public, no auth required)
// FLOW   : Load board tokens from api.lever_board_tokens (status != INVALID)
//          → GET https://api.lever.co/v0/postings/{token}?mode=json  (full list + details in one call)
//          → Update token status (VALID / EMPTY / INVALID) based on response
//          → Upsert into api.raw_jobs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CareerPanda.DataAccess.DA;
using CareerPanda.DataAccess.Entities.Api;
using CareerPanda.Framework.Cache;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CareerPanda.BL.Background.Handlers;

public class LeverJobsJobHandler : IJobHandler
{
    public string JobType => "LeverJobs";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _http;
    private readonly ICacheService _cache;
    private readonly ILogger<LeverJobsJobHandler> _logger;

    // How many boards are processed concurrently — tunable via appsettings.
    private readonly int _companyParallel;

    public LeverJobsJobHandler(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        ICacheService cacheService,
        IConfiguration configuration,
        ILogger<LeverJobsJobHandler> logger)
    {
        _scopeFactory = scopeFactory;
        _http         = httpClientFactory;
        _cache        = cacheService;
        _logger       = logger;

        _companyParallel = Math.Max(1,
            configuration.GetSection("JobApiSettings").GetValue("LeverCompanyParallel", 12));
    }

    public async Task ExecuteAsync(
        JobWorkRequest request,
        IJobProgressReporter progress,
        CancellationToken cancellationToken)
    {
        var run = new ApiJobFetchRun
        {
            Id               = request.JobId,
            BackgroundTaskId = request.JobId,
            JobCategory      = "LeverJobs",
            ApiSource        = "Lever",
            Status           = "Running",
            StartedAt        = DateTime.UtcNow,
            CreatedById      = request.UserId
        };

        HashSet<string> sponsors;
        List<ApiLeverBoardToken> tokens;
        using (var setupScope = _scopeFactory.CreateScope())
        {
            var setupDa = setupScope.ServiceProvider.GetRequiredService<IJobFetchDA>();
            await setupDa.CreateFetchRunAsync(run);

            sponsors = await LoadSponsorsAsync(setupDa, cancellationToken);
            _logger.LogInformation("[Lever] Loaded {Count} H1B sponsors for matching", sponsors.Count);

            tokens = await setupDa.GetActiveLeverTokensAsync(cancellationToken);
            _logger.LogInformation("[Lever] Loaded {Count} board tokens", tokens.Count);
        }

        // Counters shared across parallel workers — mutate only via Interlocked.
        int totalFetched = 0, totalInserted = 0, totalUpdated = 0, totalErrors = 0, companiesProcessed = 0;

        // UpdateFetchRunStatsAsync/ReportProgressAsync wrap scoped DbContexts — serialize them.
        using var progressLock = new SemaphoreSlim(1, 1);

        try
        {
            var client = _http.CreateClient("Lever");

            var parallelOpts = new ParallelOptions
            {
                MaxDegreeOfParallelism = _companyParallel,
                CancellationToken      = cancellationToken
            };

            _logger.LogInformation("[Lever] Processing {Total} boards (companyParallel={C})", tokens.Count, _companyParallel);

            await Parallel.ForEachAsync(tokens, parallelOpts, async (token, ct) =>
            {
                bool isH1B = CompanyNameNormalizer.IsH1BSponsored(token.CompanyName, sponsors);

                // Each parallel worker gets its own DbContext — EF Core is not thread-safe
                using var itemScope = _scopeFactory.CreateScope();
                var fetchDa = itemScope.ServiceProvider.GetRequiredService<IJobFetchDA>();

                try
                {
                    var (jobs, httpCode, tokenStatus, jobCount) =
                        await FetchCompanyJobsAsync(client, token, run.Id, isH1B, ct);

                    // null = transient error — leave status unchanged so it retries next run
                    if (tokenStatus != null)
                        await fetchDa.UpdateLeverTokenStatusAsync(token.Id, tokenStatus, httpCode, jobCount, ct);

                    if (jobs.Count > 0)
                    {
                        Interlocked.Add(ref totalFetched, jobs.Count);
                        var (ins, upd, err) = await fetchDa.BulkUpsertRawJobsAsync(jobs, ct);
                        Interlocked.Add(ref totalInserted, ins);
                        Interlocked.Add(ref totalUpdated,  upd);
                        Interlocked.Add(ref totalErrors,   err);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (FatalDatabaseException) { throw; }
                catch (Exception ex)
                {
                    // Transient error (timeout, DNS, network) — don't touch status, will retry next run
                    Interlocked.Increment(ref totalErrors);
                    _logger.LogWarning(ex, "[Lever] Transient error {Company} — status unchanged", token.CompanyName);
                }

                int done = Interlocked.Increment(ref companiesProcessed);

                if (done % 10 == 0 || done == tokens.Count)
                {
                    int fetched  = Volatile.Read(ref totalFetched);
                    int inserted = Volatile.Read(ref totalInserted);
                    int updated  = Volatile.Read(ref totalUpdated);
                    int errors   = Volatile.Read(ref totalErrors);
                    int pct      = (int)((double)done / tokens.Count * 90);

                    await progressLock.WaitAsync(ct);
                    try
                    {
                        using var statsScope = _scopeFactory.CreateScope();
                        var statsDa = statsScope.ServiceProvider.GetRequiredService<IJobFetchDA>();
                        await statsDa.UpdateFetchRunStatsAsync(run.Id, fetched, inserted, updated, 0, errors, done);
                        await progress.ReportProgressAsync(pct,
                            $"Companies: {done}/{tokens.Count} — Inserted: {inserted}, Updated: {updated}");
                    }
                    finally { progressLock.Release(); }
                }
            });

            using (var doneScope = _scopeFactory.CreateScope())
            {
                var doneDa = doneScope.ServiceProvider.GetRequiredService<IJobFetchDA>();
                await doneDa.CompleteFetchRunAsync(run.Id, "Completed");
            }
        }
        catch (OperationCanceledException)
        {
            using var failScope = _scopeFactory.CreateScope();
            var failDa = failScope.ServiceProvider.GetRequiredService<IJobFetchDA>();
            await failDa.CompleteFetchRunAsync(run.Id, "Cancelled", "Cancelled by user.");
            throw;
        }
        catch (Exception ex)
        {
            using var failScope = _scopeFactory.CreateScope();
            var failDa = failScope.ServiceProvider.GetRequiredService<IJobFetchDA>();
            await failDa.CompleteFetchRunAsync(run.Id, "Failed", ex.Message);
            throw;
        }

        _logger.LogInformation(
            "[Lever] Done — Companies={C} Fetched={F} Inserted={I} Updated={U} Errors={E}",
            companiesProcessed, totalFetched, totalInserted, totalUpdated, totalErrors);
    }

    // ── H1B sponsor check ─────────────────────────────────────────────────────

    private async Task<HashSet<string>> LoadSponsorsAsync(IJobFetchDA fetchDa, CancellationToken ct)
    {
        var cached = await _cache.GetAsync<List<string>>(SponsorCacheWarmupService.CacheKey, ct);
        if (cached is { Count: > 0 })
            return CompanyNameNormalizer.BuildSponsorSet(cached);

        var names = await fetchDa.GetH1BSponsorNamesAsync(ct);
        await _cache.SetAsync(SponsorCacheWarmupService.CacheKey, names, SponsorCacheWarmupService.CacheTtl, ct);
        _logger.LogInformation("[Lever] Reloaded {Count} H1B sponsors from DB into cache", names.Count);
        return CompanyNameNormalizer.BuildSponsorSet(names);
    }

    // ── Per-company fetch ─────────────────────────────────────────────────────

    // tokenStatus = null means transient error — caller must NOT update the token status
    private async Task<(List<ApiRawJob> jobs, short httpCode, string? tokenStatus, int jobCount)>
        FetchCompanyJobsAsync(
            HttpClient client,
            ApiLeverBoardToken token,
            string fetchRunId,
            bool isH1B,
            CancellationToken ct)
    {
        // Lever v0 returns full job details in a single call — no per-job follow-up needed
        var url = $"https://api.lever.co/v0/postings/{token.BoardToken}?mode=json";
        using var resp = await client.GetAsync(url, ct);

        var httpCode = (short)resp.StatusCode;

        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("[Lever] {Status} fetching {Token}", (int)resp.StatusCode, token.BoardToken);

            // 404 = board genuinely doesn't exist → permanent INVALID.
            if (resp.StatusCode == HttpStatusCode.NotFound)
                return ([], httpCode, "INVALID", 0);

            // 401/403/5xx/429/network → transient (WAF/rate-limit/Cloudflare).
            // Leave status unchanged so the board is retried next run.
            return ([], httpCode, null, 0);
        }

        var jobs = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);

        if (jobs.ValueKind != JsonValueKind.Array)
            return ([], httpCode, "INVALID", 0);

        var jobArray = jobs.EnumerateArray().ToList();

        if (jobArray.Count == 0)
            return ([], httpCode, "EMPTY", 0);

        _logger.LogInformation("[Lever] {Company}: {Count} jobs", token.CompanyName, jobArray.Count);

        var results = new List<ApiRawJob>(jobArray.Count);
        foreach (var j in jobArray)
        {
            var mapped = MapJob(j, token, fetchRunId, isH1B);
            if (mapped != null) results.Add(mapped);
        }

        return (results, httpCode, "VALID", jobArray.Count);
    }

    // ── Field mapping ─────────────────────────────────────────────────────────

    private static ApiRawJob? MapJob(JsonElement d, ApiLeverBoardToken token, string fetchRunId, bool isH1B)
    {
        if (!d.TryGetProperty("id", out var idEl)) return null;

        var sourceId = idEl.GetString();
        if (string.IsNullOrEmpty(sourceId)) return null;

        var jobTitle = d.TryGetProperty("text", out var t) ? t.GetString() ?? "Untitled" : "Untitled";
        var jobLink  = d.TryGetProperty("hostedUrl", out var hu) ? hu.GetString() : null;

        // ── Categories (location, commitment, department, team) ───────────────
        string? city = null, state = null, country = "US";
        var workType    = "OnSite";
        var jobWorkMode = "OnSite";
        string? department = null;
        bool isContract = false, isInternship = false;

        if (d.TryGetProperty("categories", out var cats))
        {
            if (cats.TryGetProperty("location", out var loc))
                ParseLocation(loc.GetString() ?? "", out city, out state, out country, out workType, out jobWorkMode);

            if (cats.TryGetProperty("department", out var dept))
                department = dept.GetString();

            if (cats.TryGetProperty("commitment", out var commit))
            {
                var c = commit.GetString() ?? "";
                isContract   = c.Contains("contract", StringComparison.OrdinalIgnoreCase)
                            || c.Contains("freelance", StringComparison.OrdinalIgnoreCase);
                isInternship = c.Contains("intern", StringComparison.OrdinalIgnoreCase);
            }
        }

        // Skip jobs explicitly located outside the US
        // (Lever convention: an empty/null country is treated as US — ParseLocation
        // initializes country="US" and only overwrites it for clearly-foreign strings.)
        if (!string.IsNullOrEmpty(country) && !UsLocationHelper.CountryVariants.Contains(country))
            return null;

        // ── Description ───────────────────────────────────────────────────────
        string? desc = null;
        if (d.TryGetProperty("description", out var descEl) && descEl.ValueKind == JsonValueKind.String)
            desc = descEl.GetString();

        // ── H1B ──────────────────────────────────────────────────────────────
        var isH1BFinal = isH1B ||
            (desc != null && (desc.Contains("h1b", StringComparison.OrdinalIgnoreCase) ||
                              desc.Contains("h-1b", StringComparison.OrdinalIgnoreCase) ||
                              desc.Contains("visa sponsor", StringComparison.OrdinalIgnoreCase)));

        // ── Post date (epoch milliseconds) ────────────────────────────────────
        DateTime? postDate = null;
        if (d.TryGetProperty("createdAt", out var ca) && ca.ValueKind == JsonValueKind.Number)
        {
            var epochMs = ca.GetInt64();
            postDate = DateTimeOffset.FromUnixTimeMilliseconds(epochMs).UtcDateTime;
        }

        return new ApiRawJob
        {
            PublicId        = Guid.NewGuid().ToString("N"),
            Source          = "Lever",
            SourceId        = sourceId,
            FetchRunId      = fetchRunId,
            JobTitle        = jobTitle,
            JobLink         = jobLink,
            JobDescription  = desc,
            City            = city,
            State           = state,
            Country         = country ?? "US",
            PostDate        = postDate,
            HoursBackPosted = postDate.HasValue ? (int)(DateTime.UtcNow - postDate.Value).TotalHours : null,
            SalaryMin       = null,
            SalaryMax       = null,
            SalaryType      = null,
            SalaryRangeText = null,
            SalaryCurrency  = "USD",
            WorkType        = workType,
            JobWorkMode     = jobWorkMode,
            Industry        = token.Industry ?? department,
            Skills          = department != null ? [department] : null,
            ApplyType       = "ExternalApply",
            CompanyName     = token.CompanyName,
            CompanyUrl      = token.BoardUrl,
            CompanyType     = "Private",
            IsH1BSponsored  = isH1BFinal,
            IsSponsored     = isH1BFinal,
            IsW2            = null,
            IsC2C           = false,
            IsContractJob   = isContract,
            IsFreelanceJob  = false,
            IsPrimeVendor   = false,
            IsStaffing      = false,
            IsStartupJob    = false,
            IsNonProfitJob  = false,
            IsUniversityJob = isInternship,
            Status          = true,
            CreatedOn       = DateTime.UtcNow,
            UpdatedOn       = DateTime.UtcNow
        };
    }

    // ── Location parsing ──────────────────────────────────────────────────────
    // US-location reference data lives in UsLocationHelper (shared with all ATS handlers).

    private static void ParseLocation(
        string raw, out string? city, out string? state,
        out string? country, out string workType, out string jobWorkMode)
    {
        city        = null;
        state       = null;
        country     = "US";
        workType    = "OnSite";
        jobWorkMode = "OnSite";

        if (string.IsNullOrWhiteSpace(raw)) return;

        var lower = raw.ToLowerInvariant();
        if (lower.Contains("remote"))
        {
            workType    = "Remote";
            jobWorkMode = "Remote";
            if (!lower.Contains(',')) return;
        }
        else if (lower.Contains("hybrid"))
        {
            workType    = "Hybrid";
            jobWorkMode = "Hybrid";
        }

        var parts = raw.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length >= 1) city = parts[0];

        if (parts.Length >= 2)
        {
            var part2 = parts[1].Trim();

            if (UsLocationHelper.StateAbbrs.Contains(part2))
                state = part2;                                    // exact US state abbr (AL, CA, NY…)
            else if (UsLocationHelper.StateNames.Contains(part2))
                state = part2;                                    // full US state name (Texas, California…)
            else if (UsLocationHelper.CountryVariants.Contains(part2))
                { /* country already "US" */ }                    // USA, U.S., United States…
            else if (part2.Length > 2)
                country = part2;                                  // anything else → treat as foreign country
            // 1-2 char tokens that aren't a known US abbr are ignored
        }

        if (parts.Length >= 3)
        {
            var part3 = parts[2].Trim();
            country = UsLocationHelper.CountryVariants.Contains(part3) ? "US" : part3;
        }
    }
}
