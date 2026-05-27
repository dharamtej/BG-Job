// CareerPandaBL/Background/Handlers/RunAllJobsJobHandler.cs
// META-HANDLER : runs every job-fetch handler sequentially in one task.
// FLOW : ATS board-token sources first (Greenhouse → … → Recruitee),
//        then the API/query sources (AllJobs/JSearch, Adzuna, …),
//        and finally H1B sponsor enrichment (operates on already-fetched rows).
//        Each child runs to completion before the next starts, and each writes
//        its own api.job_fetch_runs row (fresh JobId) so it shows up individually
//        under GET /api/fetchjobs/runs. A failure in one child is logged and the
//        chain continues with the next.
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
        "IcimsJobs",
        "BambooHrJobs",
        "RecruiteeJobs",
        "GreenhouseJobs",
        // ── Free / unlimited API sources ────────────────────────────────────
        // JSearch-backed sources (AllJobs, ContractJobs, H1BJobs, PrimeVendorJobs)
        // are intentionally excluded — they share a rate-limited quota.
        "AdzunaJobs",       // Adzuna — free
        "GovernmentJobs",   // USAJobs.gov — free
        "RemoteOkJobs",     // RemoteOK — free
        "JobicyJobs",       // Jobicy — free
        "StartupJobs",      // The Muse (company_size=Startup,Small) — free
        "NonProfitJobs",    // The Muse (company_size=Non-Profit) — free
        // ── Post-processing — must run after fetches populate raw_jobs ───────
        "H1BSponsorEnrichment",   // Wikipedia lookups — free
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

    public async Task ExecuteAsync(
        JobWorkRequest request,
        IJobProgressReporter progress,
        CancellationToken cancellationToken)
    {
        var handlers   = _serviceProvider.GetServices<IJobHandler>();
        int total      = ChildJobTypes.Length;
        int succeeded  = 0, failed = 0, skipped = 0;

        await progress.ReportProgressAsync(0, $"Starting job chain — {total} jobs queued.");

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

            try
            {
                await handler.ExecuteAsync(childRequest, childProgress, cancellationToken);
                succeeded++;
                _logger.LogInformation("[RunAll] [{N}/{Total}] Completed {Child}", i + 1, total, childType);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("[RunAll] Cancelled during {Child} — stopping chain", childType);
                throw;
            }
            catch (Exception ex)
            {
                // One child failing must not abort the whole chain — log and move on.
                failed++;
                _logger.LogError(ex, "[RunAll] {Child} failed — continuing with next", childType);
            }

            int pct = (int)((double)(i + 1) / total * 100);
            await progress.ReportProgressAsync(pct,
                $"[{i + 1}/{total}] {childType} done — Succeeded: {succeeded}, Failed: {failed}, Skipped: {skipped}");
        }

        _logger.LogInformation(
            "[RunAll] Chain done — Total={T} Succeeded={S} Failed={F} Skipped={K}",
            total, succeeded, failed, skipped);

        await progress.ReportProgressAsync(100,
            $"Job chain complete — {succeeded}/{total} succeeded, {failed} failed, {skipped} skipped.");
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
