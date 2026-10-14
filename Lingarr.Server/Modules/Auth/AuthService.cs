using Lingarr.Core.Configuration;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Microsoft.AspNetCore.Cryptography.KeyDerivation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Lingarr.Server.Modules.Settings;

namespace Lingarr.Server.Modules.Auth;

public class AuthService : IAuthService
{
    /// <summary>
    /// PBKDF2 iteration count for newly created hashes (P2-2), following the
    /// OWASP recommendation for PBKDF2-HMAC-SHA256 (600,000).
    /// </summary>
    public const int Pbkdf2Iterations = 600_000;

    /// <summary>
    /// Iteration count of the legacy <c>salt:hash</c> format. Kept only for
    /// verification of existing hashes; successful logins transparently
    /// re-hash them at <see cref="Pbkdf2Iterations"/>.
    /// </summary>
    public const int LegacyPbkdf2Iterations = 100_000;

    /// <summary>
    /// Upper bound accepted for the iteration count embedded in a stored
    /// hash. Generous headroom over <see cref="Pbkdf2Iterations"/> while
    /// keeping a single verification bounded.
    /// </summary>
    private const int MaxEmbeddedIterations = 10_000_000;

    /// <summary>
    /// Pre-generated PBKDF2 hash of a fixed throwaway password, in the exact
    /// iterations.salt.hash format and cost (600k iterations, HMAC-SHA256) of
    /// real hashes. The login path verifies against it when the username is
    /// unknown so both failure branches burn the same PBKDF2 work and the
    /// response timing does not reveal whether the account exists (P1-3).
    /// </summary>
    public const string DummyPasswordHash =
        "600000.AAECAwQFBgcICQoLDA0ODw==.uVUP6Bfaqv24wxSvx1/MafMXbPZdNy29mT7DT4Pmlbw=";

    private readonly LingarrDbContext _context;
    private readonly ISettingService _settingService;
    private readonly IMemoryCache _memoryCache;

    public AuthService(
        LingarrDbContext context,
        ISettingService settingService,
        IMemoryCache memoryCache,
        ILogger<AuthService> logger)
    {
        _context = context;
        _settingService = settingService;
        _memoryCache = memoryCache;
    }

    public string GenerateApiKey()
    {
        var bytes = new byte[32];
        using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
        {
            rng.GetBytes(bytes);
        }
        return Convert.ToBase64String(bytes).Replace("/", "_").Replace("+", "-").Substring(0, 43);
    }

