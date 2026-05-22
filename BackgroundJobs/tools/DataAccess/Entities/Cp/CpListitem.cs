using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CareerPanda.DataAccess.Entities.Cp;

[Table("list_items", Schema = "cp")]
public partial class CpListitem
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("post_id")]
    public long PostId { get; set; }

    [Column("position")]
    public int Position { get; set; }

    [Column("company_id")]
    public long? CompanyId { get; set; }

    [Column("label")]
    public string? Label { get; set; }

    [Column("subtitle")]
    public string? Subtitle { get; set; }

    [Column("url")]
    public string? Url { get; set; }

    [Column("snapshot")]
    public string? Snapshot { get; set; }

    [Column("metrics")]
    public string? Metrics { get; set; }

    [Column("created_on")]
    public DateTime CreatedOn { get; set; }

    [Column("job_id")]
    public int? JobId { get; set; }

}
