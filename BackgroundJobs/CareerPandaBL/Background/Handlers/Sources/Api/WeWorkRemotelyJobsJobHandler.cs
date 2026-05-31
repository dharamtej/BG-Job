// CareerPandaBL/Background/Handlers/WeWorkRemotelyJobsJobHandler.cs
// SOURCE : WeWorkRemotely public RSS feeds — https://weworkremotely.com/categories/{slug}.rss
// AUTH   : Free, no key.
// STRATEGY: pull each category RSS once, parse <item> blocks, upsert.
using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CareerPanda.DataAccess.DA;
using static CareerPanda.BL.Background.Handlers.JobFetchHelpers;
using CareerPanda.DataAccess.Entities.Api;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CareerPanda.BL.Background.Handlers;

public class WeWorkRemotelyJobsJobHandler : IJobHandler
{
    public string JobType => "WeWorkRemotelyJobs";

    // Public category RSS slugs (stable list maintained by WWR for years).
    private static readonly string[] CategorySlugs =
    [
        "remote-programming-jobs",
        "remote-design-jobs",
        "remote-customer-support-jobs",
        "remote-copywriting-jobs",
        "remote-devops-sysadmin-jobs",
        "remote-business-exec-management-jobs",
        "remote-product-jobs",
        "remote-marketing-jobs",
        "remote-sales-and-marketing-jobs",
        "all-other-remote-jobs",
    ];

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<WeWorkRemotelyJobsJobHandler> _logger;

    public WeWorkRemotelyJobsJobHandler(IServiceScopeFactory scopeFactory, IHttpClientFactory httpClientFactory, ILogger<WeWorkRemotelyJobsJobHandler> logger)
    { _scopeFactory = scopeFactory; _http = httpClientFactory; _logger = logger; }

    public async Task ExecuteAsync(JobWorkRequest request, IJobProgressReporter progress, CancellationToken cancellationToken)
    {
        var http = _http.CreateClient("WWR");

        var run = new ApiJobFetchRun
        {
            Id               = request.JobId,
            BackgroundTaskId = request.JobId,
            JobCategory      = "WeWorkRemotelyJobs",
            ApiSource        = "WeWorkRemotely",
            Status           = "Running",
            StartedAt        = DateTime.UtcNow,
            LocationFilter   = "Remote (Global)",
            CreatedById      = request.UserId
        };
        using (var s = _scopeFactory.CreateScope())
        {
            var da = s.ServiceProvider.GetRequiredService<IJobFetchDA>();
            await da.CreateFetchRunAsync(run);
        }

        int totalFetched = 0, totalInserted = 0, totalUpdated = 0, totalErrors = 0, categoriesDone = 0;
        try
        {
            foreach (var slug in CategorySlugs)
            {
                if (cancellationToken.IsCancellationRequested) break;
                categoriesDone++;
                var url = $"https://weworkremotely.com/categories/{slug}.rss";
                _logger.LogInformation("[WWR] [{N}/{T}] {Slug}", categoriesDone, CategorySlugs.Length, slug);

                List<ApiRawJob> jobs;
                try
                {
                    using var resp = await http.GetAsync(url, cancellationToken);
                    if (!resp.IsSuccessStatusCode) { totalErrors++; continue; }
                    var xml = await resp.Content.ReadAsStringAsync(cancellationToken);
                    jobs = ParseRss(xml, slug, run.Id);
                    totalFetched += jobs.Count;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex) { totalErrors++; _logger.LogWarning(ex, "[WWR] {Slug} failed", slug); continue; }

                if (jobs.Count > 0)
                {
                    using var s = _scopeFactory.CreateScope();
                    var da = s.ServiceProvider.GetRequiredService<IJobFetchDA>();
                    var (ins, upd, err) = await da.BulkUpsertRawJobsAsync(JobValidationGate.FilterValid(jobs, _logger, "[WeWorkRemotely]"), cancellationToken);
                    totalInserted += ins; totalUpdated += upd; totalErrors += err;
                }

                using (var s = _scopeFactory.CreateScope())
                {
                    var da = s.ServiceProvider.GetRequiredService<IJobFetchDA>();
                    await da.UpdateFetchRunStatsAsync(run.Id, totalFetched, totalInserted, totalUpdated, 0, totalErrors, categoriesDone);
                }
                int pct = (int)((double)categoriesDone / CategorySlugs.Length * 90);
                await progress.ReportProgressAsync(pct, $"Categories: {categoriesDone}/{CategorySlugs.Length} — Inserted: {totalInserted}, Updated: {totalUpdated}");
            }

            using (var s = _scopeFactory.CreateScope())
            {
                var da = s.ServiceProvider.GetRequiredService<IJobFetchDA>();
                await da.CompleteFetchRunAsync(run.Id, "Completed");
            }
        }
        catch (OperationCanceledException)
        {
            using var s = _scopeFactory.CreateScope();
            var da = s.ServiceProvider.GetRequiredService<IJobFetchDA>();
            await da.CompleteFetchRunAsync(run.Id, "Cancelled", "Cancelled by user.");
            throw;
        }
        catch (Exception ex)
        {
            using var s = _scopeFactory.CreateScope();
            var da = s.ServiceProvider.GetRequiredService<IJobFetchDA>();
            await da.CompleteFetchRunAsync(run.Id, "Failed", ex.Message);
            throw;
        }

        await progress.ReportProgressAsync(100,
            $"WeWorkRemotely done — Fetched {totalFetched}, Inserted {totalInserted}, Updated {totalUpdated}, Errors {totalErrors}.");
    }

