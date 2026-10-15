using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Models.FileSystem;

namespace Lingarr.Server.Models.Api;

/// <summary>
/// One row of the media library matrix (gap-audit A4): the media's subtitle
/// track coverage per language, its translation state and last activity,
/// aggregated from the filesystem scan and the request table. Shows carry
/// the full entity so the inline season detail can render without a second
/// fetch; movies carry their scanned subtitle files for the same reason.
/// </summary>
public class MediaLibraryItem
{
    public required int Id { get; init; }
    public required MediaType MediaType { get; init; }
    public required string Title { get; init; }
    public DateTimeOffset? DateAdded { get; init; }
    public int SeasonCount { get; init; }
    public int EpisodeCount { get; init; }
    public required List<SubtitleTrackSummary> Tracks { get; init; }
    public required MediaTranslationState State { get; init; }
    public int CompletedCount { get; init; }
    public int FailedCount { get; init; }
    public DateTimeOffset? LastActivityAt { get; init; }
    public bool IncludeInTranslation { get; init; }
    public int? TranslationAgeThreshold { get; init; }

    /// <summary>
    /// Normalized audio track languages of the media (PRD card A); null while
    /// unknown ("und" after ffprobe fallback). Shows aggregate over their
    /// episodes: null when any episode is unknown, else the distinct union.
    /// </summary>
    public string[]? AudioLanguages { get; init; }

    /// <summary>
    /// The subtitle-need rule judged this media as not needing subtitles
    /// (manual override=false, target-language audio track, or full
    /// coverage). Drives the "native" library state filter.
    /// </summary>
    public bool NeedsNoSubtitles { get; init; }

    /// <summary>Populated for shows: seasons with episodes for the inline detail.</summary>
    public Show? Show { get; init; }

    /// <summary>Populated for movies: subtitle files found by the scan.</summary>
    public List<Subtitles>? SubtitleFiles { get; init; }
}

public class SubtitleTrackSummary
{
    public required string Language { get; init; }
    public int Have { get; init; }
    public int Total { get; init; }

    /// <summary>complete (Have == Total), partial (0 &lt; Have &lt; Total) or none.</summary>
    public required string State { get; init; }
}

public enum MediaTranslationState
{
    None,
    Queued,
    InProgress,
    Completed,
    Failed
}
