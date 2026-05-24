// CareerPandaBL/Background/Handlers/H1BJobsJobHandler.cs
// SOURCE : JSearch via RapidAPI — queries "h1b visa sponsorship" keywords
//          Cross-references USCIS FY2026 H1B employer data (api.h1b_sponsors table)
// API KEY: JobApiSettings:JSearchApiKey
using System.Net.Http.Json;
using System.Text.Json;
using CareerPanda.DataAccess.DA;
using CareerPanda.DataAccess.Entities.Api;
using CareerPanda.Framework.Cache;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CareerPanda.BL.Background.Handlers;

public class H1BJobsJobHandler : JobFetchBaseHandler
{
    public override string JobType        => "H1BJobs";
    protected override string JobCategory => "H1BJobs";
    protected override string ApiSource   => "JSearch";

    // Each entry targets a different sponsor group — page N uses entry (N-1) % length
    // so every page fetches fresh results instead of paginating the same query (which dies at page 2-3 on free tier)
    private static readonly string[] H1BSponsorQueries =
    [
        "software engineer developer amazon google",
        "software engineer developer microsoft apple meta",
        "software engineer developer infosys tata cognizant",
        "software engineer developer wipro hcl accenture capgemini",
        "software engineer developer ibm oracle salesforce",
        "software engineer developer intel nvidia qualcomm cisco",
        "software engineer developer vmware adobe jpmorgan",
        "software engineer developer deloitte kpmg pwc ernst young",
        "software engineer developer goldman sachs morgan stanley",
        "software engineer developer facebook twitter linkedin"
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

    private readonly IHttpClientFactory _http;
    private readonly string _apiKey;
    private readonly string _apiHost;

    public H1BJobsJobHandler(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        ICacheService cacheService,
        IConfiguration configuration,
        ILogger<H1BJobsJobHandler> logger)
        : base(scopeFactory, cacheService, logger)
    {
        _http    = httpClientFactory;
        _apiKey  = configuration["JobApiSettings:JSearchApiKey"] ?? string.Empty;
        _apiHost = configuration["JobApiSettings:JSearchApiHost"] ?? "jsearch.p.rapidapi.com";
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
        var fetchDa  = scope.ServiceProvider.GetRequiredService<IJobFetchDA>();
        var sponsors = await LoadSponsorsAsync(cancellationToken);

        // Load role queries same as AllJobs
        var queries   = await fetchDa.GetActiveJobRoleQueriesAsync(cancellationToken);
        if (queries.Count == 0) queries = [.. H1BSponsorQueries];

        // null → expand across all 50 US states
        // any explicit value (including "United States") → single location search
        var locations = input.Location != null
            ? [input.Location]
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

        Logger.LogInformation("[H1BJobs] Starting — {R} roles × {L} locations × {P} pages = {T} total calls",
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
                        var locInput = input with { Location = location, SearchQuery = query };

                        List<ApiRawJob> jobs;
                        try
                        {
                            jobs = await FetchAndFilterAsync(locInput, run.Id, sponsors, cancellationToken);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            Logger.LogError(ex, "[H1BJobs] Failed: '{Q}' in {L}", query, location);
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
                            $"[{callIndex}/{totalCalls}] '{query}' in {location} — H1B Inserted: {totalInserted}");

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
            "[H1BJobs] Done — Roles={R} Locations={L} Fetched={F} Inserted={I} Updated={U} Errors={E}",
            queries.Count, locations.Length, totalFetched, totalInserted, totalUpdated, totalErrors);

        await progress.ReportProgressAsync(100,
            $"Done — {queries.Count} roles × {locations.Length} states, H1B Inserted: {totalInserted}, Updated: {totalUpdated}");
    }

    private async Task<List<ApiRawJob>> FetchAndFilterAsync(
        JobFetchInput input, string fetchRunId, HashSet<string> sponsors, CancellationToken ct)
    {
        var client     = _http.CreateClient("JSearch");
        var query      = Uri.EscapeDataString(input.SearchQuery!);
        var location   = Uri.EscapeDataString(input.Location ?? "United States");
        var datePosted = input.HoursBack <= 24 ? "today"
                       : input.HoursBack <= 72  ? "3days"
                       : input.HoursBack <= 168  ? "week"
                       : "month";

        var url = $"https://jsearch.p.rapidapi.com/search?query={query}%20in%20{location}" +
                  $"&page=1&num_pages=1&date_posted={datePosted}";

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
            try
            {
                var job = MapJob(item, fetchRunId, sponsors);
                if (job.IsH1BSponsored == true) jobs.Add(job);
            }
            catch (Exception ex) { Logger.LogWarning(ex, "[H1BJobs] Map failed"); }
        }
        return jobs;
    }

