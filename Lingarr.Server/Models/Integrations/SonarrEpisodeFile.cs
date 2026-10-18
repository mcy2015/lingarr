using System.Text.Json.Serialization;

namespace Lingarr.Server.Models.Integrations;

public class SonarrEpisodeFile
{
    [JsonPropertyName("path")]
    public string? Path { get; set; } = string.Empty;

    [JsonPropertyName("dateAdded")]
    public DateTimeOffset? DateAdded { get; set; }

    /// <summary>
    /// Aggregate language guess ([{id,name}]) — may fall back to the original
    /// language, so it ranks below mediaInfo.audioLanguages (PRD card A).
    /// </summary>
    [JsonPropertyName("languages")]
    public List<IntegrationLanguage>? Languages { get; set; }

    /// <summary>ffprobe-derived container truth (audioLanguages slash string).</summary>
    [JsonPropertyName("mediaInfo")]
    public IntegrationMediaInfo? MediaInfo { get; set; }

    /// <summary>
    /// Quality wrapper — same shape as Radarr's movieFile.quality; the name
    /// feeds episodes.quality (the episode table's 质量 column).
    /// </summary>
    [JsonPropertyName("quality")]
    public RadarrMovieFileQuality? Quality { get; set; }

    /// <summary>File size in bytes, stored on episodes.size_bytes.</summary>
    [JsonPropertyName("size")]
    public long? Size { get; set; }
}
