using Lingarr.Core.Configuration;
using Lingarr.Server.Interfaces.Services;
using Microsoft.AspNetCore.Authorization;

namespace Lingarr.Server.Authorization;

/// <summary>
/// The single access requirement for all API controllers and SignalR hubs.
/// Replaces the service-locating LingarrAuthorizeAttribute with the framework
/// authorization pipeline (cookie + API-key schemes).
/// </summary>
public class LingarrAccessRequirement : IAuthorizationRequirement
{
}

public class LingarrAccessHandler : IAuthorizationHandler
{
    public async Task HandleAsync(AuthorizationHandlerContext context)
    {
        var requirement = context.Requirements.OfType<LingarrAccessRequirement>().FirstOrDefault();
        if (requirement == null)
        {
            return; // not our requirement — other handlers decide
        }

        var httpContext = context.Resource as HttpContext;
        if (httpContext == null)
        {
            context.Fail(new AuthorizationFailureReason(this, "Authentication required."));
            return;
        }

        var settingService = httpContext.RequestServices.GetRequiredService<ISettingService>();

        // Authentication can be deliberately disabled — everything is open then.
        var authEnabled = await settingService.GetSetting(SettingKeys.Authentication.AuthEnabled);
        if (authEnabled == "false")
        {
            context.Succeed(requirement);
            return;
        }

        // Cookie sessions and API-key principals (either scheme) are accepted.
        if (httpContext.User.Identity?.IsAuthenticated ?? false)
        {
            context.Succeed(requirement);
            return;
        }

        // Fallback for transports where the API-key scheme did not run.
        if (httpContext.Request.Headers.TryGetValue(ApiKeyAuthenticationHandler.HeaderName, out var apiKeyHeader))
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
