using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lingarr.Core.Configuration;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Server.Models;
using Microsoft.AspNetCore.Cryptography.KeyDerivation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Xunit;
using Lingarr.Server.Modules.Auth;
using Lingarr.Server.Modules.Settings;

namespace Lingarr.Server.Tests.Services;

/// <summary>
/// Auth fundamentals: PBKDF2 round-trip, malformed-hash tolerance and
/// constant-time API-key validation. AuthController behaviour is covered
/// separately once user CRUD moves out of the controller.
/// </summary>
public class AuthServiceTests
{
    private static LingarrDbContext BuildContext()
    {
        var options = new DbContextOptionsBuilder<LingarrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new LingarrDbContext(options);
    }

    private static AuthService CreateService(
        LingarrDbContext? context = null,
        string? storedApiKey = null)
    {
        var settings = new StubSettingService();
        if (storedApiKey is not null)
        {
            settings.Encrypted[SettingKeys.Authentication.ApiKey] = storedApiKey;
        }
        return new AuthService(
            context ?? BuildContext(),
            settings,
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger);
    }

    private static readonly Microsoft.Extensions.Logging.Abstractions.NullLogger<AuthService> NullLogger =
        Microsoft.Extensions.Logging.Abstractions.NullLogger<AuthService>.Instance;

    // ===== Password hashing =====

    [Fact]
    public void HashPassword_And_VerifyPassword_RoundTrip()
    {
        var service = CreateService();

        var hash = service.HashPassword("correct horse battery staple");

        Assert.NotEqual("correct horse battery staple", hash);
        Assert.True(service.VerifyPassword("correct horse battery staple", hash));
    }

    [Fact]
    public void VerifyPassword_RejectsWrongPassword()
    {
        var service = CreateService();
        var hash = service.HashPassword("right password");

        Assert.False(service.VerifyPassword("wrong password", hash));
    }

    [Fact]
    public void HashPassword_UsesRandomSaltPerCall()
    {
        var service = CreateService();

        var hash1 = service.HashPassword("same input");
        var hash2 = service.HashPassword("same input");

        Assert.NotEqual(hash1, hash2);
        Assert.True(service.VerifyPassword("same input", hash1));
        Assert.True(service.VerifyPassword("same input", hash2));
    }

    [Theory]
    [InlineData("")]
    [InlineData("noseparator")]
    [InlineData("notbase64:notbase64either!!")]
    [InlineData("a:b:c")]
    public void VerifyPassword_ToleratesMalformedHashes(string malformed)
    {
        var service = CreateService();

        Assert.False(service.VerifyPassword("anything", malformed));
    }

    // ===== P2-2: 600k iterations, iterations.salt.hash format, legacy compat =====

    [Fact]
    public void HashPassword_ProducesCurrentFormatAndIterationCount()
    {
        var service = CreateService();

        var hash = service.HashPassword("a-secure-password");

        var parts = hash.Split('.');
        Assert.Equal(3, parts.Length);
        Assert.Equal(AuthService.Pbkdf2Iterations.ToString(), parts[0]);
        Assert.Equal(600_000, AuthService.Pbkdf2Iterations);
        Assert.DoesNotContain(':', hash);
    }

    /// <summary>
    /// Builds a hash in the legacy pre-P2-2 <c>salt:hash</c> format at the
    /// historical 100k iteration count — what existing databases still hold.
    /// </summary>
    private static string LegacyHash(string password)
    {
        var salt = Convert.FromBase64String("AAECAwQFBgcICQoLDA0ODw==");
        var hashed = Convert.ToBase64String(KeyDerivation.Pbkdf2(
            password: password,
            salt: salt,
            prf: KeyDerivationPrf.HMACSHA256,
            iterationCount: AuthService.LegacyPbkdf2Iterations,
            numBytesRequested: 256 / 8));
        return $"{Convert.ToBase64String(salt)}:{hashed}";
    }

    /// <summary>
    /// Builds a hash in the current <c>iterations.salt.hash</c> format at an
    /// arbitrary iteration count.
    /// </summary>
    private static string HashWithIterations(string password, int iterations)
    {
        var salt = Convert.FromBase64String("AAECAwQFBgcICQoLDA0ODw==");
        var hashed = Convert.ToBase64String(KeyDerivation.Pbkdf2(
            password: password,
            salt: salt,
            prf: KeyDerivationPrf.HMACSHA256,
            iterationCount: iterations,
            numBytesRequested: 256 / 8));
        return $"{iterations}.{Convert.ToBase64String(salt)}.{hashed}";
    }

