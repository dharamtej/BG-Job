using CareerPanda.Framework.Core;
using CareerPanda.Framework.Util;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace CareerPanda.Framework.MVC;

[Authorize]
[ApiController]
[EnableCors("AllowAll")]
public class CoreController : ControllerBase
{
    public string UserId =>
        HttpContext?.User?.Claims?
            .FirstOrDefault(c => c.Type == ClaimTypes.Sid)?.Value ?? string.Empty;

    public string UserName =>
        HttpContext?.User?.Claims?
            .FirstOrDefault(c => c.Type == ClaimTypes.Email)?.Value ?? string.Empty;

    public string SessionId =>
        HttpContext?.User?.Claims?
            .FirstOrDefault(c => c.Type == "SessionId")?.Value ?? string.Empty;

    protected void AssignDefaultValues(EntityBase entity, CrudOperation operation)
    {
        if (operation == CrudOperation.Create)
        {
            entity.Id = Guid.NewGuid().ToString();
            entity.CreatedDate = DateTime.UtcNow;
            entity.UpdatedDate = DateTime.UtcNow;
            entity.CreatedById = UserId;
            entity.UpdatedById = UserId;
        }
        else if (operation == CrudOperation.Update)
        {
            entity.UpdatedDate = DateTime.UtcNow;
            entity.UpdatedById = UserId;
        }
    }
}
