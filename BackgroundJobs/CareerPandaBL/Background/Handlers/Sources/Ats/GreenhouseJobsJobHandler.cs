// CareerPandaBL/Background/Handlers/GreenhouseJobsJobHandler.cs
// SOURCE : Greenhouse Job Board API (public, no auth required)
// FLOW   : Load board tokens from api.greenhouse_board_tokens
//          → GET /v1/boards/{token}/jobs        (job list with IDs)
//          → GET /v1/boards/{token}/jobs/{id}?content=true  (full details)
//          → Upsert into api.raw_jobs + api.companies
using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using CareerPanda.DataAccess.DA;
using CareerPanda.DataAccess.Entities.Api;
using CareerPanda.Framework.Cache;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using static CareerPanda.BL.Background.Handlers.JobFetchHelpers;

namespace CareerPanda.BL.Background.Handlers;

public partial class GreenhouseJobsJobHandler : IJobHandler
{
    public string JobType => "GreenhouseJobs";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _http;
    private readonly ICacheService _cache;
    private readonly ILogger<GreenhouseJobsJobHandler> _logger;

    // Bounded parallelism — tunable via appsettings without redeploy.
    // CompanyParallel: how many boards are processed concurrently.
    // JobDetailParallel: how many job-detail calls run concurrently within one board.
    private readonly int _companyParallel;
    private readonly int _jobDetailParallel;

    public GreenhouseJobsJobHandler(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        ICacheService cacheService,
        IConfiguration configuration,
        ILogger<GreenhouseJobsJobHandler> logger)
    {
        _scopeFactory = scopeFactory;
        _http         = httpClientFactory;
        _cache        = cacheService;
        _logger       = logger;

        var gh = configuration.GetSection("JobApiSettings");
        _companyParallel   = Math.Max(1, gh.GetValue("GreenhouseCompanyParallel",   12));
        _jobDetailParallel = Math.Max(1, gh.GetValue("GreenhouseJobDetailParallel",  6));
    }

