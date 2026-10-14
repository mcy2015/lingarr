using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Lingarr.Core.Data;
using Lingarr.Server.Modules.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lingarr.Server.Tests.Services;

/// <summary>
/// Service-level coverage of the SQLite-backed ticket store behind cookie
/// authentication (P0-2): store/retrieve round-trip, removal, lazy expiry
/// handling, per-user revocation and expired-row purge. The end-to-end
/// "logged-out cookie replays to 401" behaviour is proven separately with the
/// real cookie handler in CookieTicketRevocationTests.
/// </summary>
public class AuthSessionStoreTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;
    private readonly ManualTimeProvider _time = new();
    private readonly SqliteTicketStore _store;

    public AuthSessionStoreTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(_time);
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
    }

    // ===== ITicketStore contract =====

    [Fact]
    public async Task StoreAsync_ReturnsKeyAndRetrieveAsync_RoundTripsTicket()
    {
        var ticket = CreateTicket("henry", userId: 1, expires: _time.GetUtcNow().AddDays(7));

        var key = await _store.StoreAsync(ticket);
        var retrieved = await _store.RetrieveAsync(key);

        Assert.NotNull(retrieved);
        Assert.Equal("henry", retrieved.Principal.Identity!.Name);
        Assert.Equal("1", retrieved.Principal.FindFirst(ClaimTypes.NameIdentifier)!.Value);
    }

    [Fact]
    public async Task StoreAsync_PersistsExpiryFromTicketProperties()
    {
        var expires = _time.GetUtcNow().AddDays(3);
        var key = await _store.StoreAsync(CreateTicket("henry", 1, expires));

        var session = await FindSession(key);
        Assert.NotNull(session);
        Assert.Equal(expires.UtcDateTime, session!.ExpiresAt.UtcDateTime, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task StoreAsync_WithoutExpiry_FallsBackToSevenDays()
    {
        var claims = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "1"), new Claim(ClaimTypes.Name, "henry")],
            "Cookies");
        var ticket = new AuthenticationTicket(
            new ClaimsPrincipal(claims),
            new AuthenticationProperties(),
            "Cookies");

        var key = await _store.StoreAsync(ticket);

        var session = await FindSession(key);
        Assert.NotNull(session);
        Assert.Equal(_time.GetUtcNow().AddDays(7).UtcDateTime, session!.ExpiresAt.UtcDateTime, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task StoreAsync_WithoutUserIdClaim_StoresNullUserId()
    {
        var claims = new ClaimsIdentity([new Claim(ClaimTypes.Name, "anonymous-ish")], "Cookies");
        var ticket = new AuthenticationTicket(
            new ClaimsPrincipal(claims),
            new AuthenticationProperties { ExpiresUtc = _time.GetUtcNow().AddDays(1) },
            "Cookies");

        var key = await _store.StoreAsync(ticket);

        var session = await FindSession(key);
        Assert.NotNull(session);
        Assert.Null(session!.UserId);
    }

    [Fact]
    public async Task RetrieveAsync_UnknownKey_ReturnsNull()
    {
        Assert.Null(await _store.RetrieveAsync("no-such-key"));
    }

    [Fact]
    public async Task RemoveAsync_MakesTicketUnretrievableAndDeletesRow()
    {
        var key = await _store.StoreAsync(CreateTicket("henry", 1, _time.GetUtcNow().AddDays(7)));

        await _store.RemoveAsync(key);

        Assert.Null(await _store.RetrieveAsync(key));
        Assert.Null(await FindSession(key));
    }

    [Fact]
    public async Task RetrieveAsync_ExpiredRow_ReturnsNullAndDeletesRow()
    {
        var key = await _store.StoreAsync(CreateTicket("henry", 1, _time.GetUtcNow().AddDays(7)));

        _time.Advance(TimeSpan.FromDays(8));

        Assert.Null(await _store.RetrieveAsync(key));
        Assert.Null(await FindSession(key));
    }

    [Fact]
    public async Task RenewAsync_UpdatesExpiryAndPrincipal()
    {
        var key = await _store.StoreAsync(CreateTicket("henry", 1, _time.GetUtcNow().AddDays(1)));

        var renewedExpiry = _time.GetUtcNow().AddDays(7);
        await _store.RenewAsync(key, CreateTicket("henry-renamed", 2, renewedExpiry));

        var session = await FindSession(key);
        Assert.NotNull(session);
        Assert.Equal(2, session!.UserId);
        Assert.Equal(renewedExpiry.UtcDateTime, session.ExpiresAt.UtcDateTime, TimeSpan.FromSeconds(1));

        var retrieved = await _store.RetrieveAsync(key);
        Assert.NotNull(retrieved);
        Assert.Equal("henry-renamed", retrieved.Principal.Identity!.Name);
    }

    [Fact]
    public async Task RenewAsync_UnknownKey_IsANoOp()
    {
        // Must not throw — the handler renews opportunistically.
        await _store.RenewAsync("no-such-key", CreateTicket("henry", 1, _time.GetUtcNow().AddDays(7)));
    }

    // ===== Revocation and cleanup =====

    [Fact]
    public async Task RemoveAllForUserAsync_DeletesOnlyThatUsersSessions()
    {
        var henry1 = await _store.StoreAsync(CreateTicket("henry", 1, _time.GetUtcNow().AddDays(7)));
        var henry2 = await _store.StoreAsync(CreateTicket("henry", 1, _time.GetUtcNow().AddDays(7)));
        var other = await _store.StoreAsync(CreateTicket("other", 2, _time.GetUtcNow().AddDays(7)));

        await _store.RemoveAllForUserAsync(1);

        Assert.Null(await _store.RetrieveAsync(henry1));
        Assert.Null(await _store.RetrieveAsync(henry2));
        Assert.NotNull(await _store.RetrieveAsync(other));
    }

    [Fact]
    public async Task RemoveExpiredAsync_DeletesOnlyExpiredRows()
    {
        var live = await _store.StoreAsync(CreateTicket("henry", 1, _time.GetUtcNow().AddDays(7)));
        var stale = await _store.StoreAsync(CreateTicket("old", 1, _time.GetUtcNow().AddMinutes(1)));

        _time.Advance(TimeSpan.FromMinutes(5));
        await _store.RemoveExpiredAsync();

        Assert.NotNull(await _store.RetrieveAsync(live));
        Assert.Null(await FindSession(stale));
    }

    // ===== Helpers =====

    private static AuthenticationTicket CreateTicket(string username, int userId, DateTimeOffset expires)
    {
        var claims = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                new Claim(ClaimTypes.Name, username)
            ],
            "Cookies");
        return new AuthenticationTicket(
            new ClaimsPrincipal(claims),
            new AuthenticationProperties { ExpiresUtc = expires },
            "Cookies");
    }

    private async Task<Lingarr.Core.Entities.AuthSession?> FindSession(string key)
    {
        using var scope = _provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LingarrDbContext>();
        return await context.AuthSessions.FirstOrDefaultAsync(s => s.Key == key);
    }

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Dispose();
    }

    /// <summary>Deterministic clock the expiry tests advance by hand.</summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);
    }
}
