// DataAccess/PostgreSQL/CareerPandaDbContext.JobFetch.cs
// Registers ApiJobFetchRun → api.job_fetch_runs
using CareerPanda.DataAccess.Entities.Api;
using Microsoft.EntityFrameworkCore;

namespace CareerPanda.DataAccess.PostgreSQL;

public partial class CareerPandaDbContext
{
    public DbSet<ApiJobFetchRun> JobFetchRuns { get; set; } = null!;
}
