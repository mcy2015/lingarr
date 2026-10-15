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

        var orphanedCount = await _dbContext.Shows
            .CountAsync(s => !existingSonarrIds.Contains(s.SonarrId));
        if (orphanedCount == 0)
        {
            return;
        }

        _logger.LogInformation("Removing {Count} shows that no longer exist in Sonarr", orphanedCount);

        // Child-first set-based deletes: no Include graph is materialized and no
        // per-row DELETE statements are issued (children of 20k shows can be 100k+
        // episodes).
        await _dbContext.Episodes
            .Where(e => !existingSonarrIds.Contains(e.Season.Show.SonarrId))
            .ExecuteDeleteAsync();
        await _dbContext.Seasons
            .Where(s => !existingSonarrIds.Contains(s.Show.SonarrId))
            .ExecuteDeleteAsync();
        await _dbContext.Images
            .Where(i => i.Show != null && !existingSonarrIds.Contains(i.Show.SonarrId))
            .ExecuteDeleteAsync();
        await _dbContext.Shows
            .Where(s => !existingSonarrIds.Contains(s.SonarrId))
            .ExecuteDeleteAsync();
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
