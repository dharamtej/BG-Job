using Microsoft.EntityFrameworkCore;

namespace CareerPanda.Framework.Logger;

public class ApplicationLogDbContext : DbContext
{
    public ApplicationLogDbContext(DbContextOptions<ApplicationLogDbContext> options) : base(options)
    {
    }

    public DbSet<ApplicationLogEntry> Logs { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<ApplicationLogEntry>(e =>
        {
            e.ToTable("application_logs", "public");
            e.HasIndex(l => l.Timestamp);
            e.Property(l => l.Message).HasMaxLength(4000);
            e.Property(l => l.Source).HasMaxLength(512);
            e.Property(l => l.Level).HasMaxLength(32);
        });
    }
}
