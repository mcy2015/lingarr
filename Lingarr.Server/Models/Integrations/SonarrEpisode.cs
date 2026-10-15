using System.Text.Json.Serialization;

namespace Lingarr.Server.Models.Integrations;

public class SonarrEpisode
{
    [JsonPropertyName("id")]
    public required int Id { get; set; }
    [JsonPropertyName("episodeNumber")]
    public required int EpisodeNumber { get; set; }
    [JsonPropertyName("title")]
    public required string Title { get; set; }
    [JsonPropertyName("seasonNumber")]
    public required int SeasonNumber { get; set; }
    [JsonPropertyName("hasFile")]
    public required bool HasFile { get; set; }
    /// <summary>
    /// Present on the episode resource itself whenever <see cref="HasFile"/> is true.
    /// Mapping it here removes the need for a per-episode /api/v3/episode/{id}
    /// round trip (one HTTP call per episode, ~190k calls on a 100k-episode library).
    /// </summary>
    [JsonPropertyName("episodeFile")]
    public SonarrEpisodeFile? EpisodeFile { get; set; }
    [JsonPropertyName("series")]
    public SonarrShow? Show { get; set; }
}