using Lingarr.Core.Entities;
using Lingarr.Server.Models.Integrations;

namespace Lingarr.Server.Modules.Sync;

public interface ISeasonSync
{
    /// <summary>
    /// Synchronizes a single season from Sonarr. The season path is derived from the
    /// pre-fetched episode list (first episode that has a file), so this step performs
    /// no HTTP calls of its own.
    /// </summary>
    /// <param name="show">The show entity the season belongs to</param>
    /// <param name="season">The Sonarr season to sync</param>
    /// <param name="episodes">The already-fetched Sonarr episodes of this season, or <c>null</c> when the fetch failed</param>
    /// <param name="defaultInclude">Whether newly imported seasons should be included in translation by default</param>
    /// <returns>The synchronized season entity</returns>
    Task<Season> SyncSeason(
        Show show,
        SonarrSeason season,
        IReadOnlyList<SonarrEpisode>? episodes,
        bool defaultInclude = true);
}
