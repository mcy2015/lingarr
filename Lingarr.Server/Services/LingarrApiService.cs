using System.Text.Json;
using Lingarr.Core;
using Lingarr.Server.Interfaces.Services;
using Microsoft.Extensions.Caching.Memory;

namespace Lingarr.Server.Services;

public class LingarrApiService : ILingarrApiService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<LingarrApiService> _logger;
    private readonly IMemoryCache _cache;
    private readonly string _baseUrl;
    private const string CacheKeyLatestVersion = "LingarrApi_LatestVersion";

    public LingarrApiService(
        IHttpClientFactory httpClientFactory,
        ILogger<LingarrApiService> logger,
        IMemoryCache cache)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _cache = cache;

        _baseUrl = new UriBuilder
        {
            Scheme = Uri.UriSchemeHttps,
            Host = $"api.{LingarrVersion.Name.ToLower()}.com"
        }.Uri.ToString().TrimEnd('/');
    }

    public async Task<string?> GetLatestVersion()
    {
        // Check cache first
        if (_cache.TryGetValue(CacheKeyLatestVersion, out string? cachedVersion))
        {
            _logger.LogDebug("Returning cached version information from Lingarr API");
            return cachedVersion;
        }

        try
        {
            var httpClient = _httpClientFactory.CreateClient();
            httpClient.DefaultRequestHeaders.Add("User-Agent", $"{LingarrVersion.Name}/{LingarrVersion.Number}");
            httpClient.DefaultRequestHeaders.Add("x-lingarr-platform", LingarrVersion.Platform);

            var response = await httpClient.GetAsync($"{_baseUrl}/version/latest");

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to get latest version from Lingarr API: {StatusCode}",
                    response.StatusCode);
                return null;
            }

            var content = await response.Content.ReadAsStringAsync();
            var versionResponse = JsonSerializer.Deserialize<VersionResponse>(content,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

            if (versionResponse?.Version != null)
            {
                var cacheOptions = new MemoryCacheEntryOptions()
                    .SetAbsoluteExpiration(TimeSpan.FromHours(24));
                _cache.Set(CacheKeyLatestVersion, versionResponse.Version, cacheOptions);

                _logger.LogInformation("Retrieved latest version: {Version}", versionResponse.Version);
                return versionResponse.Version;
            }

            _logger.LogWarning("Lingarr API returned empty version");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch latest version from Lingarr API");
            return null;
        }
    }

    private class VersionResponse
    {
        public string? Version { get; set; }
    }
}
