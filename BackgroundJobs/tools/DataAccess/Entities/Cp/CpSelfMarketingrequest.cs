using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CareerPanda.DataAccess.Entities.Cp;

[Table("self_marketing_requests", Schema = "cp")]
public partial class CpSelfMarketingrequest
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("user_id")]
    public long UserId { get; set; }

    [Column("resume_url")]
    public string? ResumeUrl { get; set; }

    [Column("cover_letter")]
    public string? CoverLetter { get; set; }

    [Column("linkedin")]
    public string? Linkedin { get; set; }

    [Column("github")]
    public string? Github { get; set; }

    [Column("transaction_id")]
    public long? TransactionId { get; set; }

    [Column("payment_amount")]
    public decimal? PaymentAmount { get; set; }

    [Column("payment_status")]
    public string? PaymentStatus { get; set; }

    [Column("stripe_payment_id")]
    public string? StripePaymentId { get; set; }

    [Column("invoice_url")]
    public string? InvoiceUrl { get; set; }

    [Column("invoice_no")]
    public string? InvoiceNo { get; set; }

    [Column("payment_method")]
    public string? PaymentMethod { get; set; }

    [Column("payment_date")]
    public DateTime? PaymentDate { get; set; }

    [Column("auto_renewal_opted")]
    public bool? AutoRenewalOpted { get; set; }

    [Column("assigned_agent")]
    public long? AssignedAgent { get; set; }

    [Column("assigned_by")]
    public long? AssignedBy { get; set; }

    [Column("login_username")]
    public string? LoginUsername { get; set; }

    [Column("login_password")]
    public string? LoginPassword { get; set; }

    [Column("subscription_start_date")]
    public DateTime? SubscriptionStartDate { get; set; }

    [Column("subscription_end_date")]
    public DateTime? SubscriptionEndDate { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

    [Column("plan")]
    public string? Plan { get; set; }

    [Column("request_status")]
    public string RequestStatus { get; set; }

    [Column("admin_notes")]
    public string? AdminNotes { get; set; }

}
