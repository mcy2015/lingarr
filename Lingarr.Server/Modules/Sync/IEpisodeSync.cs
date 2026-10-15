using Lingarr.Core.Entities;
using Lingarr.Server.Models.Integrations;

namespace Lingarr.Server.Modules.Sync;

public interface IEpisodeSync
{
    /// <summary>
    /// Synchronizes a pre-fetched season episode list with the given season entity.
    /// The caller supplies the episodes (already fetched once per season) so this
    /// step performs no HTTP calls of its own; file paths and dates come from the
    /// <c>episodeFile</c> object embedded in each episode resource.
    /// </summary>
    /// <param name="season">The season to sync episodes for</param>
    /// <param name="episodes">The Sonarr episodes of this season, or <c>null</c> when the fetch failed</param>
    /// <param name="defaultInclude">Whether newly imported episodes should be included in translation by default</param>
    /// <returns>A task representing the asynchronous operation</returns>
    Task SyncEpisodes(Season season, IReadOnlyList<SonarrEpisode>? episodes, bool defaultInclude = true);
}
