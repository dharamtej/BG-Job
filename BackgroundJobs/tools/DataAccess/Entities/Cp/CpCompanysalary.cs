using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CareerPanda.DataAccess.Entities.Cp;

[Table("company_salaries", Schema = "cp")]
public partial class CpCompanysalary
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("user_id")]
    public long UserId { get; set; }

    [Column("company_id")]
    public long CompanyId { get; set; }

    [Column("job_title")]
    public string JobTitle { get; set; }

    [Column("department")]
    public string? Department { get; set; }

    [Column("office_location")]
    public string? OfficeLocation { get; set; }

    [Column("employment_type")]
    public string? EmploymentType { get; set; }

    [Column("is_current_employee")]
    public bool? IsCurrentEmployee { get; set; }

    [Column("fixed_amount")]
    public decimal? FixedAmount { get; set; }

    [Column("fixed_period")]
    public string? FixedPeriod { get; set; }

    [Column("fixed_currency")]
    public string? FixedCurrency { get; set; }

    [Column("variable_amount")]
    public decimal? VariableAmount { get; set; }

    [Column("variable_period")]
    public string? VariablePeriod { get; set; }

    [Column("variable_currency")]
    public string? VariableCurrency { get; set; }

    [Column("working_period_start")]
    public DateTime? WorkingPeriodStart { get; set; }

    [Column("working_period_end")]
    public DateTime? WorkingPeriodEnd { get; set; }

    [Column("is_anonymous")]
    public bool IsAnonymous { get; set; }

    [Column("total_annual_comp")]
    public decimal? TotalAnnualComp { get; set; }

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

    [Column("is_consent")]
    public bool? IsConsent { get; set; }

    [Column("gender")]
    public string? Gender { get; set; }

    [Column("additional_pay")]
    public string? AdditionalPay { get; set; }

    [Column("display_preference")]
    public int? DisplayPreference { get; set; }

    [Column("job_role_id")]
    public long? JobRoleId { get; set; }

    [Column("total_company_experience")]
    public decimal? TotalCompanyExperience { get; set; }

}
