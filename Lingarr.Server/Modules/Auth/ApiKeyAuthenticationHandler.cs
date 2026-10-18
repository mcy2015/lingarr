using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Lingarr.Server.Modules.Auth;

/// <summary>
/// Authenticates requests carrying a valid <c>X-Api-Key</c> header or — the
/// *arr convention — an <c>?apikey=</c> query parameter. The query form is
/// what webhook senders use: Radarr/Sonarr webhook forms have no
/// custom-header field, so their URL
/// (<c>http://lingarr/api/webhook/radarr?apikey=…</c>) is the only key
/// transport available. Paired with the cookie scheme in the
/// <c>LingarrAccess</c> policy so API clients and browser sessions share one
/// authorization pipeline.
/// </summary>
public class ApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "Lingarr.ApiKey";
    public const string HeaderName = "X-Api-Key";

    /// <summary>Query-parameter key spelling, matching the *arr APIs.</summary>
    public const string QueryName = "apikey";

    private readonly IAuthService _authService;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IAuthService authService)
        : base(options, logger, encoder)
    {
        _authService = authService;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? apiKey = null;
        if (Request.Headers.TryGetValue(HeaderName, out var values))
        {
            apiKey = values.FirstOrDefault();
        }
        else if (Request.Query.TryGetValue(QueryName, out var queryValues))
        {
            apiKey = queryValues.FirstOrDefault();
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return AuthenticateResult.NoResult();
        }

        if (!await _authService.ValidateApiKey(apiKey))
        {
            return AuthenticateResult.Fail("Invalid API key.");
        }

        var claims = new[]
        {
            new Claim(ClaimTypes.AuthenticationMethod, Scheme.Name),
            new Claim(ClaimTypes.Name, "ApiKey")
        };
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        return AuthenticateResult.Success(ticket);
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        // Mirror the historical response shape of the LingarrAuthorize filter.
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.ContentType = "application/json";
        return Response.WriteAsync(
            """{"message":"Authentication required","authenticated":false}""");
    }
}
