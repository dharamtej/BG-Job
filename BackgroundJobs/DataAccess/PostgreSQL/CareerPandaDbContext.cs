using CareerPanda.DataAccess.Entities.Api;
using CareerPanda.DataAccess.Entities.Cp;
using Microsoft.EntityFrameworkCore;

namespace CareerPanda.DataAccess.PostgreSQL;

public partial class CareerPandaDbContext : DbContext
{
    public CareerPandaDbContext(DbContextOptions<CareerPandaDbContext> options) : base(options)
    {
    }

    /// <summary>API async tasks (table created via tools/sql/create_api_background_tasks.sql).</summary>
    public DbSet<BackgroundTask> BackgroundTasks { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<CpUser>(e =>
        {
            e.HasIndex(u => u.Email).IsUnique();
            e.HasIndex(u => u.ClerkUserId).IsUnique();
        });

        modelBuilder.Entity<BackgroundTask>(e =>
        {
            e.Property(t => t.Status).HasConversion<string>();
        });

        ConfigureEnums(modelBuilder);
        ConfigureCompositeKeys(modelBuilder);
    }

    static partial void ConfigureEnums(ModelBuilder modelBuilder);
    static partial void ConfigureCompositeKeys(ModelBuilder modelBuilder);
}
