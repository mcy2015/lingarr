using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Server.Modules.Sync;
using Lingarr.Server.Models.Integrations;
using Microsoft.EntityFrameworkCore;

namespace Lingarr.Server.Modules.Sync;

public class MovieSyncService : IMovieSyncService
{
    private const int BatchSize = 100;

    /// <summary>
    /// Maximum share of the stored library a single orphan-removal pass may
    /// delete (QA P1 guardrail). A reverse-proxy outage or a reinstalled
    /// Radarr serving HTTP 200 with an empty or stale-partial movie list is
    /// indistinguishable from "everything was deleted upstream"; without this
    /// cap such a response wipes the local library in one pass. Real churn
    /// between two sync runs stays far below half a library, while
    /// aggressive-but-legitimate cleanups under the line still run.
    /// </summary>
    private const double MaxLibraryRemovalRatio = 0.5;

    /// <summary>
    /// Ids per DELETE statement (EmbeddedTrackService delete precedent).
    /// SQLite allows at most 32766 host parameters per command, so an id list
    /// must never be inlined into a single Contains — a 20k+ movie library
    /// used to overflow that cap and abort the removal pass.
    /// </summary>
    private const int DeleteChunkSize = 500;

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

        var existing = existingRadarrIds.ToHashSet();
        if (existing.Count == 0)
        {
            // QA P1: HTTP 200 with an empty array is what a reverse-proxy
            // failure or a freshly reinstalled Radarr looks like from here —
            // treating it as "every movie was deleted" used to wipe whole
            // libraries in one pass. Skip the pass instead.
            _logger.LogWarning(
                "Radarr returned an empty movie list — skipping this deletion pass. " +
                "An empty list usually means a reverse-proxy failure or a reinstalled Radarr instance, " +
                "not that every movie was deleted");
            return;
        }

        // The orphan diff runs in memory over one parameter-free id projection:
        // the upstream id list never becomes SQL parameters, and the orphan ids
        // are deleted in bounded chunks (see DeleteChunkSize).
        var dbRadarrIds = await _dbContext.Movies
            .Select(movie => movie.RadarrId)
            .ToListAsync();
        var orphanedRadarrIds = dbRadarrIds
            .Where(id => !existing.Contains(id))
            .ToList();

        if (orphanedRadarrIds.Count == 0)
        {
            return;
        }

        if (orphanedRadarrIds.Count > dbRadarrIds.Count * MaxLibraryRemovalRatio)
        {
            // QA P1 guardrail: same failure mode as the empty list, but the
            // proxy/Radarr served a stale partial list. Refuse this round and
            // let the next run with a healthy upstream decide.
            _logger.LogWarning(
                "Refusing to remove {OrphanedCount} of {TotalCount} stored movies in one pass: " +
                "more than half the library disappearing is treated as an upstream failure " +
                "(reverse-proxy outage or reinstalled Radarr) rather than real deletions. No movies were removed",
                orphanedRadarrIds.Count, dbRadarrIds.Count);
            return;
        }

        _logger.LogInformation("Removing {Count} movies that no longer exist in Radarr", orphanedRadarrIds.Count);

        // Child-first set-based deletes (S2 precedent, ShowSyncService): images
        // before movies, no Include graph materialized and one DELETE per table
        // per 500-orphan chunk. The wrapping transaction makes the pass atomic
        // (QA P2): the two ExecuteDeletes used to run in separate implicit
        // transactions, so a failure between them left images deleted for
        // movies that survived — the old RemoveRange + single SaveChanges this
        // replaced was atomic, and this restores that contract.
        await using var transaction = await _dbContext.Database.BeginTransactionAsync();
        foreach (var chunk in orphanedRadarrIds.Distinct().Chunk(DeleteChunkSize))
        {
            var chunkIds = chunk.ToList();
            await _dbContext.Images
                .Where(image => image.Movie != null && chunkIds.Contains(image.Movie.RadarrId))
                .ExecuteDeleteAsync();
            await _dbContext.Movies
                .Where(movie => chunkIds.Contains(movie.RadarrId))
                .ExecuteDeleteAsync();
        }

        await transaction.CommitAsync();
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
