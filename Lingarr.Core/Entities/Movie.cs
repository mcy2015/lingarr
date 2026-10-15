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