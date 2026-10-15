using System.Text.Json.Serialization;

namespace Lingarr.Server.Models.Integrations;

/// <summary>
/// The ratings block of a Radarr movie resource. Only the imdb child is
/// mapped — it feeds movies.imdb_rating / shows.imdb_rating (B2). A child
/// with <see cref="RadarrRatingSource.Votes"/> &lt;= 0 carries no real user
/// score (Radarr reports value 0), which maps onto a null column instead of
/// a fake 0.
/// </summary>
public class RadarrRatings
{
    [JsonPropertyName("imdb")]
    public RadarrRatingSource? Imdb { get; set; }
}

/// <summary>One ratings child of a *arr resource: {votes, value, type}.</summary>
public class RadarrRatingSource
{
    [JsonPropertyName("votes")]
    public int Votes { get; set; }

    [JsonPropertyName("value")]
    public double? Value { get; set; }
}
