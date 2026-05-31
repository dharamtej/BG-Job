// CareerPandaBL/Background/Handlers/UsaJobsJobHandler.cs
// SOURCE : USAJobs.gov — Official US Federal Government job board (100% FREE)
// API KEY: JobApiSettings:UsaJobsAuthKey + JobApiSettings:UsaJobsUserAgent
// SIGNUP : https://developer.usajobs.gov/  (free, instant approval)
// DOCS   : https://developer.usajobs.gov/APIRequest/Index
//
// SWEEPS (run inside one ExecuteAsync / one fetch-run row)
//   1. Government — no keyword, full HiringPath + clearance parsing
//   2. University — keyword="university", forces IsUniversityJob=true
//   3. Role queries — each active md.job_roles.search_query as Keyword filter
//
// All US federal jobs: W2, no visa sponsorship, citizenship required.
// USAJobs SASE gateway blocks rapid bursts — 3s inter-page delay.
using System.Net.Http.Json;
using System.Text.Json;
using CareerPanda.DataAccess.DA;
using CareerPanda.DataAccess.Entities.Api;
using CareerPanda.Framework.Cache;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CareerPanda.BL.Background.Handlers;

public class UsaJobsJobHandler : JobFetchBaseHandler
{
    public override string JobType        => "UsaJobs";
    protected override string JobCategory => "UsaJobs";
    protected override string ApiSource   => "USAJobs";

    protected override int InterPageDelayMs => 3000;

    // (keyword, forceIsUniversity, log tag)
    private static readonly (string? Keyword, bool ForceUniversity, string Tag)[] Sweeps =
    [
        (null,         false, "Government"),
        ("university", true,  "University"),
    ];

    private readonly IHttpClientFactory _http;
    private readonly string _authKey;
    private readonly string _userAgent;

    public UsaJobsJobHandler(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        ICacheService cacheService,
        IConfiguration configuration,
        ILogger<UsaJobsJobHandler> logger)
        : base(scopeFactory, cacheService, logger)
    {
        _http      = httpClientFactory;
        _authKey   = configuration["JobApiSettings:UsaJobsAuthKey"]  ?? string.Empty;
        _userAgent = configuration["JobApiSettings:UsaJobsUserAgent"] ?? "CareerPanda";
    }

    // ── Override: multi-sweep execution ──────────────────────────────────────

