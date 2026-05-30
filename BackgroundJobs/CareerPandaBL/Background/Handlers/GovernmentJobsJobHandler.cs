// CareerPandaBL/Background/Handlers/GovernmentJobsJobHandler.cs
// SOURCE : USAJobs.gov — Official US Federal Government job board (100% FREE)
// API KEY: JobApiSettings:UsaJobsAuthKey + JobApiSettings:UsaJobsUserAgent
// SIGNUP : https://developer.usajobs.gov/  (free, instant approval)
// DOCS   : https://developer.usajobs.gov/APIRequest/Index
//
// STRATEGY
// All jobs are 100% US federal government positions — no filtering needed for country.
// Paginate up to MaxPages (25 results/page), stop early on 403 (rate limit) or empty page.
// DatePosted=120 sent to API to limit to recent postings.
// All jobs are W2 government employees — IsW2=true, H1B not applicable (requires US citizenship).
using System.Net.Http.Json;
using System.Text.Json;
using CareerPanda.DataAccess.Entities.Api;
using CareerPanda.Framework.Cache;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CareerPanda.BL.Background.Handlers;

public class GovernmentJobsJobHandler : JobFetchBaseHandler
{
    public override string JobType        => "GovernmentJobs";
    protected override string JobCategory => "GovernmentJobs";
    protected override string ApiSource   => "USAJobs";

    // USAJobs.gov SASE gateway blocks rapid bursts — use 3s between pages
    protected override int InterPageDelayMs => 3000;

    private readonly IHttpClientFactory _http;
    private readonly string _authKey;
    private readonly string _userAgent;

    public GovernmentJobsJobHandler(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        ICacheService cacheService,
        IConfiguration configuration,
        ILogger<GovernmentJobsJobHandler> logger)
        : base(scopeFactory, cacheService, logger)
    {
        _http      = httpClientFactory;
        _authKey   = configuration["JobApiSettings:UsaJobsAuthKey"]  ?? string.Empty;
        _userAgent = configuration["JobApiSettings:UsaJobsUserAgent"] ?? "CareerPanda";
    }

    protected override async Task<List<ApiRawJob>> FetchPageAsync(
        int page, JobFetchInput input, string fetchRunId, CancellationToken ct)
    {
        var client  = _http.CreateClient("USAJobs");
        var keyword = Uri.EscapeDataString(input.SearchQuery ?? "");

        // DatePosted=120 limits to last 120 days (USAJobs max is ~120 days)
        var url = $"https://data.usajobs.gov/api/search?ResultsPerPage=25&Page={page}&DatePosted=120" +
                  (string.IsNullOrWhiteSpace(keyword) ? "" : $"&Keyword={keyword}");

        // Retry up to 3 times with exponential back-off (2s → 4s → 8s)
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("Authorization-Key", _authKey);
            req.Headers.Add("User-Agent",        _userAgent);

            var res = await client.SendAsync(req, ct);

            // 403 / 429 / 503 = rate-limited or transient — back off and retry
            if (res.StatusCode == System.Net.HttpStatusCode.Forbidden      ||
                res.StatusCode == System.Net.HttpStatusCode.TooManyRequests ||
                res.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
            {
                if (attempt == 3)
                {
                    Logger.LogWarning(
                        "[GovernmentJobs] {Code} on page {P} — all retries exhausted, skipping page",
                        (int)res.StatusCode, page);
                    return [];
                }
                int backoff = (int)Math.Pow(2, attempt + 1) * 1000; // 4s → 8s → (never, exits above)
                Logger.LogWarning(
                    "[GovernmentJobs] {Code} on page {P} (attempt {A}) — retrying in {B}ms",
                    (int)res.StatusCode, page, attempt, backoff);
                await Task.Delay(backoff, ct);
                continue;
            }

            res.EnsureSuccessStatusCode();

            var json = await res.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            var jobs = new List<ApiRawJob>();

            if (!json.TryGetProperty("SearchResult", out var sr)) return jobs;

            // On page 1 — read total count and calculate how many pages we need
            // SearchResultCountAll may be a JSON Number or String depending on API version
            if (page == 1 && sr.TryGetProperty("SearchResultCountAll", out var totalEl))
            {
                int totalCount = 0;
                if (totalEl.ValueKind == JsonValueKind.Number)
                    totalEl.TryGetInt32(out totalCount);
                else if (totalEl.ValueKind == JsonValueKind.String)
                    int.TryParse(totalEl.GetString(), out totalCount);

                if (totalCount > 0)
                    Logger.LogInformation(
                        "[GovernmentJobs] Total available: {Total} jobs across ~{Pages} pages",
                        totalCount, (int)Math.Ceiling(totalCount / 25.0));
            }

            if (!sr.TryGetProperty("SearchResultItems", out var items)) return jobs;

            foreach (var item in items.EnumerateArray())
            {
                try
                {
                    if (!item.TryGetProperty("MatchedObjectDescriptor", out var desc)) continue;
                    jobs.Add(MapJob(desc, fetchRunId));
                }
                catch (Exception ex) { Logger.LogWarning(ex, "[GovernmentJobs] Map failed"); }
            }
            return jobs;
        }

        Logger.LogWarning("[GovernmentJobs] All retries exhausted for page {P} — skipping", page);
        return [];
    }

