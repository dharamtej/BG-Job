// CareerPandaWeb/Controllers/FetchJobController.cs
// ─────────────────────────────────────────────────────────────────────────────
// All job-fetch trigger + monitoring endpoints. Extends the existing
// JobController pattern — separate controller, same auth/CORS setup.
//
// ENDPOINTS
// ─────────────────────────────────────────────────────────────────────────────
//  POST  /api/fetchjobs/{category}/run          Trigger a background fetch
//  POST  /api/fetchjobs/alljobs/run             (convenience alias)
//  POST  /api/fetchjobs/startupjobs/run
//  POST  /api/fetchjobs/universityjobs/run
//  POST  /api/fetchjobs/nonprofitjobs/run
//  POST  /api/fetchjobs/contractjobs/run
//  POST  /api/fetchjobs/h1bjobs/run
//  POST  /api/fetchjobs/primevendorjobs/run
//
//  GET   /api/fetchjobs/run/{runId}             Poll one run (stats + timing)
//  GET   /api/fetchjobs/runs                    List all runs (paginated)
//  GET   /api/fetchjobs/runs/{category}         Filter by category
//  POST  /api/fetchjobs/cancel/{taskId}         Cancel a running fetch
//  GET   /api/fetchjobs/categories              List valid category keys
// ─────────────────────────────────────────────────────────────────────────────

using CareerPanda.BL.Background.Handlers;
using CareerPanda.BL.Logic;
using CareerPanda.Framework;
using CareerPanda.Framework.MVC;
using CareerPanda.Framework.Util;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CareerPanda.Web.Controllers;


public class FetchJobController : CoreController
{
    private readonly ILogger<FetchJobController> _logger;
    private readonly JobFetchBL _fetchBl;
    private readonly JobBL _jobBl;
    public FetchJobController(
        ILogger<FetchJobController> logger,
        JobFetchBL fetchBl,
        JobBL jobBl)
    {
        _logger  = logger;
        _fetchBl = fetchBl;
        _jobBl   = jobBl;
    }

    // ── Generic trigger (all categories in one route) ─────────────────────────

    /// <summary>
    /// Trigger a background fetch for the specified job category.
    /// Returns immediately with the run/task ID. Poll GET /api/fetchjobs/run/{runId} for progress.
    /// </summary>
    [HttpPost]
    [Route("api/fetchjobs/{category}/run")]
    public async Task<FrameworkResponse> TriggerFetch(
        [FromRoute] string category,
        [FromBody]  JobFetchInput? input)
    {
        ApplicationContext.CorrelationId = Guid.NewGuid().ToString();
        ApplicationContext.UserId        = UserId;

        input ??= new JobFetchInput();

        _logger.LogInformation(
            "User {User} triggered [{Category}] fetch — hoursBack={HB}, maxPages={MP}",
            UserId, category, input.HoursBack, input.MaxPages);

        return await _fetchBl.TriggerFetchAsync(category, input, UserId);
    }

    // ── Per-category convenience routes ────────────────────────────────────────

    /// <summary>Trigger All-Jobs fetch (JSearch — Indeed, LinkedIn, Glassdoor).</summary>
    [HttpPost]
    [Route("api/fetchjobs/alljobs/run")]
    public Task<FrameworkResponse> TriggerAllJobs([FromBody] JobFetchInput? input) =>
        TriggerFetch("alljobs", input);

    /// <summary>Trigger Startup-Jobs fetch (The Muse — free).</summary>
    [HttpPost]
    [Route("api/fetchjobs/startupjobs/run")]
    public Task<FrameworkResponse> TriggerStartupJobs([FromBody] JobFetchInput? input) =>
        TriggerFetch("startupjobs", input);

    /// <summary>Trigger University-Jobs fetch (USAJobs.gov — free).</summary>
    [HttpPost]
    [Route("api/fetchjobs/universityjobs/run")]
    public Task<FrameworkResponse> TriggerUniversityJobs([FromBody] JobFetchInput? input) =>
        TriggerFetch("universityjobs", input);

    /// <summary>Trigger Non-Profit-Jobs fetch (The Muse — free).</summary>
    [HttpPost]
    [Route("api/fetchjobs/nonprofitjobs/run")]
    public Task<FrameworkResponse> TriggerNonProfitJobs([FromBody] JobFetchInput? input) =>
        TriggerFetch("nonprofitjobs", input);

    /// <summary>Trigger Contract-Jobs fetch (JSearch CONTRACTOR filter).</summary>
    [HttpPost]
    [Route("api/fetchjobs/contractjobs/run")]
    public Task<FrameworkResponse> TriggerContractJobs([FromBody] JobFetchInput? input) =>
        TriggerFetch("contractjobs", input);

    /// <summary>Trigger H1B-Jobs fetch (JSearch + DOL LCA heuristics).</summary>
    [HttpPost]
    [Route("api/fetchjobs/h1bjobs/run")]
    public Task<FrameworkResponse> TriggerH1BJobs([FromBody] JobFetchInput? input) =>
        TriggerFetch("h1bjobs", input);

