using System.ComponentModel.DataAnnotations;

namespace CareerPanda.Framework.Core;

[Serializable]
public abstract class EntityBase
{
    [Key]
    public string Id { get; set; } = string.Empty;

    public string CreatedById { get; set; } = string.Empty;

    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedDate { get; set; } = DateTime.UtcNow;

    public string? UpdatedById { get; set; } = string.Empty;

    public string CustomerId { get; set; } = string.Empty;
}
