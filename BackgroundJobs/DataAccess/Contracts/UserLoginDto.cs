using System.ComponentModel.DataAnnotations.Schema;

namespace CareerPanda.DataAccess.Contracts;

/// <summary>API login/register contract (maps to cp.users via email).</summary>
public class UserLoginDto
{
    public string LoginId { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public string FirstName { get; set; } = string.Empty;

    public string LastName { get; set; } = string.Empty;

    [NotMapped]
    public string? IdToken { get; set; }

    [NotMapped]
    public string OrgCode { get; set; } = string.Empty;

    [NotMapped]
    public string CustCode { get; set; } = string.Empty;
}