    /// <summary>Trigger Prime-Vendor / C2C Jobs fetch (JSearch).</summary>
    [HttpPost]
    [Route("api/fetchjobs/primevendorjobs/run")]
    public Task<FrameworkResponse> TriggerPrimeVendorJobs([FromBody] JobFetchInput? input) =>
        TriggerFetch("primevendorjobs", input);

    /// <summary>Trigger RemoteOK Jobs fetch (RemoteOK public API — US remote jobs).</summary>
    [HttpPost]
    [Route("api/fetchjobs/remoteoknobs/run")]
    public Task<FrameworkResponse> TriggerRemoteOkJobs([FromBody] JobFetchInput? input) =>
        TriggerFetch("remoteoknobs", input);

    /// <summary>Trigger Jobicy Jobs fetch (Jobicy public API — remote jobs by industry).</summary>
    [HttpPost]
    [Route("api/fetchjobs/jobicyjobs/run")]
    public Task<FrameworkResponse> TriggerJobicyJobs([FromBody] JobFetchInput? input) =>
        TriggerFetch("jobicyjobs", input);

    /// <summary>Trigger Adzuna Jobs fetch (Adzuna API).</summary>
    [HttpPost]
    [Route("api/fetchjobs/adzunajobs/run")]
    public Task<FrameworkResponse> TriggerAdzunaJobs([FromBody] JobFetchInput? input) =>
        TriggerFetch("adzunajobs", input);

    /// <summary>Trigger H1B Sponsor Enrichment (enriches existing raw jobs with sponsor flag).</summary>
    [HttpPost]
    [Route("api/fetchjobs/h1bsponorenrichment/run")]
    public Task<FrameworkResponse> TriggerH1BSponsorEnrichment([FromBody] JobFetchInput? input) =>
        TriggerFetch("h1bsponorenrichment", input);

    // ── Status & monitoring ────────────────────────────────────────────────────

    /// <summary>
    /// Get full statistics for one fetch run:
    /// status, started_at, completed_at, duration_seconds,
    /// total_fetched, total_inserted, total_updated, total_skipped, total_errors, pages_fetched.
    /// Use this to poll progress after triggering.
    /// </summary>
    [HttpGet]
    [Route("api/fetchjobs/run/{runId}")]
    public async Task<FrameworkResponse> GetFetchRun([FromRoute] string runId)
    {
        ApplicationContext.CorrelationId = runId;
        ApplicationContext.UserId        = UserId;
        return await _fetchBl.GetFetchRunAsync(runId);
    }

    /// <summary>
    /// List all fetch runs, newest first.
    /// Query params: status (Running|Completed|Failed|Cancelled), pageNumber, pageSize.
    /// </summary>
    [HttpGet]
    [Route("api/fetchjobs/runs")]
    public async Task<FrameworkResponse> GetAllFetchRuns(
        [FromQuery] string? status     = null,
        [FromQuery] int     pageNumber = 1,
        [FromQuery] int     pageSize   = 20)
    {
        ApplicationContext.CorrelationId = Guid.NewGuid().ToString();
        ApplicationContext.UserId        = UserId;
        return await _fetchBl.GetFetchRunsAsync(pageNumber, pageSize, status: status);
    }

    /// <summary>
    /// List fetch runs filtered by category, newest first.
    /// Query params: status (Running|Completed|Failed|Cancelled), pageNumber, pageSize.
    /// </summary>
    [HttpGet]
    [Route("api/fetchjobs/runs/{category}")]
    public async Task<FrameworkResponse> GetFetchRunsByCategory(
        [FromRoute] string  category,
        [FromQuery] string? status     = null,
        [FromQuery] int     pageNumber = 1,
        [FromQuery] int     pageSize   = 20)
    {
        ApplicationContext.CorrelationId = Guid.NewGuid().ToString();
        ApplicationContext.UserId        = UserId;
        return await _fetchBl.GetFetchRunsAsync(pageNumber, pageSize, category: category, status: status);
    }

    /// <summary>
    /// Cancel a running fetch job by its task ID (same value as the fetch run ID).
    /// </summary>
    [HttpPost]
    [Route("api/fetchjobs/cancel/{taskId}")]
    public async Task<FrameworkResponse> CancelFetchJob([FromRoute] string taskId)
    {
        ApplicationContext.CorrelationId = taskId;
        ApplicationContext.UserId        = UserId;
        return await _jobBl.CancelJobAsync(taskId);
    }

    /// <summary>Returns all valid category keys and their display names.</summary>
    [HttpGet]
    [Route("api/fetchjobs/categories")]
    public FrameworkResponse GetCategories()
    {
        ApplicationContext.CorrelationId = Guid.NewGuid().ToString();
        return new FrameworkResponse
        {
            Status = Status.Success,
            Entity = JobFetchBL.CategoryMap.Select(kv => new { key = kv.Key, label = kv.Value })
        };
    }
}
