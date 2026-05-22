using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CareerPanda.DataAccess.Entities.Md;

[Table("templates", Schema = "md")]
public partial class MdTemplate
{
    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Column("html_code")]
    public string HtmlCode { get; set; }

    [Column("template_name")]
    public string TemplateName { get; set; }

    [Column("description")]
    public string? Description { get; set; }

    [Column("template_type")]
    public string? TemplateType { get; set; }

    [Column("template_job_level")]
    public string? TemplateJobLevel { get; set; }

    [Column("custom_tag")]
    public string? CustomTag { get; set; }

    [Column("created_on")]
    public DateTime CreatedOn { get; set; }

    [Column("updated_on")]
    public DateTime? UpdatedOn { get; set; }

    [Column("template_type_id")]
    public int? TemplateTypeId { get; set; }

    [Column("template_role")]
    public string? TemplateRole { get; set; }

    [Column("email_subject")]
    public string? EmailSubject { get; set; }

    [Column("status")]
    public bool? Status { get; set; }

}
