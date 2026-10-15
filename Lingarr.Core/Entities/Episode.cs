using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using Lingarr.Core.Interfaces;

namespace Lingarr.Core.Entities;

public class Episode : BaseEntity, IMedia
{
    public required int SonarrId { get; set; }
    public required int EpisodeNumber { get; set; }
    public required string Title { get; set; }
    public string? FileName { get; set; } = string.Empty;
    public string? Path { get; set; } = string.Empty;
    public string? MediaHash { get; set; } = string.Empty;
    public DateTimeOffset? DateAdded { get; set; }

    public int SeasonId { get; set; }
    [ForeignKey(nameof(SeasonId))]
    public required Season Season { get; set; }
    public bool IncludeInTranslation { get; set; } = true;

    /// <summary>
    /// PRD card A: audio languages as a normalized ISO-code JSON array
    /// (["zh","en"]; chi/zho folded onto zh by the shared container rules).
    /// Null — never "[]" — means unknown: a *arr payload without a recognized
    /// code leaves the column open for the embedded-scan ffprobe piggyback.
    /// </summary>
    public string[]? AudioLanguages { get; set; }

    /// <summary>
    /// Manual, reversible override of the subtitle-need rule (PRD card A):
    /// true forces "needs translation" even when audio/coverage say otherwise,
    /// false marks the episode as not needing subtitles. Null = decide from
    /// audio tracks + coverage. Written by the subtitle-need endpoint.
    /// </summary>
    public bool? AudioOverride { get; set; }

    /// <summary>
    /// Whether this episode currently lacks subtitles in any target language
    /// (audit P0-5): computed per-episode by the statistics scan from the
    /// contract rule (override &gt; audio exemption &gt; coverage gap) and flipped
    /// immediately by the manual override endpoint. The automated translation
    /// job selects with SQL WHERE needs_translation = 1 instead of scanning
    /// the library. Show-level display aggregates over its episodes.
    /// </summary>
    public bool NeedsTranslation { get; set; }

    /// <summary>Persisted exemption flag: audio covers a target language or
    /// the user marked the media as not needing subtitles. Maintained by the
    /// statistics scan and the subtitle-need endpoint (SubtitleNeedRule).</summary>
    public bool NoSubtitleNeeded { get; set; }

}