using Lingarr.Server.Modules.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;

namespace Lingarr.Server.Modules.Auth;

/// <summary>
/// Authentication, authorization and credential encryption.
/// </summary>
public static class AuthModule
{
    public static void AddAuthModule(this IServiceCollection services, IWebHostEnvironment environment)
    {
        services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        })
        .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options =>
        {
            options.Cookie.Name = "Lingarr.Auth";
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.ExpireTimeSpan = TimeSpan.FromDays(7);
            options.SlidingExpiration = true;
            options.Events.OnRedirectToLogin = context =>
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            };
        })
        .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, _ => { });

        services.AddAuthorization(options =>
        {
            options.AddPolicy("LingarrAccess", policy =>
            {
                policy.AddAuthenticationSchemes(
                    CookieAuthenticationDefaults.AuthenticationScheme,
                    ApiKeyAuthenticationHandler.SchemeName);
                policy.Requirements.Add(new LingarrAccessRequirement());
            });
        });
        services.AddSingleton<IAuthorizationHandler, LingarrAccessHandler>();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, LingarrAuthorizationResponseHandler>();

        // Login rate limiting: in-memory sliding window, single instance by design.
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ILoginRateLimiter, LoginRateLimiter>();

        // Register auth
        services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(
            Environment.GetEnvironmentVariable("ENCRYPTION_KEYS")?.ToLower()
            ?? Path.Combine(environment.ContentRootPath, "config", "keys")
            ));
        services.AddSingleton<IEncryptionService, EncryptionService>();
        services.AddScoped<IAuthService, AuthService>();

        // Server-side ticket storage (P0-2): the cookie only carries an opaque
        // key, the ticket lives in auth_sessions. Singleton because cookie
        // authentication options (and their SessionStore) are cached once.
        services.AddSingleton<IAuthSessionStore, SqliteTicketStore>();
        services
            .AddOptions<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme)
            .Configure<IAuthSessionStore>((options, store) => options.SessionStore = store);
    }
}
