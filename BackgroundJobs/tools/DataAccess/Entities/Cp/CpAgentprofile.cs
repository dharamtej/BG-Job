using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CareerPanda.DataAccess.Entities.Cp;

[Table("agent_profiles", Schema = "cp")]
public partial class CpAgentprofile
{
    [Key]
    [Column("user_id")]
    public long UserId { get; set; }

    [Column("specialty")]
    public string? Specialty { get; set; }

    [Column("max_capacity")]
    public int MaxCapacity { get; set; }

    [Column("is_available")]
    public bool IsAvailable { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; }

}
