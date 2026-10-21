using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lingarr.Server.Modules.Infrastructure.Notifications;

/// <summary>
/// Generic webhook channel (B11): POSTs the locked JSON contract —
/// <c>{event, mediaType, title, language, provider, result, timestamp}</c>
/// camelCase, <c>timestamp</c> ISO 8601, <c>detail</c> present only on
/// failure events — to the user-configured receiver URL.
/// <para>
/// The channel throws on any failure (transport or non-2xx); the
/// <see cref="NotificationService"/> catch turns that into a warning. The
/// URL is a user-provided destination like the Plex base URL and may appear
/// in that warning; it never carries the Telegram bot token.
/// </para>
/// </summary>
public class WebhookChannel
{
    private static readonly JsonSerializerOptions PayloadOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly HttpClient _httpClient;

    public WebhookChannel(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <summary>POSTs the event payload as JSON; throws when the receiver rejects it.</summary>
    public async Task SendAsync(string webhookUrl, NotificationPayload payload, CancellationToken cancellationToken)
    {
        // The content deliberately stays undisposed: it belongs to the
        // request, and test doubles record the request for body assertions
        // after the send (nothing here holds an unmanaged resource).
        var content = new StringContent(
            JsonSerializer.Serialize(payload, PayloadOptions), Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync(webhookUrl, content, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"The webhook receiver answered {(int)response.StatusCode} ({response.StatusCode}).");
        }
    }
}
