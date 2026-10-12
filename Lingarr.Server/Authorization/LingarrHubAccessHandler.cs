using Lingarr.Core.Configuration;
using Lingarr.Server.Interfaces.Services;
using Microsoft.AspNetCore.Authorization;

namespace Lingarr.Server.Authorization;

/// <summary>
/// Requirement protecting SignalR hub endpoints. Mirrors the semantics of
/// <c>LingarrAuthorizeAttribute</c> (onboarding gate, auth-disabled bypass,
/// cookie or API-key authentication) so hubs are not left unauthenticated
/// while controllers use the MVC filter.
/// </summary>
public class LingarrHubAccessRequirement : IAuthorizationRequirement
{
}

public class LingarrHubAccessHandler : IAuthorizationHandler
{
    public async Task HandleAsync(AuthorizationHandlerContext context)
    {
        var requirement = context.Requirements.OfType<LingarrHubAccessRequirement>().FirstOrDefault();
        if (requirement == null)
        {
            return; // not our requirement — other handlers decide
        }

        var httpContext = context.Resource as HttpContext;
        if (httpContext == null)
        {
            context.Fail(new AuthorizationFailureReason(this, "SignalR authorization requires an HTTP context."));
            return;
        }

        var settingService = httpContext.RequestServices.GetRequiredService<ISettingService>();

        // Gate: onboarding must be completed before anything is accessible.
        var onboardingCompleted = await settingService.GetSetting(SettingKeys.Authentication.OnboardingCompleted);
        if (string.IsNullOrEmpty(onboardingCompleted) || onboardingCompleted != "true")
        {
            context.Fail(new AuthorizationFailureReason(this, "Onboarding required."));
            return;
        }

        // Authentication can be deliberately disabled — hubs are open then.
        var authEnabled = await settingService.GetSetting(SettingKeys.Authentication.AuthEnabled);
        if (authEnabled == "false")
        {
            context.Succeed(requirement);
            return;
        }

        // Cookie-authenticated browser clients.
        if (httpContext.User.Identity?.IsAuthenticated ?? false)
        {
            context.Succeed(requirement);
            return;
        }

        // API-key clients (e.g. non-browser transports that can send headers).
        if (httpContext.Request.Headers.TryGetValue("X-Api-Key", out var apiKeyHeader))
        {
            var apiKey = apiKeyHeader.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                var authService = httpContext.RequestServices.GetRequiredService<IAuthService>();
                if (await authService.ValidateApiKey(apiKey))
                {
                    context.Succeed(requirement);
                    return;
                }
            }
        }

        context.Fail(new AuthorizationFailureReason(this, "Authentication required."));
    }
}
