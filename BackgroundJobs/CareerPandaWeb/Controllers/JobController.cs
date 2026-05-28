using CareerPanda.BL.Background;
using CareerPanda.BL.Logic;
using CareerPanda.DataAccess.DA;
using CareerPanda.DataAccess.Entities.Api;
using CareerPanda.Framework;
using CareerPanda.Framework.MVC;
using CareerPanda.Framework.Util;
using Microsoft.AspNetCore.Mvc;

namespace CareerPanda.Web.Controllers;

public class JobController : CoreController
{
    private readonly ILogger<JobController> _logger;
    private readonly JobBL _jobBl;
    private readonly IEnumerable<IJobHandler> _handlers;
    private readonly IBackgroundTaskDA _taskDa;
    private readonly JobSchedulerService _scheduler;

    public JobController(
        ILogger<JobController> logger,
        JobBL jobBl,
        IEnumerable<IJobHandler> handlers,
        IBackgroundTaskDA taskDa,
        JobSchedulerService scheduler)
    {
        _logger    = logger;
        _jobBl     = jobBl;
        _handlers  = handlers;
        _taskDa    = taskDa;
        _scheduler = scheduler;
    }

    [HttpPost]
    [Route("api/job/create")]
    public async Task<FrameworkResponse> CreateJob([FromBody] BackgroundTask job)
    {
        ApplicationContext.CorrelationId = job?.Id ?? Guid.NewGuid().ToString();
        ApplicationContext.UserId = UserId;

        var response = new FrameworkResponse { Status = Status.Failed };
        if (job != null)
        {
            ApplyDefaults(job, isCreate: true);
            response = await _jobBl.CreateJobAsync(job);
            response.Entity = job;
        }
        else
            response.Message = "Please validate the input.";

        return response;
    }

    [HttpPost]
    [Route("api/job/update")]
    public async Task<FrameworkResponse> UpdateJob([FromBody] BackgroundTask job)
    {
        ApplicationContext.CorrelationId = job?.Id ?? Guid.NewGuid().ToString();
        ApplicationContext.UserId = UserId;

        var response = new FrameworkResponse { Status = Status.Failed };
        if (job != null)
        {
            ApplyDefaults(job, isCreate: false);
            response = await _jobBl.UpdateJobAsync(job);
        }
        else
            response.Message = "Please validate the input.";

        return response;
    }

    [HttpGet]
    [Route("api/job/get/{jobid}")]
    public async Task<FrameworkResponse> GetJob(string jobid)
    {
        ApplicationContext.CorrelationId = jobid;
        ApplicationContext.UserId = UserId;
        return await _jobBl.GetJobAsync(jobid);
    }

    [HttpPost]
    [Route("api/job/run")]
    public async Task<FrameworkResponse> RunBackgroundJob([FromBody] BackgroundTask job)
    {
        ApplicationContext.CorrelationId = job?.Id ?? Guid.NewGuid().ToString();
        ApplicationContext.UserId = UserId;

        var response = new FrameworkResponse { Status = Status.Failed };
        if (job != null)
            response = await _jobBl.QueueBackgroundJobAsync(job, UserId);
        else
            response.Message = "Please validate the input.";

        return response;
    }

    [HttpGet]
    [Route("api/job/getstatus/{jobid}")]
    public async Task<FrameworkResponse> GetJobStatus(string jobid)
    {
        ApplicationContext.CorrelationId = jobid;
        ApplicationContext.UserId = UserId;
        return await _jobBl.GetJobStatusAsync(jobid);
    }

    [HttpPost]
    [Route("api/job/cancel/{jobid}")]
    public async Task<FrameworkResponse> CancelJob(string jobid)
    {
        ApplicationContext.CorrelationId = jobid;
        ApplicationContext.UserId = UserId;
        return await _jobBl.CancelJobAsync(jobid);
    }

