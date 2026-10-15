using Lingarr.Server.Modules.Integration;
using Lingarr.Server.Models;
using Lingarr.Server.Models.Integrations;

namespace Lingarr.Server.Modules.Integration;

public class RadarrService : IRadarrService
{
    private readonly IIntegrationService _integrationService;

    public RadarrService(IIntegrationService integrationService)
    {
        _integrationService = integrationService;
    }

    /// <inheritdoc />
    public IAsyncEnumerable<RadarrMovie> GetMovies()
    {
        return _integrationService.StreamApiResponse<RadarrMovie>(
            "/api/v3/movie/",
            new IntegrationSettingKeys
            {
                Url = "radarr_url",
                ApiKey = "radarr_api_key"
            });
    }

    /// <inheritdoc />
    public async Task<RadarrMovie?> GetMovie(int moveId)
    {
        return await _integrationService.GetApiResponse<RadarrMovie>(
            $"/api/v3/movie/{moveId}",
            new IntegrationSettingKeys
            {
                Url = "radarr_url",
                ApiKey = "radarr_api_key"
            });
    }
}
