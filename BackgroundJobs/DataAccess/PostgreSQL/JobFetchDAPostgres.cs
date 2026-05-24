// DataAccess/PostgreSQL/JobFetchDAPostgres.cs
using CareerPanda.DataAccess.DA;
using CareerPanda.DataAccess.Entities.Api;
using CareerPanda.Framework;
using CareerPanda.Framework.Util;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CareerPanda.DataAccess.PostgreSQL;

public class JobFetchDAPostgres : IJobFetchDA
{
    private readonly CareerPandaDbContext _db;
    private readonly ILogger<JobFetchDAPostgres> _logger;

    public JobFetchDAPostgres(CareerPandaDbContext db, ILogger<JobFetchDAPostgres> logger)
    {
        _db     = db;
        _logger = logger;
    }

    // ── Fetch Run ────────────────────────────────────────────────────────────

    public async Task<FrameworkResponse> CreateFetchRunAsync(ApiJobFetchRun run)
    {
        var resp = new FrameworkResponse { Status = Status.Failed };
        try
        {
            _db.JobFetchRuns.Add(run);
            await _db.SaveChangesAsync();
            resp.Status = Status.Success;
            resp.Entity = run;
        }
        catch (Exception ex)
        {
            resp.Message = ex.Message;
            _logger.LogError(ex, "CreateFetchRun failed for {RunId}", run.Id);
        }
        return resp;
    }

    public async Task<FrameworkResponse> UpdateFetchRunStatsAsync(
        string runId,
        int totalFetched, int totalInserted, int totalUpdated,
        int totalSkipped, int totalErrors, int pagesFetched)
    {
        var resp = new FrameworkResponse { Status = Status.Failed };
        try
        {
            var run = await _db.JobFetchRuns.FindAsync(runId);
            if (run == null) { resp.Message = "FetchRun not found."; return resp; }

            run.TotalFetched  = totalFetched;
            run.TotalInserted = totalInserted;
            run.TotalUpdated  = totalUpdated;
            run.TotalSkipped  = totalSkipped;
            run.TotalErrors   = totalErrors;
            run.PagesFetched  = pagesFetched;
            run.UpdatedAt     = DateTime.UtcNow;

            await _db.SaveChangesAsync();
            resp.Status = Status.Success;
            resp.Entity = run;
        }
        catch (Exception ex)
        {
            resp.Message = ex.Message;
            _logger.LogError(ex, "UpdateFetchRunStats failed for {RunId}", runId);
        }
        return resp;
    }

    public async Task<FrameworkResponse> CompleteFetchRunAsync(
        string runId, string status, string? errorMessage = null)
    {
        var resp = new FrameworkResponse { Status = Status.Failed };
        try
        {
            var run = await _db.JobFetchRuns.FindAsync(runId);
            if (run == null) { resp.Message = "FetchRun not found."; return resp; }

            run.Status          = status;
            run.CompletedAt     = DateTime.UtcNow;
            run.DurationSeconds = (int)(run.CompletedAt.Value - run.StartedAt).TotalSeconds;
            run.ErrorMessage    = errorMessage?.Length > 2000 ? errorMessage[..2000] : errorMessage;
            run.UpdatedAt       = DateTime.UtcNow;

            await _db.SaveChangesAsync();
            resp.Status = Status.Success;
            resp.Entity = run;
        }
        catch (Exception ex)
        {
            resp.Message = ex.Message;
            _logger.LogError(ex, "CompleteFetchRun failed for {RunId}", runId);
        }
        return resp;
    }

    public async Task<FrameworkResponse> GetFetchRunAsync(string runId)
    {
        var resp = new FrameworkResponse { Status = Status.Failed };
        try
        {
            var run = await _db.JobFetchRuns.FindAsync(runId);
            if (run == null) { resp.Message = "FetchRun not found."; return resp; }
            resp.Status = Status.Success;
            resp.Entity = run;
        }
        catch (Exception ex)
        {
            resp.Message = ex.Message;
            _logger.LogError(ex, "GetFetchRun failed for {RunId}", runId);
        }
        return resp;
    }

