// CareerPandaBL/Background/Handlers/RunAllJobsJobHandler.cs
// META-HANDLER : runs every job-fetch handler sequentially in one task.
// FLOW : ATS board-token sources first (Greenhouse → … → Recruitee),
//        then the API/query sources (AllJobs/JSearch, Adzuna, …),
//        and finally H1B sponsor enrichment (operates on already-fetched rows).
//        Each child runs to completion before the next starts, and each writes
//        its own api.job_fetch_runs row (fresh JobId) so it shows up individually
//        under GET /api/fetchjobs/runs. A failure in one child is logged and the
//        chain continues with the next.
using CareerPanda.DataAccess.DA;
using CareerPanda.DataAccess.Entities.Api;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CareerPanda.BL.Background.Handlers;

public class RunAllJobsJobHandler : IJobHandler
{
    public string JobType => "RunAllJobs";

    // Execution order — one entry per child handler JobType.
    // Unregistered types are skipped gracefully (logged, chain continues).
    private static readonly string[] ChildJobTypes =
    [
        // ── ATS board-token sources (each internally parallel) ──────────────
        // Greenhouse is intentionally last among ATS — it has the largest token
        // set (17K+) and the heaviest per-board cost (N+1 detail fetches).
        "LeverJobs",
        "AshbyJobs",
        "WorkdayJobs",
        "RecruiteeJobs",
        "BambooHRJobs",
        "iCIMSJobs",
        "GreenhouseJobs",
        // ── Free / unlimited API sources ────────────────────────────────────
        // JSearch (JSearchJobs) excluded — rate-limited quota, run manually.
        "AdzunaJobs",         // Adzuna — free, broad sweep
        "UsaJobs",            // USAJobs.gov — 2 sweeps: Government + University
        "TheMuseJobs",        // The Muse — 2 sweeps: Startup + NonProfit
        "RemoteOkJobs",       // RemoteOK — free
        "JobicyJobs",         // Jobicy — free, geo=usa
        "RemotiveJobs",       // Remotive — free, US-friendly filter
        "WeWorkRemotelyJobs", // WeWorkRemotely — free RSS
        "ArbeitnowJobs",      // Arbeitnow — US+remote filter applied
        // ── Post-processing — runs after every fetch refreshes classification flags
        "ReclassifyExisting",
        // H1BSponsorEnrichment and CompanyEnrichment stay manual-only.
    ];

    // Resolve handlers lazily (not via constructor) — injecting IEnumerable<IJobHandler>
    // here would create a circular dependency, since this type is itself an IJobHandler.
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<RunAllJobsJobHandler> _logger;

    public RunAllJobsJobHandler(
        IServiceProvider serviceProvider,
        ILogger<RunAllJobsJobHandler> logger)
    {
        _serviceProvider = serviceProvider;
        _logger          = logger;
    }

    // ── Run-once guard ───────────────────────────────────────────────────────
    // Prevents a second chain from starting if one is already in progress (e.g.
    // a scheduled trigger firing while a previous run is still going). Without
    // this you can pile up parallel chains and exhaust API quotas + DB pool.
    private static int _running;

    public async Task ExecuteAsync(
        JobWorkRequest request,
        IJobProgressReporter progress,
        CancellationToken cancellationToken)
    {
        // Atomic 0→1 swap: only one chain owns the slot at a time.
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            _logger.LogWarning("[RunAll] Skipped — another Run All chain is already in progress.");
            await progress.ReportProgressAsync(100, "Skipped — another Run All chain is already running.");
            return;
        }

        var handlers   = _serviceProvider.GetServices<IJobHandler>();
        int total      = ChildJobTypes.Length;
        int succeeded  = 0, failed = 0, skipped = 0;

        // Parent fetch_run so the chain appears as a single row in Run History with
        // aggregated counts. Each child still gets its own row separately.
        var chainRun = new ApiJobFetchRun
        {
            Id               = request.JobId,
            BackgroundTaskId = request.JobId,
            JobCategory      = "RunAllJobs",
            ApiSource        = "Chain",
            Status           = "Running",
            StartedAt        = DateTime.UtcNow,
            MaxPages         = total,
            LocationFilter   = "Chain",
            CreatedById      = request.UserId
        };
        using (var s = _serviceProvider.CreateScope())
        {
            var da = s.ServiceProvider.GetRequiredService<IJobFetchDA>();
            await da.CreateFetchRunAsync(chainRun);
        }

