using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using CareerPanda.Framework.Configuration;
using CareerPanda.Framework.Core;
using Microsoft.IdentityModel.Tokens;

namespace CareerPanda.Framework.Util;

public static class UtilityManager
{
    public const string JwtIssuer = "CareerPanda-Platform";
    public const string JwtAudience = "CareerPanda-API";

    public static string GetErrorMessage(Exception ex, StringBuilder sb)
    {
        sb.AppendLine(ex.StackTrace);
        sb.AppendLine(ex.Message);

        if (ex.InnerException != null)
            GetErrorMessage(ex.InnerException, sb);

        return sb.ToString();
    }

    public static string GenerateToken(
        string key,
        string username,
        string userid,
        string roleid,
        string rolecode,
        double expiryinmin,
        string sessionid,
        string? loginProvider = null)
    {
        var signingCredentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
            SecurityAlgorithms.HmacSha256);

        var jwtDate = DateTime.UtcNow;
        var claims = new List<Claim>
        {
            new(ClaimTypes.Email, username),
            new(ClaimTypes.Sid, userid),
            new(ClaimTypes.Role, roleid),
            new("SessionId", sessionid),
            new("RoleCode", rolecode)
        };

        if (!string.IsNullOrWhiteSpace(loginProvider))
            claims.Add(new Claim("LoginProvider", loginProvider));

        var jwt = new JwtSecurityToken(
            audience: JwtAudience,
            issuer: JwtIssuer,
            claims: claims,
            notBefore: jwtDate,
            expires: jwtDate.AddMinutes(expiryinmin),
            signingCredentials: signingCredentials);

        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }

    public static string GetMimeType(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".pdf" => "application/pdf",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".doc" => "application/msword",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".txt" => "text/plain",
            ".json" => "application/json",
            _ => "application/octet-stream"
        };
    }

    public static void AssignDefaultValues(EntityBase oldentity, EntityBase newentity, CrudOperation operation)
    {
        if (operation == CrudOperation.Create)
        {
            newentity.Id = Guid.NewGuid().ToString();
            newentity.CreatedDate = DateTime.UtcNow;
            newentity.UpdatedDate = DateTime.UtcNow;
            newentity.CreatedById = oldentity.CreatedById;
            newentity.UpdatedById = oldentity.UpdatedById;
        }
        else if (operation == CrudOperation.Update)
        {
            newentity.UpdatedDate = DateTime.UtcNow;
            newentity.UpdatedById = oldentity.UpdatedById;
        }
    }
}
