// CareerPandaBL/Background/Handlers/JSearchJobsJobHandler.cs
// SOURCE : JSearch via RapidAPI — aggregates Indeed, LinkedIn, Glassdoor, ZipRecruiter
// API KEY: JobApiSettings:JSearchApiKey
//
// SWEEPS (all run inside one ExecuteAsync / one fetch-run row)
//   1. Broad   — roles × US states, all employment types  (was AllJobs + H1BJobs)
//   2. Contract — roles × US states, employment_types=CONTRACTOR (was ContractJobs)
//   3. PrimeVendor — C2C query terms × US states, CONTRACTOR only (was PrimeVendorJobs)
//
// H1B sponsor cross-reference (USCIS DB) is applied to every job in MapJob, so
// IsH1BSponsored is set even when the description doesn't mention H1B explicitly.
// All classification flags are finalized at upsert time by JobClassifier.
using System.Net.Http.Json;
using System.Text.Json;
using CareerPanda.DataAccess.DA;
using CareerPanda.DataAccess.Entities.Api;
using CareerPanda.Framework.Cache;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CareerPanda.BL.Background.Handlers;

public class JSearchJobsJobHandler : JobFetchBaseHandler
{
    public override string JobType        => "JSearchJobs";
    protected override string JobCategory => "JSearchJobs";
    protected override string ApiSource   => "JSearch";

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

    private static readonly string[] PrimeVendorQueries =
    [
        "prime vendor c2c corp to corp",
        "corp to corp contract staffing IT",
        "c2c only w2 contract 1099 independent",
        "prime vendor staffing contract developer"
    ];

    private static readonly string[] FallbackRoleQueries =
    [
        "software engineer", "software developer", "full stack developer",
        "backend engineer", "frontend developer", "data scientist",
        "data engineer", "data analyst", "machine learning engineer",
        "devops engineer", "cloud engineer", "product manager",
        "cybersecurity analyst", "network engineer", "business analyst",
        "financial analyst", "marketing manager", "registered nurse",
        "mechanical engineer", "human resources manager"
    ];

    private const string QueriesCacheKey = "alljobs:role:queries";
    private static readonly TimeSpan QueriesCacheTtl = TimeSpan.FromHours(6);

    private readonly IHttpClientFactory _http;
    private readonly string _apiKey;
    private readonly string _apiHost;

    public JSearchJobsJobHandler(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        ICacheService cacheService,
        IConfiguration configuration,
        ILogger<JSearchJobsJobHandler> logger)
        : base(scopeFactory, cacheService, logger)
    {
        _http    = httpClientFactory;
        _apiKey  = configuration["JobApiSettings:JSearchApiKey"]  ?? string.Empty;
        _apiHost = configuration["JobApiSettings:JSearchApiHost"] ?? "jsearch.p.rapidapi.com";
    }

    // ── Override: 3-sweep execution ──────────────────────────────────────────

    public override async Task ExecuteAsync(
        JobWorkRequest request,
        IJobProgressReporter progress,
        CancellationToken cancellationToken)
    {
        var input    = ParseInput(request.InputPayload);
        var sponsors = await LoadSponsorsAsync(cancellationToken);
        var queries  = await LoadQueriesAsync(cancellationToken);
        var locations = input.Location != null ? (string[])[input.Location] : UsStates;

        int broadCalls = queries.Count * locations.Length * input.PagesPerQuery;
        int contractCalls = queries.Count * locations.Length;
        int pvCalls = PrimeVendorQueries.Length * locations.Length;
        int totalCalls = broadCalls + contractCalls + pvCalls;

        using var scope = _scopeFactory.CreateScope();
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
            MaxPages         = totalCalls,
            LocationFilter   = input.Location ?? "All US States",
            CreatedById      = request.UserId
        };
        await fetchDa.CreateFetchRunAsync(run);

