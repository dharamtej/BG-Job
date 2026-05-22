using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CareerPanda.DataAccess.Entities.Cp;

[Table("terms", Schema = "cp")]
public partial class Cpterm
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("taxonomy_id")]
    public long TaxonomyId { get; set; }

    [Column("slug")]
    public string Slug { get; set; }

    [Column("name")]
    public string Name { get; set; }

    [Column("created_on")]
    public DateTime CreatedOn { get; set; }

}
