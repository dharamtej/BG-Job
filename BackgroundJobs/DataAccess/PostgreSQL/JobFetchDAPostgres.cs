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
            c.H1BSponsored  += s.Classifications.H1BSponsored;
            c.Sponsored     += s.Classifications.Sponsored;
            c.W2            += s.Classifications.W2;
            c.C2C           += s.Classifications.C2C;
            c.ContractJob   += s.Classifications.ContractJob;
            c.FreelanceJob  += s.Classifications.FreelanceJob;
            c.PrimeVendor   += s.Classifications.PrimeVendor;
            c.Staffing      += s.Classifications.Staffing;
            c.StartupJob    += s.Classifications.StartupJob;
            c.NonProfitJob  += s.Classifications.NonProfitJob;
            c.UniversityJob += s.Classifications.UniversityJob;
            c.Government    += s.Classifications.Government;
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
                    H1BSponsored  = g.Sum(x => x.IsH1BSponsored == true ? 1 : 0),
                    Sponsored     = g.Sum(x => x.IsSponsored    == true ? 1 : 0),
                    W2            = g.Sum(x => x.IsW2            == true ? 1 : 0),
                    C2C           = g.Sum(x => x.IsC2C           == true ? 1 : 0),
                    ContractJob   = g.Sum(x => x.IsContractJob  == true ? 1 : 0),
                    FreelanceJob  = g.Sum(x => x.IsFreelanceJob == true ? 1 : 0),
                    PrimeVendor   = g.Sum(x => x.IsPrimeVendor  == true ? 1 : 0),
                    Staffing      = g.Sum(x => x.IsStaffing     == true ? 1 : 0),
                    StartupJob    = g.Sum(x => x.IsStartupJob   == true ? 1 : 0),
                    NonProfitJob  = g.Sum(x => x.IsNonProfitJob == true ? 1 : 0),
                    UniversityJob = g.Sum(x => x.IsUniversityJob == true ? 1 : 0),
                    Government    = g.Sum(x => x.CompanyType == "Government" ? 1 : 0),
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

    public async Task<List<TokenStatusCounts>> GetTokenStatsAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<TokenStatusCounts>();
        async Task Add(string source, IQueryable<string?> q)
        {
            try { result.Add(await TokenCountsAsync(source, q, cancellationToken)); }
            catch (PostgresException ex) when (ex.SqlState == "42P01")   // undefined_table
            { _logger.LogWarning("[Tokens] {Source}: table not present in this DB — returning zeros", source); result.Add(new TokenStatusCounts { Source = source }); }
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
                .SetProperty(c => c.LogoUrl,      c => logoUrl      ?? c.LogoUrl)
                .SetProperty(c => c.UpdatedOn,    _ => DateTime.UtcNow),
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
            job.CreatedOn = DateTime.UtcNow;
            job.UpdatedOn = DateTime.UtcNow;
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
        var batch = jobs as IList<ApiRawJob> ?? jobs.ToList();
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

                    ApiRawJob? existing = null;
                    if (!string.IsNullOrWhiteSpace(job.JobLink))
                        existingByLink.TryGetValue(job.JobLink!, out existing);
                    if (existing == null && !string.IsNullOrWhiteSpace(job.SourceId))
                        existingBySourceId.TryGetValue(job.SourceId!, out existing);

                    if (existing == null)
                    {
                        if (string.IsNullOrWhiteSpace(job.PublicId))
                            job.PublicId = Guid.NewGuid().ToString("N");
                        job.CreatedOn = DateTime.UtcNow;
                        job.UpdatedOn = DateTime.UtcNow;
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

    public async Task<List<ApiGreenhouseBoardToken>> GetValidGreenhouseTokensAsync(CancellationToken cancellationToken = default)
    {
        return await _db.GreenhouseBoardTokens
            .AsNoTracking()
            .Where(t => t.Status != "INVALID")
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
            .Where(t => t.Status != "INVALID")
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
            .Where(t => t.Status != "INVALID")
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
            .Where(t => t.Status != "INVALID")
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
            .Where(t => t.Status != "INVALID")
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
            .Where(t => t.Status != "INVALID")
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
            .Where(t => t.Status != "INVALID")
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
}
