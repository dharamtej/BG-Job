// CareerPandaBL/Background/Handlers/BambooHrJobsJobHandler.cs
// SOURCE : BambooHR Public Careers API (no auth required)
// FLOW   : Load board tokens from api.bamboohr_board_tokens (status != INVALID)
//          → GET https://{slug}.bamboohr.com/careers/list  (full list in one call)
//          → Update token status (VALID / EMPTY / INVALID)
//          → Upsert into api.raw_jobs (US-only)
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

public class BambooHrJobsJobHandler : IJobHandler
{
    public string JobType => "BambooHrJobs";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _http;
    private readonly ICacheService _cache;
    private readonly ILogger<BambooHrJobsJobHandler> _logger;

    // How many boards are processed concurrently — tunable via appsettings.
    private readonly int _companyParallel;

    public BambooHrJobsJobHandler(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        ICacheService cacheService,
        IConfiguration configuration,
        ILogger<BambooHrJobsJobHandler> logger)
    {
        _scopeFactory = scopeFactory;
        _http         = httpClientFactory;
        _cache        = cacheService;
        _logger       = logger;

        _companyParallel = Math.Max(1,
            configuration.GetSection("JobApiSettings").GetValue("BambooHrCompanyParallel", 12));
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
            JobCategory      = "BambooHrJobs",
            ApiSource        = "BambooHR",
            Status           = "Running",
            StartedAt        = DateTime.UtcNow,
            CreatedById      = request.UserId
        };

        HashSet<string> sponsors;
        List<ApiBambooHrBoardToken> tokens;
        using (var setupScope = _scopeFactory.CreateScope())
        {
            var setupDa = setupScope.ServiceProvider.GetRequiredService<IJobFetchDA>();
            await setupDa.CreateFetchRunAsync(run);

            sponsors = await LoadSponsorsAsync(setupDa, cancellationToken);
            _logger.LogInformation("[BambooHR] Loaded {Count} H1B sponsors for matching", sponsors.Count);

            tokens = await setupDa.GetActiveBambooHrTokensAsync(cancellationToken);
            _logger.LogInformation("[BambooHR] Loaded {Count} board tokens", tokens.Count);
        }

        // Counters shared across parallel workers — mutate only via Interlocked.
        int totalFetched = 0, totalInserted = 0, totalUpdated = 0, totalErrors = 0, companiesProcessed = 0;

        // UpdateFetchRunStatsAsync/ReportProgressAsync wrap scoped DbContexts — serialize them.
        using var progressLock = new SemaphoreSlim(1, 1);

        try
        {
            var client = _http.CreateClient("BambooHR");
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "CareerPanda/1.0 jobs-aggregator");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");

            var parallelOpts = new ParallelOptions
            {
                MaxDegreeOfParallelism = _companyParallel,
                CancellationToken      = cancellationToken
            };

            _logger.LogInformation("[BambooHR] Processing {Total} boards (companyParallel={C})", tokens.Count, _companyParallel);

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

