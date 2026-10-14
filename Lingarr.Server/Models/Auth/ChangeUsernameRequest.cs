namespace Lingarr.Server.Models.Auth;

/// <summary>
/// PUT /api/auth/username payload. Error responses carry a machine-readable
/// <c>reason</c> code the frontend maps to localized copy: validation,
/// invalid_username, same_username, invalid_current_password,
/// username_taken, no_session.
/// </summary>
public class ChangeUsernameRequest
{
    public required string CurrentPassword { get; set; }

    public required string NewUsername { get; set; }
}
