
namespace Lingarr.Server.Modules.Media;

/// <summary>
/// Media library access and media path mapping.
/// </summary>
public static class MediaModule
{
    public static void AddMediaModule(this IServiceCollection services)
    {
        services.AddScoped<IMediaLibraryService, MediaLibraryService>();
        services.AddScoped<IMediaService, MediaService>();
        services.AddScoped<IMediaSubtitleProcessor, MediaSubtitleProcessor>();
        services.AddScoped<IEmbeddedTrackService, EmbeddedTrackService>();
        services.AddScoped<IMappingService, MappingService>();
        services.AddScoped<AudioLanguageService>();
    }
}
