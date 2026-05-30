// CareerPandaBL/Background/Handlers/ReclassifyExistingJobsJobHandler.cs
// One-time / on-demand backfill that re-runs JobClassifier across every existing
// row in api.raw_jobs. Updates ONLY the 12 classification flag columns; nothing
// else is touched. Idempotent — safe to re-run after classifier changes.
using System.Text.Json;
using CareerPanda.DataAccess.DA;
using CareerPanda.DataAccess.Entities.Api;
using CareerPanda.DataAccess.Util;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CareerPanda.BL.Background.Handlers;

public class ReclassifyExistingJobsJobHandler : IJobHandler
{
    public string JobType => "ReclassifyExisting";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ReclassifyExistingJobsJobHandler> _logger;

    public ReclassifyExistingJobsJobHandler(IServiceScopeFactory scopeFactory, ILogger<ReclassifyExistingJobsJobHandler> logger)
    { _scopeFactory = scopeFactory; _logger = logger; }

    private sealed record Input(int BatchSize, int MaxParallel);
    private static Input ParseInput(string? payload)
    {
        int batch = 500, parallel = 8;
        if (!string.IsNullOrWhiteSpace(payload))
        {
            try
            {
                var d = JsonSerializer.Deserialize<JsonElement>(payload);
                if (d.TryGetProperty("BatchSize",   out var b) && b.ValueKind == JsonValueKind.Number) batch    = b.GetInt32();
                if (d.TryGetProperty("MaxParallel", out var p) && p.ValueKind == JsonValueKind.Number) parallel = p.GetInt32();
            } catch { }
        }
        return new Input(Math.Clamp(batch, 1, 5000), Math.Clamp(parallel, 1, 16));
    }

    public async Task ExecuteAsync(JobWorkRequest request, IJobProgressReporter progress, CancellationToken cancellationToken)
    {
        var input = ParseInput(request.InputPayload);

        // Visible in Run History.
        var run = new ApiJobFetchRun
        {
            Id               = request.JobId,
            BackgroundTaskId = request.JobId,
            JobCategory      = "ReclassifyExisting",
            ApiSource        = "Reclassify",
            Status           = "Running",
            StartedAt        = DateTime.UtcNow,
            CreatedById      = request.UserId
        };
        using (var s = _scopeFactory.CreateScope())
        {
            var da = s.ServiceProvider.GetRequiredService<IJobFetchDA>();
            await da.CreateFetchRunAsync(run);
        }

        int totalProcessed = 0, totalChanged = 0, totalErrors = 0;
        int afterId = 0, batchNo = 0;
        using var progressLock = new SemaphoreSlim(1, 1);
        var parallelOpts = new ParallelOptions { MaxDegreeOfParallelism = input.MaxParallel, CancellationToken = cancellationToken };

        await progress.ReportProgressAsync(0, "Starting reclassification…");

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                List<ApiRawJob> batch;
                using (var loadScope = _scopeFactory.CreateScope())
                {
                    var da = loadScope.ServiceProvider.GetRequiredService<IJobFetchDA>();
                    batch = await da.GetRawJobsForReclassifyAsync(afterId, input.BatchSize, cancellationToken);
                }
                if (batch.Count == 0) break;
                batchNo++;
                afterId = batch[^1].Id;

                await Parallel.ForEachAsync(batch, parallelOpts, async (row, ct) =>
                {
                    try
                    {
                        // Snapshot all current flag values so we can tell whether anything changed.
                        bool? oC2C = row.IsC2C, oC2H = row.IsContractToHire, o1099 = row.IsFreelanceJob, oW2 = row.IsW2,
                              oContract = row.IsContractJob, oPV = row.IsPrimeVendor, oStaff = row.IsStaffing,
                              oH1B = row.IsH1BSponsored, oSponsored = row.IsSponsored,
                              oOptCpt = row.IsOptCpt, oTn = row.IsTnVisa, oE3 = row.IsE3Visa,
                              oJ1 = row.IsJ1Visa, oGc = row.IsGreenCard,
                              oStartup = row.IsStartupJob, oNonProfit = row.IsNonProfitJob, oUni = row.IsUniversityJob,
                              oClearance = row.IsSecurityClearanceRequired, oVet = row.IsVeteransEligible;
                        var oLevel = row.JobLevel;

                        JobClassifier.ApplyKeywordFlags(row, row.JobDescription, employmentType: row.ContractType, companyName: row.CompanyName);

                        // Normalize JobLevel to standard tier if not already in canonical form.
                        var normalized = JobFetchHelpers.NormalizeJobLevel(row.JobLevel);
                        if (normalized != null) row.JobLevel = normalized;

                        bool changed =
                            oC2C       != row.IsC2C            || oC2H      != row.IsContractToHire ||
                            o1099      != row.IsFreelanceJob   || oW2       != row.IsW2 ||
                            oContract  != row.IsContractJob    || oPV       != row.IsPrimeVendor ||
                            oStaff     != row.IsStaffing       || oH1B      != row.IsH1BSponsored ||
                            oSponsored != row.IsSponsored      || oOptCpt   != row.IsOptCpt ||
                            oTn        != row.IsTnVisa         || oE3       != row.IsE3Visa ||
                            oJ1        != row.IsJ1Visa         || oGc        != row.IsGreenCard ||
                            oStartup   != row.IsStartupJob     || oNonProfit != row.IsNonProfitJob ||
                            oUni       != row.IsUniversityJob  || oClearance != row.IsSecurityClearanceRequired ||
                            oVet       != row.IsVeteransEligible || oLevel  != row.JobLevel;

                        if (!changed) return;

                        using var uScope = _scopeFactory.CreateScope();
                        var uDa = uScope.ServiceProvider.GetRequiredService<IJobFetchDA>();
                        await uDa.UpdateClassificationFlagsAsync(row, ct);
                        Interlocked.Increment(ref totalChanged);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex) { Interlocked.Increment(ref totalErrors); _logger.LogWarning(ex, "[Reclassify] Failed for row {Id}", row.Id); }
                });

                Interlocked.Add(ref totalProcessed, batch.Count);

                // Persist stats every batch so the dashboard ticks.
                await progressLock.WaitAsync(cancellationToken);
                try
                {
                    using var sScope = _scopeFactory.CreateScope();
                    var sDa = sScope.ServiceProvider.GetRequiredService<IJobFetchDA>();
                    await sDa.UpdateFetchRunStatsAsync(run.Id,
                        totalFetched:  totalProcessed,
                        totalInserted: 0,
                        totalUpdated:  totalChanged,
                        totalSkipped:  totalProcessed - totalChanged - totalErrors,
                        totalErrors:   totalErrors,
                        pagesFetched:  batchNo);
                    await progress.ReportProgressAsync(0, $"Batch {batchNo} — processed {totalProcessed}, updated {totalChanged}, errors {totalErrors}");
                }
                finally { progressLock.Release(); }
            }

            using (var doneScope = _scopeFactory.CreateScope())
            {
                var doneDa = doneScope.ServiceProvider.GetRequiredService<IJobFetchDA>();
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

        _logger.LogInformation("[Reclassify] Done — processed {P} updated {U} errors {E}", totalProcessed, totalChanged, totalErrors);
        await progress.ReportProgressAsync(100, $"Done — processed {totalProcessed}, updated {totalChanged}, errors {totalErrors}.");
    }
}
