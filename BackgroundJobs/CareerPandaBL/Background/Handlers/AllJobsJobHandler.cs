// CareerPandaBL/Background/Handlers/AllJobsJobHandler.cs
// SOURCE : JSearch via RapidAPI — aggregates Indeed, LinkedIn, Glassdoor, ZipRecruiter
// API KEY: JobApiSettings:JSearchApiKey  (appsettings.json)
// DOCS   : https://rapidapi.com/letscrape-6bRBa3QguO5/api/jsearch
//
// SCALE STRATEGY — roles × locations
// JSearch pagination dies at page 2-3 for most queries on any plan.
// Instead we expand coverage on TWO dimensions:
//   1. Role queries  — loaded from md.job_roles (200+ roles across all industries)
//   2. US locations  — all 50 states (hardcoded, stable)
// Each combination fires one JSearch call at page=1 → fresh unique results.
// 200 roles × 50 states × ~10 jobs = ~100,000 jobs per full run.
using System.Net.Http.Json;
using System.Text.Json;
using CareerPanda.DataAccess.DA;
using CareerPanda.DataAccess.Entities.Api;
using CareerPanda.Framework.Cache;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CareerPanda.BL.Background.Handlers;

public class AllJobsJobHandler : JobFetchBaseHandler
{
    public override string JobType        => "AllJobs";
    protected override string JobCategory => "AllJobs";
    protected override string ApiSource   => "JSearch";

    // Broad role queries — one per "page" in our loop, always page=1 on JSearch.
    // Covers tech, business, healthcare, finance, engineering, ops, creative, and entry-level.
    // Add more here to increase coverage on future runs.
    private static readonly string[] BroadQueries =
    [
        // ── Software & Engineering ──────────────────────────────────────────
        "software engineer",
        "software developer",
        "full stack developer",
        "backend engineer",
        "frontend developer",
        "mobile developer",
        "android developer",
        "ios developer",
        "devops engineer",
        "site reliability engineer",
        "cloud engineer",
        "solutions architect",
        "platform engineer",
        "embedded systems engineer",
        "firmware engineer",
        // ── Data & AI ───────────────────────────────────────────────────────
        "data scientist",
        "data engineer",
        "data analyst",
        "machine learning engineer",
        "ai engineer",
        "business intelligence analyst",
        "database administrator",
        "data architect",
        // ── Security & Networks ─────────────────────────────────────────────
        "cybersecurity analyst",
        "network engineer",
        "systems administrator",
        "information security engineer",
        // ── Product & Design ────────────────────────────────────────────────
        "product manager",
        "product designer",
        "ux designer",
        "ui designer",
        "technical writer",
        // ── QA & Support ────────────────────────────────────────────────────
        "quality assurance engineer",
        "test engineer",
        "technical support engineer",
        // ── Business & Operations ───────────────────────────────────────────
        "business analyst",
        "project manager",
        "program manager",
        "operations manager",
        "supply chain manager",
        "logistics coordinator",
        "scrum master",
        // ── Finance & Accounting ────────────────────────────────────────────
        "financial analyst",
        "accountant",
        "investment analyst",
        "risk analyst",
        "compliance analyst",
        // ── Marketing & Sales ───────────────────────────────────────────────
        "marketing manager",
        "digital marketing specialist",
        "sales manager",
        "account executive",
        "growth analyst",
        // ── Healthcare ──────────────────────────────────────────────────────
        "registered nurse",
        "physician",
        "pharmacist",
        "physical therapist",
        "medical coder",
        "healthcare analyst",
        // ── Engineering (non-software) ──────────────────────────────────────
        "mechanical engineer",
        "electrical engineer",
        "civil engineer",
        "chemical engineer",
        "manufacturing engineer",
        // ── HR & Legal ──────────────────────────────────────────────────────
        "human resources manager",
        "recruiter",
        "legal counsel",
        "paralegal",
        // ── Entry-level broad sweeps ────────────────────────────────────────
        "entry level engineer",
        "entry level analyst",
        "junior developer",
        "associate product manager",
        "internship engineering",
    ];

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

    private const string QueriesCacheKey = "alljobs:role:queries";
    private static readonly TimeSpan QueriesCacheTtl = TimeSpan.FromHours(6);

