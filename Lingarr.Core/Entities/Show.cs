using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
namespace Lingarr.Core.Entities;

public class Show : BaseEntity
{
    public required int SonarrId { get; set; }
    public required string Title { get; set; }
    public required string Path { get; set; }
    public required DateTimeOffset? DateAdded { get; set; }
    public List<Image> Images { get; set; } = new();
    public List<Season> Seasons { get; set; } = new();
    public bool IncludeInTranslation { get; set; } = true;
    public int? TranslationAgeThreshold { get; set; }
    public string? SubtitleCoverageJson { get; set; }

    /// <summary>
    /// Series year from Sonarr (M0008). Episode search requests resolve
    /// Year/ImdbId through Season→Show rather than storing them per episode
    /// (design point 1: no per-episode write amplification).
    /// </summary>
    public int? Year { get; set; }

    /// <summary>Series IMDb id ("tt…") from Sonarr; episode-search key via the Season→Show join.</summary>
    public string? ImdbId { get; set; }

    /// <summary>
    /// IMDb user rating; null when Sonarr has no real score yet
    /// (ratings.imdb.votes &lt;= 0 — the column never stores a fake 0).
    /// </summary>
    public double? ImdbRating { get; set; }

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