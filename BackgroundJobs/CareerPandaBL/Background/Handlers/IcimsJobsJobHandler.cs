// CareerPandaBL/Background/Handlers/IcimsJobsJobHandler.cs
// SOURCE : iCIMS Public Careers Page (HTML; no auth required)
// FLOW   : Load board tokens from api.icims_board_tokens (status != INVALID)
//          → GET https://careers-{slug}.icims.com/jobs/search?ss=1&searchRelation=keyword_all&in_iframe=1
//          → Extract Schema.org JobPosting JSON-LD blocks embedded in the HTML
//          → Update token status (VALID / EMPTY / INVALID)
//          → Upsert into api.raw_jobs (US-only)
//
// NOTE   : iCIMS has no public JSON API, so we parse the HTML. JSON-LD blocks
//          are the most stable surface (used by iCIMS for SEO/Google Jobs).
//          Job descriptions and salary are NOT in the search page — we'd need a
//          per-job detail call to get them, which is too slow at this scale.
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using CareerPanda.DataAccess.DA;
using CareerPanda.DataAccess.Entities.Api;
using CareerPanda.Framework.Cache;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CareerPanda.BL.Background.Handlers;

public partial class IcimsJobsJobHandler : IJobHandler
{
    public string JobType => "IcimsJobs";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _http;
    private readonly ICacheService _cache;
    private readonly ILogger<IcimsJobsJobHandler> _logger;

    // How many boards are processed concurrently — tunable via appsettings.
    private readonly int _companyParallel;

    public IcimsJobsJobHandler(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        ICacheService cacheService,
        IConfiguration configuration,
        ILogger<IcimsJobsJobHandler> logger)
    {
        _scopeFactory = scopeFactory;
        _http         = httpClientFactory;
        _cache        = cacheService;
        _logger       = logger;

        _companyParallel = Math.Max(1,
            configuration.GetSection("JobApiSettings").GetValue("IcimsCompanyParallel", 12));
    }

    // ── Matches every <script type="application/ld+json">...</script> block.
    [GeneratedRegex(@"<script[^>]*type=""application/ld\+json""[^>]*>(?<json>[\s\S]*?)</script>", RegexOptions.IgnoreCase)]
    private static partial Regex JsonLdBlocks();

    [GeneratedRegex(@"/jobs/(\d+)/")]
    private static partial Regex JobIdInUrl();

