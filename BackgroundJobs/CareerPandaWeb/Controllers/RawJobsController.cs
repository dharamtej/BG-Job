// CareerPandaWeb/Controllers/RawJobsController.cs
// Public portal endpoints — job search + reference-data dropdowns.
//
// ENDPOINTS
// ─────────────────────────────────────────────────────────────────────────────
//  GET  /api/rawjobs/industries           All active industries (for dropdowns)
//  GET  /api/rawjobs/jobroles             All active job roles (optionally ?industryId=N)
//  POST /api/rawjobs/search               Paginated job search with full filter set
// ─────────────────────────────────────────────────────────────────────────────
using CareerPanda.DataAccess.DA;
using CareerPanda.DataAccess.Models;
using CareerPanda.Framework;
using CareerPanda.Framework.MVC;
using CareerPanda.Framework.Util;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CareerPanda.Web.Controllers;

[AllowAnonymous]
public class RawJobsController : CoreController
{
    private readonly IJobFetchDA _da;

    public RawJobsController(IJobFetchDA da) => _da = da;

    /// <summary>All active industries for portal filter dropdown.</summary>
    [HttpGet]
    [Route("api/rawjobs/industries")]
    public async Task<FrameworkResponse> GetIndustries(CancellationToken ct)
    {
        var list = await _da.GetActiveIndustriesAsync(ct);
        return new FrameworkResponse { Status = Status.Success, Entity = list };
    }

    /// <summary>All active job roles. Pass ?industryId=N to filter by industry.</summary>
    [HttpGet]
    [Route("api/rawjobs/jobroles")]
    public async Task<FrameworkResponse> GetJobRoles([FromQuery] int? industryId, CancellationToken ct)
    {
        var list = await _da.GetActiveJobRolesAsync(industryId, ct);
        return new FrameworkResponse { Status = Status.Success, Entity = list };
    }

    /// <summary>
    /// Portal job search with full filter support.
    /// Returns { items: [...], total: N, page: N, pageSize: N, totalPages: N }.
    /// </summary>
    [HttpPost]
    [Route("api/rawjobs/search")]
    public async Task<FrameworkResponse> SearchJobs([FromBody] RawJobSearchQuery query, CancellationToken ct)
    {
        if (query == null)
            return new FrameworkResponse { Status = Status.Failed, Message = "Request body required." };

        query.Page     = Math.Max(1, query.Page);
        query.PageSize = Math.Clamp(query.PageSize, 1, 100);

        var (items, total) = await _da.SearchRawJobsAsync(query, ct);

        return new FrameworkResponse
        {
            Status = Status.Success,
            Entity = new
            {
                items,
                total,
                page       = query.Page,
                pageSize   = query.PageSize,
                totalPages = (int)Math.Ceiling((double)total / query.PageSize)
            }
        };
    }
}
