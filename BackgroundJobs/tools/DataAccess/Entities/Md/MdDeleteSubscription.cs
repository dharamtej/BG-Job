using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CareerPanda.DataAccess.Entities.Md;

[Table("delete_subscription", Schema = "md")]
public partial class MdDeleteSubscription
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("subscription_id")]
    public int? SubscriptionId { get; set; }

    [Column("max_resumes_per_day")]
    public int? MaxResumesPerDay { get; set; }

    [Column("max_resumes_per_week")]
    public int? MaxResumesPerWeek { get; set; }

    [Column("max_resumes_per_month")]
    public int? MaxResumesPerMonth { get; set; }

    [Column("max_cover_letters_per_month")]
    public int? MaxCoverLettersPerMonth { get; set; }

    [Column("max_cover_letters_per_day")]
    public int? MaxCoverLettersPerDay { get; set; }

    [Column("max_cover_letters_per_week")]
    public int? MaxCoverLettersPerWeek { get; set; }

    [Column("max_ats_runs_per_day")]
    public int? MaxAtsRunsPerDay { get; set; }

    [Column("max_ats_runs_per_week")]
    public int? MaxAtsRunsPerWeek { get; set; }

    [Column("max_ats_runs_per_month")]
    public int? MaxAtsRunsPerMonth { get; set; }

    [Column("max_job_matches_per_day")]
    public int? MaxJobMatchesPerDay { get; set; }

    [Column("max_job_matches_per_week")]
    public int? MaxJobMatchesPerWeek { get; set; }

    [Column("max_job_matches_per_month")]
    public int? MaxJobMatchesPerMonth { get; set; }

    [Column("max_job_applies_per_day")]
    public int? MaxJobAppliesPerDay { get; set; }

    [Column("max_job_applies_per_week")]
    public int? MaxJobAppliesPerWeek { get; set; }

    [Column("max_job_applies_per_month")]
    public int? MaxJobAppliesPerMonth { get; set; }

    [Column("created_on")]
    public DateTime CreatedOn { get; set; }

    [Column("updated_on")]
    public DateTime? UpdatedOn { get; set; }

}
