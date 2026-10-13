using System.Security.Claims;
using Lingarr.Core.Configuration;

using Lingarr.Server.Models.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Lingarr.Server.Modules.Auth;
using Lingarr.Server.Modules.Settings;

namespace Lingarr.Server.Controllers;

[ApiController]
[Authorize("LingarrAccess")]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly IAuthService _authService;
    private readonly ISettingService _settingService;
    private readonly ILogger<AuthController> _logger;

    public AuthController(IAuthService authService, ISettingService settingService, ILogger<AuthController> logger)
    {
        _authService = authService;
        _settingService = settingService;
        _logger = logger;
    }

    /// <summary>
    /// Login with username and password
    /// </summary>
    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<ActionResult> Login([FromBody] LoginRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
        {
            return BadRequest(new { message = "Username and password are required" });
        }

        var user = await _authService.GetUserByUsername(request.Username);
        if (user == null || !_authService.VerifyPassword(request.Password, user.PasswordHash))
        {
            return Unauthorized(new { message = "Invalid username or password" });
        }

        await _authService.RecordLoginAsync(user);
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Username)
        };
        var claimsIdentity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var claimsPrincipal = new ClaimsPrincipal(claimsIdentity);

        await HttpContext.SignInAsync(
            "Cookies",
            claimsPrincipal,
            new AuthenticationProperties
            {
                IsPersistent = true,
                ExpiresUtc = DateTimeOffset.UtcNow.AddDays(7)
            });

        _logger.LogInformation("User {Username} logged in", user.Username);

        return Ok();
    }

    /// <summary>
    /// Verify if the request is authenticated via Cookie
    /// </summary>
    [HttpGet("authenticated")]
    [AllowAnonymous]
    public async Task<ActionResult> Authenticated()
    {
        var authEnabled = await _settingService.GetSetting(SettingKeys.Authentication.AuthEnabled);

        // If authentication is disabled, always return true
        if (authEnabled == "false")
        {
            return Ok(new { authenticated = true, authType = "disabled" });
        }

        // If authentication is enabled, check if user is authenticated via Cookie
        if (!User.Identity?.IsAuthenticated ?? true)
        {
            return Unauthorized(new { authenticated = false });
        }

        return Ok(new { authenticated = true, authType = "Cookie" });
    }

    /// <summary>
    /// Logout the current user
    /// </summary>
    [HttpPost("logout")]
    [Authorize("LingarrAccess")]
    public async Task<ActionResult> Logout()
    {
        await HttpContext.SignOutAsync("Cookies");
        return Ok(new { message = "Logged out successfully" });
    }

    /// <summary>
    /// Generate a new API key
    /// </summary>
    [HttpPost("apikey/generate")]
    [Authorize("LingarrAccess")]
    public async Task<ActionResult<ApiKeyResponse>> GenerateNewApiKey()
    {
        var apiKey = _authService.GenerateApiKey();
        await _settingService.SetEncryptedSetting(SettingKeys.Authentication.ApiKey, apiKey);
        return Ok(new ApiKeyResponse
        {
            ApiKey = apiKey
        });
    }
}
