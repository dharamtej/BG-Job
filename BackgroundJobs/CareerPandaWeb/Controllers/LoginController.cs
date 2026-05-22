using CareerPanda.BL.Logic;
using CareerPanda.BL.Models;
using CareerPanda.DataAccess.Entities.Cp;
using CareerPanda.Framework;
using CareerPanda.Framework.MVC;
using CareerPanda.Framework.Util;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CareerPanda.Web.Controllers;

public class LoginController : CoreController
{
    private readonly ILogger<LoginController> _logger;
    private readonly LoginBL _loginBl;

    public LoginController(ILogger<LoginController> logger, LoginBL loginBl)
    {
        _logger = logger;
        _loginBl = loginBl;
    }

    [HttpPost]
    [Route("api/login/register")]
    [AllowAnonymous]
    public async Task<FrameworkResponse> Register([FromBody] CpUser user) =>
        await ExecuteAsync(() => _loginBl.RegisterAsync(user));

    [HttpPost]
    [Route("api/login/login")]
    [AllowAnonymous]
    public async Task<FrameworkResponse> CheckLogin([FromBody] CpUser user) =>
        await ExecuteAsync(() => _loginBl.CheckLoginAsync(user));

    [HttpPost]
    [Route("api/login/google")]
    [AllowAnonymous]
    public async Task<FrameworkResponse> GoogleLogin([FromBody] CpUser user) =>
        await ExecuteAsync(() => _loginBl.GoogleLoginAsync(user));

    [HttpPost]
    [Route("api/login/refresh")]
    [AllowAnonymous]
    public async Task<FrameworkResponse> Refresh([FromBody] RefreshTokenRequest request) =>
        await ExecuteAsync(() => _loginBl.RefreshTokenAsync(request));

    [HttpPost]
    [Route("api/login/forgotpassword")]
    [AllowAnonymous]
    public async Task<FrameworkResponse> ForgotPassword([FromBody] ForgotPasswordRequest request) =>
        await ExecuteAsync(() => _loginBl.ForgotPasswordAsync(request));

    [HttpPost]
    [Route("api/login/resetpassword")]
    [AllowAnonymous]
    public async Task<FrameworkResponse> ResetPassword([FromBody] ResetPasswordRequest request) =>
        await ExecuteAsync(() => _loginBl.ResetPasswordAsync(request));

    [HttpPost]
    [Route("api/login/logout")]
    public async Task<FrameworkResponse> Logout([FromBody] LogoutRequest? request)
    {
        ApplicationContext.UserId = UserId;
        ApplicationContext.CorrelationId = Guid.NewGuid().ToString();
        return await _loginBl.LogoutAsync(SessionId, request?.RefreshToken);
    }

    [HttpGet]
    [Route("api/login/ping")]
    [AllowAnonymous]
    public IActionResult Ping() =>
        Ok($"CareerPanda API {DateTime.UtcNow:O}");

    private async Task<FrameworkResponse> ExecuteAsync(Func<Task<FrameworkResponse>> action)
    {
        ApplicationContext.CorrelationId = Guid.NewGuid().ToString();
        var response = new FrameworkResponse { Status = Status.Failed };
        try
        {
            response = await action();
        }
        catch (Exception ex)
        {
            response.Message = ex.Message;
            _logger.LogError(ex, "Login operation failed");
        }
        return response;
    }
}
