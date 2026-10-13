using Lingarr.Server.Modules.Settings;

namespace Lingarr.Server.Modules.Settings;

/// <summary>
/// Settings persistence, change notification and the settings UI helpers.
/// </summary>
public static class SettingsModule
{
    public static void AddSettingsModule(this IServiceCollection services)
    {
        services.AddScoped<ISettingService, SettingService>();
        services.AddSingleton<ISettingChangeNotifier, SettingChangeNotifier>();
        services.AddSingleton<SettingChangedListener>();

        // Filesystem browser backing the settings UI path picker (DirectoryModal).
        services.AddScoped<IDirectoryService, DirectoryService>();
    }
}
