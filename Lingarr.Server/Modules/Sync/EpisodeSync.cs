using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Modules.Sync;
using Lingarr.Server.Models.Integrations;

namespace Lingarr.Server.Modules.Sync;

public class EpisodeSync : IEpisodeSync
{
    private readonly PathConversionService _pathConversionService;

    public EpisodeSync(PathConversionService pathConversionService)
    {
        _pathConversionService = pathConversionService;
    }

    /// <inheritdoc />
    public Task SyncEpisodes(Season season, IReadOnlyList<SonarrEpisode>? episodes, bool defaultInclude = true)
    {
        if (episodes == null) return Task.CompletedTask;

        foreach (var episode in episodes.Where(e => e.HasFile))
        {
            // Paths/dates come straight from the episode resource; no per-episode
            // HTTP round trip (previously one GetEpisodePath call per episode).
            var episodePath = _pathConversionService.ConvertAndMapPath(
                episode.EpisodeFile?.Path ?? string.Empty,
                MediaType.Show
            );

            SyncEpisode(episode, episodePath, season, episode.EpisodeFile?.DateAdded, defaultInclude);
        }

        RemoveNonExistentEpisodes(season, episodes);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Synchronizes a single episode with the database, creating or updating the episode entity as needed
    /// </summary>
    /// <param name="episode">The Sonarr episode containing the source data</param>
    /// <param name="episodePath">The converted and mapped file path for the episode</param>
    /// <param name="season">The season entity that owns this episode</param>
    /// <param name="dateAdded">The date the episode file was added</param>
    private static void SyncEpisode(SonarrEpisode episode, string episodePath, Season season, DateTimeOffset? dateAdded, bool defaultInclude = true)
    {
        var episodeEntity = season.Episodes.FirstOrDefault(se => se.SonarrId == episode.Id);
        if (episodeEntity == null)
        {
            episodeEntity = new Episode
            {
                SonarrId = episode.Id,
                EpisodeNumber = episode.EpisodeNumber,
                Title = episode.Title,
                FileName = Path.GetFileNameWithoutExtension(episodePath),
                Path = Path.GetDirectoryName(episodePath),
                Season = season,
                DateAdded = dateAdded,
                IncludeInTranslation = defaultInclude
            };
            season.Episodes.Add(episodeEntity);
        }
        else
        {
            episodeEntity.EpisodeNumber = episode.EpisodeNumber;
            episodeEntity.Title = episode.Title;
            episodeEntity.FileName = Path.GetFileNameWithoutExtension(episodePath);
            episodeEntity.Path = Path.GetDirectoryName(episodePath);
            episodeEntity.DateAdded = dateAdded;
        }
    }

    /// <summary>
    /// Removes episodes from the season that no longer exist in Sonarr
    /// </summary>
    /// <param name="season">The season entity containing the episodes to check</param>
    /// <param name="currentEpisodes">The list of currently existing Sonarr episodes</param>
    private static void RemoveNonExistentEpisodes(Season season, IReadOnlyList<SonarrEpisode> currentEpisodes)
    {
        var episodesToRemove = season.Episodes
            .Where(seasonEpisode => currentEpisodes.All(episode => episode.Id != seasonEpisode.SonarrId))
            .ToList();

        foreach (var episodeToRemove in episodesToRemove)
        {
            season.Episodes.Remove(episodeToRemove);
        }
    }
}
