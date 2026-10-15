using System.Text.Json.Serialization;

namespace Lingarr.Server.Models.Integrations;

/// <summary>
/// The quality wrapper of a Radarr movieFile resource:
/// <c>{ "quality": { "id": 7, "name": "BluRay-1080p" }, "revision": … }</c>.
/// Only the inner name is consumed (movies.quality, B2).
/// </summary>
public class RadarrMovieFileQuality
{
    [JsonPropertyName("quality")]
    public RadarrQualityItem? Quality { get; set; }
}

/// <summary>The inner quality item: {id, name}.</summary>
public class RadarrQualityItem
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>Quality label as shown in *arr ("WEBDL-1080p").</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }
}
