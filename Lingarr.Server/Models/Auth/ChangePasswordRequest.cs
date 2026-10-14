namespace Lingarr.Server.Models.Auth;

/// <summary>
/// POST /api/auth/password payload (P0-3 forced/voluntary password change).
/// Error responses carry a machine-readable <c>reason</c> code the frontend
/// maps to localized copy: invalid_current_password, weak_password,
/// validation, no_session.
/// </summary>
public class ChangePasswordRequest
{
    public required string CurrentPassword { get; set; }

    public required string NewPassword { get; set; }
}
