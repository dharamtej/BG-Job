// CareerPandaBL/Background/Handlers/NonProfitJobsJobHandler.cs
// SOURCE : The Muse API — Non-Profit company size filter (FREE)
// API KEY: JobApiSettings:MuseApiKey  (optional)
using System.Net.Http.Json;
using System.Text.Json;
using CareerPanda.DataAccess.Entities.Api;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CareerPanda.BL.Background.Handlers;

public class NonProfitJobsJobHandler : JobFetchBaseHandler
{
    public override string JobType        => "NonProfitJobs";
    protected override string JobCategory => "NonProfitJobs";
    protected override string ApiSource   => "TheMuse";

    private readonly IHttpClientFactory _http;
    private readonly string? _apiKey;

    public NonProfitJobsJobHandler(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<NonProfitJobsJobHandler> logger)
        : base(scopeFactory, logger)
    {
        _http   = httpClientFactory;
        _apiKey = configuration["JobApiSettings:MuseApiKey"];
    }

    protected override async Task<List<ApiRawJob>> FetchPageAsync(
        int page, JobFetchInput input, string fetchRunId, CancellationToken ct)
    {
        var client = _http.CreateClient("TheMuse");
        var url = $"https://www.themuse.com/api/public/jobs?page={page - 1}&descending=true&company_size=Non-Profit";
        if (!string.IsNullOrWhiteSpace(_apiKey))     url += $"&api_key={_apiKey}";
        if (!string.IsNullOrWhiteSpace(input.SearchQuery)) url += $"&category={Uri.EscapeDataString(input.SearchQuery)}";

        var json = await client.GetFromJsonAsync<JsonElement>(url, ct);
        var jobs = new List<ApiRawJob>();
        if (!json.TryGetProperty("results", out var results)) return jobs;

        foreach (var item in results.EnumerateArray())
        {
            try { jobs.Add(MapJob(item, fetchRunId)); }
            catch (Exception ex) { Logger.LogWarning(ex, "[NonProfitJobs] Map failed"); }
        }
        return jobs;
    }

    private static ApiRawJob MapJob(JsonElement j, string fetchRunId)
    {
        var company      = j.TryGetProperty("company", out var c) ? c : default;
        var companyName  = company.ValueKind == JsonValueKind.Object && company.TryGetProperty("name", out var cn)  ? cn.GetString()  : null;
        var companyUrl   = company.ValueKind == JsonValueKind.Object && company.TryGetProperty("url",  out var cu)  ? cu.GetString()  : null;
        var logo         = company.ValueKind == JsonValueKind.Object && company.TryGetProperty("logo", out var cl)  ? cl.GetString()  : null;
        var apiCompanyId = company.ValueKind == JsonValueKind.Object && company.TryGetProperty("id",   out var cid) ? cid.ToString()  : null;

        var location = j.TryGetProperty("locations", out var locs) && locs.ValueKind == JsonValueKind.Array
            ? string.Join(", ", locs.EnumerateArray().Where(l => l.TryGetProperty("name", out _)).Select(l => l.GetProperty("name").GetString()))
            : null;

        var cats = j.TryGetProperty("categories", out var catArr) && catArr.ValueKind == JsonValueKind.Array
            ? string.Join(", ", catArr.EnumerateArray().Where(c2 => c2.TryGetProperty("name", out _)).Select(c2 => c2.GetProperty("name").GetString()))
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
            Industry        = cats,
            ContractType    = jobType switch { "full time" => "FullTime", "part time" => "PartTime", "contract" => "Contract", "internship" => "Internship", _ => jobType },
            WorkType        = location?.Contains("Remote", StringComparison.OrdinalIgnoreCase) == true ? "Remote" : "OnSite",
            ApplyType       = "ExternalApply",
            ApiCompanyId    = apiCompanyId,
            CompanyName     = companyName,
            CompanyUrl      = companyUrl,
            CompanyLogoUrl  = logo,
            CompanyType     = "NonProfit",
            IsNonProfitJob  = true,
            IsW2            = true,
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
