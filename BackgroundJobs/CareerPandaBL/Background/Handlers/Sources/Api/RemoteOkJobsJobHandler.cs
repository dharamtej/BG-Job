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

    private readonly IHttpClientFactory _http;

    public RemoteOkJobsJobHandler(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        ICacheService cacheService,
        ILogger<RemoteOkJobsJobHandler> logger)
        : base(scopeFactory, cacheService, logger)
    {
        _http = httpClientFactory;
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
            LocationFilter   = "Remote (USA)",
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
        var url    = $"https://remoteok.com/api?tag={Uri.EscapeDataString(tag)}&location=USA";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        var res = await client.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();

        var json = await res.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);

        var cutoff = DateTime.UtcNow.AddHours(-hoursBack);
        var jobs   = new List<ApiRawJob>();

        foreach (var item in json.EnumerateArray())
        {
            // Skip the metadata object (has no "position" field)
            if (!item.TryGetProperty("position", out _)) continue;

            try
            {
                // Post-filter: keep US location or empty location (remote = open to US applicants)
                var location = item.TryGetProperty("location", out var loc) ? loc.GetString() ?? "" : "";
                if (!IsUsOrRemoteLocation(location)) continue;

                var job = MapJob(item, fetchRunId, sponsors);

                if (job.PostDate.HasValue && job.PostDate.Value < cutoff) continue;

                jobs.Add(job);
            }
            catch (Exception ex) { Logger.LogWarning(ex, "[RemoteOK] Map failed for item"); }
        }

        return jobs;
    }

    private static readonly HashSet<string> UsStates = new(StringComparer.OrdinalIgnoreCase)
    {
        "AL","AK","AZ","AR","CA","CO","CT","DE","FL","GA","HI","ID","IL","IN","IA",
        "KS","KY","LA","ME","MD","MA","MI","MN","MS","MO","MT","NE","NV","NH","NJ",
        "NM","NY","NC","ND","OH","OK","OR","PA","RI","SC","SD","TN","TX","UT","VT",
        "VA","WA","WV","WI","WY","DC"
    };

    private static readonly string[] NonUsKeywords =
    [
        "CANADA", "UK", "UNITED KINGDOM", "GERMANY", "INDIA", "MEXICO",
        "AUSTRALIA", "FRANCE", "SPAIN", "NETHERLANDS", "BRAZIL", "ARGENTINA",
        "COLOMBIA", "PHILIPPINES", "PAKISTAN", "UKRAINE", "POLAND", "ROMANIA",
        "PORTUGAL", "ITALY", "SINGAPORE", "JAPAN", "CHINA", "NIGERIA", "KENYA"
    ];

    private static bool IsUsOrRemoteLocation(string location)
    {
        if (string.IsNullOrWhiteSpace(location)) return true;

        var loc = location.ToUpperInvariant();

        if (loc.Contains("USA") || loc.Contains("UNITED STATES") || loc.Contains("U.S.A")) return true;

        var parts = loc.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && UsStates.Contains(parts[^1])) return true;

        if (NonUsKeywords.Any(k => loc.Contains(k))) return false;

        return true;
    }

    private static (string? city, string? state, string? country) ParseUsLocation(string location)
    {
        if (string.IsNullOrWhiteSpace(location)) return (null, null, null);

        var parts = location.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length >= 2 && UsStates.Contains(parts[^1].ToUpperInvariant()))
            return (parts[0], parts[^1].ToUpperInvariant(), "US");

        if (location.Contains("USA", StringComparison.OrdinalIgnoreCase) ||
            location.Contains("United States", StringComparison.OrdinalIgnoreCase))
            return (null, null, "US");

        return (null, null, null);
    }

    private ApiRawJob MapJob(JsonElement j, string fetchRunId, HashSet<string> sponsors)
    {
        var title       = j.TryGetProperty("position",    out var t)   ? t.GetString()   : "Untitled";
        var companyName = j.TryGetProperty("company",     out var co)  ? co.GetString()  : null;
        var desc        = j.TryGetProperty("description", out var d)   ? d.GetString()   : null;
        var applyUrl    = j.TryGetProperty("apply_url",   out var au)  ? au.GetString()  : null;
        var jobUrl      = j.TryGetProperty("url",         out var u)   ? u.GetString()   : null;
        var sourceId    = j.TryGetProperty("id",          out var id)  ? id.GetString()  : null;
        var logoRaw     = j.TryGetProperty("company_logo",out var lg)  ? lg.GetString()  : null;
        var logoUrl     = string.IsNullOrWhiteSpace(logoRaw) ? null : logoRaw; // API returns "" when no logo
        var locationRaw = j.TryGetProperty("location",    out var lo)  ? lo.GetString() ?? "" : "";

        // Parse post date from epoch
        DateTime? postDate = null;
        if (j.TryGetProperty("epoch", out var ep) && ep.ValueKind == JsonValueKind.Number)
            postDate = DateTimeOffset.FromUnixTimeSeconds(ep.GetInt64()).UtcDateTime;

        // Parse salary
        decimal? salMin = null, salMax = null;
        if (j.TryGetProperty("salary_min", out var sn) && sn.ValueKind == JsonValueKind.Number && sn.GetDecimal() > 0) salMin = sn.GetDecimal();
        if (j.TryGetProperty("salary_max", out var sx) && sx.ValueKind == JsonValueKind.Number && sx.GetDecimal() > 0) salMax = sx.GetDecimal();

        // Parse tags array — RemoteOK uses tags for skills, role level, and job type
        string[]? tags = null;
        if (j.TryGetProperty("tags", out var tg) && tg.ValueKind == JsonValueKind.Array)
            tags = tg.EnumerateArray().Select(x => x.GetString()!).Where(x => x != null).ToArray();

        // Extract job level from tags
        string? jobLevel = null;
        if (tags != null)
        {
            if (tags.Any(x => x.Equals("senior", StringComparison.OrdinalIgnoreCase) || x.Equals("lead", StringComparison.OrdinalIgnoreCase)))
                jobLevel = "Senior";
            else if (tags.Any(x => x.Equals("junior", StringComparison.OrdinalIgnoreCase) || x.Equals("entry", StringComparison.OrdinalIgnoreCase)))
                jobLevel = "Junior";
            else if (tags.Any(x => x.Equals("internship", StringComparison.OrdinalIgnoreCase) || x.Equals("intern", StringComparison.OrdinalIgnoreCase)))
                jobLevel = "Internship";
            else if (tags.Any(x => x.Equals("exec", StringComparison.OrdinalIgnoreCase) || x.Equals("cto", StringComparison.OrdinalIgnoreCase) || x.Equals("cfo", StringComparison.OrdinalIgnoreCase)))
                jobLevel = "Executive";
        }

        // Extract contract type from tags
        string? contractType = null;
        if (tags != null)
        {
            if (tags.Any(x => x.Equals("contract", StringComparison.OrdinalIgnoreCase))) contractType = "Contract";
            else if (tags.Any(x => x.Equals("freelance", StringComparison.OrdinalIgnoreCase))) contractType = "Freelance";
            else if (tags.Any(x => x.Equals("part-time", StringComparison.OrdinalIgnoreCase))) contractType = "Part-time";
            else if (tags.Any(x => x.Equals("internship", StringComparison.OrdinalIgnoreCase))) contractType = "Internship";
        }

        // Parse city/state/country from location string
        var (city, state, country) = ParseUsLocation(locationRaw);

        // ── Flag detection ────────────────────────────────────────────────────
        // H1B: affirmative phrases only; suppress on explicit negations (same pattern as Jobicy)
        var h1bKeywordHit = ContainsAny(desc, "h1b", "h-1b", "will sponsor visa", "visa sponsorship available",
                                "h1b sponsorship", "h1b transfer");
        var h1bNegation   = ContainsAny(desc, "no visa sponsor", "not sponsor", "unable to sponsor",
                                "cannot sponsor", "no sponsorship", "sponsorship not available",
                                "do not sponsor", "does not sponsor");
        var isH1B         = (h1bKeywordHit && !h1bNegation) ||
                            (companyName != null && (sponsors.Contains(companyName) || sponsors.Contains(NormalizeCompanyName(companyName))));
        var isOptCpt      = !h1bNegation && ContainsAny(desc, " opt ", "opt/cpt", "stem opt", "opt extension", "f-1 visa", " cpt ");
        var isTnVisa      = !h1bNegation && ContainsAny(desc, "tn visa", "tn-1", "tn-2", "usmca", "nafta visa");
        var isE3Visa      = !h1bNegation && ContainsAny(desc, "e-3", "e3 visa", "e-3 visa");
        var isJ1Visa      = !h1bNegation && ContainsAny(desc, "j-1", "j1 visa", "j-1 visa", "exchange visitor");
        var isGreenCard   = !h1bNegation && ContainsAny(desc, "green card", "gc sponsor", "perm filing", "eb-2", "eb-3", "labor certification");

        // Contract: specific job-context phrases; plain "contract" too broad
        var isC2H         = ContainsAny(desc, "contract to hire", "contract-to-hire", "c2h",
                                "right to hire", "right-to-hire", "temp to perm", "temp-to-perm");
        var isContract    = isC2H || ContainsAny(desc, "contract position", "contract role",
                                "contract opportunity", "contract assignment", "contract work", "contract employee") ||
                            (tags?.Any(x => x.Equals("contract", StringComparison.OrdinalIgnoreCase)) == true);

        // C2C / W2 / PrimeVendor: valid for US remote jobs on RemoteOK
        var isC2C         = ContainsAny(desc, "c2c", "corp to corp", "corp-to-corp", "corp2corp");
        var isW2          = ContainsAny(desc, "w2 only", "w-2 only", "w2 employment", "on w2");
        var isPrimeVendor = ContainsAny(desc, "prime vendor", "direct client", "end client");

        var isFreelance   = ContainsAny(desc, "1099", "freelance", "independent contractor");

        // Staffing: company name only — remove "consulting"/"solutions" (too broad, matches Google, Microsoft etc.)
        var isStaffing    = ContainsAny(companyName, "staffing", "recruitment", "manpower");

        // University: company name only (description check causes false positives like ManTech)
        var isUniversity  = ContainsAny(companyName, "university", "college", "institute", "academia");

        // Startup: require funding-context phrases; bare "seed"/"venture" too broad (same as Jobicy)
        var isStartup     = ContainsAny(desc, "startup", "start-up", "series a", "series b", "series c",
                                "seed round", "seed funding", "pre-seed", "venture-backed", "vc-backed") ||
                            ContainsAny(companyName, "startup", "start-up");

        var isNonProfit   = ContainsAny(desc, "nonprofit", "non-profit", "501(c)", "501c3", "ngo",
                                "not-for-profit") ||
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
            City              = city,
            State             = state,
            Country           = country,
            PostDate          = postDate,
            HoursBackPosted   = ParseHoursBack(postDate),
            SalaryMin         = salMin,
            SalaryMax         = salMax,
            SalaryRangeText   = salMin.HasValue && salMax.HasValue ? $"${salMin:N0}–${salMax:N0}/yr" : null,
            SalaryCurrency    = "USD",
            WorkType          = "Remote",
            JobWorkMode       = "Remote",
            JobLevel          = NormalizeJobLevel(jobLevel),
            ContractType      = contractType,
            CompanyName       = companyName,
            CompanyLogoUrl    = logoUrl,
            Skills            = tags,
            // ── All flags ─────────────────────────────────────────────────────
            IsH1BSponsored    = isH1B,
            IsOptCpt          = isOptCpt,
            IsTnVisa          = isTnVisa,
            IsE3Visa          = isE3Visa,
            IsJ1Visa          = isJ1Visa,
            IsGreenCard       = isGreenCard,
            IsSponsored       = isH1B || isOptCpt || isTnVisa || isE3Visa || isJ1Visa || isGreenCard,
            IsContractJob     = isContract,
            IsContractToHire  = isC2H,
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
        FetchTagAsync(input.SearchQuery ?? "software-engineer", input.HoursBack, fetchRunId,
            LoadSponsorsAsync(ct).GetAwaiter().GetResult(), ct);
}
