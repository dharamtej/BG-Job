// CareerPandaBL/Background/Handlers/TheMuseJobsJobHandler.cs
// SOURCE : The Muse API — startup / small company and non-profit jobs (FREE, optional key)
// API KEY: JobApiSettings:MuseApiKey  (optional — leave empty for free tier)
// DOCS   : https://www.themuse.com/developers/api/v2
//
// SWEEPS (run inside one ExecuteAsync / one fetch-run row)
//   1. Startup   — company_size=Startup,Small  → IsStartupJob = true
//   2. NonProfit — company_size=Non-Profit     → IsNonProfitJob = true
//   3. Category sweep — each TheMuse category × no company_size filter (broad coverage)
//
// US filter: jobs whose location string doesn't resolve to US or Remote are dropped.
// All other classification flags set at upsert time by JobClassifier.
using System.Net.Http.Json;
using System.Text.Json;
using CareerPanda.DataAccess.DA;
using CareerPanda.DataAccess.Entities.Api;
using CareerPanda.Framework.Cache;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CareerPanda.BL.Background.Handlers;

public class TheMuseJobsJobHandler : JobFetchBaseHandler
{
    public override string JobType        => "TheMuseJobs";
    protected override string JobCategory => "TheMuseJobs";
    protected override string ApiSource   => "TheMuse";

    // Each tuple: (company_size param, IsStartupJob, IsNonProfitJob, log tag)
    private static readonly (string CompanySize, bool IsStartup, bool IsNonProfit, string Tag)[] Sweeps =
    [
        ("Startup,Small", true,  false, "Startup"),
        ("Non-Profit",    false, true,  "NonProfit"),
    ];

    // TheMuse's own category names — these are the exact strings the API accepts as &category=
    // Sweeping all categories covers the full job board beyond just startups/nonprofits.
    private static readonly string[] MuseCategories =
    [
        "Tech",
        "Engineering",
        "Data & Analytics",
        "Product & UX",
        "Design & UX",
        "Dev & IT",
        "Business & Strategy",
        "Project & Program Management",
        "Operations",
        "Finance",
        "Accounting",
        "Sales",
        "Marketing & PR",
        "Customer Service",
        "HR & Recruiting",
        "Legal",
        "Healthcare",
        "Science",
        "Education",
        "Content & Writing",
        "Media & Journalism",
        "Social Impact",
        "Non-Profit Management",
        "Social Media & Community",
        "Administrative",
        "Real Estate",
        "Retail",
        "Hospitality",
    ];

    private readonly IHttpClientFactory _http;
    private readonly string? _apiKey;

    public TheMuseJobsJobHandler(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        ICacheService cacheService,
        IConfiguration configuration,
        ILogger<TheMuseJobsJobHandler> logger)
        : base(scopeFactory, cacheService, logger)
    {
        _http   = httpClientFactory;
        _apiKey = configuration["JobApiSettings:MuseApiKey"];
    }

    // ── Override: company-size sweeps + category sweeps ─────────────────────

