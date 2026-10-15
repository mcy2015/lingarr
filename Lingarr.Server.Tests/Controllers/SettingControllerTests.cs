using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Lingarr.Core.Entities;
using Lingarr.Server.Controllers;
using Lingarr.Server.Modules.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Lingarr.Server.Modules.Settings;

namespace Lingarr.Server.Tests.Controllers;

/// <summary>
/// P2-5 audit lines: every settings write path logs one Information entry
/// carrying the key(s) and whether the value was stored encrypted — and
/// never the value itself. The per-key reveal endpoint keeps that contract:
/// cookie sessions only, one audit line without the plaintext.
/// </summary>
public class SettingControllerTests
{
    private readonly Mock<ISettingService> _settingService = new();
    private readonly Mock<ILogger<SettingController>> _logger = new();
    private readonly SettingController _controller;

    public SettingControllerTests()
    {
        _controller = new SettingController(_settingService.Object, _logger.Object);
    }

    [Fact]
    public async Task SetSetting_Success_LogsKeyWithoutValue()
    {
        _settingService.Setup(s => s.SetSetting("movie_schedule", "0 4 * * *")).ReturnsAsync(true);

        await _controller.SetSetting(new Setting { Key = "movie_schedule", Value = "0 4 * * *" });

        AssertInformationLog(count: 1, containing: "movie_schedule", notContaining: "0 4 * * *");
    }

