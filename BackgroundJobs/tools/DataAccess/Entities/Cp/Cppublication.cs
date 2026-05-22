using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CareerPanda.DataAccess.Entities.Cp;

[Table("publications", Schema = "cp")]
public partial class Cppublication
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("resume_id")]
    public long ResumeId { get; set; }

    [Column("type")]
    public string Type { get; set; }

    [Column("title")]
    public string Title { get; set; }

    [Column("authors")]
    public string? Authors { get; set; }

    [Column("journal")]
    public string? Journal { get; set; }

    [Column("volume")]
    public string? Volume { get; set; }

    [Column("issue")]
    public string? Issue { get; set; }

    [Column("issue_date")]
    public DateOnly? IssueDate { get; set; }

    [Column("url")]
    public string? Url { get; set; }

    [Column("abstract")]
    public string? Abstract { get; set; }

    [Column("created_on")]
    public DateTime CreatedOn { get; set; }

    [Column("updated_on")]
    public DateTime? UpdatedOn { get; set; }

}
