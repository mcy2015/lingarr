using System.Text.Json.Serialization;

namespace Lingarr.Server.Models.Integrations;

public class RadarrMovieFile
{
    [JsonPropertyName("path")]
    public string? Path { get; set; } = string.Empty;

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
    /// Quality wrapper — quality.quality.name is the release quality label
    /// ("WEBDL-1080p") stored on movies.quality (B2).
    /// </summary>
    [JsonPropertyName("quality")]
    public RadarrMovieFileQuality? Quality { get; set; }

    /// <summary>File size in bytes, stored on movies.size_bytes (B2).</summary>
    [JsonPropertyName("size")]
    public long? Size { get; set; }
}
