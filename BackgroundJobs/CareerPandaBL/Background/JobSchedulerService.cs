// CareerPandaBL/Background/JobSchedulerService.cs
// Bridges CareerPanda BackgroundTask schedules with Hangfire recurring jobs.
// Each scheduled BackgroundTask gets one Hangfire recurring job keyed by task ID.
// When Hangfire fires, a fresh run task is created and queued through the existing pipeline.
using CareerPanda.DataAccess.DA;
using CareerPanda.DataAccess.Entities.Api;
using CareerPanda.Framework;
using CareerPanda.Framework.Util;
using Hangfire;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CareerPanda.BL.Background;

public class JobSchedulerService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IRecurringJobManager _recurringJobs;
    private readonly ILogger<JobSchedulerService> _logger;

    public JobSchedulerService(
        IServiceScopeFactory scopeFactory,
        IRecurringJobManager recurringJobs,
        ILogger<JobSchedulerService> logger)
    {
        _scopeFactory  = scopeFactory;
        _recurringJobs = recurringJobs;
        _logger        = logger;
    }

    // ── Called on startup to re-register all persisted schedules ─────────────

    public async Task RegisterAllAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var da = scope.ServiceProvider.GetRequiredService<IBackgroundTaskDA>();
        var tasks = await da.GetScheduledTasksAsync();

        foreach (var task in tasks)
            RegisterJob(task);

        _logger.LogInformation("[Scheduler] Registered {Count} recurring jobs from DB", tasks.Count);
    }

    // ── Register or update a Hangfire recurring job for one task ─────────────

    public void RegisterJob(BackgroundTask task)
    {
        var cron = ToCron(task);
        if (cron == null)
        {
            _recurringJobs.RemoveIfExists(task.Id);
            return;
        }

        _recurringJobs.AddOrUpdate<JobSchedulerService>(
            recurringJobId: task.Id,
            methodCall:     s => s.ExecuteScheduledJobAsync(task.Id),
            cronExpression: cron,
            options:        new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        _logger.LogInformation("[Scheduler] Registered job '{Id}' ({Name}) → cron '{Cron}'",
            task.Id, task.Name, cron);
    }

    // ── Remove a recurring Hangfire job ──────────────────────────────────────

    public void RemoveJob(string taskId)
    {
        _recurringJobs.RemoveIfExists(taskId);
        _logger.LogInformation("[Scheduler] Removed recurring job '{Id}'", taskId);
    }

    // ── Hangfire calls this method on each firing ─────────────────────────────

    [AutomaticRetry(Attempts = 0)]
    public async Task ExecuteScheduledJobAsync(string templateTaskId)
    {
        using var scope = _scopeFactory.CreateScope();
        var da    = scope.ServiceProvider.GetRequiredService<IBackgroundTaskDA>();
        var queue = scope.ServiceProvider.GetRequiredService<IBackgroundJobQueue>();
        var exec  = scope.ServiceProvider.GetRequiredService<JobExecutionService>();
        var reg   = scope.ServiceProvider.GetRequiredService<JobCancellationRegistry>();

        var templateResponse = await da.GetJobAsync(templateTaskId);
        if (templateResponse.Entity is not BackgroundTask template)
        {
            _logger.LogWarning("[Scheduler] Template task '{Id}' not found — skipping", templateTaskId);
            return;
        }

        // Create a fresh run task derived from the template
        var runId = Guid.NewGuid().ToString();
        var run = new BackgroundTask
        {
            Id          = runId,
            Name        = template.Name,
            Description = template.Description,
            JobType     = template.JobType,
            Status      = JobStatus.Pending,
            CreatedById = template.CreatedById,
            CreatedAt   = DateTime.UtcNow,
            UpdatedAt   = DateTime.UtcNow
        };

        await da.CreateJobAsync(run);
        await da.UpdateLastScheduledRunAsync(templateTaskId, DateTime.UtcNow);

        var workRequest = new JobWorkRequest
        {
            JobId        = runId,
            UserId       = template.CreatedById ?? "scheduler",
            JobType      = template.JobType,
            InputPayload = template.Description
        };

        var cts = reg.Register(runId);

        await queue.QueueBackgroundWorkItemAsync(async workerCt =>
        {
            ApplicationContext.UserId       = workRequest.UserId;
            ApplicationContext.CorrelationId = runId;
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(workerCt, cts.Token);
                await exec.ProcessAsync(workRequest, linked.Token);
            }
            finally
            {
                reg.Remove(runId);
            }
        });

        _logger.LogInformation("[Scheduler] Fired scheduled job — template='{T}' run='{R}' type='{Type}'",
            templateTaskId, runId, template.JobType);
    }

    // ── Cron helpers ──────────────────────────────────────────────────────────

    private static string? ToCron(BackgroundTask task) => task.ScheduleType switch
    {
        "Daily"    => DailyCron(task.ScheduleDailyTime),
        "Interval" => IntervalCron(task.ScheduleIntervalHours),
        _          => null
    };

    private static string? DailyCron(TimeSpan? time)
    {
        if (time == null) return null;
        // e.g. 02:30 → "30 2 * * *"
        return $"{time.Value.Minutes} {time.Value.Hours} * * *";
    }

    private static string? IntervalCron(int? hours)
    {
        if (hours == null || hours <= 0) return null;
        return hours switch
        {
            1  => "0 * * * *",           // every hour
            2  => "0 */2 * * *",
            3  => "0 */3 * * *",
            4  => "0 */4 * * *",
            6  => "0 */6 * * *",
            8  => "0 */8 * * *",
            12 => "0 */12 * * *",
            24 => "0 0 * * *",           // daily at midnight
            _  => $"0 */{hours} * * *"   // best-effort for other values
        };
    }
}
