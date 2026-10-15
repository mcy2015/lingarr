using Lingarr.Server.Models;

namespace Lingarr.Server.Modules.Integration;

public interface IIntegrationService
{
    /// <summary>
    /// Asynchronously sends an HTTP GET request to the specified API endpoint and deserializes the response.
    /// </summary>
    /// <typeparam name="T">The expected type of the response data.</typeparam>
    /// <param name="apiUrl">The relative API endpoint URL to send the request to.</param>
    /// <param name="settingKeys">The integration settings containing the base URL and API key.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result contains the deserialized response
    /// of type <typeparamref name="T"/>, or <c>null</c> if the request fails or the deserialization is unsuccessful.
    /// </returns>
    /// <exception cref="HttpRequestException">Thrown when the API request fails.</exception>
    Task<T?> GetApiResponse<T>(string apiUrl, IntegrationSettingKeys settingKeys);

    /// <summary>
    /// Streams a JSON array response element by element. The response is read with
    /// <see cref="HttpCompletionOption.ResponseHeadersRead"/> and the top-level array is
    /// deserialized incrementally, so full-library payloads (hundreds of MB at 100k media)
    /// never need to be materialized as a single managed object graph.
    /// </summary>
    /// <typeparam name="T">The element type of the response array.</typeparam>
    /// <param name="apiUrl">The relative API endpoint URL to send the request to; must return a JSON array.</param>
    /// <param name="settingKeys">The integration settings containing the base URL and API key.</param>
    /// <param name="cancellationToken">Propagates notification that the stream should be cancelled.</param>
    /// <returns>An async enumerable of the deserialized array elements.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the integration settings are not configured.</exception>
    /// <exception cref="HttpRequestException">Thrown when the API request fails.</exception>
    /// <exception cref="System.Text.Json.JsonException">Thrown when the payload is not a JSON array of <typeparamref name="T"/>.</exception>
    IAsyncEnumerable<T> StreamApiResponse<T>(
        string apiUrl,
        IntegrationSettingKeys settingKeys,
        CancellationToken cancellationToken = default);
}
