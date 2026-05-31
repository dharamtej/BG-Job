// CareerPandaBL/Background/Handlers/RemotiveJobsJobHandler.cs
// SOURCE : Remotive public API — https://remotive.com/api/remote-jobs
// AUTH   : Free, no API key required. Attribution recommended (link back to remotive.com).
// COVERAGE: Contract / Freelance / Full-Time remote roles, mostly tech.
// Returns the full active job list (typically a few thousand rows) in a single response.
using System.Net.Http.Json;
using System.Text.Json;
using CareerPanda.DataAccess.DA;
using static CareerPanda.BL.Background.Handlers.JobFetchHelpers;
using CareerPanda.DataAccess.Entities.Api;
using CareerPanda.DataAccess.Util;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CareerPanda.BL.Background.Handlers;

public class RemotiveJobsJobHandler : IJobHandler
{
    public string JobType => "RemotiveJobs";

    // Remotive's candidate_required_location is a free-text string. Accept anything that
    // looks US-friendly. Empty/null is treated as "Worldwide" → accepted.
    private static readonly string[] UsFriendlyLocationHints =
    [
        "usa", "united states", "u.s.", "us only", "us-only", "us residents",
        "anywhere", "worldwide", "global", "americas", "north america", "remote"
    ];

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<RemotiveJobsJobHandler> _logger;

    public RemotiveJobsJobHandler(IServiceScopeFactory scopeFactory, IHttpClientFactory httpClientFactory, ILogger<RemotiveJobsJobHandler> logger)
    { _scopeFactory = scopeFactory; _http = httpClientFactory; _logger = logger; }

