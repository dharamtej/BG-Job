using CareerPanda.DataAccess.Entities.Api;
using CareerPanda.Framework;
using CareerPanda.Framework.Util;

namespace CareerPanda.DataAccess.DA;

public interface IBackgroundTaskDA
{
    Task<FrameworkResponse> CreateJobAsync(BackgroundTask job);

    Task<FrameworkResponse> UpdateJobAsync(BackgroundTask job);

    Task<FrameworkResponse> GetJobAsync(string jobId);

    Task<FrameworkResponse> GetJobStatusAsync(string jobId);

    Task<FrameworkResponse> GetJobsAsync(int pageNumber, int pageSize);

    Task<FrameworkResponse> UpdateJobStatusAsync(
        string jobId,
        JobStatus status,
        int? progressPercent = null,
        string? resultPayload = null,
        string? errorMessage = null);

    Task<List<BackgroundTask>> GetScheduledTasksAsync();

    Task UpdateScheduleAsync(string jobId, string? scheduleType, TimeSpan? dailyTime, int? intervalHours);

    Task UpdateLastScheduledRunAsync(string jobId, DateTime ranAt);
}
