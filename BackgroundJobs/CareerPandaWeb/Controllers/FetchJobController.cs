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

    /// <summary>Trigger Government-Jobs fetch (USAJobs.gov — US federal jobs, free).</summary>
    [HttpPost]
    [Route("api/fetchjobs/governmentjobs/run")]
    public Task<FrameworkResponse> TriggerGovernmentJobs([FromBody] JobFetchInput? input) =>
        TriggerFetch("governmentjobs", input);

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

    /// <summary>
    /// Trigger all FREE/unlimited job fetches in sequence within a single task: the ATS
    /// board-token sources (Lever → … → Recruitee → Greenhouse last), then Adzuna, Government, RemoteOK,
    /// Jobicy, Startup, NonProfit, then H1B sponsor enrichment last. JSearch-quota sources
    /// (AllJobs, Contract, H1B, PrimeVendor) are excluded.
    /// Each runs to completion before the next starts, and each records its own fetch run.
    /// Returns immediately with the chain's task ID; poll GET /api/fetchjobs/run/{runId} for overall progress.
    /// </summary>
    [HttpPost]
    [Route("api/fetchjobs/runalljobs/run")]
    public Task<FrameworkResponse> TriggerRunAllJobs([FromBody] JobFetchInput? input) =>
        TriggerFetch("runalljobs", input);

    /// <summary>
    /// Enrich api.companies — fills logo_url (square), about_company, website and career_page
    /// for companies, updating only those columns. Body (optional): {"BatchSize":200,"MaxParallel":8}.
    /// </summary>
    [HttpPost]
    [Route("api/fetchjobs/companyenrichment/run")]
    public Task<FrameworkResponse> TriggerCompanyEnrichment([FromBody] JobFetchInput? input) =>
        TriggerFetch("companyenrichment", input);

    // ── Dashboard stats ─────────────────────────────────────────────────────────

    /// <summary>
    /// High-level dashboard summary: total jobs, active jobs, total/new companies,
    /// classification-flag counts (H1B, Sponsored, W2, C2C, Contract, Freelance,
    /// PrimeVendor, Staffing, Startup, NonProfit, University), and jobs-by-source.
    /// </summary>
    [HttpGet]
    [Route("api/fetchjobs/stats/overview")]
    public async Task<FrameworkResponse> GetStatsOverview([FromQuery] int newCompanyWindowHours = 24)
    {
        ApplicationContext.CorrelationId = Guid.NewGuid().ToString();
        ApplicationContext.UserId        = UserId;
        return await _fetchBl.GetStatsOverviewAsync(newCompanyWindowHours);
    }

    /// <summary>
    /// Per-handler roll-up: for each source — total/active jobs, distinct companies,
    /// classification-flag counts, and the latest fetch run's status + stats.
    /// </summary>
    [HttpGet]
    [Route("api/fetchjobs/stats/byhandler")]
    public async Task<FrameworkResponse> GetStatsByHandler()
    {
        ApplicationContext.CorrelationId = Guid.NewGuid().ToString();
        ApplicationContext.UserId        = UserId;
        return await _fetchBl.GetStatsByHandlerAsync();
    }

    /// <summary>
    /// Board-token health per ATS source: counts of VALID / INVALID / EMPTY / UNKNOWN tokens.
    /// </summary>
    [HttpGet]
    [Route("api/fetchjobs/stats/tokens")]
    public async Task<FrameworkResponse> GetTokenStats()
    {
        ApplicationContext.CorrelationId = Guid.NewGuid().ToString();
        ApplicationContext.UserId        = UserId;
        return await _fetchBl.GetTokenStatsAsync();
    }

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
