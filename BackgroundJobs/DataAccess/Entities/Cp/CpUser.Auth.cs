using System.ComponentModel.DataAnnotations.Schema;

namespace CareerPanda.DataAccess.Entities.Cp;

/// <summary>API-only fields for login (not stored in cp.users).</summary>
public partial class CpUser
{
    [NotMapped]
    public string LoginId
    {
        get => Email;
        set => Email = value;
    }

    [NotMapped]
    public string? IdToken { get; set; }

    [NotMapped]
    public string? GoogleSubjectId { get; set; }

    [NotMapped]
    public string LoginProvider { get; set; } = "Internal";

    [NotMapped]
    public string RoleCode { get; set; } = string.Empty;

    [NotMapped]
    public string OrgCode { get; set; } = string.Empty;

    [NotMapped]
    public string CustCode { get; set; } = string.Empty;
}
