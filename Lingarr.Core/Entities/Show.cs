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