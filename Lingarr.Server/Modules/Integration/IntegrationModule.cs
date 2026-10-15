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

        // Full-library pulls (/api/v3/movie, /api/v3/series) legitimately run
        // for minutes on large libraries on slow NAS disks; the HttpClient
        // factory default of 100s cancels those first syncs mid-flight.
        services.AddHttpClient<IIntegrationService, IntegrationService>(client =>
        {
            client.Timeout = TimeSpan.FromMinutes(10);
        });

        services.AddScoped<IRadarrService, RadarrService>();
        services.AddScoped<ISonarrService, SonarrService>();

        // Streams poster artwork proxied from the Radarr/Sonarr APIs.
        services.AddScoped<IImageService, ImageService>();
    }
}
