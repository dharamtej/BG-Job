using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CareerPanda.DataAccess.Entities.Cp;

[Table("company_followers", Schema = "cp")]
public partial class CpCompanyfollower
{
    [Key]
    [Column("company_id")]
    public long CompanyId { get; set; }

    [Key]
    [Column("user_id")]
    public long UserId { get; set; }

    [Column("followed_at")]
    public DateTime FollowedAt { get; set; }

}
