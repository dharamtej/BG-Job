using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CareerPanda.DataAccess.Entities.Api;

[Table("raw_jobs", Schema = "api")]
public partial class ApiRawjob
{
    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Column("api_job_id")]
    public string? ApiJobId { get; set; }

    [Column("job_link")]
    public string? JobLink { get; set; }

    [Column("api_company_id")]
    public string? ApiCompanyId { get; set; }

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
    public int OurCompanyId { get; set; }

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

    [Column("job_work_mode")]
    public string? JobWorkMode { get; set; }

    [Column("status")]
    public bool? Status { get; set; }

    [Column("updated_on")]
    public DateTime? UpdatedOn { get; set; }

    [Column("created_on")]
    public DateTime CreatedOn { get; set; }

    [Column("technical")]
    public bool? Technical { get; set; }

    [Column("job_category_id")]
    public int? JobCategoryId { get; set; }

    [Column("salary_currency")]
    public string? SalaryCurrency { get; set; }

    [Column("work_authorization")]
    public string? WorkAuthorization { get; set; }

    [Column("job_role_id")]
    public int? JobRoleId { get; set; }

    [Column("industry_id")]
    public int? IndustryId { get; set; }

    [Column("public_id")]
    public string PublicId { get; set; }

}
