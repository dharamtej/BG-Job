using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CareerPanda.DataAccess.Entities.Cp;

[Table("user_target_roles", Schema = "cp")]
public partial class CpUserTargetrole
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("user_id")]
    public long UserId { get; set; }

    [Column("job_role_id")]
    public long? JobRoleId { get; set; }

    [Column("role_text")]
    public string RoleText { get; set; }

    [Column("position")]
    public int? Position { get; set; }

    [Column("created_on")]
    public DateTime CreatedOn { get; set; }

    [Column("updated_on")]
    public DateTime? UpdatedOn { get; set; }

}
