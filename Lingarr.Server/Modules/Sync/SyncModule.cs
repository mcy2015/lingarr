using Lingarr.Server.Modules.Sync;

namespace Lingarr.Server.Modules.Sync;

/// <summary>
/// Library synchronization with the external integrations.
/// </summary>
public static class SyncModule
{
    public static void AddSyncModule(this IServiceCollection services)
    {
        // Add Sync services
        services.AddTransient<PathConversionService>();
        services.AddScoped<IShowSyncService, ShowSyncService>();
        services.AddScoped<IMovieSyncService, MovieSyncService>();
        services.AddScoped<IMovieSync, MovieSync>();
        services.AddScoped<IEpisodeSync, EpisodeSync>();
        services.AddScoped<ISeasonSync, SeasonSync>();
        services.AddScoped<IShowSync, ShowSync>();
        services.AddScoped<IImageSync, ImageSync>();
    }
}