    [Fact]
    public void VerifyPassword_AcceptsLegacyFormatHash()
    {
        var service = CreateService();

        Assert.True(service.VerifyPassword("legacy-password", LegacyHash("legacy-password")));
        Assert.False(service.VerifyPassword("wrong-password", LegacyHash("legacy-password")));
    }

    [Fact]
    public void VerifyPassword_HonoursIterationsEmbeddedInTheHash()
    {
        var service = CreateService();

        // A non-default embedded count must still verify (self-describing
        // format), not silently fall back to the current or legacy default —
        // this is what keeps future cost bumps backward compatible.
        var cheaper = HashWithIterations("cheap-password", 150_000);
        Assert.True(service.VerifyPassword("cheap-password", cheaper));
        Assert.False(service.VerifyPassword("wrong-password", cheaper));
    }

    [Theory]
    [InlineData("0.AAECAwQFBgcICQoLDA0ODw==.AAAA")]
    [InlineData("-5.AAECAwQFBgcICQoLDA0ODw==.AAAA")]
    [InlineData("notanumber.AAECAwQFBgcICQoLDA0ODw==.AAAA")]
    [InlineData("2000000000.AAECAwQFBgcICQoLDA0ODw==.AAAA")]
    [InlineData("600000.!!!!.AAAA")]
    [InlineData("600000.AAECAwQFBgcICQoLDA0ODw==")]
    [InlineData("a.b.c.d")]
    public void VerifyPassword_ToleratesMalformedNewFormatHashes(string malformed)
    {
        var service = CreateService();

        // Includes a pathological iteration count: rejected without ever
        // running the derivation, bounding a single verification.
        Assert.False(service.VerifyPassword("anything", malformed));
    }

    [Fact]
    public void VerifyPassword_DummyHashStaysNewFormatAndRejectsWrongPassword()
    {
        var service = CreateService();

        // The unknown-user timing decoy must track the current cost/format.
        Assert.StartsWith(AuthService.Pbkdf2Iterations + ".", AuthService.DummyPasswordHash);
        Assert.False(service.VerifyPassword("any-login-attempt", AuthService.DummyPasswordHash));
    }

    [Fact]
    public async Task VerifyPasswordAndUpgradeAsync_UpgradesLegacyHashTransparently()
    {
        var context = BuildContext();
        var service = CreateService(context);
        var user = new User { Username = "henry", PasswordHash = LegacyHash("OldPass123") };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        Assert.True(await service.VerifyPasswordAndUpgradeAsync(user, "OldPass123"));

        var stored = await context.Users.SingleAsync(u => u.Username == "henry");
        Assert.StartsWith(AuthService.Pbkdf2Iterations + ".", stored.PasswordHash);
        Assert.DoesNotContain(':', stored.PasswordHash);
        // The cleartext password still verifies against the upgraded hash.
        Assert.True(service.VerifyPassword("OldPass123", stored.PasswordHash));
        Assert.False(service.VerifyPassword("wrong", stored.PasswordHash));
    }

    [Fact]
    public async Task VerifyPasswordAndUpgradeAsync_LeavesCurrentFormatHashUntouched()
    {
        var context = BuildContext();
        var service = CreateService(context);
        var user = new User { Username = "henry", PasswordHash = service.HashPassword("Passw0rd") };
        context.Users.Add(user);
        await context.SaveChangesAsync();
        var hashBefore = user.PasswordHash;

        Assert.True(await service.VerifyPasswordAndUpgradeAsync(user, "Passw0rd"));

        Assert.Equal(hashBefore, user.PasswordHash);
    }

