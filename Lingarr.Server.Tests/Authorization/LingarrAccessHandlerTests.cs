using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Lingarr.Core.Configuration;
using Lingarr.Server.Authorization;
using Lingarr.Server.Interfaces.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Authorization;

/// <summary>
/// The single access gate for controllers and hubs: auth-disabled bypass,
/// cookie/api-key acceptance.
/// </summary>
public class LingarrAccessHandlerTests
{
    private readonly Mock<ISettingService> _settings = new();
    private readonly Mock<IAuthService> _authService = new();

    private async Task<AuthorizationHandlerContext> HandleAsync(
        ClaimsPrincipal? user = null,
        string? apiKeyHeader = null)
    {
        var httpContext = new DefaultHttpContext { User = user ?? new ClaimsPrincipal() };
        httpContext.RequestServices = new StubServiceProvider(_settings.Object, _authService.Object);
        if (apiKeyHeader != null)
        {
            httpContext.Request.Headers[ApiKeyAuthenticationHandler.HeaderName] = apiKeyHeader;
        }

        var requirement = new LingarrAccessRequirement();
        var context = new AuthorizationHandlerContext([requirement], user ?? new ClaimsPrincipal(), httpContext);
        var handler = new LingarrAccessHandler();
        await handler.HandleAsync(context);
        return context;
    }

    private void AuthEnabled(string value = "true") =>
        _settings.Setup(s => s.GetSetting(SettingKeys.Authentication.AuthEnabled))
            .ReturnsAsync(value);

    [Fact]
    public async Task Succeeds_WhenAuthenticationDisabled()
    {
        AuthEnabled("false");

        var context = await HandleAsync(); // anonymous

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task Succeeds_ForAuthenticatedUser()
    {
        AuthEnabled("true");

        var context = await HandleAsync(user: Authenticated());

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task Succeeds_ForValidApiKeyHeader()
    {
        AuthEnabled("true");
        _authService.Setup(a => a.ValidateApiKey("valid-key")).ReturnsAsync(true);

        var context = await HandleAsync(apiKeyHeader: "valid-key");

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task Fails_ForAnonymousWithoutApiKey()
    {
        AuthEnabled("true");

        var context = await HandleAsync();

        Assert.True(context.HasFailed);
    }

    [Fact]
    public async Task Fails_ForInvalidApiKeyHeader()
    {
        AuthEnabled("true");
        _authService.Setup(a => a.ValidateApiKey("wrong-key")).ReturnsAsync(false);

        var context = await HandleAsync(apiKeyHeader: "wrong-key");

        Assert.True(context.HasFailed);
    }

    private static ClaimsPrincipal Authenticated() =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.Name, "henry")], authenticationType: "Cookies"));

    /// <summary>
    /// Minimal service provider for the two services the handler resolves
    /// from <c>HttpContext.RequestServices</c>.
    /// </summary>
    private sealed class StubServiceProvider(ISettingService settings, IAuthService authService)
        : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(ISettingService) ? settings :
            serviceType == typeof(IAuthService) ? authService :
            null;
    }
}