    public override async Task ExecuteAsync(
        JobWorkRequest request,
        IJobProgressReporter progress,
        CancellationToken cancellationToken)
    {
        var input    = ParseInput(request.InputPayload);
        var sponsors = await LoadSponsorsAsync(cancellationToken);

        using var scope = _scopeFactory.CreateScope();
        var fetchDa = scope.ServiceProvider.GetRequiredService<IJobFetchDA>();

        // If a specific SearchQuery was passed (manual run), only fetch that category
        var categories = string.IsNullOrWhiteSpace(input.SearchQuery)
            ? MuseCategories
            : [input.SearchQuery];

        int totalSweeps = Sweeps.Length + categories.Length;

        var run = new DataAccess.Entities.Api.ApiJobFetchRun
        {
            Id               = request.JobId,
            BackgroundTaskId = request.JobId,
            JobCategory      = JobCategory,
            ApiSource        = ApiSource,
            Status           = "Running",
            StartedAt        = DateTime.UtcNow,
            HoursBack        = input.HoursBack,
            MaxPages         = totalSweeps * input.MaxPages,
            LocationFilter   = "US (company_size + category sweeps)",
            CreatedById      = request.UserId
        };
        await fetchDa.CreateFetchRunAsync(run);

        Logger.LogInformation("[TheMuse] Starting — {CS} company-size sweeps + {Cat} category sweeps = {Total} total",
            Sweeps.Length, categories.Length, totalSweeps);

        int totalFetched = 0, totalInserted = 0, totalUpdated = 0,
            totalSkipped = 0, totalErrors = 0, pagesFetched = 0, sweepIndex = 0;

        try
        {
            // ── Phase 1: company-size sweeps (Startup + NonProfit) ────────────
            foreach (var (companySize, isStartup, isNonProfit, tag) in Sweeps)
            {
                if (cancellationToken.IsCancellationRequested) break;
                Logger.LogInformation("[TheMuse] [{I}/{T}] company_size sweep '{Tag}'",
                    sweepIndex + 1, totalSweeps, tag);

                for (int page = 1; page <= input.MaxPages; page++)
                {
                    if (cancellationToken.IsCancellationRequested) break;

                    List<ApiRawJob> jobs;
                    try { jobs = await FetchMusePageAsync(page, input, companySize, isStartup, isNonProfit, run.Id, sponsors, cancellationToken); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { Logger.LogError(ex, "[TheMuse] {T} page {P} failed", tag, page); totalErrors++; continue; }

                    if (jobs.Count == 0) break;

                    pagesFetched++; totalFetched += jobs.Count;
                    (int ins, int upd, int err) = await fetchDa.BulkUpsertRawJobsAsync(jobs, cancellationToken);
                    totalInserted += ins; totalUpdated += upd; totalErrors += err;
                    totalSkipped  += jobs.Count - ins - upd - err;

                    await fetchDa.UpdateFetchRunStatsAsync(run.Id, totalFetched, totalInserted, totalUpdated, totalSkipped, totalErrors, pagesFetched);
                    int pct = (int)(((sweepIndex * input.MaxPages + page) / (double)(totalSweeps * input.MaxPages)) * 90);
                    await progress.ReportProgressAsync(pct, $"[{sweepIndex + 1}/{totalSweeps}] {tag} p{page} — Ins:{totalInserted}");
                    await Task.Delay(InterPageDelayMs, cancellationToken);
                }
                sweepIndex++;
            }

            // ── Phase 2: category sweeps (all MuseCategories) ─────────────────
            foreach (var category in categories)
            {
                if (cancellationToken.IsCancellationRequested) break;
                Logger.LogInformation("[TheMuse] [{I}/{T}] category sweep '{Cat}'",
                    sweepIndex + 1, totalSweeps, category);

                for (int page = 1; page <= input.MaxPages; page++)
                {
                    if (cancellationToken.IsCancellationRequested) break;

                    List<ApiRawJob> jobs;
                    try { jobs = await FetchMuseCategoryPageAsync(page, input, category, run.Id, sponsors, cancellationToken); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { Logger.LogError(ex, "[TheMuse] category '{Cat}' page {P} failed", category, page); totalErrors++; continue; }

                    if (jobs.Count == 0) break;

                    pagesFetched++; totalFetched += jobs.Count;
                    (int ins, int upd, int err) = await fetchDa.BulkUpsertRawJobsAsync(jobs, cancellationToken);
                    totalInserted += ins; totalUpdated += upd; totalErrors += err;
                    totalSkipped  += jobs.Count - ins - upd - err;

                    await fetchDa.UpdateFetchRunStatsAsync(run.Id, totalFetched, totalInserted, totalUpdated, totalSkipped, totalErrors, pagesFetched);
                    int pct = (int)(((sweepIndex * input.MaxPages + page) / (double)(totalSweeps * input.MaxPages)) * 90);
                    await progress.ReportProgressAsync(pct, $"[{sweepIndex + 1}/{totalSweeps}] {category} p{page} — Ins:{totalInserted}");
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

        Logger.LogInformation("[TheMuse] Done — Fetched={F} Ins={I} Upd={U} Err={E}",
            totalFetched, totalInserted, totalUpdated, totalErrors);
        await progress.ReportProgressAsync(100,
            $"Done — Inserted:{totalInserted} Updated:{totalUpdated} Errors:{totalErrors}");
    }

    // ── Fetch one page from The Muse ──────────────────────────────────────────

    private async Task<List<ApiRawJob>> FetchMusePageAsync(
        int page, JobFetchInput input, string companySize,
        bool isStartup, bool isNonProfit,
        string fetchRunId, HashSet<string> sponsors, CancellationToken ct)
    {
        var client = _http.CreateClient("TheMuse");
        var url = $"https://www.themuse.com/api/public/jobs?page={page - 1}&descending=true&company_size={companySize}";
        if (!string.IsNullOrWhiteSpace(_apiKey)) url += $"&api_key={_apiKey}";

        var json = await client.GetFromJsonAsync<JsonElement>(url, ct);
        var jobs = new List<ApiRawJob>();
        if (!json.TryGetProperty("results", out var results)) return jobs;

        foreach (var item in results.EnumerateArray())
        {
            try
            {
                var job = MapJob(item, fetchRunId, sponsors, isStartup, isNonProfit);
                if (job != null) jobs.Add(job);
            }
            catch (Exception ex) { Logger.LogWarning(ex, "[TheMuse] Map failed"); }
        }
        return jobs;
    }

    // ── Category-based page fetch ─────────────────────────────────────────────

    private async Task<List<ApiRawJob>> FetchMuseCategoryPageAsync(
        int page, JobFetchInput input, string category,
        string fetchRunId, HashSet<string> sponsors, CancellationToken ct)
    {
        var client = _http.CreateClient("TheMuse");
        var url = $"https://www.themuse.com/api/public/jobs?page={page - 1}&descending=true" +
                  $"&category={Uri.EscapeDataString(category)}";
        if (!string.IsNullOrWhiteSpace(_apiKey)) url += $"&api_key={_apiKey}";

        var json = await client.GetFromJsonAsync<JsonElement>(url, ct);
        var jobs = new List<ApiRawJob>();
        if (!json.TryGetProperty("results", out var results)) return jobs;

        foreach (var item in results.EnumerateArray())
        {
            try
            {
                var job = MapJob(item, fetchRunId, sponsors, false, false);
                if (job != null) jobs.Add(job);
            }
            catch (Exception ex) { Logger.LogWarning(ex, "[TheMuse] Map failed (category={Cat})", category); }
        }
        return jobs;
    }

    // ── MapJob ────────────────────────────────────────────────────────────────

    private ApiRawJob? MapJob(JsonElement j, string fetchRunId, HashSet<string> sponsors,
        bool isStartup, bool isNonProfit)
    {
        var company      = j.TryGetProperty("company",  out var c)   ? c   : default;
        var companyName  = company.ValueKind == JsonValueKind.Object && company.TryGetProperty("name",  out var cn)  ? cn.GetString()  : null;
        var companyUrl   = company.ValueKind == JsonValueKind.Object && company.TryGetProperty("url",   out var cu)  ? cu.GetString()  : null;
        var companyLogo  = company.ValueKind == JsonValueKind.Object && company.TryGetProperty("logo",  out var cl)  ? cl.GetString()  : null;
        var apiCompanyId = company.ValueKind == JsonValueKind.Object && company.TryGetProperty("id",    out var cid) ? cid.ToString()  : null;

        // Company size: "Startup" | "Small" | "Medium" | "Large" | "Very Large" | "Non-Profit"
        int? companySize = null;
        if (company.ValueKind == JsonValueKind.Object && company.TryGetProperty("size", out var csz) && csz.ValueKind == JsonValueKind.Object
            && csz.TryGetProperty("name", out var cszName))
        {
            companySize = cszName.GetString()?.ToLowerInvariant() switch
            {
                "startup"    => 25,
                "small"      => 150,
                "medium"     => 750,
                "large"      => 3500,
                "very large" => 10000,
                _            => null
            };
        }

        // Company short description from profile sections
        string? companyAbout = null;
        if (company.ValueKind == JsonValueKind.Object && company.TryGetProperty("profile", out var profile)
            && profile.ValueKind == JsonValueKind.Object && profile.TryGetProperty("sections", out var sections)
            && sections.ValueKind == JsonValueKind.Array)
        {
            foreach (var section in sections.EnumerateArray())
            {
                var sectionName = section.TryGetProperty("name", out var sn) ? sn.GetString() : null;
                if (string.Equals(sectionName, "About", StringComparison.OrdinalIgnoreCase)
                    && section.TryGetProperty("body", out var body))
                {
                    companyAbout = JobFetchHelpers.StripHtml(body.GetString());
                    break;
                }
            }
        }

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

        // US-only filter
        if (!UsLocationHelper.IsUsFriendlyLocation(location)) return null;

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
                            (companyName != null && (sponsors.Contains(companyName) ||
                                                     sponsors.Contains(NormalizeCompanyName(companyName)))));
        var isOptCpt      = !visaNegation && ContainsAny(desc, " opt ", "opt/cpt", "stem opt", "opt extension", "f-1 visa", " cpt ");
        var isTnVisa      = !visaNegation && ContainsAny(desc, "tn visa", "tn-1", "tn-2", "usmca", "nafta visa");
        var isE3Visa      = !visaNegation && ContainsAny(desc, "e-3", "e3 visa", "e-3 visa");
        var isJ1Visa      = !visaNegation && ContainsAny(desc, "j-1", "j1 visa", "j-1 visa", "exchange visitor");
        var isGreenCard   = !visaNegation && ContainsAny(desc, "green card", "gc sponsor", "perm filing", "eb-2", "eb-3", "labor certification");
        var isContract    = ContainsAny(desc, "contract", "contractor") ||
                            jobType?.Equals("contract", StringComparison.OrdinalIgnoreCase) == true;
        var isC2H         = ContainsAny(desc, "contract to hire", "contract-to-hire", "c2h",
                                "right to hire", "right-to-hire", "temp to perm", "temp-to-perm");
        var isC2C         = ContainsAny(desc, "c2c", "corp to corp", "corp-to-corp");
        var isW2          = ContainsAny(desc, "w2", "w-2");
        var isFreelance   = ContainsAny(desc, "1099", "freelance", "independent contractor");
        var isPrimeVendor = ContainsAny(desc, "prime vendor", "direct client", "end client");
        var isStaffing    = ContainsAny(desc, "staffing", "recruiting firm") ||
                            ContainsAny(companyName, "staffing", "consulting", "solutions");
        var isUniversity  = ContainsAny(companyName, "university", "college", "institute") ||
                            ContainsAny(desc, "university", "academic", "faculty");
        // Allow the classifier to also detect startup/nonprofit from description
        // even if the sweep already set the flag via API filter.
        var startupFromDesc   = ContainsAny(desc, "startup", "start-up", "series a", "series b", "seed") ||
                                ContainsAny(companyName, "startup", "start-up");
        var nonProfitFromDesc = ContainsAny(desc, "nonprofit", "non-profit", "501(c)", "ngo") ||
                                ContainsAny(companyName, "foundation", "nonprofit", "non-profit");

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
            ContractType      = jobType switch { "full time" => "FullTime", "part time" => "PartTime", "contract" => "Contract", "internship" => "Internship", _ => null },
            WorkType          = isRemote ? "Remote" : "OnSite",
            JobWorkMode       = isRemote ? "Remote" : "OnSite",
            JobLevel          = NormalizeJobLevel(j.TryGetProperty("name", out var titleEl) ? titleEl.GetString() : null),
            ApplyType         = "ExternalApply",
            ApiCompanyId      = apiCompanyId,
            CompanyName       = companyName,
            CompanyUrl        = companyUrl,
            CompanyLogoUrl    = companyLogo,
            CompanyType       = isStartup ? "Startup" : isNonProfit ? "NonProfit" : null,
            CompanyAbout      = companyAbout,
            CompanySize       = companySize,
            IsH1BSponsored    = isH1B,
            IsOptCpt          = isOptCpt,
            IsTnVisa          = isTnVisa,
            IsE3Visa          = isE3Visa,
            IsJ1Visa          = isJ1Visa,
            IsGreenCard       = isGreenCard,
            IsSponsored       = isH1B || isOptCpt || isTnVisa || isE3Visa || isJ1Visa || isGreenCard ||
                                ContainsAny(desc, "sponsor", "visa"),
            IsContractJob     = isContract,
            IsContractToHire  = isC2H,
            IsC2C             = isC2C,
            IsW2              = isW2,
            IsFreelanceJob    = isFreelance,
            IsPrimeVendor     = isPrimeVendor,
            IsStaffing        = isStaffing,
            IsUniversityJob   = isUniversity,
            IsStartupJob      = isStartup  || startupFromDesc,
            IsNonProfitJob    = isNonProfit || nonProfitFromDesc,
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
        // Default to Startup sweep when called via base handler
        return await FetchMusePageAsync(page, input, "Startup,Small", true, false, fetchRunId, sponsors, ct);
    }
}
