using Lingarr.Core.Interfaces;

using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
namespace Lingarr.Core.Entities;

public class Movie : BaseEntity, IMedia
{
    public required int RadarrId { get; set; }
    public required string Title { get; set; }
    public required string? FileName { get; set; }
    public required string? Path { get; set; }
    public string? MediaHash { get; set; } = string.Empty;

    /// <summary>
    /// OpenSubtitles moviehash of the video file (M0011): lazily computed by
    /// the subtitle-search request builder and written back by
    /// <c>LazyMediaHasher.WriteBackVideoHashAsync</c> — the column's single
    /// writer. Deliberately separate from <see cref="MediaHash"/>, which the
    /// translation pipeline owns (subtitle-set hash with clear-on-change
    /// events); the split keeps the two writers from ever contending. Null
    /// means "not computed yet" — there is no empty-string sentinel here.
    /// </summary>
    public string? VideoHash { get; set; }

    public required DateTimeOffset? DateAdded { get; set; }
    public List<Image> Images { get; set; } = new();
    public bool IncludeInTranslation { get; set; } = true;
    public int? TranslationAgeThreshold { get; set; }
    public string? SubtitleCoverageJson { get; set; }

    /// <summary>
    /// PRD card A: audio languages as a normalized ISO-code JSON array
    /// (["zh","en"]; chi/zho folded onto zh by the shared container rules).
    /// Null — never "[]" — means unknown: a *arr payload without a recognized
    /// code leaves the column open for the embedded-scan ffprobe piggyback.
    /// </summary>
    public string[]? AudioLanguages { get; set; }

    /// <summary>
    /// The work's original language (TMDb spoken language via
    /// Radarr.movie.originalLanguage), a GUESS for files whose container
    /// carries no language tag. Kept apart from <see cref="AudioLanguages"/>
    /// so it never blocks the ffprobe backfill; the UI shows it as a muted
    /// 「推测」hint. Null when the payload carries none.
    /// </summary>
    public string? SuggestedLanguage { get; set; }

    /// <summary>
    /// The manual language editor / remote identification action wrote the
    /// current <see cref="AudioLanguages"/> — a later sync payload without a
    /// recognized code never clears it (audio_override precedent).
    /// </summary>
    public bool? AudioLanguagesManual { get; set; }

    /// <summary>
    /// Manual, reversible override of the subtitle-need rule (PRD card A):
    /// true forces "needs translation" even when audio/coverage say otherwise,
    /// false marks the media as not needing subtitles. Null = decide from
    /// audio tracks + coverage. Written by the subtitle-need endpoint.
    /// </summary>
    public bool? AudioOverride { get; set; }

    /// <summary>
    /// Whether this movie currently lacks subtitles in any target language
    /// (audit P0-5): computed by the statistics scan from the contract rule
    /// (override &gt; audio exemption &gt; coverage gap) and flipped immediately
    /// by the manual override endpoint. The automated translation job selects
    /// with SQL WHERE needs_translation = 1 instead of scanning the library.
    /// </summary>
    public bool NeedsTranslation { get; set; }

    /// <summary>Persisted exemption flag: audio covers a target language or
    /// the user marked the media as not needing subtitles. Maintained by the
    /// statistics scan and the subtitle-need endpoint (SubtitleNeedRule).</summary>
    public bool NoSubtitleNeeded { get; set; }

    /// <summary>
    /// Release year from Radarr (M0008/B2). Feed for the subtitle-search
    /// request builder (title + year disambiguation, tech design ②/⑤).
    /// </summary>
    public int? Year { get; set; }

    /// <summary>IMDb id ("tt…") from Radarr; strongest provider-side search key after hash.</summary>
    public string? ImdbId { get; set; }

    /// <summary>
    /// Release quality label from movieFile.quality.quality.name
    /// ("WEBDL-1080p"); a release-name token for candidate scoring (③).
    /// </summary>
    public string? Quality { get; set; }

    /// <summary>File size in bytes from movieFile.size (library B2).</summary>
    public long? SizeBytes { get; set; }

    /// <summary>
    /// IMDb user rating; null when Radarr has no real score yet
    /// (ratings.imdb.votes &lt;= 0 — the column never stores a fake 0).
    /// </summary>
    public double? ImdbRating { get; set; }

    /// <summary>TMDb plot summary from Radarr (movie.overview); the detail
    /// page's 简介 block. Null when the payload carries none.</summary>
    public string? Overview { get; set; }

    /// <summary>Video codec from movieFile.mediaInfo.videoCodec ("h264").</summary>
    public string? VideoCodec { get; set; }

    /// <summary>Audio codec from movieFile.mediaInfo.audioCodec ("eac3").</summary>
    public string? AudioCodec { get; set; }

    /// <summary>Audio channel count from movieFile.mediaInfo.audioChannels (5.1).</summary>
    public double? AudioChannels { get; set; }

    /// <summary>Container pixel size from movieFile.mediaInfo.resolution ("1920x1080").</summary>
    public string? Resolution { get; set; }

    /// <summary>Runtime in minutes, parsed from movieFile.mediaInfo.runTime ("1:57:00").</summary>
    public int? RuntimeMinutes { get; set; }

    /// <summary>
    /// Coverage watermark (M0009): when the statistics scan last computed
    /// this movie's language coverage. Written by the M7 watermark batch;
    /// null = not yet checked (full-scan eligible).
    /// </summary>
    public DateTimeOffset? CoverageCheckedAt { get; set; }

    /// <summary>
    /// Language -> subtitle coverage, written by the statistics scan; null
    /// until the first scan after the coverage migration.
    /// </summary>
    [NotMapped]
    public Dictionary<string, MediaLanguageCoverage>? SubtitleCoverage
    {
        get => SubtitleCoverageJson == null
            ? null
            : JsonSerializer.Deserialize<Dictionary<string, MediaLanguageCoverage>>(SubtitleCoverageJson);
        set => SubtitleCoverageJson = value == null
            ? null
            : JsonSerializer.Serialize(value);
    }

}