// DataAccess/PostgreSQL/JobFetchDAPostgres.cs
using CareerPanda.DataAccess.DA;
using CareerPanda.DataAccess.Entities.Api;
using CareerPanda.DataAccess.Models;
using CareerPanda.Framework;
using CareerPanda.Framework.Util;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

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

    // ── Dashboard stats ───────────────────────────────────────────────────────

    public async Task<JobStatsOverview> GetStatsOverviewAsync(
        int newCompanyWindowHours, CancellationToken cancellationToken = default)
    {
        if (newCompanyWindowHours <= 0) newCompanyWindowHours = 24;

        // One grouped pass over raw_jobs gives per-source job + classification counts.
        var perSource = await GetSourceAggregatesAsync(cancellationToken);

        var since = DateTime.UtcNow.AddHours(-newCompanyWindowHours);
        var totalCompanies = await _db.Companies.AsNoTracking().CountAsync(cancellationToken);
        var newCompanies   = await _db.Companies.AsNoTracking()
            .CountAsync(c => c.CreatedOn >= since, cancellationToken);

        var overview = new JobStatsOverview
        {
            TotalCompanies        = totalCompanies,
            NewCompaniesInWindow  = newCompanies,
            NewCompanyWindowHours = newCompanyWindowHours,
            DistinctSources       = perSource.Count,
            JobsBySource          = perSource.ToDictionary(s => s.Source, s => s.TotalJobs)
        };

        // Roll the per-source rows up into the totals.
        foreach (var s in perSource)
        {
            overview.TotalJobs  += s.TotalJobs;
            overview.ActiveJobs += s.ActiveJobs;
            var c = overview.Classifications;
            c.H1BSponsored             += s.Classifications.H1BSponsored;
            c.Sponsored                += s.Classifications.Sponsored;
            c.OptCpt                   += s.Classifications.OptCpt;
            c.TnVisa                   += s.Classifications.TnVisa;
            c.E3Visa                   += s.Classifications.E3Visa;
            c.J1Visa                   += s.Classifications.J1Visa;
            c.GreenCard                += s.Classifications.GreenCard;
            c.W2                       += s.Classifications.W2;
            c.C2C                      += s.Classifications.C2C;
            c.ContractJob              += s.Classifications.ContractJob;
            c.ContractToHire           += s.Classifications.ContractToHire;
            c.FreelanceJob             += s.Classifications.FreelanceJob;
            c.PrimeVendor              += s.Classifications.PrimeVendor;
            c.Staffing                 += s.Classifications.Staffing;
            c.StartupJob               += s.Classifications.StartupJob;
            c.NonProfitJob             += s.Classifications.NonProfitJob;
            c.UniversityJob            += s.Classifications.UniversityJob;
            c.Government               += s.Classifications.Government;
            c.SecurityClearanceRequired += s.Classifications.SecurityClearanceRequired;
            c.VeteransEligible         += s.Classifications.VeteransEligible;
            var w = overview.WorkModes;
            w.Remote     += s.WorkModes.Remote;
            w.Hybrid     += s.WorkModes.Hybrid;
            w.OnSite     += s.WorkModes.OnSite;
            w.FullTime   += s.WorkModes.FullTime;
            w.PartTime   += s.WorkModes.PartTime;
            w.Contract   += s.WorkModes.Contract;
            w.Internship += s.WorkModes.Internship;
        }

        return overview;
    }

    public async Task<List<HandlerStats>> GetStatsByHandlerAsync(CancellationToken cancellationToken = default)
    {
        var perSource = await GetSourceAggregatesAsync(cancellationToken);

        // Latest fetch run per category (correlated subquery on max started_at).
        var latestRuns = await _db.JobFetchRuns.AsNoTracking()
            .Where(r => r.StartedAt == _db.JobFetchRuns
                .Where(x => x.JobCategory == r.JobCategory)
                .Max(x => x.StartedAt))
            .ToListAsync(cancellationToken);

        // Map a run's category/api-source to the raw_jobs.source value so we can attach it.
        foreach (var run in latestRuns)
        {
            var match = perSource.FirstOrDefault(s =>
                string.Equals(s.Source, run.ApiSource, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s.Source, run.JobCategory?.Replace("Jobs", ""), StringComparison.OrdinalIgnoreCase));

            var summary = new LatestRunSummary
            {
                RunId           = run.Id,
                Status          = run.Status,
                StartedAt       = run.StartedAt,
                CompletedAt     = run.CompletedAt,
                DurationSeconds = run.DurationSeconds,
                TotalFetched    = run.TotalFetched,
                TotalInserted   = run.TotalInserted,
                TotalUpdated    = run.TotalUpdated,
                TotalSkipped    = run.TotalSkipped,
                TotalErrors     = run.TotalErrors,
                UnitsProcessed  = run.PagesFetched,
                ErrorMessage    = run.ErrorMessage
            };

            if (match != null)
                match.LatestRun = summary;
            else
                // Run exists but no jobs yet under that source — surface it anyway.
                perSource.Add(new HandlerStats { Source = run.ApiSource ?? run.JobCategory ?? "Unknown", LatestRun = summary });
        }

        return perSource.OrderByDescending(s => s.TotalJobs).ToList();
    }

    // Single grouped query over raw_jobs → job counts + classification breakdown per source.
    private async Task<List<HandlerStats>> GetSourceAggregatesAsync(CancellationToken cancellationToken)
    {
        return await _db.RawJobs.AsNoTracking()
            .GroupBy(j => j.Source)
            .Select(g => new HandlerStats
            {
                Source            = g.Key ?? "Unknown",
                TotalJobs         = g.Count(),
                ActiveJobs        = g.Sum(x => x.Status == true ? 1 : 0),
                DistinctCompanies = g.Select(x => x.OurCompanyId).Distinct().Count(),
                Classifications   = new ClassificationCounts
                {
                    H1BSponsored             = g.Sum(x => x.IsH1BSponsored            == true ? 1 : 0),
                    Sponsored                = g.Sum(x => x.IsSponsored               == true ? 1 : 0),
                    OptCpt                   = g.Sum(x => x.IsOptCpt                  == true ? 1 : 0),
                    TnVisa                   = g.Sum(x => x.IsTnVisa                  == true ? 1 : 0),
                    E3Visa                   = g.Sum(x => x.IsE3Visa                  == true ? 1 : 0),
                    J1Visa                   = g.Sum(x => x.IsJ1Visa                  == true ? 1 : 0),
                    GreenCard                = g.Sum(x => x.IsGreenCard               == true ? 1 : 0),
                    W2                       = g.Sum(x => x.IsW2                      == true ? 1 : 0),
                    C2C                      = g.Sum(x => x.IsC2C                     == true ? 1 : 0),
                    ContractJob              = g.Sum(x => x.IsContractJob             == true ? 1 : 0),
                    ContractToHire           = g.Sum(x => x.IsContractToHire          == true ? 1 : 0),
                    FreelanceJob             = g.Sum(x => x.IsFreelanceJob            == true ? 1 : 0),
                    PrimeVendor              = g.Sum(x => x.IsPrimeVendor             == true ? 1 : 0),
                    Staffing                 = g.Sum(x => x.IsStaffing                == true ? 1 : 0),
                    StartupJob               = g.Sum(x => x.IsStartupJob              == true ? 1 : 0),
                    NonProfitJob             = g.Sum(x => x.IsNonProfitJob            == true ? 1 : 0),
                    UniversityJob            = g.Sum(x => x.IsUniversityJob           == true ? 1 : 0),
                    Government               = g.Sum(x => x.CompanyType == "Government" ? 1 : 0),
                    SecurityClearanceRequired = g.Sum(x => x.IsSecurityClearanceRequired == true ? 1 : 0),
                    VeteransEligible         = g.Sum(x => x.IsVeteransEligible        == true ? 1 : 0),
                },
                WorkModes = new WorkModeCounts
                {
                    Remote     = g.Sum(x => x.WorkType == "Remote" ? 1 : 0),
                    Hybrid     = g.Sum(x => x.WorkType == "Hybrid" ? 1 : 0),
                    OnSite     = g.Sum(x => x.WorkType == "OnSite" ? 1 : 0),
                    FullTime   = g.Sum(x => x.ContractType == "FullTime"   ? 1 : 0),
                    PartTime   = g.Sum(x => x.ContractType == "PartTime"   ? 1 : 0),
                    Contract   = g.Sum(x => x.ContractType == "Contract"   ? 1 : 0),
                    Internship = g.Sum(x => x.ContractType == "Internship" ? 1 : 0),
                }
            })
            .ToListAsync(cancellationToken);
    }

    // Process-wide skip-list for token sources whose table is known to be missing.
    // Stops the dashboard's 5s poll from spamming the Postgres error log.
    // TTL means we'll retry after the table is (potentially) created — no restart needed.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _missingTokenSources = new();
    private static readonly TimeSpan _missingTokenTtl = TimeSpan.FromMinutes(5);

    public async Task<List<TokenStatusCounts>> GetTokenStatsAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<TokenStatusCounts>();
        async Task Add(string source, IQueryable<string?> q)
        {
            // If we recently observed this source's table missing, return zeros without querying.
            if (_missingTokenSources.TryGetValue(source, out var until) && until > DateTime.UtcNow)
            {
                result.Add(new TokenStatusCounts { Source = source });
                return;
            }
            try
            {
                result.Add(await TokenCountsAsync(source, q, cancellationToken));
                _missingTokenSources.TryRemove(source, out _);   // success — clear any prior skip
            }
            catch (PostgresException ex) when (ex.SqlState == "42P01")   // undefined_table
            {
                _missingTokenSources[source] = DateTime.UtcNow + _missingTokenTtl;
                _logger.LogWarning("[Tokens] {Source}: table not present — suppressing queries for {Min} min", source, _missingTokenTtl.TotalMinutes);
                result.Add(new TokenStatusCounts { Source = source });
            }
            catch (Exception ex)
            { _logger.LogWarning(ex, "[Tokens] {Source}: unavailable — returning zeros", source); result.Add(new TokenStatusCounts { Source = source }); }
        }

        await Add("Greenhouse", _db.GreenhouseBoardTokens.Select(t => (string?)t.Status));
        await Add("Lever",      _db.LeverBoardTokens.Select(t => (string?)t.Status));
        await Add("Ashby",      _db.AshbyBoardTokens.Select(t => (string?)t.Status));
        await Add("Workday",    _db.WorkdayBoardTokens.Select(t => (string?)t.Status));
        await Add("iCIMS",      _db.IcimsBoardTokens.Select(t => (string?)t.Status));
        await Add("BambooHR",   _db.BambooHrBoardTokens.Select(t => (string?)t.Status));
        await Add("Recruitee",  _db.RecruiteeBoardTokens.Select(t => (string?)t.Status));
        return result;
    }

    // Count one token table's status column into valid/invalid/unknown buckets.
    // Simple COUNT(*) predicates translate cleanly to SQL (GroupBy on a cast projection did not).
    private static async Task<TokenStatusCounts> TokenCountsAsync(
        string source, IQueryable<string?> statuses, CancellationToken ct)
    {
        var total   = await statuses.CountAsync(ct);
        var valid   = await statuses.CountAsync(s => s == "VALID", ct);
        var invalid = await statuses.CountAsync(s => s == "INVALID", ct);
        return new TokenStatusCounts
        {
            Source  = source,
            Valid   = valid,
            Invalid = invalid,
            Unknown = total - valid - invalid,   // UNKNOWN / EMPTY / null / other
            Total   = total,
        };
    }

    // ── Company enrichment ────────────────────────────────────────────────────

    public async Task<List<ApiCompany>> GetCompaniesForEnrichmentAsync(int afterId, int batchSize, CancellationToken cancellationToken = default)
    {
        return await _db.Companies.AsNoTracking()
            .Where(c => c.Id > afterId)
            .OrderBy(c => c.Id)
            .Take(batchSize)
            .ToListAsync(cancellationToken);
    }

    public async Task<string?> GetSampleCompanyUrlAsync(int ourCompanyId, CancellationToken cancellationToken = default)
    {
        return await _db.RawJobs.AsNoTracking()
            .Where(j => j.OurCompanyId == ourCompanyId && j.CompanyUrl != null && j.CompanyUrl != "")
            .Select(j => j.CompanyUrl)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task UpdateCompanyEnrichmentAsync(int id, string? companyType, int? companySize, string? aboutCompany,
        string? website, string? careerPage, string? logoUrl, CancellationToken cancellationToken = default)
    {
        // COALESCE semantics — a null argument keeps the existing column value.
        await _db.Companies
            .Where(c => c.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.CompanyType,  c => companyType  ?? c.CompanyType)
                .SetProperty(c => c.CompanySize,  c => companySize  ?? c.CompanySize)
                .SetProperty(c => c.AboutCompany, c => aboutCompany ?? c.AboutCompany)
                .SetProperty(c => c.Website,      c => website      ?? c.Website)
                .SetProperty(c => c.CareerPage,   c => careerPage   ?? c.CareerPage)
                // Only write a new logo if it's a real one — never blank a real logo, never write a favicon.
                .SetProperty(c => c.LogoUrl,      c => (logoUrl != null && !logoUrl.Contains("google.com/s2/favicons"))
                                                        ? logoUrl
                                                        : c.LogoUrl)
                .SetProperty(c => c.UpdatedOn,    _ => DateTime.UtcNow),
            cancellationToken);
    }

    // ── Reclassify existing raw_jobs ──────────────────────────────────────────

    public async Task<List<ApiRawJob>> GetRawJobsForReclassifyAsync(int afterId, int batchSize, CancellationToken cancellationToken = default)
    {
        return await _db.RawJobs.AsNoTracking()
            .Where(j => j.Id > afterId)
            .OrderBy(j => j.Id)
            .Take(batchSize)
            .ToListAsync(cancellationToken);
    }

    public async Task UpdateClassificationFlagsAsync(ApiRawJob job, CancellationToken cancellationToken = default)
    {
        await _db.RawJobs.Where(j => j.Id == job.Id).ExecuteUpdateAsync(s => s
            .SetProperty(j => j.IsC2C,            job.IsC2C)
            .SetProperty(j => j.IsContractToHire, job.IsContractToHire)
            .SetProperty(j => j.IsFreelanceJob,   job.IsFreelanceJob)
            .SetProperty(j => j.IsW2,             job.IsW2)
            .SetProperty(j => j.IsContractJob,    job.IsContractJob)
            .SetProperty(j => j.IsPrimeVendor,    job.IsPrimeVendor)
            .SetProperty(j => j.IsStaffing,       job.IsStaffing)
            .SetProperty(j => j.IsH1BSponsored,   job.IsH1BSponsored)
            .SetProperty(j => j.IsSponsored,      job.IsSponsored)
            .SetProperty(j => j.IsOptCpt,         job.IsOptCpt)
            .SetProperty(j => j.IsTnVisa,         job.IsTnVisa)
            .SetProperty(j => j.IsE3Visa,         job.IsE3Visa)
            .SetProperty(j => j.IsJ1Visa,         job.IsJ1Visa)
            .SetProperty(j => j.IsGreenCard,      job.IsGreenCard)
            .SetProperty(j => j.IsStartupJob,                    job.IsStartupJob)
            .SetProperty(j => j.IsNonProfitJob,                  job.IsNonProfitJob)
            .SetProperty(j => j.IsUniversityJob,                 job.IsUniversityJob)
            .SetProperty(j => j.IsSecurityClearanceRequired,     job.IsSecurityClearanceRequired)
            .SetProperty(j => j.IsVeteransEligible,             job.IsVeteransEligible)
            .SetProperty(j => j.JobLevel,                       job.JobLevel)
            .SetProperty(j => j.UpdatedOn,                      DateTime.UtcNow),
        cancellationToken);
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
            job.CreatedOn  = DateTime.UtcNow;
            job.UpdatedOn  = DateTime.UtcNow;
            job.NormStatus = "pending";
            _db.RawJobs.Add(job);
            try
            {
                await _db.SaveChangesAsync();
                return (true, job.Id);
            }
            catch
            {
                _db.Entry(job).State = EntityState.Detached;
                throw;
            }
        }
        else
        {
            // UPDATE — refresh all mutable fields
            MapUpdatableFields(existing, job);
            existing.UpdatedOn = DateTime.UtcNow;
            try
            {
                await _db.SaveChangesAsync();
                return (false, existing.Id);
            }
            catch
            {
                _db.Entry(existing).State = EntityState.Detached;
                throw;
            }
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

    // Returns true only for real logo URLs — not Google favicon fallbacks.
    private static bool IsRealLogo(string? url) =>
        !string.IsNullOrWhiteSpace(url) &&
        !url.Contains("google.com/s2/favicons", StringComparison.OrdinalIgnoreCase);

    private static void EnrichCompany(ApiCompany company, ApiRawJob job)
    {
        // Overwrite Google favicon placeholders with real logos from the source API.
        if (!IsRealLogo(company.LogoUrl)     && IsRealLogo(job.CompanyLogoUrl))     company.LogoUrl      = job.CompanyLogoUrl;
        if (string.IsNullOrWhiteSpace(company.Website)      && !string.IsNullOrWhiteSpace(job.CompanyUrl))     company.Website      = job.CompanyUrl;
        if (string.IsNullOrWhiteSpace(company.CompanyType)  && !string.IsNullOrWhiteSpace(job.CompanyType))    company.CompanyType  = job.CompanyType;
        if (string.IsNullOrWhiteSpace(company.ApiCompanyId) && !string.IsNullOrWhiteSpace(job.ApiCompanyId))   company.ApiCompanyId = job.ApiCompanyId;
        if (string.IsNullOrWhiteSpace(company.AboutCompany) && !string.IsNullOrWhiteSpace(job.CompanyAbout))   company.AboutCompany = job.CompanyAbout;
        if (company.CompanySize == null                     && job.CompanySize.HasValue)                        company.CompanySize  = job.CompanySize;
        if (string.IsNullOrWhiteSpace(company.CareerPage)   && !string.IsNullOrWhiteSpace(job.JobLink))        company.CareerPage   = job.JobLink;
        company.UpdatedOn = DateTime.UtcNow;
    }

    public async Task<(int inserted, int updated, int errors)> BulkUpsertRawJobsAsync(
        IEnumerable<ApiRawJob> jobs, CancellationToken cancellationToken)
    {
        int inserted = 0, updated = 0, errors = 0;
        var batch = jobs as IList<ApiRawJob> ?? jobs.ToList();
        if (batch.Count == 0) return (0, 0, 0);

        // Exclude jobs with no description — without it the classifier cannot set any flags
        // and the job provides no value to the platform. These count as skipped by the caller.
        batch = batch.Where(j => !string.IsNullOrWhiteSpace(j.JobDescription)).ToList();
        if (batch.Count == 0) return (0, 0, 0);

        // Group by (Source, CompanyName) — each ATS handler typically sends one company per call,
        // so this collapses to a single group and a single company resolution.
        var groups = batch.GroupBy(j => (j.Source ?? "", (j.CompanyName ?? "").Trim().ToLowerInvariant()));

        foreach (var group in groups)
        {
            if (cancellationToken.IsCancellationRequested) break;

            var groupJobs = group.ToList();
            int batchInserted = 0, batchUpdated = 0;
            try
            {
                // 1. Resolve company once for the whole group
                int companyId = 0;
                var first = groupJobs[0];
                if (!string.IsNullOrWhiteSpace(first.CompanyName))
                    companyId = await ResolveOrCreateCompanyAsync(first);

                // 2. Batch-load existing rows by (source, source_id) and by job_link
                var source = first.Source ?? "";
                var sourceIds = groupJobs
                    .Where(j => !string.IsNullOrWhiteSpace(j.SourceId))
                    .Select(j => j.SourceId!)
                    .Distinct()
                    .ToList();
                var jobLinks = groupJobs
                    .Where(j => !string.IsNullOrWhiteSpace(j.JobLink))
                    .Select(j => j.JobLink!)
                    .Distinct()
                    .ToList();

                var existingBySourceId = sourceIds.Count > 0
                    ? await _db.RawJobs
                        .Where(j => j.Source == source && sourceIds.Contains(j.SourceId!))
                        .ToDictionaryAsync(j => j.SourceId!, cancellationToken)
                    : new Dictionary<string, ApiRawJob>();

                var existingByLink = jobLinks.Count > 0
                    ? await _db.RawJobs
                        .Where(j => j.JobLink != null && jobLinks.Contains(j.JobLink))
                        .ToDictionaryAsync(j => j.JobLink!, cancellationToken)
                    : new Dictionary<string, ApiRawJob>();

                // 3. Walk the batch, classify each row as insert/update, accumulate in-memory
                var toAdd = new List<ApiRawJob>();
                foreach (var job in groupJobs)
                {
                    job.OurCompanyId = companyId;

                    // Single chokepoint that tags C2C / C2H / 1099 / W2 / Contract / PrimeVendor / Staffing /
                    // H1B / OPT-CPT / TN / E-3 / J-1 / GreenCard / Sponsored / Startup / NonProfit / University
                    // from the job description + company name. Preserves any flag the handler already set to true.
                    CareerPanda.DataAccess.Util.JobClassifier.ApplyKeywordFlags(job, job.JobDescription, employmentType: job.ContractType, companyName: job.CompanyName);

                    ApiRawJob? existing = null;
                    if (!string.IsNullOrWhiteSpace(job.JobLink))
                        existingByLink.TryGetValue(job.JobLink!, out existing);
                    if (existing == null && !string.IsNullOrWhiteSpace(job.SourceId))
                        existingBySourceId.TryGetValue(job.SourceId!, out existing);

                    if (existing == null)
                    {
                        if (string.IsNullOrWhiteSpace(job.PublicId))
                            job.PublicId = Guid.NewGuid().ToString("N");
                        job.CreatedOn  = DateTime.UtcNow;
                        job.UpdatedOn  = DateTime.UtcNow;
                        job.NormStatus = "pending";
                        toAdd.Add(job);
                        batchInserted++;
                    }
                    else
                    {
                        MapUpdatableFields(existing, job);
                        existing.UpdatedOn = DateTime.UtcNow;
                        batchUpdated++;
                    }
                }

                if (toAdd.Count > 0)
                    _db.RawJobs.AddRange(toAdd);

                // 4. Single SaveChanges for the whole company batch
                await _db.SaveChangesAsync(cancellationToken);

                // Only commit the batch counts to the totals AFTER a successful save.
                inserted += batchInserted;
                updated  += batchUpdated;

                // 5. Detach so the change-tracker doesn't bloat for the next batch
                foreach (var j in toAdd) _db.Entry(j).State = EntityState.Detached;
                foreach (var j in existingBySourceId.Values) _db.Entry(j).State = EntityState.Detached;
                foreach (var j in existingByLink.Values)
                    if (_db.Entry(j).State != EntityState.Detached) _db.Entry(j).State = EntityState.Detached;
            }
            catch (Exception ex)
            {
                // Reset the context BEFORE any rethrow so the next batch starts clean
                foreach (var entry in _db.ChangeTracker.Entries().ToList())
                    entry.State = EntityState.Detached;

                // Detect fatal DB conditions — abort the entire run instead of continuing
                var pg = FindPostgresException(ex);
                if (pg != null && IsFatalSqlState(pg.SqlState))
                {
                    _logger.LogCritical(
                        "==> FATAL DB error ({SqlState}) — aborting run | Source={Source} Company={Company} | {Message}",
                        pg.SqlState, groupJobs[0].Source, groupJobs[0].CompanyName, pg.Message);
                    throw new FatalDatabaseException(
                        $"Fatal Postgres error {pg.SqlState}: {pg.Message}", pg.SqlState, ex);
                }

                // Batch never committed — only the error count goes up.
                // batchInserted/batchUpdated were locals, so they're discarded automatically.
                errors += groupJobs.Count;
                var inner = ex.InnerException?.Message ?? ex.Message;
                _logger.LogError(
                    "==> BulkUpsert batch FAILED | Source={Source} Company={Company} Count={Count} | Error: {Error} | Inner: {Inner}",
                    groupJobs[0].Source, groupJobs[0].CompanyName, groupJobs.Count, ex.Message, inner);
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
        dest.IsOptCpt          = src.IsOptCpt;
        dest.IsTnVisa          = src.IsTnVisa;
        dest.IsE3Visa          = src.IsE3Visa;
        dest.IsJ1Visa          = src.IsJ1Visa;
        dest.IsGreenCard       = src.IsGreenCard;
        dest.IsUniversityJob   = src.IsUniversityJob;
        dest.IsStartupJob      = src.IsStartupJob;
        dest.IsNonProfitJob    = src.IsNonProfitJob;
        dest.IsContractJob     = src.IsContractJob;
        dest.IsFreelanceJob    = src.IsFreelanceJob;
        dest.IsPrimeVendor                = src.IsPrimeVendor;
        dest.IsSecurityClearanceRequired  = src.IsSecurityClearanceRequired;
        dest.IsVeteransEligible           = src.IsVeteransEligible;
        dest.FetchRunId                   = src.FetchRunId;
        dest.Source                       = src.Source;
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

    public async Task<List<ApiGreenhouseBoardToken>> GetValidGreenhouseTokensAsync(CancellationToken cancellationToken = default)
    {
        return await _db.GreenhouseBoardTokens
            .AsNoTracking()
            // Retry INVALID tokens after a 14-day cooldown — many INVALIDs were caused
            // by transient WAF / rate-limit 403s, not "board doesn't exist" 404s.
            .Where(t => t.Status != "INVALID" || t.UpdatedAt < DateTime.UtcNow.AddDays(-14))
            .OrderBy(t => t.CompanyName)
            .ToListAsync(cancellationToken);
    }

    public async Task UpdateGreenhouseTokenStatusAsync(int id, string status, short httpCode, int jobCount, CancellationToken cancellationToken = default)
    {
        await _db.GreenhouseBoardTokens
            .Where(t => t.Id == id)
            .ExecuteUpdateAsync(t => t
                .SetProperty(x => x.Status,    status)
                .SetProperty(x => x.HttpCode,  httpCode)
                .SetProperty(x => x.JobCount,  jobCount)
                .SetProperty(x => x.UpdatedAt, DateTime.UtcNow),
            cancellationToken);
    }

    public async Task<List<ApiLeverBoardToken>> GetActiveLeverTokensAsync(CancellationToken cancellationToken = default)
    {
        return await _db.LeverBoardTokens
            .AsNoTracking()
            // Retry INVALID tokens after a 14-day cooldown — many INVALIDs were caused
            // by transient WAF / rate-limit 403s, not "board doesn't exist" 404s.
            .Where(t => t.Status != "INVALID" || t.UpdatedAt < DateTime.UtcNow.AddDays(-14))
            .OrderBy(t => t.CompanyName)
            .ToListAsync(cancellationToken);
    }

    public async Task UpdateLeverTokenStatusAsync(int id, string status, short httpCode, int jobCount, CancellationToken cancellationToken = default)
    {
        await _db.LeverBoardTokens
            .Where(t => t.Id == id)
            .ExecuteUpdateAsync(t => t
                .SetProperty(x => x.Status,    status)
                .SetProperty(x => x.HttpCode,  httpCode)
                .SetProperty(x => x.JobCount,  jobCount)
                .SetProperty(x => x.UpdatedAt, DateTime.UtcNow),
            cancellationToken);
    }

    public async Task<List<ApiWorkdayBoardToken>> GetActiveWorkdayTokensAsync(CancellationToken cancellationToken = default)
    {
        return await _db.WorkdayBoardTokens
            .AsNoTracking()
            // Retry INVALID tokens after a 14-day cooldown — many INVALIDs were caused
            // by transient WAF / rate-limit 403s, not "board doesn't exist" 404s.
            .Where(t => t.Status != "INVALID" || t.UpdatedAt < DateTime.UtcNow.AddDays(-14))
            .OrderBy(t => t.CompanyName)
            .ThenBy(t => t.SiteId)
            .ToListAsync(cancellationToken);
    }

    public async Task UpdateWorkdayTokenStatusAsync(int id, string status, short httpCode, int jobCount, CancellationToken cancellationToken = default)
    {
        await _db.WorkdayBoardTokens
            .Where(t => t.Id == id)
            .ExecuteUpdateAsync(t => t
                .SetProperty(x => x.Status,    status)
                .SetProperty(x => x.HttpCode,  httpCode)
                .SetProperty(x => x.JobCount,  jobCount)
                .SetProperty(x => x.UpdatedAt, DateTime.UtcNow),
            cancellationToken);
    }

    // ── Fatal Postgres error detection ──────────────────────────────────────
    // SQLSTATE classes that indicate the DB itself is unhealthy and further
    // work cannot succeed. Hitting any of these aborts the run immediately.
    private static readonly HashSet<string> FatalSqlStates = new(StringComparer.Ordinal)
    {
        "53100", // disk_full
        "53200", // out_of_memory
        "53300", // too_many_connections
        "53400", // configuration_limit_exceeded
        "57P01", // admin_shutdown
        "57P02", // crash_shutdown
        "57P03", // cannot_connect_now
        "58000", // system_error
        "58030", // io_error
        "XX000"  // internal_error
    };

    private static bool IsFatalSqlState(string? sqlState)
    {
        if (string.IsNullOrEmpty(sqlState)) return false;
        // Class 08 = Connection Exception — all variants are fatal
        if (sqlState.StartsWith("08", StringComparison.Ordinal)) return true;
        return FatalSqlStates.Contains(sqlState);
    }

    private static NpgsqlException? FindPostgresException(Exception? ex)
    {
        while (ex != null)
        {
            if (ex is PostgresException pg) return pg;
            if (ex is NpgsqlException ne) return ne;
            ex = ex.InnerException;
        }
        return null;
    }

    public async Task<int> RecoverOrphanedFetchRunsAsync(CancellationToken cancellationToken = default)
    {
        return await _db.JobFetchRuns
            .Where(r => r.Status == "Running")
            .ExecuteUpdateAsync(r => r
                .SetProperty(x => x.Status,       "Failed")
                .SetProperty(x => x.ErrorMessage, "Orphaned by app restart — marked Failed on startup.")
                .SetProperty(x => x.CompletedAt,  DateTime.UtcNow),
            cancellationToken);
    }

    public async Task<List<ApiAshbyBoardToken>> GetActiveAshbyTokensAsync(CancellationToken cancellationToken = default)
    {
        return await _db.AshbyBoardTokens
            .AsNoTracking()
            // Retry INVALID tokens after a 14-day cooldown — many INVALIDs were caused
            // by transient WAF / rate-limit 403s, not "board doesn't exist" 404s.
            .Where(t => t.Status != "INVALID" || t.UpdatedAt < DateTime.UtcNow.AddDays(-14))
            .OrderBy(t => t.CompanyName)
            .ToListAsync(cancellationToken);
    }

    public async Task UpdateAshbyTokenStatusAsync(int id, string status, short httpCode, int jobCount, CancellationToken cancellationToken = default)
    {
        await _db.AshbyBoardTokens
            .Where(t => t.Id == id)
            .ExecuteUpdateAsync(t => t
                .SetProperty(x => x.Status,    status)
                .SetProperty(x => x.HttpCode,  httpCode)
                .SetProperty(x => x.JobCount,  jobCount)
                .SetProperty(x => x.UpdatedAt, DateTime.UtcNow),
            cancellationToken);
    }

    public async Task<List<ApiBambooHrBoardToken>> GetActiveBambooHrTokensAsync(CancellationToken cancellationToken = default)
    {
        return await _db.BambooHrBoardTokens
            .AsNoTracking()
            // Retry INVALID tokens after a 14-day cooldown — many INVALIDs were caused
            // by transient WAF / rate-limit 403s, not "board doesn't exist" 404s.
            .Where(t => t.Status != "INVALID" || t.UpdatedAt < DateTime.UtcNow.AddDays(-14))
            .OrderBy(t => t.CompanyName)
            .ToListAsync(cancellationToken);
    }

    public async Task UpdateBambooHrTokenStatusAsync(int id, string status, short httpCode, int jobCount, CancellationToken cancellationToken = default)
    {
        await _db.BambooHrBoardTokens
            .Where(t => t.Id == id)
            .ExecuteUpdateAsync(t => t
                .SetProperty(x => x.Status,    status)
                .SetProperty(x => x.HttpCode,  httpCode)
                .SetProperty(x => x.JobCount,  jobCount)
                .SetProperty(x => x.UpdatedAt, DateTime.UtcNow),
            cancellationToken);
    }

    public async Task<List<ApiIcimsBoardToken>> GetActiveIcimsTokensAsync(CancellationToken cancellationToken = default)
    {
        return await _db.IcimsBoardTokens
            .AsNoTracking()
            // Retry INVALID tokens after a 14-day cooldown — many INVALIDs were caused
            // by transient WAF / rate-limit 403s, not "board doesn't exist" 404s.
            .Where(t => t.Status != "INVALID" || t.UpdatedAt < DateTime.UtcNow.AddDays(-14))
            .OrderBy(t => t.CompanyName)
            .ToListAsync(cancellationToken);
    }

    public async Task UpdateIcimsTokenStatusAsync(int id, string status, short httpCode, int jobCount, CancellationToken cancellationToken = default)
    {
        await _db.IcimsBoardTokens
            .Where(t => t.Id == id)
            .ExecuteUpdateAsync(t => t
                .SetProperty(x => x.Status,    status)
                .SetProperty(x => x.HttpCode,  httpCode)
                .SetProperty(x => x.JobCount,  jobCount)
                .SetProperty(x => x.UpdatedAt, DateTime.UtcNow),
            cancellationToken);
    }

    public async Task<List<ApiRecruiteeBoardToken>> GetActiveRecruiteeTokensAsync(CancellationToken cancellationToken = default)
    {
        return await _db.RecruiteeBoardTokens
            .AsNoTracking()
            // Retry INVALID tokens after a 14-day cooldown — many INVALIDs were caused
            // by transient WAF / rate-limit 403s, not "board doesn't exist" 404s.
            .Where(t => t.Status != "INVALID" || t.UpdatedAt < DateTime.UtcNow.AddDays(-14))
            .OrderBy(t => t.CompanyName)
            .ToListAsync(cancellationToken);
    }

    public async Task UpdateRecruiteeTokenStatusAsync(int id, string status, short httpCode, int jobCount, CancellationToken cancellationToken = default)
    {
        await _db.RecruiteeBoardTokens
            .Where(t => t.Id == id)
            .ExecuteUpdateAsync(t => t
                .SetProperty(x => x.Status,    status)
                .SetProperty(x => x.HttpCode,  httpCode)
                .SetProperty(x => x.JobCount,  jobCount)
                .SetProperty(x => x.UpdatedAt, DateTime.UtcNow),
            cancellationToken);
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

    // ── Normalization ────────────────────────────────────────────────────────

    public Task<List<ApiRawJob>> GetRawJobsForNormalizationAsync(int afterId, int batchSize, CancellationToken ct = default)
        => _db.RawJobs.AsNoTracking()
            .Where(j => j.Id > afterId && (j.NormStatus == null || j.NormStatus == "pending"))
            .OrderBy(j => j.Id)
            .Take(batchSize)
            .ToListAsync(ct);

    public async Task<Dictionary<string, int>> GetIndustryAliasMapAsync(CancellationToken ct = default)
        => await _db.IndustryAliases.AsNoTracking()
            .ToDictionaryAsync(a => a.Alias, a => a.IndustryId, ct);

    public async Task<Dictionary<string, int>> GetJobRoleAliasMapAsync(CancellationToken ct = default)
        => await _db.JobRoleAliases.AsNoTracking()
            .ToDictionaryAsync(a => a.Alias, a => a.JobRoleId, ct);

    public Task UpdateJobNormalizationAsync(int jobId, int? industryId, int? jobRoleId, string normStatus, CancellationToken ct = default)
        => _db.RawJobs
            .Where(j => j.Id == jobId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.IndustryId,  industryId)
                .SetProperty(x => x.JobRoleId,   jobRoleId)
                .SetProperty(x => x.NormStatus,  normStatus),
            ct);

    public async Task TryAddIndustryAliasAsync(string alias, int industryId, string? source, CancellationToken ct = default)
    {
        alias = alias.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(alias)) return;
        if (await _db.IndustryAliases.AnyAsync(a => a.Alias == alias, ct)) return;
        _db.IndustryAliases.Add(new CareerPanda.DataAccess.Entities.Md.MdIndustryAlias
        {
            Alias = alias, IndustryId = industryId, Source = source, CreatedOn = DateTime.UtcNow
        });
        try { await _db.SaveChangesAsync(ct); }
        catch { _db.ChangeTracker.Clear(); } // ignore unique constraint race
    }

    public async Task TryAddJobRoleAliasAsync(string alias, int jobRoleId, string? source, CancellationToken ct = default)
    {
        alias = alias.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(alias)) return;
        if (await _db.JobRoleAliases.AnyAsync(a => a.Alias == alias, ct)) return;
        _db.JobRoleAliases.Add(new CareerPanda.DataAccess.Entities.Md.MdJobRoleAlias
        {
            Alias = alias, JobRoleId = jobRoleId, Source = source, CreatedOn = DateTime.UtcNow
        });
        try { await _db.SaveChangesAsync(ct); }
        catch { _db.ChangeTracker.Clear(); }
    }

    // ── Reference data ───────────────────────────────────────────────────────

    public Task<List<IndustryDto>> GetActiveIndustriesAsync(CancellationToken ct = default)
        => _db.Industries.AsNoTracking()
            .Where(i => i.IsActive)
            .OrderBy(i => i.Name)
            .Select(i => new IndustryDto { Id = (int)i.Id, Slug = i.Slug, Name = i.Name })
            .ToListAsync(ct);

    public Task<List<JobRoleDto>> GetActiveJobRolesAsync(int? industryId = null, CancellationToken ct = default)
        => _db.JobRoles.AsNoTracking()
            .Where(r => r.IsActive && (industryId == null || r.IndustryId == industryId))
            .OrderBy(r => r.IndustryId)
            .ThenBy(r => r.Name)
            .Select(r => new JobRoleDto
            {
                Id = (int)r.Id, IndustryId = r.IndustryId,
                Slug = r.Slug, Name = r.Name, SearchQuery = r.SearchQuery
            })
            .ToListAsync(ct);

    // ── Portal job search ────────────────────────────────────────────────────

    public async Task<(List<RawJobSearchResult> Items, int Total)> SearchRawJobsAsync(
        RawJobSearchQuery query, CancellationToken ct = default)
    {
        var q = _db.RawJobs.AsNoTracking()
            .Where(j => j.Status == true && j.Country == "US");

        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            var kw = query.Keyword.ToLower();
            q = q.Where(j => j.JobTitle.ToLower().Contains(kw) ||
                              (j.JobDescription != null && j.JobDescription.ToLower().Contains(kw)));
        }

        if (query.IndustryIds is { Length: > 0 })
            q = q.Where(j => j.IndustryId != null && query.IndustryIds.Contains(j.IndustryId.Value));

        if (query.JobRoleIds is { Length: > 0 })
            q = q.Where(j => j.JobRoleId != null && query.JobRoleIds.Contains(j.JobRoleId.Value));

        if (query.WorkTypes is { Length: > 0 })
            q = q.Where(j => j.WorkType != null && query.WorkTypes.Contains(j.WorkType));

        if (query.ContractTypes is { Length: > 0 })
            q = q.Where(j => j.ContractType != null && query.ContractTypes.Contains(j.ContractType));

        if (query.States is { Length: > 0 })
            q = q.Where(j => j.State != null && query.States.Contains(j.State));

        if (!string.IsNullOrWhiteSpace(query.JobLevel))
            q = q.Where(j => j.JobLevel == query.JobLevel);

        if (query.SalaryMin.HasValue)
            q = q.Where(j => j.SalaryMin >= query.SalaryMin.Value || j.SalaryMax >= query.SalaryMin.Value);

        if (query.PostedWithinDays.HasValue)
        {
            var cutoff = DateTime.UtcNow.AddDays(-query.PostedWithinDays.Value);
            q = q.Where(j => j.PostDate >= cutoff);
        }

        if (query.H1BSponsored == true)  q = q.Where(j => j.IsH1BSponsored == true);
        if (query.OptCpt == true)         q = q.Where(j => j.IsOptCpt == true);
        if (query.GreenCard == true)      q = q.Where(j => j.IsGreenCard == true);
        if (query.Remote == true)         q = q.Where(j => j.WorkType == "Remote");

        var total = await q.CountAsync(ct);

        var page     = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);

        // Page raw_jobs first, then enrich — avoids EF Core GroupJoin translation issues
        var pagedJobs = await q
            .OrderByDescending(j => j.PostDate)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        if (pagedJobs.Count == 0)
            return ([], total);

        // Load company/industry/role lookup data for only these jobs
        var companyIds  = pagedJobs.Select(j => j.OurCompanyId).Distinct().ToList();
        var industryIds = pagedJobs.Where(j => j.IndustryId.HasValue).Select(j => j.IndustryId!.Value).Distinct().ToList();
        var roleIds     = pagedJobs.Where(j => j.JobRoleId.HasValue).Select(j => j.JobRoleId!.Value).Distinct().ToList();

        var companies  = await _db.Companies.AsNoTracking()
            .Where(c => companyIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, ct);
        var industries = industryIds.Count > 0
            ? await _db.Industries.AsNoTracking()
                .Where(i => industryIds.Contains((int)i.Id))
                .ToDictionaryAsync(i => (int)i.Id, ct)
            : new Dictionary<int, CareerPanda.DataAccess.Entities.Md.MdIndustry>();
        var roles = roleIds.Count > 0
            ? await _db.JobRoles.AsNoTracking()
                .Where(r => roleIds.Contains((int)r.Id))
                .ToDictionaryAsync(r => (int)r.Id, ct)
            : new Dictionary<int, CareerPanda.DataAccess.Entities.Md.MdJobRole>();

        var items = pagedJobs.Select(j =>
        {
            companies.TryGetValue(j.OurCompanyId, out var co);
            var indName  = j.IndustryId.HasValue && industries.TryGetValue(j.IndustryId.Value, out var ind) ? ind.Name : null;
            var roleName = j.JobRoleId.HasValue  && roles.TryGetValue(j.JobRoleId.Value,  out var ro)  ? ro.Name  : null;
            return new RawJobSearchResult
            {
                Id             = j.Id,
                PublicId       = j.PublicId,
                JobTitle       = j.JobTitle,
                JobLink        = j.JobLink,
                CompanyName    = j.CompanyName ?? co?.CompanyName ?? "",
                CompanyLogo    = co?.LogoUrl,
                CompanyWebsite = co?.Website,
                Industry       = indName,
                Role           = roleName,
                WorkType       = j.WorkType,
                ContractType   = j.ContractType,
                JobLevel       = j.JobLevel,
                City           = j.City,
                State          = j.State,
                SalaryMin      = j.SalaryMin,
                SalaryMax      = j.SalaryMax,
                SalaryType     = j.SalaryType,
                PostDate       = j.PostDate,
                IsH1BSponsored = j.IsH1BSponsored,
                IsOptCpt       = j.IsOptCpt,
                IsGreenCard    = j.IsGreenCard,
                Source         = j.Source
            };
        }).ToList();

        return (items, total);
    }
}