    public async Task ExecuteAsync(
        JobWorkRequest request,
        IJobProgressReporter progress,
        CancellationToken cancellationToken)
    {
        var run = new ApiJobFetchRun
        {
            Id               = request.JobId,
            BackgroundTaskId = request.JobId,
            JobCategory      = "IcimsJobs",
            ApiSource        = "iCIMS",
            Status           = "Running",
            StartedAt        = DateTime.UtcNow,
            CreatedById      = request.UserId
        };

        HashSet<string> sponsors;
        List<ApiIcimsBoardToken> tokens;
        using (var setupScope = _scopeFactory.CreateScope())
        {
            var setupDa = setupScope.ServiceProvider.GetRequiredService<IJobFetchDA>();
            await setupDa.CreateFetchRunAsync(run);

            sponsors = await LoadSponsorsAsync(setupDa, cancellationToken);
            tokens   = await setupDa.GetActiveIcimsTokensAsync(cancellationToken);
            _logger.LogInformation("[iCIMS] Loaded {Tokens} tokens, {Sponsors} sponsors", tokens.Count, sponsors.Count);
        }

        // Counters shared across parallel workers — mutate only via Interlocked.
        int totalFetched = 0, totalInserted = 0, totalUpdated = 0, totalErrors = 0, companiesProcessed = 0;

        // UpdateFetchRunStatsAsync/ReportProgressAsync wrap scoped DbContexts — serialize them.
        using var progressLock = new SemaphoreSlim(1, 1);

        try
        {
            var client = _http.CreateClient("iCIMS");
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (compatible; CareerPanda/1.0; jobs-aggregator)");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml");

            var parallelOpts = new ParallelOptions
            {
                MaxDegreeOfParallelism = _companyParallel,
                CancellationToken      = cancellationToken
            };

            _logger.LogInformation("[iCIMS] Processing {Total} boards (companyParallel={C})", tokens.Count, _companyParallel);

            await Parallel.ForEachAsync(tokens, parallelOpts, async (token, ct) =>
            {
                bool isH1B = CompanyNameNormalizer.IsH1BSponsored(token.CompanyName, sponsors);

                // Each parallel worker gets its own DbContext — EF Core is not thread-safe
                using var itemScope = _scopeFactory.CreateScope();
                var fetchDa = itemScope.ServiceProvider.GetRequiredService<IJobFetchDA>();

                try
                {
                    var (jobs, code, status, count) =
                        await FetchCompanyJobsAsync(client, token, run.Id, isH1B, ct);

                    if (status != null)
                        await fetchDa.UpdateIcimsTokenStatusAsync(token.Id, status, code, count, ct);

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
                    _logger.LogWarning(ex, "[iCIMS] Transient error {Company} — status unchanged", token.CompanyName);
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
            "[iCIMS] Done — Companies={C} Fetched={F} Inserted={I} Updated={U} Errors={E}",
            companiesProcessed, totalFetched, totalInserted, totalUpdated, totalErrors);
    }

    // ── H1B sponsor check (same pattern as other ATS handlers) ────────────────

    private async Task<HashSet<string>> LoadSponsorsAsync(IJobFetchDA fetchDa, CancellationToken ct)
    {
        var cached = await _cache.GetAsync<List<string>>(SponsorCacheWarmupService.CacheKey, ct);
        if (cached is { Count: > 0 }) return CompanyNameNormalizer.BuildSponsorSet(cached);
        var names = await fetchDa.GetH1BSponsorNamesAsync(ct);
        await _cache.SetAsync(SponsorCacheWarmupService.CacheKey, names, SponsorCacheWarmupService.CacheTtl, ct);
        return CompanyNameNormalizer.BuildSponsorSet(names);
    }

    // ── Per-company fetch & parse ─────────────────────────────────────────────

    private async Task<(List<ApiRawJob> jobs, short httpCode, string? tokenStatus, int jobCount)>
        FetchCompanyJobsAsync(
            HttpClient client,
            ApiIcimsBoardToken token,
            string fetchRunId,
            bool isH1B,
            CancellationToken ct)
    {
        var url = $"https://careers-{token.BoardToken}.icims.com/jobs/search?ss=1&searchRelation=keyword_all&in_iframe=1";
        using var resp = await client.GetAsync(url, ct);
        var httpCode = (short)resp.StatusCode;

        if (!resp.IsSuccessStatusCode)
        {
            if (resp.StatusCode is HttpStatusCode.NotFound
                                or HttpStatusCode.Forbidden
                                or HttpStatusCode.Unauthorized
                                or HttpStatusCode.BadRequest)
                return ([], httpCode, "INVALID", 0);
            return ([], httpCode, null, 0);
        }

        var html = await resp.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(html))
            return ([], httpCode, "EMPTY", 0);

        var results = new List<ApiRawJob>();
        int totalSeen = 0;

        foreach (Match m in JsonLdBlocks().Matches(html))
        {
            var raw = m.Groups["json"].Value.Trim();
            if (raw.Length == 0) continue;

            JsonElement doc;
            try { doc = JsonSerializer.Deserialize<JsonElement>(raw); }
            catch { continue; }

            // JSON-LD can be a single object or an array
            if (doc.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in doc.EnumerateArray())
                    TryMap(item, token, fetchRunId, isH1B, results, ref totalSeen);
            }
            else if (doc.ValueKind == JsonValueKind.Object)
            {
                TryMap(doc, token, fetchRunId, isH1B, results, ref totalSeen);
            }
        }

        if (totalSeen == 0) return ([], httpCode, "EMPTY", 0);
        return (results, httpCode, results.Count > 0 ? "VALID" : "EMPTY", totalSeen);
    }

    private static void TryMap(
        JsonElement el, ApiIcimsBoardToken token, string fetchRunId, bool isH1B,
        List<ApiRawJob> sink, ref int seen)
    {
        if (el.ValueKind != JsonValueKind.Object) return;

        // Only care about @type == "JobPosting"
        if (!el.TryGetProperty("@type", out var typeEl)) return;
        var typeStr = typeEl.ValueKind == JsonValueKind.String ? typeEl.GetString() : null;
        if (!string.Equals(typeStr, "JobPosting", StringComparison.OrdinalIgnoreCase)) return;

        seen++;
        var mapped = MapJob(el, token, fetchRunId, isH1B);
        if (mapped != null) sink.Add(mapped);
    }

    // ── Field mapping ─────────────────────────────────────────────────────────

