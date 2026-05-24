// CareerPandaBL/Background/Handlers/JobicyJobsJobHandler.cs
// SOURCE : Jobicy public API — https://jobicy.com/api/v2/remote-jobs
// AUTH   : Free, no API key required.
// TERMS  : Attribution appreciated; link back to jobicy.com in UI.
//
// STRATEGY
// On startup fetch all industries and locations dynamically from Jobicy API.
// Loop: for each geo × each industry → fetch count=100 (Jobicy max per call, no pagination).
// Duplicates across geo×industry combos are deduped by SourceId at DB upsert time.
// Every job is remote-only (WorkType/JobWorkMode = "Remote" always).
using System.Net.Http.Json;
using System.Text.Json;
using CareerPanda.DataAccess.DA;
using CareerPanda.DataAccess.Entities.Api;
using CareerPanda.Framework.Cache;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CareerPanda.BL.Background.Handlers;

public class JobicyJobsJobHandler : JobFetchBaseHandler
{
    public override string JobType        => "JobicyJobs";
    protected override string JobCategory => "JobicyJobs";
    protected override string ApiSource   => "Jobicy";

    protected override int InterPageDelayMs => 1500;

    private readonly IHttpClientFactory _http;

    public JobicyJobsJobHandler(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        ICacheService cacheService,
        ILogger<JobicyJobsJobHandler> logger)
        : base(scopeFactory, cacheService, logger)
    {
        _http = httpClientFactory;
    }

    public override async Task ExecuteAsync(
        JobWorkRequest request,
        IJobProgressReporter progress,
        CancellationToken cancellationToken)
    {
        var input    = ParseInput(request.InputPayload);
        var sponsors = await LoadSponsorsAsync(cancellationToken);
        var client   = _http.CreateClient("Jobicy");

        // ── Load industries and geos once for the entire job ─────────────────
        await progress.ReportProgressAsync(2, "Loading Jobicy industries and locations...");

        var industries = await FetchMetaAsync(client, "industries", "industrySlug", cancellationToken);

        // TODO: expand to all geos once validated — fetch with: FetchMetaAsync(client, "locations", "geoSlug", cancellationToken)
        var geos = new List<string> { "usa" };

        Logger.LogInformation("[Jobicy] Loaded {I} industries, geo restricted to: {G}", industries.Count, string.Join(", ", geos));

        if (industries.Count == 0)
        {
            Logger.LogError("[Jobicy] Failed to load industries — aborting");
            throw new InvalidOperationException("Jobicy meta fetch returned empty industries.");
        }

        int totalCombos = industries.Count * geos.Count;

        using var scope = _scopeFactory.CreateScope();
        var fetchDa = scope.ServiceProvider.GetRequiredService<IJobFetchDA>();

        var run = new ApiJobFetchRun
        {
            Id               = request.JobId,
            BackgroundTaskId = request.JobId,
            JobCategory      = JobCategory,
            ApiSource        = ApiSource,
            Status           = "Running",
            StartedAt        = DateTime.UtcNow,
            HoursBack        = input.HoursBack,
            MaxPages         = totalCombos,
            LocationFilter   = "Remote (Global)",
            CreatedById      = request.UserId
        };
        await fetchDa.CreateFetchRunAsync(run);

        Logger.LogInformation("[Jobicy] Starting — {G} geos × {I} industries = {T} combos",
            geos.Count, industries.Count, totalCombos);

        int totalFetched = 0, totalInserted = 0, totalUpdated = 0,
            totalSkipped = 0, totalErrors = 0, combosDone = 0;

        var cutoff = DateTime.UtcNow.AddHours(-input.HoursBack);

        try
        {
            foreach (var geo in geos)
            {
                foreach (var industry in industries)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var url = $"https://jobicy.com/api/v2/remote-jobs?count=100" +
                              $"&geo={Uri.EscapeDataString(geo)}" +
                              $"&industry={Uri.EscapeDataString(industry)}";

                    Logger.LogInformation("[Jobicy] [{Done}/{Total}] geo='{G}' industry='{I}'",
                        combosDone + 1, totalCombos, geo, industry);

                    List<ApiRawJob> jobs = [];
                    try
                    {
                        using var req = new HttpRequestMessage(HttpMethod.Get, url);
                        var res = await client.SendAsync(req, cancellationToken);
                        res.EnsureSuccessStatusCode();

                        var json = await res.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);

                        if (json.TryGetProperty("jobs", out var jobsArray) &&
                            jobsArray.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var item in jobsArray.EnumerateArray())
                            {
                                try
                                {
                                    var job = MapJob(item, run.Id, sponsors);
                                    if (job.PostDate.HasValue && job.PostDate.Value < cutoff) continue;
                                    jobs.Add(job);
                                }
                                catch (Exception ex) { Logger.LogWarning(ex, "[Jobicy] Map failed"); }
                            }
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        Logger.LogError(ex, "[Jobicy] Failed geo='{G}' industry='{I}'", geo, industry);
                        totalErrors++;
                    }

                    combosDone++;

                    if (jobs.Count > 0)
                    {
                        totalFetched += jobs.Count;
                        var (ins, upd, err) = await fetchDa.BulkUpsertRawJobsAsync(jobs, cancellationToken);
                        totalInserted += ins;
                        totalUpdated  += upd;
                        totalErrors   += err;
                        totalSkipped  += jobs.Count - ins - upd - err;

                        await fetchDa.UpdateFetchRunStatsAsync(
                            run.Id, totalFetched, totalInserted, totalUpdated,
                            totalSkipped, totalErrors, combosDone);
                    }

                    int pct = Math.Min(95, (int)((double)combosDone / totalCombos * 95));
                    await progress.ReportProgressAsync(pct,
                        $"[{combosDone}/{totalCombos}] geo='{geo}' industry='{industry}' — Inserted: {totalInserted}");

                    await Task.Delay(InterPageDelayMs, cancellationToken);
                }
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

        Logger.LogInformation(
            "[Jobicy] Done — Combos={C} Fetched={F} Inserted={I} Updated={U} Skipped={S} Errors={E}",
            combosDone, totalFetched, totalInserted, totalUpdated, totalSkipped, totalErrors);

        await progress.ReportProgressAsync(100,
            $"Done — {combosDone} combos, Fetched: {totalFetched}, Inserted: {totalInserted}, Updated: {totalUpdated}");
    }