    [HttpGet]
    [Route("api/job/list")]
    public async Task<FrameworkResponse> GetJobs([FromQuery] int pageNumber = 1, [FromQuery] int pageSize = 20)
    {
        ApplicationContext.CorrelationId = Guid.NewGuid().ToString();
        ApplicationContext.UserId = UserId;
        return await _jobBl.GetJobsAsync(pageNumber, pageSize);
    }

    /// <summary>
    /// Set or update the recurring schedule for a job template.
    /// ScheduleType: "Daily" | "Interval" | "None"
    /// For Daily: provide ScheduleDailyTime as "HH:mm" (UTC). Example: "02:30"
    /// For Interval: provide ScheduleIntervalHours (1, 2, 3, 4, 6, 8, 12, 24).
    /// Sending "None" removes the schedule.
    /// </summary>
    [HttpPost]
    [Route("api/job/schedule/{jobid}")]
    public async Task<FrameworkResponse> SetSchedule(string jobid, [FromBody] ScheduleRequest req)
    {
        ApplicationContext.CorrelationId = jobid;
        ApplicationContext.UserId = UserId;

        var response = new FrameworkResponse { Status = Status.Failed };
        var jobResp  = await _jobBl.GetJobAsync(jobid);
        if (jobResp.Entity is not BackgroundTask task)
        {
            response.Message = "Job not found.";
            return response;
        }

        TimeSpan? dailyTime = null;
        if (req.ScheduleType == "Daily" && !string.IsNullOrWhiteSpace(req.ScheduleDailyTime))
        {
            if (!TimeSpan.TryParse(req.ScheduleDailyTime, out var ts))
            {
                response.Message = "ScheduleDailyTime must be in HH:mm format (e.g. '02:30').";
                return response;
            }
            dailyTime = ts;
        }

        await _taskDa.UpdateScheduleAsync(jobid, req.ScheduleType, dailyTime, req.ScheduleIntervalHours);

        task.ScheduleType          = req.ScheduleType;
        task.ScheduleDailyTime     = dailyTime;
        task.ScheduleIntervalHours = req.ScheduleIntervalHours;

        if (req.ScheduleType is null or "None")
            _scheduler.RemoveJob(jobid);
        else
            _scheduler.RegisterJob(task);

        response.Status  = Status.Success;
        response.Message = $"Schedule set: {req.ScheduleType}";
        return response;
    }

    /// <summary>
    /// List all job templates that currently have a recurring schedule.
    /// </summary>
    [HttpGet]
    [Route("api/job/schedules")]
    public async Task<FrameworkResponse> GetSchedules()
    {
        ApplicationContext.CorrelationId = Guid.NewGuid().ToString();
        ApplicationContext.UserId = UserId;
        var tasks = await _taskDa.GetScheduledTasksAsync();
        return new FrameworkResponse { Status = Status.Success, Entity = tasks };
    }

    /// <summary>
    /// Remove the recurring schedule from a job template.
    /// </summary>
    [HttpDelete]
    [Route("api/job/schedule/{jobid}")]
    public async Task<FrameworkResponse> RemoveSchedule(string jobid)
    {
        ApplicationContext.CorrelationId = jobid;
        ApplicationContext.UserId = UserId;

        await _taskDa.UpdateScheduleAsync(jobid, "None", null, null);
        _scheduler.RemoveJob(jobid);

        return new FrameworkResponse { Status = Status.Success, Message = "Schedule removed." };
    }

    /// <summary>
    /// Returns all registered job types and the JSON payload to pass in the Description field when calling /api/job/run.
    /// </summary>
    [HttpGet]
    [Route("api/job/types")]
    public FrameworkResponse GetJobTypes()
    {
        var types = _handlers
            .Where(h => h.JobType != "Default")
            .Select(h => new
            {
                jobType    = h.JobType,
                sampleBody = SampleBody(h.JobType)
            })
            .OrderBy(x => x.jobType)
            .ToList();

        return new FrameworkResponse { Status = Status.Success, Entity = types };
    }

