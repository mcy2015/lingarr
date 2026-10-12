using System.Security.Claims;
using Lingarr.Core.Configuration;
using Lingarr.Core.Data;

using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Models.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

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
    /// Create the first user (signup wizard)
    /// </summary>
    [HttpPost("signup")]
    [AllowAnonymous]
    public async Task<ActionResult> Signup([FromBody] LoginRequest request)
    {
        try
        {
            var authEnabled = await _settingService.GetSetting(SettingKeys.Authentication.AuthEnabled);
            var isAuthenticated = User.Identity?.IsAuthenticated ?? false;
            if (authEnabled != "false" && !isAuthenticated)
            {
                return Unauthorized(new { message = "Authentication required" });
            }

            if (string.IsNullOrWhiteSpace(request.Username) || request.Username.Length < 2)
            {
                return BadRequest(new { message = "Username must be at least 2 characters long" });
            }

            if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Length < 8)
            {
                return BadRequest(new { message = "Password must be at least 8 characters long" });
            }

            // Create user and sign in
            var user = await _authService.CreateUser(request.Username, request.Password);
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

            _logger.LogInformation("User {Username} created successfully", user.Username);

            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during signup");
            return StatusCode(500, new { message = "An error occurred during signup" });
        }
    }

    /// <summary>
    /// Verify if the request is authenticated via Cookie and check onboarding status
    /// </summary>
    [HttpGet("authenticated")]
    [AllowAnonymous]
    public async Task<ActionResult> Authenticated()
    {
        var onboardingCompleted = await _settingService.GetSetting(SettingKeys.Authentication.OnboardingCompleted);

        // If onboarding is not completed, redirect to onboarding
        if (onboardingCompleted != "true")
        {
            return Ok(new { authenticated = false, requiresOnboarding = true });
        }

        var authEnabled = await _settingService.GetSetting(SettingKeys.Authentication.AuthEnabled);

        // If authentication is disabled, always return true
        if (authEnabled == "false")
        {
            return Ok(new { authenticated = true, authType = "disabled", requiresOnboarding = false });
        }

        // If authentication is enabled, check if user is authenticated via Cookie
        if (!User.Identity?.IsAuthenticated ?? true)
        {
            return Unauthorized(new { authenticated = false, requiresOnboarding = false });
        }

        return Ok(new { authenticated = true, authType = "Cookie", requiresOnboarding = false });
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
    /// Complete onboarding and configure authentication
    /// </summary>
    [HttpPost("onboarding")]
    [AllowAnonymous]
    public async Task<ActionResult> CompleteOnboarding([FromBody] OnboardingRequest request)
    {
        var onboardingCompleted = await _settingService.GetSetting(SettingKeys.Authentication.OnboardingCompleted);
        if (onboardingCompleted == "true")
        {
            return Conflict(new { message = "Onboarding has already been completed" });
        }

        await _settingService.SetSettings(new Dictionary<string, string>
        {
            { SettingKeys.Authentication.AuthEnabled, request.EnableUserAuth },
            { SettingKeys.Authentication.OnboardingCompleted, "true" }
        });

        return Ok();
    }

    /// <summary>
    /// Generate a new API key (only during onboarding or if not already exists)
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

    /// <summary>
    /// Check if any users exist in the system
    /// </summary>
    [HttpGet("users/any")]
    [AllowAnonymous]
    public async Task<ActionResult<bool>> HasAnyUsers()
    {
        var hasUsers = await _authService.HasAnyUsers();
        return Ok(hasUsers);
    }

    /// <summary>
    /// Get all users
    /// </summary>
    [HttpGet("users")]
    [Authorize("LingarrAccess")]
    public async Task<ActionResult> GetAllUsers()
    {
        var users = await _authService.GetAllUsers();
        var userDtos = users.Select(u => new
        {
            u.Id,
            u.Username,
            u.CreatedAt,
            u.LastLoginAt
        });
        return Ok(userDtos);
    }

    /// <summary>
    /// Update a user
    /// </summary>
    [HttpPut("users/{id}")]
    [Authorize("LingarrAccess")]
    public async Task<ActionResult> UpdateUser(int id, [FromBody] UpdateUserRequest request)
    {
        var user = await _authService.GetUserById(id);
        if (user == null)
        {
            return NotFound(new { message = "User not found" });
        }

        if (!string.IsNullOrWhiteSpace(request.Username))
        {
            if (request.Username.Length < 2)
            {
                return BadRequest(new { message = "Username must be at least 2 characters long" });
            }

            if (await _authService.IsUsernameTaken(request.Username, id))
            {
                return BadRequest(new { message = "Username already exists" });
            }

            user.Username = request.Username;
        }

        if (!string.IsNullOrWhiteSpace(request.Password))
        {
            if (request.Password.Length < 8)
            {
                return BadRequest(new { message = "Password must be at least 8 characters long" });
            }

            user.PasswordHash = _authService.HashPassword(request.Password);
        }

        await _authService.SaveUserAsync(user);

        _logger.LogInformation("User {Username} updated", user.Username);

        return Ok(new { message = "User updated successfully" });
    }

    /// <summary>
    /// Delete a user
    /// </summary>
    [HttpDelete("users/{id}")]
    [Authorize("LingarrAccess")]
    public async Task<ActionResult> DeleteUser(int id)
    {
        var user = await _authService.GetUserById(id);
        if (user == null)
        {
            return NotFound(new { message = "User not found" });
        }

        try
        {
            await _authService.DeleteUserAsync(user);
        }
        catch (InvalidOperationException)
        {
            return BadRequest(new { message = "Cannot delete the last user" });
        }

        _logger.LogInformation("User {Username} deleted", user.Username);

        return Ok(new { message = "User deleted successfully" });
    }
}
