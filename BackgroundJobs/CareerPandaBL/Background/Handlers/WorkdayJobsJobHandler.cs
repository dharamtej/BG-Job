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
using System.Text.RegularExpressions;
using CareerPanda.DataAccess.DA;
using CareerPanda.DataAccess.Entities.Api;
using CareerPanda.Framework.Cache;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CareerPanda.BL.Background.Handlers;

public partial class WorkdayJobsJobHandler : IJobHandler
{
    public string JobType => "WorkdayJobs";

    private const int PageSize = 20;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _http;
    private readonly ICacheService _cache;
    private readonly ILogger<WorkdayJobsJobHandler> _logger;

    public WorkdayJobsJobHandler(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        ICacheService cacheService,
        ILogger<WorkdayJobsJobHandler> logger)
    {
        _scopeFactory = scopeFactory;
        _http         = httpClientFactory;
        _cache        = cacheService;
        _logger       = logger;
    }

    public async Task ExecuteAsync(
        JobWorkRequest request,
        IJobProgressReporter progress,
        CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var fetchDa     = scope.ServiceProvider.GetRequiredService<IJobFetchDA>();

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
        await fetchDa.CreateFetchRunAsync(run);

        int totalFetched = 0, totalInserted = 0, totalUpdated = 0, totalErrors = 0, sitesProcessed = 0;

        try
        {
            var sponsors = await LoadSponsorsAsync(fetchDa, cancellationToken);
            _logger.LogInformation("[Workday] Loaded {Count} H1B sponsors for matching", sponsors.Count);

            var tokens = await fetchDa.GetActiveWorkdayTokensAsync(cancellationToken);
            _logger.LogInformation("[Workday] Loaded {Count} board tokens", tokens.Count);

            var client = _http.CreateClient("Workday");

            for (int i = 0; i < tokens.Count; i++)
            {
                if (cancellationToken.IsCancellationRequested) break;

                var token = tokens[i];
                bool isH1B = IsH1BSponsored(token.CompanyName, sponsors);

                _logger.LogInformation(
                    "[Workday] [{I}/{Total}] {Company}/{Site} H1B={H1B}",
                    i + 1, tokens.Count, token.CompanySlug, token.SiteId, isH1B);

                try
                {
                    var (jobs, httpCode, tokenStatus, jobCount) =
                        await FetchSiteJobsAsync(client, token, run.Id, isH1B, cancellationToken);

                    // null tokenStatus = transient error (5xx, 429) — leave status unchanged so it retries next run
                    if (tokenStatus != null)
                        await fetchDa.UpdateWorkdayTokenStatusAsync(token.Id, tokenStatus, httpCode, jobCount, cancellationToken);

                    if (jobs.Count > 0)
                    {
                        totalFetched += jobs.Count;
                        var (ins, upd, err) = await fetchDa.BulkUpsertRawJobsAsync(jobs, cancellationToken);
                        totalInserted += ins;
                        totalUpdated  += upd;
                        totalErrors   += err;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    // Transient error (timeout, DNS, network) — don't touch status, will retry next run
                    totalErrors++;
                    _logger.LogWarning(ex, "[Workday] Transient error {Company}/{Site} — status unchanged", token.CompanySlug, token.SiteId);
                }

                sitesProcessed++;

                if (sitesProcessed % 20 == 0 || i == tokens.Count - 1)
                {
                    await fetchDa.UpdateFetchRunStatsAsync(
                        run.Id, totalFetched, totalInserted, totalUpdated, 0, totalErrors, sitesProcessed);

                    int pct = (int)((double)(i + 1) / tokens.Count * 90);
                    await progress.ReportProgressAsync(pct,
                        $"Sites: {sitesProcessed}/{tokens.Count} — Inserted: {totalInserted}, Updated: {totalUpdated}");
                }

                // Workday is strict about rate limiting — 400ms between sites
                if (i < tokens.Count - 1)
                    await Task.Delay(400, cancellationToken);
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

        _logger.LogInformation(
            "[Workday] Done — Sites={S} Fetched={F} Inserted={I} Updated={U} Errors={E}",
            sitesProcessed, totalFetched, totalInserted, totalUpdated, totalErrors);
    }

    // ── H1B sponsor check ─────────────────────────────────────────────────────

    private async Task<HashSet<string>> LoadSponsorsAsync(IJobFetchDA fetchDa, CancellationToken ct)
    {
        var cached = await _cache.GetAsync<List<string>>(SponsorCacheWarmupService.CacheKey, ct);
        if (cached is { Count: > 0 })
            return BuildSponsorSet(cached);

        var names = await fetchDa.GetH1BSponsorNamesAsync(ct);
        await _cache.SetAsync(SponsorCacheWarmupService.CacheKey, names, SponsorCacheWarmupService.CacheTtl, ct);
        _logger.LogInformation("[Workday] Reloaded {Count} H1B sponsors from DB into cache", names.Count);
        return BuildSponsorSet(names);
    }

    private static HashSet<string> BuildSponsorSet(List<string> names)
    {
        var set = new HashSet<string>(names.Count * 2, StringComparer.OrdinalIgnoreCase);
        foreach (var name in names) { set.Add(name); set.Add(NormalizeCompanyName(name)); }
        return set;
    }

    private static bool IsH1BSponsored(string companyName, HashSet<string> sponsors) =>
        sponsors.Contains(companyName) || sponsors.Contains(NormalizeCompanyName(companyName));

    private static readonly string[] LegalSuffixes =
        ["INCORPORATED", "CORPORATION", "LIMITED", "INC", "LLC", "CORP", "LTD", "CO", "LP", "LLP", "PLLC", "PC"];

    [GeneratedRegex(@"[^\w\s]")]
    private static partial Regex NonWordChars();

    private static string NormalizeCompanyName(string name)
    {
        var upper    = name.ToUpperInvariant();
        var stripped = NonWordChars().Replace(upper, " ");
        var parts    = stripped.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int count    = parts.Length;
        while (count > 1 && LegalSuffixes.Contains(parts[count - 1])) count--;
        return string.Join(' ', parts, 0, count);
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
        var results   = new List<ApiRawJob>();
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
                _logger.LogWarning("[Workday] {Status} for {Slug}/{Site}", (int)resp.StatusCode, token.CompanySlug, token.SiteId);

                // Definitive failures — board does not exist or access permanently denied
                if (resp.StatusCode is HttpStatusCode.NotFound
                                    or HttpStatusCode.Forbidden
                                    or HttpStatusCode.Unauthorized)
                    return ([], httpCode, "INVALID", 0);

                // Transient failures (5xx, 429 rate-limit, etc.) — return null so caller skips status update
                if (results.Count == 0)
                    return ([], httpCode, null, 0);

                // Transient error mid-pagination but we already have some jobs — keep what we got
                break;
            }

            var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);

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
                var mapped = MapJob(p, token, fetchRunId, isH1B);
                if (mapped != null) results.Add(mapped);
            }

            offset += postings.Count;

            // Small delay between pages to avoid rate limiting
            if (offset < total)
                await Task.Delay(150, ct);
        }

