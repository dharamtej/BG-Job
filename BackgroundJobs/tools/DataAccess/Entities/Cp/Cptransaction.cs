using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CareerPanda.DataAccess.Entities.Cp;

[Table("transactions", Schema = "cp")]
public partial class Cptransaction
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("payment_id")]
    public string? PaymentId { get; set; }

    [Column("user_id")]
    public long? UserId { get; set; }

    [Column("transaction_type")]
    public int? TransactionType { get; set; }

    [Column("service_type")]
    public string? ServiceType { get; set; }

    [Column("payment_mode")]
    public string? PaymentMode { get; set; }

    [Column("payment_amount")]
    public decimal? PaymentAmount { get; set; }

    [Column("currency")]
    public string? Currency { get; set; }

    [Column("time_zone")]
    public string? TimeZone { get; set; }

    [Column("created_on")]
    public DateTime CreatedOn { get; set; }

    [Column("updated_on")]
    public DateTime? UpdatedOn { get; set; }

}
