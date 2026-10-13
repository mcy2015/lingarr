using Lingarr.Server.Modules.Subtitle;

namespace Lingarr.Server.Modules.Subtitle;

/// <summary>
/// Subtitle parsing, encoding and conversion.
/// </summary>
public static class SubtitleModule
{
    public static void AddSubtitleModule(this IServiceCollection services)
    {
        services.AddScoped<ISubtitleService, SubtitleService>();
        services.AddScoped<ISubtitleImportService, SubtitleImportService>();
        services.AddSingleton<ISubtitleEncodingService, SubtitleEncodingService>();
        services.AddScoped<ISubtitleConversionService, SubtitleConversionService>();

        // Note: SrtParser/SsaParser/SrtWriter/SsaWriter are instantiated directly
        // by SubtitleService per file extension — no DI consumers exist, so no
        // (conflicting) registrations are needed.
    }
}