        await progress.ReportProgressAsync(0, $"Starting job chain — {total} jobs queued.");
        try
        {

        for (int i = 0; i < ChildJobTypes.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var childType = ChildJobTypes[i];
            var handler   = handlers.FirstOrDefault(h =>
                h.JobType.Equals(childType, StringComparison.OrdinalIgnoreCase));

            if (handler == null)
            {
                skipped++;
                _logger.LogWarning("[RunAll] No handler registered for '{Child}' — skipping", childType);
                continue;
            }

            // Each child gets its own JobId so it creates an independent fetch_run row.
            var childRequest = new JobWorkRequest
            {
                JobId        = Guid.NewGuid().ToString(),
                UserId       = request.UserId,
                JobType      = childType,
                InputPayload = request.InputPayload   // pass caller's input through to each child
            };

            // Remap the child's 0-100 progress into its slice of the overall chain.
            var childProgress = new SliceProgressReporter(progress, i, total, childType);

            _logger.LogInformation("[RunAll] [{N}/{Total}] Starting {Child} (RunId={RunId})",
                i + 1, total, childType, childRequest.JobId);

            // Per-child linked CTS — fires when either the parent chain is cancelled OR the user
            // cancels this specific child by its runId from the dashboard.
            using var childCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var cancelReg = _serviceProvider.GetRequiredService<JobCancellationRegistry>();
            cancelReg.Register(childRequest.JobId, childCts);
            try
            {
                await handler.ExecuteAsync(childRequest, childProgress, childCts.Token);
                succeeded++;
                _logger.LogInformation("[RunAll] [{N}/{Total}] Completed {Child}", i + 1, total, childType);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("[RunAll] Parent cancelled during {Child} — stopping chain", childType);
                throw;
            }
            catch (OperationCanceledException)
            {
                // Only THIS child was cancelled — record as failure but continue to the next.
                failed++;
                _logger.LogWarning("[RunAll] {Child} cancelled by user — continuing with next", childType);
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogError(ex, "[RunAll] {Child} failed — continuing with next", childType);
            }
            finally { cancelReg.Remove(childRequest.JobId); }

            int pct = (int)((double)(i + 1) / total * 100);
            await progress.ReportProgressAsync(pct,
                $"[{i + 1}/{total}] {childType} done — Succeeded: {succeeded}, Failed: {failed}, Skipped: {skipped}");

            // Live stats update on the chain's own fetch_run so the dashboard's Run History reflects progress.
            using var liveScope = _serviceProvider.CreateScope();
            var liveDa = liveScope.ServiceProvider.GetRequiredService<IJobFetchDA>();
            await liveDa.UpdateFetchRunStatsAsync(chainRun.Id,
                totalFetched:  i + 1,
                totalInserted: succeeded,
                totalUpdated:  0,
                totalSkipped:  skipped,
                totalErrors:   failed,
                pagesFetched:  i + 1);
        }

            _logger.LogInformation(
                "[RunAll] Chain done — Total={T} Succeeded={S} Failed={F} Skipped={K}",
                total, succeeded, failed, skipped);

            using (var doneScope = _serviceProvider.CreateScope())
            {
                var doneDa = doneScope.ServiceProvider.GetRequiredService<IJobFetchDA>();
                await doneDa.UpdateFetchRunStatsAsync(chainRun.Id, total, succeeded, 0, skipped, failed, total);
                await doneDa.CompleteFetchRunAsync(chainRun.Id, "Completed");
            }

            await progress.ReportProgressAsync(100,
                $"Job chain complete — {succeeded}/{total} succeeded, {failed} failed, {skipped} skipped.");
        }
        catch (OperationCanceledException)
        {
            using var s = _serviceProvider.CreateScope();
            var da = s.ServiceProvider.GetRequiredService<IJobFetchDA>();
            await da.UpdateFetchRunStatsAsync(chainRun.Id, total, succeeded, 0, skipped, failed, total);
            await da.CompleteFetchRunAsync(chainRun.Id, "Cancelled", "Cancelled by user.");
            throw;
        }
        catch (Exception ex)
        {
            using var s = _serviceProvider.CreateScope();
            var da = s.ServiceProvider.GetRequiredService<IJobFetchDA>();
            await da.CompleteFetchRunAsync(chainRun.Id, "Failed", ex.Message);
            throw;
        }
        finally
        {
            // Release the run-once slot so the next scheduled fire (or a manual trigger) can proceed.
            Interlocked.Exchange(ref _running, 0);
        }
    }

    // Maps a child handler's 0-100 progress onto [slice * index, slice * (index+1)]
    // of the parent task's progress bar, so the overall percentage advances smoothly.
    private sealed class SliceProgressReporter : IJobProgressReporter
    {
        private readonly IJobProgressReporter _parent;
        private readonly int    _index;
        private readonly int    _total;
        private readonly string _childType;

        public SliceProgressReporter(IJobProgressReporter parent, int index, int total, string childType)
        {
            _parent    = parent;
            _index     = index;
            _total     = total;
            _childType = childType;
        }

        public Task ReportProgressAsync(int percent, string? message = null)
        {
            double sliceStart = (double)_index / _total * 100;
            double sliceSize  = 100.0 / _total;
            int    overall    = (int)(sliceStart + sliceSize * (Math.Clamp(percent, 0, 100) / 100.0));
            return _parent.ReportProgressAsync(overall, $"[{_index + 1}/{_total}] {_childType}: {message}");
        }
    }
}
