using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Modules.Sync;
using Lingarr.Server.Models.Integrations;
using Microsoft.EntityFrameworkCore;

namespace Lingarr.Server.Modules.Sync;

public class SeasonSync : ISeasonSync
{
    private readonly LingarrDbContext _dbContext;
    private readonly PathConversionService _pathConversionService;
    private readonly ILogger<SeasonSync> _logger;

    public SeasonSync(
        LingarrDbContext dbContext,
        PathConversionService pathConversionService,
        ILogger<SeasonSync> logger)
    {
        _dbContext = dbContext;
        _pathConversionService = pathConversionService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Season> SyncSeason(
        Show show,
        SonarrSeason season,
        IReadOnlyList<SonarrEpisode>? episodes,
        bool defaultInclude = true)
    {
        var seasonPath = GetSeasonPath(season, episodes);

        var seasonEntity = await _dbContext.Seasons
            .Include(s => s.Episodes)
            .FirstOrDefaultAsync(s => s.ShowId == show.Id && s.SeasonNumber == season.SeasonNumber);

        if (seasonEntity == null)
        {
            seasonEntity = new Season
            {
                SeasonNumber = season.SeasonNumber,
                Path = seasonPath,
                Show = show,
                IncludeInTranslation = defaultInclude
            };
            show.Seasons.Add(seasonEntity);
        }
        else
        {
            seasonEntity.SeasonNumber = season.SeasonNumber;
            seasonEntity.Path = seasonPath;
            seasonEntity.Show = show;
        }

        return seasonEntity;
    }

    /// <summary>
    /// Derives the season path from the season's episodes, using the
    /// <c>episodeFile</c> object embedded in the pre-fetched episode list
    /// (no per-episode HTTP lookups).
    /// <para>
    /// Audit P2-9: the first episode with <c>hasFile</c> is not guaranteed to
    /// carry a usable <c>episodeFile.path</c> (Sonarr occasionally serves
    /// hasFile=true with an empty file resource). The first episode whose own
    /// file path is non-empty wins — its directory is the season directory;
    /// only when no episode has one does the selection fall back to the plain
    /// first hasFile episode, which then folds to the "/Season N" placeholder
    /// below exactly as before.
    /// </para>
    /// </summary>
    /// <param name="season">The Sonarr season to get the path for</param>
    /// <param name="episodes">The already-fetched Sonarr episodes of this season</param>
    /// <returns>The converted and mapped path for the season, or an empty string if no path could be determined</returns>
    private string GetSeasonPath(SonarrSeason season, IReadOnlyList<SonarrEpisode>? episodes)
    {
        var episode = episodes?.FirstOrDefault(episode =>
                          episode.HasFile && !string.IsNullOrWhiteSpace(episode.EpisodeFile?.Path))
                      ?? episodes?.Where(episode => episode.HasFile).FirstOrDefault();
        if (episode == null)
        {
            return string.Empty;
        }

        var normalizePath = _pathConversionService.NormalizePath(episode.EpisodeFile?.Path ?? string.Empty);
        var seasonPath = Path.GetDirectoryName(normalizePath);
        _logger.LogInformation("Syncing episode: {EpisodeId} with Path: {SeasonPath}", episode.Id, seasonPath);

        if (seasonPath != null)
        {
            if (!seasonPath.StartsWith("/"))
            {
                seasonPath = $"/{seasonPath}";
            }
        }
        else
        {
            seasonPath = $"/Season {season.SeasonNumber}";
        }

        return _pathConversionService.ConvertAndMapPath(
            seasonPath,
            MediaType.Show
        );
    }
}
