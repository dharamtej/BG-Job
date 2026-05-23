// CareerPandaBL/Background/Handlers/NonProfitJobsJobHandler.cs
// SOURCE : The Muse API — non-profit company size filter (FREE, optional key)
// API KEY: JobApiSettings:MuseApiKey  (optional)
// DOCS   : https://www.themuse.com/developers/api/v2
// STRATEGY
// Uses company_size=Non-Profit filter. 0-indexed pages, ~20 jobs/page.
// All flags evaluated at insert time from description + company name.
using System.Net.Http.Json;
using System.Text.Json;
using CareerPanda.DataAccess.DA;
using CareerPanda.DataAccess.Entities.Api;
using CareerPanda.Framework.Cache;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CareerPanda.BL.Background.Handlers;

public class NonProfitJobsJobHandler : JobFetchBaseHandler
{
    public override string JobType        => "NonProfitJobs";
    protected override string JobCategory => "NonProfitJobs";
    protected override string ApiSource   => "TheMuse";

    private const string SponsorsCacheKey = "h1b:sponsors:names";
    private static readonly TimeSpan SponsorsCacheTtl = TimeSpan.FromHours(24);

    private static readonly string[] LegalSuffixes =
        ["INCORPORATED", "CORPORATION", "LIMITED", "INC", "LLC", "CORP", "LTD", "CO", "LP", "LLP", "PLLC", "PC"];

    private readonly IHttpClientFactory _http;
    private readonly ICacheService _cache;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly string? _apiKey;

    public NonProfitJobsJobHandler(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        ICacheService cacheService,
        IConfiguration configuration,
        ILogger<NonProfitJobsJobHandler> logger)
        : base(scopeFactory, logger)
    {
        _scopeFactory = scopeFactory;
        _http         = httpClientFactory;
        _cache        = cacheService;
        _apiKey       = configuration["JobApiSettings:MuseApiKey"];
    }

    protected override async Task<List<ApiRawJob>> FetchPageAsync(
        int page, JobFetchInput input, string fetchRunId, CancellationToken ct)
    {
        var sponsors = await LoadSponsorsAsync(ct);
        var client   = _http.CreateClient("TheMuse");

        var url = $"https://www.themuse.com/api/public/jobs?page={page - 1}&descending=true&company_size=Non-Profit";
        if (!string.IsNullOrWhiteSpace(_apiKey))           url += $"&api_key={_apiKey}";
        if (!string.IsNullOrWhiteSpace(input.SearchQuery)) url += $"&category={Uri.EscapeDataString(input.SearchQuery)}";

        var json = await client.GetFromJsonAsync<JsonElement>(url, ct);
        var jobs = new List<ApiRawJob>();
        if (!json.TryGetProperty("results", out var results)) return jobs;

        foreach (var item in results.EnumerateArray())
        {
            try { jobs.Add(MapJob(item, fetchRunId, sponsors)); }
            catch (Exception ex) { Logger.LogWarning(ex, "[NonProfitJobs] Map failed"); }
        }
        return jobs;
    }

    private ApiRawJob MapJob(JsonElement j, string fetchRunId, HashSet<string> sponsors)
    {
        var company      = j.TryGetProperty("company", out var c)   ? c   : default;
        var companyName  = company.ValueKind == JsonValueKind.Object && company.TryGetProperty("name", out var cn)  ? cn.GetString()  : null;
        var companyUrl   = company.ValueKind == JsonValueKind.Object && company.TryGetProperty("url",  out var cu)  ? cu.GetString()  : null;
        var logo         = company.ValueKind == JsonValueKind.Object && company.TryGetProperty("logo", out var cl)  ? cl.GetString()  : null;
        var apiCompanyId = company.ValueKind == JsonValueKind.Object && company.TryGetProperty("id",   out var cid) ? cid.ToString()  : null;

        var desc     = j.TryGetProperty("contents", out var ct2) ? ct2.GetString() : null;
        var jobType  = j.TryGetProperty("type",     out var jtp) ? jtp.GetString() : null;
        var jobLink  = j.TryGetProperty("refs", out var refs) && refs.ValueKind == JsonValueKind.Object &&
                       refs.TryGetProperty("landing_page", out var lp) ? lp.GetString() : null;
        var postDate = ParsePostDate(j.TryGetProperty("publication_date", out var pd) ? pd.GetString() : null);

        var location = j.TryGetProperty("locations", out var locs) && locs.ValueKind == JsonValueKind.Array
            ? string.Join(", ", locs.EnumerateArray()
                .Where(l => l.TryGetProperty("name", out _))
                .Select(l => l.GetProperty("name").GetString()))
            : null;

        var cats = j.TryGetProperty("categories", out var catArr) && catArr.ValueKind == JsonValueKind.Array
            ? string.Join(", ", catArr.EnumerateArray()
                .Where(cat => cat.TryGetProperty("name", out _))
                .Select(cat => cat.GetProperty("name").GetString()))
            : null;

        var isRemote = location?.Contains("Remote", StringComparison.OrdinalIgnoreCase) == true;

        // ── Flag detection ────────────────────────────────────────────────────
        var isH1B         = ContainsAny(desc, "h1b", "h-1b", "visa sponsor", "will sponsor") ||
                            (companyName != null && sponsors.Contains(companyName));
        var isContract    = ContainsAny(desc, "contract", "contractor") ||
                            jobType?.Equals("contract", StringComparison.OrdinalIgnoreCase) == true;
        var isC2C         = ContainsAny(desc, "c2c", "corp to corp", "corp-to-corp");
        var isW2          = ContainsAny(desc, "w2", "w-2");
        var isFreelance   = ContainsAny(desc, "1099", "freelance", "independent contractor");
        var isPrimeVendor = ContainsAny(desc, "prime vendor", "direct client", "end client");
        var isStaffing    = ContainsAny(desc, "staffing", "recruiting firm") ||
                            ContainsAny(companyName, "staffing", "consulting", "solutions");
        var isUniversity  = ContainsAny(companyName, "university", "college", "institute") ||
                            ContainsAny(desc, "university", "academic", "faculty");
        var isStartup     = ContainsAny(desc, "startup", "start-up", "series a", "series b", "seed") ||
                            ContainsAny(companyName, "startup", "start-up");

        return new ApiRawJob
        {
            PublicId          = Guid.NewGuid().ToString("N"),
            Source            = "TheMuse",
            SourceId          = j.TryGetProperty("id", out var id) ? id.ToString() : null,
            FetchRunId        = fetchRunId,
            JobTitle          = j.TryGetProperty("name", out var name) ? name.GetString()! : "Untitled",
            JobLink           = jobLink,
            JobDescription    = desc,
            City              = location?.Split(',').FirstOrDefault()?.Trim(),
            Country           = null,
            PostDate          = postDate,
            HoursBackPosted   = ParseHoursBack(postDate),
            Industry          = cats,
            ContractType      = jobType switch { "full time" => "FullTime", "part time" => "PartTime", "contract" => "Contract", "internship" => "Internship", _ => jobType },
            WorkType          = isRemote ? "Remote" : "OnSite",
            JobWorkMode       = isRemote ? "Remote" : "OnSite",
            ApplyType         = "ExternalApply",
            ApiCompanyId      = apiCompanyId,
            CompanyName       = companyName,
            CompanyUrl        = companyUrl,
            CompanyLogoUrl    = logo,
            CompanyType       = "NonProfit",
            // ── All flags ─────────────────────────────────────────────────────
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
            IsNonProfitJob    = true,
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
        Logger.LogInformation("[NonProfitJobs] Loaded {Count} H1B sponsors into cache", names.Count);
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
}
