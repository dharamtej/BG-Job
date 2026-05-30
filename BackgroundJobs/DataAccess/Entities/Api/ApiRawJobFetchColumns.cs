// DataAccess/Entities/Api/ApiRawJobFetchColumns.cs
// Partial extension of the auto-generated ApiRawJob entity.
// Adds all columns introduced by migration 001_add_raw_jobs_fetch_columns.sql
#nullable disable
using System.ComponentModel.DataAnnotations.Schema;

namespace CareerPanda.DataAccess.Entities.Api;

public partial class ApiRawJob
{
    // ── Source tracking ─────────────────────────────────────────────────────
    /// <summary>Which API sourced this job: JSearch | USAJobs | TheMuse</summary>
    [Column("source")]
    public string Source { get; set; }

    /// <summary>Original job ID from the external API (for deduplication).</summary>
    [Column("source_id")]
    public string SourceId { get; set; }

    /// <summary>FK → api.job_fetch_runs.id</summary>
    [Column("fetch_run_id")]
    public string FetchRunId { get; set; }

    // ── Job metadata ─────────────────────────────────────────────────────────
    /// <summary>Approximate hours since the job was posted.</summary>
    [Column("hours_back_posted")]
    public int? HoursBackPosted { get; set; }

    /// <summary>EasyApply | ExternalApply | DirectApply</summary>
    [Column("apply_type")]
    public string ApplyType { get; set; }

    /// <summary>FullTime | PartTime | Contract | Internship | Temporary</summary>
    [Column("contract_type")]
    public string ContractType { get; set; }

    /// <summary>Remote | Hybrid | OnSite</summary>
    [Column("work_type")]
    public string WorkType { get; set; }

    /// <summary>Raw salary string from the API, e.g. "$80k–$120k/yr".</summary>
    [Column("salary_range_text")]
    public string SalaryRangeText { get; set; }

    /// <summary>Minimum years of experience required.</summary>
    [Column("experience_min")]
    public int? ExperienceMin { get; set; }

    /// <summary>Maximum years of experience required.</summary>
    [Column("experience_max")]
    public int? ExperienceMax { get; set; }

    // ── Company enrichment ───────────────────────────────────────────────────
    /// <summary>Company name as returned by the external API.</summary>
    [Column("company_name")]
    public string? CompanyName { get; set; }

    [Column("company_logo_url")]
    public string CompanyLogoUrl { get; set; }

    /// <summary>Company website or LinkedIn profile URL.</summary>
    [Column("company_url")]
    public string CompanyUrl { get; set; }

    /// <summary>Startup | NonProfit | University | Government | Enterprise | Staffing</summary>
    [Column("company_type")]
    public string CompanyType { get; set; }

    /// <summary>Short company description from the source API (not stored in raw_jobs — passed to api.companies).</summary>
    [NotMapped]
    public string? CompanyAbout { get; set; }

    /// <summary>Employee count or size category from source API (not stored in raw_jobs — passed to api.companies).</summary>
    [NotMapped]
    public int? CompanySize { get; set; }

    // ── Poster / recruiter info ──────────────────────────────────────────────
    /// <summary>Full name of the recruiter / hiring manager who posted the job.</summary>
    [Column("posted_by_name")]
    public string PostedByName { get; set; }

    /// <summary>LinkedIn or profile URL of the person who posted the job.</summary>
    [Column("posted_by_profile_url")]
    public string PostedByProfileUrl { get; set; }

    // ── Classification flags ─────────────────────────────────────────────────
    [Column("is_c2c")]
    public bool? IsC2C { get; set; }

    [Column("is_w2")]
    public bool? IsW2 { get; set; }

    [Column("is_staffing")]
    public bool? IsStaffing { get; set; }

    [Column("is_sponsored")]
    public bool? IsSponsored { get; set; }

    [Column("is_h1b_sponsored")]
    public bool? IsH1BSponsored { get; set; }

    [Column("is_university_job")]
    public bool? IsUniversityJob { get; set; }

    [Column("is_startup_job")]
    public bool? IsStartupJob { get; set; }

    [Column("is_non_profit_job")]
    public bool? IsNonProfitJob { get; set; }

    [Column("is_contract_job")]
    public bool? IsContractJob { get; set; }

    [Column("is_freelance_job")]
    public bool? IsFreelanceJob { get; set; }

    [Column("is_prime_vendor")]
    public bool? IsPrimeVendor { get; set; }

    /// <summary>Contract-to-Hire — converts to FTE after a contract period.</summary>
    [Column("is_contract_to_hire")]
    public bool? IsContractToHire { get; set; }

    // ── Visa classification flags ────────────────────────────────────────────
    /// <summary>OPT / CPT — F-1 student work authorization.</summary>
    [Column("is_opt_cpt")]
    public bool? IsOptCpt { get; set; }

    /// <summary>TN Visa — Canadian / Mexican professionals under USMCA.</summary>
    [Column("is_tn_visa")]
    public bool? IsTnVisa { get; set; }

    /// <summary>E-3 Visa — Australian specialty occupation workers.</summary>
    [Column("is_e3_visa")]
    public bool? IsE3Visa { get; set; }

    /// <summary>J-1 Visa — Exchange visitors / cultural exchange programs.</summary>
    [Column("is_j1_visa")]
    public bool? IsJ1Visa { get; set; }

    /// <summary>Green Card / Permanent Residency sponsorship (PERM / EB-2 / EB-3).</summary>
    [Column("is_green_card")]
    public bool? IsGreenCard { get; set; }

    /// <summary>Job requires a US government security clearance (Secret, TS/SCI, Public Trust, etc.).</summary>
    [Column("is_security_clearance_required")]
    public bool? IsSecurityClearanceRequired { get; set; }

    /// <summary>Job is open to veterans / has veteran hiring preference (USAJobs HiringPath "veterans").</summary>
    [Column("is_veterans_eligible")]
    public bool? IsVeteransEligible { get; set; }
}
