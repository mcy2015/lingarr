using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Lingarr.Core.Data;
using Lingarr.Server.Modules.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Xunit;

namespace Lingarr.Server.Tests.Authorization;

/// <summary>
/// End-to-end proof of P0-2 with the real CookieAuthenticationHandler wired
/// to the real SQLite ticket store: a signed-in cookie authenticates, and
/// once the backing session row is removed (sign-out or password change) the
/// very same cookie value replays to a failed authentication — the state the
/// middleware surface turns into 401. No cookie value is ever trusted after
/// its server-side ticket is gone.
/// </summary>
public class CookieTicketRevocationTests : IDisposable
{
    private const string CookieName = "Lingarr.Auth";

    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;
    private readonly SqliteTicketStore _store;
    private readonly IOptionsMonitor<CookieAuthenticationOptions> _optionsMonitor;
    private readonly AuthenticationScheme _scheme = new(
        CookieAuthenticationDefaults.AuthenticationScheme,
        "Cookies",
        typeof(CookieAuthenticationHandler));

    public CookieTicketRevocationTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IDataProtectionProvider, EphemeralDataProtectionProvider>();
        services.AddDbContext<LingarrDbContext>(options => options
            .UseSqlite(_connection)
            .UseSnakeCaseNamingConvention());
        services.AddSingleton<ILogger<SqliteTicketStore>>(NullLogger<SqliteTicketStore>.Instance);
        services.AddSingleton<SqliteTicketStore>();
        _provider = services.BuildServiceProvider();

        using (var scope = _provider.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<LingarrDbContext>().Database.EnsureCreated();
        }

        _store = _provider.GetRequiredService<SqliteTicketStore>();

        var options = new CookieAuthenticationOptions
        {
            Cookie = { Name = CookieName, HttpOnly = true, SecurePolicy = CookieSecurePolicy.SameAsRequest },
            // No DI post-configuration here, so options without inline
            // defaults must be set by hand.
            CookieManager = new ChunkingCookieManager(),
            ExpireTimeSpan = TimeSpan.FromDays(7),
            SlidingExpiration = false,
            SessionStore = _store,
            TicketDataFormat = new TicketDataFormat(
                _provider.GetRequiredService<IDataProtectionProvider>()
                    .CreateProtector("CookieAuthenticationHandler", _scheme.Name, "v2"))
        };
        _optionsMonitor = new StaticOptionsMonitor(options);
    }

    [Fact]
    public async Task StoredCookie_AuthenticatesUntilItsSessionIsRemoved()
    {
        var cookieValue = await SignInAsync("henry", userId: 1, DateTimeOffset.UtcNow.AddDays(7));

        // The freshly issued cookie carries a live server-side session.
        var before = await AuthenticateAsync(cookieValue);
        Assert.True(before.Succeeded);
        Assert.Equal("henry", before.Principal.Identity!.Name);

        // Sign-out equivalent: the session row behind the cookie key is
        // removed; the same cookie value must stop authenticating.
        var key = await GetOnlySessionKey();
        await _store.RemoveAsync(key);

        var afterRemoval = await AuthenticateAsync(cookieValue);
        Assert.False(afterRemoval.Succeeded);

        // Password-change equivalent: revoking every session of the user has
        // the same effect on a still-untouched cookie.
        var freshCookie = await SignInAsync("henry", userId: 1, DateTimeOffset.UtcNow.AddDays(7));
        await _store.RemoveAllForUserAsync(1);

        var afterRevocation = await AuthenticateAsync(freshCookie);
        Assert.False(afterRevocation.Succeeded);
    }

    [Fact]
    public async Task ExpiredSession_MakesCookieReplayFailAndDropsTheRow()
    {
        // The ticket itself carries a past expiry; the handler must refuse it
        // and clean the session row out of the store.
        var cookieValue = await SignInAsync("henry", userId: 1, DateTimeOffset.UtcNow.AddSeconds(-30));

        var result = await AuthenticateAsync(cookieValue);
        Assert.False(result.Succeeded);

        using (var scope = _provider.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<LingarrDbContext>();
            Assert.Equal(0, await context.AuthSessions.CountAsync());
        }
    }

    [Fact]
    public async Task SignInAsync_StoresTicketInTheSessionStore()
    {
        var expires = DateTimeOffset.UtcNow.AddDays(7);
        await SignInAsync("henry", userId: 7, expires);

        using var scope = _provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LingarrDbContext>();
        var session = await context.AuthSessions.SingleAsync();
        Assert.Equal(7, session.UserId);
        Assert.Equal(expires.UtcDateTime, session.ExpiresAt.UtcDateTime, TimeSpan.FromSeconds(1));
    }

    // ===== Helpers =====

    /// <summary>
    /// Runs the real handler's sign-in on a scratch context and returns the
    /// Lingarr.Auth cookie value it issues.
    /// </summary>
    private async Task<string> SignInAsync(string username, int userId, DateTimeOffset expiresUtc)
    {
        var handler = CreateHandler();
        var httpContext = new DefaultHttpContext();
        httpContext.RequestServices = BuildRequestServices();
        await handler.InitializeAsync(_scheme, httpContext);

        var claims = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                new Claim(ClaimTypes.Name, username)
            ],
            CookieAuthenticationDefaults.AuthenticationScheme);
        // IAuthenticationSignInHandler.SignInAsync is an explicit interface
        // implementation on the handler, so it is invoked through the
        // interface exactly like the authentication service does.
        await ((IAuthenticationSignInHandler)handler).SignInAsync(
            new ClaimsPrincipal(claims),
            new AuthenticationProperties { IsPersistent = true, ExpiresUtc = expiresUtc });

        var setCookie = httpContext.Response.Headers[HeaderNames.SetCookie].ToString();
        Assert.Contains($"{CookieName}=", setCookie);
        var value = setCookie.Split($"{CookieName}=", 2)[1].Split(';')[0];
        Assert.NotEmpty(value);
        return value;
    }

    /// <summary>
    /// Authenticates a request that carries exactly the given cookie value
    /// through the real handler.
    /// </summary>
    private async Task<AuthenticateResult> AuthenticateAsync(string cookieValue)
    {
        var handler = CreateHandler();
        var httpContext = new DefaultHttpContext();
        httpContext.RequestServices = BuildRequestServices();
        httpContext.Request.Headers[HeaderNames.Cookie] = $"{CookieName}={cookieValue}";
        await handler.InitializeAsync(_scheme, httpContext);
        return await handler.AuthenticateAsync();
    }

    /// <summary>
    /// The authentication service resolves a fresh handler instance per
    /// request; handlers keep per-request state, so each simulated request
    /// gets its own.
    /// </summary>
    private CookieAuthenticationHandler CreateHandler() => new(
        _optionsMonitor,
        NullLoggerFactory.Instance,
        System.Text.Encodings.Web.UrlEncoder.Default);

    private async Task<string> GetOnlySessionKey()
    {
        using var scope = _provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LingarrDbContext>();
        var session = await context.AuthSessions.SingleAsync();
        return session.Key;
    }

    private IServiceProvider BuildRequestServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        return services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Dispose();
    }

    private sealed class StaticOptionsMonitor(CookieAuthenticationOptions value)
        : IOptionsMonitor<CookieAuthenticationOptions>
    {
        public CookieAuthenticationOptions CurrentValue => value;

        public CookieAuthenticationOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<CookieAuthenticationOptions, string?> listener) => null;
    }
}