        Logger.LogInformation(
            "[JSearch] Starting — {R} roles × {L} locations × {P} pages (broad={B} contract={C} pv={PV})",
            queries.Count, locations.Length, input.PagesPerQuery, broadCalls, contractCalls, pvCalls);

        int totalFetched = 0, totalInserted = 0, totalUpdated = 0,
            totalSkipped = 0, totalErrors = 0, pagesFetched = 0, callIndex = 0;

        async Task FlushStats() =>
            await fetchDa.UpdateFetchRunStatsAsync(
                run.Id, totalFetched, totalInserted, totalUpdated,
                totalSkipped, totalErrors, pagesFetched);

        try
        {
            // ── Sweep 1: Broad ────────────────────────────────────────────────
            Logger.LogInformation("[JSearch] Sweep 1/3 — Broad ({C} calls)", broadCalls);
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
                        try { jobs = await FetchJSearchPageAsync(query, location, p, input.HoursBack, null, run.Id, sponsors, cancellationToken); }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex) { Logger.LogError(ex, "[JSearch] Broad '{Q}' {L} p{P}", query, location, p); totalErrors++; continue; }
                        if (jobs.Count == 0) break;
                        pagesFetched++; totalFetched += jobs.Count;
                        var (ins, upd, err) = await fetchDa.BulkUpsertRawJobsAsync(ApplyGate(jobs, Logger, "[JSearch]"), cancellationToken);
                        totalInserted += ins; totalUpdated += upd; totalErrors += err;
                        totalSkipped  += jobs.Count - ins - upd - err;
                        await FlushStats();
                        await progress.ReportProgressAsync((int)((double)callIndex / totalCalls * 85),
                            $"[Broad {callIndex}/{totalCalls}] '{query}' {location} — Ins:{totalInserted} Upd:{totalUpdated}");
                        await Task.Delay(InterPageDelayMs, cancellationToken);
                    }
                }
            }

            // ── Sweep 2: Contractor ───────────────────────────────────────────
            Logger.LogInformation("[JSearch] Sweep 2/3 — Contractor ({C} calls)", contractCalls);
            foreach (var query in queries)
            {
                if (cancellationToken.IsCancellationRequested) break;
                foreach (var location in locations)
                {
                    if (cancellationToken.IsCancellationRequested) break;
                    callIndex++;
                    List<ApiRawJob> jobs;
                    try { jobs = await FetchJSearchPageAsync(query, location, 1, input.HoursBack, "CONTRACTOR", run.Id, sponsors, cancellationToken); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { Logger.LogError(ex, "[JSearch] Contract '{Q}' {L}", query, location); totalErrors++; continue; }
                    if (jobs.Count == 0) continue;
                    pagesFetched++; totalFetched += jobs.Count;
                    var (ins, upd, err) = await fetchDa.BulkUpsertRawJobsAsync(ApplyGate(jobs, Logger, "[JSearch]"), cancellationToken);
                    totalInserted += ins; totalUpdated += upd; totalErrors += err;
                    totalSkipped  += jobs.Count - ins - upd - err;
                    await FlushStats();
                    await progress.ReportProgressAsync((int)((double)callIndex / totalCalls * 85),
                        $"[Contract {callIndex}/{totalCalls}] '{query}' {location} — Ins:{totalInserted}");
                    await Task.Delay(InterPageDelayMs, cancellationToken);
                }
            }

            // ── Sweep 3: PrimeVendor ──────────────────────────────────────────
            Logger.LogInformation("[JSearch] Sweep 3/3 — PrimeVendor ({C} calls)", pvCalls);
            foreach (var pvQuery in PrimeVendorQueries)
            {
                if (cancellationToken.IsCancellationRequested) break;
                foreach (var location in locations)
                {
                    if (cancellationToken.IsCancellationRequested) break;
                    callIndex++;
                    List<ApiRawJob> jobs;
                    try { jobs = await FetchJSearchPageAsync(pvQuery, location, 1, input.HoursBack, "CONTRACTOR", run.Id, sponsors, cancellationToken); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { Logger.LogError(ex, "[JSearch] PV '{Q}' {L}", pvQuery, location); totalErrors++; continue; }
                    // Prime vendor sweep: only store jobs that actually mention C2C/prime vendor terms
                    jobs = jobs.Where(j => j.IsPrimeVendor == true || j.IsC2C == true).ToList();
                    if (jobs.Count == 0) continue;
                    pagesFetched++; totalFetched += jobs.Count;
                    var (ins, upd, err) = await fetchDa.BulkUpsertRawJobsAsync(ApplyGate(jobs, Logger, "[JSearch]"), cancellationToken);
                    totalInserted += ins; totalUpdated += upd; totalErrors += err;
                    totalSkipped  += jobs.Count - ins - upd - err;
                    await FlushStats();
                    await progress.ReportProgressAsync((int)((double)callIndex / totalCalls * 85),
                        $"[PV {callIndex}/{totalCalls}] '{pvQuery}' {location} — Ins:{totalInserted}");
                    await Task.Delay(InterPageDelayMs, cancellationToken);
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
            "[JSearch] Done — Fetched={F} Ins={I} Upd={U} Skipped={S} Err={E}",
            totalFetched, totalInserted, totalUpdated, totalSkipped, totalErrors);
        await progress.ReportProgressAsync(100,
            $"Done — Inserted:{totalInserted} Updated:{totalUpdated} Errors:{totalErrors}");
    }

    // ── JSearch API fetch ─────────────────────────────────────────────────────

    private async Task<List<ApiRawJob>> FetchJSearchPageAsync(
        string roleQuery, string location, int page, int hoursBack,
        string? employmentType, string fetchRunId, HashSet<string> sponsors, CancellationToken ct)
    {
        var client     = _http.CreateClient("JSearch");
        var query      = Uri.EscapeDataString(roleQuery);
        var loc        = Uri.EscapeDataString(location);
        var datePosted = hoursBack <= 24 ? "today" : hoursBack <= 72 ? "3days" : hoursBack <= 168 ? "week" : "month";

        var url = $"https://jsearch.p.rapidapi.com/search?query={query}%20in%20{loc}" +
                  $"&page={page}&num_pages=1&date_posted={datePosted}";
        if (!string.IsNullOrEmpty(employmentType))
            url += $"&employment_types={employmentType}";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("X-RapidAPI-Key",  _apiKey);
        req.Headers.Add("X-RapidAPI-Host", _apiHost);

        var res = await client.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();

        var json = await ReadJsonAsync(res.Content, ct);
        var data = json.GetProperty("data");

        var jobs = new List<ApiRawJob>();
        foreach (var item in data.EnumerateArray())
        {
            try { jobs.Add(MapJob(item, fetchRunId, sponsors, roleQuery)); }
            catch (Exception ex) { Logger.LogWarning(ex, "[JSearch] Map failed for item"); }
        }
        return jobs;
    }

    // ── MapJob ────────────────────────────────────────────────────────────────

    private ApiRawJob MapJob(JsonElement j, string fetchRunId, HashSet<string> sponsors, string roleQuery = "")
    {
        var postDate     = ParsePostDate(j.TryGetProperty("job_posted_at_datetime_utc", out var pd)  ? pd.GetString()   : null);
        var desc         = j.TryGetProperty("job_description",     out var d)   ? d.GetString()   : null;
        var empType      = j.TryGetProperty("job_employment_type", out var et)  ? et.GetString()  : null;
        var isRemote     = j.TryGetProperty("job_is_remote",       out var rem) && rem.GetBoolean();
        var isDirect     = j.TryGetProperty("job_apply_is_direct", out var ea)  && ea.GetBoolean();
        var employerName = j.TryGetProperty("employer_name",       out var en)  ? en.GetString()  : null;

        decimal? salMin = null, salMax = null;
        if (j.TryGetProperty("job_min_salary", out var sn) && sn.ValueKind == JsonValueKind.Number) salMin = sn.GetDecimal();
        if (j.TryGetProperty("job_max_salary", out var sx) && sx.ValueKind == JsonValueKind.Number) salMax = sx.GetDecimal();
        var salType = NormalizeSalaryPeriod(j.TryGetProperty("job_salary_period", out var sp) ? sp.GetString() : null);

        int? expMonths = null;
        if (j.TryGetProperty("job_required_experience", out var re) && re.ValueKind == JsonValueKind.Object &&
            re.TryGetProperty("required_experience_in_months", out var m) && m.ValueKind == JsonValueKind.Number)
            expMonths = m.GetInt32();

        string[]? skills = null;
        if (j.TryGetProperty("job_required_skills", out var sk) && sk.ValueKind == JsonValueKind.Array)
            skills = sk.EnumerateArray().Select(s => s.GetString()!).Where(s => s != null).ToArray();

        var benefits     = j.TryGetProperty("job_benefits", out var ben) && ben.ValueKind == JsonValueKind.String ? ben.GetString() : null;
        var requirements = JobFetchHelpers.ExtractJobHighlights(j);

        // ── Visa flags ────────────────────────────────────────────────────────
        var visaNegation = ContainsAny(desc,
            "do not sponsor", "does not sponsor", "no sponsorship", "unable to sponsor",
            "cannot sponsor", "will not sponsor", "no h-1b", "no h1b",
            "must be authorized to work", "must have work authorization",
            "authorized to work in the us", "authorized to work in the united states");

        // H1B: keyword match OR employer is a known USCIS sponsor
        var isH1B = !visaNegation && (
            ContainsAny(desc, "h1b", "h-1b", "h1-b", "visa sponsor", "will sponsor",
                        "sponsorship available", "open to sponsorship", "sponsorship provided", "h1b transfer") ||
            (employerName != null && (sponsors.Contains(employerName) ||
                                      sponsors.Contains(NormalizeCompanyName(employerName)))));
        var isOptCpt    = !visaNegation && ContainsAny(desc, " opt ", "opt/cpt", "stem opt", "opt extension", "f-1 visa", " cpt ");
        var isTnVisa    = !visaNegation && ContainsAny(desc, "tn visa", "tn-1", "tn-2", "usmca", "nafta visa");
        var isE3Visa    = !visaNegation && ContainsAny(desc, "e-3", "e3 visa", "e-3 visa");
        var isJ1Visa    = !visaNegation && ContainsAny(desc, "j-1", "j1 visa", "j-1 visa", "exchange visitor");
        var isGreenCard = !visaNegation && ContainsAny(desc, "green card", "gc sponsor", "perm filing", "eb-2", "eb-3", "labor certification");

        // ── Contract flags ────────────────────────────────────────────────────
        var isC2C        = ContainsAny(desc, "c2c", "corp to corp", "corp-to-corp");
        var isW2         = ContainsAny(desc, "w2", "w-2") || empType == "FULLTIME";
        var isC2H        = ContainsAny(desc, "contract to hire", "contract-to-hire", "c2h",
                               "right to hire", "right-to-hire", "temp to perm", "temp-to-perm");
        var is1099       = ContainsAny(desc, "1099", "independent contractor");
        var isPrimeVendor = ContainsAny(desc, "prime vendor", "prime contractors", "direct client", "end client");
        var isStaffing   = ContainsAny(desc, "staffing", "recruiting firm") ||
                           ContainsAny(employerName, "staffing", "consulting", "solutions");

        return new ApiRawJob
        {
            PublicId          = Guid.NewGuid().ToString("N"),
            Source            = "JSearch",
            SourceId          = j.TryGetProperty("job_id",           out var jid)  ? jid.GetString()   : null,
            FetchRunId        = fetchRunId,
            JobTitle          = j.TryGetProperty("job_title",         out var jt)   ? jt.GetString()!   : "Untitled",
            JobLink           = j.TryGetProperty("job_apply_link",    out var jl)   ? jl.GetString()    : null,
            JobDescription    = desc,
            City              = j.TryGetProperty("job_city",    out var ct2) ? ct2.GetString() : null,
            State             = NormalizeState(j.TryGetProperty("job_state",   out var st)  ? st.GetString()  : null),
            Country           = JobFetchHelpers.NormalizeJobCountry(j.TryGetProperty("job_country", out var cy) ? cy.GetString() : null),
            PostDate          = postDate,
            HoursBackPosted   = ParseHoursBack(postDate),
            SalaryMin         = salMin,
            SalaryMax         = salMax,
            SalaryType        = salType,
            SalaryCurrency    = j.TryGetProperty("job_salary_currency", out var sc) ? sc.GetString() : "USD",
            SalaryRangeText   = BuildSalaryRangeText(salMin, salMax, salType),
            JobBenefits       = benefits,
            ExperienceYears   = expMonths.HasValue ? expMonths / 12 : null,
            ExperienceMin     = expMonths.HasValue ? expMonths / 12 : null,
            ExperienceMax     = expMonths.HasValue ? (expMonths / 12) + 2 : null,
            WorkType          = isRemote ? "Remote" : "OnSite",
            JobWorkMode       = isRemote ? "Remote" : "OnSite",
            ContractType      = empType switch { "FULLTIME" => "FullTime", "PARTTIME" => "PartTime", "CONTRACTOR" => "Contract", "INTERN" => "Internship", _ => "FullTime" },
            JobLevel          = NormalizeJobLevel(j.TryGetProperty("job_title", out var jt2) ? jt2.GetString() : null),
            ApplyType         = isDirect ? "DirectApply" : "ExternalApply",
            EasyApply         = isDirect,
            CompanyName       = employerName,
            CompanyLogoUrl    = j.TryGetProperty("employer_logo",         out var logo) ? logo.GetString() : null,
            CompanyUrl        = j.TryGetProperty("employer_website",      out var cw)   ? cw.GetString()   : null,
            CompanyType       = j.TryGetProperty("employer_company_type", out var ctp)  ? ctp.GetString()  : null,
            // roleQuery comes from md.job_roles.search_query which has an industry_id —
            // store it as raw Industry text so the NormalizeJobs handler can alias-match it.
            Industry          = roleQuery,
            Skills            = skills,
            Requirements      = requirements,
            IsH1BSponsored    = isH1B,
            IsOptCpt          = isOptCpt,
            IsTnVisa          = isTnVisa,
            IsE3Visa          = isE3Visa,
            IsJ1Visa          = isJ1Visa,
            IsGreenCard       = isGreenCard,
            IsSponsored       = isH1B || isOptCpt || isTnVisa || isE3Visa || isJ1Visa || isGreenCard,
            IsContractJob     = empType == "CONTRACTOR",
            IsContractToHire  = isC2H,
            IsC2C             = isC2C,
            IsW2              = isW2,
            IsFreelanceJob    = is1099,
            IsPrimeVendor     = isPrimeVendor,
            IsStaffing        = isStaffing,
            Status            = true,
            CreatedOn         = DateTime.UtcNow,
            UpdatedOn         = DateTime.UtcNow
        };
    }

    // ── Required by base class ────────────────────────────────────────────────
    protected override async Task<List<ApiRawJob>> FetchPageAsync(
        int page, JobFetchInput input, string fetchRunId, CancellationToken ct)
    {
        var sponsors = await LoadSponsorsAsync(ct);
        var queries  = await LoadQueriesAsync(ct);
        var query    = input.SearchQuery ?? queries[(page - 1) % queries.Count];
        return await FetchJSearchPageAsync(query, input.Location ?? "United States", 1,
            input.HoursBack, null, fetchRunId, sponsors, ct);
    }

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

        Logger.LogWarning("[JSearch] md.job_roles is empty — using hardcoded fallback. Run migration 007.");
        return [.. FallbackRoleQueries];
    }
}
