using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CareerPanda.DataAccess.Entities.Md;

[Table("subscription_features", Schema = "md")]
public partial class MdSubscriptionfeature
{
    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Column("subscription_id")]
    public int SubscriptionId { get; set; }

    [Column("feature_type_id")]
    public int FeatureTypeId { get; set; }

    [Column("limit_value")]
    public int? LimitValue { get; set; }

    [Column("created_on")]
    public DateTime CreatedOn { get; set; }

    [Column("updated_on")]
    public DateTime? UpdatedOn { get; set; }

}
