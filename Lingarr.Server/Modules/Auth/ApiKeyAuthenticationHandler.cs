using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Lingarr.Server.Modules.Auth;

/// <summary>
/// Authenticates requests carrying a valid <c>X-Api-Key</c> header. Paired with
/// the cookie scheme in the <c>LingarrAccess</c> policy so API clients and
/// browser sessions share one authorization pipeline.
/// </summary>
public class ApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "Lingarr.ApiKey";
    public const string HeaderName = "X-Api-Key";

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
        if (!Request.Headers.TryGetValue(HeaderName, out var values))
        {
            return AuthenticateResult.NoResult();
        }

        var apiKey = values.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(apiKey) || !await _authService.ValidateApiKey(apiKey))
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
