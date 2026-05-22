using CareerPanda.DataAccess.Entities.Cp;
using Microsoft.EntityFrameworkCore;

namespace CareerPanda.DataAccess.PostgreSQL;

public partial class CareerPandaDbContext
{
    static partial void ConfigureCompositeKeys(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CpCompanyFollower>()
            .HasKey(e => new { e.CompanyId, e.UserId });

        modelBuilder.Entity<CpExperienceSkill>()
            .HasKey(e => new { e.ExperienceId, e.SkillId });

        modelBuilder.Entity<CpPostTerm>()
            .HasKey(e => new { e.PostId, e.TermId });

        modelBuilder.Entity<CpProfileSkill>()
            .HasKey(e => new { e.ResumeId, e.SkillId });

        modelBuilder.Entity<CpProjectSkill>()
            .HasKey(e => new { e.ProjectId, e.SkillId });
    }
}
