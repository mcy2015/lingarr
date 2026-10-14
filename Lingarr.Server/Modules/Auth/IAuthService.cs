using Lingarr.Core.Entities;

namespace Lingarr.Server.Modules.Auth;

public interface IAuthService
{
    /// <summary>
    /// Generates an API key.
    /// </summary>
    string GenerateApiKey();

    /// <summary>
    /// Verifies a plaintext password against a stored password hash.
    /// </summary>
    /// <param name="password">The plaintext password to verify.</param>
    /// <param name="passwordHash">The stored password hash.</param>
    bool VerifyPassword(string password, string passwordHash);

    /// <summary>
    /// Verifies the password of a persisted user and transparently upgrades
    /// a legacy-format hash to the current parameters after a successful
    /// verification (P2-2).
    /// </summary>
    /// <param name="user">The user whose hash is verified (and possibly upgraded).</param>
    /// <param name="password">The plaintext password to verify.</param>
    Task<bool> VerifyPasswordAndUpgradeAsync(User user, string password);

    /// <summary>
    /// Retrieves a user from the database by username.
    /// </summary>
    /// <param name="username">The username to search for.</param>
    Task<User?> GetUserByUsername(string username);

    /// <summary>
    /// Retrieves a user from the database by primary key.
    /// </summary>
    Task<User?> GetUserById(int id);

    /// <summary>
    /// Creates a new user with the specified credentials.
    /// </summary>
    /// <param name="username">The username for the new user.</param>
    /// <param name="password">The plaintext password.</param>
    Task<User> CreateUser(string username, string password);

    /// <summary>
    /// Validates an API key against the stored API key.
    /// </summary>
    /// <param name="apiKey">The API key to validate.</param>
    Task<bool> ValidateApiKey(string apiKey);

    /// <summary>
    /// Check if a user exists in the database
    /// </summary>
    Task<bool> HasAnyUsers();

    /// <summary>
    /// Hashes a password
    /// </summary>
    string HashPassword(string password);

    /// <summary>
    /// Stamps the login timestamp and persists it.
    /// </summary>
    Task RecordLoginAsync(User user);

    /// <summary>
    /// Overwrites the user's password hash with a fresh PBKDF2 hash of the
    /// new password (P0-3).
    /// </summary>
    Task ChangePasswordAsync(User user, string newPassword);

    /// <summary>
    /// True when the stored must_change_password setting is "true" (the
    /// default admin/admin account has not changed its password yet).
    /// </summary>
    Task<bool> GetMustChangePasswordAsync();

    /// <summary>
    /// Upserts the must_change_password setting and invalidates its cached
    /// value so subsequent reads observe the new state immediately.
    /// </summary>
    Task SetMustChangePasswordAsync(bool value);

    /// <summary>
    /// Minimal password policy (P0-3): at least 8 characters with at least
    /// one letter and one digit.
    /// </summary>
    bool IsPasswordStrong(string password);
}
