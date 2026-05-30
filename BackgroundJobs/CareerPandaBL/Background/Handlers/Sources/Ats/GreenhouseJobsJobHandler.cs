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

    // ── Per-company fetch: list jobs → detail each job ───────────────────────
    // tokenStatus = null means transient error — caller must NOT update the token status

    private async Task<(List<ApiRawJob> jobs, short httpCode, string? tokenStatus, int jobCount)>
        FetchCompanyJobsAsync(
            HttpClient client,
            ApiGreenhouseBoardToken token,
            string fetchRunId,
            bool isH1B,
            CancellationToken ct)
    {
        // Step 1: get job list (id + basic fields) — no auth required
        var listUrl = $"https://boards-api.greenhouse.io/v1/boards/{token.BoardToken}/jobs";
        using var listResp = await client.GetAsync(listUrl, ct);
        var httpCode = (short)listResp.StatusCode;

        if (!listResp.IsSuccessStatusCode)
        {
            // 403/404/429 are expected for stale or rate-limited tokens — log at Debug to avoid
            // flooding Railway's 500 logs/sec cap when hundreds of invalid boards run in parallel.
            var sc = listResp.StatusCode;
            if (sc is System.Net.HttpStatusCode.Forbidden
                    or System.Net.HttpStatusCode.NotFound
                    or System.Net.HttpStatusCode.TooManyRequests)
                _logger.LogDebug("[Greenhouse] {Status} fetching job list for {Token}",
                    (int)sc, token.BoardToken);
            else
                _logger.LogWarning("[Greenhouse] {Status} fetching job list for {Token}",
                    (int)sc, token.BoardToken);

            // Definitive failure — board genuinely doesn't exist.
            if (sc == System.Net.HttpStatusCode.NotFound)
                return ([], httpCode, "INVALID", 0);

            // 401/403 used to be marked INVALID forever, but they're frequently caused by
            // Cloudflare WAF / rate-limit under burst load — treat as transient now.
            // 5xx / 429 / network errors are also transient — status unchanged so it retries.
            return ([], httpCode, null, 0);
        }

        var listJson = await listResp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        if (!listJson.TryGetProperty("jobs", out var jobsEl) || jobsEl.ValueKind != JsonValueKind.Array)
            return ([], httpCode, "INVALID", 0);

        var jobIds = jobsEl.EnumerateArray()
            .Where(j => j.TryGetProperty("id", out _))
            .Select(j => j.GetProperty("id").GetInt64())
            .ToList();

        if (jobIds.Count == 0)
            return ([], httpCode, "EMPTY", 0);

        _logger.LogInformation("[Greenhouse] {Company}: {Count} jobs to fetch", token.CompanyName, jobIds.Count);

        // Step 2: fetch full detail for each job — bounded concurrency, no artificial delay
        var results = new ConcurrentBag<ApiRawJob>();
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
                using var detailResp = await client.GetAsync(detailUrl, innerCt);

                if (!detailResp.IsSuccessStatusCode) return;

                var detail = await detailResp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: innerCt);
                var mapped = MapJob(detail, token, fetchRunId, isH1B);
                if (mapped != null) results.Add(mapped);
            }
            catch (OperationCanceledException) when (innerCt.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Greenhouse] Failed fetching job {JobId} for {Token}", jobId, token.BoardToken);
            }
        });

        var list = results.ToList();
        return (list, httpCode, list.Count > 0 ? "VALID" : "EMPTY", jobIds.Count);
    }

    // ── Field mapping ─────────────────────────────────────────────────────────

    private static ApiRawJob? MapJob(JsonElement d, ApiGreenhouseBoardToken token, string fetchRunId, bool isH1B)
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
            ParseLocation(locName.GetString() ?? "", out city, out state, out country, out workType, out jobWorkMode);

        // ── Department / Industry ─────────────────────────────────────────────
        string? department = null;
        string[]? skills   = null;
        if (d.TryGetProperty("departments", out var depts) && depts.ValueKind == JsonValueKind.Array)
        {
            var deptNames = depts.EnumerateArray()
                .Where(x => x.TryGetProperty("name", out _))
                .Select(x => x.GetProperty("name").GetString()!)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToArray();
            if (deptNames.Length > 0) { department = deptNames[0]; skills = deptNames; }
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
            Industry        = token.Industry ?? department,
            Skills          = skills,
            ApplyType       = "ExternalApply",
            CompanyName     = token.CompanyName,
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

    // ── Location parsing ──────────────────────────────────────────────────────

    [GeneratedRegex(@"\b[A-Z]{2}\b")]
    private static partial Regex StateAbbrRegex();

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

        // Parse "City, ST" or "City, State" or "City, ST, Country"
        var parts = raw.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length >= 1) city = parts[0];
        if (parts.Length >= 2)
        {
            var part2 = parts[1].Trim();
            if (part2.Length > 3 && !StateAbbrRegex().IsMatch(part2))
                country = part2;
            else
                state = part2;
        }
        if (parts.Length >= 3)
            country = parts[2].Trim();

        if (state == null && country == "US" && parts.Length >= 2)
            country = parts[1].Trim();
    }
}
