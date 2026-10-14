using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lingarr.Core.Configuration;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Microsoft.EntityFrameworkCore;
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

        Environment.SetEnvironmentVariable(AdminUserEnvVar, null);
        Environment.SetEnvironmentVariable(AdminPasswordEnvVar, null);
    }

    private static IServiceProvider BuildProvider()
    {
        var databaseName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddMemoryCache();
        services.AddScoped<ISettingService, StubSettingService>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddDbContext<LingarrDbContext>(options =>
            options.UseInMemoryDatabase(databaseName));

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