    public async Task ExecuteAsync(JobWorkRequest request, IJobProgressReporter progress, CancellationToken cancellationToken)
    {
        var http = _http.CreateClient("Remotive");

        // Fetch-run row so the dashboard sees this run end-to-end.
        var run = new ApiJobFetchRun
        {
            Id               = request.JobId,
            BackgroundTaskId = request.JobId,
            JobCategory      = "RemotiveJobs",
            ApiSource        = "Remotive",
            Status           = "Running",
            StartedAt        = DateTime.UtcNow,
            LocationFilter   = "Remote (US-friendly)",
            CreatedById      = request.UserId
        };
        using (var s = _scopeFactory.CreateScope())
        {
            var da = s.ServiceProvider.GetRequiredService<IJobFetchDA>();
            await da.CreateFetchRunAsync(run);
        }

        int totalFetched = 0, totalInserted = 0, totalUpdated = 0, totalErrors = 0;
        try
        {
            await progress.ReportProgressAsync(5, "Fetching Remotive jobs…");

            // Single call — Remotive returns the whole active set.
            using var resp = await http.GetAsync("https://remotive.com/api/remote-jobs?limit=10000", cancellationToken);
            resp.EnsureSuccessStatusCode();
            var doc = await ReadJsonAsync(resp.Content, cancellationToken);
            if (!doc.TryGetProperty("jobs", out var jobsEl) || jobsEl.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("Remotive response has no 'jobs' array.");

            var jobs = new List<ApiRawJob>(jobsEl.GetArrayLength());
            foreach (var j in jobsEl.EnumerateArray())
            {
                if (cancellationToken.IsCancellationRequested) break;
                var mapped = MapJob(j, run.Id);
                if (mapped != null) jobs.Add(mapped);
            }
            totalFetched = jobs.Count;
            _logger.LogInformation("[Remotive] Mapped {Count} US-friendly remote jobs", totalFetched);

            await progress.ReportProgressAsync(60, $"Upserting {totalFetched} jobs…");

            if (jobs.Count > 0)
            {
                using var s = _scopeFactory.CreateScope();
                var da = s.ServiceProvider.GetRequiredService<IJobFetchDA>();
                var (ins, upd, err) = await da.BulkUpsertRawJobsAsync(JobValidationGate.FilterValid(jobs, _logger, "[Remotive]"), cancellationToken);
                totalInserted = ins; totalUpdated = upd; totalErrors = err;
            }

            using (var doneScope = _scopeFactory.CreateScope())
            {
                var doneDa = doneScope.ServiceProvider.GetRequiredService<IJobFetchDA>();
                await doneDa.UpdateFetchRunStatsAsync(run.Id, totalFetched, totalInserted, totalUpdated, 0, totalErrors, 1);
                await doneDa.CompleteFetchRunAsync(run.Id, "Completed");
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
            $"Remotive done — Fetched {totalFetched}, Inserted {totalInserted}, Updated {totalUpdated}, Errors {totalErrors}.");
    }

    // ── Field mapping ────────────────────────────────────────────────────────
    private static ApiRawJob? MapJob(JsonElement d, string fetchRunId)
    {
        // US-friendly location filter
        var loc = d.TryGetProperty("candidate_required_location", out var lEl) ? lEl.GetString() ?? "" : "";
        var lower = loc.ToLowerInvariant();
        bool usFriendly = string.IsNullOrWhiteSpace(loc) ||
            UsFriendlyLocationHints.Any(h => lower.Contains(h, StringComparison.OrdinalIgnoreCase));
        if (!usFriendly) return null;

        var sourceId = d.TryGetProperty("id", out var idEl)
            ? (idEl.ValueKind == JsonValueKind.Number ? idEl.GetInt64().ToString() : idEl.GetString())
            : null;
        if (string.IsNullOrEmpty(sourceId)) return null;

        var title = d.TryGetProperty("title", out var t) ? t.GetString() ?? "Untitled" : "Untitled";
        var url   = d.TryGetProperty("url",   out var u) ? u.GetString() : null;
        var desc  = d.TryGetProperty("description", out var dEl) ? StripHtml(dEl.GetString()) : null;
        var company   = d.TryGetProperty("company_name", out var cn) ? cn.GetString() : null;
        var companyLg = d.TryGetProperty("company_logo_url", out var cl) ? cl.GetString() : null;
        var category  = d.TryGetProperty("category", out var cat) ? cat.GetString() : null;
        var salaryRaw = d.TryGetProperty("salary", out var sal) && sal.ValueKind == JsonValueKind.String
            ? sal.GetString() : null;
        var (salMin, salMax, salPeriod) = SalaryParser.Parse(salaryRaw);
        var salText = salaryRaw;
        if (salMin.HasValue && salMax.HasValue)
        {
            var suffix = salPeriod switch { "Annual" => "/yr", "Monthly" => "/mo", "Hourly" => "/hr", "Weekly" => "/wk", _ => "" };
            salText = $"${salMin:N0}–${salMax:N0}{suffix}";
        }

        DateTime? postDate = null;
        if (d.TryGetProperty("publication_date", out var pd) && pd.ValueKind == JsonValueKind.String
            && DateTime.TryParse(pd.GetString(), out var parsedDt))
            postDate = parsedDt.ToUniversalTime();

        // job_type: full_time | contract | freelance | part_time | internship | other
        var jobType = d.TryGetProperty("job_type", out var jt) ? jt.GetString() ?? "" : "";
        var jt2 = jobType.ToLowerInvariant();
        bool isContract  = jt2.Contains("contract");
        bool isFreelance = jt2.Contains("freelance");
        bool isIntern    = jt2.Contains("intern");
        string contractType = jt2 switch
        {
            "full_time" => "FullTime",
            "part_time" => "PartTime",
            "contract"  => "Contract",
            "freelance" => "Contract",
            "internship" => "Internship",
            _ => "FullTime"
        };

        string[]? skills = null;
        if (d.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
        {
            skills = tags.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString()!)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToArray();
            if (skills.Length == 0) skills = null;
        }

        return new ApiRawJob
        {
            PublicId        = Guid.NewGuid().ToString("N"),
            Source          = "Remotive",
            SourceId        = sourceId,
            FetchRunId      = fetchRunId,
            JobTitle        = title,
            JobLink         = url,
            JobDescription  = desc,
            City            = null,
            State           = null,
            Country         = "US",
            PostDate        = postDate,
            HoursBackPosted = postDate.HasValue ? (int)(DateTime.UtcNow - postDate.Value).TotalHours : null,
            SalaryMin       = salMin,
            SalaryMax       = salMax,
            SalaryType      = salPeriod,
            SalaryRangeText = salText,
            SalaryCurrency  = "USD",
            WorkType        = "Remote",
            JobWorkMode     = "Remote",
            ContractType    = contractType,
            JobLevel        = NormalizeJobLevel(title),
            Industry        = category,
            Skills          = skills,
            ApplyType       = "ExternalApply",
            CompanyName     = company,
            CompanyUrl      = null,
            CompanyLogoUrl  = companyLg,
            CompanyType     = "Private",
            IsContractJob   = isContract,
            IsFreelanceJob  = isFreelance,
            IsUniversityJob = false,
            IsW2            = null,
            IsC2C           = false,
            IsPrimeVendor   = false,
            IsStaffing      = false,
            IsStartupJob    = false,
            IsNonProfitJob  = false,
            IsH1BSponsored  = false,
            IsOptCpt        = false,
            IsTnVisa        = false,
            IsE3Visa        = false,
            IsJ1Visa        = false,
            IsGreenCard     = false,
            IsSponsored     = false,
            Status          = true,
            CreatedOn       = DateTime.UtcNow,
            UpdatedOn       = DateTime.UtcNow
        };
        // JobClassifier runs inside BulkUpsertRawJobsAsync — will refine flags from the description.
    }
}