    protected override async Task<List<ApiRawJob>> FetchPageAsync(
        int page, JobFetchInput input, string fetchRunId, CancellationToken ct)
    {
        var sponsors = await LoadSponsorsAsync(ct);

        var client     = _http.CreateClient("JSearch");
        var baseQuery  = input.SearchQuery ?? H1BSponsorQueries[(page - 1) % H1BSponsorQueries.Length];
        var query      = Uri.EscapeDataString(baseQuery);
        var location   = Uri.EscapeDataString(input.Location ?? "United States");
        var datePosted = input.HoursBack <= 24 ? "today" : input.HoursBack <= 72 ? "3days" : input.HoursBack <= 168 ? "week" : "month";

        var url = $"https://jsearch.p.rapidapi.com/search?query={query}%20in%20{location}" +
                  $"&page=1&num_pages=1&date_posted={datePosted}";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("X-RapidAPI-Key",  _apiKey);
        req.Headers.Add("X-RapidAPI-Host", _apiHost);

        var res  = await client.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();

        var json = await res.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        var data = json.GetProperty("data");

        var jobs = new List<ApiRawJob>();
        foreach (var item in data.EnumerateArray())
        {
            try
            {
                var job = MapJob(item, fetchRunId, sponsors);
                if (job.IsH1BSponsored == true) jobs.Add(job);
            }
            catch (Exception ex) { Logger.LogWarning(ex, "[H1BJobs] Map failed"); }
        }
        return jobs;
    }

    private ApiRawJob MapJob(JsonElement j, string fetchRunId, HashSet<string> sponsors)
    {
        var postDate     = ParsePostDate(j.TryGetProperty("job_posted_at_datetime_utc", out var pd)  ? pd.GetString()   : null);
        var desc         = j.TryGetProperty("job_description",    out var d)   ? d.GetString()   : null;
        var empType      = j.TryGetProperty("job_employment_type",out var et)  ? et.GetString()  : null;
        var isRemote     = j.TryGetProperty("job_is_remote",      out var rem) && rem.GetBoolean();
        var employerName = j.TryGetProperty("employer_name",      out var en)  ? en.GetString()  : null;

        var isH1B = ContainsAny(desc, "h1b","h-1b","h1-b","h 1b","visa sponsor","will sponsor","sponsorship available","work authorization") ||
                    (employerName != null &&
                        (sponsors.Contains(employerName) ||
                         sponsors.Contains(NormalizeCompanyName(employerName))));

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

        return new ApiRawJob
        {
            PublicId          = Guid.NewGuid().ToString("N"),
            Source            = "JSearch",
            SourceId          = j.TryGetProperty("job_id",          out var jid) ? jid.GetString()  : null,
            FetchRunId        = fetchRunId,
            JobTitle          = j.TryGetProperty("job_title",        out var jt)  ? jt.GetString()!  : "Untitled",
            JobLink           = j.TryGetProperty("job_apply_link",   out var jl)  ? jl.GetString()   : null,
            JobDescription    = desc,
            City              = j.TryGetProperty("job_city",    out var ct2) ? ct2.GetString() : null,
            State             = j.TryGetProperty("job_state",   out var st)  ? st.GetString()  : null,
            Country           = j.TryGetProperty("job_country", out var cy)  ? cy.GetString()  : null,
            PostDate          = postDate,
            HoursBackPosted   = ParseHoursBack(postDate),
            SalaryMin         = salMin,
            SalaryMax         = salMax,
            SalaryType        = j.TryGetProperty("job_salary_period",   out var sp) ? sp.GetString() : null,
            SalaryCurrency    = j.TryGetProperty("job_salary_currency", out var sc) ? sc.GetString() : "USD",
            SalaryRangeText   = salMin.HasValue && salMax.HasValue ? $"${salMin:N0}–${salMax:N0}/yr" : null,
            ExperienceYears   = expMonths.HasValue ? expMonths / 12 : null,
            ExperienceMin     = expMonths.HasValue ? expMonths / 12 : null,
            ExperienceMax     = expMonths.HasValue ? (expMonths / 12) + 2 : null,
            WorkType          = isRemote ? "Remote" : "OnSite",
            JobWorkMode       = isRemote ? "Remote" : "OnSite",
            ContractType      = empType switch { "FULLTIME" => "FullTime", "CONTRACTOR" => "Contract", _ => empType },
            ApplyType         = "ExternalApply",
            WorkAuthorization = "H1B",
            Sponsorship       = isH1B ? "H1B Sponsored" : null,
            CompanyName       = employerName,
            CompanyLogoUrl    = j.TryGetProperty("employer_logo",    out var logo) ? logo.GetString() : null,
            CompanyUrl        = j.TryGetProperty("employer_website", out var cw)   ? cw.GetString()   : null,
            CompanyType       = j.TryGetProperty("employer_company_type", out var ctp) ? ctp.GetString() : null,
            Skills            = skills,
            IsH1BSponsored    = isH1B,
            IsSponsored       = isH1B,
            IsContractJob     = empType == "CONTRACTOR",
            IsW2              = empType == "FULLTIME",
            Status            = true,
            CreatedOn         = DateTime.UtcNow,
            UpdatedOn         = DateTime.UtcNow
        };
    }
}
