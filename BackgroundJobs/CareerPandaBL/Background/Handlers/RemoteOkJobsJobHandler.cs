// CareerPandaBL/Background/Handlers/RemoteOkJobsJobHandler.cs
// SOURCE : RemoteOK public API — https://remoteok.com/api
// AUTH   : Free, no API key. Must not use "bot"/"google" in User-Agent.
// TERMS  : Must link back to RemoteOK with rel="follow" in UI.
//
// STRATEGY
// RemoteOK has no pagination — one call per tag returns ~25 recent remote jobs.
// We loop through all RemoteOK tags to maximize coverage.
// Every job is remote-only (IsRemote=true always).
// All flags (H1B, Contract, C2C, W2, Startup, NonProfit, etc.) evaluated at insert time.
using System.Net.Http.Json;
using System.Text.Json;
using CareerPanda.DataAccess.DA;
using CareerPanda.DataAccess.Entities.Api;
using CareerPanda.Framework.Cache;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CareerPanda.BL.Background.Handlers;

public class RemoteOkJobsJobHandler : JobFetchBaseHandler
{
    public override string JobType        => "RemoteOkJobs";
    protected override string JobCategory => "RemoteOkJobs";
    protected override string ApiSource   => "RemoteOK";

    protected override int InterPageDelayMs => 1000; // RemoteOK is a small site — be polite

    // RemoteOK tag slugs — maps to https://remoteok.com/api?tag={slug}
    private static readonly string[] RemoteOkTags =
    [
        // Software
        "software-engineer", "developer", "backend", "frontend", "full-stack",
        "mobile", "ios", "android", "devops", "cloud", "sre", "infra",
        "embedded", "game",
        // Data & AI
        "data-science", "data-engineer", "machine-learning", "ai", "nlp",
        "analytics", "database",
        // Security
        "security", "cybersecurity",
        // Product & Design
        "product", "ux", "design", "ui",
        // QA
        "qa", "testing",
        // Business & Ops
        "project-management", "operations", "scrum",
        "business-development", "strategy",
        // Finance
        "finance", "accounting",
        // Marketing & Sales
        "marketing", "growth", "seo", "sales", "content",
        // HR & Legal
        "hr", "recruiting", "legal",
        // Support
        "customer-support", "technical-support",
        // Healthcare & Science
        "healthcare", "medical",
        // Creative
        "video", "writing", "copywriting",
        // Executive
        "exec", "cto", "cfo",
        // Broad
        "non-tech", "senior", "junior", "internship"
    ];

    private const string SponsorsCacheKey = "h1b:sponsors:names";
    private static readonly TimeSpan SponsorsCacheTtl = TimeSpan.FromHours(24);

    private static readonly string[] LegalSuffixes =
        ["INCORPORATED", "CORPORATION", "LIMITED", "INC", "LLC", "CORP", "LTD", "CO", "LP", "LLP", "PLLC", "PC"];

    private readonly IHttpClientFactory _http;
    private readonly ICacheService _cache;
    private readonly IServiceScopeFactory _scopeFactory;

    public RemoteOkJobsJobHandler(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        ICacheService cacheService,
        ILogger<RemoteOkJobsJobHandler> logger)
        : base(scopeFactory, logger)
    {
        _scopeFactory = scopeFactory;
        _http         = httpClientFactory;
        _cache        = cacheService;
    }

    // ── Override: loop all RemoteOK tags ─────────────────────────────────────

    public override async Task ExecuteAsync(
        JobWorkRequest request,
        IJobProgressReporter progress,
        CancellationToken cancellationToken)
    {
        var input    = ParseInput(request.InputPayload);
        var sponsors = await LoadSponsorsAsync(cancellationToken);

        // If specific tag passed via SearchQuery use it; otherwise all tags
        var tags = input.SearchQuery != null
            ? [input.SearchQuery]
            : RemoteOkTags;

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
            MaxPages         = tags.Length,
            LocationFilter   = "Remote (Global)",
            CreatedById      = request.UserId
        };
        await fetchDa.CreateFetchRunAsync(run);

        Logger.LogInformation("[RemoteOK] Starting — {T} tags to fetch", tags.Length);

        int totalFetched = 0, totalInserted = 0, totalUpdated = 0,
            totalSkipped = 0, totalErrors = 0, pagesFetched = 0;

