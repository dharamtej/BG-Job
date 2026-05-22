using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CareerPanda.DataAccess.Entities.Cp;

[Table("consultant_applications", Schema = "cp")]
public partial class CpConsultantapplication
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("user_id")]
    public long UserId { get; set; }

    [Column("status")]
    public string Status { get; set; }

    [Column("experience")]
    public string? Experience { get; set; }

    [Column("credentials")]
    public string? Credentials { get; set; }

    [Column("linkedin_url")]
    public string? LinkedinUrl { get; set; }

    [Column("portfolio_url")]
    public string? PortfolioUrl { get; set; }

    [Column("admin_remarks")]
    public string? AdminRemarks { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; }

}
