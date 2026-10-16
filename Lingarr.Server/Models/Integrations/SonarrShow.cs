using System.Text.Json.Serialization;

namespace Lingarr.Server.Models.Integrations;

public class SonarrShow
{
    [JsonPropertyName("id")]
    public required int Id { get; set; }
    [JsonPropertyName("title")]
    public required string Title { get; set; }
    [JsonPropertyName("path")]
    public required string Path { get; set; }
    [JsonPropertyName("added")]
    public required string Added { get; set; }
    [JsonPropertyName("seasonFolder")]
    public required bool SeasonFolder { get; set; }
    [JsonPropertyName("rootFolderPath")]
    public string? RootFolderPath { get; set; } 
    [JsonPropertyName("seasons")]
    public List<SonarrSeason> Seasons { get; set; } = new();
    [JsonPropertyName("images")]
    public List<IntegrationImage>? Images { get; set; } = new();

    /// <summary>Series year — feeds shows.year (M0008, subtitle-search request building).</summary>
    [JsonPropertyName("year")]
    public int? Year { get; set; }

    /// <summary>Series IMDb id ("tt…") — feeds shows.imdb_id; episode searches resolve it through Season→Show.</summary>
    [JsonPropertyName("imdbId")]
    public string? ImdbId { get; set; }

    /// <summary>
    /// Ratings block of the series resource; only the imdb child is mapped
    /// (B2: shows.imdb_rating). Shares the shape with Radarr's movie
    /// resource, so the ratings DTO is reused.
    /// </summary>
    [JsonPropertyName("ratings")]
    public RadarrRatings? Ratings { get; set; }
}