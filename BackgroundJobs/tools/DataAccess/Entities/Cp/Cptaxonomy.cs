using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CareerPanda.DataAccess.Entities.Cp;

[Table("taxonomies", Schema = "cp")]
public partial class Cptaxonomy
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("code")]
    public string Code { get; set; }

    [Column("created_on")]
    public DateTime CreatedOn { get; set; }

}