        return (results, httpCode, results.Count > 0 ? "VALID" : "EMPTY", results.Count);
    }

    // ── Field mapping ─────────────────────────────────────────────────────────

    private static ApiRawJob? MapJob(JsonElement d, ApiWorkdayBoardToken token, string fetchRunId, bool isH1B)
    {
        // externalPath: "/en-US/site_id/job/Title/Job-Title_JOB-ID"
        // The last segment is the job ID
        var externalPath = d.TryGetProperty("externalPath", out var ep) ? ep.GetString() : null;
        if (string.IsNullOrEmpty(externalPath)) return null;

        var sourceId = externalPath.Split('/').LastOrDefault();
        if (string.IsNullOrEmpty(sourceId)) return null;

        var title    = d.TryGetProperty("title", out var t) ? t.GetString() ?? "Untitled" : "Untitled";
        var jobLink  = $"https://{token.CompanySlug}.{token.WdInstance}.myworkdayjobs.com{externalPath}";

        // ── Location ──────────────────────────────────────────────────────────
        var locText = d.TryGetProperty("locationsText", out var lt) ? lt.GetString() ?? "" : "";
        ParseLocation(locText, out var city, out var state, out var country, out var workType, out var jobWorkMode);

        // Skip non-US jobs
        if (!string.IsNullOrEmpty(country) && !UsCountryVariants.Contains(country))
            return null;

        // ── Description / H1B ────────────────────────────────────────────────
        string? desc = null;
        if (d.TryGetProperty("jobDescription", out var jd) && jd.ValueKind == JsonValueKind.Object
            && jd.TryGetProperty("descriptor", out var jdd))
            desc = jdd.GetString();

        var isH1BFinal = isH1B ||
            (desc != null && (desc.Contains("h1b", StringComparison.OrdinalIgnoreCase) ||
                              desc.Contains("h-1b", StringComparison.OrdinalIgnoreCase) ||
                              desc.Contains("visa sponsor", StringComparison.OrdinalIgnoreCase)));

        // ── Post date ─────────────────────────────────────────────────────────
        DateTime? postDate = null;
        if (d.TryGetProperty("postedOn", out var po) && po.ValueKind == JsonValueKind.String)
        {
            var raw = po.GetString();
            if (!string.IsNullOrEmpty(raw) && DateTime.TryParse(raw, out var dt))
                postDate = DateTime.SpecifyKind(dt, DateTimeKind.Utc);
        }

        // ── Employment type ───────────────────────────────────────────────────
        // Workday puts the employment type in the bulletFields array,
        // e.g. ["Full Time"], ["Part Time"], ["Contract"], ["Intern"]
        string contractType  = "FullTime";
        bool isContract      = false;
        bool isInternship    = false;
        bool isFreelance     = false;

        if (d.TryGetProperty("bulletFields", out var bullets) && bullets.ValueKind == JsonValueKind.Array)
        {
            foreach (var b in bullets.EnumerateArray())
            {
                var val = b.GetString() ?? "";
                if (val.Contains("Intern", StringComparison.OrdinalIgnoreCase))
                    { contractType = "Internship"; isInternship = true; }
                else if (val.Contains("Part Time", StringComparison.OrdinalIgnoreCase)
                      || val.Contains("Part-Time", StringComparison.OrdinalIgnoreCase))
                    contractType = "PartTime";
                else if (val.Contains("Temporary", StringComparison.OrdinalIgnoreCase)
                      || val.Contains("Temp ", StringComparison.OrdinalIgnoreCase))
                    { contractType = "Temporary"; isContract = true; }
                else if (val.Contains("Freelance", StringComparison.OrdinalIgnoreCase))
                    { contractType = "Contract"; isContract = true; isFreelance = true; }
                else if (val.Contains("Contract", StringComparison.OrdinalIgnoreCase))
                    { contractType = "Contract"; isContract = true; }
            }
        }

        // Also check employmentType field (present on some Workday tenants)
        if (d.TryGetProperty("employmentType", out var et))
        {
            var emp = et.GetString() ?? "";
            if (emp.Contains("Intern", StringComparison.OrdinalIgnoreCase))
                { contractType = "Internship"; isInternship = true; }
            else if (emp.Contains("Part", StringComparison.OrdinalIgnoreCase))
                contractType = "PartTime";
            else if (emp.Contains("Temporary", StringComparison.OrdinalIgnoreCase))
                { contractType = "Temporary"; isContract = true; }
            else if (emp.Contains("Contract", StringComparison.OrdinalIgnoreCase))
                { contractType = "Contract"; isContract = true; }
        }

        return new ApiRawJob
        {
            PublicId        = Guid.NewGuid().ToString("N"),
            Source          = "Workday",
            SourceId        = sourceId,
            FetchRunId      = fetchRunId,
            JobTitle        = title,
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
            ContractType    = contractType,
            Industry        = token.Industry,
            Skills          = null,
            ApplyType       = "ExternalApply",
            CompanyName     = token.CompanyName,
            CompanyUrl      = token.BoardUrl,
            CompanyType     = "Private",
            IsH1BSponsored  = isH1BFinal,
            IsSponsored     = isH1BFinal,
            IsW2            = null,
            IsC2C           = false,
            IsContractJob   = isContract,
            IsFreelanceJob  = isFreelance,
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

    private static readonly HashSet<string> UsStateAbbrs = new(StringComparer.OrdinalIgnoreCase)
    {
        "AL","AK","AZ","AR","CA","CO","CT","DE","FL","GA","HI","ID","IL","IN","IA",
        "KS","KY","LA","ME","MD","MA","MI","MN","MS","MO","MT","NE","NV","NH","NJ",
        "NM","NY","NC","ND","OH","OK","OR","PA","RI","SC","SD","TN","TX","UT","VT",
        "VA","WA","WV","WI","WY",
        "DC","PR","GU","VI","AS","MP"
    };

    private static readonly HashSet<string> UsStateNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Alabama","Alaska","Arizona","Arkansas","California","Colorado","Connecticut",
        "Delaware","Florida","Georgia","Hawaii","Idaho","Illinois","Indiana","Iowa",
        "Kansas","Kentucky","Louisiana","Maine","Maryland","Massachusetts","Michigan",
        "Minnesota","Mississippi","Missouri","Montana","Nebraska","Nevada",
        "New Hampshire","New Jersey","New Mexico","New York","North Carolina",
        "North Dakota","Ohio","Oklahoma","Oregon","Pennsylvania","Rhode Island",
        "South Carolina","South Dakota","Tennessee","Texas","Utah","Vermont",
        "Virginia","Washington","West Virginia","Wisconsin","Wyoming",
        "District of Columbia","Washington DC","Washington D.C.",
        "D.C.","Puerto Rico","Guam","Virgin Islands","American Samoa"
    };

    private static readonly HashSet<string> UsCountryVariants = new(StringComparer.OrdinalIgnoreCase)
    {
        "US", "USA", "U.S.", "U.S.A.", "United States", "United States of America"
    };

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
            if (UsCountryVariants.Contains(only))
                return;  // "United States" alone — country stays "US", no city
            if (UsStateNames.Contains(only) || UsStateAbbrs.Contains(only))
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

            if (UsStateAbbrs.Contains(part2))
                state = part2;
            else if (UsStateNames.Contains(part2))
                state = part2;
            else if (UsCountryVariants.Contains(part2))
                { /* country already "US" */ }
            else if (part2.Length > 2)
                country = part2;
        }

        if (parts.Length >= 3)
        {
            var part3 = parts[2].Trim();
            country = UsCountryVariants.Contains(part3) ? "US" : part3;
        }
    }
}
