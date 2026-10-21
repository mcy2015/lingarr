using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lingarr.Server.Modules.Infrastructure.Notifications;

/// <summary>
/// Telegram channel (B11): <c>POST https://api.telegram.org/bot{token}/sendMessage</c>
/// with <c>chat_id</c> + <c>text</c> (the Telegram API forces the bot token
/// into the URL — there is no header form).
/// <para>
/// Security contract: this class never logs. Transport failures can fold
/// the request URL (token included) into the exception message, so the
/// <see cref="NotificationService"/> catch redacts the token before logging
/// — the PlexService token-in-header ruling, adapted to the one upstream
/// that does not offer a header.
/// </para>
/// </summary>
public class TelegramChannel
{
    private const string ApiBaseUrl = "https://api.telegram.org/bot";

    private readonly HttpClient _httpClient;

    public TelegramChannel(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <summary>Sends the payload as one Telegram message; throws when Telegram rejects it.</summary>
    public async Task SendAsync(
        string botToken, string chatId, NotificationPayload payload, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(
            new TelegramMessage(chatId, payload.ToTelegramText()),
            TelegramMessage.Options);

        // The content deliberately stays undisposed (the webhook channel's
        // comment): test doubles read the recorded request body afterwards.
        var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync(
            $"{ApiBaseUrl}{botToken}/sendMessage", content, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // Status only — the URL (and with it the token) stays out of the message.
            throw new InvalidOperationException(
                $"Telegram sendMessage answered {(int)response.StatusCode} ({response.StatusCode}).");
        }
    }

    /// <summary>
    /// The sendMessage request body. The API's field names are snake_case
    /// (<c>chat_id</c>), so the properties carry explicit names — no naming
    /// policy that would camelCase them into a different contract.
    /// </summary>
    private sealed record TelegramMessage(
        [property: JsonPropertyName("chat_id")] string ChatId,
        [property: JsonPropertyName("text")] string Text)
    {
        public static readonly JsonSerializerOptions Options = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }
}