    public async Task<FrameworkResponse> GetFetchRunsAsync(
        int pageNumber, int pageSize,
        string? jobCategory = null, string? status = null)
    {
        var resp = new FrameworkResponse { Status = Status.Failed };
        try
        {
            var query = _db.JobFetchRuns.AsQueryable();

            if (!string.IsNullOrWhiteSpace(jobCategory))
                query = query.Where(r => r.JobCategory == jobCategory);

            if (!string.IsNullOrWhiteSpace(status))
                query = query.Where(r => r.Status == status);

            resp.TotalRecords = await query.CountAsync();

            var items = await query
                .OrderByDescending(r => r.StartedAt)
                .Skip((pageNumber - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            resp.Status = Status.Success;
            resp.Entity = items;
        }
        catch (Exception ex)
        {
            resp.Message = ex.Message;
            _logger.LogError(ex, "GetFetchRuns failed");
        }
        return resp;
    }

    // ── Raw Jobs ─────────────────────────────────────────────────────────────

    public async Task<(bool isNew, int rawJobId)> UpsertRawJobAsync(ApiRawJob job)
    {
        // Resolve (or create) the company first so OurCompanyId is set before save.
        job.OurCompanyId = await ResolveOrCreateCompanyAsync(job);

        // Lookup key: job_link (canonical apply URL).
        // Falls back to source + source_id if no URL.
        ApiRawJob? existing = null;

        if (!string.IsNullOrWhiteSpace(job.JobLink))
            existing = await _db.RawJobs.FirstOrDefaultAsync(j => j.JobLink == job.JobLink);

        if (existing == null && !string.IsNullOrWhiteSpace(job.SourceId) && !string.IsNullOrWhiteSpace(job.Source))
            existing = await _db.RawJobs.FirstOrDefaultAsync(j => j.SourceId == job.SourceId && j.Source == job.Source);

        if (existing == null)
        {
            // INSERT
            if (string.IsNullOrWhiteSpace(job.PublicId))
                job.PublicId = Guid.NewGuid().ToString("N");
            job.CreatedOn = DateTime.UtcNow;
            job.UpdatedOn = DateTime.UtcNow;
            _db.RawJobs.Add(job);
            await _db.SaveChangesAsync();
            return (true, job.Id);
        }
        else
        {
            // UPDATE — refresh all mutable fields
            MapUpdatableFields(existing, job);
            existing.UpdatedOn = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            return (false, existing.Id);
        }
    }

    // ── Company resolution ───────────────────────────────────────────────────

    /// <summary>
    /// Finds a matching company in api.companies or creates a new one.
    /// Returns the company's internal id (OurCompanyId for the raw job).
    /// Returns 0 when no company name is available.
    /// </summary>
    private async Task<int> ResolveOrCreateCompanyAsync(ApiRawJob job)
    {
        if (string.IsNullOrWhiteSpace(job.CompanyName)) return 0;

        var name = job.CompanyName.Trim();

        // 1. Match by external API company ID (stable for TheMuse)
        if (!string.IsNullOrWhiteSpace(job.ApiCompanyId))
        {
            var byApiId = await _db.Companies
                .FirstOrDefaultAsync(c => c.ApiCompanyId == job.ApiCompanyId);
            if (byApiId != null)
            {
                EnrichCompany(byApiId, job);
                await _db.SaveChangesAsync();
                return byApiId.Id;
            }
        }

        // 2. Match by company name (case-insensitive)
        var nameLower = name.ToLower();
        var byName = await _db.Companies
            .FirstOrDefaultAsync(c => c.CompanyName.ToLower() == nameLower);
        if (byName != null)
        {
            EnrichCompany(byName, job);
            await _db.SaveChangesAsync();
            return byName.Id;
        }

        // 3. Create new company record
        var newCompany = new ApiCompany
        {
            PublicId     = Guid.NewGuid().ToString("N"),
            ApiCompanyId = job.ApiCompanyId,
            CompanyName  = name,
            LogoUrl      = job.CompanyLogoUrl,
            Website      = job.CompanyUrl,
            CompanyType  = job.CompanyType,
            CreatedOn    = DateTime.UtcNow,
            UpdatedOn    = DateTime.UtcNow
        };
        _db.Companies.Add(newCompany);
        try
        {
            await _db.SaveChangesAsync();
            return newCompany.Id;
        }
        catch (DbUpdateException ex)
        {
            // Race condition: another concurrent request created the same company.
            var inner = ex.InnerException?.Message ?? ex.Message;
            _logger.LogWarning("==> Company upsert race for '{Name}' — {Inner}", name, inner);
            _db.Entry(newCompany).State = EntityState.Detached;
            var existing = await _db.Companies
                .FirstOrDefaultAsync(c => c.CompanyName.ToLower() == nameLower);
            return existing?.Id ?? 0;
        }
    }

    private static void EnrichCompany(ApiCompany company, ApiRawJob job)
    {
        if (string.IsNullOrWhiteSpace(company.LogoUrl)      && !string.IsNullOrWhiteSpace(job.CompanyLogoUrl)) company.LogoUrl      = job.CompanyLogoUrl;
        if (string.IsNullOrWhiteSpace(company.Website)      && !string.IsNullOrWhiteSpace(job.CompanyUrl))     company.Website      = job.CompanyUrl;
        if (string.IsNullOrWhiteSpace(company.CompanyType)  && !string.IsNullOrWhiteSpace(job.CompanyType))    company.CompanyType  = job.CompanyType;
        if (string.IsNullOrWhiteSpace(company.ApiCompanyId) && !string.IsNullOrWhiteSpace(job.ApiCompanyId))   company.ApiCompanyId = job.ApiCompanyId;
        company.UpdatedOn = DateTime.UtcNow;
    }

    public async Task<(int inserted, int updated, int errors)> BulkUpsertRawJobsAsync(
        IEnumerable<ApiRawJob> jobs, CancellationToken cancellationToken)
    {
        int inserted = 0, updated = 0, errors = 0;

        foreach (var job in jobs)
        {
            if (cancellationToken.IsCancellationRequested) break;
            try
            {
                var (isNew, _) = await UpsertRawJobAsync(job);
                if (isNew) inserted++; else updated++;
            }
            catch (Exception ex)
            {
                errors++;
                var inner = ex.InnerException?.Message ?? ex.Message;
                _logger.LogError(
                    "==> UpsertRawJob FAILED | Source={Source} SourceId={SourceId} Title={Title} Link={Link} | Error: {Error} | Inner: {Inner}",
                    job.Source, job.SourceId, job.JobTitle, job.JobLink, ex.Message, inner);
            }
        }

        return (inserted, updated, errors);
    }

    // ── Field mapping ────────────────────────────────────────────────────────

    private static void MapUpdatableFields(ApiRawJob dest, ApiRawJob src)
    {
        dest.JobTitle          = src.JobTitle;
        dest.JobDescription    = src.JobDescription;
        dest.JobLink           = src.JobLink;
        dest.CompanyName       = src.CompanyName;
        dest.OurCompanyId      = src.OurCompanyId;
        dest.SalaryMin         = src.SalaryMin;
        dest.SalaryMax         = src.SalaryMax;
        dest.SalaryType        = src.SalaryType;
        dest.SalaryRangeText   = src.SalaryRangeText;
        dest.SalaryCurrency    = src.SalaryCurrency;
        dest.PostDate          = src.PostDate;
        dest.LastDate          = src.LastDate;
        dest.City              = src.City;
        dest.State             = src.State;
        dest.Country           = src.Country;
        dest.Industry          = src.Industry;
        dest.IndustryId        = src.IndustryId;
        dest.Role              = src.Role;
        dest.JobLevel          = src.JobLevel;
        dest.JobWorkMode       = src.JobWorkMode;
        dest.WorkType          = src.WorkType;
        dest.ContractType      = src.ContractType;
        dest.ApplyType         = src.ApplyType;
        dest.EasyApply         = src.EasyApply;
        dest.Sponsorship       = src.Sponsorship;
        dest.WorkAuthorization = src.WorkAuthorization;
        dest.Skills            = src.Skills;
        dest.Requirements      = src.Requirements;
        dest.Responsibilities  = src.Responsibilities;
        dest.ExperienceYears   = src.ExperienceYears;
        dest.ExperienceMin     = src.ExperienceMin;
        dest.ExperienceMax     = src.ExperienceMax;
        dest.HoursBackPosted   = src.HoursBackPosted;
        dest.CompanyLogoUrl    = src.CompanyLogoUrl;
        dest.CompanyUrl        = src.CompanyUrl;
        dest.CompanyType       = src.CompanyType;
        dest.PostedByName      = src.PostedByName;
        dest.PostedByProfileUrl= src.PostedByProfileUrl;
        dest.IsC2C             = src.IsC2C;
        dest.IsW2              = src.IsW2;
        dest.IsStaffing        = src.IsStaffing;
        dest.IsSponsored       = src.IsSponsored;
        dest.IsH1BSponsored    = src.IsH1BSponsored;
        dest.IsUniversityJob   = src.IsUniversityJob;
        dest.IsStartupJob      = src.IsStartupJob;
        dest.IsNonProfitJob    = src.IsNonProfitJob;
        dest.IsContractJob     = src.IsContractJob;
        dest.IsFreelanceJob    = src.IsFreelanceJob;
        dest.IsPrimeVendor     = src.IsPrimeVendor;
        dest.FetchRunId        = src.FetchRunId;
        dest.Source            = src.Source;
    }

    // ── H1B Sponsors ────────────────────────────────────────────────────────

    public async Task<List<string>> GetActiveJobRoleQueriesAsync(CancellationToken cancellationToken = default)
    {
        return await _db.JobRoles
            .AsNoTracking()
            .Where(r => r.IsActive && r.SearchQuery != null)
            .OrderBy(r => r.IndustryId)
            .ThenBy(r => r.Id)
            .Select(r => r.SearchQuery!)
            .ToListAsync(cancellationToken);
    }

    public async Task<List<string>> GetH1BSponsorNamesAsync(CancellationToken cancellationToken = default)
    {
        return await _db.H1bSponsors
            .AsNoTracking()
            .Select(s => s.NormalizedName ?? s.EmployerNameKey)
            .ToListAsync(cancellationToken);
    }

    public async Task<List<ApiH1bSponsor>> GetUnenrichedSponsorsAsync(int batchSize, CancellationToken cancellationToken = default)
    {
        return await _db.H1bSponsors
            .AsNoTracking()
            .Where(s => s.NormalizedName == null)
            .OrderByDescending(s => s.TotalApprovals)
            .Take(batchSize)
            .ToListAsync(cancellationToken);
    }

    public async Task UpdateSponsorNormalizedNameAsync(int id, string normalizedName, CancellationToken cancellationToken = default)
    {
        await _db.H1bSponsors
            .Where(s => s.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.NormalizedName, normalizedName)
                .SetProperty(x => x.EnrichedAt, DateTime.UtcNow),
            cancellationToken);
    }
}