    [Fact]
    public async Task SetSetting_Failure_DoesNotLogAnAuditLine()
    {
        _settingService.Setup(s => s.SetSetting(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(false);

        var action = await _controller.SetSetting(new Setting { Key = "movie_schedule", Value = "x" });

        Assert.IsType<BadRequestObjectResult>(action.Result);
        AssertInformationLog(count: 0, containing: "movie_schedule");
    }

    [Fact]
    public async Task SetSettings_Success_LogsAllKeysWithoutValues()
    {
        _settingService
            .Setup(s => s.SetSettings(It.IsAny<Dictionary<string, string>>()))
            .ReturnsAsync(true);

        await _controller.SetSettings(new Dictionary<string, string>
        {
            ["movie_schedule"] = "secret-1",
            ["show_schedule"] = "secret-2"
        });

        AssertInformationLog(count: 1, containing: "movie_schedule", notContaining: "secret-1");
        AssertInformationLog(count: 1, containing: "show_schedule", notContaining: "secret-2");
    }

    [Fact]
    public async Task SetEncryptedSetting_Success_LogsKeyAndEncryptionMarkerWithoutValue()
    {
        _settingService
            .Setup(s => s.SetEncryptedSetting("api_key", "super-secret-key"))
            .ReturnsAsync(true);

        await _controller.SetEncryptedSetting(new Setting { Key = "api_key", Value = "super-secret-key" });

        AssertInformationLog(count: 1, containing: "api_key", notContaining: "super-secret-key");
        AssertInformationLog(count: 1, containing: "encrypted");
    }

    // ===== Batch encrypted read: configured flags only, never plaintext =====

    [Fact]
    public async Task GetEncryptedSettings_ReturnsConfiguredFlagsPerKey()
    {
        // Security walk finding: this endpoint used to hand every decrypted
        // secret to any logged-in session. The response is now a per-key
        // "configured" flag; the dictionary value type pins that shape.
        _settingService
            .Setup(s => s.GetEncryptedSettingStates(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(new Dictionary<string, bool>
            {
                ["radarr_api_key"] = true,
                ["openai_api_key"] = false
            });

        var action = await _controller.GetEncryptedSettings(
            new[] { "radarr_api_key", "openai_api_key" });

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var states = Assert.IsType<Dictionary<string, bool>>(ok.Value);
        Assert.True(states["radarr_api_key"]);
        Assert.False(states["openai_api_key"]);
        Assert.All(states.Values, value => Assert.IsType<bool>(value));
    }

    [Fact]
    public async Task GetEncryptedSettings_DelegatesToTheStatesMethodNotThePlaintextRead()
    {
        // The controller must never call a plaintext-valued bulk decrypt again.
        _settingService
            .Setup(s => s.GetEncryptedSettingStates(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(new Dictionary<string, bool>());

        await _controller.GetEncryptedSettings(new[] { "api_key" });

        _settingService.Verify(
            s => s.GetEncryptedSettingStates(It.Is<IEnumerable<string>>(keys => keys.SequenceEqual(new[] { "api_key" }))),
            Times.Once);
    }

    [Fact]
    public async Task GetEncryptedSettings_ReadIsNotAudited()
    {
        // Encrypted reads intentionally add no log lines (audit requirement);
        // only writes log.
        _settingService
            .Setup(s => s.GetEncryptedSettingStates(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(new Dictionary<string, bool>());

        await _controller.GetEncryptedSettings(new[] { "api_key" });

        _logger.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains("api_key")),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);
    }

    // ===== Per-key reveal: cookie sessions only, audited without the value =====

    [Fact]
    public async Task RevealEncryptedSetting_AnonymousCaller_Returns401()
    {
        SignIn(new ClaimsPrincipal(new ClaimsIdentity()));

        var action = await _controller.RevealEncryptedSetting("radarr_api_key");

        var unauthorized = Assert.IsType<UnauthorizedObjectResult>(action);
        Assert.Equal(StatusCodes.Status401Unauthorized, unauthorized.StatusCode);
        _settingService.Verify(
            s => s.GetEncryptedSetting(It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task RevealEncryptedSetting_ApiKeyPrincipal_Returns403()
    {
        SignIn(ApiKeyPrincipal());

        var action = await _controller.RevealEncryptedSetting("radarr_api_key");

        var forbidden = Assert.IsType<ObjectResult>(action);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
        _settingService.Verify(
            s => s.GetEncryptedSetting(It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task RevealEncryptedSetting_CookieSession_ReturnsDecryptedValue()
    {
        SignIn(CookiePrincipal("henry"));
        _settingService
            .Setup(s => s.GetEncryptedSetting("radarr_api_key"))
            .ReturnsAsync("the-plaintext-key");

        var action = await _controller.RevealEncryptedSetting("radarr_api_key");

        var ok = Assert.IsType<OkObjectResult>(action);
        Assert.Equal("the-plaintext-key", new ResponseAdapter(ok.Value!).Get("value"));
    }

    [Fact]
    public async Task RevealEncryptedSetting_CookieSessionWithUnsetKey_ReturnsEmptyString()
    {
        SignIn(CookiePrincipal("henry"));
        _settingService
            .Setup(s => s.GetEncryptedSetting("radarr_api_key"))
            .ReturnsAsync((string?)null);

        var action = await _controller.RevealEncryptedSetting("radarr_api_key");

        var ok = Assert.IsType<OkObjectResult>(action);
        Assert.Equal(string.Empty, new ResponseAdapter(ok.Value!).Get("value"));
    }

    [Fact]
    public async Task RevealEncryptedSetting_CookieSession_LogsKeyAndUserWithoutTheValue()
    {
        SignIn(CookiePrincipal("henry"));
        _settingService
            .Setup(s => s.GetEncryptedSetting("radarr_api_key"))
            .ReturnsAsync("the-plaintext-key");

        await _controller.RevealEncryptedSetting("radarr_api_key");

        AssertInformationLog(
            count: 1,
            containing: "radarr_api_key",
            notContaining: "the-plaintext-key");
    }

    /// <summary>Sets the controller's HttpContext principal and remote IP.</summary>
    private void SignIn(ClaimsPrincipal principal)
    {
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
        _controller.HttpContext.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.10");
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

    /// <summary>
    /// Verifies the controller emitted exactly <paramref name="count"/>
    /// Information entries whose rendered message contains (and optionally
    /// does not contain) the given fragments.
    /// </summary>
    private void AssertInformationLog(
        int count,
        string containing,
        string? notContaining = null)
    {
        _logger.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((state, _) =>
                    state.ToString()!.Contains(containing)
                    && (notContaining == null || !state.ToString()!.Contains(notContaining))),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Exactly(count));
    }
}
