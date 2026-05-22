using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CareerPanda.DataAccess.Entities.Md;

[Table("service_categories", Schema = "md")]
public partial class MdServicecategory
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("category")]
    public string? Category { get; set; }

    [Column("description")]
    public string? Description { get; set; }

    [Column("category_type")]
    public string? CategoryType { get; set; }

    [Column("icon")]
    public string? Icon { get; set; }

    [Column("image")]
    public string? Image { get; set; }

    [Column("created_on")]
    public DateTime CreatedOn { get; set; }

}