    private static object SampleBody(string jobType) => jobType switch
    {
        "H1BSponsorEnrichment" => new
        {
            Name        = "H1B Sponsor Enrichment",
            JobType     = "H1BSponsorEnrichment",
            Description = """{"BatchSize":500,"MaxParallel":10}"""
        },
        "H1BJobs" => new
        {
            Name        = "Fetch H1B Jobs",
            JobType     = "H1BJobs",
            Description = """{"HoursBack":720,"MaxPages":10,"Location":"United States"}"""
        },
        "AllJobs" => new
        {
            Name        = "Fetch All Jobs — All Roles × All US States (JSearch)",
            JobType     = "AllJobs",
            Description = """{"HoursBack":720,"PagesPerQuery":1}"""
        },
        "AdzunaJobs" => new
        {
            Name        = "Fetch All Jobs — All Roles × All US States (Adzuna)",
            JobType     = "AdzunaJobs",
            Description = """{"HoursBack":720,"PagesPerQuery":2}"""
        },
        "RemoteOkJobs" => new
        {
            Name        = "Fetch Remote Jobs — All Tags (RemoteOK)",
            JobType     = "RemoteOkJobs",
            Description = """{"HoursBack":720}"""
        },
        "JobicyJobs" => new
        {
            Name        = "Fetch Remote Jobs — All Industries (Jobicy)",
            JobType     = "JobicyJobs",
            Description = """{"HoursBack":720}"""
        },
        "GovernmentJobs" => new
        {
            Name        = "Fetch US Federal Government Jobs (USAJobs.gov)",
            JobType     = "GovernmentJobs",
            Description = """{"MaxPages":500}"""
        },
        "GreenhouseJobs" => new
        {
            Name        = "Fetch Greenhouse Jobs — All Board Tokens",
            JobType     = "GreenhouseJobs",
            Description = """{}"""
        },
        "LeverJobs" => new
        {
            Name        = "Fetch Lever Jobs — All 4368 Board Tokens",
            JobType     = "LeverJobs",
            Description = """{}"""
        },
        "WorkdayJobs" => new
        {
            Name        = "Fetch Workday Jobs — All 10132 Board Tokens",
            JobType     = "WorkdayJobs",
            Description = """{}"""
        },
        "AshbyJobs" => new
        {
            Name        = "Fetch Ashby Jobs — All 2985 Board Tokens",
            JobType     = "AshbyJobs",
            Description = """{}"""
        },
        "BambooHrJobs" => new
        {
            Name        = "Fetch BambooHR Jobs — All Board Tokens",
            JobType     = "BambooHrJobs",
            Description = """{}"""
        },
        "IcimsJobs" => new
        {
            Name        = "Fetch iCIMS Jobs — All Board Tokens",
            JobType     = "IcimsJobs",
            Description = """{}"""
        },
        _ => new
        {
            Name        = jobType,
            JobType     = jobType,
            Description = (string?)null
        }
    };

    private void ApplyDefaults(BackgroundTask job, bool isCreate)
    {
        if (isCreate)
        {
            if (string.IsNullOrWhiteSpace(job.Id))
                job.Id = Guid.NewGuid().ToString();
            job.CreatedAt = DateTime.UtcNow;
            job.CreatedById = UserId;
        }

        job.UpdatedAt = DateTime.UtcNow;
    }
}

public class ScheduleRequest
{
    /// <summary>Daily | Interval | None</summary>
    public string? ScheduleType { get; set; }

    /// <summary>Used when ScheduleType=Daily. Time of day in UTC, format HH:mm (e.g. "02:30").</summary>
    public string? ScheduleDailyTime { get; set; }

    /// <summary>Used when ScheduleType=Interval. Hours between runs (1, 2, 3, 4, 6, 8, 12, 24).</summary>
    public int? ScheduleIntervalHours { get; set; }
}
