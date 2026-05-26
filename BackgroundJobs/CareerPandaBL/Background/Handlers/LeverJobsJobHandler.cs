// CareerPandaBL/Background/Handlers/LeverJobsJobHandler.cs
// SOURCE : Lever Job Board API (public, no auth required)
// FLOW   : Load board tokens from api.lever_board_tokens (status != INVALID)
//          → GET https://api.lever.co/v0/postings/{token}?mode=json  (full list + details in one call)
//          → Update token status (VALID / EMPTY / INVALID) based on response
//          → Upsert into api.raw_jobs
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using CareerPanda.DataAccess.DA;
using CareerPanda.DataAccess.Entities.Api;
using CareerPanda.Framework.Cache;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CareerPanda.BL.Background.Handlers;

public partial class LeverJobsJobHandler : IJobHandler
{
    public string JobType => "LeverJobs";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _http;
    private readonly ICacheService _cache;
    private readonly ILogger<LeverJobsJobHandler> _logger;

    public LeverJobsJobHandler(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        ICacheService cacheService,
        ILogger<LeverJobsJobHandler> logger)
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

        var run = new ApiJobFetchRun
        {
            Id               = request.JobId,
            BackgroundTaskId = request.JobId,
            JobCategory      = "LeverJobs",
            ApiSource        = "Lever",
            Status           = "Running",
            StartedAt        = DateTime.UtcNow,
            CreatedById      = request.UserId
        };
        await fetchDa.CreateFetchRunAsync(run);

        int totalFetched = 0, totalInserted = 0, totalUpdated = 0, totalErrors = 0, companiesProcessed = 0;

