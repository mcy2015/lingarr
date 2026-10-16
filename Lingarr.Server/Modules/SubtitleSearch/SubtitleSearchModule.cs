using Lingarr.Server.Modules.SubtitleSearch;
using Lingarr.Server.Modules.SubtitleSearch.Abstractions;
using Lingarr.Server.Modules.SubtitleSearch.Manifests;
using Lingarr.Server.Modules.SubtitleSearch.Orchestration;
using Lingarr.Server.Modules.SubtitleSearch.Pipeline;
using Lingarr.Server.Modules.SubtitleSearch.Providers;
using Lingarr.Server.Modules.SubtitleSearch.Queue;
using Lingarr.Server.Modules.SubtitleSearch.Requests;
using Lingarr.Server.Modules.SubtitleSearch.Scoring;
using Lingarr.Server.Modules.SubtitleSearch.Telemetry;

namespace Lingarr.Server.Modules.SubtitleSearch;

/// <summary>
/// Subtitle search &amp; download (docs/subtitle-search-tech-design.zh-CN.md):
/// provider ABI, registry, orchestration, download pipeline and history.
/// The M2 batch adds the seven v1 sources (tech design ⑩), the match scorer
/// (③) and the candidate cache backing the download refs; M3/M4 add the
/// request builder, priority queue, orchestrator, automated job and the
/// download pipeline.
/// </summary>
public static class SubtitleSearchModule
{
    /// <summary>
    /// Per-source timeout (design ②: 单源 10s) applied to every named
    /// subtitle HttpClient — search and download alike. The orchestrator
    /// additionally imposes the 10s budget through cancellation tokens.
    /// </summary>
    private static readonly TimeSpan SourceTimeout = TimeSpan.FromSeconds(10);

    public static void AddSubtitleSearchModule(this IServiceCollection services)
    {
        services.AddSingleton<SubtitleMatchScorer>();
        services.AddSingleton<SubtitleCandidateCache>();
        services.AddSingleton<LazyMediaHasher>();

        // The seven v1 sources (⑩) as typed clients on their named
        // HttpClients ("subtitle-opensubtitles"…, ②). Providers are
        // transient — they resolve scoped ISettingsAccess per call — so the
        // registry is scoped to avoid a captive dependency.
        AddSource<OpenSubtitlesProvider>(services);
        AddSource<SubSourceProvider>(services);
        AddSource<SubdlProvider>(services);
        AddSource<GestdownProvider>(services);
        AddSource<PodnapisiProvider>(services);
        AddSource<AssrtProvider>(services);
        AddSource<ShooterProvider>(services);

        // Settings forms for the source cards. Registered as the dedicated
        // ISubtitleSourceManifest service type (not IPluginManifest) so the
        // translation PluginRegistry never lists the subtitle sources.
        services.AddSingleton<ISubtitleSourceManifest, OpenSubtitlesPluginManifest>();
        services.AddSingleton<ISubtitleSourceManifest, SubSourcePluginManifest>();
        services.AddSingleton<ISubtitleSourceManifest, SubdlPluginManifest>();
        services.AddSingleton<ISubtitleSourceManifest, GestdownPluginManifest>();
        services.AddSingleton<ISubtitleSourceManifest, PodnapisiPluginManifest>();
        services.AddSingleton<ISubtitleSourceManifest, AssrtPluginManifest>();
        services.AddSingleton<ISubtitleSourceManifest, ShooterPluginManifest>();

        services.AddScoped<SubtitleProviderRegistry>();

        // M3: request building (the lazy hasher's cache is singleton, the
        // builder scoped over the DbContext), the priority queue, pinning,
        // cooldown state, telemetry and the orchestrator. M4: archive
        // extraction and the download pipeline. SubtitleSearchJob itself is
        // Hangfire-activated without a DI registration (StatisticsJob
        // precedent) — the activator builds it from scoped services.
        services.AddScoped<SubtitleSourceCooldownStore>();
        services.AddScoped<SubtitleTelemetryService>();
        services.AddScoped<SubtitleSearchRequestBuilder>();
        services.AddScoped<SubtitleSearchQueueService>();
        services.AddScoped<SubtitlePinService>();
        services.AddScoped<SubtitleSearchOrchestrator>();
        services.AddSingleton<SubtitleArchiveExtractor>();
        services.AddScoped<SubtitleCoverageRefresher>();
        services.AddScoped<SubtitleDownloadPipeline>();
    }

    private static void AddSource<TProvider>(IServiceCollection services)
        where TProvider : SubtitleProviderBase, ISubtitleProvider
    {
        // The provider key is read from the attribute without instantiating
        // the type (the ctor needs runtime dependencies).
        var attribute = typeof(TProvider)
            .GetCustomAttributes(typeof(SubtitleProviderAttribute), inherit: false)
            .OfType<SubtitleProviderAttribute>()
            .FirstOrDefault();
        var name = attribute?.Name
            ?? throw new InvalidOperationException(
                $"{typeof(TProvider).Name} must declare SubtitleProviderAttribute.");

        services.AddHttpClient<ISubtitleProvider, TProvider>(
            SubtitleProviderBase.HttpClientName(name),
            client => client.Timeout = SourceTimeout);
    }
}
