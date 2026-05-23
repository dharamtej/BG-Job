// CareerPandaBL/Background/Handlers/JobicyJobsJobHandler.cs
// SOURCE : Jobicy public API — https://jobicy.com/api/v2/remote-jobs
// AUTH   : Free, no API key required.
// TERMS  : Attribution appreciated; link back to jobicy.com in UI.
//
// STRATEGY
// Jobicy exposes a remote-jobs endpoint filterable by industry slug.
// Max 50 results per call, no pagination — we loop all industry slugs.
// Every job is remote-only (WorkType/JobWorkMode = "Remote" always).
// All flags (H1B, Contract, C2C, W2, Startup, NonProfit, etc.) evaluated at insert time.
using System.Net.Http.Json;
using System.Text.Json;
using CareerPanda.DataAccess.DA;
using CareerPanda.DataAccess.Entities.Api;
using CareerPanda.Framework.Cache;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CareerPanda.BL.Background.Handlers;

public class JobicyJobsJobHandler : JobFetchBaseHandler
{
    public override string JobType        => "JobicyJobs";
    protected override string JobCategory => "JobicyJobs";
    protected override string ApiSource   => "Jobicy";

    protected override int InterPageDelayMs => 2000; // be polite — free public API

    // Jobicy industry slugs — maps to ?industry={slug}
    private static readonly string[] JobicyIndustries =
    [
        "programming",
        "devops-sysadmin",
        "design-ui-ux",
        "content-writing",
        "customer-support",
        "finance-legal",
        "human-resources",
        "marketing",
        "product-management",
        "project-management",
        "sales",
        "software-testing",
        "web-design",
        "web-development",
        "business-management",
        "data-science",
        "machine-learning",
        "cybersecurity",
        "blockchain",
        "healthcare"
    ];

    private const string SponsorsCacheKey = "h1b:sponsors:names";
    private static readonly TimeSpan SponsorsCacheTtl = TimeSpan.FromHours(24);

    private static readonly string[] LegalSuffixes =
        ["INCORPORATED", "CORPORATION", "LIMITED", "INC", "LLC", "CORP", "LTD", "CO", "LP", "LLP", "PLLC", "PC"];

    private readonly IHttpClientFactory _http;
    private readonly ICacheService _cache;
    private readonly IServiceScopeFactory _scopeFactory;

    public JobicyJobsJobHandler(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        ICacheService cacheService,
        ILogger<JobicyJobsJobHandler> logger)
        : base(scopeFactory, logger)
    {
        _scopeFactory = scopeFactory;
        _http         = httpClientFactory;
        _cache        = cacheService;
    }

    // ── Override: loop all Jobicy industry slugs ─────────────────────────────

    public override async Task ExecuteAsync(
        JobWorkRequest request,
        IJobProgressReporter progress,
        CancellationToken cancellationToken)
    {
        var input    = ParseInput(request.InputPayload);
        var sponsors = await LoadSponsorsAsync(cancellationToken);

        // If specific industry passed via SearchQuery use it; otherwise all industries
        var industries = input.SearchQuery != null
            ? [input.SearchQuery]
            : JobicyIndustries;

        using var scope   = _scopeFactory.CreateScope();
        var fetchDa = scope.ServiceProvider.GetRequiredService<IJobFetchDA>();

        var run = new ApiJobFetchRun
        {
            Id               = request.JobId,
            BackgroundTaskId = request.JobId,
            JobCategory      = JobCategory,
            ApiSource        = ApiSource,
            Status           = "Running",
            StartedAt        = DateTime.UtcNow,
            HoursBack        = input.HoursBack,
            MaxPages         = industries.Length,
            LocationFilter   = "Remote (Global)",
            CreatedById      = request.UserId
        };
        await fetchDa.CreateFetchRunAsync(run);

        Logger.LogInformation("[Jobicy] Starting — {N} industries to fetch", industries.Length);

        int totalFetched = 0, totalInserted = 0, totalUpdated = 0,
            totalSkipped = 0, totalErrors = 0, pagesFetched = 0;

        try
        {
            for (int i = 0; i < industries.Length; i++)
            {
                if (cancellationToken.IsCancellationRequested) break;

                var industry = industries[i];
                Logger.LogInformation("[Jobicy] Industry {I}/{N}: '{Ind}'", i + 1, industries.Length, industry);

                List<ApiRawJob> jobs;
                try
                {
                    jobs = await FetchIndustryAsync(industry, input.HoursBack, run.Id, sponsors, cancellationToken);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "[Jobicy] Fetch failed for industry '{Ind}'", industry);
                    totalErrors++;
                    continue;
                }

                if (jobs.Count == 0)
                {
                    Logger.LogInformation("[Jobicy] No results for industry '{Ind}'", industry);
                    await Task.Delay(InterPageDelayMs, cancellationToken);
                    continue;
                }

                pagesFetched++;
                totalFetched += jobs.Count;

                var (ins, upd, err) = await fetchDa.BulkUpsertRawJobsAsync(jobs, cancellationToken);
                totalInserted += ins;
                totalUpdated  += upd;
                totalErrors   += err;
                totalSkipped  += jobs.Count - ins - upd - err;

                await fetchDa.UpdateFetchRunStatsAsync(
                    run.Id, totalFetched, totalInserted, totalUpdated,
                    totalSkipped, totalErrors, pagesFetched);

                int pct = (int)((double)(i + 1) / industries.Length * 90);
                await progress.ReportProgressAsync(pct,
                    $"[{i + 1}/{industries.Length}] '{industry}' — Inserted: {totalInserted}, Updated: {totalUpdated}");

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
            "[Jobicy] Done — Industries={N} Fetched={F} Inserted={I} Updated={U} Errors={E}",
            industries.Length, totalFetched, totalInserted, totalUpdated, totalErrors);

        await progress.ReportProgressAsync(100,
            $"Done — {industries.Length} industries, Inserted: {totalInserted}, Updated: {totalUpdated}");
    }

