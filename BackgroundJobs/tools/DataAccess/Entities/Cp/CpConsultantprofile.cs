using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CareerPanda.DataAccess.Entities.Cp;

[Table("consultant_profiles", Schema = "cp")]
public partial class CpConsultantprofile
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("consultant_id")]
    public long ConsultantId { get; set; }

    [Column("about_me")]
    public string? AboutMe { get; set; }

    [Column("carrer_journey")]
    public string? CarrerJourney { get; set; }

    [Column("rating")]
    public decimal? Rating { get; set; }

    [Column("created_on")]
    public DateTime CreatedOn { get; set; }

    [Column("updated_on")]
    public DateTime? UpdatedOn { get; set; }

    [Column("is_available")]
    public bool? IsAvailable { get; set; }

    [Column("journey_through_resume")]
    public bool? JourneyThroughResume { get; set; }

    [Column("google_refresh_token")]
    public string? GoogleRefreshToken { get; set; }

    [Column("commission_type")]
    public string CommissionType { get; set; }

    [Column("consultant_profile_commission_percentage")]
    public decimal? ConsultantProfileCommissionPercentage { get; set; }

}