    public override async Task ExecuteAsync(
        JobWorkRequest request,
        IJobProgressReporter progress,
        CancellationToken cancellationToken)
    {
        var input = ParseInput(request.InputPayload);

        using var scope = _scopeFactory.CreateScope();
        var fetchDa = scope.ServiceProvider.GetRequiredService<IJobFetchDA>();

        // Build full sweep list: fixed sweeps + one per active job role query
        var roleQueries = await fetchDa.GetActiveJobRoleQueriesAsync(cancellationToken);
        var allSweeps = Sweeps
            .Select(s => (s.Keyword, s.ForceUniversity, s.Tag))
            .Concat(roleQueries.Select(q => ((string?)q, false, q)))
            .ToList();

        var run = new ApiJobFetchRun
        {
            Id               = request.JobId,
            BackgroundTaskId = request.JobId,
            JobCategory      = JobCategory,
            ApiSource        = ApiSource,
            Status           = "Running",
            StartedAt        = DateTime.UtcNow,
            HoursBack        = input.HoursBack,
            MaxPages         = allSweeps.Count * input.MaxPages,
            LocationFilter   = "United States (USAJobs.gov)",
            CreatedById      = request.UserId
        };
        await fetchDa.CreateFetchRunAsync(run);

        Logger.LogInformation("[USAJobs] Starting — {Fixed} fixed sweeps + {Roles} role queries = {Total} total sweeps",
            Sweeps.Length, roleQueries.Count, allSweeps.Count);

        int totalFetched = 0, totalInserted = 0, totalUpdated = 0,
            totalSkipped = 0, totalErrors = 0, pagesFetched = 0, sweepIndex = 0;

        try
        {
            foreach (var (keyword, forceUniversity, tag) in allSweeps)
            {
                if (cancellationToken.IsCancellationRequested) break;
                Logger.LogInformation("[USAJobs] [{I}/{T}] Sweep '{Tag}' (keyword='{K}')",
                    sweepIndex + 1, allSweeps.Count, tag, keyword ?? "(none)");

                for (int page = 1; page <= input.MaxPages; page++)
                {
                    if (cancellationToken.IsCancellationRequested) break;

                    List<ApiRawJob> jobs;
                    try { jobs = await FetchUsaJobsPageAsync(page, keyword ?? input.SearchQuery, forceUniversity, run.Id, cancellationToken); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { Logger.LogError(ex, "[USAJobs] {T} page {P} failed", tag, page); totalErrors++; continue; }

                    if (jobs.Count == 0) { Logger.LogInformation("[USAJobs] {T} — no more results at page {P}", tag, page); break; }

                    pagesFetched++; totalFetched += jobs.Count;
                    (int ins, int upd, int err) = await fetchDa.BulkUpsertRawJobsAsync(ApplyGate(jobs, Logger, "[USAJobs]"), cancellationToken);
                    totalInserted += ins; totalUpdated += upd; totalErrors += err;
                    totalSkipped  += jobs.Count - ins - upd - err;

                    await fetchDa.UpdateFetchRunStatsAsync(run.Id, totalFetched, totalInserted, totalUpdated, totalSkipped, totalErrors, pagesFetched);

                    int pct = (int)(((sweepIndex * input.MaxPages + page) / (double)(allSweeps.Count * input.MaxPages)) * 90);
                    await progress.ReportProgressAsync(pct,
                        $"[{sweepIndex + 1}/{allSweeps.Count}] {tag} p{page} — Inserted:{totalInserted} Updated:{totalUpdated}");
                    await Task.Delay(InterPageDelayMs, cancellationToken);
                }
                sweepIndex++;
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

        Logger.LogInformation("[USAJobs] Done — Fetched={F} Ins={I} Upd={U} Err={E}",
            totalFetched, totalInserted, totalUpdated, totalErrors);
        await progress.ReportProgressAsync(100,
            $"Done — Inserted:{totalInserted} Updated:{totalUpdated} Errors:{totalErrors}");
    }

    // ── USAJobs API fetch ─────────────────────────────────────────────────────

    private async Task<List<ApiRawJob>> FetchUsaJobsPageAsync(
        int page, string? keyword, bool forceUniversity, string fetchRunId, CancellationToken ct)
    {
        var client = _http.CreateClient("USAJobs");
        var kw     = string.IsNullOrWhiteSpace(keyword) ? "" : $"&Keyword={Uri.EscapeDataString(keyword)}";
        var url    = $"https://data.usajobs.gov/api/search?ResultsPerPage=25&Page={page}&DatePosted=120{kw}";

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("Authorization-Key", _authKey);
            req.Headers.Add("User-Agent",        _userAgent);

            var res = await client.SendAsync(req, ct);

            if (res.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                Logger.LogWarning("[USAJobs] 403 on page {P} (attempt {A}) — rate limited, stopping sweep", page, attempt);
                return [];
            }
            if (res.StatusCode == System.Net.HttpStatusCode.TooManyRequests ||
                res.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
            {
                if (attempt == 3) { Logger.LogWarning("[USAJobs] All retries exhausted page {P}", page); return []; }
                int backoff = (int)Math.Pow(2, attempt + 1) * 1000;
                Logger.LogWarning("[USAJobs] {Code} page {P} attempt {A} — retry in {B}ms", (int)res.StatusCode, page, attempt, backoff);
                await Task.Delay(backoff, ct);
                continue;
            }

            res.EnsureSuccessStatusCode();

            var json = await ReadJsonAsync(res.Content, ct);
            var jobs = new List<ApiRawJob>();

            if (!json.TryGetProperty("SearchResult", out var sr)) return jobs;

            if (page == 1 && sr.TryGetProperty("SearchResultCountAll", out var totalEl))
            {
                int total = 0;
                if (totalEl.ValueKind == JsonValueKind.Number) totalEl.TryGetInt32(out total);
                else if (totalEl.ValueKind == JsonValueKind.String) int.TryParse(totalEl.GetString(), out total);
                if (total > 0)
                    Logger.LogInformation("[USAJobs] Total available: {Total} (~{Pages} pages)", total, (int)Math.Ceiling(total / 25.0));
            }

            if (!sr.TryGetProperty("SearchResultItems", out var items)) return jobs;

            foreach (var item in items.EnumerateArray())
            {
                try
                {
                    if (!item.TryGetProperty("MatchedObjectDescriptor", out var desc)) continue;
                    jobs.Add(MapJob(desc, fetchRunId, forceUniversity));
                }
                catch (Exception ex) { Logger.LogWarning(ex, "[USAJobs] Map failed"); }
            }
            return jobs;
        }

        return [];
    }

    // ── MapJob ────────────────────────────────────────────────────────────────

    private static ApiRawJob MapJob(JsonElement d, string fetchRunId, bool forceUniversity)
    {
        // ── Location ──────────────────────────────────────────────────────────
        string? city = null, state = null;
        if (d.TryGetProperty("PositionLocation", out var locs) && locs.ValueKind == JsonValueKind.Array)
        {
            var first = locs.EnumerateArray().FirstOrDefault();
            if (first.ValueKind == JsonValueKind.Object)
            {
                city  = first.TryGetProperty("CityName",               out var cn)  ? cn.GetString()  : null;
                state = first.TryGetProperty("CountrySubDivisionCode", out var csc) ? csc.GetString() : null;
                if (string.IsNullOrWhiteSpace(city) && first.TryGetProperty("LocationName", out var ln))
                {
                    var parts = (ln.GetString() ?? "").Split(',', StringSplitOptions.TrimEntries);
                    if (parts.Length >= 2) { city = parts[0]; state = parts[^1]; }
                    else if (parts.Length == 1) city = parts[0];
                }
            }
        }

        // ── Salary ────────────────────────────────────────────────────────────
        decimal? salMin = null, salMax = null; string? salType = null;
        if (d.TryGetProperty("PositionRemuneration", out var rem) && rem.ValueKind == JsonValueKind.Array)
        {
            var first = rem.EnumerateArray().FirstOrDefault();
            if (first.ValueKind == JsonValueKind.Object)
            {
                if (first.TryGetProperty("MinimumRange", out var mn) && decimal.TryParse(mn.GetString(), out var mnV)) salMin = mnV;
                if (first.TryGetProperty("MaximumRange", out var mx) && decimal.TryParse(mx.GetString(), out var mxV)) salMax = mxV;
                if (first.TryGetProperty("RateIntervalCode", out var ric)) salType = NormalizeSalaryPeriod(ric.GetString());
            }
        }

        // ── Job link ──────────────────────────────────────────────────────────
        var jobLink = d.TryGetProperty("PositionURI", out var uri) ? uri.GetString() : null;
        if (string.IsNullOrWhiteSpace(jobLink) &&
            d.TryGetProperty("ApplyURI", out var applyUri) && applyUri.ValueKind == JsonValueKind.Array)
            jobLink = applyUri.EnumerateArray().FirstOrDefault().GetString();

        // ── Schedule / contract type ──────────────────────────────────────────
        string? schedule = null;
        if (d.TryGetProperty("PositionSchedule", out var sched) && sched.ValueKind == JsonValueKind.Array)
        {
            var first = sched.EnumerateArray().FirstOrDefault();
            if (first.ValueKind == JsonValueKind.Object && first.TryGetProperty("Name", out var sn))
                schedule = sn.GetString();
        }

        string contractType = "FullTime";
        if (d.TryGetProperty("PositionOfferingType", out var pot) && pot.ValueKind == JsonValueKind.Array)
        {
            var first = pot.EnumerateArray().FirstOrDefault();
            if (first.ValueKind == JsonValueKind.Object && first.TryGetProperty("Name", out var pn))
            {
                var raw = pn.GetString() ?? "";
                contractType = raw.ToLowerInvariant() switch
                {
                    var s when s.Contains("permanent") || s.Contains("career") || s.Contains("regular") || s.Contains("federal") => "FullTime",
                    var s when s.Contains("temporary") || s.Contains("term") || s.Contains("seasonal") || s.Contains("nte")      => "Temporary",
                    var s when s.Contains("intern")                                                                                => "Internship",
                    var s when s.Contains("part")                                                                                  => "PartTime",
                    var s when s.Contains("contract") || s.Contains("excepted")                                                   => "Contract",
                    _ => "FullTime"
                };
            }
        }

        // ── Industry / skills from JobCategory ────────────────────────────────
        string? industry = null; string[]? skills = null;
        if (d.TryGetProperty("JobCategory", out var cats) && cats.ValueKind == JsonValueKind.Array)
        {
            var names = cats.EnumerateArray()
                .Where(x => x.TryGetProperty("Name", out _))
                .Select(x => x.GetProperty("Name").GetString()!)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToArray();
            if (names.Length > 0) { industry = names[0]; skills = names; }
        }

        // ── HiringPath + security clearance ──────────────────────────────────
        bool isClearance = false, isVeterans = false;
        if (d.TryGetProperty("UserArea", out var ua) && ua.ValueKind == JsonValueKind.Object &&
            ua.TryGetProperty("Details", out var details) && details.ValueKind == JsonValueKind.Object)
        {
            if (details.TryGetProperty("HiringPath", out var hp) && hp.ValueKind == JsonValueKind.Array)
            {
                var paths = hp.EnumerateArray().Select(x => x.GetString()).Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
                isVeterans = paths.Any(p => p!.Contains("veteran", StringComparison.OrdinalIgnoreCase));
            }
            if (details.TryGetProperty("TenantRequirements", out var tr) && tr.ValueKind == JsonValueKind.String)
            {
                var req = tr.GetString() ?? "";
                isClearance = req.Contains("clearance", StringComparison.OrdinalIgnoreCase) ||
                              req.Contains("secret", StringComparison.OrdinalIgnoreCase);
            }
        }

        // ── Description ───────────────────────────────────────────────────────
        string? desc = null;
        if (d.TryGetProperty("UserArea", out var ua2) && ua2.ValueKind == JsonValueKind.Object &&
            ua2.TryGetProperty("Details", out var det2) && det2.ValueKind == JsonValueKind.Object &&
            det2.TryGetProperty("JobSummary", out var js) && js.ValueKind == JsonValueKind.String)
            desc = js.GetString();
        if (string.IsNullOrWhiteSpace(desc))
            desc = d.TryGetProperty("QualificationSummary", out var qs) ? qs.GetString() : null;

        // ── Work type (telework) ──────────────────────────────────────────────
        var workType = "OnSite"; var jobWorkMode = "OnSite";
        if (d.TryGetProperty("UserArea", out var ua3) && ua3.ValueKind == JsonValueKind.Object &&
            ua3.TryGetProperty("Details", out var det3) && det3.ValueKind == JsonValueKind.Object &&
            det3.TryGetProperty("TeleworkEligible", out var te))
        {
            if (te.ValueKind == JsonValueKind.True ||
                (te.ValueKind == JsonValueKind.String && te.GetString()?.Contains("eligible", StringComparison.OrdinalIgnoreCase) == true))
            { workType = "Hybrid"; jobWorkMode = "Hybrid"; }
        }

        // ── Organization ──────────────────────────────────────────────────────
        var orgName     = d.TryGetProperty("OrganizationName", out var on) ? on.GetString() : null;
        var deptName    = d.TryGetProperty("DepartmentName",   out var dn) ? dn.GetString() : null;
        var companyName = orgName ?? deptName;

        // University: forced true for the university sweep; also detected from org name
        var isUniversity = forceUniversity ||
                           ContainsAny(orgName, "university", "college", "academy", "institute of technology",
                               "naval postgraduate", "national defense university") ||
                           ContainsAny(deptName, "university", "college", "academy");

        var postDate  = ParsePostDate(d.TryGetProperty("PublicationStartDate", out var psd) ? psd.GetString() : null);
        var closeDate = ParsePostDate(d.TryGetProperty("ApplicationCloseDate", out var acd) ? acd.GetString() : null);

        return new ApiRawJob
        {
            PublicId          = Guid.NewGuid().ToString("N"),
            Source            = "USAJobs",
            SourceId          = d.TryGetProperty("PositionID", out var pid) ? pid.GetString() : null,
            FetchRunId        = fetchRunId,
            JobTitle          = d.TryGetProperty("PositionTitle", out var pt) ? pt.GetString()! : "Untitled",
            JobLink           = jobLink,
            JobDescription    = desc,
            City              = city,
            State             = state,
            Country           = "US",
            PostDate          = postDate,
            LastDate          = closeDate,
            HoursBackPosted   = ParseHoursBack(postDate),
            SalaryMin         = salMin,
            SalaryMax         = salMax,
            SalaryType        = salType,
            SalaryRangeText   = BuildSalaryRangeText(salMin, salMax, salType),
            SalaryCurrency    = "USD",
            WorkType          = workType,
            JobWorkMode       = jobWorkMode,
            ContractType      = contractType,
            JobLevel          = NormalizeJobLevel(d.TryGetProperty("PositionTitle", out var ptl) ? ptl.GetString() : null),
            Industry          = industry,
            Skills            = skills,
            ApplyType         = "ExternalApply",
            CompanyName       = companyName,
            CompanyType       = isUniversity ? "University" : "Government",
            // Federal jobs require US citizenship — no visa sponsorship
            IsH1BSponsored    = false,
            IsOptCpt          = false,
            IsTnVisa          = false,
            IsE3Visa          = false,
            IsJ1Visa          = false,
            IsGreenCard       = false,
            IsSponsored       = false,
            IsW2              = true,
            IsC2C             = false,
            IsContractJob     = contractType?.Contains("Temporary", StringComparison.OrdinalIgnoreCase) == true ||
                                contractType?.Contains("Term", StringComparison.OrdinalIgnoreCase) == true,
            IsFreelanceJob    = false,
            IsPrimeVendor     = false,
            IsStaffing        = false,
            IsStartupJob      = false,
            IsNonProfitJob    = false,
            IsUniversityJob               = isUniversity,
            IsSecurityClearanceRequired   = isClearance || ContainsAny(desc,
                "security clearance", "secret clearance", "top secret", "ts/sci",
                "public trust", "clearance required", "active clearance"),
            IsVeteransEligible            = isVeterans,
            Status            = true,
            CreatedOn         = DateTime.UtcNow,
            UpdatedOn         = DateTime.UtcNow
        };
    }

    private static DateTime? ParsePostDate(string? raw) =>
        !string.IsNullOrWhiteSpace(raw) && DateTime.TryParse(raw, out var dt) ? dt.ToUniversalTime() : null;

    private static int? ParseHoursBack(DateTime? d) =>
        d.HasValue ? (int)(DateTime.UtcNow - d.Value).TotalHours : null;

    // ── Required by base class ────────────────────────────────────────────────
    protected override async Task<List<ApiRawJob>> FetchPageAsync(
        int page, JobFetchInput input, string fetchRunId, CancellationToken ct)
        => await FetchUsaJobsPageAsync(page, input.SearchQuery, false, fetchRunId, ct);
}
