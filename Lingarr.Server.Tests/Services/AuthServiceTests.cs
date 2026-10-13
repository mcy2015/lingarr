using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lingarr.Core.Configuration;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Server.Models;
using Microsoft.EntityFrameworkCore;
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

    private sealed class StubSettingService : ISettingService
    {
        public Dictionary<string, string> Encrypted { get; } = new();

        public event SettingChangedHandler? SettingChanged
        {
            add { }
            remove { }
        }

        public Task<string?> GetSetting(string key) => Task.FromResult<string?>(null);

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

        public Task<System.Collections.Generic.Dictionary<string, string>> GetEncryptedSettings(
            System.Collections.Generic.IEnumerable<string> keys) =>
            Task.FromResult(new System.Collections.Generic.Dictionary<string, string>());
    }
}
