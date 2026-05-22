using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CareerPanda.DataAccess.Entities.Cp;

[Table("consultant_commission_history", Schema = "cp")]
public partial class CpConsultantCommissionHistory
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("consultant_id")]
    public long? ConsultantId { get; set; }

    [Column("prev_percentage")]
    public decimal PrevPercentage { get; set; }

    [Column("new_percentage")]
    public decimal NewPercentage { get; set; }

    [Column("created_on")]
    public DateTime CreatedOn { get; set; }

    [Column("updated_on")]
    public DateTime? UpdatedOn { get; set; }

    [Column("updated_by")]
    public long? UpdatedBy { get; set; }

    [Column("previous_commission_type")]
    public string? PreviousCommissionType { get; set; }

    [Column("new_commission_type")]
    public string? NewCommissionType { get; set; }

}
