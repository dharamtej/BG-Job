using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CareerPanda.DataAccess.Entities.Cp;

[Table("benefits_submissions", Schema = "cp")]
public partial class CpBenefitssubmission
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("user_id")]
    public long UserId { get; set; }

    [Column("company_id")]
    public long CompanyId { get; set; }

    [Column("job_role_id")]
    public int JobRoleId { get; set; }

    [Column("department")]
    public string? Department { get; set; }

    [Column("office_location")]
    public string? OfficeLocation { get; set; }

    [Column("total_experience_years")]
    public decimal? TotalExperienceYears { get; set; }

    [Column("total_company_experience")]
    public decimal? TotalCompanyExperience { get; set; }

    [Column("gender")]
    public string? Gender { get; set; }

    [Column("employment_type")]
    public string? EmploymentType { get; set; }

    [Column("is_current_employee")]
    public bool? IsCurrentEmployee { get; set; }

    [Column("overall_benefits_rating")]
    public decimal OverallBenefitsRating { get; set; }

    [Column("experience_text")]
    public string? ExperienceText { get; set; }

    [Column("is_anonymous")]
    public bool IsAnonymous { get; set; }

    [Column("ben_child_care_facility")]
    public int? BenChildCareFacility { get; set; }

    [Column("ben_office_cab_shuttle")]
    public int? BenOfficeCabShuttle { get; set; }

    [Column("ben_cafeteria")]
    public int? BenCafeteria { get; set; }

    [Column("ben_festival_gifts")]
    public int? BenFestivalGifts { get; set; }

    [Column("ben_office_gym")]
    public int? BenOfficeGym { get; set; }

    [Column("ben_performance_bonus")]
    public int? BenPerformanceBonus { get; set; }

    [Column("ben_mobile_bill_reimb")]
    public int? BenMobileBillReimb { get; set; }

    [Column("ben_annual_health_check")]
    public int? BenAnnualHealthCheck { get; set; }

    [Column("ben_life_insurance")]
    public int? BenLifeInsurance { get; set; }

    [Column("ben_health_insurance")]
    public int? BenHealthInsurance { get; set; }

    [Column("ben_gym_membership")]
    public int? BenGymMembership { get; set; }

    [Column("ben_international_onsite")]
    public int? BenInternationalOnsite { get; set; }

    [Column("ben_softskill_training")]
    public int? BenSoftskillTraining { get; set; }

    [Column("ben_rewards_recognition")]
    public int? BenRewardsRecognition { get; set; }

    [Column("ben_prof_degree_assist")]
    public int? BenProfDegreeAssist { get; set; }

    [Column("is_consent")]
    public bool? IsConsent { get; set; }

    [Column("status")]
    public string Status { get; set; }

    [Column("moderated_by")]
    public long? ModeratedBy { get; set; }

    [Column("moderated_on")]
    public DateTime? ModeratedOn { get; set; }

    [Column("moderation_notes")]
    public string? ModerationNotes { get; set; }

    [Column("created_on")]
    public DateTime CreatedOn { get; set; }

    [Column("updated_on")]
    public DateTime? UpdatedOn { get; set; }

    [Column("working_period_start")]
    public DateTime? WorkingPeriodStart { get; set; }

    [Column("working_period_end")]
    public DateTime? WorkingPeriodEnd { get; set; }

}
