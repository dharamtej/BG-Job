using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CareerPanda.DataAccess.Entities.Cp;

[Table("event_registrations", Schema = "cp")]
public partial class CpEventregistration
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("event_id")]
    public long EventId { get; set; }

    [Column("user_id")]
    public long UserId { get; set; }

    [Column("payment_amount")]
    public decimal? PaymentAmount { get; set; }

    [Column("transaction_id")]
    public long? TransactionId { get; set; }

    [Column("stripe_payment_id")]
    public string? StripePaymentId { get; set; }

    [Column("payment_date")]
    public DateTime? PaymentDate { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("locked_price")]
    public decimal? LockedPrice { get; set; }

    [Column("payment_status")]
    public string? PaymentStatus { get; set; }

    [Column("registration_status")]
    public string? RegistrationStatus { get; set; }

    [Column("currency")]
    public string? Currency { get; set; }

    [Column("time_zone")]
    public string? TimeZone { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

}
