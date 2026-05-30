// CareerPandaBL/Background/Handlers/RecruiteeJobsJobHandler.cs
// SOURCE : Recruitee Public Offers API (no auth required)
// FLOW   : Load board tokens from api.recruitee_board_tokens (status != INVALID)
//          → GET https://{slug}.recruitee.com/api/offers/
//          → Update token status (VALID / EMPTY / INVALID)
//          → Map + apply JobValidationGate
//          → Upsert into api.raw_jobs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CareerPanda.DataAccess.DA;
using CareerPanda.DataAccess.Entities.Api;
using CareerPanda.Framework.Cache;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CareerPanda.BL.Background.Handlers;

public class RecruiteeJobsJobHandler : IJobHandler
{
    public string JobType => "RecruiteeJobs";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _http;
    private readonly ICacheService _cache;
    private readonly ILogger<RecruiteeJobsJobHandler> _logger;

    // How many boards are processed concurrently — tunable via appsettings.
    private readonly int _companyParallel;

    public RecruiteeJobsJobHandler(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        ICacheService cacheService,
        IConfiguration configuration,
        ILogger<RecruiteeJobsJobHandler> logger)
    {
        _scopeFactory = scopeFactory;
        _http         = httpClientFactory;
        _cache        = cacheService;
        _logger       = logger;

        _companyParallel = Math.Max(1,
            configuration.GetSection("JobApiSettings").GetValue("RecruiteeCompanyParallel", 12));
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
            JobCategory      = "RecruiteeJobs",
            ApiSource        = "Recruitee",
            Status           = "Running",
            StartedAt        = DateTime.UtcNow,
            CreatedById      = request.UserId
        };

        HashSet<string> sponsors;
        List<ApiRecruiteeBoardToken> tokens;
        using (var setupScope = _scopeFactory.CreateScope())
        {
            var setupDa = setupScope.ServiceProvider.GetRequiredService<IJobFetchDA>();
            await setupDa.CreateFetchRunAsync(run);

            sponsors = await LoadSponsorsAsync(setupDa, cancellationToken);
            tokens   = await setupDa.GetActiveRecruiteeTokensAsync(cancellationToken);
            _logger.LogInformation("[Recruitee] Loaded {Tokens} tokens, {Sponsors} sponsors", tokens.Count, sponsors.Count);
        }

        // Counters shared across parallel workers — mutate only via Interlocked.
        int totalFetched = 0, totalInserted = 0, totalUpdated = 0, totalErrors = 0, companiesProcessed = 0;

        // UpdateFetchRunStatsAsync/ReportProgressAsync wrap scoped DbContexts — serialize them.
        using var progressLock = new SemaphoreSlim(1, 1);

        try
        {
            var client = _http.CreateClient("Recruitee");
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "CareerPanda/1.0 jobs-aggregator");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept",     "application/json");

            var parallelOpts = new ParallelOptions
            {
                MaxDegreeOfParallelism = _companyParallel,
                CancellationToken      = cancellationToken
            };