    public async Task ExecuteAsync(
        JobWorkRequest request,
        IJobProgressReporter progress,
        CancellationToken cancellationToken)
    {
        // Create fetch-run row + load reference data on a short-lived outer scope.
        var run = new ApiJobFetchRun
        {
            Id               = request.JobId,
            BackgroundTaskId = request.JobId,
            JobCategory      = "GreenhouseJobs",
            ApiSource        = "Greenhouse",
            Status           = "Running",
            StartedAt        = DateTime.UtcNow,
            CreatedById      = request.UserId
        };

        HashSet<string> sponsors;
        List<ApiGreenhouseBoardToken> tokens;
        using (var setupScope = _scopeFactory.CreateScope())
        {
            var setupDa = setupScope.ServiceProvider.GetRequiredService<IJobFetchDA>();
            await setupDa.CreateFetchRunAsync(run);

            // Load H1B sponsor set from Redis cache (same source as all other handlers)
            sponsors = await LoadSponsorsAsync(setupDa, cancellationToken);
            _logger.LogInformation("[Greenhouse] Loaded {Count} H1B sponsors for matching", sponsors.Count);

            tokens = await setupDa.GetValidGreenhouseTokensAsync(cancellationToken);
            _logger.LogInformation("[Greenhouse] Loaded {Count} board tokens", tokens.Count);
        }

        // Counters shared across parallel workers — mutate only via Interlocked.
        int totalFetched = 0, totalInserted = 0, totalUpdated = 0, totalErrors = 0, companiesProcessed = 0;

        // UpdateFetchRunStatsAsync/ReportProgressAsync wrap scoped DbContexts — serialize them.
        using var progressLock = new SemaphoreSlim(1, 1);

        try
        {
            var client = _http.CreateClient();
            client.DefaultRequestHeaders.Add("User-Agent", "CareerPanda/1.0 jobs-aggregator");

            var parallelOpts = new ParallelOptions
            {
                MaxDegreeOfParallelism = _companyParallel,
                CancellationToken      = cancellationToken
            };

            _logger.LogInformation(
                "[Greenhouse] Processing {Total} boards (companyParallel={C}, jobDetailParallel={J})",
                tokens.Count, _companyParallel, _jobDetailParallel);

            await Parallel.ForEachAsync(tokens, parallelOpts, async (token, ct) =>
            {
                // Resolve H1B flag once per company — all jobs from this board share the same employer
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
                        await fetchDa.UpdateGreenhouseTokenStatusAsync(token.Id, tokenStatus, httpCode, jobCount, ct);

                    if (jobs.Count > 0)
                    {
                        Interlocked.Add(ref totalFetched, jobs.Count);
                        var (ins, upd, err) = await fetchDa.BulkUpsertRawJobsAsync(JobValidationGate.FilterValid(jobs, _logger, "[Greenhouse]"), ct);
                        Interlocked.Add(ref totalInserted, ins);
                        Interlocked.Add(ref totalUpdated,  upd);
                        Interlocked.Add(ref totalErrors,   err);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (FatalDatabaseException) { throw; }
                catch (Exception ex)
                {
                    // Transient error (timeout, DNS) — don't touch status, will retry next run
                    Interlocked.Increment(ref totalErrors);
                    _logger.LogWarning(ex, "[Greenhouse] Transient error {Company} — status unchanged", token.CompanyName);
                }

                int done = Interlocked.Increment(ref companiesProcessed);

                // Persist stats + report progress every 10 companies (and on the last one)
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
                        await statsDa.UpdateFetchRunStatsAsync(
                            run.Id, fetched, inserted, updated, 0, errors, done);

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
            "[Greenhouse] Done — Companies={C} Fetched={F} Inserted={I} Updated={U} Errors={E}",
            companiesProcessed, totalFetched, totalInserted, totalUpdated, totalErrors);
    }

    // ── H1B sponsor check ─────────────────────────────────────────────────────

    private async Task<HashSet<string>> LoadSponsorsAsync(IJobFetchDA fetchDa, CancellationToken ct)
    {
        var cached = await _cache.GetAsync<List<string>>(SponsorCacheWarmupService.CacheKey, ct);
        if (cached is { Count: > 0 })
            return CompanyNameNormalizer.BuildSponsorSet(cached);

        // Cache miss — reload from DB and re-cache
        var names = await fetchDa.GetH1BSponsorNamesAsync(ct);
        await _cache.SetAsync(SponsorCacheWarmupService.CacheKey, names, SponsorCacheWarmupService.CacheTtl, ct);
        _logger.LogInformation("[Greenhouse] Reloaded {Count} H1B sponsors from DB into cache", names.Count);
        return CompanyNameNormalizer.BuildSponsorSet(names);
    }

    // ── Per-company fetch: board info → job list → job details ──────────────
    // Returns tokenStatus=null to tell caller NOT to update the token row (transient error).
    // Only 404 on the board-info endpoint is a definitive "board doesn't exist" signal.

    private async Task<(List<ApiRawJob> jobs, short httpCode, string? tokenStatus, int jobCount)>
        FetchCompanyJobsAsync(
            HttpClient client,
            ApiGreenhouseBoardToken token,
            string fetchRunId,
            bool isH1B,
            CancellationToken ct)
    {
        // ── Step 1: board info (/v1/boards/{token}) ──────────────────────────
        // This is the ONLY reliable signal for whether a board truly exists.
        // 404 here = board gone. 403/429/5xx = transient, retry next run.
        var boardUrl = $"https://boards-api.greenhouse.io/v1/boards/{token.BoardToken}";
        using var boardResp = await RetryGetAsync(client, boardUrl, ct);
        var httpCode = (short)boardResp.StatusCode;

        if (!boardResp.IsSuccessStatusCode)
        {
            var sc = boardResp.StatusCode;
            if (sc is System.Net.HttpStatusCode.Forbidden
                    or System.Net.HttpStatusCode.NotFound
                    or System.Net.HttpStatusCode.TooManyRequests)
                _logger.LogDebug("[Greenhouse] {Status} on board-info for {Token}", (int)sc, token.BoardToken);
            else
                _logger.LogWarning("[Greenhouse] {Status} on board-info for {Token}", (int)sc, token.BoardToken);

            // Only 404 is definitive — board genuinely deleted or never existed
            if (sc == System.Net.HttpStatusCode.NotFound)
                return ([], httpCode, "INVALID", 0);

            // All other errors are transient (WAF, rate-limit, network) — retry next run
            return ([], httpCode, null, 0);
        }

        // Extract company enrichment data from board info (logo, name correction, website)
        string? companyLogoFromBoard = null;
        try
        {
            var boardJson = await ReadJsonAsync(boardResp.Content, ct);
            if (boardJson.TryGetProperty("logo", out var logo) && logo.ValueKind == JsonValueKind.String)
                companyLogoFromBoard = logo.GetString();
        }
        catch { /* board info parse failure is non-fatal — continue to jobs */ }

        // ── Step 2: job list (/v1/boards/{token}/jobs) ───────────────────────
        var listUrl = $"https://boards-api.greenhouse.io/v1/boards/{token.BoardToken}/jobs";
        using var listResp = await RetryGetAsync(client, listUrl, ct);

        if (!listResp.IsSuccessStatusCode)
        {
            // Board exists (step 1 passed) but job list failed — transient
            _logger.LogDebug("[Greenhouse] {Status} on job-list for {Token}", (short)listResp.StatusCode, token.BoardToken);
            return ([], (short)listResp.StatusCode, null, 0);
        }

        List<long> jobIds;
        try
        {
            var listJson = await ReadJsonAsync(listResp.Content, ct);

            // If `jobs` property is missing it's a transient/malformed response — NOT a definitive INVALID
            // (Greenhouse sometimes returns error JSON with 200 status under rate-limit pressure)
            if (!listJson.TryGetProperty("jobs", out var jobsEl) || jobsEl.ValueKind != JsonValueKind.Array)
            {
                _logger.LogDebug("[Greenhouse] No 'jobs' property in 200 response for {Token} — treating as transient", token.BoardToken);
                return ([], httpCode, null, 0);
            }

            jobIds = jobsEl.EnumerateArray()
                .Where(j => j.TryGetProperty("id", out _))
                .Select(j => j.GetProperty("id").GetInt64())
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Greenhouse] Job-list JSON parse failed for {Token} — transient", token.BoardToken);
            return ([], httpCode, null, 0);
        }

        if (jobIds.Count == 0)
            return ([], httpCode, "EMPTY", 0);

        _logger.LogInformation("[Greenhouse] {Company}: {Count} jobs to fetch detail", token.CompanyName, jobIds.Count);

        // ── Step 3: job details (/v1/boards/{token}/jobs/{id}?content=true) ──
        var results    = new ConcurrentBag<ApiRawJob>();
        int detailFails = 0;
        var detailOpts = new ParallelOptions
        {
            MaxDegreeOfParallelism = _jobDetailParallel,
            CancellationToken      = ct
        };

        await Parallel.ForEachAsync(jobIds, detailOpts, async (jobId, innerCt) =>
        {
            try
            {
                var detailUrl = $"https://boards-api.greenhouse.io/v1/boards/{token.BoardToken}/jobs/{jobId}?content=true";
                using var detailResp = await RetryGetAsync(client, detailUrl, innerCt);

                if (!detailResp.IsSuccessStatusCode)
                {
                    Interlocked.Increment(ref detailFails);
                    return;
                }

                var detail = await ReadJsonAsync(detailResp.Content, innerCt);
                // Pass logo from board-info so it enriches the company record
                var mapped = MapJob(detail, token, fetchRunId, isH1B, companyLogoFromBoard);
                if (mapped != null) results.Add(mapped);
            }
            catch (OperationCanceledException) when (innerCt.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                Interlocked.Increment(ref detailFails);
                _logger.LogWarning(ex, "[Greenhouse] Detail fetch failed job {JobId} for {Token}", jobId, token.BoardToken);
            }
        });

        var list = results.ToList();

        // If ALL detail fetches failed (rate-limit burst), treat as transient — do not mark EMPTY
        // so the next run retries the full detail sweep. Only mark EMPTY if the list API
        // confirmed zero jobs. Mark VALID only when we actually got job records back.
        if (list.Count == 0 && detailFails == jobIds.Count)
        {
            _logger.LogWarning("[Greenhouse] All {N} detail calls failed for {Token} — transient, status unchanged",
                jobIds.Count, token.BoardToken);
            return ([], httpCode, null, 0);
        }

        return (list, httpCode, list.Count > 0 ? "VALID" : "EMPTY", jobIds.Count);
    }

    // ── Field mapping ─────────────────────────────────────────────────────────

    private static ApiRawJob? MapJob(JsonElement d, ApiGreenhouseBoardToken token, string fetchRunId, bool isH1B,
        string? boardLogoUrl = null)
    {
        if (!d.TryGetProperty("id", out var idEl)) return null;

        var sourceId = idEl.GetInt64().ToString();
        var jobTitle = d.TryGetProperty("title", out var t) ? t.GetString() ?? "Untitled" : "Untitled";
        var jobLink  = d.TryGetProperty("absolute_url", out var au) ? au.GetString() : null;

        // ── Location ──────────────────────────────────────────────────────────
        string? city = null, state = null, country = "US";
        var workType    = "OnSite";
        var jobWorkMode = "OnSite";

        if (d.TryGetProperty("location", out var loc) && loc.TryGetProperty("name", out var locName))
            UsLocationHelper.ParseLocation(locName.GetString() ?? "", out city, out state, out country, out workType, out jobWorkMode);

        // Reject non-US jobs — ParseLocation defaults to "US" so only explicitly foreign strings fail
        if (!UsLocationHelper.NormalizeToUs(ref country, ref state)) return null;

        // ── Department (departments[] → job_domain / job_sub_domain) ──────────
        string? department = null, subDepartment = null;
        if (d.TryGetProperty("departments", out var depts) && depts.ValueKind == JsonValueKind.Array)
        {
            var deptNames = depts.EnumerateArray()
                .Where(x => x.TryGetProperty("name", out _))
                .Select(x => x.GetProperty("name").GetString()!)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToArray();
            if (deptNames.Length > 0) department    = deptNames[0];
            if (deptNames.Length > 1) subDepartment = deptNames[1];
        }

        // ── Description (HTML → plain text) ──────────────────────────────────
        string? desc = null;
        if (d.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
            desc = StripHtml(content.GetString());

        // Also check description text for H1B keywords in case company name didn't match
        // Per-job negation overrides the company-level sponsor flag — a specific role may
        // require US citizenship even if the company generally sponsors H1B.
        var visaNegation = ContainsAny(desc,
            "do not sponsor", "does not sponsor", "no sponsorship", "unable to sponsor",
            "cannot sponsor", "will not sponsor", "no h-1b", "no h1b",
            "must be authorized to work", "must have work authorization",
            "authorized to work in the us", "authorized to work in the united states");

        var isH1BFinal  = !visaNegation && (isH1B ||
            (desc != null && (desc.Contains("h1b", StringComparison.OrdinalIgnoreCase) ||
                              desc.Contains("h-1b", StringComparison.OrdinalIgnoreCase) ||
                              desc.Contains("visa sponsor", StringComparison.OrdinalIgnoreCase))));
        var isOptCpt    = !visaNegation && ContainsAny(desc, " opt ", "opt/cpt", "stem opt", "opt extension", "f-1 visa", " cpt ");
        var isTnVisa    = !visaNegation && ContainsAny(desc, "tn visa", "tn-1", "tn-2", "usmca", "nafta visa");
        var isE3Visa    = !visaNegation && ContainsAny(desc, "e-3", "e3 visa", "e-3 visa");
        var isJ1Visa    = !visaNegation && ContainsAny(desc, "j-1", "j1 visa", "j-1 visa", "exchange visitor");
        var isGreenCard = !visaNegation && ContainsAny(desc, "green card", "gc sponsor", "perm filing", "eb-2", "eb-3", "labor certification");

        // ── Employment type (Greenhouse API has no field — derive from title + description) ──
        bool isContract   = ContainsAny(desc,   "contract role", "contract position", "contractor", " on contract") ||
                            ContainsAny(jobTitle, "contract", "contractor");
        bool isInternship = ContainsAny(desc,   "internship", "intern role", "intern position") ||
                            ContainsAny(jobTitle, "intern");
        bool isPartTime   = ContainsAny(desc,   "part-time", "part time") ||
                            ContainsAny(jobTitle, "part-time", "part time");
        // Always derive ContractType — default is FullTime when no signal detected
        string contractType = JobValidationGate.DeriveContractType(isContract, isInternship, isPartTime);

        // ── Salary ────────────────────────────────────────────────────────────
        decimal? salMin = null, salMax = null;
        string? salCurrency = "USD", salType = null, salRangeText = null;
        if (d.TryGetProperty("pay_input_ranges", out var pay) && pay.ValueKind == JsonValueKind.Array)
        {
            var first = pay.EnumerateArray().FirstOrDefault();
            if (first.ValueKind == JsonValueKind.Object)
            {
                if (first.TryGetProperty("min_cents", out var minC) && minC.ValueKind == JsonValueKind.Number)
                    salMin = minC.GetDecimal() / 100m;
                if (first.TryGetProperty("max_cents", out var maxC) && maxC.ValueKind == JsonValueKind.Number)
                    salMax = maxC.GetDecimal() / 100m;
                if (first.TryGetProperty("currency_type", out var cur))
                    salCurrency = cur.GetString() ?? "USD";
                salType = "Annual";
                if (salMin.HasValue && salMax.HasValue)
                    salRangeText = $"${salMin:N0}–${salMax:N0}/yr";
            }
        }

        // ── Post date ─────────────────────────────────────────────────────────
        DateTime? postDate = null;
        if (d.TryGetProperty("updated_at", out var ua) && DateTime.TryParse(ua.GetString(), out var dt))
            postDate = dt.ToUniversalTime();

        return new ApiRawJob
        {
            PublicId        = Guid.NewGuid().ToString("N"),
            Source          = "Greenhouse",
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
            SalaryMin       = salMin,
            SalaryMax       = salMax,
            SalaryType      = salType,
            SalaryRangeText = salRangeText,
            SalaryCurrency  = salCurrency,
            WorkType        = workType,
            JobWorkMode     = jobWorkMode,
            Industry        = token.Industry,
            JobDomain       = department,
            JobSubDomain    = subDepartment,
            Skills          = null,   // real skills extracted from the description in NormalizeJobs
            ApplyType       = "ExternalApply",
            CompanyName     = token.CompanyName,
            CompanyLogoUrl  = boardLogoUrl,
            CompanyUrl      = token.BoardUrl,
            CompanyType     = "Private",
            IsH1BSponsored  = isH1BFinal,
            IsOptCpt        = isOptCpt,
            IsTnVisa        = isTnVisa,
            IsE3Visa        = isE3Visa,
            IsJ1Visa        = isJ1Visa,
            IsGreenCard     = isGreenCard,
            IsSponsored     = isH1BFinal || isOptCpt || isTnVisa || isE3Visa || isJ1Visa || isGreenCard,
            IsW2            = null,
            IsC2C           = false,
            IsContractJob   = isContract,
            IsFreelanceJob  = false,
            IsPrimeVendor   = false,
            IsStaffing      = false,
            ContractType    = contractType,
            JobLevel        = NormalizeJobLevel(jobTitle),
            IsStartupJob    = false,
            IsNonProfitJob  = false,
            IsUniversityJob = false,
            Status          = true,
            CreatedOn       = DateTime.UtcNow,
            UpdatedOn       = DateTime.UtcNow
        };
    }

    // ParseLocation delegated to UsLocationHelper — single source of truth for all handlers.

    // ── Retry helper with exponential back-off ────────────────────────────────
    // Retries on 429 (rate-limit) and 503 (transient overload) only.
    // Other status codes are returned as-is so callers decide what they mean.
    //
    // Back-off schedule (maxRetries = 3):
    //   attempt 1 → wait 2s ± 250ms jitter
    //   attempt 2 → wait 4s ± 250ms jitter
    //   attempt 3 → wait 8s ± 250ms jitter
    //   attempt 4 → return last response (caller handles)

    private static readonly Random _jitter = new();
    private const int MaxRetries = 3;

    private static async Task<HttpResponseMessage> RetryGetAsync(
        HttpClient client, string url, CancellationToken ct)
    {
        HttpResponseMessage? resp = null;
        for (int attempt = 0; attempt <= MaxRetries; attempt++)
        {
            resp?.Dispose();
            resp = await client.GetAsync(url, ct);

            if ((int)resp.StatusCode != 429 && (int)resp.StatusCode != 503)
                return resp;   // success or a non-retryable error

            if (attempt == MaxRetries) break;

            // Honour Retry-After header if present (Greenhouse/Cloudflare set it on 429)
            int retryAfterMs = 0;
            if (resp.Headers.RetryAfter?.Delta is { } delta)
                retryAfterMs = (int)delta.TotalMilliseconds;
            else if (resp.Headers.RetryAfter?.Date is { } date)
                retryAfterMs = (int)(date - DateTimeOffset.UtcNow).TotalMilliseconds;

            resp.Dispose();
            resp = null;

            int backoffMs = retryAfterMs > 0
                ? retryAfterMs + _jitter.Next(0, 500)          // use server hint + small jitter
                : (int)Math.Pow(2, attempt + 1) * 1000 + _jitter.Next(-250, 250);  // exponential

            await Task.Delay(Math.Max(500, backoffMs), ct);
        }

        // Return last response (429/503) so caller can record it
        return resp!;
    }
}
