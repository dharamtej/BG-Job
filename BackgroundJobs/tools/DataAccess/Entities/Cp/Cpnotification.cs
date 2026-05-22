using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CareerPanda.DataAccess.Entities.Cp;

[Table("notifications", Schema = "cp")]
public partial class Cpnotification
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("template_id")]
    public long TemplateId { get; set; }

    [Column("notification_type")]
    public long NotificationType { get; set; }

    [Column("target_user_id")]
    public long TargetUserId { get; set; }

    [Column("target_email_id")]
    public string? TargetEmailId { get; set; }

    [Column("target_mobile_no")]
    public string? TargetMobileNo { get; set; }

    [Column("sender_id")]
    public long SenderId { get; set; }

    [Column("sender_email_id")]
    public string? SenderEmailId { get; set; }

    [Column("sender_mobile_no")]
    public string? SenderMobileNo { get; set; }

    [Column("email_subject")]
    public string? EmailSubject { get; set; }

    [Column("message_text")]
    public string? MessageText { get; set; }

    [Column("status")]
    public int? Status { get; set; }

    [Column("delivered_at")]
    public DateTime DeliveredAt { get; set; }

    [Column("redirect_url")]
    public string? RedirectUrl { get; set; }

}
