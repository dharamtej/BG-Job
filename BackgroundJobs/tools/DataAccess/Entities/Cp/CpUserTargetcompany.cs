using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CareerPanda.DataAccess.Entities.Cp;

[Table("user_target_companies", Schema = "cp")]
public partial class CpUserTargetcompany
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("user_id")]
    public long UserId { get; set; }

    [Column("company_id")]
    public long? CompanyId { get; set; }

    [Column("company_name")]
    public string CompanyName { get; set; }

    [Column("position")]
    public int? Position { get; set; }

    [Column("created_on")]
    public DateTime CreatedOn { get; set; }

    [Column("updated_on")]
    public DateTime? UpdatedOn { get; set; }

}
