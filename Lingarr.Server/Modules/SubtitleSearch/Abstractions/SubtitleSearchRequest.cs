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
public sealed record SubtitleSearchRequest(
    string Title,
    int? Year,
    int? Season,
    int? Episode,
    string? ReleaseName,
    string? VideoHash,
    string[] Languages,
    string? ImdbId);
