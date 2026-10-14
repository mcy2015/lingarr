using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Lingarr.Server.Modules.Auth;

/// <summary>
/// Server-side cookie ticket storage (P0-2). Extends the standard
/// <see cref="ITicketStore"/> cookie-authentication contract with the
/// revocation operations Lingarr needs: wipe every session of a user after a
/// password change and lazily purge expired rows.
/// </summary>
public interface IAuthSessionStore : ITicketStore
{
    /// <summary>
    /// Deletes every stored session that belongs to the given user. Used by
    /// the password-change flow to revoke all of the user's tickets.
    /// </summary>
    Task RemoveAllForUserAsync(int userId);

    /// <summary>
    /// Deletes all sessions whose expiry has passed. Called opportunistically
    /// on login/logout instead of running a background timer.
    /// </summary>
    Task RemoveExpiredAsync();
}
