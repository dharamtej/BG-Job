using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CareerPanda.DataAccess.Entities.Api;

[Table("companies", Schema = "api")]
public partial class Apicompany
{
    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Column("api_company_id")]
    public string? ApiCompanyId { get; set; }

    [Column("company_size")]
    public int? CompanySize { get; set; }

    [Column("company_name")]
    public string CompanyName { get; set; }

    [Column("featured")]
    public bool? Featured { get; set; }

    [Column("about_company")]
    public string? AboutCompany { get; set; }

    [Column("website")]
    public string? Website { get; set; }

    [Column("career_page")]
    public string? CareerPage { get; set; }

    [Column("logo_url")]
    public string? LogoUrl { get; set; }

    [Column("company_type")]
    public string? CompanyType { get; set; }

    [Column("created_on")]
    public DateTime CreatedOn { get; set; }

    [Column("updated_on")]
    public DateTime? UpdatedOn { get; set; }

    [Column("industry_id")]
    public long? IndustryId { get; set; }

    [Column("image_urls")]
    public string[]? ImageUrls { get; set; }

    [Column("public_id")]
    public string PublicId { get; set; }

}