        try
        {
            for (int i = 0; i < tags.Length; i++)
            {
                if (cancellationToken.IsCancellationRequested) break;

                var tag = tags[i];
                Logger.LogInformation("[RemoteOK] Tag {I}/{T}: '{Tag}'", i + 1, tags.Length, tag);

                List<ApiRawJob> jobs;
                try
                {
                    jobs = await FetchTagAsync(tag, input.HoursBack, run.Id, sponsors, cancellationToken);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "[RemoteOK] Fetch failed for tag '{Tag}'", tag);
                    totalErrors++;
                    continue;
                }

                if (jobs.Count == 0)
                {
                    Logger.LogInformation("[RemoteOK] No results for tag '{Tag}'", tag);
                    await Task.Delay(InterPageDelayMs, cancellationToken);
                    continue;
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

                int pct = (int)((double)(i + 1) / tags.Length * 90);
                await progress.ReportProgressAsync(pct,
                    $"[{i + 1}/{tags.Length}] '{tag}' — Inserted: {totalInserted}, Updated: {totalUpdated}");

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
            "[RemoteOK] Done — Tags={T} Fetched={F} Inserted={I} Updated={U} Errors={E}",
            tags.Length, totalFetched, totalInserted, totalUpdated, totalErrors);

        await progress.ReportProgressAsync(100,
            $"Done — {tags.Length} tags, Inserted: {totalInserted}, Updated: {totalUpdated}");
    }

    // ── RemoteOK API fetch ────────────────────────────────────────────────────

    private async Task<List<ApiRawJob>> FetchTagAsync(
        string tag, int hoursBack, string fetchRunId, HashSet<string> sponsors, CancellationToken ct)
    {
        var client = _http.CreateClient("RemoteOK");
        var url    = $"https://remoteok.com/api?tag={Uri.EscapeDataString(tag)}";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        var res = await client.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();

        var json = await res.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);

        // First element is metadata, skip it
        var cutoff = DateTime.UtcNow.AddHours(-hoursBack);
        var jobs   = new List<ApiRawJob>();

        foreach (var item in json.EnumerateArray())
        {
            // Skip the metadata object (has no "position" field)
            if (!item.TryGetProperty("position", out _)) continue;

            try
            {
                var job = MapJob(item, fetchRunId, sponsors);

                // Filter by HoursBack if post date available
                if (job.PostDate.HasValue && job.PostDate.Value < cutoff) continue;

                jobs.Add(job);
            }
            catch (Exception ex) { Logger.LogWarning(ex, "[RemoteOK] Map failed for item"); }
        }

