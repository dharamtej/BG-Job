// CareerPandaBL/Background/Handlers/WorkdayJobsJobHandler.cs
// SOURCE : Workday Job Board API (public, no auth required)
// FLOW   : Load board tokens from api.workday_board_tokens (status != INVALID)
//          → POST https://{slug}.{wd}.myworkdayjobs.com/wday/cxs/{slug}/{site_id}/jobs
//            body: {"limit":20,"offset":0}  (paginated)
//          → Update token status (VALID / EMPTY / INVALID) based on response
//          → Upsert into api.raw_jobs
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CareerPanda.DataAccess.DA;
using CareerPanda.DataAccess.Entities.Api;
using CareerPanda.Framework.Cache;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static CareerPanda.BL.Background.Handlers.JobFetchHelpers;

namespace CareerPanda.BL.Background.Handlers;

public class WorkdayJobsJobHandler : IJobHandler
{
    public string JobType => "WorkdayJobs";

    private const int PageSize = 20;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _http;
    private readonly ICacheService _cache;
    private readonly ILogger<WorkdayJobsJobHandler> _logger;

    // How many sites are processed concurrently — tunable via appsettings.
    // Workday rate-limits per-tenant, and each site is its own tenant host, so
    // concurrency across sites is safe; pages within one site stay sequential.
    private readonly int _siteParallel;

    public WorkdayJobsJobHandler(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        ICacheService cacheService,
        IConfiguration configuration,
        ILogger<WorkdayJobsJobHandler> logger)
    {
        _scopeFactory = scopeFactory;
        _http         = httpClientFactory;
        _cache        = cacheService;
        _logger       = logger;

        _siteParallel = Math.Max(1,
            configuration.GetSection("JobApiSettings").GetValue("WorkdaySiteParallel", 8));
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
            JobCategory      = "WorkdayJobs",
            ApiSource        = "Workday",
            Status           = "Running",
            StartedAt        = DateTime.UtcNow,
            CreatedById      = request.UserId
        };

        HashSet<string> sponsors;
        List<ApiWorkdayBoardToken> tokens;
        using (var setupScope = _scopeFactory.CreateScope())
        {
            var setupDa = setupScope.ServiceProvider.GetRequiredService<IJobFetchDA>();
            await setupDa.CreateFetchRunAsync(run);

            sponsors = await LoadSponsorsAsync(setupDa, cancellationToken);
            _logger.LogInformation("[Workday] Loaded {Count} H1B sponsors for matching", sponsors.Count);

            tokens = await setupDa.GetActiveWorkdayTokensAsync(cancellationToken);
            _logger.LogInformation("[Workday] Loaded {Count} board tokens", tokens.Count);
        }

        // Counters shared across parallel workers — mutate only via Interlocked.
        int totalFetched = 0, totalInserted = 0, totalUpdated = 0, totalErrors = 0, sitesProcessed = 0;

        // UpdateFetchRunStatsAsync/ReportProgressAsync wrap scoped DbContexts — serialize them.
        using var progressLock = new SemaphoreSlim(1, 1);

        try
        {
            var client = _http.CreateClient("Workday");

            var parallelOpts = new ParallelOptions
            {
                MaxDegreeOfParallelism = _siteParallel,
                CancellationToken      = cancellationToken
            };

            _logger.LogInformation("[Workday] Processing {Total} sites (siteParallel={S})", tokens.Count, _siteParallel);

            await Parallel.ForEachAsync(tokens, parallelOpts, async (token, ct) =>
            {
                bool isH1B = CompanyNameNormalizer.IsH1BSponsored(token.CompanyName, sponsors);

                // Each parallel worker gets its own DbContext — EF Core is not thread-safe
                using var itemScope = _scopeFactory.CreateScope();
                var fetchDa = itemScope.ServiceProvider.GetRequiredService<IJobFetchDA>();

                try
                {
                    var (jobs, httpCode, tokenStatus, jobCount) =
                        await FetchSiteJobsAsync(client, token, run.Id, isH1B, ct);

                    // null tokenStatus = transient error (5xx, 429) — leave status unchanged so it retries next run
                    if (tokenStatus != null)
                        await fetchDa.UpdateWorkdayTokenStatusAsync(token.Id, tokenStatus, httpCode, jobCount, ct);

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
                    _logger.LogWarning(ex, "[Workday] Transient error {Company}/{Site} — status unchanged", token.CompanySlug, token.SiteId);
                }

                int done = Interlocked.Increment(ref sitesProcessed);

                if (done % 20 == 0 || done == tokens.Count)
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
                            $"Sites: {done}/{tokens.Count} — Inserted: {inserted}, Updated: {updated}");
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
            "[Workday] Done — Sites={S} Fetched={F} Inserted={I} Updated={U} Errors={E}",
            sitesProcessed, totalFetched, totalInserted, totalUpdated, totalErrors);
    }