    private static ApiRawJob? MapJob(JsonElement d, ApiIcimsBoardToken token, string fetchRunId, bool isH1B)
    {
        var jobTitle = d.TryGetProperty("title", out var t) ? t.GetString() ?? "Untitled" : "Untitled";

        string? jobLink = null;
        if (d.TryGetProperty("url", out var ue) && ue.ValueKind == JsonValueKind.String)
            jobLink = ue.GetString();
        else if (d.TryGetProperty("hiringOrganization", out var ho)
                 && ho.ValueKind == JsonValueKind.Object
                 && ho.TryGetProperty("sameAs", out var sa) && sa.ValueKind == JsonValueKind.String)
            jobLink = sa.GetString();

        // SourceId from identifier.value, identifier, or URL tail
        string? sourceId = null;
        if (d.TryGetProperty("identifier", out var idEl))
        {
            if (idEl.ValueKind == JsonValueKind.String) sourceId = idEl.GetString();
            else if (idEl.ValueKind == JsonValueKind.Object
                     && idEl.TryGetProperty("value", out var idVal))
            {
                sourceId = idVal.ValueKind switch
                {
                    JsonValueKind.String => idVal.GetString(),
                    JsonValueKind.Number => idVal.GetInt64().ToString(),
                    _ => null
                };
            }
        }
        if (string.IsNullOrWhiteSpace(sourceId) && !string.IsNullOrWhiteSpace(jobLink))
        {
            // Pull numeric id out of "/jobs/12345/job-title/job"
            var m = JobIdInUrl().Match(jobLink);
            if (m.Success) sourceId = m.Groups[1].Value;
        }
        if (string.IsNullOrWhiteSpace(sourceId)) return null;

        // ── Location ──────────────────────────────────────────────────────────
        // country starts null; only set to "US" after the IsUs check passes.
        string? city = null, state = null, country = null;

        if (d.TryGetProperty("jobLocation", out var jl))
        {
            // jobLocation can be an object or array
            var first = jl.ValueKind == JsonValueKind.Array
                ? jl.EnumerateArray().FirstOrDefault()
                : jl;
            if (first.ValueKind == JsonValueKind.Object
                && first.TryGetProperty("address", out var addr)
                && addr.ValueKind == JsonValueKind.Object)
            {
                if (addr.TryGetProperty("addressLocality", out var c) && c.ValueKind == JsonValueKind.String)
                    city = c.GetString();
                if (addr.TryGetProperty("addressRegion", out var s) && s.ValueKind == JsonValueKind.String)
                    state = s.GetString();
                if (addr.TryGetProperty("addressCountry", out var co))
                {
                    country = co.ValueKind switch
                    {
                        JsonValueKind.String => co.GetString(),
                        JsonValueKind.Object when co.TryGetProperty("name", out var nm) => nm.GetString(),
                        _ => null
                    };
                }
            }
        }

        bool isRemote = false;
        if (d.TryGetProperty("jobLocationType", out var jlt)
            && jlt.ValueKind == JsonValueKind.String
            && string.Equals(jlt.GetString(), "TELECOMMUTE", StringComparison.OrdinalIgnoreCase))
            isRemote = true;

        var workType    = isRemote ? "Remote" : "OnSite";
        var jobWorkMode = isRemote ? "Remote" : "OnSite";

        // US filter — non-US remote jobs are kept (preserve original country);
        // non-US on-site jobs are dropped.
        bool isUs = UsLocationHelper.IsUs(country, state);
        if (!isUs && !isRemote) return null;
        if (isUs) country = "US";

        // ── Employment type ───────────────────────────────────────────────────
        bool isContract = false, isInternship = false;
        if (d.TryGetProperty("employmentType", out var etEl))
        {
            string et = etEl.ValueKind switch
            {
                JsonValueKind.String => etEl.GetString() ?? "",
                JsonValueKind.Array  => string.Join(",", etEl.EnumerateArray().Select(x => x.GetString() ?? "")),
                _ => ""
            };
            if (et.Contains("CONTRACTOR", StringComparison.OrdinalIgnoreCase)
                || et.Contains("TEMPORARY", StringComparison.OrdinalIgnoreCase))
                isContract = true;
            if (et.Contains("INTERN", StringComparison.OrdinalIgnoreCase))
                isInternship = true;
        }

        // ── Description (JSON-LD usually includes a stripped description) ─────
        string? desc = null;
        if (d.TryGetProperty("description", out var de) && de.ValueKind == JsonValueKind.String)
            desc = de.GetString();

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

        // ── Salary (rare on iCIMS JSON-LD, but parse if present) ─────────────
        decimal? salMin = null, salMax = null;
        string?  salCurrency = "USD", salType = null, salRangeText = null;
        if (d.TryGetProperty("baseSalary", out var bs) && bs.ValueKind == JsonValueKind.Object)
        {
            if (bs.TryGetProperty("currency", out var cur) && cur.ValueKind == JsonValueKind.String)
                salCurrency = cur.GetString();
            if (bs.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Object)
            {
                if (v.TryGetProperty("minValue", out var mn) && mn.ValueKind == JsonValueKind.Number) salMin = mn.GetDecimal();
                if (v.TryGetProperty("maxValue", out var mx) && mx.ValueKind == JsonValueKind.Number) salMax = mx.GetDecimal();
                if (v.TryGetProperty("unitText", out var ut) && ut.ValueKind == JsonValueKind.String)
                {
                    salType = ut.GetString()?.ToUpperInvariant() switch
                    {
                        "YEAR" => "Annual",
                        "MONTH" => "Monthly",
                        "HOUR" => "Hourly",
                        _ => null
                    };
                }
            }
            if (salMin.HasValue && salMax.HasValue)
                salRangeText = salType == "Hourly"
                    ? $"${salMin:N0}–${salMax:N0}/hr"
                    : $"${salMin:N0}–${salMax:N0}/yr";
        }

        // ── Post date ─────────────────────────────────────────────────────────
        DateTime? postDate = null;
        if (d.TryGetProperty("datePosted", out var dp) && dp.ValueKind == JsonValueKind.String
            && DateTime.TryParse(dp.GetString(), out var pdt))
            postDate = pdt.ToUniversalTime();

        return new ApiRawJob
        {
            PublicId        = Guid.NewGuid().ToString("N"),
            Source          = "iCIMS",
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
            Industry        = token.Industry,
            Skills          = null,
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

}
