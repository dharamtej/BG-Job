using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CareerPanda.DataAccess.Entities.Cp;

[Table("resumes", Schema = "cp")]
public partial class Cpresume
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("about")]
    public string? About { get; set; }

    [Column("completeness_percent")]
    public int CompletenessPercent { get; set; }

    [Column("is_default")]
    public bool IsDefault { get; set; }

    [Column("version")]
    public int Version { get; set; }

    [Column("created_on")]
    public DateTime CreatedOn { get; set; }

    [Column("updated_on")]
    public DateTime UpdatedOn { get; set; }

    [Column("user_id")]
    public int UserId { get; set; }

    [Column("headline")]
    public string? Headline { get; set; }

    [Column("pdf_path")]
    public string? PdfPath { get; set; }

    [Column("is_manual")]
    public bool? IsManual { get; set; }

    [Column("parsed_text")]
    public string? ParsedText { get; set; }

    [Column("file_name")]
    public string? FileName { get; set; }

    [Column("parsing_failed")]
    public bool? ParsingFailed { get; set; }

    [Column("template_id")]
    public long? TemplateId { get; set; }

    [Column("ats_score")]
    public int? AtsScore { get; set; }

    [Column("ats_report")]
    public string? AtsReport { get; set; }

    [Column("ats_last_scanned_on")]
    public DateTime? AtsLastScannedOn { get; set; }

    [Column("is_version")]
    public bool IsVersion { get; set; }

    [Column("parent_resume_id")]
    public long? ParentResumeId { get; set; }

    [Column("version_name")]
    public string? VersionName { get; set; }

    [Column("version_description")]
    public string? VersionDescription { get; set; }

    [Column("status")]
    public string Status { get; set; }

    [Column("first_name")]
    public string? FirstName { get; set; }

    [Column("last_name")]
    public string? LastName { get; set; }

    [Column("country_code")]
    public string? CountryCode { get; set; }

    [Column("mobile_no")]
    public string? MobileNo { get; set; }

    [Column("email")]
    public string? Email { get; set; }

    [Column("linkedin_url")]
    public string? LinkedinUrl { get; set; }

    [Column("website_url")]
    public string? WebsiteUrl { get; set; }

    [Column("github_url")]
    public string? GithubUrl { get; set; }

    [Column("city")]
    public string? City { get; set; }

    [Column("state")]
    public string? State { get; set; }

    [Column("country")]
    public string? Country { get; set; }

    [Column("job_id")]
    public long? JobId { get; set; }

    [Column("description")]
    public string? Description { get; set; }

    [Column("resume_origin")]
    public string ResumeOrigin { get; set; }

    [Column("acted_by_agent_id")]
    public long? ActedByAgentId { get; set; }

    [Column("font_name")]
    public string? FontName { get; set; }

    [Column("section_order")]
    public string? SectionOrder { get; set; }

}
