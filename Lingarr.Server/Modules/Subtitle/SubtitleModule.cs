using Lingarr.Server.Modules.Subtitle;
using Lingarr.Server.Modules.Subtitle.PostProcessing;

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
        services.AddScoped<ISubtitleNormalizeService, SubtitleNormalizeService>();
        services.AddSingleton<ISubtitleEncodingService, SubtitleEncodingService>();
        services.AddScoped<ISubtitleConversionService, SubtitleConversionService>();

        // Post-processing (B01): stages are registered as ISubtitlePostProcessor
        // in execution order — the composite runs them in DI registration order.
        // The variant stage resolves the singleton encoding service (legacy
        // GBK/Big5 files on disk must parse) and stays stateless itself, so
        // scoped is safe.
        services.AddScoped<ISubtitlePostProcessor, ChineseVariantStage>();
        services.AddScoped<CompositePostProcessor>();
        services.AddScoped<ISubtitleVariantBatchService, SubtitleVariantBatchService>();

        // Note: SrtParser/SsaParser/SrtWriter/SsaWriter are instantiated directly
        // by SubtitleService per file extension — no DI consumers exist, so no
        // (conflicting) registrations are needed.
    }
}
