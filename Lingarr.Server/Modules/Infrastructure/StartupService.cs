using Lingarr.Core.Configuration;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Server.Modules.SubtitleSearch;
using Lingarr.Server.Modules.Translation.Plugins;
using Lingarr.Server.Modules.Translation;
using Microsoft.EntityFrameworkCore;
using Lingarr.Server.Modules.Auth;

namespace Lingarr.Server.Modules.Infrastructure;

public class StartupService : IHostedService
{
    private const string AdminUserEnvVar = "LINGARR_ADMIN_USER";
    private const string AdminPasswordEnvVar = "LINGARR_ADMIN_PASSWORD";

    /// <summary>
    /// Settings rows owned by the compiled-in subtitle sources. Unlike the
    /// translation plugins, these keys have no manifest-driven seeder, and
    /// <c>SettingService.SetSetting</c> only updates rows that already exist —
    /// without these placeholder rows the settings UI could never store
    /// subtitle-source credentials (every save was rejected with 400
    /// "Setting could not be updated."). The <c>*_enabled</c> switches are
    /// deliberately not seeded: a missing row means enabled, so the rollout
    /// needs no migration. Provider names match the subtitle providers
    /// endpoint keys ("opensubtitles"…); the global proxy override belongs to
    /// the module itself and carries no provider.
    /// </summary>
    private static readonly (string Key, string? Provider)[] SubtitleSourceSettingSeeds =
    [
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
    ];

    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<StartupService> _logger;

    public StartupService(IServiceProvider serviceProvider, ILogger<StartupService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    /// <summary>
    /// Initializes the application by validating and updating integration settings for integration services.
    /// This method is part of the application startup process and ensures all required service
    /// configurations are properly set.
    /// </summary>
    /// <param name="cancellationToken">Allows for cancellation of the startup process.</param>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<LingarrDbContext>();

        await SeedDefaultUser(scope.ServiceProvider);
        await NormaliseServiceType(dbContext);
        await ApplySettingsFromEnvironment(dbContext);

        var pluginRegistry = scope.ServiceProvider.GetService<IPluginRegistry>();
        var pluginLoader = scope.ServiceProvider.GetService<PluginLoader>();
        if (pluginRegistry is not null)
        {
            await SyncPluginSettings(dbContext, pluginRegistry, pluginLoader?.LoadingEnabled ?? false);
        }

        await SeedSubtitleSourceSettings(dbContext);
        await SeedAudioIdentificationSettings(dbContext);
        await SeedSubtitlePostProcessingSettings(dbContext);
        await SeedPlexSettings(dbContext);

        await CheckAndUpdateIntegrationSettings(dbContext, "radarr", [
            SettingKeys.Integration.RadarrUrl,
            SettingKeys.Integration.RadarrApiKey
        ]);

        await CheckAndUpdateIntegrationSettings(dbContext, "sonarr", [
            SettingKeys.Integration.SonarrUrl,
            SettingKeys.Integration.SonarrApiKey
        ]);
    }

    /// <summary>
    /// Creates the single admin user at startup when the database holds no users yet.
    /// Credentials come from the LINGARR_ADMIN_USER / LINGARR_ADMIN_PASSWORD environment
    /// variables; missing values fall back to admin/admin with a warning.
    /// The must_change_password flag is raised when (and only when) the well-known
    /// default credentials were used, so a first boot forces a password change while
    /// instances provisioned through environment variables are left alone (P0-3).
    /// </summary>
    /// <param name="serviceProvider">Scoped service provider used to resolve the auth service.</param>
    private async Task SeedDefaultUser(IServiceProvider serviceProvider)
    {
        var authService = serviceProvider.GetRequiredService<IAuthService>();
        if (await authService.HasAnyUsers())
        {
            return;
        }

        var username = Environment.GetEnvironmentVariable(AdminUserEnvVar)?.Trim();
        var password = Environment.GetEnvironmentVariable(AdminPasswordEnvVar)?.Trim();
        var usedDefaultCredentials = string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password);
        if (usedDefaultCredentials)
        {
            _logger.LogWarning(
                "No users found and {UserEnv}/{PasswordEnv} are not fully set; default credentials are used. " +
                "Set the {UserEnv} and {PasswordEnv} environment variables to override the default admin/admin account.",
                AdminUserEnvVar,
                AdminPasswordEnvVar,
                AdminUserEnvVar,
                AdminPasswordEnvVar);
            username = string.IsNullOrEmpty(username) ? "admin" : username;
            password = string.IsNullOrEmpty(password) ? "admin" : password;
        }

