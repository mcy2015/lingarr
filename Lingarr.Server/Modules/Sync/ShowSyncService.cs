using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Server.Modules.Integration;
using Lingarr.Server.Modules.Sync;
using Lingarr.Server.Models.Integrations;
using Microsoft.EntityFrameworkCore;

namespace Lingarr.Server.Modules.Sync;

public class ShowSyncService : IShowSyncService
{
    private const int BatchSize = 100;

    /// <summary>
    /// Maximum share of the stored library a single orphan-removal pass may
    /// delete (QA P1 guardrail). A reverse-proxy outage or a reinstalled
    /// Sonarr serving HTTP 200 with an empty or stale-partial show list is
    /// indistinguishable from "everything was deleted upstream"; without this
    /// cap such a response wipes the local library in one pass. Real churn
    /// between two sync runs stays far below half a library, while
    /// aggressive-but-legitimate cleanups under the line still run.
    /// </summary>
    private const double MaxLibraryRemovalRatio = 0.5;

    /// <summary>
    /// Ids per DELETE statement (EmbeddedTrackService delete precedent).
    /// SQLite allows at most 32766 host parameters per command, so an id list
    /// must never be inlined into a single Contains — a 20k+ show library
    /// used to overflow that cap and abort the removal pass.
    /// </summary>
    private const int DeleteChunkSize = 500;

    private readonly LingarrDbContext _dbContext;
    private readonly IShowSync _showSync;
    private readonly ISeasonSync _seasonSync;
    private readonly IEpisodeSync _episodeSync;
    private readonly ISonarrService _sonarrService;
    private readonly ILogger<ShowSyncService> _logger;

