using System.Text.Json.Serialization;

namespace Lingarr.Server.Models.Integrations;

public class RadarrMovie
{
    [JsonPropertyName("id")]
    public required int Id { get; set; }
    [JsonPropertyName("title")]
    public required string Title { get; set; }
    [JsonPropertyName("path")]
    public required string Path { get; set; }
    [JsonPropertyName("rootFolderPath")]
    public required string RootFolderPath { get; set; }
    [JsonPropertyName("added")]
    public required string Added { get; set; }
    [JsonPropertyName("hasFile")]
    public required bool HasFile { get; set; }
    /// <summary>Release year — feeds movies.year (M0008, subtitle-search request building).</summary>
    [JsonPropertyName("year")]
    public int? Year { get; set; }
    /// <summary>IMDb id ("tt…") — feeds movies.imdb_id (M0008).</summary>
    [JsonPropertyName("imdbId")]
    public string? ImdbId { get; set; }
    /// <summary>Ratings block; only the imdb child is mapped (B2: movies.imdb_rating).</summary>
    [JsonPropertyName("ratings")]
    public RadarrRatings? Ratings { get; set; }
    [JsonPropertyName("movieFile")]
    public RadarrMovieFile MovieFile { get; set; } = new();
    [JsonPropertyName("images")]
    public List<IntegrationImage>? Images { get; set; } = new();
}