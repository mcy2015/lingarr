using Hangfire;
using Lingarr.Core.Configuration;
using Lingarr.Core.Data;
using Lingarr.Server.Hubs;
using Lingarr.Server.Modules.Translation;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Lingarr.Server.Modules.Sync;

namespace Lingarr.Server.Modules.Settings;

public class SettingChangedListener
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IHubContext<SettingUpdatesHub> _hubContext;
    private readonly ILogger<SettingChangedListener> _logger;
    private static readonly HashSet<string> BatchServiceTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "openai", "anthropic", "localai", "gemini", "mistral", "xai"
    };

    public SettingChangedListener(IServiceProvider serviceProvider,
        IHubContext<SettingUpdatesHub> hubContext,
        ILogger<SettingChangedListener> logger,
        ISettingChangeNotifier changeNotifier)
    {
        _serviceProvider = serviceProvider;
        _hubContext = hubContext;
        _logger = logger;

        // Singleton-to-singleton subscription, wired exactly once — replaces the
        // per-scope subscriptions SettingService used to make in its constructor.
        changeNotifier.Changed += OnSettingChanged;
    }

    public void OnSettingChanged(SettingService settingService, string setting)
    {
        // Fire-and-forget, but observed: an async void event handler would
        // crash the process on any unhandled exception.
        _ = HandleSettingChangedAsync(settingService, setting);
    }

    private async Task HandleSettingChangedAsync(SettingService settingService, string setting)
    {
        try
        {
            var settingGroups = new Dictionary<string, (string actionType, string actionName, string[] keys)>
            {
                {
                    "radarr", ("Job", "Radarr", [
                        SettingKeys.Integration.RadarrApiKey,
                        SettingKeys.Integration.RadarrUrl
                    ])
                },
                {
                    "sonarr", ("Job", "Sonarr", [
                        SettingKeys.Integration.SonarrApiKey,
                        SettingKeys.Integration.SonarrUrl
                    ])
                },
                {
                    "clearHash", ("Action", "ClearHash", [
                        SettingKeys.Translation.SourceLanguages
                    ])
                },
                {
                    "serviceType", ("Action", "ServiceType", [
                        SettingKeys.Translation.ServiceType
                    ])
                }
            };

            // Find and execute the appropriate action for the changed setting
            foreach (var group in settingGroups)
            {
                // Check if the changed setting belongs to this configuration group based on it's *keys*
                if (group.Value.keys.Contains(setting))
                {
                    switch (group.Value.actionType)
                    {
                        case "Job":
                            await RunJob(group.Value.actionName, group.Value.keys);
                            break;
                        case "Action":
                            await RunAction(group.Value.actionName, group.Value.keys);
                            break;
                    }

                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle setting change for {Setting}", setting);
        }
    }

    /// <summary>
    /// This method retrieves the required settings from the database. If all required settings have non-empty values,
    /// it enqueues the appropriate background job based on the <paramref name="jobName"/>:
    /// </summary>
    /// <param name="jobName">The name of the job to run.</param>
    /// <param name="requiredKeys">An array of setting keys that must have values in the database.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    private async Task RunJob(string jobName, string[] requiredKeys)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<LingarrDbContext>();
        var settingService = scope.ServiceProvider.GetRequiredService<ISettingService>();

        var settings = await dbContext.Settings
            .Where(s => requiredKeys.Contains(s.Key))
            .ToDictionaryAsync(s => s.Key, s => s.Value);

        bool allRequiredKeysHaveValues = requiredKeys.All(key =>
            settings.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value));

        if (allRequiredKeysHaveValues)
        {
            switch (jobName)
            {
                case "Radarr":
                    _logger.LogInformation(
                        $"Settings changed for {jobName}. All settings are complete, indexing media...");

                    await _hubContext.Clients.Group("SettingUpdates").SendAsync("SettingUpdate", new
                    {
                        Key = SettingKeys.Integration.RadarrSettingsCompleted,
                        Value = "true"
                    });

                    await settingService.SetSetting(SettingKeys.Integration.RadarrSettingsCompleted, "true");
                    scope.ServiceProvider.GetRequiredService<IBackgroundJobClient>()
                        .Schedule<SyncMovieJob>(job => job.Execute(), TimeSpan.FromMinutes(1));
                    break;
                case "Sonarr":
                    _logger.LogInformation(
                        $"Settings changed for {jobName}. All settings are complete, indexing media...");

                    await _hubContext.Clients.Group("SettingUpdates").SendAsync("SettingUpdate", new
                    {
                        Key = SettingKeys.Integration.SonarrSettingsCompleted,
                        Value = "true"
                    });

                    await settingService.SetSetting(SettingKeys.Integration.SonarrSettingsCompleted, "true");
                    scope.ServiceProvider.GetRequiredService<IBackgroundJobClient>()
                        .Schedule<SyncShowJob>(job => job.Execute(), TimeSpan.FromMinutes(1));
                    break;
            }
        }
    }

    /// <summary>
    /// This method retrieves the required settings from the database. If all required settings have non-empty values,
    /// it performs an action based on the <paramref name="actionName"/>:
    /// </summary>
    /// <param name="actionName">The name of the action to run.</param>
    /// <param name="requiredKeys">An array of setting keys that must have values in the database.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    private async Task RunAction(string actionName, string[] requiredKeys)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<LingarrDbContext>();
        var settingService = scope.ServiceProvider.GetRequiredService<ISettingService>();

        var settings = await dbContext.Settings
            .Where(s => requiredKeys.Contains(s.Key))
            .ToDictionaryAsync(s => s.Key, s => s.Value);

        bool allRequiredKeysHaveValues = requiredKeys.All(key =>
            settings.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value));

        if (allRequiredKeysHaveValues)
        {
            switch (actionName)
            {
                case "ClearHash":
                    dbContext.Database.ExecuteSqlRaw("UPDATE movies SET media_hash = ''");
                    dbContext.Database.ExecuteSqlRaw("UPDATE episodes SET media_hash = ''");
                    break;

                case "ServiceType":
                    var serviceTypeRaw = await settingService.GetSetting(SettingKeys.Translation.ServiceType);
                    var primary = TranslationServices.Parse(serviceTypeRaw)[0];
                    if (!BatchServiceTypes.Contains(primary))
                    {
                        await settingService.SetSetting(SettingKeys.Translation.UseBatchTranslation, "false");
                    }
                    break;
            }
        }
    }
}