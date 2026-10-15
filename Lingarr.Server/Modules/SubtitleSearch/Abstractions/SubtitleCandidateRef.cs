namespace Lingarr.Server.Modules.SubtitleSearch.Abstractions;

/// <summary>
/// Opaque reference to a cached candidate (tech design ②): the orchestrator
/// stores the candidate in an IMemoryCache entry (UUID key, 10min TTL) and
/// the download endpoint accepts only this key — candidate payloads never
/// round-trip through the client.
/// </summary>
/// <param name="CacheKey">IMemoryCache UUID of the cached candidate.</param>
public sealed record SubtitleCandidateRef(string CacheKey);
