using System;
using System.Net;
using System.Security.Claims;
using System.Threading.Tasks;
using Lingarr.Server.Controllers;
using Lingarr.Server.Models.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using Lingarr.Server.Modules.Auth;
using Lingarr.Server.Modules.Settings;

namespace Lingarr.Server.Tests.Controllers;

/// <summary>
/// Covers the settings-UI gate (GET /api/auth/users/any), the cookie-session
/// guard on API key minting (anonymous and API-key callers are rejected even
/// while authentication is disabled), and the login rate limiter wiring.
/// </summary>
public class AuthControllerTests
{
    private static readonly IPAddress RemoteIp = IPAddress.Parse("203.0.113.10");

    private readonly Mock<IAuthService> _authService = new();
    private readonly Mock<ISettingService> _settingService = new();
    private readonly Mock<ILoginRateLimiter> _loginRateLimiter = new();
    private readonly Mock<IAuthSessionStore> _sessionStore = new();
    private readonly AuthController _controller;

    public AuthControllerTests()
    {
        _controller = CreateController(_loginRateLimiter.Object);
    }

    private AuthController CreateController(ILoginRateLimiter rateLimiter)
    {
        var controller = new AuthController(
            _authService.Object,
            _settingService.Object,
            rateLimiter,
            _sessionStore.Object,
            NullLogger<AuthController>.Instance);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
        controller.HttpContext.Connection.RemoteIpAddress = RemoteIp;
        return controller;
    }

    // ===== users/any =====

    [Fact]
    public async Task HasAnyUsers_ReturnsTrueWhenAUserExists()
    {
        _authService.Setup(s => s.HasAnyUsers()).ReturnsAsync(true);

        var result = await _controller.HasAnyUsers();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(true, ok.Value);
    }

    [Fact]
    public async Task HasAnyUsers_ReturnsFalseForEmptyUserTable()
    {
        _authService.Setup(s => s.HasAnyUsers()).ReturnsAsync(false);

        var result = await _controller.HasAnyUsers();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(false, ok.Value);
    }

    [Fact]
    public void HasAnyUsers_IsAllowAnonymous_SoTheUiProbeSurvivesLockedInstances()
    {
        // P2-6: with auth enabled and no session, the settings UI still needs
        // this probe — otherwise enabling auth dead-ends behind its own 401.
        var method = typeof(AuthController).GetMethod(nameof(AuthController.HasAnyUsers));
        Assert.NotNull(method);
        Assert.Contains(method.GetCustomAttributes(true), a => a is AllowAnonymousAttribute);
    }

    // ===== Login uses the upgrade-capable verification (P2-2) =====

    [Fact]
    public async Task Login_VerifiesThroughPasswordAndUpgradeWrapper()
    {
        var controller = CreateController(_loginRateLimiter.Object);
        var user = SetupSuccessfulLogin(controller, "henry");

        await controller.Login(new LoginRequest { Username = "henry", Password = "pw" });

        _authService.Verify(a => a.VerifyPasswordAndUpgradeAsync(user, "pw"), Times.Once);
    }

    [Fact]
    public async Task Login_FailedUpgradeVerification_Returns401WithoutSignIn()
    {
        var controller = CreateController(_loginRateLimiter.Object);
        var user = new Lingarr.Core.Entities.User { Id = 1, Username = "henry", PasswordHash = "hash" };
        _authService.Setup(a => a.GetUserByUsername("henry")).ReturnsAsync(user);
        _authService.Setup(a => a.VerifyPasswordAndUpgradeAsync(user, "wrong")).ReturnsAsync(false);

        var result = await controller.Login(new LoginRequest { Username = "henry", Password = "wrong" });

        Assert.IsType<UnauthorizedObjectResult>(result);
        _loginRateLimiter.Verify(l => l.RecordFailure("henry", RemoteIp.ToString()), Times.Once);
    }

    // ===== API key minting: cookie sessions only =====

