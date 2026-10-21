using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lingarr.Core.Configuration;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Server.Modules.SubtitleSearch;
using Lingarr.Server.Modules.Translation.Plugins;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Lingarr.Server.Modules.Auth;
using Lingarr.Server.Modules.Infrastructure;
using Lingarr.Server.Modules.Settings;

namespace Lingarr.Server.Tests.Services;

[Collection("StartupServiceEnv")]
public class StartupServiceTests : IDisposable
{
    private const string AdminUserEnvVar = "LINGARR_ADMIN_USER";
    private const string AdminPasswordEnvVar = "LINGARR_ADMIN_PASSWORD";

    private static readonly (string EnvVar, string SettingKey)[] EnvMap =
    {
        ("RADARR_URL", SettingKeys.Integration.RadarrUrl),
        ("RADARR_API_KEY", SettingKeys.Integration.RadarrApiKey),
        ("SONARR_URL", SettingKeys.Integration.SonarrUrl),
        ("SONARR_API_KEY", SettingKeys.Integration.SonarrApiKey),
        ("SOURCE_LANGUAGES", SettingKeys.Translation.SourceLanguages),
        ("TARGET_LANGUAGES", SettingKeys.Translation.TargetLanguages),
        ("USE_BATCH_TRANSLATION", SettingKeys.Translation.UseBatchTranslation),
        ("LIBRE_TRANSLATE_URL", SettingKeys.Translation.LibreTranslate.Url),
        ("LIBRE_TRANSLATE_API_KEY", SettingKeys.Translation.LibreTranslate.ApiKey),
        ("AI_PROMPT", SettingKeys.Translation.AiPrompt),
        ("OPENAI_MODEL", SettingKeys.Translation.OpenAi.Model),
        ("OPENAI_API_KEY", SettingKeys.Translation.OpenAi.ApiKey),
        ("ANTHROPIC_MODEL", SettingKeys.Translation.Anthropic.Model),
        ("ANTHROPIC_API_KEY", SettingKeys.Translation.Anthropic.ApiKey),
        ("ANTHROPIC_VERSION", SettingKeys.Translation.Anthropic.Version),
        ("LOCAL_AI_MODEL", SettingKeys.Translation.LocalAi.Model),
        ("LOCAL_AI_API_KEY", SettingKeys.Translation.LocalAi.ApiKey),
        ("LOCAL_AI_ENDPOINT", SettingKeys.Translation.LocalAi.Endpoint),
        ("GEMINI_MODEL", SettingKeys.Translation.Gemini.Model),
        ("GEMINI_API_KEY", SettingKeys.Translation.Gemini.ApiKey),
        ("DEEPSEEK_MODEL", SettingKeys.Translation.DeepSeek.Model),
        ("DEEPSEEK_API_KEY", SettingKeys.Translation.DeepSeek.ApiKey),
        ("MISTRAL_MODEL", SettingKeys.Translation.Mistral.Model),
        ("MISTRAL_API_KEY", SettingKeys.Translation.Mistral.ApiKey),
        ("XAI_MODEL", SettingKeys.Translation.XAi.Model),
        ("XAI_API_KEY", SettingKeys.Translation.XAi.ApiKey),
        ("DEEPL_API_KEY", SettingKeys.Translation.DeepL.DeeplApiKey),
        ("AUTH_ENABLED", SettingKeys.Authentication.AuthEnabled)
    };

    /// <summary>
    /// AUTH_ENABLED only seeds fresh databases and never overwrites a stored
    /// value, so it is excluded from the generic overwrite theory and covered
    /// by its own tests below.
    /// </summary>
    private static readonly (string EnvVar, string SettingKey)[] OverwritableEnvMap =
        EnvMap.Where(entry => entry.SettingKey != SettingKeys.Authentication.AuthEnabled).ToArray();

    private readonly string _tempDir;

