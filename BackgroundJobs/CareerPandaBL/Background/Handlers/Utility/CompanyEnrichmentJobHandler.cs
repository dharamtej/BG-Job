// CareerPandaBL/Background/Handlers/CompanyEnrichmentJobHandler.cs
// Multi-source company enrichment — fills logo_url / about_company / website /
// career_page / company_size on api.companies, never overwriting existing data.
//
// PER-COMPANY CHAIN:
//   1. Wikidata search by name → Q-id (if the entity looks like a company).
//      From the entity we read:
//        P856  → official website
//        P154  → official logo (Commons filename → Special:FilePath URL, square)
//        P1128 → number of employees   → company_size
//        sitelinks.enwiki.title        → exact Wikipedia article title
//   2. Wikipedia summary (by enwiki title or company name) → about_company
//      extract + thumbnail (fallback logo).
//   3. Domain discovery:
//        existing company.Website  →  raw_jobs.company_url (ATS hosts filtered)
//                                  →  Clearbit autocomplete  →  Wikidata P856
//   4. Brandfetch (by domain) → real SVG/PNG logo + description fallback
//   5. logo_url     = wikidata logo || wikipedia thumbnail || brandfetch logo  (NO favicon)
//      website      = https://{domain} when discovered
//      career_page  = raw_jobs.company_url for this company (the apply/board URL)
//      company_size = Wikidata P1128 (latest)
//
// All fields use COALESCE — null inputs don't blank existing values.
using System.Net.Http.Json;
using System.Text.Json;
using CareerPanda.DataAccess.DA;
using CareerPanda.DataAccess.Entities.Api;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CareerPanda.BL.Background.Handlers;

public class CompanyEnrichmentJobHandler : IJobHandler
{
    public string JobType => "CompanyEnrichment";

    // ATS aggregator hosts — these are board URLs, NOT the company's real domain.
    private static readonly string[] AtsHostSuffixes =
    [
        "lever.co", "greenhouse.io", "boards-api.greenhouse.io", "workday.com", "myworkdayjobs.com",
        "icims.com", "bamboohr.com", "ashbyhq.com", "recruitee.com", "smartrecruiters.com",
        "jobvite.com", "taleo.net", "successfactors.com", "applicantpro.com", "breezy.hr",
        "ziprecruiter.com", "indeed.com", "linkedin.com", "themuse.com", "usajobs.gov",
        "adzuna.com", "remoteok.com", "remoteok.io", "jobicy.com",
    ];

    // Wikidata properties that strongly indicate "this is a company"
    private static readonly string[] CompanyIndicatorProps = ["P856", "P1128", "P159", "P452", "P127", "P749", "P355"];

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<CompanyEnrichmentJobHandler> _logger;
    private readonly string? _brandfetchApiKey;

    public CompanyEnrichmentJobHandler(IServiceScopeFactory scopeFactory, IHttpClientFactory httpClientFactory,
        ILogger<CompanyEnrichmentJobHandler> logger, IConfiguration configuration)
    {
        _scopeFactory      = scopeFactory;
        _http              = httpClientFactory;
        _logger            = logger;
        _brandfetchApiKey  = configuration["JobApiSettings:BrandfetchApiKey"];
    }

    private sealed record Input(int BatchSize, int MaxParallel);
    private static Input ParseInput(string? payload)
    {
        int batch = 200, parallel = 8;
        if (!string.IsNullOrWhiteSpace(payload))
        {
            try
            {
                var d = JsonSerializer.Deserialize<JsonElement>(payload);
                if (d.TryGetProperty("BatchSize",   out var b) && b.ValueKind == JsonValueKind.Number) batch    = b.GetInt32();
                if (d.TryGetProperty("MaxParallel", out var p) && p.ValueKind == JsonValueKind.Number) parallel = p.GetInt32();
            } catch { }
        }
        return new Input(Math.Clamp(batch, 1, 1000), Math.Clamp(parallel, 1, 16));
    }

    private sealed record WikidataInfo(string? Website, string? LogoUrl, int? Employees, string? EnwikiTitle);
    private sealed record BrandfetchInfo(string? LogoUrl, string? Description);

