using Lingarr.Core.Configuration;
using Microsoft.AspNetCore.Authorization;
using Lingarr.Server.Modules.Settings;

namespace Lingarr.Server.Modules.Auth;

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
        // Query spelling included: webhook senders cannot set headers (the
        // *arr webhook forms have no header field — the URL is their only
        // key transport).
        var candidateKeys = new List<string>();
        if (httpContext.Request.Headers.TryGetValue(ApiKeyAuthenticationHandler.HeaderName, out var apiKeyHeader))
        {
            candidateKeys.AddRange(apiKeyHeader);
        }

        if (httpContext.Request.Query.TryGetValue(ApiKeyAuthenticationHandler.QueryName, out var apiKeyQuery))
        {
            candidateKeys.AddRange(apiKeyQuery);
        }

        var authService = httpContext.RequestServices.GetRequiredService<IAuthService>();
        foreach (var apiKey in candidateKeys)
        {
            if (!string.IsNullOrWhiteSpace(apiKey) && await authService.ValidateApiKey(apiKey))
            {
                context.Succeed(requirement);
                return;
            }
        }

        context.Fail(new AuthorizationFailureReason(this, "Authentication required."));
    }
}
