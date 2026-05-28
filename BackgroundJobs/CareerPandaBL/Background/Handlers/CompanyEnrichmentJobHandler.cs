// CareerPandaBL/Background/Handlers/CompanyEnrichmentJobHandler.cs
// SOURCE : Clearbit autocomplete (domain + square logo, free/keyless) + Wikipedia (about).
// FLOW   : Page through api.companies → for each, look up domain/logo (Clearbit),
//          summary (Wikipedia), career page (our own raw_jobs.company_url)
//          → update ONLY company_type/size/about/website/career_page/logo_url (COALESCE).
// NOTE   : company_size & image_urls have no reliable free source — left untouched.
//          Logos come back square (1:1) from Clearbit.
using System.Net.Http.Json;
using System.Text.Json;
using CareerPanda.DataAccess.DA;
using CareerPanda.DataAccess.Entities.Api;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CareerPanda.BL.Background.Handlers;

public class CompanyEnrichmentJobHandler : IJobHandler
{
    public string JobType => "CompanyEnrichment";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<CompanyEnrichmentJobHandler> _logger;

    public CompanyEnrichmentJobHandler(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        ILogger<CompanyEnrichmentJobHandler> logger)
    {
        _scopeFactory = scopeFactory;
        _http         = httpClientFactory;
        _logger       = logger;
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
                if (d.TryGetProperty("BatchSize", out var b) && b.ValueKind == JsonValueKind.Number) batch = b.GetInt32();
                if (d.TryGetProperty("MaxParallel", out var p) && p.ValueKind == JsonValueKind.Number) parallel = p.GetInt32();
            }
            catch { /* defaults */ }
        }
        return new Input(Math.Clamp(batch, 1, 1000), Math.Clamp(parallel, 1, 16));
    }

    public async Task ExecuteAsync(JobWorkRequest request, IJobProgressReporter progress, CancellationToken cancellationToken)
    {
        var input = ParseInput(request.InputPayload);
        var http  = _http.CreateClient("Enrichment");

        int totalProcessed = 0, updated = 0, logos = 0, abouts = 0, skipped = 0;
        int afterId = 0, batchNo = 0;

        // Progress writes wrap a scoped DbContext — serialize them.
        using var progressLock = new SemaphoreSlim(1, 1);
        var parallelOpts = new ParallelOptions { MaxDegreeOfParallelism = input.MaxParallel, CancellationToken = cancellationToken };

        await progress.ReportProgressAsync(0, "Starting company enrichment…");

        while (!cancellationToken.IsCancellationRequested)
        {
            List<ApiCompany> batch;
            using (var loadScope = _scopeFactory.CreateScope())
            {
                var da = loadScope.ServiceProvider.GetRequiredService<IJobFetchDA>();
                batch = await da.GetCompaniesForEnrichmentAsync(afterId, input.BatchSize, cancellationToken);
            }
            if (batch.Count == 0) break;

            batchNo++;
            afterId = batch[^1].Id;   // page forward by max id in this batch

            await Parallel.ForEachAsync(batch, parallelOpts, async (company, ct) =>
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(company.CompanyName)) { Interlocked.Increment(ref skipped); return; }

                    var (domain, logo) = await ClearbitLookupAsync(http, company.CompanyName, ct);
                    var about          = await WikipediaAboutAsync(http, company.CompanyName, ct);

                    string? careerPage = null;
                    using (var s = _scopeFactory.CreateScope())
                    {
                        var da = s.ServiceProvider.GetRequiredService<IJobFetchDA>();
                        careerPage = await da.GetSampleCompanyUrlAsync(company.Id, ct);

                        var website = domain != null ? $"https://{domain}" : null;
                        await da.UpdateCompanyEnrichmentAsync(company.Id,
                            companyType: null,        // preserve handler-set type
                            companySize: null,        // no reliable free source
                            aboutCompany: about,
                            website: website,
                            careerPage: careerPage,
                            logoUrl: logo, ct);
                    }

                    if (logo != null)  Interlocked.Increment(ref logos);
                    if (about != null) Interlocked.Increment(ref abouts);
                    if (logo != null || about != null || domain != null || careerPage != null) Interlocked.Increment(ref updated);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { _logger.LogWarning(ex, "[CompanyEnrich] Failed for {Company}", company.CompanyName); }

                var done = Interlocked.Increment(ref totalProcessed);
                if (done % 25 == 0)
                {
                    await progressLock.WaitAsync(ct);
                    try { await progress.ReportProgressAsync(0, $"Batch {batchNo} — processed {done}, updated {Volatile.Read(ref updated)} (logos {Volatile.Read(ref logos)}, about {Volatile.Read(ref abouts)})"); }
                    finally { progressLock.Release(); }
                }
            });
        }

        _logger.LogInformation("[CompanyEnrich] Done — processed {P} updated {U} logos {L} about {A} skipped {S}",
            totalProcessed, updated, logos, abouts, skipped);
        await progress.ReportProgressAsync(100, $"Done — processed {totalProcessed}, updated {updated} (logos {logos}, about {abouts}, skipped {skipped}).");
    }

    // ── Clearbit autocomplete — name → {domain, square logo} (free, keyless) ──
    private async Task<(string? domain, string? logo)> ClearbitLookupAsync(HttpClient http, string name, CancellationToken ct)
    {
        try
        {
            var url = $"https://autocomplete.clearbit.com/v1/companies/suggest?query={Uri.EscapeDataString(name)}";
            using var resp = await http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return (null, null);

            var arr = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            if (arr.ValueKind != JsonValueKind.Array) return (null, null);

            foreach (var item in arr.EnumerateArray())
            {
                var domain = item.TryGetProperty("domain", out var d) ? d.GetString() : null;
                var logo   = item.TryGetProperty("logo",   out var l) ? l.GetString() : null;
                if (!string.IsNullOrWhiteSpace(domain))
                    return (domain, string.IsNullOrWhiteSpace(logo) ? $"https://logo.clearbit.com/{domain}" : logo);
            }
        }
        catch (Exception ex) { _logger.LogDebug(ex, "[CompanyEnrich] Clearbit lookup failed for {Name}", name); }
        return (null, null);
    }

    // ── Wikipedia summary — name → about_company extract ──────────────────────
    private async Task<string?> WikipediaAboutAsync(HttpClient http, string name, CancellationToken ct)
    {
        try
        {
            var slug = Uri.EscapeDataString(name.Trim().Replace(' ', '_'));
            using var resp = await http.GetAsync($"https://en.wikipedia.org/api/rest_v1/page/summary/{slug}", ct);
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            var type = json.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type == "disambiguation") return null;
            var extract = json.TryGetProperty("extract", out var e) ? e.GetString() : null;
            return string.IsNullOrWhiteSpace(extract) ? null : extract;
        }
        catch (Exception ex) { _logger.LogDebug(ex, "[CompanyEnrich] Wikipedia lookup failed for {Name}", name); return null; }
    }
}