    // ── H1B sponsor check ─────────────────────────────────────────────────────

    private async Task<HashSet<string>> LoadSponsorsAsync(IJobFetchDA fetchDa, CancellationToken ct)
    {
        var cached = await _cache.GetAsync<List<string>>(SponsorCacheWarmupService.CacheKey, ct);
        if (cached is { Count: > 0 })
            return CompanyNameNormalizer.BuildSponsorSet(cached);

        var names = await fetchDa.GetH1BSponsorNamesAsync(ct);
        await _cache.SetAsync(SponsorCacheWarmupService.CacheKey, names, SponsorCacheWarmupService.CacheTtl, ct);
        _logger.LogInformation("[Workday] Reloaded {Count} H1B sponsors from DB into cache", names.Count);
        return CompanyNameNormalizer.BuildSponsorSet(names);
    }

    // ── Per-site paginated fetch ───────────────────────────────────────────────

    // tokenStatus = null means transient error — caller must NOT update the token status
    private async Task<(List<ApiRawJob> jobs, short httpCode, string? tokenStatus, int jobCount)>
        FetchSiteJobsAsync(
            HttpClient client,
            ApiWorkdayBoardToken token,
            string fetchRunId,
            bool isH1B,
            CancellationToken ct)
    {
        var results   = new List<(ApiRawJob Job, string ExternalPath)>();
        int offset    = 0;
        int total     = int.MaxValue;
        short httpCode = 200;

        while (offset < total)
        {
            if (ct.IsCancellationRequested) break;

            var body    = new StringContent(
                $$"""{"limit":{{PageSize}},"offset":{{offset}}}""",
                Encoding.UTF8,
                "application/json");

            using var resp = await client.PostAsync(token.ApiUrl, body, ct);
            httpCode = (short)resp.StatusCode;

            if (!resp.IsSuccessStatusCode)
            {
                if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound
                        or HttpStatusCode.Unauthorized or HttpStatusCode.TooManyRequests)
                    _logger.LogDebug("[Workday] {Status} for {Slug}/{Site}", (int)resp.StatusCode, token.CompanySlug, token.SiteId);
                else
                    _logger.LogWarning("[Workday] {Status} for {Slug}/{Site}", (int)resp.StatusCode, token.CompanySlug, token.SiteId);

                // 404 = site genuinely doesn't exist → permanent INVALID.
                if (resp.StatusCode == HttpStatusCode.NotFound)
                    return ([], httpCode, "INVALID", 0);

                // 401/403 used to be INVALID forever — now treated as transient (WAF / rate-limit).

                // Transient failures (5xx, 429 rate-limit, etc.) — return null so caller skips status update
                if (results.Count == 0)
                    return ([], httpCode, null, 0);

                // Transient error mid-pagination but we already have some jobs — keep what we got
                break;
            }

            var doc = await ReadJsonAsync(resp.Content, ct);

            // First page — determine total
            if (offset == 0)
            {
                if (doc.TryGetProperty("total", out var totalEl))
                    total = totalEl.GetInt32();
                else
                    total = 0;  // unexpected shape → treat as done

                if (total == 0)
                    return ([], httpCode, "EMPTY", 0);
            }

            if (!doc.TryGetProperty("jobPostings", out var postingsEl)
                || postingsEl.ValueKind != JsonValueKind.Array)
                break;

            var postings = postingsEl.EnumerateArray().ToList();
            if (postings.Count == 0) break;

            foreach (var p in postings)
            {
                var externalPath = p.TryGetProperty("externalPath", out var ep) ? ep.GetString() : null;
                if (string.IsNullOrEmpty(externalPath)) continue;

                var mapped = MapJob(p, token, fetchRunId, isH1B, externalPath);
                if (mapped != null)
                    results.Add((mapped, externalPath));
            }

            offset += postings.Count;

            // Small delay between pages to avoid rate limiting
            if (offset < total)
                await Task.Delay(150, ct);
        }

