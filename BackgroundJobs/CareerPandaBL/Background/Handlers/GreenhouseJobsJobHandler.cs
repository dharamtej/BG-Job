// CareerPandaBL/Background/Handlers/GreenhouseJobsJobHandler.cs
// SOURCE : Greenhouse Job Board API (public, no auth required)
// FLOW   : Load board tokens from api.greenhouse_board_tokens
//          → GET /v1/boards/{token}/jobs        (job list with IDs)
//          → GET /v1/boards/{token}/jobs/{id}?content=true  (full details)
//          → Upsert into api.raw_jobs + api.companies
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using CareerPanda.DataAccess.DA;
using CareerPanda.DataAccess.Entities.Api;
using CareerPanda.Framework.Cache;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CareerPanda.BL.Background.Handlers;

public partial class GreenhouseJobsJobHandler : IJobHandler
{
    public string JobType => "GreenhouseJobs";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _http;
    private readonly ICacheService _cache;
    private readonly ILogger<GreenhouseJobsJobHandler> _logger;

    public GreenhouseJobsJobHandler(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        ICacheService cacheService,
        ILogger<GreenhouseJobsJobHandler> logger)
    {
        _scopeFactory = scopeFactory;
        _http         = httpClientFactory;
        _cache        = cacheService;
        _logger       = logger;
    }