    public ShowSyncService(
        LingarrDbContext dbContext,
        IShowSync showSync,
        ISeasonSync seasonSync,
        IEpisodeSync episodeSync,
        ISonarrService sonarrService,
        ILogger<ShowSyncService> logger)
    {
        _dbContext = dbContext;
        _showSync = showSync;
        _seasonSync = seasonSync;
        _episodeSync = episodeSync;
        _sonarrService = sonarrService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task SyncShows(List<SonarrShow> shows, bool defaultInclude)
    {
        var processedCount = 0;

        foreach (var show in shows)
        {
            var showEntity = await _showSync.SyncShow(show, defaultInclude);
            await SyncSeasonsAndEpisodes(showEntity, show, defaultInclude);

            processedCount++;

            if (processedCount % BatchSize == 0)
            {
                await SaveChanges(processedCount, shows.Count);
            }
        }

        if (processedCount % BatchSize != 0)
        {
            await SaveChanges(processedCount, shows.Count);
        }
    }

    /// <inheritdoc />
    public async Task<Show> SyncShow(SonarrShow show, bool defaultInclude)
    {
        var showEntity = await _showSync.SyncShow(show, defaultInclude);
        await SyncSeasonsAndEpisodes(showEntity, show, defaultInclude);

        await _dbContext.SaveChangesAsync();
        _logger.LogInformation("Synced a single show");

        return showEntity;
    }

    /// <summary>
    /// Syncs every season (and its episodes) of one show. The episode list is fetched
    /// exactly once per season and shared by the season-path derivation and the
    /// episode diff — previously this performed two fetches plus one path lookup per
    /// episode.
    /// </summary>
    private async Task SyncSeasonsAndEpisodes(Show showEntity, SonarrShow show, bool defaultInclude)
    {
        foreach (var season in show.Seasons)
        {
            var episodes = await _sonarrService.GetEpisodes(show.Id, season.SeasonNumber);
            var seasonEntity = await _seasonSync.SyncSeason(showEntity, season, episodes, defaultInclude);
            await _episodeSync.SyncEpisodes(seasonEntity, episodes, defaultInclude);
        }
    }

    /// <inheritdoc />
    public async Task RemoveNonExistentShows(HashSet<int> existingSonarrIds)
    {
        // ExecuteDelete bypasses the change tracker; drop anything still tracked from
        // the preceding sync batches so stale instances cannot be resurrected later.
        _dbContext.ChangeTracker.Clear();

        if (existingSonarrIds.Count == 0)
        {
            // QA P1: HTTP 200 with an empty array is what a reverse-proxy
            // failure or a freshly reinstalled Sonarr looks like from here —
            // treating it as "every show was deleted" used to wipe whole
            // libraries in one pass. Skip the pass instead.
            _logger.LogWarning(
                "Sonarr returned an empty show list — skipping this deletion pass. " +
                "An empty list usually means a reverse-proxy failure or a reinstalled Sonarr instance, " +
                "not that every show was deleted");
            return;
        }

        // The orphan diff runs in memory over one parameter-free id projection:
        // the upstream id list never becomes SQL parameters, and the orphan ids
        // are deleted in bounded chunks (see DeleteChunkSize).
        var dbSonarrIds = await _dbContext.Shows
            .Select(show => show.SonarrId)
            .ToListAsync();
        var orphanedSonarrIds = dbSonarrIds
            .Where(id => !existingSonarrIds.Contains(id))
            .ToList();

        if (orphanedSonarrIds.Count == 0)
        {
            return;
        }

        if (orphanedSonarrIds.Count > dbSonarrIds.Count * MaxLibraryRemovalRatio)
        {
            // QA P1 guardrail: same failure mode as the empty list, but the
            // proxy/Sonarr served a stale partial list. Refuse this round and
            // let the next run with a healthy upstream decide.
            _logger.LogWarning(
                "Refusing to remove {OrphanedCount} of {TotalCount} stored shows in one pass: " +
                "more than half the library disappearing is treated as an upstream failure " +
                "(reverse-proxy outage or reinstalled Sonarr) rather than real deletions. No shows were removed",
                orphanedSonarrIds.Count, dbSonarrIds.Count);
            return;
        }

        _logger.LogInformation("Removing {Count} shows that no longer exist in Sonarr", orphanedSonarrIds.Count);

        // Child-first set-based deletes: no Include graph is materialized and no
        // per-row DELETE statements are issued (children of 20k shows can be
        // 100k+ episodes) — one DELETE per table per 500-orphan chunk. The
        // wrapping transaction makes the pass atomic (QA P2): the four
        // ExecuteDeletes used to run in separate implicit transactions, so a
        // failure between them left a half-deleted show graph.
        await using var transaction = await _dbContext.Database.BeginTransactionAsync();
        foreach (var chunk in orphanedSonarrIds.Distinct().Chunk(DeleteChunkSize))
        {
            var chunkIds = chunk.ToList();
            await _dbContext.Episodes
                .Where(e => chunkIds.Contains(e.Season.Show.SonarrId))
                .ExecuteDeleteAsync();
            await _dbContext.Seasons
                .Where(s => chunkIds.Contains(s.Show.SonarrId))
                .ExecuteDeleteAsync();
            await _dbContext.Images
                .Where(i => i.Show != null && chunkIds.Contains(i.Show.SonarrId))
                .ExecuteDeleteAsync();
            await _dbContext.Shows
                .Where(s => chunkIds.Contains(s.SonarrId))
                .ExecuteDeleteAsync();
        }

        await transaction.CommitAsync();
    }

    /// <summary>
    /// Saves pending changes to the database and logs the sync progress. The change
    /// tracker is cleared after every batch so the tracked graph does not grow
    /// monotonically across a 20k-show run (DetectChanges cost is quadratic in the
    /// number of tracked entries otherwise).
    /// </summary>
    /// <param name="processedCount">The number of shows processed so far</param>
    /// <param name="totalCount">The total number of shows to process</param>
    private async Task SaveChanges(int processedCount, int totalCount)
    {
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();
        _logger.LogInformation("Synced and saved {ProcessedCount} out of {TotalCount} shows",
            processedCount, totalCount);
    }
}
