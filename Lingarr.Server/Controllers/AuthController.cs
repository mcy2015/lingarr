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
    private readonly IAuthSessionStore _sessionStore;
    private readonly ILogger<AuthController> _logger;

    public AuthController(
        IAuthService authService,
        ISettingService settingService,
        ILoginRateLimiter loginRateLimiter,
        IAuthSessionStore sessionStore,
        ILogger<AuthController> logger)
    {
        _authService = authService;
        _settingService = settingService;
        _loginRateLimiter = loginRateLimiter;
        _sessionStore = sessionStore;
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
        if (user == null)
        {
            // P1-3 timing alignment: unknown usernames still run one full
            // PBKDF2 verification against a fixed dummy hash so the "no such
            // user" and "wrong password" branches take the same time and the
            // response cannot be used to enumerate valid usernames.
            _authService.VerifyPassword(request.Password, AuthService.DummyPasswordHash);
            return LoginFailed(request.Username, remoteIp);
        }

        // P2-2: verification honours the format embedded in the stored hash
        // (legacy 100k or current 600k iterations) and transparently re-hashes
        // legacy hashes at the current cost on success.
        if (!await _authService.VerifyPasswordAndUpgradeAsync(user, request.Password))
        {
            return LoginFailed(request.Username, remoteIp);
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

        // Opportunistic cleanup instead of a background timer (P0-2).
        await _sessionStore.RemoveExpiredAsync();

        return Ok(new { mustChangePassword = await _authService.GetMustChangePasswordAsync() });
    }

    /// <summary>
    /// Shared failure tail of the login branches (rate-limit accounting,
    /// warning log, 401 with an identical body for both failure kinds).
    /// </summary>
    private ActionResult LoginFailed(string username, string remoteIp)
    {
        _loginRateLimiter.RecordFailure(username, remoteIp);
        _logger.LogWarning(
            "Failed login attempt for user {Username} from {RemoteIp}.",
            username,
            remoteIp);
        return Unauthorized(new { message = "Invalid username or password" });
    }

    /// <summary>
    /// Check whether any user account exists. The settings UI calls this before
    /// enabling authentication so it cannot lock the instance out with zero users.
    /// </summary>
    /// <remarks>
    /// Deliberately anonymous (P2-6): the probe must also work while
    /// authentication is enabled but no session exists yet — otherwise the UI
    /// precondition check itself 401s and the user-enable flow dead-ends.
    /// This reveals whether at least one user exists; in the single-user model
    /// that answer is always "yes" for any working instance, so it discloses
    /// nothing an attacker could act on.
    /// </remarks>
    [HttpGet("users/any")]
    [AllowAnonymous]
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

        return Ok(new
        {
            authenticated = true,
            authType = "Cookie",
            mustChangePassword = await _authService.GetMustChangePasswordAsync()
        });
    }

    /// <summary>
    /// Logout the current user
    /// </summary>
    [HttpPost("logout")]
    [Authorize("LingarrAccess")]
    public async Task<ActionResult> Logout()
    {
        // SignOutAsync makes the cookie handler drop the auth_sessions row
        // behind the browser's cookie key, so replays of the old cookie fail
        // with 401 (P0-2).
        await HttpContext.SignOutAsync("Cookies");

        // Opportunistic cleanup instead of a background timer (P0-2).
        await _sessionStore.RemoveExpiredAsync();
        return Ok(new { message = "Logged out successfully" });
    }

    /// <summary>
    /// Change the password of the logged-in user (P0-3). On success every
    /// session of the user — including the current one — is revoked, so the
    /// client must sign in again with the new password; this is deliberate
    /// and reported as such in the response message.
    /// </summary>
    [HttpPost("password")]
    [Authorize("LingarrAccess")]
    public async Task<ActionResult> ChangePassword([FromBody] ChangePasswordRequest request)
    {
        var remoteIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (!User.IsCookieSession())
        {
            _logger.LogWarning(
                "Rejected password change without a cookie session from {RemoteIp}.",
                remoteIp);

            return User.Identity?.IsAuthenticated == true
                ? StatusCode(StatusCodes.Status403Forbidden, new
                {
                    reason = "no_session",
                    message = "Password changes require a logged-in browser session"
                })
                : Unauthorized(new
                {
                    reason = "no_session",
                    message = "Password changes require a logged-in browser session"
                });
        }

        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!int.TryParse(userIdClaim, out var userId))
        {
            return Unauthorized(new { reason = "no_session", message = "No user is attached to this session" });
        }

        if (string.IsNullOrWhiteSpace(request.CurrentPassword) || string.IsNullOrWhiteSpace(request.NewPassword))
        {
            return BadRequest(new
            {
                reason = "validation",
                message = "Current and new password are required"
            });
        }

        // Cheap policy check first, so malformed requests never burn a PBKDF2 run.
        if (!_authService.IsPasswordStrong(request.NewPassword))
        {
            return BadRequest(new
            {
                reason = "weak_password",
                message = "The new password must be at least 8 characters long and contain a letter and a digit"
            });
        }

        var user = await _authService.GetUserById(userId);
        if (user == null)
        {
            return Unauthorized(new { reason = "no_session", message = "No user is attached to this session" });
        }

        if (!_authService.VerifyPassword(request.CurrentPassword, user.PasswordHash))
        {
            return Unauthorized(new
            {
                reason = "invalid_current_password",
                message = "The current password is incorrect"
            });
        }

        await _authService.ChangePasswordAsync(user, request.NewPassword);
        await _authService.SetMustChangePasswordAsync(false);

        // Revoke every session of the user, including the one making the
        // request: the caller must authenticate again with the new password.
        await _sessionStore.RemoveAllForUserAsync(userId);

        _logger.LogInformation("User {Username} changed their password; all sessions were revoked.", user.Username);
        return Ok(new
        {
            message = "Password updated. All sessions were signed out; please sign in again.",
            mustChangePassword = false
        });
    }

    /// <summary>
    /// Change the username of the logged-in user. Requires the current
    /// password as confirmation; the session stays valid because the user
    /// id the ticket carries is unchanged.
    /// </summary>
    [HttpPut("username")]
    [Authorize("LingarrAccess")]
    public async Task<ActionResult> ChangeUsername([FromBody] ChangeUsernameRequest request)
    {
        var remoteIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (!User.IsCookieSession())
        {
            _logger.LogWarning(
                "Rejected username change without a cookie session from {RemoteIp}.",
                remoteIp);

            return User.Identity?.IsAuthenticated == true
                ? StatusCode(StatusCodes.Status403Forbidden, new
                {
                    reason = "no_session",
                    message = "Username changes require a logged-in browser session"
                })
                : Unauthorized(new
                {
                    reason = "no_session",
                    message = "Username changes require a logged-in browser session"
                });
        }

        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!int.TryParse(userIdClaim, out var userId))
        {
            return Unauthorized(new { reason = "no_session", message = "No user is attached to this session" });
        }

        var newUsername = request.NewUsername?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(request.CurrentPassword) || newUsername.Length == 0)
        {
            return BadRequest(new
            {
                reason = "validation",
                message = "Current password and new username are required"
            });
        }

        if (!System.Text.RegularExpressions.Regex.IsMatch(newUsername, "^[A-Za-z0-9._-]{3,32}$"))
        {
            return BadRequest(new
            {
                reason = "invalid_username",
                message = "The username must be 3-32 characters of letters, digits, dot, dash or underscore"
            });
        }

        var user = await _authService.GetUserById(userId);
        if (user == null)
        {
            return Unauthorized(new { reason = "no_session", message = "No user is attached to this session" });
        }

        if (!_authService.VerifyPassword(request.CurrentPassword, user.PasswordHash))
        {
            return Unauthorized(new
            {
                reason = "invalid_current_password",
                message = "The current password is incorrect"
            });
        }

        if (string.Equals(user.Username, newUsername, StringComparison.Ordinal))
        {
            return BadRequest(new
            {
                reason = "same_username",
                message = "This is already your username"
            });
        }

        if (!await _authService.ChangeUsernameAsync(user, newUsername))
        {
            return Conflict(new
            {
                reason = "username_taken",
                message = "This username is already taken"
            });
        }

        _logger.LogInformation(
            "User {OldUsername} renamed to {NewUsername} from {RemoteIp}.",
            user.Username,
            newUsername,
            remoteIp);
        return Ok(new { username = newUsername });
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

    /// <summary>
    /// Read the current API key.
    /// </summary>
    /// <remarks>
    /// Security walk finding: the settings UI used to learn the key from the
    /// bulk encrypted-settings read, which decrypted every secret for any
    /// logged-in session. Revealing the key is now this dedicated endpoint with
    /// exactly the guard of <see cref="GenerateNewApiKey"/>: showing the key is
    /// an explicit user action, so its exposure surface equals minting one —
    /// anonymous callers get 401, API-key principals 403, only interactive
    /// cookie sessions get the value. The one Information audit line ("API key
    /// revealed") never contains the key itself. Provider secrets have no such
    /// endpoint: they are write-only from the client's view.
    /// </remarks>
    [HttpGet("apikey")]
    [Authorize("LingarrAccess")]
    public async Task<ActionResult<ApiKeyResponse>> GetApiKey()
    {
        var remoteIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (!User.IsCookieSession())
        {
            _logger.LogWarning(
                "Rejected API key reveal request without a cookie session " +
                "(authenticated: {IsAuthenticated}) from {RemoteIp}.",
                User.Identity?.IsAuthenticated ?? false,
                remoteIp);

            return User.Identity?.IsAuthenticated == true
                ? StatusCode(StatusCodes.Status403Forbidden,
                    new { message = "API key display requires a logged-in browser session" })
                : Unauthorized(new { message = "API key display requires a logged-in browser session" });
        }

        var apiKey = await _settingService.GetEncryptedSetting(SettingKeys.Authentication.ApiKey);

        // Audit line: user + action only, never the value.
        _logger.LogInformation("API key revealed by user {Username}.", User.Identity?.Name);

        return Ok(new ApiKeyResponse
        {
            ApiKey = apiKey ?? string.Empty
        });
    }

    /// <summary>
    /// Revoke the current API key (P2-4) by clearing the encrypted api_key
    /// setting; an empty stored key never validates.
    /// </summary>
    /// <remarks>
    /// Guarded like generation: interactive cookie sessions only. A leaked
    /// API key must not be able to revoke the legitimate one (which would
    /// force a re-login-to-regenerate denial of the admin's tooling), just as
    /// it must not mint its own replacement.
    /// </remarks>
    [HttpPost("apikey/revoke")]
    [Authorize("LingarrAccess")]
    public async Task<ActionResult> RevokeApiKey()
    {
        var remoteIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (!User.IsCookieSession())
        {
            _logger.LogWarning(
                "Rejected API key revocation request without a cookie session " +
                "(authenticated: {IsAuthenticated}) from {RemoteIp}.",
                User.Identity?.IsAuthenticated ?? false,
                remoteIp);

            return User.Identity?.IsAuthenticated == true
                ? StatusCode(StatusCodes.Status403Forbidden,
                    new { message = "API key revocation requires a logged-in browser session" })
                : Unauthorized(new { message = "API key revocation requires a logged-in browser session" });
        }

        await _settingService.SetEncryptedSetting(SettingKeys.Authentication.ApiKey, string.Empty);

        _logger.LogInformation("User {Username} revoked the API key.", User.Identity?.Name);
        return Ok(new { message = "API key revoked" });
    }
}
