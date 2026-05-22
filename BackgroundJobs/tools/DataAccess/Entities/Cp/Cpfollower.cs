using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CareerPanda.DataAccess.Entities.Cp;

[Table("followers", Schema = "cp")]
public partial class Cpfollower
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("follower_id")]
    public long FollowerId { get; set; }

    [Column("following_id")]
    public long FollowingId { get; set; }

    [Column("created_on")]
    public DateTime CreatedOn { get; set; }

}
