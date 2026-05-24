// CareerPandaBL/Background/Handlers/UniversityJobsJobHandler.cs
// SOURCE : USAJobs.gov — Official US Government / University job board (100% FREE)
// API KEY: JobApiSettings:UsaJobsAuthKey + JobApiSettings:UsaJobsUserAgent
// SIGNUP : https://developer.usajobs.gov/  (free, instant approval)
// DOCS   : https://developer.usajobs.gov/APIRequest/Index
using System.Net.Http.Json;
using System.Text.Json;
using CareerPanda.DataAccess.Entities.Api;
using CareerPanda.Framework.Cache;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CareerPanda.BL.Background.Handlers;

public class UniversityJobsJobHandler : JobFetchBaseHandler
{
    public override string JobType        => "UniversityJobs";
    protected override string JobCategory => "UniversityJobs";
    protected override string ApiSource   => "USAJobs";

    // USAJobs.gov SASE gateway blocks rapid bursts — use 3s between pages
    protected override int InterPageDelayMs => 3000;

    private readonly IHttpClientFactory _http;
    private readonly string _authKey;
    private readonly string _userAgent;

    public UniversityJobsJobHandler(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        ICacheService cacheService,
        IConfiguration configuration,
        ILogger<UniversityJobsJobHandler> logger)
        : base(scopeFactory, cacheService, logger)
    {
        _http      = httpClientFactory;
        _authKey   = configuration["JobApiSettings:UsaJobsAuthKey"]   ?? string.Empty;
        _userAgent = configuration["JobApiSettings:UsaJobsUserAgent"]  ?? "CareerPanda";
    }

    protected override async Task<List<ApiRawJob>> FetchPageAsync(
        int page, JobFetchInput input, string fetchRunId, CancellationToken ct)
    {
        var client  = _http.CreateClient("USAJobs");
        var keyword = Uri.EscapeDataString(input.SearchQuery ?? "");
        var url     = $"https://data.usajobs.gov/api/search?ResultsPerPage=25&Page={page}" +
                      (string.IsNullOrWhiteSpace(keyword) ? "" : $"&Keyword={keyword}");

        // Retry up to 3 times with exponential back-off (2s → 4s → 8s)
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("Authorization-Key", _authKey);
            req.Headers.Add("User-Agent",        _userAgent);
            // Host header is set automatically from the URL — do NOT add manually

            var res = await client.SendAsync(req, ct);

            // 403 = rate-limited / gateway blocked — stop the page loop cleanly
            if (res.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                Logger.LogWarning(
                    "[UniversityJobs] 403 on page {P} (attempt {A}) — rate limited, stopping fetch gracefully",
                    page, attempt);
                return [];   // empty list → base handler breaks the loop, no errors counted
            }

            // 429 / 503 = transient — wait and retry
            if (res.StatusCode == System.Net.HttpStatusCode.TooManyRequests ||
                res.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
            {
                int backoff = (int)Math.Pow(2, attempt) * 1000; // 2s, 4s, 8s
                Logger.LogWarning(
                    "[UniversityJobs] {Code} on page {P} (attempt {A}) — retrying in {B}ms",
                    (int)res.StatusCode, page, attempt, backoff);
                await Task.Delay(backoff, ct);
                continue;
            }

            res.EnsureSuccessStatusCode();

            var json = await res.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            var jobs = new List<ApiRawJob>();

            if (!json.TryGetProperty("SearchResult", out var sr)) return jobs;
            if (!sr.TryGetProperty("SearchResultItems", out var items)) return jobs;

            foreach (var item in items.EnumerateArray())
            {
                try
                {
                    if (!item.TryGetProperty("MatchedObjectDescriptor", out var desc)) continue;
                    jobs.Add(MapJob(desc, fetchRunId));
                }
                catch (Exception ex) { Logger.LogWarning(ex, "[UniversityJobs] Map failed"); }
            }
            return jobs;
        }

        // All retries exhausted
        Logger.LogWarning("[UniversityJobs] All retries exhausted for page {P} — skipping", page);
        return [];
    }

    private static ApiRawJob MapJob(JsonElement d, string fetchRunId)
    {
        // Location
        var locationName = string.Empty;
        if (d.TryGetProperty("PositionLocation", out var locs) && locs.ValueKind == JsonValueKind.Array)
        {
            var first = locs.EnumerateArray().FirstOrDefault();
            if (first.ValueKind == JsonValueKind.Object && first.TryGetProperty("LocationName", out var ln))
                locationName = ln.GetString() ?? string.Empty;
        }

        // Salary
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

        // Job link
        var jobLink = d.TryGetProperty("PositionURI", out var uri) ? uri.GetString() : null;
        if (string.IsNullOrWhiteSpace(jobLink) && d.TryGetProperty("ApplyURI", out var applyUri) &&
            applyUri.ValueKind == JsonValueKind.Array)
            jobLink = applyUri.EnumerateArray().FirstOrDefault().GetString();

        // Schedule
        string? schedule = null;
        if (d.TryGetProperty("PositionSchedule", out var sched) && sched.ValueKind == JsonValueKind.Array)
        {
            var first = sched.EnumerateArray().FirstOrDefault();
            if (first.ValueKind == JsonValueKind.Object && first.TryGetProperty("Name", out var sn))
                schedule = sn.GetString();
        }

        var postDate  = ParsePostDate(d.TryGetProperty("PublicationStartDate", out var psd) ? psd.GetString() : null);
        var closeDate = ParsePostDate(d.TryGetProperty("ApplicationCloseDate", out var acd) ? acd.GetString() : null);
        var orgName   = d.TryGetProperty("OrganizationName", out var on) ? on.GetString() : null;

        return new ApiRawJob
        {
            PublicId        = Guid.NewGuid().ToString("N"),
            Source          = "USAJobs",
            SourceId        = d.TryGetProperty("PositionID", out var pid) ? pid.GetString() : null,
            FetchRunId      = fetchRunId,
            JobTitle        = d.TryGetProperty("PositionTitle", out var pt) ? pt.GetString()! : "Untitled",
            JobLink         = jobLink,
            JobDescription  = d.TryGetProperty("QualificationSummary", out var qs) ? qs.GetString() : null,
            City            = locationName.Split(',').FirstOrDefault()?.Trim(),
            State           = locationName.Contains(',') ? locationName.Split(',').LastOrDefault()?.Trim() : null,
            Country         = "United States",
            PostDate        = postDate,
            LastDate        = closeDate,
            HoursBackPosted = ParseHoursBack(postDate),
            SalaryMin       = salMin,
            SalaryMax       = salMax,
            SalaryType      = salType,
            SalaryRangeText = salMin.HasValue && salMax.HasValue ? $"${salMin:N0}–${salMax:N0}/{salType}" : null,
            ContractType    = schedule?.Contains("Full", StringComparison.OrdinalIgnoreCase) == true ? "FullTime" : schedule,
            ApplyType       = "ExternalApply",
            WorkType        = "OnSite",
            CompanyName     = orgName,
            CompanyType     = "University",
            IsUniversityJob = true,
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
