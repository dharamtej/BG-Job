// CareerPandaBL/Background/Handlers/AdzunaJobsJobHandler.cs
// SOURCE : Adzuna API — aggregates jobs from across the web
// API    : https://api.adzuna.com/v1/api/jobs/us/search/{page}
// AUTH   : app_id + app_key query params (no headers needed)
// DOCS   : https://developer.adzuna.com/activedocs
//
// STRATEGY — same roles × US states expansion as AllJobs (JSearch).
// Adzuna pagination works properly (unlike JSearch) so PagesPerQuery=2
// gives 100 jobs per role+state combo.
// Run this alongside AllJobsJobHandler for maximum coverage.
using System.Net.Http.Json;
using System.Text.Json;
using CareerPanda.DataAccess.DA;
using CareerPanda.DataAccess.Entities.Api;
using CareerPanda.Framework.Cache;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CareerPanda.BL.Background.Handlers;

public class AdzunaJobsJobHandler : JobFetchBaseHandler
{
    public override string JobType        => "AdzunaJobs";
    protected override string JobCategory => "AdzunaJobs";
    protected override string ApiSource   => "Adzuna";

    protected override int InterPageDelayMs => 300; // Adzuna is more lenient than JSearch

    private static readonly string[] UsStates =
    [
        "Alabama", "Alaska", "Arizona", "Arkansas", "California",
        "Colorado", "Connecticut", "Delaware", "Florida", "Georgia",
        "Hawaii", "Idaho", "Illinois", "Indiana", "Iowa",
        "Kansas", "Kentucky", "Louisiana", "Maine", "Maryland",
        "Massachusetts", "Michigan", "Minnesota", "Mississippi", "Missouri",
        "Montana", "Nebraska", "Nevada", "New Hampshire", "New Jersey",
        "New Mexico", "New York", "North Carolina", "North Dakota", "Ohio",
        "Oklahoma", "Oregon", "Pennsylvania", "Rhode Island", "South Carolina",
        "South Dakota", "Tennessee", "Texas", "Utah", "Vermont",
        "Virginia", "Washington", "West Virginia", "Wisconsin", "Wyoming"
    ];

    private readonly IHttpClientFactory _http;
    private readonly string _appId;
    private readonly string _appKey;

    private const string QueriesCacheKey = "alljobs:role:queries";
    private static readonly TimeSpan QueriesCacheTtl = TimeSpan.FromHours(6);

    public AdzunaJobsJobHandler(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        ICacheService cacheService,
        IConfiguration configuration,
        ILogger<AdzunaJobsJobHandler> logger)
        : base(scopeFactory, cacheService, logger)
    {
        _http   = httpClientFactory;
        _appId  = configuration["JobApiSettings:AdzunaAppId"]  ?? string.Empty;
        _appKey = configuration["JobApiSettings:AdzunaAppKey"] ?? string.Empty;
    }

    // ── Override: loop all role queries × all US states ──────────────────────