    // ── Fetch meta list (industries or locations) from Jobicy ─────────────────

    private async Task<List<string>> FetchMetaAsync(
        HttpClient client, string getParam, string slugField, CancellationToken ct)
    {
        try
        {
            var url = $"https://jobicy.com/api/v2/remote-jobs?get={getParam}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            var res = await client.SendAsync(req, ct);
            res.EnsureSuccessStatusCode();

            var json  = await res.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            var key   = getParam == "industries" ? "industries" : "locations";

            if (!json.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array)
                return [];

            return arr.EnumerateArray()
                .Where(x => x.TryGetProperty(slugField, out _))
                .Select(x => x.GetProperty(slugField).GetString()!)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[Jobicy] Failed to fetch meta '{Param}'", getParam);
            return [];
        }
    }

    // ── Job mapping ───────────────────────────────────────────────────────────

    private ApiRawJob MapJob(JsonElement j, string fetchRunId, HashSet<string> sponsors)
    {
        var sourceId    = j.TryGetProperty("id",             out var id)  ? id.GetRawText()  : null;
        var title       = j.TryGetProperty("jobTitle",       out var t)   ? t.GetString()    : "Untitled";
        var companyName = j.TryGetProperty("companyName",    out var co)  ? co.GetString()   : null;
        var desc        = j.TryGetProperty("jobDescription", out var d)   ? d.GetString()    : null;
        var excerpt     = j.TryGetProperty("jobExcerpt",     out var ex)  ? ex.GetString()   : null;
        var applyUrl    = j.TryGetProperty("url",            out var u)   ? u.GetString()    : null;
        var logoUrl     = j.TryGetProperty("companyLogo",    out var lg)  ? lg.GetString()   : null;
        var geoRaw      = j.TryGetProperty("jobGeo",         out var geo) ? geo.GetString()  : null;
        var pubDateStr  = j.TryGetProperty("pubDate",        out var pd)  ? pd.GetString()   : null;
        var jobLevel    = j.TryGetProperty("jobLevel",       out var jl)  ? jl.GetString()   : null;
        var salaryPeriod = j.TryGetProperty("salaryPeriod",  out var sp)  ? sp.GetString()   : null;

        var fullText = string.Concat(desc, " ", excerpt);

        DateTime? postDate = null;
        if (pubDateStr != null && DateTime.TryParse(pubDateStr, out var dt))
            postDate = DateTime.SpecifyKind(dt, DateTimeKind.Utc);

        decimal? salMin = null, salMax = null;
        if (j.TryGetProperty("salaryMin", out var sn) && sn.ValueKind == JsonValueKind.Number && sn.GetDecimal() > 0) salMin = sn.GetDecimal();
        if (j.TryGetProperty("salaryMax", out var sx) && sx.ValueKind == JsonValueKind.Number && sx.GetDecimal() > 0) salMax = sx.GetDecimal();
        var salaryCurrency = j.TryGetProperty("salaryCurrency", out var sc) ? sc.GetString() ?? "USD" : "USD";

        string? contractType = null;
        if (j.TryGetProperty("jobType", out var jt) && jt.ValueKind == JsonValueKind.Array)
        {
            var types = jt.EnumerateArray().Select(x => x.GetString()).Where(x => x != null).ToArray();
            contractType = types.Length > 0 ? string.Join(", ", types) : null;
        }

        string[]? skills = null;
        string? industryText = null;
        if (j.TryGetProperty("jobIndustry", out var ji) && ji.ValueKind == JsonValueKind.Array)
        {
            skills = ji.EnumerateArray().Select(x => x.GetString()!).Where(x => x != null).ToArray();
            industryText = skills.Length > 0 ? string.Join(", ", skills) : null;
        }

        string? country = null;
        if (geoRaw != null)
        {
            var g = geoRaw.ToUpperInvariant();
            if (g is "USA" or "US" or "UNITED STATES") country = "US";
            else if (g is "WORLDWIDE" or "ANYWHERE" or "REMOTE") country = null;
            else country = geoRaw;
        }

        var isH1B         = ContainsAny(fullText, "h1b", "h-1b", "visa sponsor", "will sponsor") ||
                            (companyName != null && sponsors.Contains(companyName));
        const bool isContract    = false; // Jobicy is a remote-only board — not a contract/staffing board
        const bool isC2C         = false;
        const bool isW2          = false;
        const bool isFreelance   = false;
        const bool isPrimeVendor = false;
        const bool isStaffing    = false;
        const bool isUniversity  = false;
        const bool isStartup     = false;
        const bool isNonProfit   = false;

        return new ApiRawJob
        {
            PublicId          = Guid.NewGuid().ToString("N"),
            Source            = "Jobicy",
            SourceId          = sourceId,
            FetchRunId        = fetchRunId,
            JobTitle          = title ?? "Untitled",
            JobLink           = applyUrl,
            JobDescription    = desc ?? excerpt,
            City              = null,
            State             = null,
            Country           = country,
            PostDate          = postDate,
            HoursBackPosted   = ParseHoursBack(postDate),
            SalaryMin         = salMin,
            SalaryMax         = salMax,
            SalaryRangeText   = salMin.HasValue && salMax.HasValue ? $"${salMin:N0}–${salMax:N0}/yr" : null,
            SalaryCurrency    = salaryCurrency,
            SalaryType        = salaryPeriod,
            WorkType          = "Remote",
            JobWorkMode       = "Remote",
            ContractType      = contractType,
            JobLevel          = jobLevel,
            Industry          = industryText,
            CompanyName       = companyName,
            CompanyLogoUrl    = logoUrl,
            Skills            = skills,
            IsH1BSponsored    = isH1B,
            IsSponsored       = isH1B,
            IsContractJob     = isContract,
            IsC2C             = isC2C,
            IsW2              = isW2,
            IsFreelanceJob    = isFreelance,
            IsPrimeVendor     = isPrimeVendor,
            IsStaffing        = isStaffing,
            IsUniversityJob   = isUniversity,
            IsStartupJob      = isStartup,
            IsNonProfitJob    = isNonProfit,
            Status            = true,
            CreatedOn         = DateTime.UtcNow,
            UpdatedOn         = DateTime.UtcNow
        };
    }

    // Required by base class — not used (ExecuteAsync fully overridden)
    protected override Task<List<ApiRawJob>> FetchPageAsync(
        int page, JobFetchInput input, string fetchRunId, CancellationToken ct) =>
        Task.FromResult(new List<ApiRawJob>());
}
