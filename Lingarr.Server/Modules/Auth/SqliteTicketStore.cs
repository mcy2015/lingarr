using System.Security.Claims;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Lingarr.Server.Modules.Auth;

/// <summary>
/// <see cref="ITicketStore"/> implementation backed by the SQLite
/// auth_sessions table (P0-2 ticket revocation).
///
/// Wiring: CookieAuthenticationOptions.SessionStore is pointed at this store,
/// so the Lingarr.Auth cookie only ever carries an opaque key while the
/// serialized ticket lives in the database. The cookie handler consults
/// RetrieveAsync on every authentication and RemoveAsync on sign-out, which
/// is what makes a logged-out (or password-changed) cookie replay fail with
/// 401 immediately.
///
/// Lifetime: cookie authentication options are cached as a singleton, so this
/// store must be a singleton too; it reaches the scoped
/// <see cref="LingarrDbContext"/> through a fresh scope per operation.
/// </summary>
public class SqliteTicketStore : IAuthSessionStore
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SqliteTicketStore> _logger;

    public SqliteTicketStore(
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        ILogger<SqliteTicketStore> logger)
    {
        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<string> StoreAsync(AuthenticationTicket ticket)
    {
        var key = Guid.NewGuid().ToString("N");
        var now = _timeProvider.GetUtcNow();

        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LingarrDbContext>();
        context.AuthSessions.Add(new AuthSession
        {
            Key = key,
            Value = TicketSerializer.Default.Serialize(ticket),
            UserId = GetUserId(ticket),
            ExpiresAt = ticket.Properties.ExpiresUtc ?? now.AddDays(7)
        });
        await context.SaveChangesAsync();

        return key;
    }

    /// <inheritdoc />
    public async Task RenewAsync(string key, AuthenticationTicket ticket)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LingarrDbContext>();
        var session = await context.AuthSessions.FirstOrDefaultAsync(s => s.Key == key);
        if (session is null)
        {
            return;
        }

        session.Value = TicketSerializer.Default.Serialize(ticket);
        session.UserId = GetUserId(ticket);
        session.ExpiresAt = ticket.Properties.ExpiresUtc ?? _timeProvider.GetUtcNow().AddDays(7);
        await context.SaveChangesAsync();
    }

    /// <inheritdoc />
    public async Task<AuthenticationTicket?> RetrieveAsync(string key)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LingarrDbContext>();
        var session = await context.AuthSessions.FirstOrDefaultAsync(s => s.Key == key);
        if (session is null)
        {
            return null;
        }

        if (session.ExpiresAt <= _timeProvider.GetUtcNow())
        {
            context.AuthSessions.Remove(session);
            await context.SaveChangesAsync();
            return null;
        }

        try
        {
            return TicketSerializer.Default.Deserialize(session.Value);
        }
        catch (Exception ex)
        {
            // Corrupt payload: drop the row so the browser is simply asked to
            // log in again instead of failing on every request.
            _logger.LogWarning(ex, "Dropping unreadable auth session {Key}.", key);
            context.AuthSessions.Remove(session);
            await context.SaveChangesAsync();
            return null;
        }
    }

    /// <inheritdoc />
    public async Task RemoveAsync(string key)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LingarrDbContext>();
        await context.AuthSessions.Where(s => s.Key == key).ExecuteDeleteAsync();
    }

    /// <inheritdoc />
    public async Task RemoveAllForUserAsync(int userId)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LingarrDbContext>();
        var removed = await context.AuthSessions.Where(s => s.UserId == userId).ExecuteDeleteAsync();
        if (removed > 0)
        {
            _logger.LogInformation("Revoked {Count} auth session(s) of user {UserId}.", removed, userId);
        }
    }

    /// <inheritdoc />
    public async Task RemoveExpiredAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LingarrDbContext>();
        await context.AuthSessions
            .Where(s => s.ExpiresAt <= _timeProvider.GetUtcNow())
            .ExecuteDeleteAsync();
    }

    private static int? GetUserId(AuthenticationTicket ticket)
    {
        var claim = ticket.Principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return int.TryParse(claim, out var id) ? id : null;
    }
}