    /// <summary>
    /// Verifies a plaintext password against a stored hash in either format
    /// (P2-2): the current <c>iterations.salt.hash</c> form verifies with the
    /// embedded iteration count, the legacy <c>salt:hash</c> form with the
    /// historical 100k iterations. Callers that also own the user should
    /// prefer <see cref="VerifyPasswordAndUpgradeAsync"/> so legacy hashes are
    /// transparently re-hashed on success.
    /// </summary>
    public bool VerifyPassword(string password, string passwordHash)
    {
        try
        {
            var hashInfo = ParseStoredHash(passwordHash);
            if (hashInfo is null)
            {
                return false;
            }

            var hashToVerify = Convert.ToBase64String(KeyDerivation.Pbkdf2(
                password: password,
                salt: hashInfo.Salt,
                prf: KeyDerivationPrf.HMACSHA256,
                iterationCount: hashInfo.Iterations,
                numBytesRequested: 256 / 8));

            return FixedTimeEquals(hashToVerify, hashInfo.Hash);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Verifies the password of a persisted user and, when the stored hash is
    /// still in the legacy 100k-iteration format, transparently re-hashes it
    /// with the current parameters and saves it (P2-2). The upgrade only runs
    /// after a successful verification, so a wrong password never rewrites
    /// anything.
    /// </summary>
    public async Task<bool> VerifyPasswordAndUpgradeAsync(User user, string password)
    {
        if (!VerifyPassword(password, user.PasswordHash))
        {
            return false;
        }

        if (IsLegacyHashFormat(user.PasswordHash))
        {
            user.PasswordHash = HashPassword(password);
            await _context.SaveChangesAsync();
        }

        return true;
    }

    /// <summary>True when the hash predates the iterations.salt.hash format.</summary>
    internal static bool IsLegacyHashFormat(string passwordHash) =>
        !passwordHash.Contains('.');

    /// <summary>
    /// Parses a stored hash into salt/hash bytes and the iteration count.
    /// Supports the current <c>iterations.salt.hash</c> format and the legacy
    /// <c>salt:hash</c> format (P2-2); anything else returns null.
    /// </summary>
    private static StoredHash? ParseStoredHash(string passwordHash)
    {
        if (string.IsNullOrEmpty(passwordHash))
        {
            return null;
        }

        // Current format: iterations.salt.hash — the iteration count travels
        // with the hash so future cost bumps stay verifiable. The sanity cap
        // bounds a single verification even if the stored row ever holds a
        // pathological value (only HashPassword writes these rows normally).
        var dotParts = passwordHash.Split('.');
        if (dotParts.Length == 3)
        {
            if (!int.TryParse(dotParts[0], out var iterations)
                || iterations <= 0
                || iterations > MaxEmbeddedIterations)
            {
                return null;
            }

            return new StoredHash(
                Convert.FromBase64String(dotParts[1]),
                dotParts[2],
                iterations);
        }

        // Legacy format: salt:hash, always hashed at the historical cost.
        var colonParts = passwordHash.Split(':');
        if (colonParts.Length == 2)
        {
            return new StoredHash(
                Convert.FromBase64String(colonParts[0]),
                colonParts[1],
                LegacyPbkdf2Iterations);
        }

        return null;
    }

    private sealed record StoredHash(byte[] Salt, string Hash, int Iterations);

    public async Task<User?> GetUserByUsername(string username)
    {
        return await _context.Users.FirstOrDefaultAsync(u => u.Username == username);
    }

    /// <inheritdoc />
    public async Task<User?> GetUserById(int id)
    {
        return await _context.Users.FindAsync(id);
    }

    public async Task<User> CreateUser(string username, string password)
    {
        var user = new User
        {
            Username = username,
            PasswordHash = HashPassword(password),
            CreatedAt = DateTimeOffset.UtcNow
        };

        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        return user;
    }

    public async Task<bool> ValidateApiKey(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return false;
        }

        var storedApiKey = await _settingService.GetEncryptedSetting(SettingKeys.Authentication.ApiKey);
        if (string.IsNullOrWhiteSpace(storedApiKey))
        {
            return false;
        }

        return FixedTimeEquals(apiKey, storedApiKey);
    }

    /// <summary>
    /// Compares two strings in constant time to prevent timing attacks
    /// (e.g. against the shared API key). The length check is unavoidable
    /// but does not reveal content.
    /// </summary>
    private static bool FixedTimeEquals(string a, string b)
    {
        var bytesA = System.Text.Encoding.UTF8.GetBytes(a);
        var bytesB = System.Text.Encoding.UTF8.GetBytes(b);
        return bytesA.Length == bytesB.Length
               && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(bytesA, bytesB);
    }

    public string HashPassword(string password)
    {
        // random salt
        var salt = new byte[128 / 8];
        using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
        {
            rng.GetBytes(salt);
        }

        var hashed = Convert.ToBase64String(KeyDerivation.Pbkdf2(
            password: password,
            salt: salt,
            prf: KeyDerivationPrf.HMACSHA256,
            iterationCount: Pbkdf2Iterations,
            numBytesRequested: 256 / 8));

        return $"{Pbkdf2Iterations}.{Convert.ToBase64String(salt)}.{hashed}";
    }

    public async Task<bool> HasAnyUsers()
    {
        return await _context.Users.AnyAsync();
    }

    /// <inheritdoc />
    public async Task RecordLoginAsync(User user)
    {
        user.LastLoginAt = DateTimeOffset.UtcNow;
        await _context.SaveChangesAsync();
    }

    /// <inheritdoc />
    public async Task ChangePasswordAsync(User user, string newPassword)
    {
        user.PasswordHash = HashPassword(newPassword);
        await _context.SaveChangesAsync();
    }

    /// <inheritdoc />
    public async Task<bool> GetMustChangePasswordAsync()
    {
        return await _settingService.GetSetting(SettingKeys.Authentication.MustChangePassword) == "true";
    }

    /// <inheritdoc />
    public async Task SetMustChangePasswordAsync(bool value)
    {
        var serialized = value ? "true" : "false";
        var setting = await _context.Settings
            .FirstOrDefaultAsync(s => s.Key == SettingKeys.Authentication.MustChangePassword);

        if (setting is null)
        {
            _context.Settings.Add(new Setting
            {
                Key = SettingKeys.Authentication.MustChangePassword,
                Value = serialized
            });
        }
        else
        {
            setting.Value = serialized;
        }

        await _context.SaveChangesAsync();

        // The setting service caches reads for up to an hour; drop the entry
        // so login/authenticated responses observe the flip immediately.
        _memoryCache.Remove(SettingKeys.Authentication.MustChangePassword);
    }

    /// <inheritdoc />
    public bool IsPasswordStrong(string password)
    {
        return password.Length >= 8
               && password.Any(char.IsLetter)
               && password.Any(char.IsDigit);
    }
}
