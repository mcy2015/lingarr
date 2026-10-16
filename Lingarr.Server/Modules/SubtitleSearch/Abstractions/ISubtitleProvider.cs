namespace Lingarr.Server.Modules.SubtitleSearch.Abstractions;

/// <summary>
/// Source ABI for every subtitle provider (tech design ②). Implementations
/// declare themselves with <see cref="SubtitleProviderAttribute"/> and are
/// discovered through DI by <see cref="SubtitleProviderRegistry"/>; each gets
/// a named HttpClient ("subtitle-{Provider}") for single-shot requests.
/// There is no retry/rate-limit handler chain: one request per call, with
/// failure classification (429/5xx/timeout/credentials) centralized in
/// <c>Providers.SubtitleHttp</c> so every source maps onto the same
/// <see cref="SubtitleProviderException"/> reasons and cooldown table (⑧).
/// </summary>
public interface ISubtitleProvider
{
    /// <summary>
    /// Stable lowercase source key ("opensubtitles", "assrt"…) — matches the
    /// attribute name and the settings/telemetry columns.
    /// </summary>
    string Provider { get; }

    /// <summary>
    /// Queries the source for one media item / language set. Callers impose
    /// a 10s per-source timeout via the cancellation token; implementations
    /// must pass it into every HTTP call and never swallow it.
    /// </summary>
    Task<SubtitleSearchResult> SearchAsync(SubtitleSearchRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Fetches a previously returned candidate. The ref carries only the
    /// IMemoryCache key (UUID, 10min TTL) under which the orchestrator cached
    /// the candidate — providers resolve it back to their request context,
    /// so no cross-request payload travels through the API.
    /// </summary>
    Task<SubtitleDownload> DownloadAsync(SubtitleCandidateRef candidateRef, CancellationToken cancellationToken);
}
