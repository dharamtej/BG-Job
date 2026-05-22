using CareerPanda.BL.Background;
using CareerPanda.DataAccess.DA;
using CareerPanda.DataAccess.Entities.Api;
using CareerPanda.Framework;
using CareerPanda.Framework.Util;
using Microsoft.Extensions.Logging;

namespace CareerPanda.BL.Logic;

public class JobBL
{
    private readonly IBackgroundTaskDA _jobDa;
    private readonly IBackgroundJobQueue _backgroundQueue;
    private readonly JobExecutionService _jobExecutionService;
    private readonly JobCancellationRegistry _cancellationRegistry;
    private readonly ILogger<JobBL> _logger;

    public JobBL(
        IBackgroundTaskDA jobDa,
        IBackgroundJobQueue backgroundQueue,
        JobExecutionService jobExecutionService,
        JobCancellationRegistry cancellationRegistry,
        ILogger<JobBL> logger)
    {
        _jobDa = jobDa;
        _backgroundQueue = backgroundQueue;
        _jobExecutionService = jobExecutionService;
        _cancellationRegistry = cancellationRegistry;
        _logger = logger;
    }

    public Task<FrameworkResponse> CreateJobAsync(BackgroundTask job) =>
        _jobDa.CreateJobAsync(job);

    public Task<FrameworkResponse> UpdateJobAsync(BackgroundTask job) =>
        _jobDa.UpdateJobAsync(job);

    public Task<FrameworkResponse> GetJobAsync(string jobId) =>
        _jobDa.GetJobAsync(jobId);

    public Task<FrameworkResponse> GetJobStatusAsync(string jobId) =>
        _jobDa.GetJobStatusAsync(jobId);

    public Task<FrameworkResponse> GetJobsAsync(int pageNumber, int pageSize) =>
        _jobDa.GetJobsAsync(pageNumber, pageSize);

    public async Task<FrameworkResponse> CancelJobAsync(string jobId)
    {
        var response = new FrameworkResponse { Status = Status.Failed };

        var jobResponse = await _jobDa.GetJobAsync(jobId);
        if (jobResponse.Status != Status.Success)
        {
            response.Message = "Job not found.";
            return response;
        }

        _cancellationRegistry.TryCancel(jobId);
        response = await _jobDa.UpdateJobStatusAsync(jobId, JobStatus.Cancelled, errorMessage: "Cancelled by user.");
        return response;
    }

    public async Task<FrameworkResponse> QueueBackgroundJobAsync(BackgroundTask job, string userId)
    {
        var response = new FrameworkResponse { Status = Status.Failed };

        if (job == null)
        {
            response.Message = "Please validate the input.";
            return response;
        }

        job.Id = string.IsNullOrWhiteSpace(job.Id) ? Guid.NewGuid().ToString() : job.Id;
        job.Status = JobStatus.Pending;
        job.ProgressPercent = 0;
        job.CreatedAt = DateTime.UtcNow;
        job.UpdatedAt = DateTime.UtcNow;
        job.CreatedById = userId;

        if (string.IsNullOrWhiteSpace(job.JobType))
            job.JobType = "Default";

        response = await _jobDa.CreateJobAsync(job);
        if (response.Status != Status.Success)
            return response;

        var workRequest = new JobWorkRequest
        {
            JobId = job.Id,
            UserId = userId,
            JobType = job.JobType,
            InputPayload = job.Description
        };

        var jobCts = _cancellationRegistry.Register(job.Id);

        await _backgroundQueue.QueueBackgroundWorkItemAsync(async workerCt =>
        {
            ApplicationContext.UserId = userId;
            ApplicationContext.CorrelationId = job.Id;
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(workerCt, jobCts.Token);
                await _jobExecutionService.ProcessAsync(workRequest, linked.Token);
            }
            finally
            {
                _cancellationRegistry.Remove(job.Id);
            }
        });

        _logger.LogInformation("Queued background job {JobId} type {JobType}", job.Id, job.JobType);

        response.Status = Status.Success;
        response.Message = job.Id;
        response.Entity = job;
        return response;
    }
}
