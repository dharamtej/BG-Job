// CareerPandaBL/Background/Handlers/H1BSponsorEnrichmentJobHandler.cs
// Enriches api.h1b_sponsors with normalized_name via Wikipedia REST API.
// Run this job manually after the initial data load and periodically for new rows.
// Wikipedia requires a descriptive User-Agent — configured in Program.cs.
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using CareerPanda.DataAccess.DA;
using CareerPanda.DataAccess.Entities.Api;
using CareerPanda.Framework.Cache;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CareerPanda.BL.Background.Handlers;

public partial class H1BSponsorEnrichmentJobHandler : IJobHandler
{
    public string JobType => "H1BSponsorEnrichment";

    private const string SponsorsCacheKey = "h1b:sponsors:names";

    private static readonly string[] LegalSuffixes =
        ["INCORPORATED", "CORPORATION", "LIMITED", "INC", "LLC", "CORP", "LTD", "CO", "LP", "LLP", "PLLC", "PC"];

    private static readonly string[] CompanyDescriptionWords =
        ["company", "corporation", "firm", "organization", "technology", "software",
         "services", "group", "holding", "american", "multinational", "startup", "business", "enterprise"];

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ICacheService _cache;
    private readonly ILogger<H1BSponsorEnrichmentJobHandler> _logger;

    public H1BSponsorEnrichmentJobHandler(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpFactory,
        ICacheService cache,
        ILogger<H1BSponsorEnrichmentJobHandler> logger)
    {
        _scopeFactory = scopeFactory;
        _httpFactory  = httpFactory;
        _cache        = cache;
        _logger       = logger;
    }

    public async Task ExecuteAsync(
        JobWorkRequest request,
        IJobProgressReporter progress,
        CancellationToken cancellationToken)
    {
        var input = ParseInput(request.InputPayload);

        var http = _httpFactory.CreateClient("Wikipedia");

        int totalProcessed = 0, wikiHits = 0, fallbacks = 0;
        int batchNumber = 0;

        // Progress reporter wraps a scoped DbContext — serialize calls to avoid concurrent write errors
        using var progressLock = new SemaphoreSlim(1, 1);

        var parallelOpts = new ParallelOptions
        {
            MaxDegreeOfParallelism = input.MaxParallel,
            CancellationToken      = cancellationToken
        };

        while (!cancellationToken.IsCancellationRequested)
        {
            // Each batch load gets its own short-lived scope
            List<ApiH1bSponsor> sponsors;
            using (var loadScope = _scopeFactory.CreateScope())
            {
                var loadDa = loadScope.ServiceProvider.GetRequiredService<IJobFetchDA>();
                sponsors = await loadDa.GetUnenrichedSponsorsAsync(input.BatchSize, cancellationToken);
            }

            if (sponsors.Count == 0)
            {
                if (totalProcessed == 0)
                    await progress.ReportProgressAsync(100, "All sponsors already enriched.");
                break;
            }

            batchNumber++;
            _logger.LogInformation("[H1BEnrich] Batch {Batch} — {Count} sponsors (parallel={P})",
                batchNumber, sponsors.Count, input.MaxParallel);

            await Parallel.ForEachAsync(sponsors, parallelOpts, async (sponsor, ct) =>
            {
                var searchTerm = ExtractSearchTerm(sponsor.EmployerName);
                var wikiName   = await LookupWikipediaAsync(http, searchTerm, ct);
                var finalName  = wikiName ?? searchTerm;

                if (wikiName != null) Interlocked.Increment(ref wikiHits);
                else                  Interlocked.Increment(ref fallbacks);

                // Each parallel item gets its own DbContext — EF Core is not thread-safe
                using var itemScope = _scopeFactory.CreateScope();
                var itemDa = itemScope.ServiceProvider.GetRequiredService<IJobFetchDA>();
                await itemDa.UpdateSponsorNormalizedNameAsync(sponsor.Id, finalName, ct);

                var done = Interlocked.Increment(ref totalProcessed);
                _logger.LogDebug("[H1BEnrich] {Employer} → {Name} ({Source})",
                    sponsor.EmployerName, finalName, wikiName != null ? "Wikipedia" : "fallback");

                await progressLock.WaitAsync(ct);
                try
                {
                    await progress.ReportProgressAsync(0,
                        $"Batch {batchNumber} total={done} — Wiki: {wikiHits}, Fallback: {fallbacks}");
                }
                finally { progressLock.Release(); }
            });
        }

        // Bust the Redis cache so H1BJobsJobHandler picks up new names on next run
        await _cache.RemoveAsync(SponsorsCacheKey, cancellationToken);

        _logger.LogInformation("[H1BEnrich] Done — total={T}, Wikipedia: {W}, Fallback: {F}", totalProcessed, wikiHits, fallbacks);
        await progress.ReportProgressAsync(100,
            $"Done — total={totalProcessed}, Wikipedia: {wikiHits}, Fallback: {fallbacks}. Cache cleared.");
    }