    private readonly IHttpClientFactory _http;
    private readonly string _apiKey;
    private readonly string _apiHost;

    public AllJobsJobHandler(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        ICacheService cacheService,
        IConfiguration configuration,
        ILogger<AllJobsJobHandler> logger)
        : base(scopeFactory, cacheService, logger)
    {
        _http    = httpClientFactory;
        _apiKey  = configuration["JobApiSettings:JSearchApiKey"] ?? string.Empty;
        _apiHost = configuration["JobApiSettings:JSearchApiHost"] ?? "jsearch.p.rapidapi.com";
    }

    // ── Override ExecuteAsync to iterate ALL role queries without a MaxPages cap ──

    public override async Task ExecuteAsync(
        JobWorkRequest request,
        IJobProgressReporter progress,
        CancellationToken cancellationToken)
    {
        var input = ParseInput(request.InputPayload);

        // If caller passed a specific query, fall back to base handler (normal MaxPages loop)
        if (input.SearchQuery != null)
        {
            await base.ExecuteAsync(request, progress, cancellationToken);
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var fetchDa = scope.ServiceProvider.GetRequiredService<IJobFetchDA>();

        var queries   = (await LoadQueriesAsync(cancellationToken)).ToList();
        // If caller specified a single location use it; otherwise expand across all 50 US states
        // null → expand across all 50 US states
        // any explicit value (including "United States") → single location search
        var locations = input.Location != null
            ? (string[])[input.Location]
            : UsStates;

        int totalCalls = queries.Count * locations.Length * input.PagesPerQuery;

        var run = new DataAccess.Entities.Api.ApiJobFetchRun
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

        Logger.LogInformation("[AllJobs] Starting — {R} roles × {L} locations × {P} pages = {T} total calls",
            queries.Count, locations.Length, input.PagesPerQuery, totalCalls);

        int totalFetched = 0, totalInserted = 0, totalUpdated = 0,
            totalSkipped = 0, totalErrors  = 0, pagesFetched  = 0, callIndex = 0;

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

                        // Override location per iteration
                        var locInput = input with { Location = location };

                        List<ApiRawJob> jobs;
                        try
                        {
                            jobs = await FetchQueryPageAsync(query, p, locInput, run.Id, cancellationToken);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            Logger.LogError(ex, "[AllJobs] Failed: '{Q}' in {L} page {P}", query, location, p);
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
            "[AllJobs] Done — Roles={R} Locations={L} Fetched={F} Inserted={I} Updated={U} Skipped={S} Errors={E}",
            queries.Count, locations.Length, totalFetched, totalInserted, totalUpdated, totalSkipped, totalErrors);

        await progress.ReportProgressAsync(100,
            $"Done — {queries.Count} roles × {locations.Length} states, Inserted: {totalInserted}, Updated: {totalUpdated}");
    }

    private async Task<IReadOnlyList<string>> LoadQueriesAsync(CancellationToken ct)
    {
        var cached = await _cache.GetAsync<List<string>>(QueriesCacheKey, ct);
        if (cached is { Count: > 0 }) return cached;

        using var scope = _scopeFactory.CreateScope();
        var da      = scope.ServiceProvider.GetRequiredService<IJobFetchDA>();
        var queries = await da.GetActiveJobRoleQueriesAsync(ct);

        if (queries.Count > 0)
        {
            await _cache.SetAsync(QueriesCacheKey, queries, QueriesCacheTtl, ct);
            Logger.LogInformation("[AllJobs] Loaded {Count} role queries from DB", queries.Count);
            return queries;
        }

        // DB not seeded yet — fall back to hardcoded list
        Logger.LogWarning("[AllJobs] md.job_roles is empty — using hardcoded fallback queries. Run migration 007.");
        return BroadQueries;
    }

    private async Task<List<ApiRawJob>> FetchQueryPageAsync(
        string roleQuery, int jsearchPage, JobFetchInput input, string fetchRunId, CancellationToken ct)
    {
        var client     = _http.CreateClient("JSearch");
        var query      = Uri.EscapeDataString(roleQuery);
        var location   = Uri.EscapeDataString(input.Location ?? "United States");
        var datePosted = input.HoursBack <= 24 ? "today"
                       : input.HoursBack <= 72  ? "3days"
                       : input.HoursBack <= 168  ? "week"
                       : "month";

        var url = $"https://jsearch.p.rapidapi.com/search?query={query}%20in%20{location}" +
                  $"&page={jsearchPage}&num_pages=1&date_posted={datePosted}";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("X-RapidAPI-Key",  _apiKey);
        req.Headers.Add("X-RapidAPI-Host", _apiHost);

        var res = await client.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();

        var json = await res.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        var data = json.GetProperty("data");

        var jobs = new List<ApiRawJob>();
        foreach (var item in data.EnumerateArray())
        {
            try { jobs.Add(MapJob(item, fetchRunId)); }
            catch (Exception ex) { Logger.LogWarning(ex, "[AllJobs] Map failed for item"); }
        }
        return jobs;
    }

    protected override async Task<List<ApiRawJob>> FetchPageAsync(
        int page, JobFetchInput input, string fetchRunId, CancellationToken ct)
    {
        var client = _http.CreateClient("JSearch");

        // If caller passed a specific query, use it (and paginate normally).
        // Otherwise rotate through DB role queries — always page=1 on JSearch
        // so each call returns a fresh result set instead of hitting the pagination wall.
        string query;
        int jsearchPage;
        if (input.SearchQuery != null)
        {
            query       = Uri.EscapeDataString(input.SearchQuery);
            jsearchPage = page;
        }
        else
        {
            var queries = await LoadQueriesAsync(ct);
            query       = Uri.EscapeDataString(queries[(page - 1) % queries.Count]);
            jsearchPage = 1;
        }

        var location   = Uri.EscapeDataString(input.Location ?? "United States");
        var datePosted = input.HoursBack <= 24 ? "today"
                       : input.HoursBack <= 72  ? "3days"
                       : input.HoursBack <= 168  ? "week"
                       : "month";

        var url = $"https://jsearch.p.rapidapi.com/search?query={query}%20in%20{location}" +
                  $"&page={jsearchPage}&num_pages=1&date_posted={datePosted}";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("X-RapidAPI-Key",  _apiKey);
        req.Headers.Add("X-RapidAPI-Host", _apiHost);

        var res = await client.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();

        var json = await res.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        var data = json.GetProperty("data");

        var jobs = new List<ApiRawJob>();
        foreach (var item in data.EnumerateArray())
        {
            try { jobs.Add(MapJob(item, fetchRunId)); }
            catch (Exception ex) { Logger.LogWarning(ex, "[AllJobs] Map failed for item"); }
        }
        return jobs;
    }

    private ApiRawJob MapJob(JsonElement j, string fetchRunId)
    {
        var postDate = ParsePostDate(
            j.TryGetProperty("job_posted_at_datetime_utc", out var pd) ? pd.GetString() : null);
        var desc     = j.TryGetProperty("job_description",    out var d)   ? d.GetString()   : null;
        var empType  = j.TryGetProperty("job_employment_type",out var et)  ? et.GetString()  : null;
        var isRemote = j.TryGetProperty("job_is_remote",      out var rem) && rem.GetBoolean();
        var isDirect = j.TryGetProperty("job_apply_is_direct",out var ea)  && ea.GetBoolean();

        decimal? salMin = null, salMax = null;
        if (j.TryGetProperty("job_min_salary", out var sn) && sn.ValueKind == JsonValueKind.Number) salMin = sn.GetDecimal();
        if (j.TryGetProperty("job_max_salary", out var sx) && sx.ValueKind == JsonValueKind.Number) salMax = sx.GetDecimal();

        int? expMonths = null;
        if (j.TryGetProperty("job_required_experience", out var re) && re.ValueKind == JsonValueKind.Object &&
            re.TryGetProperty("required_experience_in_months", out var m) && m.ValueKind == JsonValueKind.Number)
            expMonths = m.GetInt32();

        string[]? skills = null;
        if (j.TryGetProperty("job_required_skills", out var sk) && sk.ValueKind == JsonValueKind.Array)
            skills = sk.EnumerateArray().Select(s => s.GetString()!).Where(s => s != null).ToArray();

        var visaNegation  = ContainsAny(desc,
            "do not sponsor", "does not sponsor", "no sponsorship", "unable to sponsor",
            "cannot sponsor", "will not sponsor", "no h-1b", "no h1b",
            "must be authorized to work", "must have work authorization",
            "authorized to work in the us", "authorized to work in the united states");
        var isH1B         = !visaNegation && ContainsAny(desc, "h1b", "h-1b", "visa sponsor", "will sponsor", "sponsorship available");
        var isOptCpt      = !visaNegation && ContainsAny(desc, " opt ", "opt/cpt", "stem opt", "opt extension", "f-1 visa", " cpt ");
        var isTnVisa      = !visaNegation && ContainsAny(desc, "tn visa", "tn-1", "tn-2", "usmca", "nafta visa");
        var isE3Visa      = !visaNegation && ContainsAny(desc, "e-3", "e3 visa", "e-3 visa");
        var isJ1Visa      = !visaNegation && ContainsAny(desc, "j-1", "j1 visa", "j-1 visa", "exchange visitor");
        var isGreenCard   = !visaNegation && ContainsAny(desc, "green card", "gc sponsor", "perm filing", "eb-2", "eb-3", "labor certification");

        return new ApiRawJob
        {
            PublicId          = Guid.NewGuid().ToString("N"),
            Source            = "JSearch",
            SourceId          = j.TryGetProperty("job_id",           out var jid)  ? jid.GetString()   : null,
            FetchRunId        = fetchRunId,
            JobTitle          = j.TryGetProperty("job_title",         out var jt)   ? jt.GetString()!   : "Untitled",
            JobLink           = j.TryGetProperty("job_apply_link",    out var jl)   ? jl.GetString()    : null,
            JobDescription    = desc,
            City              = j.TryGetProperty("job_city",          out var ct2)  ? ct2.GetString()   : null,
            State             = j.TryGetProperty("job_state",         out var st)   ? st.GetString()    : null,
            Country           = j.TryGetProperty("job_country",       out var cy)   ? cy.GetString()    : null,
            PostDate          = postDate,
            HoursBackPosted   = ParseHoursBack(postDate),
            SalaryMin         = salMin,
            SalaryMax         = salMax,
            SalaryType        = j.TryGetProperty("job_salary_period",   out var sp)  ? sp.GetString()   : null,
            SalaryCurrency    = j.TryGetProperty("job_salary_currency", out var sc)  ? sc.GetString()   : "USD",
            SalaryRangeText   = salMin.HasValue && salMax.HasValue ? $"${salMin:N0}–${salMax:N0}/yr" : null,
            ExperienceYears   = expMonths.HasValue ? expMonths / 12 : null,
            ExperienceMin     = expMonths.HasValue ? expMonths / 12 : null,
            ExperienceMax     = expMonths.HasValue ? (expMonths / 12) + 2 : null,
            WorkType          = isRemote ? "Remote" : "OnSite",
            JobWorkMode       = isRemote ? "Remote" : "OnSite",
            ContractType      = empType switch { "FULLTIME" => "FullTime", "PARTTIME" => "PartTime", "CONTRACTOR" => "Contract", "INTERN" => "Internship", _ => empType },
            ApplyType         = isDirect ? "DirectApply" : "ExternalApply",
            EasyApply         = isDirect,
            CompanyName       = j.TryGetProperty("employer_name",    out var en)  ? en.GetString()  : null,
            CompanyLogoUrl    = j.TryGetProperty("employer_logo",    out var logo) ? logo.GetString() : null,
            CompanyUrl        = j.TryGetProperty("employer_website", out var cw)   ? cw.GetString()   : null,
            CompanyType       = j.TryGetProperty("employer_company_type", out var ctp) ? ctp.GetString() : null,
            Skills            = skills,
            IsContractJob     = empType == "CONTRACTOR",
            IsH1BSponsored    = isH1B,
            IsOptCpt          = isOptCpt,
            IsTnVisa          = isTnVisa,
            IsE3Visa          = isE3Visa,
            IsJ1Visa          = isJ1Visa,
            IsGreenCard       = isGreenCard,
            IsSponsored       = isH1B || isOptCpt || isTnVisa || isE3Visa || isJ1Visa || isGreenCard,
            Status            = true,
            CreatedOn         = DateTime.UtcNow,
            UpdatedOn         = DateTime.UtcNow
        };
    }
}