    public async Task ExecuteAsync(JobWorkRequest request, IJobProgressReporter progress, CancellationToken cancellationToken)
    {
        var input = ParseInput(request.InputPayload);
        var http  = _http.CreateClient("Enrichment");

        // Create a fetch-run row so this job is visible in Run History + per-handler stats.
        var run = new ApiJobFetchRun
        {
            Id               = request.JobId,
            BackgroundTaskId = request.JobId,
            JobCategory      = "CompanyEnrichment",
            ApiSource        = "Enrichment",
            Status           = "Running",
            StartedAt        = DateTime.UtcNow,
            CreatedById      = request.UserId
        };
        using (var initScope = _scopeFactory.CreateScope())
        {
            var initDa = initScope.ServiceProvider.GetRequiredService<IJobFetchDA>();
            await initDa.CreateFetchRunAsync(run);
        }

        int totalProcessed = 0, updated = 0, logos = 0, abouts = 0, websites = 0, sizes = 0, skipped = 0, errors = 0;
        int afterId = 0, batchNo = 0;

        using var progressLock = new SemaphoreSlim(1, 1);
        var parallelOpts = new ParallelOptions { MaxDegreeOfParallelism = input.MaxParallel, CancellationToken = cancellationToken };

        await progress.ReportProgressAsync(0, "Starting company enrichment…");
        try
        {
        while (!cancellationToken.IsCancellationRequested)
        {
            List<ApiCompany> batch;
            using (var loadScope = _scopeFactory.CreateScope())
            {
                var da = loadScope.ServiceProvider.GetRequiredService<IJobFetchDA>();
                batch = await da.GetCompaniesForEnrichmentAsync(afterId, input.BatchSize, cancellationToken);
            }
            if (batch.Count == 0) break;
            batchNo++; afterId = batch[^1].Id;

            await Parallel.ForEachAsync(batch, parallelOpts, async (company, ct) =>
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(company.CompanyName)) { Interlocked.Increment(ref skipped); return; }

                    // 1) Wikidata — structured firmographics (website, logo, employees)
                    var wd = await WikidataEnrichAsync(http, company.CompanyName, ct);

                    // 2) Wikipedia summary — about + thumbnail. Use the enwiki title when known.
                    var (about, thumb) = await WikipediaAsync(http, wd.EnwikiTitle ?? company.CompanyName, ct);

                    // 3) Domain chain — for Brandfetch and website.
                    string? domain = ExtractDomain(company.Website)
                                     ?? ExtractDomain(wd.Website);
                    if (domain == null)
                    {
                        using var s1 = _scopeFactory.CreateScope();
                        var da1 = s1.ServiceProvider.GetRequiredService<IJobFetchDA>();
                        var sample = await da1.GetSampleCompanyUrlAsync(company.Id, ct);
                        domain = ExtractDomain(sample);
                    }
                    if (domain == null) domain = await ClearbitDomainAsync(http, company.CompanyName, ct);

                    // 4) Brandfetch — real SVG/PNG logo by domain (free tier, no favicon junk)
                    BrandfetchInfo bf = new(null, null);
                    if (domain != null) bf = await BrandfetchAsync(http, domain, ct);

                    // 5) Final field values — NO favicon fallback; null is better than blurry 16×16
                    string? logoUrl = wd.LogoUrl ?? thumb ?? bf.LogoUrl;
                    if (about == null && bf.Description != null) about = bf.Description;

                    var website = domain != null ? $"https://{domain}" : null;
                    int?   size = wd.Employees;

                    string? careerPage;
                    using (var s2 = _scopeFactory.CreateScope())
                    {
                        var da2 = s2.ServiceProvider.GetRequiredService<IJobFetchDA>();
                        careerPage = await da2.GetSampleCompanyUrlAsync(company.Id, ct);
                        await da2.UpdateCompanyEnrichmentAsync(company.Id,
                            companyType: null, companySize: size,
                            aboutCompany: about, website: website,
                            careerPage: careerPage, logoUrl: logoUrl, ct);
                    }

                    if (logoUrl != null) Interlocked.Increment(ref logos);
                    if (about   != null) Interlocked.Increment(ref abouts);
                    if (website != null) Interlocked.Increment(ref websites);
                    if (size    != null) Interlocked.Increment(ref sizes);
                    if (logoUrl != null || about != null || website != null || careerPage != null || size != null) Interlocked.Increment(ref updated);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { Interlocked.Increment(ref errors); _logger.LogWarning(ex, "[CompanyEnrich] Failed for {Company}", company.CompanyName); }

                var done = Interlocked.Increment(ref totalProcessed);
                if (done % 25 == 0)
                {
                    await progressLock.WaitAsync(ct);
                    try
                    {
                        await progress.ReportProgressAsync(0, $"Batch {batchNo} — processed {done}, updated {Volatile.Read(ref updated)} (logos {Volatile.Read(ref logos)}, sites {Volatile.Read(ref websites)}, size {Volatile.Read(ref sizes)}, about {Volatile.Read(ref abouts)})");
                        // Persist live stats so the dashboard shows progress.
                        using var sScope = _scopeFactory.CreateScope();
                        var sDa = sScope.ServiceProvider.GetRequiredService<IJobFetchDA>();
                        await sDa.UpdateFetchRunStatsAsync(run.Id,
                            totalFetched:  done,
                            totalInserted: Volatile.Read(ref updated),
                            totalUpdated:  Volatile.Read(ref logos),
                            totalSkipped:  Volatile.Read(ref skipped),
                            totalErrors:   Volatile.Read(ref errors),
                            pagesFetched:  batchNo);
                    }
                    finally { progressLock.Release(); }
                }
            });
        }
            using (var doneScope = _scopeFactory.CreateScope())
            {
                var doneDa = doneScope.ServiceProvider.GetRequiredService<IJobFetchDA>();
                await doneDa.UpdateFetchRunStatsAsync(run.Id, totalProcessed, updated, logos, skipped, errors, batchNo);
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

        _logger.LogInformation("[CompanyEnrich] Done — processed {P} updated {U} logos {L} sites {W} size {Z} about {A} skipped {S}",
            totalProcessed, updated, logos, websites, sizes, abouts, skipped);
        await progress.ReportProgressAsync(100,
            $"Done — processed {totalProcessed}, updated {updated} (logos {logos}, sites {websites}, size {sizes}, about {abouts}, skipped {skipped}).");
    }

