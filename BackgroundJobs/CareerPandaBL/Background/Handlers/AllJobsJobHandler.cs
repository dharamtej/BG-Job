// CareerPandaBL/Background/Handlers/AllJobsJobHandler.cs
// SOURCE : JSearch via RapidAPI — aggregates Indeed, LinkedIn, Glassdoor, ZipRecruiter
// API KEY: JobApiSettings:JSearchApiKey  (appsettings.json)
// DOCS   : https://rapidapi.com/letscrape-6bRBa3QguO5/api/jsearch
using System.Net.Http.Json;
using System.Text.Json;
using CareerPanda.DataAccess.Entities.Api;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CareerPanda.BL.Background.Handlers;

public class AllJobsJobHandler : JobFetchBaseHandler
{
    public override string JobType        => "AllJobs";
    protected override string JobCategory => "AllJobs";
    protected override string ApiSource   => "JSearch";

    private readonly IHttpClientFactory _http;
    private readonly string _apiKey;
    private readonly string _apiHost;

    public AllJobsJobHandler(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<AllJobsJobHandler> logger)
        : base(scopeFactory, logger)
    {
        _http    = httpClientFactory;
        _apiKey  = configuration["JobApiSettings:JSearchApiKey"] ?? string.Empty;
        _apiHost = configuration["JobApiSettings:JSearchApiHost"] ?? "jsearch.p.rapidapi.com";
    }

    protected override async Task<List<ApiRawJob>> FetchPageAsync(
        int page, JobFetchInput input, string fetchRunId, CancellationToken ct)
    {
        var client     = _http.CreateClient("JSearch");
        var query      = Uri.EscapeDataString(input.SearchQuery ?? "software engineer developer analyst");
        var location   = Uri.EscapeDataString(input.Location    ?? "United States");
        var datePosted = input.HoursBack <= 24 ? "today" : input.HoursBack <= 72 ? "3days" : input.HoursBack <= 168 ? "week" : "month";

        var url = $"https://jsearch.p.rapidapi.com/search?query={query}%20in%20{location}" +
                  $"&page={page}&num_pages=1&date_posted={datePosted}";

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
            try { jobs.Add(MapJob(item, fetchRunId)); }
            catch (Exception ex) { Logger.LogWarning(ex, "[AllJobs] Map failed"); }
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
            IsH1BSponsored    = ContainsAny(desc, "h1b", "h-1b", "visa sponsor"),
            IsSponsored       = ContainsAny(desc, "sponsor", "visa"),
            Status            = true,
            CreatedOn         = DateTime.UtcNow,
            UpdatedOn         = DateTime.UtcNow
        };
    }
}
