using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Server.Modules.Sync;
using Lingarr.Server.Models.Integrations;
using Microsoft.EntityFrameworkCore;

namespace Lingarr.Server.Modules.Sync;

public class MovieSyncService : IMovieSyncService
{
    private const int BatchSize = 100;
    private readonly LingarrDbContext _dbContext;
    private readonly IMovieSync _movieSync;
    private readonly ILogger<MovieSyncService> _logger;

    public MovieSyncService(
        LingarrDbContext dbContext,
        IMovieSync movieSync,
        ILogger<MovieSyncService> logger)
    {
        _dbContext = dbContext;
        _movieSync = movieSync;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<List<int>> SyncMovies(IAsyncEnumerable<RadarrMovie> movies, bool defaultInclude)
    {
        var processedCount = 0;
        // Movies flow through in batches of BatchSize; only the (small) id list is
        // retained for the whole run so orphan removal knows what still exists.
        var radarrIds = new List<int>();

        await foreach (var movie in movies)
        {
            radarrIds.Add(movie.Id);
            await _movieSync.SyncMovie(movie, defaultInclude);
            processedCount++;

            if (processedCount % BatchSize == 0)
            {
                await SaveChanges(processedCount);
            }
        }

        if (processedCount % BatchSize != 0)
        {
            await SaveChanges(processedCount);
        }

        return radarrIds;
    }

    /// <inheritdoc />
    public async Task<Movie?> SyncMovie(RadarrMovie movie, bool defaultInclude)
    {
        var movieEntity = await _movieSync.SyncMovie(movie, defaultInclude);
        await _dbContext.SaveChangesAsync();

        if (movieEntity != null)
        {
            _logger.LogInformation("Synced a single movie");
        }

        return movieEntity;
    }

    /// <inheritdoc />
    public async Task RemoveNonExistentMovies(IEnumerable<int> existingRadarrIds)
    {
        // ExecuteDelete bypasses the change tracker; drop anything still tracked
        // from the preceding sync batches so stale instances cannot be
        // resurrected later (same contract as ShowSyncService).
        _dbContext.ChangeTracker.Clear();

        var orphanedCount = await _dbContext.Movies
            .CountAsync(movie => !existingRadarrIds.Contains(movie.RadarrId));
        if (orphanedCount == 0)
        {
            return;
        }

        _logger.LogInformation("Removing {Count} movies that no longer exist in Radarr", orphanedCount);

        // Child-first set-based deletes (S2 precedent, ShowSyncService): images
        // before movies, no Include graph materialized and a constant number of
        // DELETE statements no matter how many movies go — the old per-row
        // RemoveRange issued 2K+1 statements for K orphaned movies.
        await _dbContext.Images
            .Where(image => image.Movie != null && !existingRadarrIds.Contains(image.Movie.RadarrId))
            .ExecuteDeleteAsync();
        await _dbContext.Movies
            .Where(movie => !existingRadarrIds.Contains(movie.RadarrId))
            .ExecuteDeleteAsync();
    }

    /// <summary>
    /// Saves pending changes to the database and logs the sync progress. The change
    /// tracker is cleared after every batch so the tracked graph does not grow
    /// monotonically across a 20k-movie run.
    /// </summary>
    /// <param name="processedCount">The number of movies processed so far</param>
    private async Task SaveChanges(int processedCount)
    {
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();
        _logger.LogInformation("Synced and saved {ProcessedCount} movies so far",
            processedCount);
    }
}
