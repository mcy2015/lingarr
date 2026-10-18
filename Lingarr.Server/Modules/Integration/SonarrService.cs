using Lingarr.Server.Modules.Integration;
using Lingarr.Server.Models;
using Lingarr.Server.Models.Integrations;

namespace Lingarr.Server.Modules.Integration;

public class SonarrService : ISonarrService
{
    private readonly IIntegrationService _integrationService;

    public SonarrService(IIntegrationService integrationService)
    {
        _integrationService = integrationService;
    }
    
    /// <inheritdoc />
    public async Task<List<SonarrShow>?> GetShows()
    {
        // QA4 P2-1: the series payload is consumed through the streaming
        // endpoint (ResponseHeadersRead + incremental top-level array
        // deserialization — the Radarr precedent), so the full JSON document
        // is never buffered by HttpClient alongside the materialized list.
        // A 20k-series library (~60-160MB) keeps one residency instead of
        // two. The elements are buffered into the returned list here because
        // the current callers need the full set (count + orphan diff); the
        // seam to switch them to a pure IAsyncEnumerable is
        // ISonarrService.GetShows + ShowSyncService.SyncShows.
        var shows = new List<SonarrShow>();
        try
        {
            await foreach (var show in _integrationService.StreamApiResponse<SonarrShow>(
                               "/api/v3/series/",
                               new IntegrationSettingKeys
                               {
                                   Url = "sonarr_url",
                                   ApiKey = "sonarr_api_key"
                               }))
            {
                shows.Add(show);
            }
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not configured", StringComparison.Ordinal))
        {
            // StreamApiResponse signals "integration not configured" by
            // throwing (an async stream cannot yield null the way
            // GetApiResponse returned default). The historical GetShows
            // contract — null, caller skips the run — is preserved.
            return null;
        }

        return shows;
    }
    
    /// <inheritdoc />
    public async Task<List<SonarrEpisode>?> GetEpisodes(int seriesNumber, int seasonNumber)
    {
        // includeImages omitted (image blobs were never consumed here — they
        // only inflated the payload 2-5x). includeEpisodeFile=true is NOT
        // optional: Sonarr's list endpoint defaults the flag to false, which
        // serves episodes without their episodeFile resource (hasFile is an
        // independent scalar and stays true) — every path, dateAdded and
        // mediaInfo.audioLanguages would silently deserialize to null.
        return await _integrationService.GetApiResponse<List<SonarrEpisode>>(
            $"/api/v3/episode?seriesId={seriesNumber}&seasonNumber={seasonNumber}&includeEpisodeFile=true",
            new IntegrationSettingKeys
            {
                Url = "sonarr_url",
                ApiKey = "sonarr_api_key"
            });
    }

    /// <inheritdoc />
    public async Task<SonarrEpisode?> GetEpisode(int episodeNumber)
    {
        return await _integrationService.GetApiResponse<SonarrEpisode>(
            $"/api/v3/episode/{episodeNumber}",
            new IntegrationSettingKeys
            {
                Url = "sonarr_url",
                ApiKey = "sonarr_api_key"
            });
    }
}