        try
        {
            var sponsors = await LoadSponsorsAsync(fetchDa, cancellationToken);
            _logger.LogInformation("[Lever] Loaded {Count} H1B sponsors for matching", sponsors.Count);

            var tokens = await fetchDa.GetActiveLeverTokensAsync(cancellationToken);
            _logger.LogInformation("[Lever] Loaded {Count} board tokens", tokens.Count);

            var client = _http.CreateClient();
            client.DefaultRequestHeaders.Add("User-Agent", "CareerPanda/1.0 jobs-aggregator");

            for (int i = 0; i < tokens.Count; i++)
            {
                if (cancellationToken.IsCancellationRequested) break;

                var token = tokens[i];
                bool isH1B = IsH1BSponsored(token.CompanyName, sponsors);

                _logger.LogInformation(
                    "[Lever] [{I}/{Total}] Processing {Company} ({Token}) H1B={H1B}",
                    i + 1, tokens.Count, token.CompanyName, token.BoardToken, isH1B);

                try
                {
                    var (jobs, httpCode, tokenStatus, jobCount) =
                        await FetchCompanyJobsAsync(client, token, run.Id, isH1B, cancellationToken);

                    // Update token status after every company (validates on first run)
                    await fetchDa.UpdateLeverTokenStatusAsync(token.Id, tokenStatus, httpCode, jobCount, cancellationToken);

                    if (jobs.Count > 0)
                    {
                        totalFetched += jobs.Count;
                        var (ins, upd, err) = await fetchDa.BulkUpsertRawJobsAsync(jobs, cancellationToken);
                        totalInserted += ins;
                        totalUpdated  += upd;
                        totalErrors   += err;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    totalErrors++;
                    _logger.LogWarning(ex, "[Lever] Failed processing {Company}", token.CompanyName);
                }

                companiesProcessed++;

                if (companiesProcessed % 10 == 0 || i == tokens.Count - 1)
                {
                    await fetchDa.UpdateFetchRunStatsAsync(
                        run.Id, totalFetched, totalInserted, totalUpdated, 0, totalErrors, companiesProcessed);

                    int pct = (int)((double)(i + 1) / tokens.Count * 90);
                    await progress.ReportProgressAsync(pct,
                        $"Companies: {companiesProcessed}/{tokens.Count} — Inserted: {totalInserted}, Updated: {totalUpdated}");
                }

                if (i < tokens.Count - 1)
                    await Task.Delay(200, cancellationToken);
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
            "[Lever] Done — Companies={C} Fetched={F} Inserted={I} Updated={U} Errors={E}",
            companiesProcessed, totalFetched, totalInserted, totalUpdated, totalErrors);
    }

    // ── H1B sponsor check ─────────────────────────────────────────────────────

    private async Task<HashSet<string>> LoadSponsorsAsync(IJobFetchDA fetchDa, CancellationToken ct)
    {
        var cached = await _cache.GetAsync<List<string>>(SponsorCacheWarmupService.CacheKey, ct);
        if (cached is { Count: > 0 })
            return BuildSponsorSet(cached);

        var names = await fetchDa.GetH1BSponsorNamesAsync(ct);
        await _cache.SetAsync(SponsorCacheWarmupService.CacheKey, names, SponsorCacheWarmupService.CacheTtl, ct);
        _logger.LogInformation("[Lever] Reloaded {Count} H1B sponsors from DB into cache", names.Count);
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

    // ── Per-company fetch ─────────────────────────────────────────────────────

    private async Task<(List<ApiRawJob> jobs, short httpCode, string tokenStatus, int jobCount)>
        FetchCompanyJobsAsync(
            HttpClient client,
            ApiLeverBoardToken token,
            string fetchRunId,
            bool isH1B,
            CancellationToken ct)
    {
        // Lever v0 returns full job details in a single call — no per-job follow-up needed
        var url = $"https://api.lever.co/v0/postings/{token.BoardToken}?mode=json";
        using var resp = await client.GetAsync(url, ct);

        var httpCode = (short)resp.StatusCode;

        if (!resp.IsSuccessStatusCode)
        {
            var status = (int)resp.StatusCode == 404 ? "INVALID" : "UNKNOWN";
            _logger.LogWarning("[Lever] {Status} fetching {Token}", (int)resp.StatusCode, token.BoardToken);
            return ([], httpCode, status, 0);
        }

        var jobs = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);

        if (jobs.ValueKind != JsonValueKind.Array)
            return ([], httpCode, "INVALID", 0);

        var jobArray = jobs.EnumerateArray().ToList();

        if (jobArray.Count == 0)
            return ([], httpCode, "EMPTY", 0);

        _logger.LogInformation("[Lever] {Company}: {Count} jobs", token.CompanyName, jobArray.Count);

        var results = new List<ApiRawJob>(jobArray.Count);
        foreach (var j in jobArray)
        {
            var mapped = MapJob(j, token, fetchRunId, isH1B);
            if (mapped != null) results.Add(mapped);
        }

        return (results, httpCode, "VALID", jobArray.Count);
    }

    // ── Field mapping ─────────────────────────────────────────────────────────

    private static ApiRawJob? MapJob(JsonElement d, ApiLeverBoardToken token, string fetchRunId, bool isH1B)
    {
        if (!d.TryGetProperty("id", out var idEl)) return null;

        var sourceId = idEl.GetString();
        if (string.IsNullOrEmpty(sourceId)) return null;

        var jobTitle = d.TryGetProperty("text", out var t) ? t.GetString() ?? "Untitled" : "Untitled";
        var jobLink  = d.TryGetProperty("hostedUrl", out var hu) ? hu.GetString() : null;

        // ── Categories (location, commitment, department, team) ───────────────
        string? city = null, state = null, country = "US";
        var workType    = "OnSite";
        var jobWorkMode = "OnSite";
        string? department = null;
        bool isContract = false, isInternship = false;

        if (d.TryGetProperty("categories", out var cats))
        {
            if (cats.TryGetProperty("location", out var loc))
                ParseLocation(loc.GetString() ?? "", out city, out state, out country, out workType, out jobWorkMode);

            if (cats.TryGetProperty("department", out var dept))
                department = dept.GetString();

            if (cats.TryGetProperty("commitment", out var commit))
            {
                var c = commit.GetString() ?? "";
                isContract   = c.Contains("contract", StringComparison.OrdinalIgnoreCase)
                            || c.Contains("freelance", StringComparison.OrdinalIgnoreCase);
                isInternship = c.Contains("intern", StringComparison.OrdinalIgnoreCase);
            }
        }

        // ── Description ───────────────────────────────────────────────────────
        string? desc = null;
        if (d.TryGetProperty("description", out var descEl) && descEl.ValueKind == JsonValueKind.String)
            desc = descEl.GetString();

        // ── H1B ──────────────────────────────────────────────────────────────
        var isH1BFinal = isH1B ||
            (desc != null && (desc.Contains("h1b", StringComparison.OrdinalIgnoreCase) ||
                              desc.Contains("h-1b", StringComparison.OrdinalIgnoreCase) ||
                              desc.Contains("visa sponsor", StringComparison.OrdinalIgnoreCase)));

        // ── Post date (epoch milliseconds) ────────────────────────────────────
        DateTime? postDate = null;
        if (d.TryGetProperty("createdAt", out var ca) && ca.ValueKind == JsonValueKind.Number)
        {
            var epochMs = ca.GetInt64();
            postDate = DateTimeOffset.FromUnixTimeMilliseconds(epochMs).UtcDateTime;
        }

        return new ApiRawJob
        {
            PublicId        = Guid.NewGuid().ToString("N"),
            Source          = "Lever",
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
            SalaryMin       = null,
            SalaryMax       = null,
            SalaryType      = null,
            SalaryRangeText = null,
            SalaryCurrency  = "USD",
            WorkType        = workType,
            JobWorkMode     = jobWorkMode,
            Industry        = token.Industry ?? department,
            Skills          = department != null ? [department] : null,
            ApplyType       = "ExternalApply",
            CompanyName     = token.CompanyName,
            CompanyUrl      = token.BoardUrl,
            CompanyType     = "Private",
            IsH1BSponsored  = isH1BFinal,
            IsSponsored     = isH1BFinal,
            IsW2            = null,
            IsC2C           = false,
            IsContractJob   = isContract,
            IsFreelanceJob  = false,
            IsPrimeVendor   = false,
            IsStaffing      = false,
            IsStartupJob    = false,
            IsNonProfitJob  = false,
            IsUniversityJob = isInternship,
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
