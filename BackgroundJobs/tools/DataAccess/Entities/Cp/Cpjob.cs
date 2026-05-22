using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CareerPanda.DataAccess.Entities.Cp;

[Table("jobs", Schema = "cp")]
public partial class Cpjob
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("api_job_id")]
    public string? ApiJobId { get; set; }

    [Column("user_id")]
    public long? UserId { get; set; }

    [Column("job_link")]
    public string? JobLink { get; set; }

    [Column("company_name")]
    public string CompanyName { get; set; }

    [Column("company_id")]
    public long? CompanyId { get; set; }

    [Column("job_title")]
    public string JobTitle { get; set; }

    [Column("job_description")]
    public string? JobDescription { get; set; }

    [Column("easy_apply")]
    public bool? EasyApply { get; set; }

    [Column("job_apply_grad")]
    public string? JobApplyGrad { get; set; }

    [Column("job_benefits")]
    public string? JobBenefits { get; set; }

    [Column("our_company_id")]
    public long? OurCompanyId { get; set; }

    [Column("job_domain")]
    public string? JobDomain { get; set; }

    [Column("job_sub_domain")]
    public string? JobSubDomain { get; set; }

    [Column("education_qualification")]
    public string? EducationQualification { get; set; }

    [Column("experience_years")]
    public int? ExperienceYears { get; set; }

    [Column("industry")]
    public string? Industry { get; set; }

    [Column("post_date")]
    public DateTime? PostDate { get; set; }

    [Column("last_date")]
    public DateTime? LastDate { get; set; }

    [Column("role")]
    public string? Role { get; set; }

    [Column("job_level")]
    public string? JobLevel { get; set; }

    [Column("city")]
    public string? City { get; set; }

    [Column("state")]
    public string? State { get; set; }

    [Column("country")]
    public string? Country { get; set; }

    [Column("salary_max")]
    public decimal? SalaryMax { get; set; }

    [Column("salary_min")]
    public decimal? SalaryMin { get; set; }

    [Column("salary_type")]
    public string? SalaryType { get; set; }

    [Column("security_clearance")]
    public string? SecurityClearance { get; set; }

    [Column("shift_schedule")]
    public string? ShiftSchedule { get; set; }

    [Column("sponsorship")]
    public string? Sponsorship { get; set; }

    [Column("requirements")]
    public string? Requirements { get; set; }

    [Column("responsibilities")]
    public string? Responsibilities { get; set; }

    [Column("skills")]
    public string[]? Skills { get; set; }

    [Column("travel_requirement")]
    public string? TravelRequirement { get; set; }

    [Column("work_mode")]
    public string? WorkMode { get; set; }

    [Column("status")]
    public bool? Status { get; set; }

    [Column("updated_on")]
    public DateTime? UpdatedOn { get; set; }

    [Column("created_on")]
    public DateTime CreatedOn { get; set; }

    [Column("technical")]
    public bool? Technical { get; set; }

    [Column("viewed")]
    public bool? Viewed { get; set; }

    [Column("saved")]
    public bool? Saved { get; set; }

    [Column("applied")]
    public bool? Applied { get; set; }

    [Column("last_viewed_date")]
    public DateTime? LastViewedDate { get; set; }

    [Column("saved_date")]
    public DateTime? SavedDate { get; set; }

    [Column("applied_date")]
    public DateTime? AppliedDate { get; set; }

    [Column("job_category_id")]
    public int? JobCategoryId { get; set; }

    [Column("salary_currency")]
    public string? SalaryCurrency { get; set; }

    [Column("work_authorization")]
    public string? WorkAuthorization { get; set; }

    [Column("job_mode")]
    public string? JobMode { get; set; }

    [Column("interviewed")]
    public bool? Interviewed { get; set; }

    [Column("offered")]
    public bool? Offered { get; set; }

    [Column("rejected")]
    public bool? Rejected { get; set; }

    [Column("job_role_id")]
    public int? JobRoleId { get; set; }

    [Column("industry_id")]
    public int? IndustryId { get; set; }

    [Column("company_logo_url")]
    public string? CompanyLogoUrl { get; set; }

    [Column("raw_job_public_id")]
    public string? RawJobPublicId { get; set; }

    [Column("acted_by_agent_id")]
    public long? ActedByAgentId { get; set; }

}
