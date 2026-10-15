using Lingarr.Server.Models.Integrations;

namespace Lingarr.Server.Modules.Integration;

/// <summary>
/// Defines a service for interacting with the Radarr API.
/// </summary>
public interface IRadarrService
{
    /// <summary>
    /// Streams the full movie library from the Radarr API element by element.
    /// The response body is never buffered as a whole — at 100k movies the
    /// /api/v3/movie payload reaches hundreds of MB and must not be materialized
    /// into a single <see cref="List{T}"/>.
    /// </summary>
    /// <returns>
    /// An <see cref="IAsyncEnumerable{T}"/> of <see cref="RadarrMovie"/> that yields
    /// each movie as it is deserialized from the response stream.
    /// </returns>
    /// <exception cref="InvalidOperationException">Thrown when the Radarr settings are not configured.</exception>
    /// <exception cref="HttpRequestException">Thrown when the API request fails.</exception>
    IAsyncEnumerable<RadarrMovie> GetMovies();

    /// <summary>
    /// Asynchronously retrieves a movie from the Radarr API.
    /// </summary>
    /// <returns>
    /// This method calls the Radarr API to fetch a movie
    /// The task result contains a <see cref="RadarrMovie"/>
    /// objects representing the movie, or <c>null</c> if the API call fails.
    /// </returns>
    Task<RadarrMovie?> GetMovie(int MovieId);
}