        // Enrich each job with description + ContractType from the detail endpoint
        if (results.Count > 0)
            await EnrichWithDescriptionsAsync(client, token, results, ct);

        var jobs = results.Select(r => r.Job).ToList();
        return (jobs, httpCode, jobs.Count > 0 ? "VALID" : "EMPTY", jobs.Count);
    }

    // ── Per-job description enrichment ────────────────────────────────────────
    // Workday list endpoint has no description. Full content requires a GET to the detail endpoint.
    // Detail URL = {api_url without /jobs}{externalPath}
    // Response: jobPostingInfo.jobDescription (HTML), jobPostingInfo.timeType (ContractType)
    // Max 3 concurrent calls per site to respect per-tenant rate limits.

    private async Task EnrichWithDescriptionsAsync(
        HttpClient client,
        ApiWorkdayBoardToken token,
        List<(ApiRawJob Job, string ExternalPath)> items,
        CancellationToken ct)
    {
        // Build the base URL: strip "/jobs" from the end of apiUrl
        var baseUrl = token.ApiUrl.TrimEnd('/');
        if (baseUrl.EndsWith("/jobs", StringComparison.OrdinalIgnoreCase))
            baseUrl = baseUrl[..^"/jobs".Length];

        using var sem = new SemaphoreSlim(3, 3);
        await Task.WhenAll(items.Select(async item =>
        {
            await sem.WaitAsync(ct);
            try
            {
                // GET {base}{externalPath}
                // e.g. https://9dot.wd503.myworkdayjobs.com/wday/cxs/9dot/skyrocket/job/City/Title_R12345
                var detailUrl = baseUrl + item.ExternalPath;
                using var resp = await client.GetAsync(detailUrl, ct);
                if (!resp.IsSuccessStatusCode) return;

                JsonElement detail;
                try { detail = await ReadJsonAsync(resp.Content, ct); }
                catch { return; }

                if (!detail.TryGetProperty("jobPostingInfo", out var jpi) || jpi.ValueKind != JsonValueKind.Object)
                    return;

                // ── Description (HTML → plain text) ──────────────────────────
                if (jpi.TryGetProperty("jobDescription", out var jd) && jd.ValueKind == JsonValueKind.String)
                {
                    var html = jd.GetString();
                    if (!string.IsNullOrWhiteSpace(html))
                    {
                        var plain = StripHtml(html);
                        item.Job.JobDescription = plain;

                        // Salary often embedded in description e.g. "$30.00 - $42.50 - Hourly"
                        if (!item.Job.SalaryMin.HasValue)
                        {
                            var (salMin, salMax, salPeriod) = CareerPanda.DataAccess.Util.SalaryParser.Parse(plain);
                            if (salMin.HasValue || salMax.HasValue)
                            {
                                item.Job.SalaryMin       = salMin;
                                item.Job.SalaryMax       = salMax;
                                item.Job.SalaryType      = salPeriod;
                                item.Job.SalaryRangeText = BuildSalaryRangeText(salMin, salMax, salPeriod);
                            }
                        }
                    }
                }

                // ── Per-job visa negation (company-level H1B may be overridden) ─
                if (item.Job.IsH1BSponsored == true && !string.IsNullOrWhiteSpace(item.Job.JobDescription))
                {
                    var neg = ContainsAny(item.Job.JobDescription,
                        "do not sponsor", "does not sponsor", "no sponsorship", "unable to sponsor",
                        "cannot sponsor", "will not sponsor", "no h-1b", "no h1b",
                        "must be authorized to work", "must have work authorization",
                        "authorized to work in the us", "authorized to work in the united states");
                    if (neg) { item.Job.IsH1BSponsored = false; item.Job.IsSponsored = false; }
                }

                // ── ContractType from timeType (only in detail) ───────────────
                if (string.IsNullOrWhiteSpace(item.Job.ContractType) &&
                    jpi.TryGetProperty("timeType", out var tt) && tt.ValueKind == JsonValueKind.String)
                {
                    var timeType = tt.GetString() ?? "";
                    item.Job.ContractType = timeType.ToLowerInvariant() switch
                    {
                        var s when s.Contains("part")     => "PartTime",
                        var s when s.Contains("intern")   => "Internship",
                        var s when s.Contains("temp")     => "Temporary",
                        var s when s.Contains("contract") => "Contract",
                        _                                 => "FullTime"
                    };
                    item.Job.IsContractJob = item.Job.ContractType is "Contract" or "Temporary";
                }

                // ── Real company name from hiringOrganization ─────────────────
                if (detail.TryGetProperty("hiringOrganization", out var org) &&
                    org.ValueKind == JsonValueKind.Object &&
                    org.TryGetProperty("name", out var orgName) &&
                    orgName.ValueKind == JsonValueKind.String)
                {
                    var name = orgName.GetString();
                    if (!string.IsNullOrWhiteSpace(name))
                        item.Job.CompanyName = name;

                    if (org.TryGetProperty("url", out var orgUrl) &&
                        orgUrl.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(orgUrl.GetString()))
                        item.Job.CompanyUrl = orgUrl.GetString();
                }

                // ── Country from structured field ─────────────────────────────
                if (jpi.TryGetProperty("jobRequisitionLocation", out var reqLoc) &&
                    reqLoc.ValueKind == JsonValueKind.Object &&
                    reqLoc.TryGetProperty("country", out var reqCountry) &&
                    reqCountry.ValueKind == JsonValueKind.Object &&
                    reqCountry.TryGetProperty("alpha2Code", out var alpha2) &&
                    alpha2.ValueKind == JsonValueKind.String)
                {
                    var code = alpha2.GetString();
                    if (!string.IsNullOrWhiteSpace(code))
                        item.Job.Country = code.ToUpperInvariant();
                }

                // ── City/State fallback if list locationsText gave nothing ─────
                if (string.IsNullOrWhiteSpace(item.Job.City) &&
                    jpi.TryGetProperty("location", out var locEl) &&
                    locEl.ValueKind == JsonValueKind.String)
                {
                    var loc = locEl.GetString() ?? "";
                    var parts = loc.Split(',', StringSplitOptions.TrimEntries);
                    if (parts.Length >= 1) item.Job.City  = parts[0];
                    if (parts.Length >= 2) item.Job.State = NormalizeState(parts[1]);
                }

                // ── PostDate fallback from startDate ──────────────────────────
                if (!item.Job.PostDate.HasValue &&
                    jpi.TryGetProperty("startDate", out var sd) &&
                    sd.ValueKind == JsonValueKind.String &&
                    DateTime.TryParse(sd.GetString(), out var startDt))
                {
                    item.Job.PostDate        = startDt.ToUniversalTime();
                    item.Job.HoursBackPosted = (int)(DateTime.UtcNow - item.Job.PostDate.Value).TotalHours;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { /* best-effort — skip if detail unavailable */ }
            finally { sem.Release(); }
        }));
    }

    // ── Field mapping ─────────────────────────────────────────────────────────

    // externalPath passed in from the caller since it's already extracted before MapJob is called
    private static ApiRawJob? MapJob(JsonElement d, ApiWorkdayBoardToken token, string fetchRunId, bool isH1B, string externalPath)
    {
        // sourceId = last path segment: "Job-Title_R012345"
        var sourceId = externalPath.Split('/').LastOrDefault();
        if (string.IsNullOrEmpty(sourceId)) return null;

        var title   = d.TryGetProperty("title", out var t) ? t.GetString() ?? "Untitled" : "Untitled";
        var jobLink = $"https://{token.CompanySlug}.{token.WdInstance}.myworkdayjobs.com{externalPath}";

        // ── Location ──────────────────────────────────────────────────────────
        var locText = d.TryGetProperty("locationsText", out var lt) ? lt.GetString() ?? "" : "";
        ParseLocation(locText, out var city, out var state, out var country, out var workType, out var jobWorkMode);

        if (!string.IsNullOrEmpty(country) && !UsLocationHelper.CountryVariants.Contains(country))
            return null;

        // ── Post date — "Posted 9 Days Ago" / "Posted Today" / "Posted Yesterday" ──
        var postDate = ParseWorkdayPostedOn(
            d.TryGetProperty("postedOn", out var po) ? po.GetString() : null);

        // Description and ContractType come from the detail endpoint (EnrichWithDescriptionsAsync).
        // Leave null here — jobs without description will be filtered by BulkUpsertRawJobsAsync.

        return new ApiRawJob
        {
            PublicId        = Guid.NewGuid().ToString("N"),
            Source          = "Workday",
            SourceId        = sourceId,
            FetchRunId      = fetchRunId,
            JobTitle        = title,
            JobLink         = jobLink,
            JobDescription  = null,   // filled by EnrichWithDescriptionsAsync
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
            ContractType    = "FullTime",   // refined by EnrichWithDescriptionsAsync from timeType
            JobLevel        = NormalizeJobLevel(title),
            Industry        = token.Industry,
            Skills          = null,
            ApplyType       = "ExternalApply",
            CompanyName     = token.CompanyName,
            CompanyUrl      = token.BoardUrl,
            CompanyType     = "Private",
            IsH1BSponsored  = isH1B,   // company-level; per-job negation applied after description loaded
            IsW2            = null,
            IsC2C           = false,
            IsContractJob   = false,
            IsFreelanceJob  = false,
            IsPrimeVendor   = false,
            IsStaffing      = false,
            IsStartupJob    = false,
            IsNonProfitJob  = false,
            IsUniversityJob = false,
            Status          = true,
            CreatedOn       = DateTime.UtcNow,
            UpdatedOn       = DateTime.UtcNow
        };
    }

    // Parses Workday's relative date strings: "Posted 9 Days Ago", "Posted Today", etc.
    private static DateTime? ParseWorkdayPostedOn(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (raw.Contains("Today",     StringComparison.OrdinalIgnoreCase)) return DateTime.UtcNow;
        if (raw.Contains("Yesterday", StringComparison.OrdinalIgnoreCase)) return DateTime.UtcNow.AddDays(-1);
        var m = System.Text.RegularExpressions.Regex.Match(raw, @"(\d+)\+?\s+Days?", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var days)) return DateTime.UtcNow.AddDays(-days);
        return null;
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

        // Single-part location: if it's a US country variant keep default "US";
        // if it's a US state name treat it as state-only; otherwise it's a foreign country.
        if (parts.Length == 1)
        {
            var only = parts[0].Trim();
            if (UsLocationHelper.CountryVariants.Contains(only))
                return;  // "United States" alone — country stays "US", no city
            if (UsLocationHelper.StateNames.Contains(only) || UsLocationHelper.StateAbbrs.Contains(only))
                { state = only; return; }
            // anything else is likely a foreign country or city-only — mark as foreign
            // so the US filter in MapJob drops it unless it's clearly a US location
            country = only;
            city    = null;
            return;
        }

        if (parts.Length >= 1) city = parts[0];

        if (parts.Length >= 2)
        {
            var part2 = parts[1].Trim();

            if (UsLocationHelper.StateAbbrs.Contains(part2))
                state = part2;
            else if (UsLocationHelper.StateNames.Contains(part2))
                state = part2;
            else if (UsLocationHelper.CountryVariants.Contains(part2))
                { /* country already "US" */ }
            else if (part2.Length > 2)
                country = part2;
        }

        if (parts.Length >= 3)
        {
            var part3 = parts[2].Trim();
            country = UsLocationHelper.CountryVariants.Contains(part3) ? "US" : part3;
        }
    }
}
