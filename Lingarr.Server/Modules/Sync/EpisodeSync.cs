using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Modules.Media;
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
        var audioLanguages = ResolveAudioLanguages(episode);
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
            // The setter folds an empty result back to null — the column never
            // stores "[]" (unknown is null).
            episodeEntity.AudioLanguages = audioLanguages?.ToArray();
            season.Episodes.Add(episodeEntity);
        }
        else
        {
            episodeEntity.EpisodeNumber = episode.EpisodeNumber;
            episodeEntity.Title = episode.Title;
            episodeEntity.FileName = Path.GetFileNameWithoutExtension(episodePath);
            episodeEntity.Path = Path.GetDirectoryName(episodePath);
            episodeEntity.DateAdded = dateAdded;
            // A payload without a recognized code never clears the column — an
            // ffprobe-filled value survives until *arr has real data.
            if (audioLanguages != null)
            {
                episodeEntity.AudioLanguages = audioLanguages?.ToArray();
            }
        }
    }

    /// <summary>
    /// PRD card A trust chain: mediaInfo.audioLanguages (ffprobe truth,
    /// "english / chi") outranks the episodeFile languages aggregate, which in
    /// turn outranks the episode-level mirror. Returns null when no source
    /// carries a recognized code — the column then stays null ("unknown") so
    /// the embedded-scan ffprobe piggyback can fill it later.
    /// </summary>
    private static List<string>? ResolveAudioLanguages(SonarrEpisode episode)
    {
        var codes = EmbeddedTrackService.ParseAudioLanguageString(episode.EpisodeFile?.MediaInfo?.AudioLanguages);
        if (codes.Count == 0)
        {
            // An empty-but-present file-level list still falls through to the
            // episode-level mirror — only actual entries count as a source.
            // Null ELEMENTS are real payloads (includeEpisodeFile=true serves
            // [null] for unknown languages) and must never reach .Name.
            IEnumerable<string?> names = episode.EpisodeFile?.Languages is { Count: > 0 } fileLanguages
                ? fileLanguages.Where(language => language != null).Select(language => language!.Name)
                : episode.Languages?.Where(language => language != null).Select(language => language!.Name) ?? [];
            codes = EmbeddedTrackService.NormalizeLanguageNames(names);
        }

        return codes.Count > 0 ? codes : null;
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
