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
    private readonly ILoginRateLimiter _loginRateLimiter;
    private readonly ILogger<AuthController> _logger;

    public AuthController(
        IAuthService authService,
        ISettingService settingService,
        ILoginRateLimiter loginRateLimiter,
        ILogger<AuthController> logger)
    {
        _authService = authService;
        _settingService = settingService;
        _loginRateLimiter = loginRateLimiter;
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

        var remoteIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        if (_loginRateLimiter.IsBlocked(request.Username, remoteIp))
        {
            _logger.LogWarning(
                "Login rate limit exceeded for user {Username} from {RemoteIp}.",
                request.Username,
                remoteIp);
            return StatusCode(StatusCodes.Status429TooManyRequests, new
            {
                message = "Too many failed login attempts. Please try again in a minute."
            });
        }

        var user = await _authService.GetUserByUsername(request.Username);
        if (user == null || !_authService.VerifyPassword(request.Password, user.PasswordHash))
        {
            _loginRateLimiter.RecordFailure(request.Username, remoteIp);
            _logger.LogWarning(
                "Failed login attempt for user {Username} from {RemoteIp}.",
                request.Username,
                remoteIp);
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

        // Reserved for the upcoming forced-password-change flow; always false
        // until that lands so clients can start reading it today.
        return Ok(new { mustChangePassword = false });
    }

    /// <summary>
    /// Check whether any user account exists. The settings UI calls this before
    /// enabling authentication so it cannot lock the instance out with zero users.
    /// </summary>
    [HttpGet("users/any")]
    public async Task<ActionResult<bool>> HasAnyUsers()
    {
        return Ok(await _authService.HasAnyUsers());
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
            return Ok(new { authenticated = true, authType = "disabled", mustChangePassword = false });
        }

        // If authentication is enabled, check if user is authenticated via Cookie
        if (!User.Identity?.IsAuthenticated ?? true)
        {
            return Unauthorized(new { authenticated = false });
        }

        return Ok(new { authenticated = true, authType = "Cookie", mustChangePassword = false });
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
    /// <remarks>
    /// The <c>LingarrAccess</c> policy lets anonymous callers through while
    /// authentication is disabled, and accepts API-key principals otherwise —
    /// neither may mint a key. Minting is restricted to interactive cookie
    /// sessions so a leaked API key can never mint its own replacement and an
    /// open instance cannot hand out credentials to strangers.
    /// </remarks>
    [HttpPost("apikey/generate")]
    [Authorize("LingarrAccess")]
    public async Task<ActionResult<ApiKeyResponse>> GenerateNewApiKey()
    {
        var remoteIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (!User.IsCookieSession())
        {
            _logger.LogWarning(
                "Rejected API key generation request without a cookie session " +
                "(authenticated: {IsAuthenticated}) from {RemoteIp}.",
                User.Identity?.IsAuthenticated ?? false,
                remoteIp);

            return User.Identity?.IsAuthenticated == true
                ? StatusCode(StatusCodes.Status403Forbidden,
                    new { message = "API key generation requires a logged-in browser session" })
                : Unauthorized(new { message = "API key generation requires a logged-in browser session" });
        }

        var apiKey = _authService.GenerateApiKey();
        await _settingService.SetEncryptedSetting(SettingKeys.Authentication.ApiKey, apiKey);
        return Ok(new ApiKeyResponse
        {
            ApiKey = apiKey
        });
    }
}