        return jobs;
    }

    private ApiRawJob MapJob(JsonElement j, string fetchRunId, HashSet<string> sponsors)
    {
        var title       = j.TryGetProperty("position",    out var t)   ? t.GetString()   : "Untitled";
        var companyName = j.TryGetProperty("company",     out var co)  ? co.GetString()  : null;
        var desc        = j.TryGetProperty("description", out var d)   ? d.GetString()   : null;
        var applyUrl    = j.TryGetProperty("apply_url",   out var au)  ? au.GetString()  : null;
        var jobUrl      = j.TryGetProperty("url",         out var u)   ? u.GetString()   : null;
        var sourceId    = j.TryGetProperty("id",          out var id)  ? id.GetString()  : null;
        var logoUrl     = j.TryGetProperty("company_logo",out var lg)  ? lg.GetString()  : null;
        var location    = j.TryGetProperty("location",    out var lo)  ? lo.GetString()  : "Remote";

        // Parse post date from epoch
        DateTime? postDate = null;
        if (j.TryGetProperty("epoch", out var ep) && ep.ValueKind == JsonValueKind.Number)
            postDate = DateTimeOffset.FromUnixTimeSeconds(ep.GetInt64()).UtcDateTime;

        // Parse salary
        decimal? salMin = null, salMax = null;
        if (j.TryGetProperty("salary_min", out var sn) && sn.ValueKind == JsonValueKind.Number && sn.GetDecimal() > 0) salMin = sn.GetDecimal();
        if (j.TryGetProperty("salary_max", out var sx) && sx.ValueKind == JsonValueKind.Number && sx.GetDecimal() > 0) salMax = sx.GetDecimal();

        // Parse tags array
        string[]? tags = null;
        if (j.TryGetProperty("tags", out var tg) && tg.ValueKind == JsonValueKind.Array)
            tags = tg.EnumerateArray().Select(x => x.GetString()!).Where(x => x != null).ToArray();

        // ── Flag detection ────────────────────────────────────────────────────
        var isH1B         = ContainsAny(desc, "h1b", "h-1b", "visa sponsor", "will sponsor") ||
                            (companyName != null && sponsors.Contains(companyName));
        var isContract    = ContainsAny(desc, "contract", "contractor") ||
                            (tags?.Any(x => x.Contains("contract", StringComparison.OrdinalIgnoreCase)) == true);
        var isC2C         = ContainsAny(desc, "c2c", "corp to corp", "corp-to-corp");
        var isW2          = ContainsAny(desc, "w2", "w-2");
        var isFreelance   = ContainsAny(desc, "1099", "freelance", "independent contractor");
        var isPrimeVendor = ContainsAny(desc, "prime vendor", "direct client", "end client");
        var isStaffing    = ContainsAny(desc, "staffing", "recruiting firm") ||
                            ContainsAny(companyName, "staffing", "consulting", "solutions");
        var isUniversity  = ContainsAny(companyName, "university", "college", "institute", "academia") ||
                            ContainsAny(desc, "university", "academic", "faculty");
        var isStartup     = ContainsAny(desc, "startup", "start-up", "series a", "series b", "seed", "venture") ||
                            ContainsAny(companyName, "startup", "start-up");
        var isNonProfit   = ContainsAny(desc, "nonprofit", "non-profit", "501(c)", "501c3", "ngo") ||
                            ContainsAny(companyName, "foundation", "nonprofit", "non-profit");

        return new ApiRawJob
        {
            PublicId          = Guid.NewGuid().ToString("N"),
            Source            = "RemoteOK",
            SourceId          = sourceId,
            FetchRunId        = fetchRunId,
            JobTitle          = title ?? "Untitled",
            JobLink           = applyUrl ?? jobUrl,
            JobDescription    = desc,
            City              = null,
            State             = null,
            Country           = location.Contains("USA", StringComparison.OrdinalIgnoreCase) ||
                                 location.Contains("US only", StringComparison.OrdinalIgnoreCase) ? "US" : null,
            PostDate          = postDate,
            HoursBackPosted   = ParseHoursBack(postDate),
            SalaryMin         = salMin,
            SalaryMax         = salMax,
            SalaryRangeText   = salMin.HasValue && salMax.HasValue ? $"${salMin:N0}–${salMax:N0}/yr" : null,
            SalaryCurrency    = "USD",
            WorkType          = "Remote",
            JobWorkMode       = "Remote",
            CompanyName       = companyName,
            CompanyLogoUrl    = logoUrl,
            Skills            = tags,
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

    // ── H1B sponsor list ──────────────────────────────────────────────────────

    private async Task<HashSet<string>> LoadSponsorsAsync(CancellationToken ct)
    {
        var cached = await _cache.GetAsync<List<string>>(SponsorsCacheKey, ct);
        if (cached is { Count: > 0 }) return BuildSponsorSet(cached);

        using var scope = _scopeFactory.CreateScope();
        var da    = scope.ServiceProvider.GetRequiredService<IJobFetchDA>();
        var names = await da.GetH1BSponsorNamesAsync(ct);
        await _cache.SetAsync(SponsorsCacheKey, names, SponsorsCacheTtl, ct);
        Logger.LogInformation("[RemoteOK] Loaded {Count} H1B sponsors into cache", names.Count);
        return BuildSponsorSet(names);
    }

    private static HashSet<string> BuildSponsorSet(List<string> names)
    {
        var set = new HashSet<string>(names.Count * 2, StringComparer.OrdinalIgnoreCase);
        foreach (var name in names) { set.Add(name); set.Add(NormalizeCompanyName(name)); }
        return set;
    }

    private static string NormalizeCompanyName(string name)
    {
        var upper   = name.ToUpperInvariant();
        var stripped = System.Text.RegularExpressions.Regex.Replace(upper, @"[^\w\s]", " ");
        var parts   = stripped.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int count   = parts.Length;
        while (count > 1 && LegalSuffixes.Contains(parts[count - 1])) count--;
        return string.Join(' ', parts, 0, count);
    }

    // Required by base class — not used (ExecuteAsync fully overridden)
    protected override Task<List<ApiRawJob>> FetchPageAsync(
        int page, JobFetchInput input, string fetchRunId, CancellationToken ct) =>
        FetchTagAsync(input.SearchQuery ?? "software-engineer", input.HoursBack, fetchRunId,
            LoadSponsorsAsync(ct).GetAwaiter().GetResult(), ct);
}
