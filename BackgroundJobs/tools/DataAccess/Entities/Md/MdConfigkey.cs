using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CareerPanda.DataAccess.Entities.Md;

[Table("config_keys", Schema = "md")]
public partial class MdConfigkey
{
    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Column("key")]
    public string Key { get; set; }

    [Column("value")]
    public string Value { get; set; }

    [Column("description")]
    public string? Description { get; set; }

    [Column("created_on")]
    public DateTime CreatedOn { get; set; }

    [Column("updated_on")]
    public DateTime? UpdatedOn { get; set; }

}
