using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Server.Modules.Auth;
using Lingarr.Server.Modules.Settings;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lingarr.Server.Tests.Services;

/// <summary>
/// Encrypted-settings semantics after the security-walk fix: the bulk read
/// answers only "configured: true/false" per key (values may be decrypted
/// in-process to test for non-emptiness but never leave the service), while
/// the single-key decrypt stays available for server-internal callers and the
/// encrypted write path keeps round-tripping through real encryption.
/// </summary>
public class SettingServiceTests : IDisposable
{
    private readonly LingarrDbContext _dbContext;
    private readonly SettingService _service;
    private readonly EncryptionService _encryption;

    public SettingServiceTests()
    {
        var options = new DbContextOptionsBuilder<LingarrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbContext = new LingarrDbContext(options);

        // Ephemeral provider shared by seeding and the service: real
        // protect/unprotect semantics within each test run.
        _encryption = new EncryptionService(new EphemeralDataProtectionProvider());
        _service = new SettingService(
            _dbContext,
            NullLogger<ISettingService>.Instance,
            new MemoryCache(new MemoryCacheOptions()),
            new SettingChangeNotifier(),
            _encryption);
    }

    public void Dispose() => _dbContext.Dispose();

    private async Task SeedAsync(params Setting[] settings)
    {
        _dbContext.Settings.AddRange(settings);
        await _dbContext.SaveChangesAsync();
    }

    private string Encrypt(string value) => _encryption.Encrypt(value);

    [Fact]
    public async Task GetEncryptedSettingStates_ReportsConfiguredFlagPerKey()
    {
        await SeedAsync(
            new Setting { Key = "radarr_api_key", Value = Encrypt("x".PadLeft(32, 'x')) },
            new Setting { Key = "openai_api_key", Value = Encrypt("sk-live-token") },
            // revoke stores the empty string; empty means not configured
            new Setting { Key = "api_key", Value = string.Empty });

        var states = await _service.GetEncryptedSettingStates(
            new[] { "radarr_api_key", "openai_api_key", "api_key", "sonarr_api_key" });

        Assert.True(states["radarr_api_key"]);
        Assert.True(states["openai_api_key"]);
        Assert.False(states["api_key"]);
        // Keys without a stored row are simply absent, not false-with-a-value.
        Assert.DoesNotContain("sonarr_api_key", states.Keys);
    }

    [Fact]
    public async Task GetEncryptedSettingStates_NeverReturnsTheDecryptedValues()
    {
        const string secret = "super-secret-provider-token";
        await SeedAsync(new Setting { Key = "openai_api_key", Value = Encrypt(secret) });

        var states = await _service.GetEncryptedSettingStates(new[] { "openai_api_key" });

        // The only value a caller can observe is the boolean flag — the
        // dictionary is typed Dictionary<string, bool> so the plaintext
        // cannot ride along.
        var value = Assert.IsType<bool>(states["openai_api_key"]);
        Assert.True(value);
    }

    [Fact]
    public async Task GetEncryptedSettingStates_PlaintextLegacyRow_IsConfigured()
    {
        // Decrypt falls back to the raw value when unprotect fails (legacy
        // rows that were never encrypted) — the flag still reflects
        // "non-empty", and the raw value itself is not returned.
        await SeedAsync(new Setting { Key = "deepl_api_key", Value = "legacy-plaintext-key" });

        var states = await _service.GetEncryptedSettingStates(new[] { "deepl_api_key" });

        Assert.True(states["deepl_api_key"]);
    }

    [Fact]
    public async Task SetEncryptedSetting_StoresCiphertext_AndRoundTripsOnRead()
    {
        await SeedAsync(new Setting { Key = "radarr_api_key", Value = string.Empty });

        var saved = await _service.SetEncryptedSetting("radarr_api_key", "plain-secret-value");
        var raw = await _service.GetSetting("radarr_api_key");
        var decrypted = await _service.GetEncryptedSetting("radarr_api_key");

        Assert.True(saved);
        Assert.NotEqual("plain-secret-value", raw);
        Assert.NotEmpty(raw);
        Assert.Equal("plain-secret-value", decrypted);
    }

    [Fact]
    public async Task SetEncryptedSetting_EmptyValue_IsStoredEmptyAndNotConfigured()
    {
        await SeedAsync(new Setting { Key = "api_key", Value = Encrypt("previous-key") });

        await _service.SetEncryptedSetting("api_key", string.Empty);

        Assert.Equal(string.Empty, await _service.GetSetting("api_key"));
        var states = await _service.GetEncryptedSettingStates(new[] { "api_key" });
        Assert.False(states["api_key"]);
    }
}