    [Fact]
    public async Task VerifyPasswordAndUpgradeAsync_WrongPasswordReturnsFalseAndKeepsHash()
    {
        var context = BuildContext();
        var service = CreateService(context);
        var user = new User { Username = "henry", PasswordHash = LegacyHash("right") };
        context.Users.Add(user);
        await context.SaveChangesAsync();
        var hashBefore = user.PasswordHash;

        Assert.False(await service.VerifyPasswordAndUpgradeAsync(user, "wrong"));

        // Failed verification must never rewrite the stored hash.
        Assert.Equal(hashBefore, (await context.Users.SingleAsync(u => u.Username == "henry")).PasswordHash);
    }

    // ===== API key =====

    [Fact]
    public async Task ValidateApiKey_RejectsMissingOrEmptyKey()
    {
        var service = CreateService(storedApiKey: "stored-key");

        Assert.False(await service.ValidateApiKey(null!));
        Assert.False(await service.ValidateApiKey(string.Empty));
        Assert.False(await service.ValidateApiKey("   "));
    }

    [Fact]
    public async Task ValidateApiKey_RejectsWhenNoKeyIsConfigured()
    {
        var service = CreateService(storedApiKey: null);

        Assert.False(await service.ValidateApiKey("anything"));
    }

    [Fact]
    public async Task ValidateApiKey_AcceptsExactMatch()
    {
        var key = new string('k', 43);
        var service = CreateService(storedApiKey: key);

        Assert.True(await service.ValidateApiKey(key));
    }

    [Fact]
    public async Task ValidateApiKey_RejectsDifferentKeyOfSameLength()
    {
        var service = CreateService(storedApiKey: new string('a', 43));

        Assert.False(await service.ValidateApiKey(new string('b', 43)));
    }

    [Fact]
    public void GenerateApiKey_IsUrlSafeAndSufficientlyLong()
    {
        var service = CreateService();

        var key = service.GenerateApiKey();

        Assert.Equal(43, key.Length);
        Assert.DoesNotContain('/', key);
        Assert.DoesNotContain('+', key);
        Assert.DoesNotContain('=', key);
    }

    // ===== User persistence =====

    [Fact]
    public async Task CreateUser_PersistsUserWithVerifiableHash()
    {
        var context = BuildContext();
        var service = CreateService(context);

        var user = await service.CreateUser("henry", "a-secure-password");

        Assert.Equal("henry", user.Username);
        var stored = await context.Users.SingleAsync(u => u.Username == "henry");
        Assert.NotEqual("a-secure-password", stored.PasswordHash);
        Assert.True(service.VerifyPassword("a-secure-password", stored.PasswordHash));
    }

    [Fact]
    public async Task GetUserByUsername_ReturnsNullForUnknownUser()
    {
        var service = CreateService();

        Assert.Null(await service.GetUserByUsername("nobody"));
    }

    [Fact]
    public async Task HasAnyUsers_ReflectsDatabaseState()
    {
        var context = BuildContext();
        var service = CreateService(context);

        Assert.False(await service.HasAnyUsers());

        context.Users.Add(new User { Username = "first", PasswordHash = "x" });
        await context.SaveChangesAsync();

        Assert.True(await service.HasAnyUsers());
    }

    // ===== P0-3: password change =====

    [Fact]
    public async Task ChangePasswordAsync_ReplacesHashWithVerifiableOne()
    {
        var context = BuildContext();
        var service = CreateService(context);
        var user = new User { Username = "henry", PasswordHash = service.HashPassword("OldPass123") };
        context.Users.Add(user);
        await context.SaveChangesAsync();
        var oldHash = user.PasswordHash;

        await service.ChangePasswordAsync(user, "NewPass123");

        var stored = await context.Users.SingleAsync(u => u.Username == "henry");
        Assert.NotEqual(oldHash, stored.PasswordHash);
        Assert.False(service.VerifyPassword("OldPass123", stored.PasswordHash));
        Assert.True(service.VerifyPassword("NewPass123", stored.PasswordHash));
    }

    [Theory]
    [InlineData("Passw0rd", true)]
    [InlineData("a1b2c3d4", true)]
    [InlineData("Passw0r", false)]      // 7 chars: too short
    [InlineData("nodigitshere", false)] // no digit
    [InlineData("12345678", false)]     // no letter
    [InlineData("", false)]
    public void IsPasswordStrong_EnforcesLengthLetterAndDigit(string password, bool expected)
    {
        var service = CreateService();

        Assert.Equal(expected, service.IsPasswordStrong(password));
    }