    private static readonly Regex _htmlTag    = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex _whitespace = new(@"\s{2,}", RegexOptions.Compiled);
    private static string? StripHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        var s = _htmlTag.Replace(html, " ");
        s = WebUtility.HtmlDecode(s);
        return _whitespace.Replace(s, " ").Trim();
    }

    // ── RSS parsing ──────────────────────────────────────────────────────────
    // <item> fields: <title>, <link>, <guid>, <pubDate>, <description>(HTML), <category>
    private static List<ApiRawJob> ParseRss(string xml, string categorySlug, string fetchRunId)
    {
        var jobs = new List<ApiRawJob>();
        XDocument doc;
        try { doc = XDocument.Parse(xml); } catch { return jobs; }

        var items = doc.Descendants("item");
        foreach (var it in items)
        {
            var title = (string?)it.Element("title");
            var link  = (string?)it.Element("link");
            var guid  = (string?)it.Element("guid") ?? link;
            var desc  = (string?)it.Element("description");
            var pub   = (string?)it.Element("pubDate");

            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(link) || string.IsNullOrWhiteSpace(guid))
                continue;

            DateTime? postDate = null;
            if (DateTime.TryParse(pub, out var parsedDt)) postDate = parsedDt.ToUniversalTime();

            // WWR titles often look like "Company Name: Senior Engineer" — split on the first colon.
            string? company = null;
            var jobTitle = title!;
            var colonIdx = title!.IndexOf(':');
            if (colonIdx > 0 && colonIdx < title.Length - 1)
            {
                company  = title[..colonIdx].Trim();
                jobTitle = title[(colonIdx + 1)..].Trim();
            }

            jobs.Add(new ApiRawJob
            {
                PublicId        = Guid.NewGuid().ToString("N"),
                Source          = "WeWorkRemotely",
                SourceId        = guid,
                FetchRunId      = fetchRunId,
                JobTitle        = jobTitle,
                JobLink         = link,
                JobDescription  = StripHtml(desc),
                Country         = "US",
                PostDate        = postDate,
                HoursBackPosted = postDate.HasValue ? (int)(DateTime.UtcNow - postDate.Value).TotalHours : null,
                WorkType        = "Remote",
                JobWorkMode     = "Remote",
                ContractType    = "FullTime",
                JobLevel        = NormalizeJobLevel(jobTitle),
                Industry        = categorySlug,
                ApplyType       = "ExternalApply",
                CompanyName     = company,
                CompanyType     = "Private",
                Status          = true,
                CreatedOn       = DateTime.UtcNow,
                UpdatedOn       = DateTime.UtcNow
            });
        }
        return jobs;
    }
}
