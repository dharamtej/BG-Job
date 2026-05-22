using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CareerPanda.DataAccess.Entities.Md;

[Table("work_authorization_types", Schema = "md")]
public partial class MdWorkAuthorizationtypes
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("code")]
    public string Code { get; set; }

    [Column("name")]
    public string Name { get; set; }

    [Column("country")]
    public string? Country { get; set; }

    [Column("created_on")]
    public DateTime CreatedOn { get; set; }

    [Column("updated_on")]
    public DateTime? UpdatedOn { get; set; }

}