    public async Task ExecuteAsync(
        JobWorkRequest request,
        IJobProgressReporter progress,
        CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var fetchDa     = scope.ServiceProvider.GetRequiredService<IJobFetchDA>();

        // Create fetch-run row
        var run = new ApiJobFetchRun
        {
            Id               = request.JobId,
            BackgroundTaskId = request.JobId,
            JobCategory      = "GreenhouseJobs",
            ApiSource        = "Greenhouse",
            Status           = "Running",
            StartedAt        = DateTime.UtcNow,
            CreatedById      = request.UserId
        };
        await fetchDa.CreateFetchRunAsync(run);

        int totalFetched = 0, totalInserted = 0, totalUpdated = 0, totalErrors = 0, companiesProcessed = 0;

        try
        {
            // Load H1B sponsor set from Redis cache (same source as all other handlers)
            var sponsors = await LoadSponsorsAsync(fetchDa, cancellationToken);
            _logger.LogInformation("[Greenhouse] Loaded {Count} H1B sponsors for matching", sponsors.Count);

            var tokens = await fetchDa.GetValidGreenhouseTokensAsync(cancellationToken);
            _logger.LogInformation("[Greenhouse] Loaded {Count} board tokens", tokens.Count);

            var client = _http.CreateClient();
            client.DefaultRequestHeaders.Add("User-Agent", "CareerPanda/1.0 jobs-aggregator");

            for (int i = 0; i < tokens.Count; i++)
            {
                if (cancellationToken.IsCancellationRequested) break;

                var token = tokens[i];

                // Resolve H1B flag once per company — all jobs from this board share the same employer
                bool isH1B = IsH1BSponsored(token.CompanyName, sponsors);

                _logger.LogInformation(
                    "[Greenhouse] [{I}/{Total}] Processing {Company} ({Token}) H1B={H1B}",
                    i + 1, tokens.Count, token.CompanyName, token.BoardToken, isH1B);

                try
                {
                    var (jobs, httpCode, tokenStatus, jobCount) =
                        await FetchCompanyJobsAsync(client, token, run.Id, isH1B, cancellationToken);

                    // null = transient error — leave status unchanged so it retries next run
                    if (tokenStatus != null)
                        await fetchDa.UpdateGreenhouseTokenStatusAsync(token.Id, tokenStatus, httpCode, jobCount, cancellationToken);

                    if (jobs.Count > 0)
                    {
                        totalFetched += jobs.Count;
                        var (ins, upd, err) = await fetchDa.BulkUpsertRawJobsAsync(jobs, cancellationToken);
                        totalInserted += ins;
                        totalUpdated  += upd;
                        totalErrors   += err;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    // Transient error (timeout, DNS) — don't touch status, will retry next run
                    totalErrors++;
                    _logger.LogWarning(ex, "[Greenhouse] Transient error {Company} — status unchanged", token.CompanyName);
                }

                companiesProcessed++;

                // Update stats and progress every 10 companies
                if (companiesProcessed % 10 == 0 || i == tokens.Count - 1)
                {
                    await fetchDa.UpdateFetchRunStatsAsync(
                        run.Id, totalFetched, totalInserted, totalUpdated, 0, totalErrors, companiesProcessed);

                    int pct = (int)((double)(i + 1) / tokens.Count * 90);
                    await progress.ReportProgressAsync(pct,
                        $"Companies: {companiesProcessed}/{tokens.Count} — Inserted: {totalInserted}, Updated: {totalUpdated}");
                }

                // Small delay between companies to be a polite API consumer
                if (i < tokens.Count - 1)
                    await Task.Delay(300, cancellationToken);
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

        _logger.LogInformation(
            "[Greenhouse] Done — Companies={C} Fetched={F} Inserted={I} Updated={U} Errors={E}",
            companiesProcessed, totalFetched, totalInserted, totalUpdated, totalErrors);
    }

    // ── H1B sponsor check ─────────────────────────────────────────────────────

    private async Task<HashSet<string>> LoadSponsorsAsync(IJobFetchDA fetchDa, CancellationToken ct)
    {
        var cached = await _cache.GetAsync<List<string>>(SponsorCacheWarmupService.CacheKey, ct);
        if (cached is { Count: > 0 })
            return BuildSponsorSet(cached);

        // Cache miss — reload from DB and re-cache
        var names = await fetchDa.GetH1BSponsorNamesAsync(ct);
        await _cache.SetAsync(SponsorCacheWarmupService.CacheKey, names, SponsorCacheWarmupService.CacheTtl, ct);
        _logger.LogInformation("[Greenhouse] Reloaded {Count} H1B sponsors from DB into cache", names.Count);
        return BuildSponsorSet(names);
    }

    private static HashSet<string> BuildSponsorSet(List<string> names)
    {
        var set = new HashSet<string>(names.Count * 2, StringComparer.OrdinalIgnoreCase);
        foreach (var name in names) { set.Add(name); set.Add(NormalizeCompanyName(name)); }
        return set;
    }

    private static bool IsH1BSponsored(string companyName, HashSet<string> sponsors) =>
        sponsors.Contains(companyName) || sponsors.Contains(NormalizeCompanyName(companyName));

    private static readonly string[] LegalSuffixes =
        ["INCORPORATED", "CORPORATION", "LIMITED", "INC", "LLC", "CORP", "LTD", "CO", "LP", "LLP", "PLLC", "PC"];

    [GeneratedRegex(@"[^\w\s]")]
    private static partial Regex NonWordChars();

    private static string NormalizeCompanyName(string name)
    {
        var upper    = name.ToUpperInvariant();
        var stripped = NonWordChars().Replace(upper, " ");
        var parts    = stripped.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int count    = parts.Length;
        while (count > 1 && LegalSuffixes.Contains(parts[count - 1])) count--;
        return string.Join(' ', parts, 0, count);
    }

    // ── Per-company fetch: list jobs → detail each job ───────────────────────
    // tokenStatus = null means transient error — caller must NOT update the token status

    private async Task<(List<ApiRawJob> jobs, short httpCode, string? tokenStatus, int jobCount)>
        FetchCompanyJobsAsync(
            HttpClient client,
            ApiGreenhouseBoardToken token,
            string fetchRunId,
            bool isH1B,
            CancellationToken ct)
    {
        // Step 1: get job list (id + basic fields) — no auth required
        var listUrl = $"https://boards-api.greenhouse.io/v1/boards/{token.BoardToken}/jobs";
        using var listResp = await client.GetAsync(listUrl, ct);
        var httpCode = (short)listResp.StatusCode;

        if (!listResp.IsSuccessStatusCode)
        {
            _logger.LogWarning("[Greenhouse] {Status} fetching job list for {Token}",
                (int)listResp.StatusCode, token.BoardToken);

            // Definitive failures
            if (listResp.StatusCode is System.Net.HttpStatusCode.NotFound
                                    or System.Net.HttpStatusCode.Forbidden
                                    or System.Net.HttpStatusCode.Unauthorized)
                return ([], httpCode, "INVALID", 0);

            // Transient (5xx, 429) — leave status unchanged
            return ([], httpCode, null, 0);
        }

        var listJson = await listResp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        if (!listJson.TryGetProperty("jobs", out var jobsEl) || jobsEl.ValueKind != JsonValueKind.Array)
            return ([], httpCode, "INVALID", 0);

        var jobIds = jobsEl.EnumerateArray()
            .Where(j => j.TryGetProperty("id", out _))
            .Select(j => j.GetProperty("id").GetInt64())
            .ToList();

        if (jobIds.Count == 0)
            return ([], httpCode, "EMPTY", 0);

        _logger.LogInformation("[Greenhouse] {Company}: {Count} jobs to fetch", token.CompanyName, jobIds.Count);

        // Step 2: fetch full detail for each job
        var results = new List<ApiRawJob>(jobIds.Count);
        foreach (var jobId in jobIds)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var detailUrl = $"https://boards-api.greenhouse.io/v1/boards/{token.BoardToken}/jobs/{jobId}?content=true";
                using var detailResp = await client.GetAsync(detailUrl, ct);

                if (!detailResp.IsSuccessStatusCode) continue;

                var detail = await detailResp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
                var mapped = MapJob(detail, token, fetchRunId, isH1B);
                if (mapped != null) results.Add(mapped);

                await Task.Delay(50, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Greenhouse] Failed fetching job {JobId} for {Token}", jobId, token.BoardToken);
            }
        }

        return (results, httpCode, results.Count > 0 ? "VALID" : "EMPTY", jobIds.Count);
    }

    // ── Field mapping ─────────────────────────────────────────────────────────

    private static ApiRawJob? MapJob(JsonElement d, ApiGreenhouseBoardToken token, string fetchRunId, bool isH1B)
    {
        if (!d.TryGetProperty("id", out var idEl)) return null;

        var sourceId = idEl.GetInt64().ToString();
        var jobTitle = d.TryGetProperty("title", out var t) ? t.GetString() ?? "Untitled" : "Untitled";
        var jobLink  = d.TryGetProperty("absolute_url", out var au) ? au.GetString() : null;

        // ── Location ──────────────────────────────────────────────────────────
        string? city = null, state = null, country = "US";
        var workType    = "OnSite";
        var jobWorkMode = "OnSite";

        if (d.TryGetProperty("location", out var loc) && loc.TryGetProperty("name", out var locName))
            ParseLocation(locName.GetString() ?? "", out city, out state, out country, out workType, out jobWorkMode);

        // ── Department / Industry ─────────────────────────────────────────────
        string? department = null;
        string[]? skills   = null;
        if (d.TryGetProperty("departments", out var depts) && depts.ValueKind == JsonValueKind.Array)
        {
            var deptNames = depts.EnumerateArray()
                .Where(x => x.TryGetProperty("name", out _))
                .Select(x => x.GetProperty("name").GetString()!)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToArray();
            if (deptNames.Length > 0) { department = deptNames[0]; skills = deptNames; }
        }

        // ── Description (HTML content) ────────────────────────────────────────
        string? desc = null;
        if (d.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
            desc = content.GetString();

        // Also check description text for H1B keywords in case company name didn't match
        var isH1BFinal = isH1B ||
            (desc != null && (desc.Contains("h1b", StringComparison.OrdinalIgnoreCase) ||
                              desc.Contains("h-1b", StringComparison.OrdinalIgnoreCase) ||
                              desc.Contains("visa sponsor", StringComparison.OrdinalIgnoreCase)));

        // ── Salary ────────────────────────────────────────────────────────────
        decimal? salMin = null, salMax = null;
        string? salCurrency = "USD", salType = null, salRangeText = null;
        if (d.TryGetProperty("pay_input_ranges", out var pay) && pay.ValueKind == JsonValueKind.Array)
        {
            var first = pay.EnumerateArray().FirstOrDefault();
            if (first.ValueKind == JsonValueKind.Object)
            {
                if (first.TryGetProperty("min_cents", out var minC) && minC.ValueKind == JsonValueKind.Number)
                    salMin = minC.GetDecimal() / 100m;
                if (first.TryGetProperty("max_cents", out var maxC) && maxC.ValueKind == JsonValueKind.Number)
                    salMax = maxC.GetDecimal() / 100m;
                if (first.TryGetProperty("currency_type", out var cur))
                    salCurrency = cur.GetString() ?? "USD";
                salType = "Annual";
                if (salMin.HasValue && salMax.HasValue)
                    salRangeText = $"${salMin:N0}–${salMax:N0}/yr";
            }
        }

        // ── Post date ─────────────────────────────────────────────────────────
        DateTime? postDate = null;
        if (d.TryGetProperty("updated_at", out var ua) && DateTime.TryParse(ua.GetString(), out var dt))
            postDate = dt.ToUniversalTime();

        return new ApiRawJob
        {
            PublicId        = Guid.NewGuid().ToString("N"),
            Source          = "Greenhouse",
            SourceId        = sourceId,
            FetchRunId      = fetchRunId,
            JobTitle        = jobTitle,
            JobLink         = jobLink,
            JobDescription  = desc,
            City            = city,
            State           = state,
            Country         = country ?? "US",
            PostDate        = postDate,
            HoursBackPosted = postDate.HasValue ? (int)(DateTime.UtcNow - postDate.Value).TotalHours : null,
            SalaryMin       = salMin,
            SalaryMax       = salMax,
            SalaryType      = salType,
            SalaryRangeText = salRangeText,
            SalaryCurrency  = salCurrency,
            WorkType        = workType,
            JobWorkMode     = jobWorkMode,
            Industry        = token.Industry ?? department,
            Skills          = skills,
            ApplyType       = "ExternalApply",
            CompanyName     = token.CompanyName,
            CompanyUrl      = token.BoardUrl,
            CompanyType     = "Private",
            IsH1BSponsored  = isH1BFinal,
            IsSponsored     = isH1BFinal,
            IsW2            = null,   // unknown — Greenhouse API doesn't expose employment type
            IsC2C           = false,
            IsContractJob   = false,
            IsFreelanceJob  = false,
            IsPrimeVendor   = false,
            IsStaffing      = false,
            IsStartupJob    = false,
            IsNonProfitJob  = false,
            IsUniversityJob = false,
            Status          = true,
            CreatedOn       = DateTime.UtcNow,
            UpdatedOn       = DateTime.UtcNow
        };
    }

    // ── Location parsing ──────────────────────────────────────────────────────

    [GeneratedRegex(@"\b[A-Z]{2}\b")]
    private static partial Regex StateAbbrRegex();

    private static void ParseLocation(
        string raw, out string? city, out string? state,
        out string? country, out string workType, out string jobWorkMode)
    {
        city        = null;
        state       = null;
        country     = "US";
        workType    = "OnSite";
        jobWorkMode = "OnSite";

        if (string.IsNullOrWhiteSpace(raw)) return;

        var lower = raw.ToLowerInvariant();
        if (lower.Contains("remote"))
        {
            workType    = "Remote";
            jobWorkMode = "Remote";
            if (!lower.Contains(',')) return;
        }
        else if (lower.Contains("hybrid"))
        {
            workType    = "Hybrid";
            jobWorkMode = "Hybrid";
        }

        // Parse "City, ST" or "City, State" or "City, ST, Country"
        var parts = raw.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length >= 1) city = parts[0];
        if (parts.Length >= 2)
        {
            var part2 = parts[1].Trim();
            if (part2.Length > 3 && !StateAbbrRegex().IsMatch(part2))
                country = part2;
            else
                state = part2;
        }
        if (parts.Length >= 3)
            country = parts[2].Trim();

        if (state == null && country == "US" && parts.Length >= 2)
            country = parts[1].Trim();
    }
}
