// CareerPandaBL/Background/Handlers/AshbyJobsJobHandler.cs
// SOURCE : Ashby Public Job Board API (no auth required)
// FLOW   : Load board tokens from api.ashby_board_tokens (status != INVALID)
//          → GET https://api.ashbyhq.com/posting-api/job-board/{token}?includeCompensation=true
//          → Update token status (VALID / EMPTY / INVALID) based on response
//          → Upsert into api.raw_jobs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using CareerPanda.DataAccess.DA;
using CareerPanda.DataAccess.Entities.Api;
using CareerPanda.Framework.Cache;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CareerPanda.BL.Background.Handlers;

public partial class AshbyJobsJobHandler : IJobHandler
{
    public string JobType => "AshbyJobs";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _http;
    private readonly ICacheService _cache;
    private readonly ILogger<AshbyJobsJobHandler> _logger;

    // How many boards are processed concurrently — tunable via appsettings.
    private readonly int _companyParallel;

    public AshbyJobsJobHandler(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        ICacheService cacheService,
        IConfiguration configuration,
        ILogger<AshbyJobsJobHandler> logger)
    {
        _scopeFactory = scopeFactory;
        _http         = httpClientFactory;
        _cache        = cacheService;
        _logger       = logger;

        _companyParallel = Math.Max(1,
            configuration.GetSection("JobApiSettings").GetValue("AshbyCompanyParallel", 12));
    }

    public async Task ExecuteAsync(
        JobWorkRequest request,
        IJobProgressReporter progress,
        CancellationToken cancellationToken)
    {
        var run = new ApiJobFetchRun
        {
            Id               = request.JobId,
            BackgroundTaskId = request.JobId,
            JobCategory      = "AshbyJobs",
            ApiSource        = "Ashby",
            Status           = "Running",
            StartedAt        = DateTime.UtcNow,
            CreatedById      = request.UserId
        };

        HashSet<string> sponsors;
        List<ApiAshbyBoardToken> tokens;
        using (var setupScope = _scopeFactory.CreateScope())
        {
            var setupDa = setupScope.ServiceProvider.GetRequiredService<IJobFetchDA>();
            await setupDa.CreateFetchRunAsync(run);

            sponsors = await LoadSponsorsAsync(setupDa, cancellationToken);
            _logger.LogInformation("[Ashby] Loaded {Count} H1B sponsors for matching", sponsors.Count);

            tokens = await setupDa.GetActiveAshbyTokensAsync(cancellationToken);
            _logger.LogInformation("[Ashby] Loaded {Count} board tokens", tokens.Count);
        }

        // Counters shared across parallel workers — mutate only via Interlocked.
        int totalFetched = 0, totalInserted = 0, totalUpdated = 0, totalErrors = 0, companiesProcessed = 0;

        // UpdateFetchRunStatsAsync/ReportProgressAsync wrap scoped DbContexts — serialize them.
        using var progressLock = new SemaphoreSlim(1, 1);

        try
        {
            var client = _http.CreateClient("Ashby");
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "CareerPanda/1.0 jobs-aggregator");

            var parallelOpts = new ParallelOptions
            {
                MaxDegreeOfParallelism = _companyParallel,
                CancellationToken      = cancellationToken
            };

            _logger.LogInformation("[Ashby] Processing {Total} boards (companyParallel={C})", tokens.Count, _companyParallel);

            await Parallel.ForEachAsync(tokens, parallelOpts, async (token, ct) =>
            {
                bool isH1B = CompanyNameNormalizer.IsH1BSponsored(token.CompanyName, sponsors);

                // Each parallel worker gets its own DbContext — EF Core is not thread-safe
                using var itemScope = _scopeFactory.CreateScope();
                var fetchDa = itemScope.ServiceProvider.GetRequiredService<IJobFetchDA>();

                try
                {
                    var (jobs, httpCode, tokenStatus, jobCount) =
                        await FetchCompanyJobsAsync(client, token, run.Id, isH1B, ct);

                    if (tokenStatus != null)
                        await fetchDa.UpdateAshbyTokenStatusAsync(token.Id, tokenStatus, httpCode, jobCount, ct);

                    if (jobs.Count > 0)
                    {
                        Interlocked.Add(ref totalFetched, jobs.Count);
                        var (ins, upd, err) = await fetchDa.BulkUpsertRawJobsAsync(jobs, ct);
                        Interlocked.Add(ref totalInserted, ins);
                        Interlocked.Add(ref totalUpdated,  upd);
                        Interlocked.Add(ref totalErrors,   err);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (FatalDatabaseException) { throw; }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref totalErrors);
                    _logger.LogWarning(ex, "[Ashby] Transient error {Company} — status unchanged", token.CompanyName);
                }

                int done = Interlocked.Increment(ref companiesProcessed);

                if (done % 10 == 0 || done == tokens.Count)
                {
                    int fetched  = Volatile.Read(ref totalFetched);
                    int inserted = Volatile.Read(ref totalInserted);
                    int updated  = Volatile.Read(ref totalUpdated);
                    int errors   = Volatile.Read(ref totalErrors);
                    int pct      = (int)((double)done / tokens.Count * 90);

                    await progressLock.WaitAsync(ct);
                    try
                    {
                        using var statsScope = _scopeFactory.CreateScope();
                        var statsDa = statsScope.ServiceProvider.GetRequiredService<IJobFetchDA>();
                        await statsDa.UpdateFetchRunStatsAsync(run.Id, fetched, inserted, updated, 0, errors, done);
                        await progress.ReportProgressAsync(pct,
                            $"Companies: {done}/{tokens.Count} — Inserted: {inserted}, Updated: {updated}");
                    }
                    finally { progressLock.Release(); }
                }
            });

            using (var doneScope = _scopeFactory.CreateScope())
            {
                var doneDa = doneScope.ServiceProvider.GetRequiredService<IJobFetchDA>();
                await doneDa.CompleteFetchRunAsync(run.Id, "Completed");
            }
        }
        catch (OperationCanceledException)
        {
            using var failScope = _scopeFactory.CreateScope();
            var failDa = failScope.ServiceProvider.GetRequiredService<IJobFetchDA>();
            await failDa.CompleteFetchRunAsync(run.Id, "Cancelled", "Cancelled by user.");
            throw;
        }
        catch (Exception ex)
        {
            using var failScope = _scopeFactory.CreateScope();
            var failDa = failScope.ServiceProvider.GetRequiredService<IJobFetchDA>();
            await failDa.CompleteFetchRunAsync(run.Id, "Failed", ex.Message);
            throw;
        }

