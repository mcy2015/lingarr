namespace Lingarr.Server.Modules.SubtitleSearch.Abstractions;

/// <summary>
/// One search query for one media item (tech design ②). Movies fill
/// <see cref="Season"/>/<see cref="Episode"/> with null; episodes resolve
/// title/year/imdb through Season→Show (design point 1).
/// </summary>
/// <param name="Title">Movie or series title.</param>
/// <param name="Year">Release/series year, null when unknown.</param>
/// <param name="Season">Season number for episodes, null for movies.</param>
/// <param name="Episode">Episode number, null for movies.</param>
/// <param name="ReleaseName">Scene release name when known (scoring token, ③).</param>
/// <param name="VideoHash">OpenSubtitles-algorithm file hash; a hit scores 100 (short-circuit).</param>
/// <param name="Languages">Normalized target language codes.</param>
/// <param name="ImdbId">IMDb id when known ("tt…").</param>
/// <param name="ShooterFileHash">
/// shooter.cn segmented-MD5 filehash (see <c>ShooterFileHasher</c>), computed
/// lazily by the request builder exactly like the OpenSubtitles hash (⑨).
/// Optional because only the shooter source consumes it; sources that cannot
/// use it simply ignore it.
/// </param>
/// <param name="XunleiFileHash">
/// Xunlei three-segment sampled SHA1 file hash (see
/// <c>XunleiFileHasher</c>, uppercase hex), computed lazily by the request
/// builder like the other file hashes (⑨). Null when the round does not
/// query xunlei or the media file is under the algorithm's 0xF000 floor —
/// files that small cannot be hash-matched and the source answers empty.
/// </param>
/// <param name="Query">
/// Keyword override (locked API contract): when non-blank it replaces the
/// derived title/keyword term of the query. Null keeps the library-derived
/// query. Populated by the manual search endpoint's passthrough; sources
/// that cannot search by free keyword ignore it.
/// </param>
public sealed record SubtitleSearchRequest(
    string Title,
    int? Year,
    int? Season,
    int? Episode,
    string? ReleaseName,
    string? VideoHash,
    string[] Languages,
    string? ImdbId,
    string? ShooterFileHash = null,
    string? XunleiFileHash = null,
    string? Query = null);
