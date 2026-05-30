using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CareerPanda.DataAccess.Entities.Md;

[Table("job_role_aliases", Schema = "md")]
public class MdJobRoleAlias
{
    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Column("alias")]
    public string Alias { get; set; } = null!;

    [Column("job_role_id")]
    public int JobRoleId { get; set; }

    [Column("source")]
    public string? Source { get; set; }

    [Column("created_on")]
    public DateTime CreatedOn { get; set; }
}