    public StartupServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lingarr-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        ClearEnv();
    }

    public void Dispose()
    {
        ClearEnv();
        try
        {
            Directory.Delete(_tempDir, true);
        }
        catch
        {
            // ignore
        }
    }

    public static IEnumerable<object[]> EnvMapData() =>
        OverwritableEnvMap.Select(entry => new object[] { entry.EnvVar, entry.SettingKey });

    [Theory]
    [MemberData(nameof(EnvMapData))]
    public async Task ApplySettingsFromEnvironment_FileVariableSet_PopulatesSetting(
        string envVar,
        string settingKey)
    {
        // Arrange
        var path = Path.Combine(_tempDir, envVar);
        await File.WriteAllTextAsync(path, "from-file-value");
        Environment.SetEnvironmentVariable(envVar + "_FILE", path);

        var serviceProvider = BuildProvider();
        var startupService = new StartupService(serviceProvider, NullLogger<StartupService>.Instance);

        // Act
        await startupService.StartAsync(CancellationToken.None);

        // Assert
        Assert.Equal("from-file-value", await GetSettingValue(serviceProvider, settingKey));
    }

    [Fact]
    public async Task ApplySettingsFromEnvironment_ServiceType_NormalisesToJsonArray()
    {
        // Arrange
        var path = Path.Combine(_tempDir, "SERVICE_TYPE");
        await File.WriteAllTextAsync(path, "openai");
        Environment.SetEnvironmentVariable("SERVICE_TYPE_FILE", path);

        var serviceProvider = BuildProvider();
        await using (var seedScope = serviceProvider.CreateAsyncScope())
        {
            var seedDb = seedScope.ServiceProvider.GetRequiredService<LingarrDbContext>();
            seedDb.Settings.Add(new Setting { Key = SettingKeys.Translation.ServiceType, Value = "" });
            await seedDb.SaveChangesAsync();
        }
        var startupService = new StartupService(serviceProvider, NullLogger<StartupService>.Instance);

        // Act
        await startupService.StartAsync(CancellationToken.None);

        // Assert: a legacy single-value env var is wrapped into a one-element JSON array.
        Assert.Equal("[\"openai\"]", await GetSettingValue(serviceProvider, SettingKeys.Translation.ServiceType));
    }

    [Fact]
    public async Task ApplySettingsFromEnvironment_BothVariablesSet_PrefersFile()
    {
        // Arrange
        var settingKey = SettingKeys.Translation.OpenAi.ApiKey;
        var path = Path.Combine(_tempDir, "openai_api_key");
        await File.WriteAllTextAsync(path, "from-file");
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", "from-env");
        Environment.SetEnvironmentVariable("OPENAI_API_KEY_FILE", path);

        var serviceProvider = BuildProvider();
        var startupService = new StartupService(serviceProvider, NullLogger<StartupService>.Instance);

        // Act
        await startupService.StartAsync(CancellationToken.None);

        // Assert
        Assert.Equal("from-file", await GetSettingValue(serviceProvider, settingKey));
    }

    [Theory]
    [InlineData("value\n", "value")]
    [InlineData("value\r\n", "value")]
    [InlineData("value", "value")]
    [InlineData("value\n\n", "value")]
    public async Task ApplySettingsFromEnvironment_FileHasTrailingNewlines_StripsThem(
        string fileContent,
        string expectedValue)
    {
        // Arrange
        var settingKey = SettingKeys.Translation.OpenAi.ApiKey;
        var path = Path.Combine(_tempDir, "openai_api_key_" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(path, fileContent);
        Environment.SetEnvironmentVariable("OPENAI_API_KEY_FILE", path);

        var serviceProvider = BuildProvider();
        var startupService = new StartupService(serviceProvider, NullLogger<StartupService>.Instance);

        // Act
        await startupService.StartAsync(CancellationToken.None);

        // Assert
        Assert.Equal(expectedValue, await GetSettingValue(serviceProvider, settingKey));
    }

    [Fact]
    public async Task ApplySettingsFromEnvironment_FileMissing_FallsBackToDirectVariable()
    {
        // Arrange
        var settingKey = SettingKeys.Translation.OpenAi.ApiKey;
        Environment.SetEnvironmentVariable("OPENAI_API_KEY_FILE", Path.Combine(_tempDir, "does-not-exist"));
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", "fallback-env-value");

        var serviceProvider = BuildProvider();
        var startupService = new StartupService(serviceProvider, NullLogger<StartupService>.Instance);

        // Act
        await startupService.StartAsync(CancellationToken.None);

        // Assert
        Assert.Equal("fallback-env-value", await GetSettingValue(serviceProvider, settingKey));
    }

    // ===== AUTH_ENABLED: first-boot seeding only =====

    [Fact]
    public async Task ApplySettingsFromEnvironment_AuthEnabled_RowExists_KeepsStoredValue()
    {
        // Arrange: an existing install that deliberately disabled auth in the UI.
        Environment.SetEnvironmentVariable("AUTH_ENABLED", "true");
        var serviceProvider = BuildProvider();
        await using (var seedScope = serviceProvider.CreateAsyncScope())
        {
            var seedDb = seedScope.ServiceProvider.GetRequiredService<LingarrDbContext>();
            (await seedDb.Settings.FindAsync(SettingKeys.Authentication.AuthEnabled))!.Value = "false";
            await seedDb.SaveChangesAsync();
        }
        var startupService = new StartupService(serviceProvider, NullLogger<StartupService>.Instance);

        // Act
        await startupService.StartAsync(CancellationToken.None);

        // Assert: the stored value wins; the env var never re-enables auth.
        Assert.Equal("false", await GetSettingValue(serviceProvider, SettingKeys.Authentication.AuthEnabled));
    }

    [Fact]
    public async Task ApplySettingsFromEnvironment_AuthEnabled_RowMissing_SeedsFromEnvironment()
    {
        // Arrange: a database without the auth_enabled row at all.
        Environment.SetEnvironmentVariable("AUTH_ENABLED", "false");
        var serviceProvider = BuildProvider();
        await using (var seedScope = serviceProvider.CreateAsyncScope())
        {
            var seedDb = seedScope.ServiceProvider.GetRequiredService<LingarrDbContext>();
            var row = await seedDb.Settings.FindAsync(SettingKeys.Authentication.AuthEnabled);
            seedDb.Settings.Remove(row!);
            await seedDb.SaveChangesAsync();
        }
        var startupService = new StartupService(serviceProvider, NullLogger<StartupService>.Instance);

        // Act
        await startupService.StartAsync(CancellationToken.None);

        // Assert: the missing row is seeded from the environment.
        Assert.Equal("false", await GetSettingValue(serviceProvider, SettingKeys.Authentication.AuthEnabled));
    }

    [Fact]
    public async Task ApplySettingsFromEnvironment_AuthEnabled_FileVariable_RowMissing_SeedsFromFileValue()
    {
        // Arrange: same, through the docker-secret _FILE indirection.
        var path = Path.Combine(_tempDir, "auth_enabled");
        await File.WriteAllTextAsync(path, "false");
        Environment.SetEnvironmentVariable("AUTH_ENABLED_FILE", path);
        var serviceProvider = BuildProvider();
        await using (var seedScope = serviceProvider.CreateAsyncScope())
        {
            var seedDb = seedScope.ServiceProvider.GetRequiredService<LingarrDbContext>();
            var row = await seedDb.Settings.FindAsync(SettingKeys.Authentication.AuthEnabled);
            seedDb.Settings.Remove(row!);
            await seedDb.SaveChangesAsync();
        }
        var startupService = new StartupService(serviceProvider, NullLogger<StartupService>.Instance);

        // Act
        await startupService.StartAsync(CancellationToken.None);

        // Assert
        Assert.Equal("false", await GetSettingValue(serviceProvider, SettingKeys.Authentication.AuthEnabled));
    }

    [Fact]
    public async Task ApplySettingsFromEnvironment_AuthEnabled_Unset_RowMissing_LeavesNoRow()
    {
        // Arrange: no env var, no row — nothing is invented at startup.
        var serviceProvider = BuildProvider();
        await using (var seedScope = serviceProvider.CreateAsyncScope())
        {
            var seedDb = seedScope.ServiceProvider.GetRequiredService<LingarrDbContext>();
            var row = await seedDb.Settings.FindAsync(SettingKeys.Authentication.AuthEnabled);
            seedDb.Settings.Remove(row!);
            await seedDb.SaveChangesAsync();
        }
        var startupService = new StartupService(serviceProvider, NullLogger<StartupService>.Instance);

        // Act
        await startupService.StartAsync(CancellationToken.None);

        // Assert
        Assert.Null(await GetSettingValue(serviceProvider, SettingKeys.Authentication.AuthEnabled));
    }

    // ===== Subtitle-source setting seeding (credential-save 400 fix) =====

    /// <summary>
    /// Expected (key, provider) pairs of the subtitle-source seed. Deliberately
    /// written out independently from the StartupService table so a typo or a
    /// missing entry there fails here instead of silently re-breaking saves.
    /// </summary>
    private static readonly (string Key, string? Provider)[] ExpectedSubtitleSourceSeeds =
    {
        (SubtitleSourceSettingKeys.GlobalProxyUrl, null),
        (SubtitleSourceSettingKeys.OpenSubtitles.ApiKey, "opensubtitles"),
        (SubtitleSourceSettingKeys.OpenSubtitles.BaseUrl, "opensubtitles"),
        (SubtitleSourceSettingKeys.OpenSubtitles.ProxyUrl, "opensubtitles"),
        (SubtitleSourceSettingKeys.SubSource.ApiKey, "subsource"),
        (SubtitleSourceSettingKeys.SubSource.BaseUrl, "subsource"),
        (SubtitleSourceSettingKeys.SubSource.ProxyUrl, "subsource"),
        (SubtitleSourceSettingKeys.Subdl.ApiKey, "subdl"),
        (SubtitleSourceSettingKeys.Subdl.BaseUrl, "subdl"),
        (SubtitleSourceSettingKeys.Subdl.ProxyUrl, "subdl"),
        (SubtitleSourceSettingKeys.Gestdown.BaseUrl, "gestdown"),
        (SubtitleSourceSettingKeys.Gestdown.ProxyUrl, "gestdown"),
        (SubtitleSourceSettingKeys.Xunlei.BaseUrl, "xunlei"),
        (SubtitleSourceSettingKeys.Xunlei.ProxyUrl, "xunlei"),
        (SubtitleSourceSettingKeys.Assrt.Token, "assrt"),
        (SubtitleSourceSettingKeys.Assrt.BaseUrl, "assrt"),
        (SubtitleSourceSettingKeys.Assrt.ProxyUrl, "assrt"),
        (SubtitleSourceSettingKeys.Shooter.BaseUrl, "shooter"),
        (SubtitleSourceSettingKeys.Shooter.ProxyUrl, "shooter")
    };

    private static readonly string[] SubtitleSourceNames =
    {
        "opensubtitles", "subsource", "subdl", "gestdown", "xunlei", "assrt", "shooter"
    };

    public static IEnumerable<object[]> SubtitleSourceSeedData() =>
        ExpectedSubtitleSourceSeeds.Select(seed => new object[] { seed.Key, seed.Provider! });

    [Theory]
    [MemberData(nameof(SubtitleSourceSeedData))]
    public async Task SeedSubtitleSourceSettings_FreshDatabase_CreatesEmptyRowOwnedBySource(
        string key,
        string? provider)
    {
        // Arrange
        var serviceProvider = BuildProvider();
        var startupService = new StartupService(serviceProvider, NullLogger<StartupService>.Instance);

        // Act
        await startupService.StartAsync(CancellationToken.None);

        // Assert: every seeded row exists, holds the empty value and is owned
        // by its source (provider key identical to the providers endpoint).
        await using var scope = serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<LingarrDbContext>();
        var row = await dbContext.Settings.FindAsync(key);
        Assert.NotNull(row);
        Assert.Equal(string.Empty, row.Value);
        Assert.Equal(provider, row.Provider);
    }

    [Fact]
    public async Task SeedSubtitleSourceSettings_FreshDatabase_DoesNotSeedEnableSwitches()
    {
        // Arrange: a missing enable switch means "enabled" — seeding one would
        // change source availability semantics for no migration benefit.
        var serviceProvider = BuildProvider();
        var startupService = new StartupService(serviceProvider, NullLogger<StartupService>.Instance);

        // Act
        await startupService.StartAsync(CancellationToken.None);

        // Assert
        await using var scope = serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<LingarrDbContext>();
        foreach (var source in SubtitleSourceNames)
        {
            Assert.Null(await dbContext.Settings.FindAsync(SubtitleSourceSettingKeys.EnabledKey(source)));
        }
    }

    [Fact]
    public async Task SeedSubtitleSourceSettings_ExistingRowValue_IsNotOverwritten()
    {
        // Arrange: an install that already stored a credential keeps it —
        // the seed only ever adds missing rows.
        var serviceProvider = BuildProvider();
        await using (var seedScope = serviceProvider.CreateAsyncScope())
        {
            var seedDb = seedScope.ServiceProvider.GetRequiredService<LingarrDbContext>();
            seedDb.Settings.Add(new Setting
            {
                Key = SubtitleSourceSettingKeys.OpenSubtitles.ApiKey,
                Value = "cipher",
                Provider = null
            });
            await seedDb.SaveChangesAsync();
        }
        var startupService = new StartupService(serviceProvider, NullLogger<StartupService>.Instance);

        // Act
        await startupService.StartAsync(CancellationToken.None);

        // Assert: value and provider ownership are untouched.
        await using var scope = serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<LingarrDbContext>();
        var row = await dbContext.Settings.FindAsync(SubtitleSourceSettingKeys.OpenSubtitles.ApiKey);
        Assert.NotNull(row);
        Assert.Equal("cipher", row.Value);
        Assert.Null(row.Provider);
    }

    [Fact]
    public async Task SeedPlexSettings_FreshDatabase_SeedsPlaceholdersAndDisabledSwitch()
    {
        // Arrange/Act: B03 — the Plex rows must exist for the settings card
        // to save, and the refresh switch must default to "false" (an
        // outbound integration never runs implicitly).
        var serviceProvider = BuildProvider();
        var startupService = new StartupService(serviceProvider, NullLogger<StartupService>.Instance);
        await startupService.StartAsync(CancellationToken.None);

        // Assert
        await using var scope = serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<LingarrDbContext>();
        Assert.Equal(string.Empty,
            (await dbContext.Settings.FindAsync(SettingKeys.Integration.PlexUrl))!.Value);
        Assert.Equal(string.Empty,
            (await dbContext.Settings.FindAsync(SettingKeys.Integration.PlexToken))!.Value);
        var enabled = await dbContext.Settings.FindAsync(SettingKeys.Integration.PlexRefreshEnabled);
        Assert.NotNull(enabled);
        Assert.Equal("false", enabled.Value);
        Assert.Null(enabled.Provider);
    }

    [Fact]
    public async Task SeedSubtitleSearchPreferenceSettings_FreshDatabase_SeedsTheOffDefault()
    {
        // Arrange/Act: B07 — the prefer_hearing_impaired row must exist for
        // the settings card to save, and the tiebreak must default to
        // "false" (the pre-B07 candidate order is the shipped default).
        var serviceProvider = BuildProvider();
        var startupService = new StartupService(serviceProvider, NullLogger<StartupService>.Instance);
        await startupService.StartAsync(CancellationToken.None);

        // Assert
        await using var scope = serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<LingarrDbContext>();
        var preference = await dbContext.Settings.FindAsync(
            SettingKeys.SubtitleSearch.PreferHearingImpaired);
        Assert.NotNull(preference);
        Assert.Equal("false", preference.Value);
        Assert.Null(preference.Provider);
    }

    [Fact]
    public async Task StartAsync_AfterSubtitleSeed_SetEncryptedSettingSucceeds()
    {
        // Arrange: the bug's acceptance core — SetSetting only updates rows
        // that exist, so before the seed every subtitle credential save was
        // refused and the controller answered 400 "Setting could not be updated."
        var serviceProvider = BuildProvider();
        var startupService = new StartupService(serviceProvider, NullLogger<StartupService>.Instance);
        await startupService.StartAsync(CancellationToken.None);

        // Act
        await using var scope = serviceProvider.CreateAsyncScope();
        var settingService = new SettingService(
            scope.ServiceProvider.GetRequiredService<LingarrDbContext>(),
            NullLogger<ISettingService>.Instance,
            new MemoryCache(new MemoryCacheOptions()),
            new SettingChangeNotifier(),
            new EncryptionService(new EphemeralDataProtectionProvider()));
        var saved = await settingService.SetEncryptedSetting(
            SubtitleSourceSettingKeys.OpenSubtitles.ApiKey, "x");

        // Assert: the write lands and round-trips through real encryption.
        Assert.True(saved);
        Assert.Equal("x", await settingService.GetEncryptedSetting(
            SubtitleSourceSettingKeys.OpenSubtitles.ApiKey));
    }

    [Fact]
    public async Task SyncPluginSettings_WithPluginLoading_KeepsSubtitleRowsWhileRemovingUnknownProviders()
    {
        // Arrange: external plugin loading enabled (PLUGINS_PATH set) — the
        // orphan cleanup must remove provider-owned rows of unloaded plugins
        // but never the compiled-in subtitle-source rows it does not own.
        // The subtitle row is pre-seeded (second-boot shape) so the cleanup
        // pass actually sees it.
        var pluginsDir = Path.Combine(_tempDir, "plugins");
        Directory.CreateDirectory(pluginsDir);
        Environment.SetEnvironmentVariable(PluginLoader.PluginsPathEnvironmentVariable, pluginsDir);

        var serviceProvider = BuildProvider(withPluginLoading: true);
        await using (var seedScope = serviceProvider.CreateAsyncScope())
        {
            var seedDb = seedScope.ServiceProvider.GetRequiredService<LingarrDbContext>();
            seedDb.Settings.Add(new Setting
            {
                Key = SubtitleSourceSettingKeys.OpenSubtitles.ApiKey,
                Value = "cipher",
                Provider = "opensubtitles"
            });
            seedDb.Settings.Add(new Setting { Key = "ghost_api_key", Value = string.Empty, Provider = "ghostplugin" });
            await seedDb.SaveChangesAsync();
        }
        var startupService = new StartupService(serviceProvider, NullLogger<StartupService>.Instance);

        // Act
        await startupService.StartAsync(CancellationToken.None);

        // Assert: the ghost row proves removal ran; the subtitle row survived it
        // with its stored value intact.
        await using var scope = serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<LingarrDbContext>();
        var subtitleRow = await dbContext.Settings.FindAsync(SubtitleSourceSettingKeys.OpenSubtitles.ApiKey);
        Assert.NotNull(subtitleRow);
        Assert.Equal("cipher", subtitleRow.Value);
        Assert.Null(await dbContext.Settings.FindAsync("ghost_api_key"));
    }

    // ===== Default user seeding =====

    [Fact]
    public async Task SeedDefaultUser_NoUsers_CreatesUserFromEnvironment()
    {
        // Arrange
        Environment.SetEnvironmentVariable(AdminUserEnvVar, "henry");
        Environment.SetEnvironmentVariable(AdminPasswordEnvVar, "sup3r-secret-pw");

        var serviceProvider = BuildProvider();
        var startupService = new StartupService(serviceProvider, NullLogger<StartupService>.Instance);

        // Act
        await startupService.StartAsync(CancellationToken.None);

        // Assert: exactly one user, with a verifiable PBKDF2 hash.
        await using (var scope = serviceProvider.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<LingarrDbContext>();
            var user = await dbContext.Users.SingleAsync();
            Assert.Equal("henry", user.Username);
            Assert.NotEqual("sup3r-secret-pw", user.PasswordHash);

            var authService = scope.ServiceProvider.GetRequiredService<IAuthService>();
            Assert.True(authService.VerifyPassword("sup3r-secret-pw", user.PasswordHash));
        }
    }

    [Fact]
    public async Task SeedDefaultUser_NoEnvironment_FallsBackToDefaultAdmin()
    {
        // Arrange
        var serviceProvider = BuildProvider();
        var startupService = new StartupService(serviceProvider, NullLogger<StartupService>.Instance);

        // Act
        await startupService.StartAsync(CancellationToken.None);

        // Assert
        await using var scope = serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<LingarrDbContext>();
        var user = await dbContext.Users.SingleAsync();
        Assert.Equal("admin", user.Username);

        var authService = scope.ServiceProvider.GetRequiredService<IAuthService>();
        Assert.True(authService.VerifyPassword("admin", user.PasswordHash));
    }

    [Fact]
    public async Task SeedDefaultUser_UserExists_DoesNotCreateAnother()
    {
        // Arrange
        Environment.SetEnvironmentVariable(AdminUserEnvVar, "henry");
        Environment.SetEnvironmentVariable(AdminPasswordEnvVar, "sup3r-secret-pw");

        var serviceProvider = BuildProvider();
        await using (var seedScope = serviceProvider.CreateAsyncScope())
        {
            var seedDb = seedScope.ServiceProvider.GetRequiredService<LingarrDbContext>();
            seedDb.Users.Add(new User { Username = "existing", PasswordHash = "x", CreatedAt = DateTimeOffset.UtcNow });
            await seedDb.SaveChangesAsync();
        }
        var startupService = new StartupService(serviceProvider, NullLogger<StartupService>.Instance);

        // Act
        await startupService.StartAsync(CancellationToken.None);

        // Assert: the existing user is left untouched.
        await using var scope = serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<LingarrDbContext>();
        var user = await dbContext.Users.SingleAsync();
        Assert.Equal("existing", user.Username);
    }

    // ===== P0-3: first-boot forced password change seeding =====

    [Fact]
    public async Task SeedDefaultUser_DefaultAdminCredentials_SetsMustChangePasswordTrue()
    {
        // Arrange: no LINGARR_ADMIN_* set — the well-known admin/admin default
        // is used, so the first login must force a password change.
        var serviceProvider = BuildProvider();
        var startupService = new StartupService(serviceProvider, NullLogger<StartupService>.Instance);

        // Act
        await startupService.StartAsync(CancellationToken.None);

        // Assert
        await using var scope = serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<LingarrDbContext>();
        var user = await dbContext.Users.SingleAsync();
        Assert.Equal("admin", user.Username);
        Assert.Equal("true", await GetSettingValue(
            serviceProvider, SettingKeys.Authentication.MustChangePassword));
    }

    [Fact]
    public async Task SeedDefaultUser_EnvironmentCredentials_SetsMustChangePasswordFalse()
    {
        // Arrange: credentials fully supplied through the environment — the
        // operator owns them, no forced change.
        Environment.SetEnvironmentVariable(AdminUserEnvVar, "henry");
        Environment.SetEnvironmentVariable(AdminPasswordEnvVar, "sup3r-secret-pw");

        var serviceProvider = BuildProvider();
        var startupService = new StartupService(serviceProvider, NullLogger<StartupService>.Instance);

        // Act
        await startupService.StartAsync(CancellationToken.None);

        // Assert
        Assert.Equal("false", await GetSettingValue(
            serviceProvider, SettingKeys.Authentication.MustChangePassword));
    }

    [Fact]
    public async Task SeedDefaultUser_PartialEnvironmentCredentials_FallsBackAndFlagsChange()
    {
        // Arrange: only the username is supplied; the password falls back to
        // the well-known default, so the pair is still default credentials.
        Environment.SetEnvironmentVariable(AdminUserEnvVar, "henry");

        var serviceProvider = BuildProvider();
        var startupService = new StartupService(serviceProvider, NullLogger<StartupService>.Instance);

        // Act
        await startupService.StartAsync(CancellationToken.None);

        // Assert
        Assert.Equal("true", await GetSettingValue(
            serviceProvider, SettingKeys.Authentication.MustChangePassword));
    }

    [Fact]
    public async Task SeedDefaultUser_UserExists_LeavesMustChangePasswordUntouched()
    {
        // Arrange: existing install — the flag row keeps whatever it held.
        var serviceProvider = BuildProvider();
        await using (var seedScope = serviceProvider.CreateAsyncScope())
        {
            var seedDb = seedScope.ServiceProvider.GetRequiredService<LingarrDbContext>();
            seedDb.Users.Add(new User { Username = "existing", PasswordHash = "x", CreatedAt = DateTimeOffset.UtcNow });
            seedDb.Settings.Add(new Setting { Key = SettingKeys.Authentication.MustChangePassword, Value = "false" });
            await seedDb.SaveChangesAsync();
        }
        var startupService = new StartupService(serviceProvider, NullLogger<StartupService>.Instance);

        // Act
        await startupService.StartAsync(CancellationToken.None);

        // Assert
        Assert.Equal("false", await GetSettingValue(
            serviceProvider, SettingKeys.Authentication.MustChangePassword));
    }

    private static void ClearEnv()
    {
        foreach (var (envVar, _) in EnvMap)
        {
            Environment.SetEnvironmentVariable(envVar, null);
            Environment.SetEnvironmentVariable(envVar + "_FILE", null);
        }

        Environment.SetEnvironmentVariable(PluginLoader.PluginsPathEnvironmentVariable, null);
        Environment.SetEnvironmentVariable(AdminUserEnvVar, null);
        Environment.SetEnvironmentVariable(AdminPasswordEnvVar, null);
    }

    private static IServiceProvider BuildProvider(bool withPluginLoading = false)
    {
        var databaseName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddMemoryCache();
        services.AddScoped<ISettingService, StubSettingService>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddDbContext<LingarrDbContext>(options =>
            options.UseInMemoryDatabase(databaseName));

        if (withPluginLoading)
        {
            // Mirrors TranslationModule: the loader scans PLUGINS_PATH before
            // the container is built; an existing empty directory flips
            // LoadingEnabled on without loading any plugin.
            var pluginLoader = new PluginLoader(NullLogger<PluginLoader>.Instance);
            pluginLoader.LoadPlugins(services);
            services.AddSingleton(pluginLoader);
            services.AddSingleton<IPluginRegistry, PluginRegistry>();
        }

        var serviceProvider = services.BuildServiceProvider();

        using var scope = serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<LingarrDbContext>();

        foreach (var (_, settingKey) in EnvMap)
        {
            dbContext.Settings.Add(new Setting { Key = settingKey, Value = "" });
        }

        dbContext.Settings.Add(new Setting { Key = SettingKeys.Integration.RadarrSettingsCompleted, Value = "false" });
        dbContext.Settings.Add(new Setting { Key = SettingKeys.Integration.SonarrSettingsCompleted, Value = "false" });
        dbContext.SaveChanges();

        return serviceProvider;
    }

    private static async Task<string?> GetSettingValue(IServiceProvider serviceProvider, string key)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<LingarrDbContext>();
        var setting = await dbContext.Settings.FindAsync(key);
        return setting?.Value;
    }

    /// <summary>
    /// Minimal <see cref="ISettingService"/> stand-in — the startup path only
    /// touches settings through the database context, never through this interface.
    /// </summary>
    private sealed class StubSettingService : ISettingService
    {
        public event SettingChangedHandler? SettingChanged
        {
            add { }
            remove { }
        }

        public Task<string?> GetSetting(string key) => Task.FromResult<string?>(null);

        public Task<Dictionary<string, string>> GetSettings(IEnumerable<string> keys) =>
            Task.FromResult(new Dictionary<string, string>());

        public Task<bool> SetSetting(string key, string value) => Task.FromResult(true);

        public Task<bool> SetSettings(Dictionary<string, string> settings) => Task.FromResult(true);

        public Task<List<T>> GetSettingAsJson<T>(string key) where T : class =>
            Task.FromResult(new List<T>());

        public Task<bool> SetEncryptedSetting(string key, string value) => Task.FromResult(true);

        public Task<string?> GetEncryptedSetting(string key) => Task.FromResult<string?>(null);

        public Task<Dictionary<string, bool>> GetEncryptedSettingStates(IEnumerable<string> keys) =>
            Task.FromResult(new Dictionary<string, bool>());
    }
}

[CollectionDefinition("StartupServiceEnv", DisableParallelization = true)]
public class StartupServiceEnvCollection
{
}
