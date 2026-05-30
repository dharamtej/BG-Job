// CareerPandaBL/Background/Handlers/NonProfitJobsJobHandler.cs
// SOURCE : The Muse API — non-profit company size filter (FREE, optional key)
// API KEY: JobApiSettings:MuseApiKey  (optional)
// DOCS   : https://www.themuse.com/developers/api/v2
// STRATEGY
// Uses company_size=Non-Profit filter. 0-indexed pages, ~20 jobs/page.
// All flags evaluated at insert time from description + company name.
using System.Net.Http.Json;
using System.Text.Json;
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

    private readonly IHttpClientFactory _http;
    private readonly string? _apiKey;

    public NonProfitJobsJobHandler(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        ICacheService cacheService,
        IConfiguration configuration,
        ILogger<NonProfitJobsJobHandler> logger)
        : base(scopeFactory, cacheService, logger)
    {
        _http   = httpClientFactory;
        _apiKey = configuration["JobApiSettings:MuseApiKey"];
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
        var visaNegation  = ContainsAny(desc,
            "do not sponsor", "does not sponsor", "no sponsorship", "unable to sponsor",
            "cannot sponsor", "will not sponsor", "no h-1b", "no h1b",
            "must be authorized to work", "must have work authorization",
            "authorized to work in the us", "authorized to work in the united states");
        var isH1B         = !visaNegation && (
                            ContainsAny(desc, "h1b", "h-1b", "visa sponsor", "will sponsor") ||
                            (companyName != null && (sponsors.Contains(companyName) || sponsors.Contains(NormalizeCompanyName(companyName)))));
        var isOptCpt      = !visaNegation && ContainsAny(desc, " opt ", "opt/cpt", "stem opt", "opt extension", "f-1 visa", " cpt ");
        var isTnVisa      = !visaNegation && ContainsAny(desc, "tn visa", "tn-1", "tn-2", "usmca", "nafta visa");
        var isE3Visa      = !visaNegation && ContainsAny(desc, "e-3", "e3 visa", "e-3 visa");
        var isJ1Visa      = !visaNegation && ContainsAny(desc, "j-1", "j1 visa", "j-1 visa", "exchange visitor");
        var isGreenCard   = !visaNegation && ContainsAny(desc, "green card", "gc sponsor", "perm filing", "eb-2", "eb-3", "labor certification");
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
            IsOptCpt          = isOptCpt,
            IsTnVisa          = isTnVisa,
            IsE3Visa          = isE3Visa,
            IsJ1Visa          = isJ1Visa,
            IsGreenCard       = isGreenCard,
            IsSponsored       = isH1B || isOptCpt || isTnVisa || isE3Visa || isJ1Visa || isGreenCard || ContainsAny(desc, "sponsor", "visa"),
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

}
