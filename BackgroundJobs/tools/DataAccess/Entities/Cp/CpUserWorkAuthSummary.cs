using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CareerPanda.DataAccess.Entities.Cp;

[Table("user_work_auth_summary", Schema = "cp")]
public partial class CpUserWorkAuthSummary
{
    [Key]
    [Column("user_id")]
    public long UserId { get; set; }

    [Column("location_text")]
    public string? LocationText { get; set; }

    [Column("needs_sponsorship")]
    public bool? NeedsSponsorship { get; set; }

    [Column("sponsorship_timeline")]
    public int? SponsorshipTimeline { get; set; }

    [Column("has_valid_work_auth")]
    public bool? HasValidWorkAuth { get; set; }

    [Column("earliest_expiry_date")]
    public DateOnly? EarliestExpiryDate { get; set; }

    [Column("created_on")]
    public DateTime CreatedOn { get; set; }

    [Column("updated_on")]
    public DateTime? UpdatedOn { get; set; }

}
