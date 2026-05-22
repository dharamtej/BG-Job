using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CareerPanda.DataAccess.Entities.Cp;

[Table("consultant_service", Schema = "cp")]
public partial class CpConsultantService
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("consultant_id")]
    public long ConsultantId { get; set; }

    [Column("category_id")]
    public long CategoryId { get; set; }

    [Column("service_id")]
    public long ServiceId { get; set; }

    [Column("title")]
    public string? Title { get; set; }

    [Column("description")]
    public string Description { get; set; }

    [Column("pricing")]
    public decimal Pricing { get; set; }

    [Column("currency")]
    public string Currency { get; set; }

    [Column("duration_min")]
    public int DurationMin { get; set; }

    [Column("buffer_min")]
    public int? BufferMin { get; set; }

    [Column("commision_percentage")]
    public decimal CommisionPercentage { get; set; }

    [Column("is_active")]
    public bool? IsActive { get; set; }

    [Column("created_on")]
    public DateTime CreatedOn { get; set; }

    [Column("updated_on")]
    public DateTime? UpdatedOn { get; set; }

    [Column("is_popular")]
    public bool IsPopular { get; set; }

    [Column("status")]
    public string Status { get; set; }

}