    // ===== P0-3: must_change_password flag =====

    [Fact]
    public async Task SetMustChangePasswordAsync_CreatesRowWhenMissing()
    {
        var context = BuildContext();
        var service = CreateService(context);

        await service.SetMustChangePasswordAsync(true);

        var row = await context.Settings.FindAsync(SettingKeys.Authentication.MustChangePassword);
        Assert.NotNull(row);
        Assert.Equal("true", row!.Value);
    }

    [Fact]
    public async Task SetMustChangePasswordAsync_UpdatesExistingRow()
    {
        var context = BuildContext();
        context.Settings.Add(new Setting { Key = SettingKeys.Authentication.MustChangePassword, Value = "true" });
        await context.SaveChangesAsync();
        var service = CreateService(context);

        await service.SetMustChangePasswordAsync(false);

        var row = await context.Settings.FindAsync(SettingKeys.Authentication.MustChangePassword);
        Assert.Equal("false", row!.Value);
    }

    [Fact]
    public async Task SetMustChangePasswordAsync_InvalidatesCachedReads()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var settings = new StubSettingService();
        settings.Plain[SettingKeys.Authentication.MustChangePassword] = "true";
        cache.Set(SettingKeys.Authentication.MustChangePassword, "true");
        var service = new AuthService(BuildContext(), settings, cache, NullLogger);

        Assert.True(await service.GetMustChangePasswordAsync()); // still cached
        await service.SetMustChangePasswordAsync(false);
        settings.Plain[SettingKeys.Authentication.MustChangePassword] = "false";

        Assert.False(await service.GetMustChangePasswordAsync()); // cache dropped, fresh read
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData(null, false)]
    public async Task GetMustChangePasswordAsync_ParsesStoredValue(string? stored, bool expected)
    {
        var settings = new StubSettingService();
        if (stored is not null)
        {
            settings.Plain[SettingKeys.Authentication.MustChangePassword] = stored;
        }
        var service = new AuthService(BuildContext(), settings, new MemoryCache(new MemoryCacheOptions()), NullLogger);

        Assert.Equal(expected, await service.GetMustChangePasswordAsync());
    }

    // ===== P0-2/P0-3: user lookup by id =====

    [Fact]
    public async Task GetUserById_ReturnsNullForUnknownId()
    {
        var service = CreateService();

        Assert.Null(await service.GetUserById(42));
    }

    [Fact]
    public async Task GetUserById_ReturnsUser()
    {
        var context = BuildContext();
        var service = CreateService(context);
        var created = await service.CreateUser("henry", "a-secure-password");

        var fetched = await service.GetUserById(created.Id);

        Assert.NotNull(fetched);
        Assert.Equal("henry", fetched!.Username);
    }

    private sealed class StubSettingService : ISettingService
    {
        public Dictionary<string, string> Encrypted { get; } = new();

        /// <summary>Plain (unencrypted) values served by GetSetting.</summary>
        public Dictionary<string, string> Plain { get; } = new();

        public event SettingChangedHandler? SettingChanged
        {
            add { }
            remove { }
        }

        public Task<string?> GetSetting(string key) =>
            Task.FromResult<string?>(Plain.GetValueOrDefault(key));

        public Task<System.Collections.Generic.Dictionary<string, string>> GetSettings(
            System.Collections.Generic.IEnumerable<string> keys) =>
            Task.FromResult(new System.Collections.Generic.Dictionary<string, string>());

        public Task<bool> SetSetting(string key, string value) => Task.FromResult(true);
        public Task<bool> SetSettings(System.Collections.Generic.Dictionary<string, string> settings) =>
            Task.FromResult(true);

        public Task<List<T>> GetSettingAsJson<T>(string key) where T : class =>
            Task.FromResult(new List<T>());

        public Task<bool> SetEncryptedSetting(string key, string value) => Task.FromResult(true);

        public Task<string?> GetEncryptedSetting(string key) =>
            Task.FromResult(Encrypted.GetValueOrDefault(key));

        public Task<System.Collections.Generic.Dictionary<string, bool>> GetEncryptedSettingStates(
            System.Collections.Generic.IEnumerable<string> keys) =>
            Task.FromResult(new System.Collections.Generic.Dictionary<string, bool>());
    }
}
