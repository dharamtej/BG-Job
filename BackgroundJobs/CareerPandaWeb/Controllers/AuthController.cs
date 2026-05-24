using CareerPanda.BL.Logic;
using CareerPanda.BL.Models;
using CareerPanda.DataAccess.Entities.Cp;
using CareerPanda.Framework;
using CareerPanda.Framework.Configuration;
using CareerPanda.Framework.Util;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CareerPanda.Web.Controllers;

/// <summary>Issues JWT access tokens for API clients and Swagger.</summary>
[ApiController]
[Route("api/auth")]
[AllowAnonymous]
public class AuthController : ControllerBase
{
    private readonly LoginBL _loginBl;
    private readonly Config _config;
    private readonly ILogger<AuthController> _logger;

    public AuthController(LoginBL loginBl, Config config, ILogger<AuthController> logger)
    {
        _loginBl = loginBl;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Authenticate with email and password; returns a JWT to use in the Authorization header.
    /// </summary>
    [HttpPost("token")]
    [ProducesResponseType(typeof(TokenResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<TokenResponse>> GetToken([FromBody] TokenRequest request)
    {
        if (string.IsNullOrWhiteSpace(request?.Email) || string.IsNullOrWhiteSpace(request.Password))
            return BadRequest(new { message = "Email and Password are required." });

        try
        {
            var loginResult = await _loginBl.CheckLoginAsync(new CpUser
            {
                Email = request.Email.Trim(),
                Password = request.Password
            });

            if (loginResult.Status != Status.Success || string.IsNullOrWhiteSpace(loginResult.Message))
            {
                return Unauthorized(new { message = loginResult.Message ?? "Invalid email or password." });
            }

            var user = loginResult.Entity as CpUser;
            var expiresIn = int.TryParse(_config.SessionConfig.Timeout, out var minutes) ? minutes : 60;

            return Ok(new TokenResponse
            {
                AccessToken = loginResult.Message,
                TokenType = "Bearer",
                ExpiresInMinutes = expiresIn,
                RefreshToken = loginResult.Response,
                Email = user?.Email ?? request.Email.Trim(),
                UserId = user?.Id > 0 ? user.Id.ToString() : string.Empty
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Token issuance failed for {Email}", request.Email);
            return StatusCode(StatusCodes.Status500InternalServerError, new { message = "Unable to issue token.", detail = ex.Message });
        }
    }
}