    // ── Wikidata: name → Q-id → website / logo / employees / enwiki title ────
    private async Task<WikidataInfo> WikidataEnrichAsync(HttpClient http, string name, CancellationToken ct)
    {
        try
        {
            // 1) wbsearchentities — get the best Q-id match
            var searchUrl = $"https://www.wikidata.org/w/api.php?action=wbsearchentities&search={Uri.EscapeDataString(name)}&language=en&format=json&type=item&limit=1";
            using var sResp = await http.GetAsync(searchUrl, ct);
            if (!sResp.IsSuccessStatusCode) return new(null, null, null, null);
            var sJson = await sResp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            if (!sJson.TryGetProperty("search", out var arr) || arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0)
                return new(null, null, null, null);
            var qid = arr[0].TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(qid)) return new(null, null, null, null);

            // 2) Fetch the entity
            using var eResp = await http.GetAsync($"https://www.wikidata.org/wiki/Special:EntityData/{qid}.json", ct);
            if (!eResp.IsSuccessStatusCode) return new(null, null, null, null);
            var eJson = await eResp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            if (!eJson.TryGetProperty("entities", out var ents) || !ents.TryGetProperty(qid, out var ent))
                return new(null, null, null, null);

            // 3) Validate it looks like a company (has at least one firmographic property)
            if (!ent.TryGetProperty("claims", out var claims) || claims.ValueKind != JsonValueKind.Object)
                return new(null, null, null, null);
            bool looksLikeCompany = false;
            foreach (var p in CompanyIndicatorProps) if (claims.TryGetProperty(p, out _)) { looksLikeCompany = true; break; }
            if (!looksLikeCompany) return new(null, null, null, null);

            string? website   = TryGetClaimString(claims, "P856");
            string? logoFile  = TryGetClaimString(claims, "P154");
            int?    employees = TryGetLatestEmployees(claims, "P1128");
            string? enwiki    = null;
            if (ent.TryGetProperty("sitelinks", out var links) && links.TryGetProperty("enwiki", out var enw)
                && enw.TryGetProperty("title", out var ti) && ti.ValueKind == JsonValueKind.String)
                enwiki = ti.GetString();

            string? logoUrl = string.IsNullOrWhiteSpace(logoFile) ? null
                : $"https://commons.wikimedia.org/wiki/Special:FilePath/{Uri.EscapeDataString(logoFile)}?width=256";

            return new(website, logoUrl, employees, enwiki);
        }
        catch (Exception ex) { _logger.LogDebug(ex, "[CompanyEnrich] Wikidata lookup failed for {Name}", name); return new(null, null, null, null); }
    }

    private static string? TryGetClaimString(JsonElement claims, string prop)
    {
        if (!claims.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0) return null;
        foreach (var c in arr.EnumerateArray())
        {
            if (c.TryGetProperty("mainsnak", out var ms)
                && ms.TryGetProperty("datavalue", out var dv)
                && dv.TryGetProperty("value", out var v)
                && v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString();
                if (!string.IsNullOrWhiteSpace(s)) return s;
            }
        }
        return null;
    }

    private static int? TryGetLatestEmployees(JsonElement claims, string prop)
    {
        if (!claims.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
        // P1128 main value is an object { amount: "+85000", unit: "1" }. Latest = highest P585 qualifier ("point in time").
        DateTime bestTime = DateTime.MinValue;
        int? best = null;
        foreach (var c in arr.EnumerateArray())
        {
            if (!c.TryGetProperty("mainsnak", out var ms) || !ms.TryGetProperty("datavalue", out var dv)
                || !dv.TryGetProperty("value", out var v) || v.ValueKind != JsonValueKind.Object) continue;
            if (!v.TryGetProperty("amount", out var amtEl) || amtEl.ValueKind != JsonValueKind.String) continue;
            var amt = (amtEl.GetString() ?? "").TrimStart('+');
            if (!int.TryParse(amt.Split('.')[0], out var n) || n <= 0) continue;

            DateTime t = DateTime.MinValue;
            if (c.TryGetProperty("qualifiers", out var qs) && qs.TryGetProperty("P585", out var p585) && p585.ValueKind == JsonValueKind.Array)
            {
                foreach (var q in p585.EnumerateArray())
                {
                    if (q.TryGetProperty("datavalue", out var qdv) && qdv.TryGetProperty("value", out var qv)
                        && qv.TryGetProperty("time", out var tt) && tt.ValueKind == JsonValueKind.String)
                    {
                        // Wikidata time format: "+2023-01-01T00:00:00Z"
                        var raw = (tt.GetString() ?? "").TrimStart('+');
                        if (DateTime.TryParse(raw, out var parsed) && parsed > t) t = parsed;
                    }
                }
            }
            if (best == null || t > bestTime) { best = n; bestTime = t; }
        }
        return best;
    }

    // ── Wikipedia: about extract + thumbnail (fallback logo) ─────────────────
    private async Task<(string? about, string? thumb)> WikipediaAsync(HttpClient http, string title, CancellationToken ct)
    {
        try
        {
            var slug = Uri.EscapeDataString(title.Trim().Replace(' ', '_'));
            using var resp = await http.GetAsync($"https://en.wikipedia.org/api/rest_v1/page/summary/{slug}", ct);
            if (!resp.IsSuccessStatusCode) return (null, null);
            var json = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            var type = json.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type == "disambiguation") return (null, null);
            var about = json.TryGetProperty("extract", out var e) ? e.GetString() : null;
            string? thumb = null;
            if (json.TryGetProperty("thumbnail", out var th) && th.ValueKind == JsonValueKind.Object
                && th.TryGetProperty("source", out var src) && src.ValueKind == JsonValueKind.String)
                thumb = src.GetString();
            return (string.IsNullOrWhiteSpace(about) ? null : about, string.IsNullOrWhiteSpace(thumb) ? null : thumb);
        }
        catch (Exception ex) { _logger.LogDebug(ex, "[CompanyEnrich] Wikipedia lookup failed for {Name}", title); return (null, null); }
    }

    // ── Clearbit autocomplete fallback for domain ────────────────────────────
    private async Task<string?> ClearbitDomainAsync(HttpClient http, string name, CancellationToken ct)
    {
        try
        {
            using var resp = await http.GetAsync($"https://autocomplete.clearbit.com/v1/companies/suggest?query={Uri.EscapeDataString(name)}", ct);
            if (!resp.IsSuccessStatusCode) return null;
            var arr = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            if (arr.ValueKind != JsonValueKind.Array) return null;
            foreach (var item in arr.EnumerateArray())
            {
                var d = item.TryGetProperty("domain", out var de) ? de.GetString() : null;
                if (!string.IsNullOrWhiteSpace(d)) return d;
            }
        }
        catch (Exception ex) { _logger.LogDebug(ex, "[CompanyEnrich] Clearbit domain lookup failed for {Name}", name); }
        return null;
    }

    // ── Brandfetch: real logo by domain (phase-2 fallback) ───────────────────
    // Free tier: 10K requests/month. Requires API key in JobApiSettings:BrandfetchApiKey.
    // Response shape: { logos: [{ type: "logo"|"icon", formats: [{ src, format }] }], description }
    private async Task<BrandfetchInfo> BrandfetchAsync(HttpClient http, string domain, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_brandfetchApiKey)) return new(null, null);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.brandfetch.io/v2/brands/{Uri.EscapeDataString(domain)}");
            req.Headers.Add("Authorization", $"Bearer {_brandfetchApiKey}");
            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return new(null, null);
            var json = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);

            // Prefer "logo" type over "icon"; prefer SVG, then PNG
            string? logoUrl = null;
            if (json.TryGetProperty("logos", out var logos) && logos.ValueKind == JsonValueKind.Array)
            {
                // Two passes: first "logo" type, then "icon" as fallback
                foreach (var preferredType in new[] { "logo", "icon" })
                {
                    foreach (var logo in logos.EnumerateArray())
                    {
                        var logoType = logo.TryGetProperty("type", out var lt) ? lt.GetString() : null;
                        if (!string.Equals(logoType, preferredType, StringComparison.OrdinalIgnoreCase)) continue;
                        if (!logo.TryGetProperty("formats", out var fmts) || fmts.ValueKind != JsonValueKind.Array) continue;

                        string? svgUrl = null, pngUrl = null;
                        foreach (var fmt in fmts.EnumerateArray())
                        {
                            var src    = fmt.TryGetProperty("src",    out var s) ? s.GetString() : null;
                            var format = fmt.TryGetProperty("format", out var f) ? f.GetString() : null;
                            if (string.IsNullOrWhiteSpace(src)) continue;
                            if (format == "svg") svgUrl = src;
                            else if (format == "png" && pngUrl == null) pngUrl = src;
                        }
                        logoUrl = svgUrl ?? pngUrl;
                        if (logoUrl != null) break;
                    }
                    if (logoUrl != null) break;
                }
            }

            string? description = null;
            if (json.TryGetProperty("description", out var desc) && desc.ValueKind == JsonValueKind.String)
                description = desc.GetString();

            return new(logoUrl, description);
        }
        catch (Exception ex) { _logger.LogDebug(ex, "[CompanyEnrich] Brandfetch failed for {Domain}", domain); return new(null, null); }
    }

    // ── Extract a real company domain, skipping ATS aggregator hosts ─────────
    private static string? ExtractDomain(string? rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl)) return null;
        var s = rawUrl.Trim(); if (!s.Contains("://")) s = "https://" + s;
        if (!Uri.TryCreate(s, UriKind.Absolute, out var u)) return null;
        var host = u.Host.ToLowerInvariant();
        if (host.StartsWith("www.")) host = host[4..];
        if (host.Length == 0) return null;
        foreach (var ats in AtsHostSuffixes)
            if (host == ats || host.EndsWith("." + ats)) return null;
        return host;
    }
}
