using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CareerPanda.DataAccess.Entities.Cp;

[Table("comments", Schema = "cp")]
public partial class Cpcomment
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("post_id")]
    public long PostId { get; set; }

    [Column("user_id")]
    public long UserId { get; set; }

    [Column("content")]
    public string Content { get; set; }

    [Column("created_on")]
    public DateTime CreatedOn { get; set; }

    [Column("is_approved")]
    public bool IsApproved { get; set; }

    [Column("parent_id")]
    public long? ParentId { get; set; }

}
