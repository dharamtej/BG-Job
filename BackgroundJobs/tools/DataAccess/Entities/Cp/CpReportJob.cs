using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CareerPanda.DataAccess.Entities.Cp;

[Table("report_job", Schema = "cp")]
public partial class CpReportJob
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("user_id")]
    public long UserId { get; set; }

    [Column("job_id")]
    public long JobId { get; set; }

    [Column("report_reason")]
    public string? ReportReason { get; set; }

    [Column("reported_on")]
    public DateTime ReportedOn { get; set; }

    [Column("reviewed_on")]
    public DateTime? ReviewedOn { get; set; }

    [Column("reviewed_by")]
    public long? ReviewedBy { get; set; }

}
