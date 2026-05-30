using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CareerPanda.DataAccess.Entities.Md;

[Table("industry_aliases", Schema = "md")]
public class MdIndustryAlias
{
    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Column("alias")]
    public string Alias { get; set; } = null!;

    [Column("industry_id")]
    public int IndustryId { get; set; }

    [Column("source")]
    public string? Source { get; set; }

    [Column("created_on")]
    public DateTime CreatedOn { get; set; }
}
