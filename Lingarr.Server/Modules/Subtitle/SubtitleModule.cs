using Lingarr.Server.Modules.Subtitle;
using Lingarr.Server.Modules.Subtitle.PostProcessing;
using Lingarr.Server.Modules.Subtitle.TimelineSync;

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

        // Timeline sync (B05): the stage runs after the variant stage — a
        // variant conversion must not shift cue times under the correlator.
        // The correlator/rewriter are stateless singletons; the extractor
        // caches its resolved ffmpeg path, the service reads settings per
        // call, so scoped wiring is exact.
        services.AddSingleton<IAudioEnvelopeExtractor, AudioEnvelopeExtractor>();
        services.AddSingleton<EnvelopeCorrelator>();
        services.AddSingleton<SubtitleTimelineRewriter>();
        services.AddScoped<ITimelineSyncService, TimelineSyncService>();
        services.AddScoped<ISubtitlePostProcessor, TimelineSyncStage>();

        services.AddScoped<CompositePostProcessor>();
        services.AddScoped<ISubtitleVariantBatchService, SubtitleVariantBatchService>();

        // B10: the manual single-file variant conversion resolves the stage's
        // concrete type — the ISubtitlePostProcessor registration above is
        // shared with the composite and cannot be resolved by class.
        services.AddScoped<ChineseVariantStage>();

        // Two-file bilingual merge (B06): reuses the scoped SubtitleService
        // above for reading/writing/naming — stateless itself, scoped wiring
        // is exact.
        services.AddScoped<IBilingualMergeService, BilingualMergeService>();

        // Note: SrtParser/SsaParser/SrtWriter/SsaWriter are instantiated directly
        // by SubtitleService per file extension — no DI consumers exist, so no
        // (conflicting) registrations are needed.
    }
}
