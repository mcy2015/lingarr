using Lingarr.Server.Modules.Infrastructure;
using Lingarr.Server.Models;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Lingarr.Server.Modules.Integration;

public class IntegrationService : IIntegrationService
{
    private readonly HttpClient _httpClient;
    private readonly IIntegrationSettingsProvider _settingsProvider;

    public IntegrationService(HttpClient httpClient, IIntegrationSettingsProvider settingsProvider)
    {
        _httpClient = httpClient;
        _settingsProvider = settingsProvider;
    }

    public async Task<T?> GetApiResponse<T>(string apiUrl, IntegrationSettingKeys settingKeys)
    {
        var settings = await _settingsProvider.GetSettings(settingKeys);
        if (settings == null) return default;

        var separator = apiUrl.Contains("?") ? "&" : "?";
        var url = $"{settings.Url}{apiUrl}{separator}apikey={settings.ApiKey}";

        var response = await SendGuardedAsync(
            _httpClient, url, HttpCompletionOption.ResponseContentRead, CancellationToken.None);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException(
                $"Integration request failed: {response.StatusCode}: {errorContent}",
                inner: null,
                statusCode: response.StatusCode);
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync();
        return await JsonSerializer.DeserializeAsync<T>(responseStream);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<T> StreamApiResponse<T>(
        string apiUrl,
        IntegrationSettingKeys settingKeys,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var settings = await _settingsProvider.GetSettings(settingKeys);
        if (settings == null)
        {
            // Unlike GetApiResponse (which yields null), an async stream cannot express
            // "not configured" as an empty sequence — an empty stream would otherwise be
            // indistinguishable from an empty library and could cascade into orphan
            // removal wiping the local database.
            throw new InvalidOperationException(
                $"Integration settings for '{settingKeys.Url}' are not configured correctly.");
        }

        var separator = apiUrl.Contains("?") ? "&" : "?";
        var url = $"{settings.Url}{apiUrl}{separator}apikey={settings.ApiKey}";

        // ResponseHeadersRead: the body is consumed while it is being deserialized,
        // instead of being buffered into memory first.
        using var response = await SendGuardedAsync(
            _httpClient, url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"Integration request failed: {response.StatusCode}: {errorContent}",
                inner: null,
                statusCode: response.StatusCode);
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);

        // Incremental top-level array deserialization: each element is yielded as soon
        // as it has been read. Disposing the enumerator (break, exception, cancellation)
        // disposes the response stream via the await-using scopes above.
        await foreach (var item in JsonSerializer.DeserializeAsyncEnumerable<T>(
                           responseStream, cancellationToken: cancellationToken))
        {
            yield return item;
        }
    }

    /// <summary>
    /// Sends a *arr request and guarantees that whatever escapes to callers —
    /// and from there into the now-persistent log files — never carries the
    /// plaintext <c>apikey=</c> query string: HttpClient folds the request
    /// URI into connection-failure exception messages. A sensitive exception
    /// is rethrown masked (type and status code preserved, so catch guards
    /// like <c>ex.StatusCode == NotFound</c> keep firing); a clean one is
    /// rethrown untouched. The request itself is constructed identically —
    /// this batch changes observability, not behavior.
    /// </summary>
    private static async Task<HttpResponseMessage> SendGuardedAsync(
        HttpClient httpClient,
        string url,
        HttpCompletionOption completionOption,
        CancellationToken cancellationToken)
    {
        try
        {
            return await httpClient.GetAsync(url, completionOption, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            var masked = LogSanitizer.MaskHttpRequestException(ex);
            if (masked is not null)
            {
                throw masked;
            }

            throw;
        }
    }
}
