using Lingarr.Core.Enum;
using Lingarr.Server.Modules.SubtitleSearch.Abstractions;
using Microsoft.Extensions.Caching.Memory;

namespace Lingarr.Server.Modules.SubtitleSearch.Providers;

/// <summary>
/// Provider-side download context store behind
/// <see cref="SubtitleCandidateRef"/> (tech design ②): when a search returns
/// candidates, each provider stores whatever it later needs to fetch the
/// file (source file id, URL, expected name…) under a UUID cache key and
/// puts that key into <see cref="SubtitleCandidate.ExternalId"/>. The
/// download call hands the key back as the ref — no candidate payload ever
/// round-trips through the client, and provider-specific request state stays
/// inside the provider.
/// <para>
/// Entries live 10 minutes (design ②). A store is deliberately tolerant of
/// unknown keys: an expired entry surfaces as a
/// <see cref="SubtitleProviderException"/> with reason NotFound so the UI
/// can send the user back to a fresh search.
/// </para>
/// <para>
/// Media ownership (QA3 P3-2): a context stored with
/// (<c>mediaType</c>, <c>mediaId</c>) can only be resolved for that media —
/// a ref generated for one library row replayed against another fails as
/// InvalidResponse instead of fetching into the wrong media. Ownership is
/// optional: entries stored without it (and legacy raw contexts) resolve
/// for any caller, so providers adopt it incrementally.
/// </para>
/// </summary>
public sealed class SubtitleCandidateCache
{
    /// <summary>Cache TTL of one candidate entry (tech design ②: 10min).</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

    private readonly IMemoryCache _cache;
    private readonly ILogger<SubtitleCandidateCache> _logger;

    public SubtitleCandidateCache(IMemoryCache cache, ILogger<SubtitleCandidateCache> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    /// <summary>
    /// Builds the external id for a candidate context and caches the context
    /// under it. Call once per candidate at search time; pass the media
    /// ownership to bind the ref to that library row.
    /// </summary>
    public string Store<TContext>(
        string provider,
        TContext context,
        MediaType? mediaType = null,
        int? mediaId = null)
        where TContext : class
    {
        var key = $"subtitle-candidate:{provider}:{Guid.NewGuid():N}";
        if (mediaType is null || mediaId is null)
        {
            _cache.Set(key, context, Ttl);
        }
        else
        {
            _cache.Set(key, new OwnedCandidate<TContext>(context, mediaType.Value, mediaId.Value), Ttl);
        }

        return key;
    }

    /// <summary>
    /// Resolves a download ref back to its context. Missing/expired entries
    /// throw <see cref="SubtitleProviderException"/> (NotFound) — the
    /// user-facing remedy is a new search, not an error screen. An owned
    /// entry resolved for a different media throws InvalidResponse
    /// (ownership binding, QA3 P3-2).
    /// </summary>
    public TContext Resolve<TContext>(
        SubtitleCandidateRef candidateRef,
        string provider,
        MediaType? mediaType = null,
        int? mediaId = null)
        where TContext : class
    {
        if (_cache.TryGetValue(candidateRef.CacheKey, out var value))
        {
            if (value is OwnedCandidate<TContext> owned)
            {
                if (mediaType is null || mediaId is null
                    || (owned.MediaType == mediaType.Value && owned.MediaId == mediaId.Value))
                {
                    return owned.Context;
                }

                _logger.LogDebug(
                    "Candidate cache key {CacheKey} of {Provider} belongs to {OwnedMediaType} {OwnedMediaId}, not {RequestedMediaType} {RequestedMediaId}.",
                    candidateRef.CacheKey, provider, owned.MediaType, owned.MediaId, mediaType, mediaId);
                throw SubtitleProviderException.ForInvalidResponse(
                    provider,
                    "the selected candidate belongs to a different media item; run a new search for this one.");
            }

            if (value is TContext unowned)
            {
                return unowned;
            }
        }

        _logger.LogDebug(
            "Candidate cache key {CacheKey} of {Provider} expired or unknown (TTL {Ttl}).",
            candidateRef.CacheKey, provider, Ttl);
        throw SubtitleProviderException.ForNotFound(
            provider,
            "the selected candidate has expired (10-minute window); run a new search.");
    }

    /// <summary>
    /// A candidate context plus the media row it was searched for — the
    /// ownership binding that keeps a replayed ref from fetching into the
    /// wrong library item.
    /// </summary>
    private sealed record OwnedCandidate<TContext>(
        TContext Context,
        MediaType MediaType,
        int MediaId)
        where TContext : class;
}
