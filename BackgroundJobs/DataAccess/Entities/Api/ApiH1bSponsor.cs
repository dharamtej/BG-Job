using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CareerPanda.DataAccess.Entities.Api;

[Table("h1b_sponsors", Schema = "api")]
public class ApiH1bSponsor
{
    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Column("employer_name")]
    public string EmployerName { get; set; } = null!;

    [Column("employer_name_key")]
    public string EmployerNameKey { get; set; } = null!;

    [Column("naics_code")]
    public string? NaicsCode { get; set; }

    [Column("industry")]
    public string? Industry { get; set; }

    [Column("city")]
    public string? City { get; set; }

    [Column("state")]
    public string? State { get; set; }

    [Column("zip_code")]
    public string? ZipCode { get; set; }

    [Column("total_approvals")]
    public int TotalApprovals { get; set; }

    [Column("total_denials")]
    public int TotalDenials { get; set; }

    [Column("total_petitions")]
    public int TotalPetitions { get; set; }

    [Column("fiscal_year")]
    public string? FiscalYear { get; set; }

    [Column("normalized_name")]
    public string? NormalizedName { get; set; }

    [Column("enriched_at")]
    public DateTime? EnrichedAt { get; set; }

    [Column("created_on")]
    public DateTime CreatedOn { get; set; }

    [Column("updated_on")]
    public DateTime UpdatedOn { get; set; }
}
