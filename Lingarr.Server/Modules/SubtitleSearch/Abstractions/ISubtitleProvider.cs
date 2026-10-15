namespace Lingarr.Server.Modules.SubtitleSearch.Abstractions;

/// <summary>
/// Source ABI for every subtitle provider (tech design ②). Implementations
/// declare themselves with <see cref="SubtitleProviderAttribute"/> and are
/// discovered through DI by <see cref="SubtitleProviderRegistry"/>; each gets
/// a named HttpClient ("subtitle-{Provider}") whose handler chain applies
/// cooldown, token-bucket rate limiting and idempotent-GET retry (M2).
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
