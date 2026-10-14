using Hangfire.Dashboard;
using Microsoft.AspNetCore.Authorization;

namespace Lingarr.Server.Modules.Auth;

/// <summary>
/// Reusable LingarrAccess evaluation for surfaces that are not
/// authorization-metadata endpoints and therefore bypass
/// <c>app.UseAuthorization()</c> — the Hangfire dashboard and Swagger (P1-4).
/// Both run the real policy (and thus <see cref="LingarrAccessHandler"/>)
/// through the framework's <see cref="IAuthorizationService"/>:
/// anonymous access only while authentication is disabled, cookie sessions
/// and API-key headers otherwise.
/// </summary>
public static class LingarrAccessGuard
{
    public const string PolicyName = "LingarrAccess";

    /// <summary>
    /// Evaluates the <see cref="PolicyName"/> policy for the given request.
    /// Must be invoked after <c>UseAuthentication</c> so the cookie principal
    /// is already populated; the API-key scheme is covered by the header
    /// fallback inside <see cref="LingarrAccessHandler"/>, which runs as part
    /// of the policy.
    /// </summary>
    public static async Task<bool> IsAllowedAsync(HttpContext context)
    {
        var authorizationService =
            context.RequestServices.GetRequiredService<IAuthorizationService>();
        var result = await authorizationService.AuthorizeAsync(context.User, context, PolicyName);
        return result.Succeeded;
    }

    /// <summary>
    /// Middleware that guards the given path prefixes with the LingarrAccess
    /// policy, answering 401 with the same JSON body the API authorization
    /// failures produce. Used for the Swagger middleware pages, which expose
    /// no per-request authorization hook of their own.
    /// </summary>
    public static IApplicationBuilder UseLingarrAccessGuard(
        this IApplicationBuilder app,
        params string[] pathPrefixes)
    {
        return app.Use(async (context, next) =>
        {
            var matched = pathPrefixes.Any(prefix =>
                context.Request.Path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase));

            if (matched && !await IsAllowedAsync(context))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(
                    """{"message":"Authentication required","authenticated":false}""");
                return;
            }

            await next();
        });
    }
}

/// <summary>
/// Wires the Hangfire dashboard to the LingarrAccess policy (P1-4): the old
/// <c>Authorization = []</c> let anyone reach the dashboard. The filter runs
/// inside Hangfire's middleware, which is registered after
/// <c>UseAuthentication</c> so cookie principals exist by then. The interface
/// is synchronous; blocking on the task is safe here because ASP.NET Core has
/// no synchronization context to deadlock on.
/// </summary>
public sealed class LingarrDashboardAuthorizationFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context)
    {
        return LingarrAccessGuard.IsAllowedAsync(context.GetHttpContext())
            .GetAwaiter()
            .GetResult();
    }
}
