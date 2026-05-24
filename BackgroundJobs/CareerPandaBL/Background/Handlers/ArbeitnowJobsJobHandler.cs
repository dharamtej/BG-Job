// CareerPandaBL/Background/Handlers/ArbeitnowJobsJobHandler.cs
// SOURCE : Arbeitnow public API — https://arbeitnow.com/api/job-board-api
// AUTH   : Free, no API key required.
// TERMS  : Attribution appreciated; link back to arbeitnow.com in UI.
//
// STRATEGY
// Arbeitnow supports pagination (?page=N, ~10-20 jobs/page).
// We paginate until the HoursBack cutoff is reached or a page returns no jobs.
// Key advantage: explicit visa_sponsorship and remote boolean fields per job.
// All flags (H1B, Contract, C2C, W2, Startup, NonProfit, etc.) evaluated at insert time.
using System.Net.Http.Json;
using System.Text.Json;
using CareerPanda.DataAccess.DA;
using CareerPanda.DataAccess.Entities.Api;
using CareerPanda.Framework.Cache;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CareerPanda.BL.Background.Handlers;

public class ArbeitnowJobsJobHandler : JobFetchBaseHandler
{
    public override string JobType        => "ArbeitnowJobs";
    protected override string JobCategory => "ArbeitnowJobs";
    protected override string ApiSource   => "Arbeitnow";

    protected override int InterPageDelayMs => 1500;

    private readonly IHttpClientFactory _http;

    public ArbeitnowJobsJobHandler(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        ICacheService cacheService,
        ILogger<ArbeitnowJobsJobHandler> logger)
        : base(scopeFactory, cacheService, logger)
    {
        _http = httpClientFactory;
    }

    // ── Override: paginate until HoursBack cutoff ────────────────────────────

    public override async Task ExecuteAsync(
        JobWorkRequest request,
        IJobProgressReporter progress,
        CancellationToken cancellationToken)
    {
        var input    = ParseInput(request.InputPayload);
        var sponsors = await LoadSponsorsAsync(cancellationToken);
        var cutoff   = DateTime.UtcNow.AddHours(-input.HoursBack);
        var maxPages = input.MaxPages > 0 ? input.MaxPages : 50;

        using var scope   = _scopeFactory.CreateScope();
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
            MaxPages         = maxPages,
            LocationFilter   = "Global / Remote",
            CreatedById      = request.UserId
        };
        await fetchDa.CreateFetchRunAsync(run);

        Logger.LogInformation("[Arbeitnow] Starting — up to {Max} pages, cutoff {Cutoff:u}", maxPages, cutoff);

        int totalFetched = 0, totalInserted = 0, totalUpdated = 0,
            totalSkipped = 0, totalErrors = 0, pagesFetched = 0;

        try
        {
            for (int page = 1; page <= maxPages; page++)
            {
                if (cancellationToken.IsCancellationRequested) break;

                Logger.LogInformation("[Arbeitnow] Page {P}/{Max}", page, maxPages);

                List<ApiRawJob> jobs;
                bool hitCutoff;
                try
                {
                    (jobs, hitCutoff) = await FetchPageAsync(page, input.HoursBack, run.Id, sponsors, cutoff, cancellationToken);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "[Arbeitnow] Fetch failed for page {P}", page);
                    totalErrors++;
                    break; // network error — stop rather than skipping pages
                }

                if (jobs.Count == 0)
                {
                    Logger.LogInformation("[Arbeitnow] Empty page {P} — stopping", page);
                    break;
                }

                pagesFetched++;
                totalFetched += jobs.Count;

                var (ins, upd, err) = await fetchDa.BulkUpsertRawJobsAsync(jobs, cancellationToken);
                totalInserted += ins;
                totalUpdated  += upd;
                totalErrors   += err;
                totalSkipped  += jobs.Count - ins - upd - err;

                await fetchDa.UpdateFetchRunStatsAsync(
                    run.Id, totalFetched, totalInserted, totalUpdated,
                    totalSkipped, totalErrors, pagesFetched);

                int pct = Math.Min(90, (int)((double)page / maxPages * 90));
                await progress.ReportProgressAsync(pct,
                    $"Page {page} — Inserted: {totalInserted}, Updated: {totalUpdated}");

                if (hitCutoff)
                {
                    Logger.LogInformation("[Arbeitnow] Reached HoursBack cutoff at page {P}", page);
                    break;
                }

                await Task.Delay(InterPageDelayMs, cancellationToken);
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
            "[Arbeitnow] Done — Pages={P} Fetched={F} Inserted={I} Updated={U} Errors={E}",
            pagesFetched, totalFetched, totalInserted, totalUpdated, totalErrors);

