using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CareerPanda.DataAccess.Entities.Cp;

[Table("posts", Schema = "cp")]
public partial class Cppost
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("author_id")]
    public long AuthorId { get; set; }

    [Column("slug")]
    public string? Slug { get; set; }

    [Column("title")]
    public string? Title { get; set; }

    [Column("excerpt")]
    public string? Excerpt { get; set; }

    [Column("hero_image_url")]
    public string? HeroImageUrl { get; set; }

    [Column("content_html")]
    public string? ContentHtml { get; set; }

    [Column("content_json")]
    public string? ContentJson { get; set; }

    [Column("post_type")]
    public string? PostType { get; set; }

    [Column("status")]
    public string Status { get; set; }

    [Column("published_at")]
    public DateTime? PublishedAt { get; set; }

    [Column("share_count")]
    public long? ShareCount { get; set; }

    [Column("allow_comments")]
    public bool? AllowComments { get; set; }

    [Column("is_featured")]
    public bool? IsFeatured { get; set; }

    [Column("pin_weight")]
    public int? PinWeight { get; set; }

    [Column("access_level_id")]
    public int? AccessLevelId { get; set; }

    [Column("access_role_id")]
    public int? AccessRoleId { get; set; }

    [Column("created_on")]
    public DateTime CreatedOn { get; set; }

    [Column("updated_on")]
    public DateTime? UpdatedOn { get; set; }

    [Column("subscription_plan")]
    public string SubscriptionPlan { get; set; }

    [Column("view_count")]
    public long ViewCount { get; set; }

    [Column("like_count")]
    public long LikeCount { get; set; }

    [Column("comment_count")]
    public long CommentCount { get; set; }

    [Column("read_time_minutes")]
    public int? ReadTimeMinutes { get; set; }

    [Column("pdf_url")]
    public string? PdfUrl { get; set; }

    [Column("pdf_label")]
    public string? PdfLabel { get; set; }

}
