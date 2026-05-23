using CareerPanda.DataAccess.DA;
using CareerPanda.DataAccess.Entities.Api;
using CareerPanda.Framework;
using CareerPanda.Framework.Util;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CareerPanda.DataAccess.PostgreSQL;

public class BackgroundTaskDAPostgres : IBackgroundTaskDA
{
    private readonly CareerPandaDbContext _context;
    private readonly ILogger<BackgroundTaskDAPostgres> _logger;

    public BackgroundTaskDAPostgres(ILogger<BackgroundTaskDAPostgres> logger, CareerPandaDbContext context)
    {
        _logger = logger;
        _context = context;
    }

    public async Task<FrameworkResponse> CreateJobAsync(BackgroundTask job)
    {
        var response = new FrameworkResponse { Status = Status.Failed };
        try
        {
            _context.BackgroundTasks.Add(job);
            await _context.SaveChangesAsync();
            response.Status = Status.Success;
            response.Entity = job;
        }
        catch (Exception ex)
        {
            response.Message = ex.Message;
            _logger.LogError(ex, "CreateBackgroundTask failed");
        }
        return response;
    }

    public async Task<FrameworkResponse> UpdateJobAsync(BackgroundTask job)
    {
        var response = new FrameworkResponse { Status = Status.Failed };
        try
        {
            var existing = await _context.BackgroundTasks.FindAsync(job.Id);
            if (existing == null)
            {
                response.Message = "Job not found.";
                return response;
            }

            existing.Name                 = job.Name;
            existing.Description          = job.Description;
            existing.JobType              = job.JobType;
            existing.Status               = job.Status;
            existing.ProgressPercent      = job.ProgressPercent;
            existing.StartedAt            = job.StartedAt;
            existing.CompletedAt          = job.CompletedAt;
            existing.ResultPayload        = job.ResultPayload;
            existing.ErrorMessage         = job.ErrorMessage;
            existing.ScheduleType         = job.ScheduleType;
            existing.ScheduleDailyTime    = job.ScheduleDailyTime;
            existing.ScheduleIntervalHours = job.ScheduleIntervalHours;
            existing.NextRunAt            = job.NextRunAt;
            existing.UpdatedAt            = DateTime.UtcNow;

            await _context.SaveChangesAsync();
            response.Status = Status.Success;
            response.Entity = existing;
        }
        catch (Exception ex)
        {
            response.Message = ex.Message;
            _logger.LogError(ex, "UpdateBackgroundTask failed for {JobId}", job.Id);
        }
        return response;
    }

    public async Task<FrameworkResponse> GetJobAsync(string jobId)
    {
        var response = new FrameworkResponse { Status = Status.Failed };
        try
        {
            var job = await _context.BackgroundTasks.FindAsync(jobId);
            if (job != null)
            {
                response.Status = Status.Success;
                response.Entity = job;
            }
            else
                response.Message = "Job not found.";
        }
        catch (Exception ex)
        {
            response.Message = ex.Message;
            _logger.LogError(ex, "GetBackgroundTask failed for {JobId}", jobId);
        }
        return response;
    }

    public async Task<FrameworkResponse> GetJobStatusAsync(string jobId)
    {
        var response = await GetJobAsync(jobId);
        if (response.Status == Status.Success && response.Entity is BackgroundTask job)
            response.Message = job.Status.ToString();
        return response;
    }

    public async Task<FrameworkResponse> GetJobsAsync(int pageNumber, int pageSize)
    {
        var response = new FrameworkResponse { Status = Status.Failed };
        try
        {
            var skip = Math.Max(0, (pageNumber - 1) * pageSize);
            var query = _context.BackgroundTasks.OrderByDescending(j => j.CreatedAt);
            response.TotalRecords = await query.CountAsync();
            var jobs = await query.Skip(skip).Take(pageSize).ToListAsync();
            response.Status = Status.Success;
            response.Entity = jobs;
        }
        catch (Exception ex)
        {
            response.Message = ex.Message;
            _logger.LogError(ex, "GetBackgroundTasks failed");
        }
        return response;
    }

    public async Task<FrameworkResponse> UpdateJobStatusAsync(
        string jobId,
        JobStatus status,
        int? progressPercent = null,
        string? resultPayload = null,
        string? errorMessage = null)
    {
        var response = new FrameworkResponse { Status = Status.Failed };
        try
        {
            var job = await _context.BackgroundTasks.FindAsync(jobId);
            if (job == null)
            {
                response.Message = "Job not found.";
                return response;
            }

            job.Status = status;
            if (progressPercent.HasValue)
                job.ProgressPercent = progressPercent.Value;
            if (resultPayload != null)
                job.ResultPayload = resultPayload;
            if (errorMessage != null)
                job.ErrorMessage = errorMessage;

            if (status == JobStatus.InProcess && job.StartedAt == null)
                job.StartedAt = DateTime.UtcNow;

            if (status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled)
                job.CompletedAt = DateTime.UtcNow;

            job.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            response.Status = Status.Success;
            response.Entity = job;
            response.Message = job.Status.ToString();
        }
        catch (Exception ex)
        {
            response.Message = ex.Message;
            _logger.LogError(ex, "UpdateBackgroundTaskStatus failed for {JobId}", jobId);
        }
        return response;
    }

    public async Task<List<BackgroundTask>> GetScheduledTasksAsync() =>
        await _context.BackgroundTasks
            .Where(t => t.ScheduleType != null && t.ScheduleType != "None")
            .ToListAsync();

    public async Task UpdateScheduleAsync(
        string jobId, string? scheduleType, TimeSpan? dailyTime, int? intervalHours)
    {
        var job = await _context.BackgroundTasks.FindAsync(jobId);
        if (job == null) return;
        job.ScheduleType          = scheduleType;
        job.ScheduleDailyTime     = dailyTime;
        job.ScheduleIntervalHours = intervalHours;
        job.Status                = scheduleType is null or "None" ? JobStatus.Pending : JobStatus.Scheduled;
        job.UpdatedAt             = DateTime.UtcNow;
        await _context.SaveChangesAsync();
    }

    public async Task UpdateLastScheduledRunAsync(string jobId, DateTime ranAt)
    {
        var job = await _context.BackgroundTasks.FindAsync(jobId);
        if (job == null) return;
        job.LastScheduledRunAt = ranAt;
        job.UpdatedAt          = DateTime.UtcNow;
        await _context.SaveChangesAsync();
    }
}
