namespace Lingarr.Server.Modules.SubtitleSearch.Abstractions;

/// <summary>
/// One searchable subtitle hit (tech design ②). <see cref="MatchScore"/> is
/// filled by the scorer (③), not the source; the orchestrator caches the
/// candidate under a UUID key and hands the API a <see cref="SubtitleCandidateRef"/>.
/// </summary>
/// <param name="Provider">Source key that returned the candidate.</param>
/// <param name="ExternalId">Source-side id of the subtitle entry.</param>
/// <param name="Language">Normalized language code.</param>
/// <param name="MatchScore">Decision score (hash hit = 100); 0 until scored.</param>
/// <param name="Forced">Forced/foreign-audio-only track.</param>
/// <param name="HearingImpaired">SDH track (matches the user preference).</param>
/// <param name="ReleaseInfo">Release/group info string from the source, when present.</param>
/// <param name="Uploader">Uploader name, when the source exposes one.</param>
/// <param name="Format">Container/format ("srt", "ass"…).</param>
/// <param name="Size">Payload size in bytes when known.</param>
/// <param name="HashMatch">Whether the source matched on the video hash.</param>
public sealed record SubtitleCandidate(
    string Provider,
    string ExternalId,
    string Language,
    int MatchScore,
    bool Forced,
    bool HearingImpaired,
    string? ReleaseInfo,
    string? Uploader,
    string Format,
    long? Size,
    bool HashMatch);