    private static ApiRawJob MapJob(JsonElement d, string fetchRunId)
    {
        // ── Location — use structured fields directly ─────────────────────────
        string? city = null, state = null;
        if (d.TryGetProperty("PositionLocation", out var locs) && locs.ValueKind == JsonValueKind.Array)
        {
            var first = locs.EnumerateArray().FirstOrDefault();
            if (first.ValueKind == JsonValueKind.Object)
            {
                city  = first.TryGetProperty("CityName",                out var cn)  ? cn.GetString()  : null;
                state = first.TryGetProperty("CountrySubDivisionCode",  out var csc) ? csc.GetString() : null;

                // Fallback: parse from LocationName if structured fields missing
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
                if (first.TryGetProperty("RateIntervalCode", out var ric)) salType = ric.GetString();
            }
        }

        // ── Job link ──────────────────────────────────────────────────────────
        var jobLink = d.TryGetProperty("PositionURI", out var uri) ? uri.GetString() : null;
        if (string.IsNullOrWhiteSpace(jobLink) &&
            d.TryGetProperty("ApplyURI", out var applyUri) && applyUri.ValueKind == JsonValueKind.Array)
            jobLink = applyUri.EnumerateArray().FirstOrDefault().GetString();

        // ── Work schedule (Full-time, Part-time, etc.) ────────────────────────
        string? schedule = null;
        if (d.TryGetProperty("PositionSchedule", out var sched) && sched.ValueKind == JsonValueKind.Array)
        {
            var first = sched.EnumerateArray().FirstOrDefault();
            if (first.ValueKind == JsonValueKind.Object && first.TryGetProperty("Name", out var sn))
                schedule = sn.GetString();
        }

        // ── Contract type from PositionOfferingType (Permanent/Temporary/Term)
        string? contractType = null;
        if (d.TryGetProperty("PositionOfferingType", out var pot) && pot.ValueKind == JsonValueKind.Array)
        {
            var first = pot.EnumerateArray().FirstOrDefault();
            if (first.ValueKind == JsonValueKind.Object && first.TryGetProperty("Name", out var pn))
                contractType = pn.GetString();
        }

        // ── Industry from JobCategory (e.g. "Information Technology") ─────────
        string? industry = null;
        string[]? skills = null;
        if (d.TryGetProperty("JobCategory", out var cats) && cats.ValueKind == JsonValueKind.Array)
        {
            var names = cats.EnumerateArray()
                .Where(x => x.TryGetProperty("Name", out _))
                .Select(x => x.GetProperty("Name").GetString()!)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToArray();
            if (names.Length > 0)
            {
                industry = names[0];
                skills   = names;
            }
        }

        // ── HiringPath — who can apply ────────────────────────────────────────
        string? hiringPath = null;
        if (d.TryGetProperty("UserArea", out var ua) && ua.ValueKind == JsonValueKind.Object &&
            ua.TryGetProperty("Details", out var details) && details.ValueKind == JsonValueKind.Object)
        {
            if (details.TryGetProperty("HiringPath", out var hp) && hp.ValueKind == JsonValueKind.Array)
            {
                var paths = hp.EnumerateArray()
                    .Select(x => x.GetString())
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToArray();
                hiringPath = paths.Length > 0 ? string.Join(", ", paths) : null;
            }
        }

        // ── Description: prefer JobSummary over QualificationSummary ─────────
        string? desc = null;
        if (d.TryGetProperty("UserArea", out var ua2) && ua2.ValueKind == JsonValueKind.Object &&
            ua2.TryGetProperty("Details", out var det2) && det2.ValueKind == JsonValueKind.Object &&
            det2.TryGetProperty("JobSummary", out var js) && js.ValueKind == JsonValueKind.String)
            desc = js.GetString();
        if (string.IsNullOrWhiteSpace(desc))
            desc = d.TryGetProperty("QualificationSummary", out var qs) ? qs.GetString() : null;

        // ── WorkType: check for telework/remote indicators ────────────────────
        var workType    = "OnSite";
        var jobWorkMode = "OnSite";
        if (d.TryGetProperty("UserArea", out var ua3) && ua3.ValueKind == JsonValueKind.Object &&
            ua3.TryGetProperty("Details", out var det3) && det3.ValueKind == JsonValueKind.Object &&
            det3.TryGetProperty("TeleworkEligible", out var te))
        {
            if (te.ValueKind == JsonValueKind.True ||
                (te.ValueKind == JsonValueKind.String &&
                 te.GetString()?.Contains("eligible", StringComparison.OrdinalIgnoreCase) == true))
            {
                workType    = "Hybrid";
                jobWorkMode = "Hybrid";
            }
        }

        // ── Organization ──────────────────────────────────────────────────────
        var orgName  = d.TryGetProperty("OrganizationName", out var on)  ? on.GetString()  : null;
        var deptName = d.TryGetProperty("DepartmentName",   out var dn)  ? dn.GetString()  : null;
        var companyName = orgName ?? deptName;

        // ── IsUniversityJob: only true for federally-operated academic institutions
        var isUniversity = ContainsAny(orgName, "university", "college", "academy", "institute of technology",
                               "naval postgraduate", "national defense university") ||
                           ContainsAny(deptName, "university", "college", "academy");

        // ── Dates ─────────────────────────────────────────────────────────────
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
            SalaryRangeText   = salMin.HasValue && salMax.HasValue ? $"${salMin:N0}–${salMax:N0}/{salType}" : null,
            SalaryCurrency    = "USD",
            WorkType          = workType,
            JobWorkMode       = jobWorkMode,
            ContractType      = contractType,   // Permanent / Temporary / Term
            JobLevel          = schedule,       // Full-time / Part-time / Intermittent
            Industry          = industry,
            Skills            = skills,
            ApplyType         = "ExternalApply",
            CompanyName       = companyName,
            CompanyType       = "Government",
            // ── Flags ──────────────────────────────────────────────────────────
            // Federal jobs require US citizenship — no visa sponsorship of any kind
            IsH1BSponsored    = false,
            IsOptCpt          = false,
            IsTnVisa          = false,
            IsE3Visa          = false,
            IsJ1Visa          = false,
            IsGreenCard       = false,
            IsSponsored       = false,
            // All federal employees are W2
            IsW2              = true,
            // Federal jobs are never C2C, staffing, prime vendor, freelance, startup, non-profit
            IsC2C             = false,
            IsContractJob     = contractType?.Contains("Temporary", StringComparison.OrdinalIgnoreCase) == true ||
                                contractType?.Contains("Term", StringComparison.OrdinalIgnoreCase) == true,
            IsFreelanceJob    = false,
            IsPrimeVendor     = false,
            IsStaffing        = false,
            IsStartupJob      = false,
            IsNonProfitJob    = false,
            IsUniversityJob   = isUniversity,
            Status            = true,
            CreatedOn         = DateTime.UtcNow,
            UpdatedOn         = DateTime.UtcNow
        };
    }

    private static DateTime? ParsePostDate(string? raw) =>
        !string.IsNullOrWhiteSpace(raw) && DateTime.TryParse(raw, out var dt) ? dt.ToUniversalTime() : null;

    private static int? ParseHoursBack(DateTime? d) =>
        d.HasValue ? (int)(DateTime.UtcNow - d.Value).TotalHours : null;
}
