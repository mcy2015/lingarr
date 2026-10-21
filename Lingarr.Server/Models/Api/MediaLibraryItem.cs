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
    /// The subtitle-need rule judged this media as not needing subtitles:
    /// a manual "not needed" mark, or known audio that already covers a
    /// target language. Subtitle coverage never exempts (the rule has no
    /// coverage branch — full coverage still surfaces as a gap until the
    /// audio or a manual mark says otherwise). Drives the "native" library
    /// state filter.
    /// </summary>
    public bool NeedsNoSubtitles { get; init; }

    /// <summary>
    /// The work's original language (TMDb via *arr) — a GUESS shown as a
    /// muted「推测」hint when the real audio languages are unknown. Never
    /// merged into <see cref="AudioLanguages"/> (the ffprobe backfill keeps
    /// its room). Null when the payload carried none.
    /// </summary>
    public string? SuggestedAudioLanguage { get; init; }

    /// <summary>
    /// The manual subtitle-need mark: true = forced "needs subtitles",
    /// false = forced "not needed", null = automatic (audio rule decides).
    /// Shows aggregate over their episodes (uniform mark → that value).
    /// Drives the detail page's persistent badge and its cancel action.
    /// </summary>
    public bool? AudioOverride { get; init; }

    /// <summary>
    /// B08 per-media target-language override: null = follow the global
    /// target_languages setting (pre-B08 behavior); empty array = the
    /// explicit「无目标」state (the media wants no subtitles); otherwise the
    /// exact codes the need rules and the detail page's coverage chips
    /// resolve against. Only the detail (single-item) projection populates
    /// it — list rows stay null. Shows carry the series-level override
    /// their episodes inherit.
    /// </summary>
    public string[]? TargetLanguagesOverride { get; init; }

    /// <summary>
    /// Release year from *arr (library B2/M0008). Shows carry the series
    /// year; movies the release year. Null when the payload had none.
    /// </summary>
    public int? Year { get; init; }

    /// <summary>
    /// IMDb id ("tt…") from *arr (B2). Null when the payload had none.
    /// </summary>
    public string? ImdbId { get; init; }

    /// <summary>
    /// Release quality label ("WEBDL-1080p", B2). A movie-file concept:
    /// show projections leave it null (episodes are not per-file scored).
    /// </summary>
    public string? Quality { get; init; }

    /// <summary>
    /// File size in bytes (B2). Movie files only: show projections leave
    /// it null — a series has no single file size.
    /// </summary>
    public long? SizeBytes { get; init; }

    /// <summary>
    /// IMDb user rating (B2); null when *arr has no real score yet
    /// (the column never stores a fake 0).
    /// </summary>
    public double? ImdbRating { get; init; }

    /// <summary>
    /// TMDb plot summary (media-file batch): movies carry movie.overview,
    /// shows the series overview. The detail page's 简介 block; null when
    /// the payload carried none.
    /// </summary>
    public string? Overview { get; init; }

    /// <summary>
    /// Poster path (media-file batch): the *arr MediaCover URL path of the
    /// synced "poster" image; the client loads it through the
    /// /api/image/{movie|show} proxy. Null when no poster was synced.
    /// </summary>
    public string? PosterPath { get; init; }

    /// <summary>
    /// Video file name without extension (media-file batch, movies only —
    /// the entity's storage convention). The file card's 文件名 line.
    /// </summary>
    public string? FileName { get; init; }

    /// <summary>
    /// Directory the video file lives in (media-file batch, movies only).
    /// The file card's 所在目录 line.
    /// </summary>
    public string? Path { get; init; }

    /// <summary>Video codec ("h264"); movies only (episode rows carry their own).</summary>
    public string? VideoCodec { get; init; }

    /// <summary>Audio codec ("eac3"); movies only.</summary>
    public string? AudioCodec { get; init; }

    /// <summary>Audio channel count (5.1); movies only.</summary>
    public double? AudioChannels { get; init; }

    /// <summary>Container pixel size ("1920x1080"); movies only.</summary>
    public string? Resolution { get; init; }

    /// <summary>Runtime in minutes; movies only.</summary>
    public int? RuntimeMinutes { get; init; }

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

    /// <summary>
    /// B07 forced dimension: how many episodes carry a forced-only (caption
    /// "forced") subtitle in this language. Presented alongside the full
    /// coverage state — a forced-only language keeps State "none" (a forced
    /// track never satisfies the full-subtitle need).
    /// </summary>
    public int ForcedHave { get; init; }

    /// <summary>
    /// B04 embedded dimension: how many media units (episodes for shows,
    /// 0/1 for movies) carry a recognized EMBEDDED subtitle track in this
    /// language, written only while the embedded_track_counts_as_coverage
    /// setting is on. Same badge shape as the forced dimension, opposite
    /// need semantics: while the setting is on an embedded-covered target
    /// language no longer enters the missing-subtitle gap queue (Have/State
    /// keep presenting the external-file verdict).
    /// </summary>
    public int EmbeddedHave { get; init; }

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
