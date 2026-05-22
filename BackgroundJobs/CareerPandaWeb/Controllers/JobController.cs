using CareerPanda.BL.Logic;
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

    public JobController(ILogger<JobController> logger, JobBL jobBl)
    {
        _logger = logger;
        _jobBl = jobBl;
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