        await authService.CreateUser(username, password);
        await authService.SetMustChangePasswordAsync(usedDefaultCredentials);
        _logger.LogInformation(
            "Seeded default user {Username} (must_change_password: {MustChangePassword}).",
            username,
            usedDefaultCredentials);
    }

    /// <summary>
    /// Synchronizes plugin-declared settings with the database, creating missing rows and
    /// removing rows for plugins that are no longer loaded.
    /// </summary>
    private async Task SyncPluginSettings(
        LingarrDbContext dbContext,
        IPluginRegistry pluginRegistry,
        bool pluginLoadingEnabled)
    {
        var pluginEntries = pluginRegistry.All
            .Where(plugin => !plugin.IsBuiltIn)
            .ToList();

        await AddDeclaredSettings(dbContext, pluginEntries);

        if (pluginLoadingEnabled)
        {
            await RemoveOrphanedSettings(dbContext, pluginEntries);
        }
    }

    private async Task AddDeclaredSettings(LingarrDbContext dbContext, IReadOnlyList<RegisteredPlugin> pluginEntries)
    {
        var declaredKeys = pluginEntries
            .SelectMany(plugin => plugin.Manifest.Settings.Select(field => field.Key))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var existingByKey = await dbContext.Settings
            .Where(setting => declaredKeys.Contains(setting.Key))
            .ToDictionaryAsync(
                setting => setting.Key,
                StringComparer.OrdinalIgnoreCase);

        foreach (var plugin in pluginEntries)
        {
            var provider = plugin.Manifest.Provider;
            foreach (var field in plugin.Manifest.Settings)
            {
                if (!existingByKey.TryGetValue(field.Key, out var existing))
                {
                    dbContext.Settings.Add(new Setting
                    {
                        Key = field.Key,
                        Value = field.Default ?? string.Empty,
                        Provider = provider
                    });
                    _logger.LogInformation("Added plugin setting {Key} for {Provider}.", field.Key, provider);
                    continue;
                }

                if (existing.Provider is null)
                {
                    _logger.LogWarning(
                        "Plugin {Provider} declares key {Key} but a built-in setting with that key already exists; skipping.",
                        provider,
                        field.Key);
                    continue;
                }

                if (!string.Equals(existing.Provider, provider, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning(
                        "Plugin {Provider} declares key {Key} but it is already owned by {Owner}; skipping.",
                        provider,
                        field.Key,
                        existing.Provider);
                }
            }
        }

        await dbContext.SaveChangesAsync();
    }

    /// <summary>
    /// <summary>
    /// Seeds the Settings rows backing the audio-identification wiring
    /// (walk-through option C): <c>SetSetting</c> only updates existing rows,
    /// so without the seed the settings card could never save its first
    /// value on an upgraded database. Rows carry no provider (orphan-safe)
    /// and existing values always win. The API key row is seeded empty —
    /// encrypted values only ever leave through the reveal endpoint.
    /// </summary>
    private async Task SeedAudioIdentificationSettings(LingarrDbContext dbContext)
    {
        string[] keys =
        [
            SettingKeys.AudioIdentification.BaseUrl,
            SettingKeys.AudioIdentification.ApiKey,
            SettingKeys.AudioIdentification.Model,
            SettingKeys.AudioIdentification.Writeback
        ];

        var existingKeys = (await dbContext.Settings
                .Where(setting => keys.Contains(setting.Key))
                .Select(setting => setting.Key)
                .ToListAsync())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = keys.Where(key => !existingKeys.Contains(key)).ToList();
        if (missing.Count == 0)
        {
            return;
        }

        foreach (var key in missing)
        {
            dbContext.Settings.Add(new Setting
            {
                Key = key,
                Value = string.Empty,
                Provider = null
            });
        }

        await dbContext.SaveChangesAsync();
        _logger.LogInformation("Seeded {Count} audio-identification setting rows.", missing.Count);
    }

    /// <summary>
    /// Seeds the subtitle post-processing variant row (B01) with its "none"
    /// default: converting stored subtitle text must never run implicitly, so
    /// the row exists (settings saves need it) but the default is a no-op.
    /// Existing rows are never overwritten.
    /// </summary>
    private async Task SeedSubtitlePostProcessingSettings(LingarrDbContext dbContext)
    {
        var key = SettingKeys.SubtitleSearch.PostProcessChineseVariant;
        if (await dbContext.Settings.AnyAsync(setting => setting.Key == key))
        {
            return;
        }

        dbContext.Settings.Add(new Setting
        {
            Key = key,
            Value = "none",
            Provider = null
        });
        await dbContext.SaveChangesAsync();
        _logger.LogInformation("Seeded the subtitle post-processing variant row (none).");
    }

    /// <summary>
    /// Seeds the Plex refresh wiring rows (B03): the URL/token placeholders
    /// plus the master switch, deliberately "false" — an outbound
    /// integration never runs implicitly. The URL/token rows are seeded
    /// empty (encrypted values only ever leave through the reveal
    /// endpoint). Existing rows are never overwritten. Seeded here rather
    /// than by a migration: the batch ships no schema change, so the next
    /// migration number stays reserved.
    /// </summary>
    private async Task SeedPlexSettings(LingarrDbContext dbContext)
    {
        (string Key, string Value)[] seeds =
        [
            (SettingKeys.Integration.PlexUrl, string.Empty),
            (SettingKeys.Integration.PlexToken, string.Empty),
            (SettingKeys.Integration.PlexRefreshEnabled, "false")
        ];

        var keys = seeds.Select(seed => seed.Key).ToArray();
        var existingKeys = (await dbContext.Settings
                .Where(setting => keys.Contains(setting.Key))
                .Select(setting => setting.Key)
                .ToListAsync())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = seeds.Where(seed => !existingKeys.Contains(seed.Key)).ToList();
        if (missing.Count == 0)
        {
            return;
        }

        foreach (var (key, value) in missing)
        {
            dbContext.Settings.Add(new Setting
            {
                Key = key,
                Value = value,
                Provider = null
            });
        }

        await dbContext.SaveChangesAsync();
        _logger.LogInformation("Seeded {Count} Plex setting rows (library refresh off by default).", missing.Count);
    }

    /// Seeds empty Settings rows for every subtitle-source key that does not exist
    /// yet, so <c>SetSetting</c> can update them afterwards. Existing rows are never
    /// overwritten — stored values and their provider ownership always win. The
    /// whole batch is committed with a single SaveChanges.
    /// </summary>
    /// <param name="dbContext">The database context used to access settings.</param>
    private async Task SeedSubtitleSourceSettings(LingarrDbContext dbContext)
    {
        var keys = SubtitleSourceSettingSeeds
            .Select(seed => seed.Key)
            .ToArray();

        var existingKeys = (await dbContext.Settings
                .Where(setting => keys.Contains(setting.Key))
                .Select(setting => setting.Key)
                .ToListAsync())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = SubtitleSourceSettingSeeds
            .Where(seed => !existingKeys.Contains(seed.Key))
            .ToList();

        if (missing.Count == 0)
        {
            return;
        }

        foreach (var (key, provider) in missing)
        {
            dbContext.Settings.Add(new Setting
            {
                Key = key,
                Value = string.Empty,
                Provider = provider
            });
        }

        await dbContext.SaveChangesAsync();
        _logger.LogInformation("Seeded {Count} subtitle-source setting rows.", missing.Count);
    }

    private async Task RemoveOrphanedSettings(LingarrDbContext dbContext, IReadOnlyList<RegisteredPlugin> pluginEntries)
    {
        var loadedProviders = new HashSet<string>(
            pluginEntries.Select(plugin => plugin.Manifest.Provider),
            StringComparer.OrdinalIgnoreCase);

        var ownedRows = await dbContext.Settings
            .Where(setting => setting.Provider != null)
            .ToListAsync();

        // Rows owned by the compiled-in subtitle sources are not translation
        // plugin settings — unloading translation plugins must never remove them.
        var subtitleSourceKeys = SubtitleSourceSettingSeeds
            .Select(seed => seed.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var orphaned = ownedRows
            .Where(setting => !subtitleSourceKeys.Contains(setting.Key))
            .Where(setting => !loadedProviders.Contains(setting.Provider!))
            .ToList();

        if (orphaned.Count == 0)
        {
            return;
        }

        dbContext.Settings.RemoveRange(orphaned);
        await dbContext.SaveChangesAsync();

        foreach (var setting in orphaned)
        {
            _logger.LogInformation(
                "Removed orphaned setting {Key} owned by {Owner} (plugin not loaded).",
                setting.Key,
                setting.Provider);
        }
    }

    /// <summary>
    /// Ensures the service_type setting is stored as a JSON array. The setting historically held a
    /// single service identifier; on every startup we promote legacy single-value rows (existing
    /// installs and fresh seeds alike) to the ordered-list form used by the rest of the system.
    /// </summary>
    /// <param name="dbContext">The database context used to access and update settings.</param>
    private async Task NormaliseServiceType(LingarrDbContext dbContext)
    {
        var setting = await dbContext.Settings
            .FirstOrDefaultAsync(s => s.Key == SettingKeys.Translation.ServiceType);

        if (setting == null || string.IsNullOrEmpty(setting.Value))
        {
            return;
        }

        if (setting.Value.TrimStart().StartsWith('['))
        {
            return;
        }

        setting.Value = TranslationServices.Normalise(setting.Value, _logger);
        await dbContext.SaveChangesAsync();

        _logger.LogInformation("Normalised service_type setting to JSON-array.");
    }

    /// <summary>
    /// Validates and updates completion status for integration settings of a specific service.
    /// </summary>
    /// <param name="dbContext">The database context for accessing settings.</param>
    /// <param name="serviceName">Name of the service being validated (e.g., "radarr", "sonarr").</param>
    /// <param name="requiredKeys">Array of setting keys that must be present and non-empty for the service.</param>
    private async Task CheckAndUpdateIntegrationSettings(LingarrDbContext dbContext, string serviceName, string[] requiredKeys)
    {
        var completedKey = serviceName == "radarr"
            ? SettingKeys.Integration.RadarrSettingsCompleted
            : SettingKeys.Integration.SonarrSettingsCompleted;

        var settings = await dbContext.Settings
            .Where(s => requiredKeys.Contains(s.Key))
            .ToDictionaryAsync(s => s.Key, s => s.Value);

        var allRequiredKeysHaveValues = requiredKeys.All(key =>
            settings.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value));

        if (allRequiredKeysHaveValues)
        {
            var setting = await dbContext.Settings.FindAsync(completedKey);
            if (setting != null)
            {
                setting.Value = "true";
                await dbContext.SaveChangesAsync();
                _logger.LogInformation($"{serviceName} settings completed.");
            }
        }
    }

    /// <summary>
    /// Applies settings from environment variables to the database.
    /// </summary>
    /// <param name="dbContext">The database context used to access and update settings.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    private async Task ApplySettingsFromEnvironment(LingarrDbContext dbContext)
    {
        var environmentSettings = new Dictionary<string, string>
        {
            { "RADARR_URL", SettingKeys.Integration.RadarrUrl },
            { "RADARR_API_KEY", SettingKeys.Integration.RadarrApiKey },
            { "SONARR_URL", SettingKeys.Integration.SonarrUrl },
            { "SONARR_API_KEY", SettingKeys.Integration.SonarrApiKey },
            { "SOURCE_LANGUAGES", SettingKeys.Translation.SourceLanguages },
            { "TARGET_LANGUAGES", SettingKeys.Translation.TargetLanguages },

            { "SERVICE_TYPE", SettingKeys.Translation.ServiceType },
            { "USE_BATCH_TRANSLATION", SettingKeys.Translation.UseBatchTranslation },
            { "LIBRE_TRANSLATE_URL", SettingKeys.Translation.LibreTranslate.Url },
            { "LIBRE_TRANSLATE_API_KEY", SettingKeys.Translation.LibreTranslate.ApiKey },
            { "AI_PROMPT", SettingKeys.Translation.AiPrompt },
            { "AI_USER_PROMPT", SettingKeys.Translation.AiUserPrompt },
            { "PROOFREAD_PROMPT", SettingKeys.Translation.ProofreadPrompt },
            { "PROOFREAD_USER_PROMPT", SettingKeys.Translation.ProofreadUserPrompt },

            { "OPENAI_MODEL", SettingKeys.Translation.OpenAi.Model },
            { "OPENAI_API_KEY", SettingKeys.Translation.OpenAi.ApiKey },

            { "ANTHROPIC_MODEL", SettingKeys.Translation.Anthropic.Model },
            { "ANTHROPIC_API_KEY", SettingKeys.Translation.Anthropic.ApiKey },
            { "ANTHROPIC_VERSION", SettingKeys.Translation.Anthropic.Version },

            { "LOCAL_AI_MODEL", SettingKeys.Translation.LocalAi.Model },
            { "LOCAL_AI_API_KEY", SettingKeys.Translation.LocalAi.ApiKey },
            { "LOCAL_AI_ENDPOINT", SettingKeys.Translation.LocalAi.Endpoint },

            { "GEMINI_MODEL", SettingKeys.Translation.Gemini.Model },
            { "GEMINI_API_KEY", SettingKeys.Translation.Gemini.ApiKey },

            { "DEEPSEEK_MODEL", SettingKeys.Translation.DeepSeek.Model },
            { "DEEPSEEK_API_KEY", SettingKeys.Translation.DeepSeek.ApiKey },

            { "MISTRAL_MODEL", SettingKeys.Translation.Mistral.Model },
            { "MISTRAL_API_KEY", SettingKeys.Translation.Mistral.ApiKey },

            { "XAI_MODEL", SettingKeys.Translation.XAi.Model },
            { "XAI_API_KEY", SettingKeys.Translation.XAi.ApiKey },

            { "DEEPL_API_KEY", SettingKeys.Translation.DeepL.DeeplApiKey },

            { "AUTH_ENABLED", SettingKeys.Authentication.AuthEnabled }
        };

        foreach (var (envVar, settingKey) in environmentSettings)
        {
            var value = ReadFromFileEnv(envVar + "_FILE") ?? Environment.GetEnvironmentVariable(envVar);
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            if (settingKey == SettingKeys.Translation.ServiceType)
            {
                value = TranslationServices.Normalise(value, _logger);
            }

            var setting = await dbContext.Settings.FirstOrDefaultAsync(s => s.Key == settingKey);

            if (settingKey == SettingKeys.Authentication.AuthEnabled)
            {
                // AUTH_ENABLED only seeds the initial value for databases that
                // have no stored setting yet (fresh installs). Existing
                // databases keep whatever the UI last chose — an environment
                // variable must never silently re-enable or disable auth on an
                // already-configured instance.
                if (setting != null)
                {
                    continue;
                }

                dbContext.Settings.Add(new Setting { Key = settingKey, Value = value });
                await dbContext.SaveChangesAsync();
                _logger.LogInformation(
                    $"Seeded setting '{settingKey}' from environment variable '{envVar}' (no stored value present).");
                continue;
            }

            if (setting != null)
            {
                setting.Value = value;
                await dbContext.SaveChangesAsync();
            }

            _logger.LogInformation($"Updated setting '{settingKey}' from environment variable '{envVar}'.");
        }
    }

    private static string? ReadFromFileEnv(string fileEnvVar)
    {
        var path = Environment.GetEnvironmentVariable(fileEnvVar);
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        try
        {
            return File.ReadAllText(path).TrimEnd('\r', '\n');
        }
        catch
        {
            return null;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
