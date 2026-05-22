using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CareerPanda.DataAccess.Entities.Md;

[Table("subscriptions", Schema = "md")]
public partial class Mdsubscription
{
    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Column("price")]
    public decimal Price { get; set; }

    [Column("duration_days")]
    public int DurationDays { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    [Column("name")]
    public string Name { get; set; }

    [Column("is_trial")]
    public bool IsTrial { get; set; }

    [Column("trial_duration_days")]
    public int TrialDurationDays { get; set; }

    [Column("description")]
    public string? Description { get; set; }

    [Column("is_active")]
    public bool? IsActive { get; set; }

    [Column("country_id")]
    public int? CountryId { get; set; }

    [Column("max_resumes_download")]
    public int? MaxResumesDownload { get; set; }

    [Column("max_cover_letters_download")]
    public int? MaxCoverLettersDownload { get; set; }

    [Column("max_jobs_apply")]
    public int? MaxJobsApply { get; set; }

    [Column("max_jobs_tailor")]
    public int? MaxJobsTailor { get; set; }

    [Column("max_ats_checks")]
    public int? MaxAtsChecks { get; set; }

    [Column("max_job_match")]
    public int? MaxJobMatch { get; set; }

    [Column("max_resumes_build")]
    public int? MaxResumesBuild { get; set; }

    [Column("is_addon")]
    public bool? IsAddon { get; set; }

    [Column("max_free_consult")]
    public int? MaxFreeConsult { get; set; }

    [Column("max_discount_consult")]
    public int? MaxDiscountConsult { get; set; }

    [Column("consult_service_ids")]
    public string? ConsultServiceIds { get; set; }

    [Column("consult_category_ids")]
    public string[]? ConsultCategoryIds { get; set; }

    [Column("features")]
    public string[]? Features { get; set; }

}
