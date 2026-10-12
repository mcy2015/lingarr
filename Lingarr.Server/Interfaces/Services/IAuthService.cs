using Lingarr.Core.Entities;

namespace Lingarr.Server.Interfaces.Services;

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
    /// Retrieves a user from the database by username.
    /// </summary>
    /// <param name="username">The username to search for.</param>
    Task<User?> GetUserByUsername(string username);

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
    /// Returns all users.
    /// </summary>
    Task<List<User>> GetAllUsers();

    /// <summary>
    /// Retrieves a user by id.
    /// </summary>
    Task<User?> GetUserById(int id);

    /// <summary>
    /// Whether the username is already taken, optionally excluding one user id.
    /// </summary>
    Task<bool> IsUsernameTaken(string username, int? excludeUserId = null);

    /// <summary>
    /// Persists changes to an existing user (username/password hash/login timestamp).
    /// </summary>
    Task SaveUserAsync(User user);

    /// <summary>
    /// Deletes a user. Throws <see cref="InvalidOperationException"/> when attempting
    /// to delete the last remaining user.
    /// </summary>
    Task DeleteUserAsync(User user);

    /// <summary>
    /// Stamps the login timestamp and persists it.
    /// </summary>
    Task RecordLoginAsync(User user);
}