    public override async Task ExecuteAsync(
        JobWorkRequest request,
        IJobProgressReporter progress,
        CancellationToken cancellationToken)
    {
        var input = ParseInput(request.InputPayload);

        // Specific query → fall back to base handler (normal MaxPages loop)
        if (input.SearchQuery != null)
        {
            await base.ExecuteAsync(request, progress, cancellationToken);
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var fetchDa = scope.ServiceProvider.GetRequiredService<IJobFetchDA>();

        // Load H1B sponsor list once — used to flag every job fetched
        var sponsors = await LoadSponsorsAsync(cancellationToken);

        var queries   = await LoadQueriesAsync(cancellationToken);
        var locations = input.Location != null
            ? (string[])[input.Location]
            : UsStates;

        int totalCalls = queries.Count * locations.Length * input.PagesPerQuery;

        var run = new ApiJobFetchRun
        {
            Id               = request.JobId,
            BackgroundTaskId = request.JobId,
            JobCategory      = JobCategory,
            ApiSource        = ApiSource,
            Status           = "Running",
            StartedAt        = DateTime.UtcNow,
            HoursBack        = input.HoursBack,
            MaxPages         = totalCalls,
            LocationFilter   = input.Location ?? "All US States",
            CreatedById      = request.UserId
        };
        await fetchDa.CreateFetchRunAsync(run);

        Logger.LogInformation("[AdzunaJobs] Starting — {R} roles × {L} locations × {P} pages = {T} total calls",
            queries.Count, locations.Length, input.PagesPerQuery, totalCalls);

        int totalFetched = 0, totalInserted = 0, totalUpdated = 0,
            totalSkipped = 0, totalErrors = 0, pagesFetched = 0, callIndex = 0;

        try
        {
            foreach (var query in queries)
            {
                if (cancellationToken.IsCancellationRequested) break;

                foreach (var location in locations)
                {
                    if (cancellationToken.IsCancellationRequested) break;

                    for (int p = 1; p <= input.PagesPerQuery; p++)
                    {
                        if (cancellationToken.IsCancellationRequested) break;

                        callIndex++;

                        List<ApiRawJob> jobs;
                        try
                        {
                            jobs = await FetchAdzunaPageAsync(query, location, p, input.HoursBack, run.Id, sponsors, cancellationToken);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            Logger.LogError(ex, "[AdzunaJobs] Failed: '{Q}' in {L} page {P}", query, location, p);
                            totalErrors++;
                            continue;
                        }

                        if (jobs.Count == 0) break;

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

                        int pct = (int)((double)callIndex / totalCalls * 90);
                        await progress.ReportProgressAsync(pct,
                            $"[{callIndex}/{totalCalls}] '{query}' in {location} — Inserted: {totalInserted}, Updated: {totalUpdated}");

                        await Task.Delay(InterPageDelayMs, cancellationToken);
                    }
                }
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
            "[AdzunaJobs] Done — Roles={R} Locations={L} Fetched={F} Inserted={I} Updated={U} Errors={E}",
            queries.Count, locations.Length, totalFetched, totalInserted, totalUpdated, totalErrors);

        await progress.ReportProgressAsync(100,
            $"Done — {queries.Count} roles × {locations.Length} states, Inserted: {totalInserted}, Updated: {totalUpdated}");
    }

    // ── Adzuna API fetch ──────────────────────────────────────────────────────

    private async Task<List<ApiRawJob>> FetchAdzunaPageAsync(
        string roleQuery, string location, int page, int hoursBack,
        string fetchRunId, HashSet<string> sponsors, CancellationToken ct)
    {
        var client     = _http.CreateClient("Adzuna");
        var what       = Uri.EscapeDataString(roleQuery);
        var where      = Uri.EscapeDataString(location);
        var maxDaysOld = hoursBack <= 24 ? 1 : hoursBack <= 168 ? 7 : 30;

        var url = $"https://api.adzuna.com/v1/api/jobs/us/search/{page}" +
                  $"?app_id={_appId}&app_key={_appKey}" +
                  $"&results_per_page=50&what={what}&where={where}" +
                  $"&max_days_old={maxDaysOld}&sort_by=date&content-type=application/json";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        var res = await client.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();

        var json    = await res.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        var results = json.GetProperty("results");

        var jobs = new List<ApiRawJob>();
        foreach (var item in results.EnumerateArray())
        {
            try { jobs.Add(MapJob(item, fetchRunId, sponsors)); }
            catch (Exception ex) { Logger.LogWarning(ex, "[AdzunaJobs] Map failed for item"); }
        }
        return jobs;
    }

    private ApiRawJob MapJob(JsonElement j, string fetchRunId, HashSet<string> sponsors)
    {
        var title       = j.TryGetProperty("title",        out var t)   ? t.GetString()   : "Untitled";
        var desc        = j.TryGetProperty("description",  out var d)   ? d.GetString()   : null;
        var applyUrl    = j.TryGetProperty("redirect_url", out var url) ? url.GetString() : null;
        var sourceId    = j.TryGetProperty("id",           out var id)  ? id.GetString()  : null;
        var postDateStr = j.TryGetProperty("created",      out var cr)  ? cr.GetString()  : null;
        var postDate    = ParsePostDate(postDateStr);

        var companyName = j.TryGetProperty("company", out var co) &&
                          co.TryGetProperty("display_name", out var cn) ? cn.GetString() : null;

        string? city = null, state = null;
        if (j.TryGetProperty("location", out var loc) &&
            loc.TryGetProperty("display_name", out var dn))
        {
            var parts = dn.GetString()?.Split(',');
            if (parts?.Length >= 2) { city = parts[0].Trim(); state = parts[1].Trim(); }
        }

        decimal? salMin = null, salMax = null;
        if (j.TryGetProperty("salary_min", out var sn) && sn.ValueKind == JsonValueKind.Number) salMin = sn.GetDecimal();
        if (j.TryGetProperty("salary_max", out var sx) && sx.ValueKind == JsonValueKind.Number) salMax = sx.GetDecimal();

        var contractType = j.TryGetProperty("contract_type", out var ct2)   ? ct2.GetString()   : null;
        var contractTime = j.TryGetProperty("contract_time", out var ctime) ? ctime.GetString() : null;
        var category     = j.TryGetProperty("category",      out var cat)   &&
                           cat.TryGetProperty("label",        out var cl)    ? cl.GetString() : null;

        var mappedContractType = contractType switch
        {
            "permanent" => contractTime == "part_time" ? "PartTime" : "FullTime",
            "contract"  => "Contract",
            _           => contractTime == "part_time" ? "PartTime" : null
        };

        // ── Flag detection — applied to every job ─────────────────────────────
        var isContract    = contractType == "contract" ||
                            ContainsAny(desc, "contract", "contractor");

        var isC2C         = ContainsAny(desc, "c2c", "corp to corp", "corp-to-corp");
        var isW2          = ContainsAny(desc, "w2", "w-2");
        var isFreelance   = ContainsAny(desc, "1099", "independent contractor", "freelance");
        var isPrimeVendor = ContainsAny(desc, "prime vendor", "prime contractors", "direct client", "end client");
        var isStaffing    = ContainsAny(desc, "staffing", "recruiting firm") ||
                            ContainsAny(companyName, "staffing", "consulting", "solutions");
        var isUniversity  = ContainsAny(companyName, "university", "college", "institute", "school", "academia") ||
                            ContainsAny(desc, "university", "academic", "faculty", "tenure");
        var isStartup     = ContainsAny(desc, "startup", "start-up", "series a", "series b", "seed stage", "venture") ||
                            ContainsAny(companyName, "startup", "start-up");
        var isNonProfit   = ContainsAny(desc, "nonprofit", "non-profit", "501(c)", "501c3", "ngo", "not for profit") ||
                            ContainsAny(companyName, "foundation", "nonprofit", "non-profit");
        var isH1B         = ContainsAny(desc, "h1b", "h-1b", "h1-b", "visa sponsor", "will sponsor", "sponsorship available") ||
                            (companyName != null && sponsors.Contains(companyName));

        return new ApiRawJob
        {
            PublicId          = Guid.NewGuid().ToString("N"),
            Source            = "Adzuna",
            SourceId          = sourceId,
            FetchRunId        = fetchRunId,
            JobTitle          = title ?? "Untitled",
            JobLink           = applyUrl,
            JobDescription    = desc,
            City              = city,
            State             = state,
            Country           = "US",
            PostDate          = postDate,
            HoursBackPosted   = ParseHoursBack(postDate),
            SalaryMin         = salMin,
            SalaryMax         = salMax,
            SalaryRangeText   = salMin.HasValue && salMax.HasValue ? $"${salMin:N0}–${salMax:N0}/yr" : null,
            SalaryCurrency    = "USD",
            ContractType      = mappedContractType,
            CompanyName       = companyName,
            Industry          = category,
            // ── All flags evaluated at insert time ────────────────────────────
            IsH1BSponsored    = isH1B,
            IsSponsored       = isH1B || ContainsAny(desc, "sponsor", "visa"),
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

    // ── Role queries (shared cache with AllJobs) ──────────────────────────────

    private async Task<List<string>> LoadQueriesAsync(CancellationToken ct)
    {
        var cached = await _cache.GetAsync<List<string>>(QueriesCacheKey, ct);
        if (cached is { Count: > 0 }) return cached;

        using var scope = _scopeFactory.CreateScope();
        var da      = scope.ServiceProvider.GetRequiredService<IJobFetchDA>();
        var queries = await da.GetActiveJobRoleQueriesAsync(ct);

        if (queries.Count > 0)
        {
            await _cache.SetAsync(QueriesCacheKey, queries, QueriesCacheTtl, ct);
            return queries;
        }

        // Fallback if md.job_roles not seeded yet
        Logger.LogWarning("[AdzunaJobs] md.job_roles is empty — using hardcoded fallback. Run migration 007.");
        return
        [
            "software engineer", "data scientist", "product manager", "financial analyst",
            "registered nurse", "marketing manager", "mechanical engineer", "business analyst",
            "cybersecurity analyst", "recruiter"
        ];
    }

    // Required by base class — used only when SearchQuery is explicitly set
    protected override async Task<List<ApiRawJob>> FetchPageAsync(
        int page, JobFetchInput input, string fetchRunId, CancellationToken ct)
    {
        var sponsors = await LoadSponsorsAsync(ct);
        return await FetchAdzunaPageAsync(input.SearchQuery!, input.Location ?? "United States", page, input.HoursBack, fetchRunId, sponsors, ct);
    }
}
