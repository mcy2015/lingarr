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
using Microsoft.Extensions.Hosting;

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
    /// Per-source search timeout (design ②: 单源 10s) applied to the named
    /// search HttpClients. The orchestrator additionally imposes the same
    /// 10s budget through cancellation tokens that penetrate the HTTP calls
    /// — the client timeout is only the belt to that braces.
    /// </summary>
    private static readonly TimeSpan SourceTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Byte-download budget (QA2 P2-4): subtitle fetches ride their own
    /// named clients ("subtitle-{source}-download") because a 10s budget
    /// kills large-file downloads that the source is still legitimately
    /// serving; 120s is the ceiling, the payload cap lives in
    /// <see cref="SubtitleHttp.MaxDownloadBytes"/>.
    /// </summary>
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Buffered-body cap of the search clients (QA2 P2-5): JSON search
    /// responses are kilobytes — a 2MB ceiling stops a hostile/huge body at
    /// the client instead of in memory. Byte downloads are unaffected (they
    /// stream with ResponseHeadersRead, see <see cref="SubtitleHttp"/>).
    /// </summary>
    private static readonly long MaxJsonResponseBytes = 2L * 1024 * 1024;

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

        // Download-client wiring (QA2 P2-4): byte downloads resolve their
        // dedicated 120s clients through the static SubtitleHttp hook —
        // see SubtitleDownloadClientWiring below.
        services.AddHostedService<SubtitleDownloadClientWiring>();

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

        // Search client: 10s timeout + the 2MB buffered-body ceiling (JSON
        // search responses only — byte downloads never use this client).
        services.AddHttpClient<ISubtitleProvider, TProvider>(
            SubtitleProviderBase.HttpClientName(name),
            client =>
            {
                client.Timeout = SourceTimeout;
                client.MaxResponseContentBufferSize = MaxJsonResponseBytes;
            });

        // Dedicated download client of the same source: the long byte-fetch
        // budget. Resolved by name through SubtitleHttp.DownloadClientFactory
        // — providers hold only the search client in their typed-client ctor.
        services.AddHttpClient(
            SubtitleHttp.DownloadHttpClientName(name),
            client => client.Timeout = DownloadTimeout);
    }
}

/// <summary>
/// Assigns <see cref="SubtitleHttp.DownloadClientFactory"/> once the host
/// starts (QA2 P2-4): providers receive only the search client through
/// their typed-client constructor, and SubtitleHttp's static surface is the
/// single meeting point — so the factory is injected here, at host start,
/// and lives for the process lifetime. Byte downloads then resolve
/// "subtitle-{source}-download" (120s) while every JSON call keeps the 10s
/// search client.
/// </summary>
internal sealed class SubtitleDownloadClientWiring(IHttpClientFactory httpClientFactory)
    : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        SubtitleHttp.DownloadClientFactory = providerKey =>
            httpClientFactory.CreateClient(SubtitleHttp.DownloadHttpClientName(providerKey));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
