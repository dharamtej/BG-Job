// CareerPandaBL/Background/Handlers/StartupJobsJobHandler.cs
// SOURCE : The Muse API — startup/small company jobs (FREE, no payment needed)
// API KEY: JobApiSettings:MuseApiKey  (optional – leave empty for free tier)
// DOCS   : https://www.themuse.com/developers/api/v2
using System.Net.Http.Json;
using System.Text.Json;
using CareerPanda.DataAccess.Entities.Api;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CareerPanda.BL.Background.Handlers;

public class StartupJobsJobHandler : JobFetchBaseHandler
{
    public override string JobType        => "StartupJobs";
    protected override string JobCategory => "StartupJobs";
    protected override string ApiSource   => "TheMuse";

    private readonly IHttpClientFactory _http;
    private readonly string? _apiKey;

    public StartupJobsJobHandler(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<StartupJobsJobHandler> logger)
        : base(scopeFactory, logger)
    {
        _http   = httpClientFactory;
        _apiKey = configuration["JobApiSettings:MuseApiKey"];
    }

    protected override async Task<List<ApiRawJob>> FetchPageAsync(
        int page, JobFetchInput input, string fetchRunId, CancellationToken ct)
    {
        var client = _http.CreateClient("TheMuse");
        // The Muse uses 0-indexed pages; company_size=Startup,Small targets startup companies
        var url = $"https://www.themuse.com/api/public/jobs?page={page - 1}&descending=true&company_size=Startup,Small";
        if (!string.IsNullOrWhiteSpace(_apiKey)) url += $"&api_key={_apiKey}";
        if (!string.IsNullOrWhiteSpace(input.SearchQuery))
            url += $"&category={Uri.EscapeDataString(input.SearchQuery)}";

        var json = await client.GetFromJsonAsync<JsonElement>(url, ct);
        var jobs = new List<ApiRawJob>();
        if (!json.TryGetProperty("results", out var results)) return jobs;

        foreach (var item in results.EnumerateArray())
        {
            try { jobs.Add(MapJob(item, fetchRunId)); }
            catch (Exception ex) { Logger.LogWarning(ex, "[StartupJobs] Map failed"); }
        }
        return jobs;
    }

    private static ApiRawJob MapJob(JsonElement j, string fetchRunId)
    {
        var company       = j.TryGetProperty("company",  out var c)  ? c  : default;
        var companyName   = company.ValueKind == JsonValueKind.Object && company.TryGetProperty("name", out var cn)  ? cn.GetString()     : null;
        var companyUrl    = company.ValueKind == JsonValueKind.Object && company.TryGetProperty("url",  out var cu)  ? cu.GetString()     : null;
        var companyLogo   = company.ValueKind == JsonValueKind.Object && company.TryGetProperty("logo", out var cl)  ? cl.GetString()     : null;
        var apiCompanyId  = company.ValueKind == JsonValueKind.Object && company.TryGetProperty("id",   out var cid) ? cid.ToString()     : null;

        var location = j.TryGetProperty("locations", out var locs) && locs.ValueKind == JsonValueKind.Array
            ? string.Join(", ", locs.EnumerateArray().Where(l => l.TryGetProperty("name", out _)).Select(l => l.GetProperty("name").GetString()))
            : null;

        var postDate = ParsePostDate(j.TryGetProperty("publication_date", out var pd) ? pd.GetString() : null);
        var jobType  = j.TryGetProperty("type", out var jtp) ? jtp.GetString() : null;
        var jobLink  = j.TryGetProperty("refs", out var refs) && refs.ValueKind == JsonValueKind.Object &&
                       refs.TryGetProperty("landing_page", out var lp) ? lp.GetString() : null;

        return new ApiRawJob
        {
            PublicId        = Guid.NewGuid().ToString("N"),
            Source          = "TheMuse",
            SourceId        = j.TryGetProperty("id", out var id) ? id.ToString() : null,
            FetchRunId      = fetchRunId,
            JobTitle        = j.TryGetProperty("name", out var name) ? name.GetString()! : "Untitled",
            JobLink         = jobLink,
            City            = location?.Split(',').FirstOrDefault()?.Trim(),
            Country         = "United States",
            PostDate        = postDate,
            HoursBackPosted = ParseHoursBack(postDate),
            ContractType    = jobType switch { "full time" => "FullTime", "part time" => "PartTime", "contract" => "Contract", "internship" => "Internship", _ => jobType },
            WorkType        = location?.Contains("Remote", StringComparison.OrdinalIgnoreCase) == true ? "Remote" : "OnSite",
            ApplyType       = "ExternalApply",
            ApiCompanyId    = apiCompanyId,
            CompanyName     = companyName,
            CompanyUrl      = companyUrl,
            CompanyLogoUrl  = companyLogo,
            CompanyType     = "Startup",
            IsStartupJob    = true,
            IsContractJob   = jobType?.Equals("contract", StringComparison.OrdinalIgnoreCase) == true,
            Status          = true,
            CreatedOn       = DateTime.UtcNow,
            UpdatedOn       = DateTime.UtcNow
        };
    }

    private static DateTime? ParsePostDate(string? raw) =>
        !string.IsNullOrWhiteSpace(raw) && DateTime.TryParse(raw, out var dt) ? dt.ToUniversalTime() : null;

    private static int? ParseHoursBack(DateTime? d) =>
        d.HasValue ? (int)(DateTime.UtcNow - d.Value).TotalHours : null;
}