        _logger.LogInformation(
            "[Ashby] Done — Companies={C} Fetched={F} Inserted={I} Updated={U} Errors={E}",
            companiesProcessed, totalFetched, totalInserted, totalUpdated, totalErrors);
    }

    // ── H1B sponsor check ─────────────────────────────────────────────────────

    private async Task<HashSet<string>> LoadSponsorsAsync(IJobFetchDA fetchDa, CancellationToken ct)
    {
        var cached = await _cache.GetAsync<List<string>>(SponsorCacheWarmupService.CacheKey, ct);
        if (cached is { Count: > 0 })
            return CompanyNameNormalizer.BuildSponsorSet(cached);

        var names = await fetchDa.GetH1BSponsorNamesAsync(ct);
        await _cache.SetAsync(SponsorCacheWarmupService.CacheKey, names, SponsorCacheWarmupService.CacheTtl, ct);
        _logger.LogInformation("[Ashby] Reloaded {Count} H1B sponsors from DB into cache", names.Count);
        return CompanyNameNormalizer.BuildSponsorSet(names);
    }

    // ── Per-company fetch ─────────────────────────────────────────────────────
    // tokenStatus = null means transient error — caller must NOT update the token status

    private async Task<(List<ApiRawJob> jobs, short httpCode, string? tokenStatus, int jobCount)>
        FetchCompanyJobsAsync(
            HttpClient client,
            ApiAshbyBoardToken token,
            string fetchRunId,
            bool isH1B,
            CancellationToken ct)
    {
        // Ashby returns full job details (incl. compensation) in a single call
        var url = $"https://api.ashbyhq.com/posting-api/job-board/{token.BoardToken}?includeCompensation=true";
        using var resp = await client.GetAsync(url, ct);

        var httpCode = (short)resp.StatusCode;

        if (!resp.IsSuccessStatusCode)
        {
            var sc = resp.StatusCode;
            if (sc is HttpStatusCode.Forbidden or HttpStatusCode.NotFound
                    or HttpStatusCode.Unauthorized or HttpStatusCode.TooManyRequests)
                _logger.LogDebug("[Ashby] {Status} fetching {Token}", (int)sc, token.BoardToken);
            else
                _logger.LogWarning("[Ashby] {Status} fetching {Token}", (int)sc, token.BoardToken);

            // 404 / 400 = board genuinely doesn't exist → permanent INVALID.
            if (sc is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
                return ([], httpCode, "INVALID", 0);

            // 401/403/5xx/429 → transient. Leave status unchanged for retry.
            return ([], httpCode, null, 0);
        }

        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);

        if (doc.ValueKind != JsonValueKind.Object
            || !doc.TryGetProperty("jobs", out var jobsEl)
            || jobsEl.ValueKind != JsonValueKind.Array)
            return ([], httpCode, "INVALID", 0);

        var jobArray = jobsEl.EnumerateArray().ToList();
        if (jobArray.Count == 0)
            return ([], httpCode, "EMPTY", 0);

        _logger.LogInformation("[Ashby] {Company}: {Count} jobs", token.CompanyName, jobArray.Count);

        var results = new List<ApiRawJob>(jobArray.Count);
        foreach (var j in jobArray)
        {
            var mapped = MapJob(j, token, fetchRunId, isH1B);
            if (mapped != null) results.Add(mapped);
        }

        return (results, httpCode, results.Count > 0 ? "VALID" : "EMPTY", jobArray.Count);
    }

    // ── Field mapping ─────────────────────────────────────────────────────────

    private static ApiRawJob? MapJob(JsonElement d, ApiAshbyBoardToken token, string fetchRunId, bool isH1B)
    {
        if (!d.TryGetProperty("id", out var idEl)) return null;
        var sourceId = idEl.GetString();
        if (string.IsNullOrEmpty(sourceId)) return null;

        // Only return jobs that are listed (drafts/unlisted are excluded)
        if (d.TryGetProperty("isListed", out var listed)
            && listed.ValueKind == JsonValueKind.False)
            return null;

        var jobTitle = d.TryGetProperty("title", out var t) ? t.GetString() ?? "Untitled" : "Untitled";
        var jobLink  = d.TryGetProperty("jobUrl", out var ju) ? ju.GetString()
                     : d.TryGetProperty("applyUrl", out var au) ? au.GetString()
                     : null;

        // ── Location ──────────────────────────────────────────────────────────
        // Collect primary + secondary locations; accept the first one that parses to US.
        bool isRemote = d.TryGetProperty("isRemote", out var rem) && rem.ValueKind == JsonValueKind.True;

        var locCandidates = new List<string>();
        if (d.TryGetProperty("location", out var locEl) && locEl.ValueKind == JsonValueKind.String)
        {
            var s = locEl.GetString();
            if (!string.IsNullOrWhiteSpace(s)) locCandidates.Add(s);
        }
        if (d.TryGetProperty("secondaryLocations", out var secLocs) && secLocs.ValueKind == JsonValueKind.Array)
        {
            foreach (var sl in secLocs.EnumerateArray())
            {
                if (sl.TryGetProperty("location", out var slLoc) && slLoc.ValueKind == JsonValueKind.String)
                {
                    var s = slLoc.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) locCandidates.Add(s);
                }
            }
        }

        string? city = null, state = null, country = "US";
        var workType    = isRemote ? "Remote" : "OnSite";
        var jobWorkMode = isRemote ? "Remote" : "OnSite";

        bool foundUs = false;
        foreach (var cand in locCandidates)
        {
            ParseLocation(cand, isRemote,
                out var cCity, out var cState, out var cCountry,
                out var cWork, out var cMode);

            if (UsLocationHelper.CountryVariants.Contains(cCountry ?? "US") || cState != null)
            {
                city = cCity; state = cState; country = "US";
                workType = cWork; jobWorkMode = cMode;
                foundUs = true;
                break;
            }
        }

        // Skip postings with no US location AND not explicitly remote-US.
        if (!foundUs)
        {
            // If isRemote=true and there are no locations at all, treat as US-remote (Ashby convention for global remote rarely sets isRemote without a hint — but be lenient).
            if (isRemote && locCandidates.Count == 0)
            {
                country = "US";
            }
            else
            {
                return null;
            }
        }

        // ── Department ────────────────────────────────────────────────────────
        string? department = null;
        if (d.TryGetProperty("department", out var deptEl) && deptEl.ValueKind == JsonValueKind.String)
            department = deptEl.GetString();
        string? team = null;
        if (d.TryGetProperty("team", out var teamEl) && teamEl.ValueKind == JsonValueKind.String)
            team = teamEl.GetString();

        // ── Employment type ───────────────────────────────────────────────────
        bool isContract   = false;
        bool isInternship = false;
        if (d.TryGetProperty("employmentType", out var etEl) && etEl.ValueKind == JsonValueKind.String)
        {
            var et = etEl.GetString() ?? "";
            if (et.Contains("Contract", StringComparison.OrdinalIgnoreCase) ||
                et.Contains("Temporary", StringComparison.OrdinalIgnoreCase))
                isContract = true;
            if (et.Contains("Intern", StringComparison.OrdinalIgnoreCase))
                isInternship = true;
        }

        // ── Description ───────────────────────────────────────────────────────
        string? desc = null;
        if (d.TryGetProperty("descriptionHtml", out var dh) && dh.ValueKind == JsonValueKind.String)
            desc = dh.GetString();
        else if (d.TryGetProperty("descriptionPlain", out var dp) && dp.ValueKind == JsonValueKind.String)
            desc = dp.GetString();

        // ── H1B keyword fallback + per-job negation ───────────────────────────
        var visaNegation = ContainsAny(desc,
            "do not sponsor", "does not sponsor", "no sponsorship", "unable to sponsor",
            "cannot sponsor", "will not sponsor", "no h-1b", "no h1b",
            "must be authorized to work", "must have work authorization",
            "authorized to work in the us", "authorized to work in the united states");
        var isH1BFinal  = !visaNegation && (isH1B ||
            (desc != null && (desc.Contains("h1b", StringComparison.OrdinalIgnoreCase) ||
                              desc.Contains("h-1b", StringComparison.OrdinalIgnoreCase) ||
                              desc.Contains("visa sponsor", StringComparison.OrdinalIgnoreCase))));
        var isOptCpt    = !visaNegation && ContainsAny(desc, " opt ", "opt/cpt", "stem opt", "opt extension", "f-1 visa", " cpt ");
        var isTnVisa    = !visaNegation && ContainsAny(desc, "tn visa", "tn-1", "tn-2", "usmca", "nafta visa");
        var isE3Visa    = !visaNegation && ContainsAny(desc, "e-3", "e3 visa", "e-3 visa");
        var isJ1Visa    = !visaNegation && ContainsAny(desc, "j-1", "j1 visa", "j-1 visa", "exchange visitor");
        var isGreenCard = !visaNegation && ContainsAny(desc, "green card", "gc sponsor", "perm filing", "eb-2", "eb-3", "labor certification");

        // ── Compensation ──────────────────────────────────────────────────────
        decimal? salMin = null, salMax = null;
        string?  salCurrency = "USD", salType = null, salRangeText = null;
        if (d.TryGetProperty("compensation", out var comp) && comp.ValueKind == JsonValueKind.Object)
        {
            if (comp.TryGetProperty("summaryComponents", out var sc) && sc.ValueKind == JsonValueKind.Array)
            {
                foreach (var sum in sc.EnumerateArray())
                {
                    var compType = sum.TryGetProperty("compensationType", out var ct) ? ct.GetString() : null;
                    if (!string.Equals(compType, "Salary", StringComparison.OrdinalIgnoreCase)) continue;

                    if (sum.TryGetProperty("minValue", out var mn) && mn.ValueKind == JsonValueKind.Number)
                        salMin = mn.GetDecimal();
                    if (sum.TryGetProperty("maxValue", out var mx) && mx.ValueKind == JsonValueKind.Number)
                        salMax = mx.GetDecimal();
                    if (sum.TryGetProperty("currencyCode", out var cc) && cc.ValueKind == JsonValueKind.String)
                        salCurrency = cc.GetString() ?? "USD";
                    if (sum.TryGetProperty("interval", out var iv) && iv.ValueKind == JsonValueKind.String)
                    {
                        var ivStr = iv.GetString() ?? "";
                        salType = ivStr.Contains("year", StringComparison.OrdinalIgnoreCase) ? "Annual"
                                : ivStr.Contains("hour", StringComparison.OrdinalIgnoreCase) ? "Hourly"
                                : ivStr.Contains("month", StringComparison.OrdinalIgnoreCase) ? "Monthly"
                                : null;
                    }
                    break;
                }
                if (salMin.HasValue && salMax.HasValue)
                    salRangeText = salType == "Hourly"
                        ? $"${salMin:N0}–${salMax:N0}/hr"
                        : $"${salMin:N0}–${salMax:N0}/yr";
            }

            // Fallback: free-text "compensationTierSummary" like "$120K – $160K"
            if (!salMin.HasValue && comp.TryGetProperty("compensationTierSummary", out var tier)
                && tier.ValueKind == JsonValueKind.String)
            {
                var tierStr = tier.GetString();
                if (!string.IsNullOrWhiteSpace(tierStr))
                {
                    salRangeText = tierStr;
                    var (pMin, pMax) = ParseCompTierSummary(tierStr);
                    salMin = pMin; salMax = pMax;
                    if (salMin.HasValue) salType ??= "Annual";
                }
            }
        }

        // ── Post date ─────────────────────────────────────────────────────────
        DateTime? postDate = null;
        if (d.TryGetProperty("publishedAt", out var pa) && pa.ValueKind == JsonValueKind.String
            && DateTime.TryParse(pa.GetString(), out var pdt))
            postDate = pdt.ToUniversalTime();
        else if (d.TryGetProperty("updatedAt", out var ua) && ua.ValueKind == JsonValueKind.String
            && DateTime.TryParse(ua.GetString(), out var udt))
            postDate = udt.ToUniversalTime();

        var skills = (department, team) switch
        {
            (not null, not null) => new[] { department, team },
            (not null, null)     => new[] { department },
            (null, not null)     => new[] { team },
            _ => null
        };

        return new ApiRawJob
        {
            PublicId        = Guid.NewGuid().ToString("N"),
            Source          = "Ashby",
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
            IsOptCpt        = isOptCpt,
            IsTnVisa        = isTnVisa,
            IsE3Visa        = isE3Visa,
            IsJ1Visa        = isJ1Visa,
            IsGreenCard     = isGreenCard,
            IsSponsored     = isH1BFinal || isOptCpt || isTnVisa || isE3Visa || isJ1Visa || isGreenCard,
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

    // ── Compensation tier summary parsing ─────────────────────────────────────
    // Matches "$120K – $160K", "$120,000 - $160,000", "$50/hr - $75/hr"
    [GeneratedRegex(@"\$?\s*([\d,]+(?:\.\d+)?)\s*([KkMm])?\s*[-–—]\s*\$?\s*([\d,]+(?:\.\d+)?)\s*([KkMm])?")]
    private static partial Regex CompRangeRegex();

    private static (decimal? min, decimal? max) ParseCompTierSummary(string s)
    {
        var m = CompRangeRegex().Match(s);
        if (!m.Success) return (null, null);

        decimal Scale(string num, string suffix)
        {
            if (!decimal.TryParse(num.Replace(",", ""), out var v)) return 0;
            return suffix.ToUpperInvariant() switch
            {
                "K" => v * 1_000m,
                "M" => v * 1_000_000m,
                _   => v
            };
        }

        var min = Scale(m.Groups[1].Value, m.Groups[2].Value);
        var max = Scale(m.Groups[3].Value, m.Groups[4].Value);
        return (min > 0 ? min : null, max > 0 ? max : null);
    }

    // ── Location parsing ──────────────────────────────────────────────────────
    // US-location reference data lives in UsLocationHelper (shared with all ATS handlers).

    private static void ParseLocation(
        string raw, bool isRemoteFlag,
        out string? city, out string? state, out string? country,
        out string workType, out string jobWorkMode)
    {
        city        = null;
        state       = null;
        country     = "US";
        workType    = isRemoteFlag ? "Remote" : "OnSite";
        jobWorkMode = isRemoteFlag ? "Remote" : "OnSite";

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
            if (UsLocationHelper.StateAbbrs.Contains(part2))            state = part2;
            else if (UsLocationHelper.StateNames.Contains(part2))       state = part2;
            else if (UsLocationHelper.CountryVariants.Contains(part2))  { /* country already US */ }
            else if (part2.Length > 2)                                  country = part2;
        }

        if (parts.Length >= 3)
        {
            var part3 = parts[2].Trim();
            country = UsLocationHelper.CountryVariants.Contains(part3) ? "US" : part3;
        }
    }
}
