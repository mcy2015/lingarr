using Lingarr.Server.Modules.Integration;

namespace Lingarr.Server.Modules.Integration;

/// <summary>
/// External service integrations (Radarr, Sonarr) and their settings access.
/// </summary>
public static class IntegrationModule
{
    public static void AddIntegrationModule(this IServiceCollection services)
    {
        services.AddScoped<IIntegrationSettingsProvider, IntegrationSettingsProvider>();
        services.AddScoped<IIntegrationService, IntegrationService>();
        services.AddScoped<IRadarrService, RadarrService>();
        services.AddScoped<ISonarrService, SonarrService>();

        // Client for the external Lingarr release-feed API (update checks).
        services.AddScoped<ILingarrApiService, LingarrApiService>();

        // Streams poster artwork proxied from the Radarr/Sonarr APIs.
        services.AddScoped<IImageService, ImageService>();
    }
}
