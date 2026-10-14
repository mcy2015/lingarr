using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.Dashboard;
using Hangfire.MemoryStorage;
using Lingarr.Core.Configuration;
using Lingarr.Server.Modules.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;
using Lingarr.Server.Modules.Settings;

namespace Lingarr.Server.Tests.Authorization;

/// <summary>
/// P1-4: the dev panels (Hangfire dashboard, Swagger) authorize through the
/// same LingarrAccess policy as the API. These tests drive the guard and the
/// dashboard filter with a real DefaultAuthorizationService wired to the real
/// <see cref="LingarrAccessHandler"/> — proving they evaluate the actual
/// policy, not a parallel reimplementation.
/// </summary>
public class LingarrAccessGuardTests
{
    private readonly Mock<ISettingService> _settings = new();
    private readonly Mock<IAuthService> _authService = new();

    private IAuthorizationService CreateAuthorizationService()
    {
        var authorizationOptions = new AuthorizationOptions();
        authorizationOptions.AddPolicy(LingarrAccessGuard.PolicyName, policy =>
        {
            policy.AddAuthenticationSchemes("Cookies", ApiKeyAuthenticationHandler.SchemeName);
            policy.Requirements.Add(new LingarrAccessRequirement());
        });

        return new DefaultAuthorizationService(
            new DefaultAuthorizationPolicyProvider(Options.Create(authorizationOptions)),
            new DefaultAuthorizationHandlerProvider([new LingarrAccessHandler()]),
            NullLogger<DefaultAuthorizationService>.Instance,
            new DefaultAuthorizationHandlerContextFactory(),
            new DefaultAuthorizationEvaluator(),
            Options.Create(authorizationOptions));
    }

    private async Task<bool> IsAllowedAsync(
        ClaimsPrincipal? user = null,
        string? apiKeyHeader = null)
    {
        var httpContext = new DefaultHttpContext { User = user ?? new ClaimsPrincipal() };
        httpContext.RequestServices = new StubServiceProvider(
            CreateAuthorizationService(), _settings.Object, _authService.Object);
        if (apiKeyHeader != null)
        {
            httpContext.Request.Headers[ApiKeyAuthenticationHandler.HeaderName] = apiKeyHeader;
        }

        return await LingarrAccessGuard.IsAllowedAsync(httpContext);
    }

    private void AuthEnabled(string value = "true") =>
        _settings.Setup(s => s.GetSetting(SettingKeys.Authentication.AuthEnabled))
            .ReturnsAsync(value);

    [Fact]
    public async Task AllowsAnonymous_WhileAuthenticationIsDisabled()
    {
        // Dev convenience preserved: an auth-disabled instance (deliberately
        // open, LAN) keeps its dev panels reachable without a login.
        AuthEnabled("false");

        Assert.True(await IsAllowedAsync());
    }

    [Fact]
    public async Task DeniesAnonymous_WhenAuthenticationIsEnabled()
    {
        AuthEnabled("true");

        Assert.False(await IsAllowedAsync());
    }

    [Fact]
    public async Task DeniesAnonymous_WhenNoAuthSettingExists()
    {
        // No stored row behaves like auth-enabled (fail closed, P1-4).
        _settings.Setup(s => s.GetSetting(SettingKeys.Authentication.AuthEnabled))
            .ReturnsAsync((string?)null);

        Assert.False(await IsAllowedAsync());
    }

    [Fact]
    public async Task AllowsCookieSession_WhenAuthenticationIsEnabled()
    {
        AuthEnabled("true");
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "henry")],
            authenticationType: "Cookies"));

        Assert.True(await IsAllowedAsync(user: principal));
    }

    [Fact]
    public async Task AllowsValidApiKeyHeader_WhenAuthenticationIsEnabled()
    {
        AuthEnabled("true");
        _authService.Setup(a => a.ValidateApiKey("valid-key")).ReturnsAsync(true);

        Assert.True(await IsAllowedAsync(apiKeyHeader: "valid-key"));
    }

    [Fact]
    public async Task DeniesInvalidApiKeyHeader_WhenAuthenticationIsEnabled()
    {
        AuthEnabled("true");
        _authService.Setup(a => a.ValidateApiKey("wrong-key")).ReturnsAsync(false);

        Assert.False(await IsAllowedAsync(apiKeyHeader: "wrong-key"));
    }

    // ===== Hangfire dashboard filter delegates to the same policy =====

    [Fact]
    public void DashboardFilter_FollowsTheLingarrAccessPolicy()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.RequestServices = new StubServiceProvider(
            CreateAuthorizationService(), _settings.Object, _authService.Object);
        var dashboardContext = new AspNetCoreDashboardContext(
            new MemoryStorage(), new DashboardOptions(), httpContext);
        var filter = new LingarrDashboardAuthorizationFilter();

        // Auth enabled, anonymous dashboard request: denied (the P1-4 fix —
        // the dashboard used to accept everyone via Authorization = []).
        AuthEnabled("true");
        Assert.False(filter.Authorize(dashboardContext));

        // Auth disabled (deliberately open instance): still reachable for
        // plain-http LAN dev convenience.
        AuthEnabled("false");
        Assert.True(filter.Authorize(dashboardContext));
    }

    /// <summary>
    /// Minimal provider for the three services resolved during a guard
    /// evaluation: the authorization service the guard asks for, plus the
    /// setting/auth services the LingarrAccessHandler resolves itself.
    /// </summary>
    private sealed class StubServiceProvider(
        IAuthorizationService authorizationService,
        ISettingService settings,
        IAuthService authService) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IAuthorizationService) ? authorizationService :
            serviceType == typeof(ISettingService) ? settings :
            serviceType == typeof(IAuthService) ? authService :
            null;
    }
}
