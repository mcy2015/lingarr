using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Lingarr.Server.Modules.Auth;

/// <summary>
/// Distinguishes interactive browser (cookie) sessions from API-key principals.
/// The <c>LingarrAccess</c> policy deliberately accepts both — and even
/// anonymous callers while authentication is disabled — so endpoints that must
/// only ever serve a logged-in human (such as API key minting) cannot rely on
/// <see cref="ClaimsPrincipal.Identity"/> alone and check the authentication
/// type through this helper instead.
/// </summary>
public static class CookieSession
{
    /// <summary>
    /// True when the principal carries an authenticated identity issued by the
    /// cookie scheme (the identity created on login and rebuilt by the cookie
    /// handler on subsequent requests). API-key principals authenticate under
    /// <see cref="ApiKeyAuthenticationHandler.SchemeName"/> and fail this check.
    /// </summary>
    public static bool IsCookieSession(this ClaimsPrincipal principal) =>
        principal.Identities.Any(identity =>
            identity.IsAuthenticated &&
            identity.AuthenticationType == CookieAuthenticationDefaults.AuthenticationScheme);
}