    // ── Wikipedia lookup ─────────────────────────────────────────────────────

    private async Task<string?> LookupWikipediaAsync(
        HttpClient http, string searchTerm, CancellationToken ct)
    {
        // 1. Try direct page summary (fast — works when name matches page title exactly)
        var slug    = Uri.EscapeDataString(searchTerm.Replace(' ', '_'));
        var result  = await TrySummaryAsync(http, slug, ct);
        if (result != null) return result;

        // 2. Fall back to full-text search with "company" appended for better relevance
        return await TrySearchAsync(http, searchTerm, ct);
    }

    private async Task<string?> TrySummaryAsync(HttpClient http, string slug, CancellationToken ct)
    {
        try
        {
            var url      = $"https://en.wikipedia.org/api/rest_v1/page/summary/{slug}";
            var response = await http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return null;

            var json        = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            var title       = json.TryGetProperty("title",       out var t) ? t.GetString() ?? "" : "";
            var description = json.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";

            if (!IsCompanyPage(description)) return null;
            return CleanTitle(title);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[H1BEnrich] Summary API failed for {Slug}", slug);
            return null;
        }
    }

    private async Task<string?> TrySearchAsync(HttpClient http, string searchTerm, CancellationToken ct)
    {
        try
        {
            var encoded = Uri.EscapeDataString($"{searchTerm} company");
            var url     = $"https://en.wikipedia.org/w/api.php?action=query&list=search" +
                          $"&srsearch={encoded}&format=json&srlimit=3&srnamespace=0";

            var response = await http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return null;

            var json    = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            var results = json.GetProperty("query").GetProperty("search");

            foreach (var item in results.EnumerateArray())
            {
                var title   = item.TryGetProperty("title",   out var t) ? t.GetString() ?? "" : "";
                var snippet = item.TryGetProperty("snippet", out var s) ? s.GetString() ?? "" : "";

                // First word of search term should appear in the Wikipedia title
                var firstWord = searchTerm.Split(' ')[0];
                if (!title.Contains(firstWord, StringComparison.OrdinalIgnoreCase)) continue;
                if (!IsCompanyPage(snippet)) continue;

                return CleanTitle(title);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[H1BEnrich] Search API failed for {Term}", searchTerm);
        }

        return null;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    // "Amazon (company)" → "Amazon",  "Meta Platforms" → "Meta Platforms"
    [GeneratedRegex(@"\s*\([^)]*\)\s*$")]
    private static partial Regex DisambiguationRe();

    private static string CleanTitle(string title) =>
        DisambiguationRe().Replace(title, "").Trim();

    private static bool IsCompanyPage(string text) =>
        CompanyDescriptionWords.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));

    // "1661 Inc D B A Goat"       → "Goat"
    // "Amazon.com Inc"            → "Amazon.com"
    // "Meta Platforms Inc"        → "Meta Platforms"
    [GeneratedRegex(@"\bD\.?\s*B\.?\s*A\.?\s+(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex DbaRe();

    [GeneratedRegex(@"[^\w\s]")]
    private static partial Regex PunctuationRe();

    private static string ExtractSearchTerm(string employerName)
    {
        var name = employerName;

        // Extract DBA trade name if present
        var dbaMatch = DbaRe().Match(name);
        if (dbaMatch.Success)
            return dbaMatch.Groups[1].Value.Trim();

        // Strip punctuation and trailing legal suffixes
        var stripped = PunctuationRe().Replace(name, " ");
        var parts    = stripped.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int count    = parts.Length;
        while (count > 1 && LegalSuffixes.Contains(parts[count - 1].ToUpperInvariant()))
            count--;

        return string.Join(' ', parts, 0, count);
    }

    private static EnrichmentInput ParseInput(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return new EnrichmentInput();
        try { return System.Text.Json.JsonSerializer.Deserialize<EnrichmentInput>(payload) ?? new EnrichmentInput(); }
        catch { return new EnrichmentInput(); }
    }

    private sealed class EnrichmentInput
    {
        public int BatchSize   { get; set; } = 500;
        public int MaxParallel { get; set; } = 10;
    }
}