                    if (tokenStatus != null)
                        await fetchDa.UpdateBambooHrTokenStatusAsync(token.Id, tokenStatus, httpCode, jobCount, ct);

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
                    Interlocked.Increment(ref totalErrors);
                    _logger.LogWarning(ex, "[BambooHR] Transient error {Company} — status unchanged", token.CompanyName);
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
            "[BambooHR] Done — Companies={C} Fetched={F} Inserted={I} Updated={U} Errors={E}",
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
        return CompanyNameNormalizer.BuildSponsorSet(names);
    }

    // ── Per-company fetch ─────────────────────────────────────────────────────

    private async Task<(List<ApiRawJob> jobs, short httpCode, string? tokenStatus, int jobCount)>
        FetchCompanyJobsAsync(
            HttpClient client,
            ApiBambooHrBoardToken token,
            string fetchRunId,
            bool isH1B,
            CancellationToken ct)
    {
        var url = $"https://{token.BoardToken}.bamboohr.com/careers/list";
        using var resp = await client.GetAsync(url, ct);
        var httpCode = (short)resp.StatusCode;

        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("[BambooHR] {Status} fetching {Token}", (int)resp.StatusCode, token.BoardToken);

            if (resp.StatusCode is HttpStatusCode.NotFound
                                or HttpStatusCode.Forbidden
                                or HttpStatusCode.Unauthorized
                                or HttpStatusCode.BadRequest)
                return ([], httpCode, "INVALID", 0);

            return ([], httpCode, null, 0);
        }

        JsonElement doc;
        try
        {
            doc = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        }
        catch
        {
            return ([], httpCode, "INVALID", 0);
        }

        if (doc.ValueKind != JsonValueKind.Object
            || !doc.TryGetProperty("result", out var resultEl)
            || resultEl.ValueKind != JsonValueKind.Array)
            return ([], httpCode, "INVALID", 0);

        var jobArray = resultEl.EnumerateArray().ToList();
        if (jobArray.Count == 0)
            return ([], httpCode, "EMPTY", 0);

        var results = new List<ApiRawJob>(jobArray.Count);
        foreach (var j in jobArray)
        {
            var mapped = MapJob(j, token, fetchRunId, isH1B);
            if (mapped != null) results.Add(mapped);
        }

        return (results, httpCode, results.Count > 0 ? "VALID" : "EMPTY", jobArray.Count);
    }

    // ── Field mapping ─────────────────────────────────────────────────────────

    private static ApiRawJob? MapJob(JsonElement d, ApiBambooHrBoardToken token, string fetchRunId, bool isH1B)
    {
        if (!d.TryGetProperty("id", out var idEl)) return null;
        var sourceId = idEl.ValueKind switch
        {
            JsonValueKind.Number => idEl.GetInt64().ToString(),
            JsonValueKind.String => idEl.GetString(),
            _ => null
        };
        if (string.IsNullOrEmpty(sourceId)) return null;

        // Skip non-open postings if status is exposed
        if (d.TryGetProperty("jobOpeningStatus", out var jos) && jos.ValueKind == JsonValueKind.String)
        {
            var status = jos.GetString() ?? "";
            if (status.Equals("Closed", StringComparison.OrdinalIgnoreCase)
                || status.Equals("Filled", StringComparison.OrdinalIgnoreCase))
                return null;
        }

        var jobTitle = d.TryGetProperty("jobOpeningName", out var t) ? t.GetString() ?? "Untitled" : "Untitled";

        // Build job link — prefer jobOpeningShareUrl, else construct from slug + id
        string? jobLink = null;
        if (d.TryGetProperty("jobOpeningShareUrl", out var su) && su.ValueKind == JsonValueKind.String)
            jobLink = su.GetString();
        if (string.IsNullOrWhiteSpace(jobLink))
            jobLink = $"https://{token.BoardToken}.bamboohr.com/careers/{sourceId}";

        // ── Location ──────────────────────────────────────────────────────────
        // Default country to null (NOT "US") — a missing country must not silently
        // satisfy the US filter. The filter requires a positive signal: a recognized
        // country string or a US state.
        string? city = null, state = null, country = null;
        var workType    = "OnSite";
        var jobWorkMode = "OnSite";

        if (d.TryGetProperty("locationCity", out var cityEl) && cityEl.ValueKind == JsonValueKind.String)
            city = cityEl.GetString();
        if (d.TryGetProperty("locationState", out var stateEl) && stateEl.ValueKind == JsonValueKind.String)
            state = stateEl.GetString();
        if (d.TryGetProperty("locationCountry", out var ctryEl) && ctryEl.ValueKind == JsonValueKind.String)
            country = ctryEl.GetString();

        // Remote flag — BambooHR exposes "isRemote" sometimes; also infer from city/state
        bool isRemoteFlag = d.TryGetProperty("isRemote", out var rem) && rem.ValueKind == JsonValueKind.True;
        if (isRemoteFlag
            || string.Equals(city, "Remote", StringComparison.OrdinalIgnoreCase)
            || string.Equals(state, "Remote", StringComparison.OrdinalIgnoreCase))
        {
            workType    = "Remote";
            jobWorkMode = "Remote";
        }

        // US-only filter (matches Lever/Ashby behavior)
        if (!UsLocationHelper.IsUs(country, state)) return null;
        country = "US";

        // ── Department ────────────────────────────────────────────────────────
        string? department = null;
        if (d.TryGetProperty("departmentLabel", out var dept) && dept.ValueKind == JsonValueKind.String)
            department = dept.GetString();

        // ── Employment type ───────────────────────────────────────────────────
        bool isContract = false, isInternship = false;
        if (d.TryGetProperty("employmentStatusLabel", out var et) && et.ValueKind == JsonValueKind.String)
        {
            var s = et.GetString() ?? "";
            if (s.Contains("Contract",  StringComparison.OrdinalIgnoreCase) ||
                s.Contains("Temporary", StringComparison.OrdinalIgnoreCase))
                isContract = true;
            if (s.Contains("Intern", StringComparison.OrdinalIgnoreCase))
                isInternship = true;
        }

        // ── Post date ─────────────────────────────────────────────────────────
        DateTime? postDate = null;
        if (d.TryGetProperty("datePosted", out var dp) && dp.ValueKind == JsonValueKind.String
            && DateTime.TryParse(dp.GetString(), out var pdt))
            postDate = pdt.ToUniversalTime();

        return new ApiRawJob
        {
            PublicId        = Guid.NewGuid().ToString("N"),
            Source          = "BambooHR",
            SourceId        = sourceId,
            FetchRunId      = fetchRunId,
            JobTitle        = jobTitle,
            JobLink         = jobLink,
            JobDescription  = null,   // BambooHR list endpoint doesn't include descriptions
            City            = city,
            State           = state,
            Country         = country,
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
            IsH1BSponsored  = isH1B,
            IsSponsored     = isH1B,
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

}
