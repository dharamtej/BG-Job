using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CareerPanda.DataAccess.Entities.Cp;

[Table("withdrawal_requests", Schema = "cp")]
public partial class CpWithdrawalrequest
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("requested_user_id")]
    public long? RequestedUserId { get; set; }

    [Column("withdrawal_type")]
    public int WithdrawalType { get; set; }

    [Column("status")]
    public int? Status { get; set; }

    [Column("reviewed_by")]
    public long? ReviewedBy { get; set; }

    [Column("current_amount")]
    public decimal CurrentAmount { get; set; }

    [Column("withdrawal_amount")]
    public decimal WithdrawalAmount { get; set; }

    [Column("currency")]
    public string? Currency { get; set; }

    [Column("timezone")]
    public string? Timezone { get; set; }

    [Column("created_on")]
    public DateTime CreatedOn { get; set; }

    [Column("updated_on")]
    public DateTime? UpdatedOn { get; set; }

    [Column("stripe_transfer_id")]
    public string? StripeTransferId { get; set; }

    [Column("failure_reason")]
    public string? FailureReason { get; set; }

}
