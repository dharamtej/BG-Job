using CareerPanda.DataAccess.DA;
using CareerPanda.DataAccess.Entities.Api;
using CareerPanda.Framework.Util;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CareerPanda.BL.Background;

public class JobExecutionService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IEnumerable<IJobHandler> _handlers;
    private readonly ILogger<JobExecutionService> _logger;

    public JobExecutionService(
        IServiceScopeFactory scopeFactory,
        IEnumerable<IJobHandler> handlers,
        ILogger<JobExecutionService> logger)
    {
        _scopeFactory = scopeFactory;
        _handlers = handlers;
        _logger = logger;
    }

    public async Task ProcessAsync(JobWorkRequest request, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var jobDa = scope.ServiceProvider.GetRequiredService<IBackgroundTaskDA>();

        var handler = _handlers.FirstOrDefault(h =>
            h.JobType.Equals(request.JobType, StringComparison.OrdinalIgnoreCase))
            ?? _handlers.FirstOrDefault(h => h.JobType.Equals("Default", StringComparison.OrdinalIgnoreCase));

        if (handler == null)
        {
            await jobDa.UpdateJobStatusAsync(
                request.JobId,
                JobStatus.Failed,
                errorMessage: $"No handler registered for job type '{request.JobType}'.");
            return;
        }

        var reporter = new JobProgressReporter(jobDa, request.JobId);

        try
        {
            await jobDa.UpdateJobStatusAsync(request.JobId, JobStatus.InProcess, progressPercent: 0);
            await handler.ExecuteAsync(request, reporter, cancellationToken);
            await jobDa.UpdateJobStatusAsync(
                request.JobId,
                JobStatus.Completed,
                progressPercent: 100,
                resultPayload: "Job completed successfully.");
        }
        catch (OperationCanceledException)
        {
            var current = await jobDa.GetJobAsync(request.JobId);
            if (current.Entity is BackgroundTask j && j.Status != JobStatus.Cancelled)
            {
                await jobDa.UpdateJobStatusAsync(
                    request.JobId,
                    JobStatus.Cancelled,
                    errorMessage: "Job was cancelled.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Background job {JobId} failed", request.JobId);
            await jobDa.UpdateJobStatusAsync(
                request.JobId,
                JobStatus.Failed,
                errorMessage: ex.Message);
        }
    }

    private sealed class JobProgressReporter : IJobProgressReporter
    {
        private readonly IBackgroundTaskDA _jobDa;
        private readonly string _jobId;

        public JobProgressReporter(IBackgroundTaskDA jobDa, string jobId)
        {
            _jobDa = jobDa;
            _jobId = jobId;
        }

        public Task ReportProgressAsync(int percent, string? message = null) =>
            _jobDa.UpdateJobStatusAsync(
                _jobId,
                JobStatus.InProcess,
                progressPercent: percent,
                resultPayload: message);
    }
}
