using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CareerPanda.DataAccess.Entities.Cp;

[Table("certifications", Schema = "cp")]
public partial class Cpcertification
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("resume_id")]
    public long ResumeId { get; set; }

    [Column("name")]
    public string Name { get; set; }

    [Column("issuer")]
    public string Issuer { get; set; }

    [Column("issue_date")]
    public DateOnly? IssueDate { get; set; }

    [Column("expiry_date")]
    public DateOnly? ExpiryDate { get; set; }

    [Column("credential_id")]
    public string? CredentialId { get; set; }

    [Column("credential_url")]
    public string? CredentialUrl { get; set; }

    [Column("created_on")]
    public DateTime CreatedOn { get; set; }

    [Column("updated_on")]
    public DateTime? UpdatedOn { get; set; }

    [Column("pdf_url")]
    public string? PdfUrl { get; set; }

}
