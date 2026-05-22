using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CareerPanda.DataAccess.Entities.Cp;

[Table("cover_letters", Schema = "cp")]
public partial class CpCoverLetter
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("user_id")]
    public long UserId { get; set; }

    [Column("template_id")]
    public int? TemplateId { get; set; }

    [Column("title")]
    public string? Title { get; set; }

    [Column("job_id")]
    public long? JobId { get; set; }

    [Column("body_json")]
    public string? BodyJson { get; set; }

    [Column("is_default")]
    public bool? IsDefault { get; set; }

    [Column("status")]
    public string? Status { get; set; }

    [Column("created_on")]
    public DateTime CreatedOn { get; set; }

    [Column("updated_on")]
    public DateTime? UpdatedOn { get; set; }

    [Column("resume_id")]
    public long? ResumeId { get; set; }

    [Column("company_name")]
    public string? CompanyName { get; set; }

    [Column("job_description")]
    public string? JobDescription { get; set; }

    [Column("acted_by_agent_id")]
    public long? ActedByAgentId { get; set; }

}
