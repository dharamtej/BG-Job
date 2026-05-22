using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CareerPanda.DataAccess.Entities.Cp;

[Table("users", Schema = "cp")]
public partial class Cpuser
{
    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Column("clerk_user_id")]
    public string? ClerkUserId { get; set; }

    [Column("first_name")]
    public string? FirstName { get; set; }

    [Column("last_name")]
    public string? LastName { get; set; }

    [Column("preferred_name")]
    public string? PreferredName { get; set; }

    [Column("pronoun")]
    public string? Pronoun { get; set; }

    [Column("current_position")]
    public string? CurrentPosition { get; set; }

    [Column("current_company")]
    public string? CurrentCompany { get; set; }

    [Column("current_company_start_date")]
    public string? CurrentCompanyStartDate { get; set; }

    [Column("university")]
    public string? University { get; set; }

    [Column("industry_id")]
    public int? IndustryId { get; set; }

    [Column("location")]
    public string? Location { get; set; }

    [Column("currently_active_on")]
    public string? CurrentlyActiveOn { get; set; }

    [Column("website_url")]
    public string? WebsiteUrl { get; set; }

    [Column("website_label")]
    public string? WebsiteLabel { get; set; }

    [Column("about")]
    public string? About { get; set; }

    [Column("avatar")]
    public string? Avatar { get; set; }

    [Column("email")]
    public string Email { get; set; }

    [Column("contact_number")]
    public string? ContactNumber { get; set; }

    [Column("onboarding_completed")]
    public bool OnboardingCompleted { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    [Column("role_id")]
    public int? RoleId { get; set; }

    [Column("referral_code")]
    public string? ReferralCode { get; set; }

    [Column("default_resume_template_id")]
    public int? DefaultResumeTemplateId { get; set; }

    [Column("updated_on")]
    public DateTime? UpdatedOn { get; set; }

    [Column("commission_balance")]
    public decimal? CommissionBalance { get; set; }

    [Column("commission_percentage")]
    public decimal? CommissionPercentage { get; set; }

    [Column("referral_click_count")]
    public long? ReferralClickCount { get; set; }

    [Column("open_remote")]
    public bool? OpenRemote { get; set; }

    [Column("is_actively_looking")]
    public bool? IsActivelyLooking { get; set; }

    [Column("open_shifts")]
    public bool? OpenShifts { get; set; }

    [Column("linkedin_url")]
    public string? LinkedinUrl { get; set; }

    [Column("github_url")]
    public string? GithubUrl { get; set; }

    [Column("gender")]
    public string? Gender { get; set; }

    [Column("date_of_birth")]
    public DateOnly? DateOfBirth { get; set; }

    [Column("password")]
    public string? Password { get; set; }

    [Column("current_company_location")]
    public string? CurrentCompanyLocation { get; set; }

    [Column("city")]
    public string? City { get; set; }

    [Column("state")]
    public string? State { get; set; }

    [Column("country")]
    public string? Country { get; set; }

    [Column("open_to_overseas")]
    public bool? OpenToOverseas { get; set; }

    [Column("last_logged_in")]
    public DateTime? LastLoggedIn { get; set; }

    [Column("notice_period_days")]
    public string? NoticePeriodDays { get; set; }

    [Column("current_ctc")]
    public string? CurrentCtc { get; set; }

    [Column("is_consultant")]
    public bool? IsConsultant { get; set; }

    [Column("consultant_commission_percentage")]
    public decimal? ConsultantCommissionPercentage { get; set; }

    [Column("timezone")]
    public string? Timezone { get; set; }

    [Column("is_exploring_opportunities")]
    public bool? IsExploringOpportunities { get; set; }

    [Column("not_looking_rignt_now")]
    public bool? NotLookingRigntNow { get; set; }

    [Column("is_google")]
    public bool? IsGoogle { get; set; }

    [Column("is_linkedin")]
    public bool? IsLinkedin { get; set; }

    [Column("default_clt_id")]
    public long? DefaultCltId { get; set; }

    [Column("stripe_account_id")]
    public string? StripeAccountId { get; set; }

    [Column("stripe_account_status")]
    public string StripeAccountStatus { get; set; }

    [Column("is_agent")]
    public bool? IsAgent { get; set; }

    [Column("is_active")]
    public bool IsActive { get; set; }

}
