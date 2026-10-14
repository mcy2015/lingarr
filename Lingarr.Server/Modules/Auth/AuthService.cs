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
    /// Pre-generated PBKDF2 hash of a fixed throwaway password, in the exact
    /// salt:hash format and cost (100k iterations, HMAC-SHA256) of real
    /// hashes. The login path verifies against it when the username is
    /// unknown so both failure branches burn the same PBKDF2 work and the
    /// response timing does not reveal whether the account exists (P1-3).
    /// </summary>
    public const string DummyPasswordHash =
        "AAECAwQFBgcICQoLDA0ODw==:3xUL2UmIys+lU+eoOQw8rwNOeMRUsaiasDQGsdUWVlA=";

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

    public bool VerifyPassword(string password, string passwordHash)
    {
        try
        {
            var parts = passwordHash.Split(':');
            if (parts.Length != 2)
            {
                return false;
            }

            var salt = Convert.FromBase64String(parts[0]);
            var storedHash = parts[1];

            var hashToVerify = Convert.ToBase64String(KeyDerivation.Pbkdf2(
                password: password,
                salt: salt,
                prf: KeyDerivationPrf.HMACSHA256,
                iterationCount: 100000,
                numBytesRequested: 256 / 8));

            return FixedTimeEquals(hashToVerify, storedHash);
        }
        catch
        {
            return false;
        }
    }

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
            iterationCount: 100000,
            numBytesRequested: 256 / 8));

        return $"{Convert.ToBase64String(salt)}:{hashed}";
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
