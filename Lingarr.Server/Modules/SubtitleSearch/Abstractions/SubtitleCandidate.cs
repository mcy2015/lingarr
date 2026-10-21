namespace Lingarr.Server.Modules.SubtitleSearch.Abstractions;

/// <summary>
/// One named contribution of the match score (B09 candidate detail drawer):
/// the dimension's stable key ("title", "year"…), the points it actually
/// earned (0 on a miss — the sum over all signals equals
/// <see cref="SubtitleCandidate.MatchScore"/> exactly, the wrong-episode cap
/// contributing a negative row when it bites) and whether the dimension hit.
/// A hash hit is the single signal "hash" at 100 (③ short-circuit).
/// </summary>
/// <param name="Name">Stable dimension key the front end maps to a localized label.</param>
/// <param name="Points">Points earned by this dimension (0 on a miss; negative for the cap row).</param>
/// <param name="Hit">Whether the dimension matched.</param>
public sealed record ScoreSignal(string Name, int Points, bool Hit);

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
/// <param name="Signals">
/// B09 score breakdown of <see cref="MatchScore"/> (one <see cref="ScoreSignal"/>
/// per scoring dimension; the sum equals the score). Null until the scorer
/// ran — pre-score constructions keep the legacy shape untouched.
/// </param>
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
    bool HashMatch,
    IReadOnlyList<ScoreSignal>? Signals = null);
