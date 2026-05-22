using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CareerPanda.DataAccess.Entities.Cp;

[Table("referrals", Schema = "cp")]
public partial class Cpreferral
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("referrer_id")]
    public long ReferrerId { get; set; }

    [Column("referred_user_id")]
    public long? ReferredUserId { get; set; }

    [Column("status")]
    public int? Status { get; set; }

    [Column("created_on")]
    public DateTime CreatedOn { get; set; }

    [Column("updated_on")]
    public DateTime UpdatedOn { get; set; }

    [Column("subscription_id")]
    public long? SubscriptionId { get; set; }

    [Column("commission_amount")]
    public decimal? CommissionAmount { get; set; }

}
