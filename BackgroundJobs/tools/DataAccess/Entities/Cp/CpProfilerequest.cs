using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CareerPanda.DataAccess.Entities.Cp;

[Table("profile_requests", Schema = "cp")]
public partial class CpProfilerequest
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("user_id")]
    public long UserId { get; set; }

    [Column("status")]
    public string Status { get; set; }

    [Column("resume")]
    public string? Resume { get; set; }

    [Column("profile_type")]
    public int? ProfileType { get; set; }

    [Column("working_company")]
    public string? WorkingCompany { get; set; }

    [Column("current_role_name")]
    public string? CurrentRoleName { get; set; }

    [Column("previous_works")]
    public string[]? PreviousWorks { get; set; }

    [Column("experience")]
    public string? Experience { get; set; }

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
