using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CareerPanda.DataAccess.Entities.Md;

[Table("templates_types", Schema = "md")]
public partial class MdTemplatestypes
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("template_type_id")]
    public int? TemplateTypeId { get; set; }

    [Column("name")]
    public string? Name { get; set; }

    [Column("description")]
    public string? Description { get; set; }

    [Column("status")]
    public bool? Status { get; set; }

    [Column("created_on")]
    public DateTime CreatedOn { get; set; }

    [Column("updated_on")]
    public DateTime? UpdatedOn { get; set; }

}
