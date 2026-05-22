using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CareerPanda.DataAccess.Entities.Cp;

[Table("refund_requests", Schema = "cp")]
public partial class CpRefundrequest
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("consultation_booking_id")]
    public long? ConsultationBookingId { get; set; }

    [Column("user_subscription_id")]
    public long? UserSubscriptionId { get; set; }

    [Column("transaction_id")]
    public long? TransactionId { get; set; }

    [Column("requested_by")]
    public long? RequestedBy { get; set; }

    [Column("refund_reason")]
    public string? RefundReason { get; set; }

    [Column("status")]
    public long? Status { get; set; }

    [Column("requested_on")]
    public DateTime? RequestedOn { get; set; }

    [Column("refunded_by")]
    public long? RefundedBy { get; set; }

    [Column("refund_type")]
    public string? RefundType { get; set; }

    [Column("created_on")]
    public DateTime CreatedOn { get; set; }

    [Column("updated_on")]
    public DateTime? UpdatedOn { get; set; }

}