    // ── Jobicy API fetch ──────────────────────────────────────────────────────

    private async Task<List<ApiRawJob>> FetchIndustryAsync(
        string industry, int hoursBack, string fetchRunId, HashSet<string> sponsors, CancellationToken ct)
    {
        var client = _http.CreateClient("Jobicy");
        var url    = $"https://jobicy.com/api/v2/remote-jobs?count=50&industry={Uri.EscapeDataString(industry)}";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        var res = await client.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();

        var json = await res.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);

        if (!json.TryGetProperty("jobs", out var jobsArray) ||
            jobsArray.ValueKind != JsonValueKind.Array)
            return [];

        var cutoff = DateTime.UtcNow.AddHours(-hoursBack);
        var jobs   = new List<ApiRawJob>();

        foreach (var item in jobsArray.EnumerateArray())
        {
            try
            {
                var job = MapJob(item, fetchRunId, sponsors);

                if (job.PostDate.HasValue && job.PostDate.Value < cutoff) continue;

                jobs.Add(job);
            }
            catch (Exception ex) { Logger.LogWarning(ex, "[Jobicy] Map failed for item"); }
        }

        return jobs;
    }

    private ApiRawJob MapJob(JsonElement j, string fetchRunId, HashSet<string> sponsors)
    {
        var sourceId    = j.TryGetProperty("id",          out var id)  ? id.GetRawText()   : null;
        var title       = j.TryGetProperty("jobTitle",    out var t)   ? t.GetString()     : "Untitled";
        var companyName = j.TryGetProperty("companyName", out var co)  ? co.GetString()    : null;
        var desc        = j.TryGetProperty("jobDescription", out var d) ? d.GetString()    : null;
        var excerpt     = j.TryGetProperty("jobExcerpt",  out var ex)  ? ex.GetString()    : null;
        var applyUrl    = j.TryGetProperty("url",         out var u)   ? u.GetString()     : null;
        var logoUrl     = j.TryGetProperty("companyLogo", out var lg)  ? lg.GetString()    : null;
        var geoRaw      = j.TryGetProperty("jobGeo",      out var geo) ? geo.GetString()   : null;
        var levelRaw    = j.TryGetProperty("jobLevel",    out var lv)  ? lv.GetString()    : null;
        var pubDateStr  = j.TryGetProperty("pubDate",     out var pd)  ? pd.GetString()    : null;

        // Merge description + excerpt for flag detection
        var fullText = string.Concat(desc, " ", excerpt);

        // Parse publish date
        DateTime? postDate = null;
        if (pubDateStr != null && DateTime.TryParse(pubDateStr, out var dt))
            postDate = DateTime.SpecifyKind(dt, DateTimeKind.Utc);

        // Parse salary
        decimal? salMin = null, salMax = null;
        if (j.TryGetProperty("annualSalaryMin", out var sn) && sn.ValueKind == JsonValueKind.Number && sn.GetDecimal() > 0) salMin = sn.GetDecimal();
        if (j.TryGetProperty("annualSalaryMax", out var sx) && sx.ValueKind == JsonValueKind.Number && sx.GetDecimal() > 0) salMax = sx.GetDecimal();
        var salaryCurrency = j.TryGetProperty("salaryCurrency", out var sc) ? sc.GetString() ?? "USD" : "USD";

        // Parse jobType array → WorkType string
        string? workType = null;
        if (j.TryGetProperty("jobType", out var jt) && jt.ValueKind == JsonValueKind.Array)
        {
            var types = jt.EnumerateArray().Select(x => x.GetString()).Where(x => x != null).ToArray();
            workType = types.Length > 0 ? string.Join(", ", types) : null;
        }

        // Parse jobIndustry array → Skills
        string[]? skills = null;
        if (j.TryGetProperty("jobIndustry", out var ji) && ji.ValueKind == JsonValueKind.Array)
            skills = ji.EnumerateArray().Select(x => x.GetString()!).Where(x => x != null).ToArray();

        // Country from geo
        string? country = null;
        if (geoRaw != null)
        {
            var g = geoRaw.ToUpperInvariant();
            if (g is "USA" or "US" or "UNITED STATES") country = "US";
            else if (g is "WORLDWIDE" or "ANYWHERE" or "REMOTE") country = null;
            else country = geoRaw;
        }

        // ── Flag detection ────────────────────────────────────────────────────
        var isH1B         = ContainsAny(fullText, "h1b", "h-1b", "visa sponsor", "will sponsor") ||
                            (companyName != null && sponsors.Contains(companyName));
        var isContract    = ContainsAny(fullText, "contract", "contractor") ||
                            (workType?.Contains("contract", StringComparison.OrdinalIgnoreCase) == true);
        var isC2C         = ContainsAny(fullText, "c2c", "corp to corp", "corp-to-corp");
        var isW2          = ContainsAny(fullText, "w2", "w-2");
        var isFreelance   = ContainsAny(fullText, "1099", "freelance", "independent contractor") ||
                            (workType?.Contains("freelance", StringComparison.OrdinalIgnoreCase) == true);
        var isPrimeVendor = ContainsAny(fullText, "prime vendor", "direct client", "end client");
        var isStaffing    = ContainsAny(fullText, "staffing", "recruiting firm") ||
                            ContainsAny(companyName, "staffing", "consulting", "solutions");
        var isUniversity  = ContainsAny(companyName, "university", "college", "institute", "academia") ||
                            ContainsAny(fullText, "university", "academic", "faculty");
        var isStartup     = ContainsAny(fullText, "startup", "start-up", "series a", "series b", "seed", "venture") ||
                            ContainsAny(companyName, "startup", "start-up");
        var isNonProfit   = ContainsAny(fullText, "nonprofit", "non-profit", "501(c)", "501c3", "ngo") ||
                            ContainsAny(companyName, "foundation", "nonprofit", "non-profit");

        return new ApiRawJob
        {
            PublicId          = Guid.NewGuid().ToString("N"),
            Source            = "Jobicy",
            SourceId          = sourceId,
            FetchRunId        = fetchRunId,
            JobTitle          = title ?? "Untitled",
            JobLink           = applyUrl,
            JobDescription    = desc ?? excerpt,
            City              = null,
            State             = null,
            Country           = country,
            PostDate          = postDate,
            HoursBackPosted   = ParseHoursBack(postDate),
            SalaryMin         = salMin,
            SalaryMax         = salMax,
            SalaryRangeText   = salMin.HasValue && salMax.HasValue ? $"${salMin:N0}–${salMax:N0}/yr" : null,
            SalaryCurrency    = salaryCurrency,
            WorkType          = "Remote",
            JobWorkMode       = "Remote",
            CompanyName       = companyName,
            CompanyLogoUrl    = logoUrl,
            Skills            = skills,
            // ── All flags ─────────────────────────────────────────────────────
            IsH1BSponsored    = isH1B,
            IsSponsored       = isH1B || ContainsAny(fullText, "sponsor", "visa"),
            IsContractJob     = isContract,
            IsC2C             = isC2C,
            IsW2              = isW2,
            IsFreelanceJob    = isFreelance,
            IsPrimeVendor     = isPrimeVendor,
            IsStaffing        = isStaffing,
            IsUniversityJob   = isUniversity,
            IsStartupJob      = isStartup,
            IsNonProfitJob    = isNonProfit,
            Status            = true,
            CreatedOn         = DateTime.UtcNow,
            UpdatedOn         = DateTime.UtcNow
        };
    }

    // ── H1B sponsor list ──────────────────────────────────────────────────────

    private async Task<HashSet<string>> LoadSponsorsAsync(CancellationToken ct)
    {
        var cached = await _cache.GetAsync<List<string>>(SponsorsCacheKey, ct);
        if (cached is { Count: > 0 }) return BuildSponsorSet(cached);

        using var scope = _scopeFactory.CreateScope();
        var da    = scope.ServiceProvider.GetRequiredService<IJobFetchDA>();
        var names = await da.GetH1BSponsorNamesAsync(ct);
        await _cache.SetAsync(SponsorsCacheKey, names, SponsorsCacheTtl, ct);
        Logger.LogInformation("[Jobicy] Loaded {Count} H1B sponsors into cache", names.Count);
        return BuildSponsorSet(names);
    }

    private static HashSet<string> BuildSponsorSet(List<string> names)
    {
        var set = new HashSet<string>(names.Count * 2, StringComparer.OrdinalIgnoreCase);
        foreach (var name in names) { set.Add(name); set.Add(NormalizeCompanyName(name)); }
        return set;
    }

    private static string NormalizeCompanyName(string name)
    {
        var upper    = name.ToUpperInvariant();
        var stripped = System.Text.RegularExpressions.Regex.Replace(upper, @"[^\w\s]", " ");
        var parts    = stripped.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int count    = parts.Length;
        while (count > 1 && LegalSuffixes.Contains(parts[count - 1])) count--;
        return string.Join(' ', parts, 0, count);
    }

    // Required by base class — not used (ExecuteAsync fully overridden)
    protected override Task<List<ApiRawJob>> FetchPageAsync(
        int page, JobFetchInput input, string fetchRunId, CancellationToken ct) =>
        FetchIndustryAsync(input.SearchQuery ?? "programming", input.HoursBack, fetchRunId,
            LoadSponsorsAsync(ct).GetAwaiter().GetResult(), ct);
}