        await progress.ReportProgressAsync(100,
            $"Done — {pagesFetched} pages, Inserted: {totalInserted}, Updated: {totalUpdated}");
    }

    // ── Arbeitnow API fetch ───────────────────────────────────────────────────

    private async Task<(List<ApiRawJob> jobs, bool hitCutoff)> FetchPageAsync(
        int page, int hoursBack, string fetchRunId, HashSet<string> sponsors,
        DateTime cutoff, CancellationToken ct)
    {
        var client = _http.CreateClient("Arbeitnow");
        var url    = $"https://arbeitnow.com/api/job-board-api?page={page}";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        var res = await client.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();

        var json = await res.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);

        if (!json.TryGetProperty("data", out var dataArray) ||
            dataArray.ValueKind != JsonValueKind.Array)
            return ([], false);

        var jobs      = new List<ApiRawJob>();
        bool hitCutoff = false;

        foreach (var item in dataArray.EnumerateArray())
        {
            try
            {
                // Parse created_at first to apply HoursBack filter
                DateTime? postDate = null;
                if (item.TryGetProperty("created_at", out var ca))
                {
                    if (ca.ValueKind == JsonValueKind.Number)
                        postDate = DateTimeOffset.FromUnixTimeSeconds(ca.GetInt64()).UtcDateTime;
                    else if (ca.ValueKind == JsonValueKind.String && DateTime.TryParse(ca.GetString(), out var dt))
                        postDate = DateTime.SpecifyKind(dt, DateTimeKind.Utc);
                }

                if (postDate.HasValue && postDate.Value < cutoff)
                {
                    hitCutoff = true;
                    continue;
                }

                jobs.Add(MapJob(item, fetchRunId, sponsors, postDate));
            }
            catch (Exception ex) { Logger.LogWarning(ex, "[Arbeitnow] Map failed for item"); }
        }

        return (jobs, hitCutoff);
    }

    private ApiRawJob MapJob(JsonElement j, string fetchRunId, HashSet<string> sponsors, DateTime? postDate)
    {
        var slug        = j.TryGetProperty("slug",         out var sl) ? sl.GetString()  : null;
        var title       = j.TryGetProperty("title",        out var t)  ? t.GetString()   : "Untitled";
        var companyName = j.TryGetProperty("company_name", out var co) ? co.GetString()  : null;
        var desc        = j.TryGetProperty("description",  out var d)  ? d.GetString()   : null;
        var applyUrl    = j.TryGetProperty("url",          out var u)  ? u.GetString()   : null;
        var location    = j.TryGetProperty("location",     out var lo) ? lo.GetString()  : null;
        var isRemote    = j.TryGetProperty("remote",       out var rm) && rm.ValueKind == JsonValueKind.True;
        var visaSponsor = j.TryGetProperty("visa_sponsorship", out var vs) && vs.ValueKind == JsonValueKind.True;

        // Parse tags array → skills
        string[]? skills = null;
        if (j.TryGetProperty("tags", out var tg) && tg.ValueKind == JsonValueKind.Array)
            skills = tg.EnumerateArray().Select(x => x.GetString()!).Where(x => x != null).ToArray();

        // Parse job_types array
        string[]? jobTypes = null;
        if (j.TryGetProperty("job_types", out var jt) && jt.ValueKind == JsonValueKind.Array)
            jobTypes = jt.EnumerateArray().Select(x => x.GetString()!).Where(x => x != null).ToArray();

        var jobTypesText = jobTypes != null ? string.Join(" ", jobTypes).ToLowerInvariant() : "";

        // ── Flag detection ────────────────────────────────────────────────────
        // visa_sponsorship field is authoritative for H1B — no keyword guessing needed
        var isH1B         = visaSponsor ||
                            ContainsAny(desc, "h1b", "h-1b", "visa sponsor", "will sponsor") ||
                            (companyName != null && sponsors.Contains(companyName));
        var isContract    = ContainsAny(desc, "contract", "contractor") ||
                            jobTypesText.Contains("contract");
        var isC2C         = ContainsAny(desc, "c2c", "corp to corp", "corp-to-corp");
        var isW2          = ContainsAny(desc, "w2", "w-2");
        var isFreelance   = ContainsAny(desc, "1099", "freelance", "independent contractor") ||
                            jobTypesText.Contains("freelance");
        var isPrimeVendor = ContainsAny(desc, "prime vendor", "direct client", "end client");
        var isStaffing    = ContainsAny(desc, "staffing", "recruiting firm") ||
                            ContainsAny(companyName, "staffing", "consulting", "solutions");
        var isUniversity  = ContainsAny(companyName, "university", "college", "institute", "academia") ||
                            ContainsAny(desc, "university", "academic", "faculty");
        var isStartup     = ContainsAny(desc, "startup", "start-up", "series a", "series b", "seed", "venture") ||
                            ContainsAny(companyName, "startup", "start-up");
        var isNonProfit   = ContainsAny(desc, "nonprofit", "non-profit", "501(c)", "501c3", "ngo") ||
                            ContainsAny(companyName, "foundation", "nonprofit", "non-profit");

        // Determine work mode
        var workMode = isRemote ? "Remote" : "On-site";
        var workType = isRemote ? "Remote" : (jobTypes?.FirstOrDefault() ?? "Full-time");

        // Parse location parts
        string? city = null, state = null, country = null;
        if (!string.IsNullOrWhiteSpace(location))
        {
            var parts = location.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length >= 2)
            {
                city    = parts[0];
                country = parts[^1];
                if (parts.Length >= 3) state = parts[1];
            }
            else
            {
                country = parts[0];
            }
        }

        return new ApiRawJob
        {
            PublicId          = Guid.NewGuid().ToString("N"),
            Source            = "Arbeitnow",
            SourceId          = slug,
            FetchRunId        = fetchRunId,
            JobTitle          = title ?? "Untitled",
            JobLink           = applyUrl,
            JobDescription    = desc,
            City              = city,
            State             = state,
            Country           = country,
            PostDate          = postDate,
            HoursBackPosted   = ParseHoursBack(postDate),
            SalaryMin         = null,
            SalaryMax         = null,
            SalaryRangeText   = null,
            SalaryCurrency    = null,
            WorkType          = workType,
            JobWorkMode       = workMode,
            CompanyName       = companyName,
            CompanyLogoUrl    = null,
            Skills            = skills,
            // ── All flags ─────────────────────────────────────────────────────
            IsH1BSponsored    = isH1B,
            IsSponsored       = isH1B || ContainsAny(desc, "sponsor", "visa"),
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
        FetchPageAsync(page, input.HoursBack, fetchRunId,
            LoadSponsorsAsync(ct).GetAwaiter().GetResult(),
            DateTime.UtcNow.AddHours(-input.HoursBack), ct)
        .ContinueWith(t => t.Result.jobs, ct);
}
