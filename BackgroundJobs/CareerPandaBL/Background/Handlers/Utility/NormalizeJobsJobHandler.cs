// CareerPandaBL/Background/Handlers/Utility/NormalizeJobsJobHandler.cs
// Links industry_id and job_role_id on api.raw_jobs rows whose norm_status is 'pending'.
//
// MATCH STRATEGY (in order, short-circuits on first hit):
//   1. Exact alias lookup   — lowercased raw text found verbatim in md.*_aliases
//   2. Keyword contains     — any alias is a substring of the raw text (or vice-versa)
//   3. Mark as 'failed'     — no match; raw text queued for human review via md.*_aliases
//
// norm_status values written:
//   'auto_high'  — exact alias match
//   'auto_low'   — contains / partial match
//   'failed'     — no match found (industry_id / job_role_id left null for that field)
using System.Text.Json;
using CareerPanda.DataAccess.DA;
using CareerPanda.DataAccess.Entities.Api;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CareerPanda.BL.Background.Handlers;

public class NormalizeJobsJobHandler : IJobHandler
{
    public string JobType => "NormalizeJobs";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<NormalizeJobsJobHandler> _logger;

    public NormalizeJobsJobHandler(IServiceScopeFactory scopeFactory, ILogger<NormalizeJobsJobHandler> logger)
    { _scopeFactory = scopeFactory; _logger = logger; }

    private sealed record Input(int BatchSize, int MaxParallel);
    private static Input ParseInput(string? payload)
    {
        int batch = 500, parallel = 4;
        if (!string.IsNullOrWhiteSpace(payload))
        {
            try
            {
                var d = JsonSerializer.Deserialize<JsonElement>(payload);
                if (d.TryGetProperty("BatchSize",   out var b) && b.ValueKind == JsonValueKind.Number) batch    = b.GetInt32();
                if (d.TryGetProperty("MaxParallel", out var p) && p.ValueKind == JsonValueKind.Number) parallel = p.GetInt32();
            } catch { }
        }
        return new Input(Math.Clamp(batch, 1, 5000), Math.Clamp(parallel, 1, 8));
    }

    public async Task ExecuteAsync(JobWorkRequest request, IJobProgressReporter progress, CancellationToken cancellationToken)
    {
        var input = ParseInput(request.InputPayload);

        var run = new CareerPanda.DataAccess.Entities.Api.ApiJobFetchRun
        {
            Id               = request.JobId,
            BackgroundTaskId = request.JobId,
            JobCategory      = "NormalizeJobs",
            ApiSource        = "Normalize",
            Status           = "Running",
            StartedAt        = DateTime.UtcNow,
            CreatedById      = request.UserId
        };
        using (var s = _scopeFactory.CreateScope())
        {
            var da = s.ServiceProvider.GetRequiredService<IJobFetchDA>();
            await da.CreateFetchRunAsync(run);
        }

        // Load alias maps once — they fit in memory (< 1000 rows each)
        Dictionary<string, int> industryMap, roleMap;
        using (var s = _scopeFactory.CreateScope())
        {
            var da = s.ServiceProvider.GetRequiredService<IJobFetchDA>();
            industryMap = await da.GetIndustryAliasMapAsync(cancellationToken);
            roleMap     = await da.GetJobRoleAliasMapAsync(cancellationToken);
        }

        int totalProcessed = 0, totalMatched = 0, totalFailed = 0, batchNo = 0;
        int afterId = 0;

        await progress.ReportProgressAsync(0, "Starting normalization…");

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                List<ApiRawJob> batch;
                using (var s = _scopeFactory.CreateScope())
                {
                    var da = s.ServiceProvider.GetRequiredService<IJobFetchDA>();
                    batch = await da.GetRawJobsForNormalizationAsync(afterId, input.BatchSize, cancellationToken);
                }
                if (batch.Count == 0) break;
                batchNo++;
                afterId = batch[^1].Id;

                var opts = new ParallelOptions { MaxDegreeOfParallelism = input.MaxParallel, CancellationToken = cancellationToken };

                await Parallel.ForEachAsync(batch, opts, async (job, ct) =>
                {
                    try
                    {
                        // Industry: prefer raw industry text from handler; fall back to job_title
                        // (after fixes JSearch/RemoteOK/Arbeitnow now populate Industry from
                        //  their category/tag signal, but title fallback handles any remaining nulls)
                        var (industryId, iConf) = Resolve(job.Industry, industryMap);
                        if (industryId == null)
                            (industryId, iConf) = Resolve(job.JobTitle, industryMap);

                        // Role: always derive from job_title — raw_jobs.role is never populated
                        var (roleId,     rConf) = Resolve(job.JobTitle, roleMap);

                        // Overall confidence: take the lower of the two; if both are null → failed
                        string normStatus;
                        if (industryId == null && roleId == null)
                            normStatus = "failed";
                        else if (iConf == "auto_low" || rConf == "auto_low")
                            normStatus = "auto_low";
                        else
                            normStatus = "auto_high";

                        using var scope = _scopeFactory.CreateScope();
                        var da = scope.ServiceProvider.GetRequiredService<IJobFetchDA>();
                        await da.UpdateJobNormalizationAsync(job.Id, industryId, roleId, normStatus, ct);

                        Interlocked.Increment(ref totalProcessed);
                        if (normStatus != "failed") Interlocked.Increment(ref totalMatched);
                        else                        Interlocked.Increment(ref totalFailed);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref totalFailed);
                        _logger.LogWarning(ex, "[NormalizeJobs] Error for job {Id}", job.Id);
                    }
                });

                using (var s = _scopeFactory.CreateScope())
                {
                    var da = s.ServiceProvider.GetRequiredService<IJobFetchDA>();
                    await da.UpdateFetchRunStatsAsync(run.Id,
                        totalFetched:  totalProcessed,
                        totalInserted: 0,
                        totalUpdated:  totalMatched,
                        totalSkipped:  0,
                        totalErrors:   totalFailed,
                        pagesFetched:  batchNo);
                }

                await progress.ReportProgressAsync(0,
                    $"Batch {batchNo} — processed {totalProcessed}, matched {totalMatched}, unmatched {totalFailed}");
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

        _logger.LogInformation("[NormalizeJobs] Done — processed {P} matched {M} failed {F}", totalProcessed, totalMatched, totalFailed);
        await progress.ReportProgressAsync(100, $"Done — {totalProcessed} processed, {totalMatched} matched, {totalFailed} unmatched.");
    }

    // ── Matching logic ───────────────────────────────────────────────────────

    private static (int? id, string? confidence) Resolve(string? raw, Dictionary<string, int> aliasMap)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (null, null);

        var normalized = raw.Trim().ToLowerInvariant();

        // 1. Exact match
        if (aliasMap.TryGetValue(normalized, out var exact))
            return (exact, "auto_high");

        // 2. The alias is a substring of the raw text (e.g. raw="software engineering" matches alias "software engineer")
        foreach (var (alias, id) in aliasMap)
        {
            if (normalized.Contains(alias) || alias.Contains(normalized))
                return (id, "auto_low");
        }

        return (null, null);
    }
}