    [Fact]
    public async Task GenerateNewApiKey_AnonymousCaller_Returns401()
    {
        // No User set on the HttpContext — anonymous, as when authentication
        // is disabled and LingarrAccess lets everyone through.
        var result = await _controller.GenerateNewApiKey();

        var unauthorized = Assert.IsType<UnauthorizedObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status401Unauthorized, unauthorized.StatusCode);
        _authService.Verify(a => a.GenerateApiKey(), Times.Never);
        _settingService.Verify(
            s => s.SetEncryptedSetting(It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task GenerateNewApiKey_ApiKeyPrincipal_Returns403()
    {
        _controller.HttpContext.User = ApiKeyPrincipal();

        var result = await _controller.GenerateNewApiKey();

        var forbidden = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
        _authService.Verify(a => a.GenerateApiKey(), Times.Never);
        _settingService.Verify(
            s => s.SetEncryptedSetting(It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task GenerateNewApiKey_CookieSession_ReturnsKey()
    {
        _controller.HttpContext.User = CookiePrincipal("henry");
        _authService.Setup(a => a.GenerateApiKey()).Returns("fresh-key");
        _settingService.Setup(s => s.SetEncryptedSetting("api_key", "fresh-key")).ReturnsAsync(true);

        var result = await _controller.GenerateNewApiKey();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<ApiKeyResponse>(ok.Value);
        Assert.Equal("fresh-key", response.ApiKey);
    }

    // ===== API key revocation (P2-4): cookie sessions only =====

    [Fact]
    public async Task RevokeApiKey_AnonymousCaller_Returns401AndKeepsStoredKey()
    {
        var result = await _controller.RevokeApiKey();

        var unauthorized = Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.Equal(StatusCodes.Status401Unauthorized, unauthorized.StatusCode);
        _settingService.Verify(
            s => s.SetEncryptedSetting(It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task RevokeApiKey_ApiKeyPrincipal_Returns403AndKeepsStoredKey()
    {
        _controller.HttpContext.User = ApiKeyPrincipal();

        var result = await _controller.RevokeApiKey();

        var forbidden = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
        _settingService.Verify(
            s => s.SetEncryptedSetting(It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task RevokeApiKey_CookieSession_ClearsStoredKeyToEmpty()
    {
        _controller.HttpContext.User = CookiePrincipal("henry");
        _settingService
            .Setup(s => s.SetEncryptedSetting("api_key", string.Empty))
            .ReturnsAsync(true);

        var result = await _controller.RevokeApiKey();

        Assert.IsType<OkObjectResult>(result);
        _settingService.Verify(s => s.SetEncryptedSetting("api_key", string.Empty), Times.Once);
    }

    // ===== Login: rate limiting + mustChangePassword contract =====

    [Fact]
    public async Task Login_Failure_Returns401()
    {
        _authService.Setup(a => a.GetUserByUsername("henry")).ReturnsAsync((Lingarr.Core.Entities.User?)null);

        var result = await _controller.Login(new LoginRequest { Username = "henry", Password = "wrong" });

        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    [Fact]
    public async Task Login_BlockedByRateLimiter_Returns429()
    {
        _loginRateLimiter.Setup(l => l.IsBlocked("henry", RemoteIp.ToString())).Returns(true);

        var result = await _controller.Login(new LoginRequest { Username = "henry", Password = "pw" });

        var tooMany = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status429TooManyRequests, tooMany.StatusCode);
        // Blocked attempts never reach the credential check.
        _authService.Verify(a => a.GetUserByUsername(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Login_Failure_RecordsFailureOnLimiter()
    {
        _authService.Setup(a => a.GetUserByUsername("henry")).ReturnsAsync((Lingarr.Core.Entities.User?)null);

        await _controller.Login(new LoginRequest { Username = "henry", Password = "wrong" });

        _loginRateLimiter.Verify(l => l.RecordFailure("henry", RemoteIp.ToString()), Times.Once);
    }

    // ===== P1-3: timing alignment on unknown usernames =====

    [Fact]
    public async Task Login_UnknownUser_RunsDummyVerificationAgainstFixedHash()
    {
        // The "no such user" branch must burn the same PBKDF2 work as the
        // "wrong password" branch, so login timing cannot reveal which
        // usernames exist.
        _authService.Setup(a => a.GetUserByUsername("ghost")).ReturnsAsync((Lingarr.Core.Entities.User?)null);

        await _controller.Login(new LoginRequest { Username = "ghost", Password = "wrong" });

        _authService.Verify(a => a.VerifyPassword("wrong", AuthService.DummyPasswordHash), Times.Once);
    }

    [Fact]
    public async Task Login_DummyHashConstant_IsAWellFormedPbkdf2Hash()
    {
        // If the constant ever drifts out of the iterations.salt.hash format,
        // the dummy verification would shortcut and reopen the timing side
        // channel (P1-3 + P2-2).
        var parts = AuthService.DummyPasswordHash.Split('.');
        Assert.Equal(3, parts.Length);
        Assert.Equal(AuthService.Pbkdf2Iterations.ToString(), parts[0]);
        Assert.NotEmpty(Convert.FromBase64String(parts[1]));
        Assert.NotEmpty(Convert.FromBase64String(parts[2]));
    }

    [Fact]
    public async Task Login_Success_ReturnsMustChangePasswordFalse()
    {
        var controller = CreateController(_loginRateLimiter.Object);
        SetupSuccessfulLogin(controller, "henry");
        _authService.Setup(a => a.GetMustChangePasswordAsync()).ReturnsAsync(false);

        var result = await controller.Login(new LoginRequest { Username = "henry", Password = "pw" });

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = new ResponseAdapter(ok.Value!);
        Assert.False((bool)response.Get("mustChangePassword")!);
    }

    [Fact]
    public async Task Login_Success_MustChangePasswordPending_ReturnsTrueAndPurgesExpiredSessions()
    {
        var controller = CreateController(_loginRateLimiter.Object);
        SetupSuccessfulLogin(controller, "henry");
        _authService.Setup(a => a.GetMustChangePasswordAsync()).ReturnsAsync(true);

        var result = await controller.Login(new LoginRequest { Username = "henry", Password = "pw" });

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = new ResponseAdapter(ok.Value!);
        Assert.True((bool)response.Get("mustChangePassword")!);
        _sessionStore.Verify(s => s.RemoveExpiredAsync(), Times.Once);
    }

    // ===== Authenticated response contract =====

    [Fact]
    public async Task Authenticated_AuthDisabled_IncludesMustChangePasswordFalse()
    {
        _settingService.Setup(s => s.GetSetting("auth_enabled")).ReturnsAsync("false");

        var result = await _controller.Authenticated();

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = new ResponseAdapter(ok.Value!);
        Assert.True((bool)response.Get("authenticated")!);
        Assert.Equal("disabled", response.Get("authType"));
        Assert.False((bool)response.Get("mustChangePassword")!);
    }

    [Fact]
    public async Task Authenticated_CookieUser_IncludesMustChangePasswordFalse()
    {
        _settingService.Setup(s => s.GetSetting("auth_enabled")).ReturnsAsync("true");
        _controller.HttpContext.User = CookiePrincipal("henry");
        _authService.Setup(a => a.GetMustChangePasswordAsync()).ReturnsAsync(false);

        var result = await _controller.Authenticated();

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = new ResponseAdapter(ok.Value!);
        Assert.True((bool)response.Get("authenticated")!);
        Assert.Equal("Cookie", response.Get("authType"));
        Assert.False((bool)response.Get("mustChangePassword")!);
    }

    [Fact]
    public async Task Authenticated_CookieUser_MustChangePasswordPending_ReturnsTrue()
    {
        _settingService.Setup(s => s.GetSetting("auth_enabled")).ReturnsAsync("true");
        _controller.HttpContext.User = CookiePrincipal("henry");
        _authService.Setup(a => a.GetMustChangePasswordAsync()).ReturnsAsync(true);

        var result = await _controller.Authenticated();

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = new ResponseAdapter(ok.Value!);
        Assert.True((bool)response.Get("mustChangePassword")!);
    }

    // ===== Logout: server-side revocation wiring =====

    [Fact]
    public async Task Logout_SignsOutCookieScheme_AndPurgesExpiredSessions()
    {
        // SignOutAsync drives the cookie handler to delete the auth_sessions
        // row behind the browser's cookie key (asserted end-to-end in
        // CookieTicketRevocationTests).
        var authentication = new Mock<IAuthenticationService>();
        authentication
            .Setup(a => a.SignOutAsync(It.IsAny<HttpContext>(), "Cookies", It.IsAny<AuthenticationProperties>()))
            .Returns(Task.CompletedTask);
        var provider = new Mock<IServiceProvider>();
        provider
            .Setup(p => p.GetService(typeof(IAuthenticationService)))
            .Returns(authentication.Object);
        _controller.HttpContext.RequestServices = provider.Object;

        var result = await _controller.Logout();

        Assert.IsType<OkObjectResult>(result);
        authentication.Verify(
            a => a.SignOutAsync(It.IsAny<HttpContext>(), "Cookies", It.IsAny<AuthenticationProperties>()),
            Times.Once);
        _sessionStore.Verify(s => s.RemoveExpiredAsync(), Times.Once);
    }

    // ===== POST /api/auth/password (P0-3) =====

    [Fact]
    public async Task ChangePassword_AnonymousCaller_Returns401()
    {
        var result = await _controller.ChangePassword(new ChangePasswordRequest
        {
            CurrentPassword = "old",
            NewPassword = "NewPass123"
        });

        var unauthorized = Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.Equal(StatusCodes.Status401Unauthorized, unauthorized.StatusCode);
        _authService.Verify(a => a.ChangePasswordAsync(It.IsAny<Lingarr.Core.Entities.User>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ChangePassword_ApiKeyPrincipal_Returns403()
    {
        _controller.HttpContext.User = ApiKeyPrincipal();

        var result = await _controller.ChangePassword(new ChangePasswordRequest
        {
            CurrentPassword = "old",
            NewPassword = "NewPass123"
        });

        var forbidden = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
        _authService.Verify(a => a.ChangePasswordAsync(It.IsAny<Lingarr.Core.Entities.User>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ChangePassword_MissingFields_Returns400WithValidationReason()
    {
        _controller.HttpContext.User = CookiePrincipal("henry");

        var result = await _controller.ChangePassword(new ChangePasswordRequest
        {
            CurrentPassword = "",
            NewPassword = ""
        });

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, bad.StatusCode);
        Assert.Equal("validation", new ResponseAdapter(bad.Value!).Get("reason"));
        _authService.Verify(a => a.VerifyPassword(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData("short1")]       // too short
    [InlineData("nodigits")]     // no digit
    [InlineData("12345678")]     // no letter
    public async Task ChangePassword_WeakNewPassword_Returns400WithWeakPasswordReason(string weak)
    {
        _controller.HttpContext.User = CookiePrincipal("henry");

        var result = await _controller.ChangePassword(new ChangePasswordRequest
        {
            CurrentPassword = "old-password",
            NewPassword = weak
        });

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, bad.StatusCode);
        Assert.Equal("weak_password", new ResponseAdapter(bad.Value!).Get("reason"));
        // The policy check runs before the PBKDF2 verification of the old password.
        _authService.Verify(a => a.VerifyPassword(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ChangePassword_WrongCurrentPassword_Returns401WithReason()
    {
        _controller.HttpContext.User = CookiePrincipal("henry");
        var user = new Lingarr.Core.Entities.User { Id = 1, Username = "henry", PasswordHash = "hash" };
        _authService.Setup(a => a.GetUserById(1)).ReturnsAsync(user);
        _authService.Setup(a => a.IsPasswordStrong("NewPass123")).Returns(true);
        _authService.Setup(a => a.VerifyPassword("wrong-old", "hash")).Returns(false);

        var result = await _controller.ChangePassword(new ChangePasswordRequest
        {
            CurrentPassword = "wrong-old",
            NewPassword = "NewPass123"
        });

        var unauthorized = Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.Equal(StatusCodes.Status401Unauthorized, unauthorized.StatusCode);
        Assert.Equal("invalid_current_password", new ResponseAdapter(unauthorized.Value!).Get("reason"));
        _authService.Verify(a => a.ChangePasswordAsync(It.IsAny<Lingarr.Core.Entities.User>(), It.IsAny<string>()), Times.Never);
        _sessionStore.Verify(s => s.RemoveAllForUserAsync(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task ChangePassword_Success_UpdatesHashClearsFlagAndRevokesAllSessions()
    {
        _controller.HttpContext.User = CookiePrincipal("henry");
        var user = new Lingarr.Core.Entities.User { Id = 1, Username = "henry", PasswordHash = "hash" };
        _authService.Setup(a => a.GetUserById(1)).ReturnsAsync(user);
        _authService.Setup(a => a.VerifyPassword("OldPass123", "hash")).Returns(true);
        _authService.Setup(a => a.IsPasswordStrong("NewPass123")).Returns(true);

        var result = await _controller.ChangePassword(new ChangePasswordRequest
        {
            CurrentPassword = "OldPass123",
            NewPassword = "NewPass123"
        });

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.False((bool)new ResponseAdapter(ok.Value!).Get("mustChangePassword")!);
        _authService.Verify(a => a.ChangePasswordAsync(user, "NewPass123"), Times.Once);
        _authService.Verify(a => a.SetMustChangePasswordAsync(false), Times.Once);
        // Every session of the user — including the caller's own — is revoked.
        _sessionStore.Verify(s => s.RemoveAllForUserAsync(1), Times.Once);
    }

    // ===== Rate limiter integration through the controller =====

    [Fact]
    public async Task Login_SixthFailedAttemptWithinWindow_Returns429()
    {
        var controller = CreateWithRealLimiter(out var limiter);
        _authService.Setup(a => a.GetUserByUsername("henry")).ReturnsAsync((Lingarr.Core.Entities.User?)null);

        for (var i = 0; i < 5; i++)
        {
            var failure = await controller.Login(new LoginRequest { Username = "henry", Password = "wrong" });
            Assert.Equal(StatusCodes.Status401Unauthorized, Assert.IsType<UnauthorizedObjectResult>(failure).StatusCode);
        }

        var sixth = await controller.Login(new LoginRequest { Username = "henry", Password = "wrong" });
        var tooMany = Assert.IsType<ObjectResult>(sixth);
        Assert.Equal(StatusCodes.Status429TooManyRequests, tooMany.StatusCode);
        Assert.True(limiter.IsBlocked("henry", RemoteIp.ToString()));
    }

    [Fact]
    public async Task Login_WindowSlides_AllowsAttemptsAgain()
    {
        var controller = CreateWithRealLimiter(out var limiter);
        _authService.Setup(a => a.GetUserByUsername("henry")).ReturnsAsync((Lingarr.Core.Entities.User?)null);

        for (var i = 0; i < 5; i++)
        {
            await controller.Login(new LoginRequest { Username = "henry", Password = "wrong" });
        }

        Assert.True(limiter.IsBlocked("henry", RemoteIp.ToString()));

        // Once the oldest failure falls out of the one-minute window the pair
        // is unblocked and attempts are answered on their merits again.
        _time.Advance(TimeSpan.FromSeconds(61));
        Assert.False(limiter.IsBlocked("henry", RemoteIp.ToString()));

        var result = await controller.Login(new LoginRequest { Username = "henry", Password = "wrong" });
        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    [Fact]
    public async Task Login_RateLimit_IsKeyedPerUsernameAndIp()
    {
        var controller = CreateWithRealLimiter(out _);
        _authService.Setup(a => a.GetUserByUsername(It.IsAny<string>()))
            .ReturnsAsync((Lingarr.Core.Entities.User?)null);

        for (var i = 0; i < 5; i++)
        {
            await controller.Login(new LoginRequest { Username = "henry", Password = "wrong" });
        }

        // Same username from another address, and another username from the
        // same address, are unaffected by henry's exhaustion.
        controller.HttpContext.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.20");
        var otherIp = await controller.Login(new LoginRequest { Username = "henry", Password = "wrong" });
        Assert.IsType<UnauthorizedObjectResult>(otherIp);

        controller.HttpContext.Connection.RemoteIpAddress = RemoteIp;
        var otherUser = await controller.Login(new LoginRequest { Username = "admin", Password = "wrong" });
        Assert.IsType<UnauthorizedObjectResult>(otherUser);
    }

    [Fact]
    public async Task Login_Success_IsNotBlockedUnderTheLimit()
    {
        var controller = CreateWithRealLimiter(out _);
        SetupSuccessfulLogin(controller, "henry");

        var result = await controller.Login(new LoginRequest { Username = "henry", Password = "pw" });

        Assert.IsType<OkObjectResult>(result);
    }

    private ManualTimeProvider _time = new();

    private AuthController CreateWithRealLimiter(out LoginRateLimiter limiter)
    {
        _time = new ManualTimeProvider();
        limiter = new LoginRateLimiter(_time);
        return CreateController(limiter);
    }

    private Lingarr.Core.Entities.User SetupSuccessfulLogin(AuthController controller, string username)
    {
        var user = new Lingarr.Core.Entities.User
        {
            Id = 1,
            Username = username,
            PasswordHash = "hash"
        };
        _authService.Setup(a => a.GetUserByUsername(username)).ReturnsAsync(user);
        // Login verifies through the upgrade-capable wrapper (P2-2).
        _authService.Setup(a => a.VerifyPasswordAndUpgradeAsync(user, "pw")).ReturnsAsync(true);

        // SignInAsync resolves IAuthenticationService from RequestServices.
        var authentication = new Mock<IAuthenticationService>();
        authentication
            .Setup(a => a.SignInAsync(
                It.IsAny<HttpContext>(),
                It.IsAny<string>(),
                It.IsAny<ClaimsPrincipal>(),
                It.IsAny<AuthenticationProperties>()))
            .Returns(Task.CompletedTask);
        var provider = new Mock<IServiceProvider>();
        provider
            .Setup(p => p.GetService(typeof(IAuthenticationService)))
            .Returns(authentication.Object);
        controller.HttpContext.RequestServices = provider.Object;
        return user;
    }

    private static ClaimsPrincipal CookiePrincipal(string username) =>
        new(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "1"), new Claim(ClaimTypes.Name, username)],
            authenticationType: "Cookies"));

    /// <summary>Mirrors the principal built by ApiKeyAuthenticationHandler.</summary>
    private static ClaimsPrincipal ApiKeyPrincipal() =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.AuthenticationMethod, ApiKeyAuthenticationHandler.SchemeName),
                new Claim(ClaimTypes.Name, "ApiKey")
            ],
            authenticationType: ApiKeyAuthenticationHandler.SchemeName));

    /// <summary>Reads properties off anonymous response objects in assertions.</summary>
    private sealed class ResponseAdapter(object instance)
    {
        public object? Get(string propertyName) =>
            instance.GetType().GetProperty(propertyName)?.GetValue(instance);
    }

    /// <summary>Deterministic clock the rate limiter tests advance by hand.</summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);
    }
}
