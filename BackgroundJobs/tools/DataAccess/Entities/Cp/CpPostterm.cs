using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CareerPanda.DataAccess.Entities.Cp;

[Table("post_terms", Schema = "cp")]
public partial class CpPostterm
{
    [Key]
    [Column("post_id")]
    public long PostId { get; set; }

    [Key]
    [Column("term_id")]
    public long TermId { get; set; }

    [Column("created_on")]
    public DateTime CreatedOn { get; set; }

}
