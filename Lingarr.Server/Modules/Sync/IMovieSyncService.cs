using Lingarr.Core.Entities;
using Lingarr.Server.Models.Integrations;

namespace Lingarr.Server.Modules.Sync;

public interface IMovieSyncService
{
    /// <summary>
    /// Synchronizes movies streamed from Radarr, committing in batches.
    /// </summary>
    /// <param name="movies">The Radarr movies to sync, streamed element by element</param>
    /// <param name="defaultInclude">Whether new movies should be included in translation by default</param>
    /// <returns>
    /// A task that completes with the Radarr IDs of every movie observed on the stream
    /// (including movies without files), for the subsequent orphan-removal step.
    /// </returns>
    Task<List<int>> SyncMovies(IAsyncEnumerable<RadarrMovie> movies, bool defaultInclude);

    /// <summary>
    /// Synchronizes a movie from Radarr
    /// </summary>
    /// <param name="movie">The Radarr movie to sync</param>
    /// <param name="defaultInclude">Whether new movies should be included in translation by default</param>
    /// <returns>A task representing the asynchronous operation and the synchronized movie</returns>
    Task<Movie?> SyncMovie(RadarrMovie movie, bool defaultInclude);

    /// <summary>
    /// Removes movies that no longer exist in Radarr
    /// </summary>
    /// <param name="existingRadarrIds">The collection of currently existing Radarr movie IDs</param>
    /// <returns>A task representing the asynchronous operation</returns>
    Task RemoveNonExistentMovies(IEnumerable<int> existingRadarrIds);
}