            _logger.LogInformation("[Recruitee] Processing {Total} boards (companyParallel={C})", tokens.Count, _companyParallel);

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
                        await fetchDa.UpdateRecruiteeTokenStatusAsync(token.Id, status, code, count, ct);

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
                    _logger.LogWarning(ex, "[Recruitee] Transient error {Company} — status unchanged", token.CompanyName);
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
            "[Recruitee] Done — Companies={C} Fetched={F} Inserted={I} Updated={U} Errors={E}",
            companiesProcessed, totalFetched, totalInserted, totalUpdated, totalErrors);
    }

    // ── H1B sponsor cache ─────────────────────────────────────────────────────

    private async Task<HashSet<string>> LoadSponsorsAsync(IJobFetchDA fetchDa, CancellationToken ct)
    {
        var cached = await _cache.GetAsync<List<string>>(SponsorCacheWarmupService.CacheKey, ct);
        if (cached is { Count: > 0 }) return CompanyNameNormalizer.BuildSponsorSet(cached);
        var names = await fetchDa.GetH1BSponsorNamesAsync(ct);
        await _cache.SetAsync(SponsorCacheWarmupService.CacheKey, names, SponsorCacheWarmupService.CacheTtl, ct);
        return CompanyNameNormalizer.BuildSponsorSet(names);
    }

    // ── Per-company fetch ─────────────────────────────────────────────────────

    private async Task<(List<ApiRawJob> jobs, short httpCode, string? tokenStatus, int jobCount)>
        FetchCompanyJobsAsync(
            HttpClient client,
            ApiRecruiteeBoardToken token,
            string fetchRunId,
            bool isH1B,
            CancellationToken ct)
    {
        var url = $"https://{token.BoardToken}.recruitee.com/api/offers/";
        using var resp = await client.GetAsync(url, ct);
        var httpCode = (short)resp.StatusCode;

        if (!resp.IsSuccessStatusCode)
        {
            // 404 / 400 = subdomain genuinely doesn't exist → permanent INVALID.
            if (resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
                return ([], httpCode, "INVALID", 0);
            // 401/403/5xx/429 → transient. Leave status unchanged for retry.
            return ([], httpCode, null, 0);
        }

        JsonElement doc;
        try { doc = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct); }
        catch { return ([], httpCode, "INVALID", 0); }

        if (doc.ValueKind != JsonValueKind.Object
            || !doc.TryGetProperty("offers", out var offersEl)
            || offersEl.ValueKind != JsonValueKind.Array)
            return ([], httpCode, "INVALID", 0);

        var offerArray = offersEl.EnumerateArray().ToList();
        if (offerArray.Count == 0)
            return ([], httpCode, "EMPTY", 0);

        var results = new List<ApiRawJob>(offerArray.Count);
        foreach (var o in offerArray)
        {
            var mapped = MapJob(o, token, fetchRunId, isH1B);
            if (mapped != null) results.Add(mapped);
        }

        return (results, httpCode, results.Count > 0 ? "VALID" : "EMPTY", offerArray.Count);
    }

    // ── Field mapping ─────────────────────────────────────────────────────────

    private ApiRawJob? MapJob(JsonElement d, ApiRecruiteeBoardToken token, string fetchRunId, bool isH1B)
    {
        // id is required
        if (!d.TryGetProperty("id", out var idEl)) return null;
        var sourceId = idEl.ValueKind switch
        {
            JsonValueKind.Number => idEl.GetInt64().ToString(),
            JsonValueKind.String => idEl.GetString(),
            _ => null
        };
        if (string.IsNullOrEmpty(sourceId)) return null;

        // Skip non-published offers
        if (d.TryGetProperty("status", out var statusEl) && statusEl.ValueKind == JsonValueKind.String)
        {
            var status = statusEl.GetString() ?? "";
            if (!status.Equals("published", StringComparison.OrdinalIgnoreCase)
                && !status.Equals("active",    StringComparison.OrdinalIgnoreCase))
                return null;
        }

        var jobTitle = d.TryGetProperty("title", out var t) ? t.GetString() ?? "Untitled" : "Untitled";

        // Prefer careers_url, fall back to careers_apply_url
        string? jobLink = null;
        if (d.TryGetProperty("careers_url", out var cu) && cu.ValueKind == JsonValueKind.String)
            jobLink = cu.GetString();
        if (string.IsNullOrWhiteSpace(jobLink)
            && d.TryGetProperty("careers_apply_url", out var au) && au.ValueKind == JsonValueKind.String)
            jobLink = au.GetString();

        // ── Location ──────────────────────────────────────────────────────────
        string? city = null, state = null, country = null;
        if (d.TryGetProperty("city",    out var c) && c.ValueKind == JsonValueKind.String)        city    = c.GetString();
        if (d.TryGetProperty("state",   out var s) && s.ValueKind == JsonValueKind.String)        state   = s.GetString();
        if (d.TryGetProperty("country_code", out var cc) && cc.ValueKind == JsonValueKind.String) country = cc.GetString();
        if (string.IsNullOrEmpty(country)
            && d.TryGetProperty("country", out var co) && co.ValueKind == JsonValueKind.String)
            country = co.GetString();

        bool remote = d.TryGetProperty("remote", out var rem) && rem.ValueKind == JsonValueKind.True;

        // US-only filter — strict gate (must have positive US signal)
        if (!UsLocationHelper.IsUs(country, state)) return null;
        country = "US";

        var workType    = remote ? "Remote" : "OnSite";
        var jobWorkMode = workType;

        // ── Employment shape (Recruitee's killer feature) ─────────────────────
        // employment_type_code values: permanent | temporary | contract | contractor
        //                               | internship | intern | freelance | volunteer
        bool isContract = false, isInternship = false, isFreelance = false;
        string contractType = "FullTime";

        if (d.TryGetProperty("employment_type_code", out var etEl) && etEl.ValueKind == JsonValueKind.String)
        {
            var et = etEl.GetString()?.ToLowerInvariant() ?? "";
            switch (et)
            {
                case "permanent":
                    contractType = "FullTime";
                    break;
                case "temporary":
                    contractType = "Temporary";  isContract = true;
                    break;
                case "contract":
                case "contractor":
                    contractType = "Contract";   isContract = true;
                    break;
                case "internship":
                case "intern":
                    contractType = "Internship"; isInternship = true;
                    break;
                case "freelance":
                    contractType = "Contract";   isContract = true; isFreelance = true;
                    break;
                case "volunteer":
                    return null;  // skip volunteer postings
                default:
                    contractType = "FullTime";
                    break;
            }
        }

        // Also honor explicit work_schedule_code if present
        if (d.TryGetProperty("work_schedule_code", out var wsEl) && wsEl.ValueKind == JsonValueKind.String)
        {
            var ws = wsEl.GetString() ?? "";
            if (ws.Contains("part", StringComparison.OrdinalIgnoreCase)
                && contractType == "FullTime")
                contractType = "PartTime";
        }

        // ── Description ───────────────────────────────────────────────────────
        string? desc = null;
        if (d.TryGetProperty("description", out var de) && de.ValueKind == JsonValueKind.String)
            desc = de.GetString();
        else if (d.TryGetProperty("requirements", out var re) && re.ValueKind == JsonValueKind.String)
            desc = re.GetString();

        // ── H1B keyword fallback + per-job negation ───────────────────────────
        var visaNegation = ContainsAny(desc,
            "do not sponsor", "does not sponsor", "no sponsorship", "unable to sponsor",
            "cannot sponsor", "will not sponsor", "no h-1b", "no h1b",
            "must be authorized to work", "must have work authorization",
            "authorized to work in the us", "authorized to work in the united states");
        var isH1BFinal  = !visaNegation && (isH1B ||
            (desc != null && (desc.Contains("h1b",          StringComparison.OrdinalIgnoreCase) ||
                              desc.Contains("h-1b",         StringComparison.OrdinalIgnoreCase) ||
                              desc.Contains("visa sponsor", StringComparison.OrdinalIgnoreCase))));
        var isOptCpt    = !visaNegation && ContainsAny(desc, " opt ", "opt/cpt", "stem opt", "opt extension", "f-1 visa", " cpt ");
        var isTnVisa    = !visaNegation && ContainsAny(desc, "tn visa", "tn-1", "tn-2", "usmca", "nafta visa");
        var isE3Visa    = !visaNegation && ContainsAny(desc, "e-3", "e3 visa", "e-3 visa");
        var isJ1Visa    = !visaNegation && ContainsAny(desc, "j-1", "j1 visa", "j-1 visa", "exchange visitor");
        var isGreenCard = !visaNegation && ContainsAny(desc, "green card", "gc sponsor", "perm filing", "eb-2", "eb-3", "labor certification");

        // ── Salary (rare on Recruitee but parseable when present) ─────────────
        decimal? salMin = null, salMax = null;
        string?  salCurrency = "USD", salType = null, salRangeText = null;
        if (d.TryGetProperty("salary", out var sal) && sal.ValueKind == JsonValueKind.Object)
        {
            if (sal.TryGetProperty("min", out var mn) && mn.ValueKind == JsonValueKind.Number) salMin = mn.GetDecimal();
            if (sal.TryGetProperty("max", out var mx) && mx.ValueKind == JsonValueKind.Number) salMax = mx.GetDecimal();
            if (sal.TryGetProperty("currency", out var cur) && cur.ValueKind == JsonValueKind.String)
                salCurrency = cur.GetString() ?? "USD";
            if (sal.TryGetProperty("period", out var per) && per.ValueKind == JsonValueKind.String)
            {
                var p = per.GetString()?.ToLowerInvariant() ?? "";
                salType = p switch
                {
                    "year"  or "yearly"  or "annual"  => "Annual",
                    "month" or "monthly"              => "Monthly",
                    "hour"  or "hourly"               => "Hourly",
                    _ => null
                };
            }
            if (salMin.HasValue && salMax.HasValue)
                salRangeText = salType == "Hourly"
                    ? $"${salMin:N0}–${salMax:N0}/hr"
                    : $"${salMin:N0}–${salMax:N0}/yr";
        }

        // ── Department / category as skills proxy ─────────────────────────────
        string? department = null;
        if (d.TryGetProperty("department", out var dpt) && dpt.ValueKind == JsonValueKind.String)
            department = dpt.GetString();
        string? category = null;
        if (d.TryGetProperty("category", out var cat) && cat.ValueKind == JsonValueKind.String)
            category = cat.GetString();

        // ── Post date ─────────────────────────────────────────────────────────
        DateTime? postDate = null;
        if (d.TryGetProperty("published_at", out var pa) && pa.ValueKind == JsonValueKind.String
            && DateTime.TryParse(pa.GetString(), out var pdt))
            postDate = pdt.ToUniversalTime();
        else if (d.TryGetProperty("created_at", out var ca) && ca.ValueKind == JsonValueKind.String
            && DateTime.TryParse(ca.GetString(), out var cdt))
            postDate = cdt.ToUniversalTime();

        var skills = (department, category) switch
        {
            (not null, not null) when department != category => new[] { department, category! },
            (not null, _)                                     => new[] { department! },
            (_,        not null)                              => new[] { category! },
            _ => null
        };

        var job = new ApiRawJob
        {
            PublicId        = Guid.NewGuid().ToString("N"),
            Source          = "Recruitee",
            SourceId        = sourceId,
            FetchRunId      = fetchRunId,
            JobTitle        = jobTitle,
            JobLink         = jobLink,
            JobDescription  = desc,
            City            = city,
            State           = state,
            Country         = country,
            PostDate        = postDate,
            HoursBackPosted = postDate.HasValue ? (int)(DateTime.UtcNow - postDate.Value).TotalHours : null,
            SalaryMin       = salMin,
            SalaryMax       = salMax,
            SalaryType      = salType,
            SalaryRangeText = salRangeText,
            SalaryCurrency  = salCurrency,
            WorkType        = workType,
            JobWorkMode     = jobWorkMode,
            ContractType    = contractType,
            Industry        = token.Industry ?? department ?? category,
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
            IsFreelanceJob  = isFreelance,
            IsPrimeVendor   = false,
            IsStaffing      = false,
            IsStartupJob    = false,
            IsNonProfitJob  = false,
            IsUniversityJob = isInternship,
            Status          = true,
            CreatedOn       = DateTime.UtcNow,
            UpdatedOn       = DateTime.UtcNow
        };

        // ── Validation gate — drop rows that don't satisfy required-field rules
        return JobValidationGate.AcceptOrNull(job, _logger, "[Recruitee]");
    }
}
