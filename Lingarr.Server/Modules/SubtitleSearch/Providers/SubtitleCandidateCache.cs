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
    /// under it. Call once per candidate at search time.
    /// </summary>
    public string Store<TContext>(string provider, TContext context)
        where TContext : class
    {
        var key = $"subtitle-candidate:{provider}:{Guid.NewGuid():N}";
        _cache.Set(key, context, Ttl);
        return key;
    }

    /// <summary>
    /// Resolves a download ref back to its context. Missing/expired entries
    /// throw <see cref="SubtitleProviderException"/> (NotFound) — the
    /// user-facing remedy is a new search, not an error screen.
    /// </summary>
    public TContext Resolve<TContext>(SubtitleCandidateRef candidateRef, string provider)
        where TContext : class
    {
        if (_cache.TryGetValue(candidateRef.CacheKey, out var value) && value is TContext typed)
        {
            return typed;
        }

        _logger.LogDebug(
            "Candidate cache key {CacheKey} of {Provider} expired or unknown (TTL {Ttl}).",
            candidateRef.CacheKey, provider, Ttl);
        throw SubtitleProviderException.ForNotFound(
            provider,
            "the selected candidate has expired (10-minute window); run a new search.");
    }
}
