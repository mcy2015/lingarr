using System.ComponentModel.DataAnnotations;

namespace Lingarr.Core.Entities;

/// <summary>
/// One server-side cookie authentication ticket (P0-2 ticket revocation).
/// The browser cookie stores only an opaque key; the serialized
/// <see cref="Microsoft.AspNetCore.Authentication.AuthenticationTicket"/>
/// lives here so signing out (or a password change) can delete the row and
/// make every replay of the old cookie fail authentication immediately.
/// </summary>
public class AuthSession
{
    /// <summary>Opaque key stored in the Lingarr.Auth cookie.</summary>
    [Key]
    [MaxLength(64)]
    public required string Key { get; set; }

    /// <summary>TicketSerializer payload of the authentication ticket.</summary>
    public required byte[] Value { get; set; }

    /// <summary>
    /// User id extracted from the ticket's NameIdentifier claim. Nullable so
    /// legacy/tickets without the claim can still be stored; used to revoke
    /// every session of a user after a password change.
    /// </summary>
    public int? UserId { get; set; }

    /// <summary>Expiry of the stored ticket; expired rows are purged lazily.</summary>
    public DateTimeOffset ExpiresAt { get; set; }
}
