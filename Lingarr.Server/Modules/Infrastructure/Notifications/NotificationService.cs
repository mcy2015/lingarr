using Lingarr.Core.Configuration;
using Lingarr.Core.Data;
using Lingarr.Core.Enum;
using Lingarr.Server.Modules.Settings;
using Microsoft.EntityFrameworkCore;

namespace Lingarr.Server.Modules.Infrastructure.Notifications;

/// <summary>
/// One outbound notification (B11): which event fired, the media it belongs
/// to (when it belongs to one), and the outcome facts the channels serialize.
/// <para>
/// <see cref="Title"/> is an override for events that carry their own
/// subject (the translation request's title, the failing job type name);
/// when null and the media coordinates are present, the service resolves
/// the library title itself — the embed sites stay one-line calls.
/// </para>
/// </summary>
/// <param name="Event">Event key (<see cref="NotificationService.EventDownloaded"/> …).</param>
/// <param name="MediaType">Owning media type; null for job-level events.</param>
/// <param name="MediaId">Owning media id; null for job-level events.</param>
/// <param name="Title">Subject override; null = resolve from the library.</param>
/// <param name="Language">Subtitle language involved, when applicable.</param>
/// <param name="Provider">Source/service involved (subtitle provider, translation service).</param>
/// <param name="Result">Short outcome label ("succeeded" / "failed").</param>
/// <param name="Detail">Readable failure reason for failure events.</param>
public sealed record NotificationEvent(
    string Event,
    MediaType? MediaType = null,
    int? MediaId = null,
    string? Title = null,
    string? Language = null,
    string? Provider = null,
    string Result = "succeeded",
    string? Detail = null);

/// <summary>
/// The wire payload both channels serialize (the batch's locked contract):
/// <c>{event, mediaType, title, language, provider, result, timestamp}</c>
/// in camelCase, plus <c>detail</c> on failure events. The timestamp is
/// ISO 8601 round-trip format (UTC, "O").
/// </summary>
public sealed record NotificationPayload(
    string Event,
    string? MediaType,
    string? Title,
    string? Language,
    string? Provider,
    string Result,
    string Timestamp,
    string? Detail = null)
{
    /// <summary>
    /// Readable one-message text for the Telegram channel (English by
    /// design — the receiver is an external service, not the UI).
    /// </summary>
    public string ToTelegramText()
    {
        var lines = new List<string> { $"Lingarr · {Event}" };
        if (!string.IsNullOrWhiteSpace(Title))
        {
            lines.Add(Title);
        }

        var facts = new List<string>();
        if (!string.IsNullOrWhiteSpace(MediaType))
        {
            facts.Add(MediaType!);
        }

        if (!string.IsNullOrWhiteSpace(Language))
        {
            facts.Add(Language!);
        }

        if (!string.IsNullOrWhiteSpace(Provider))
        {
            facts.Add(Provider!);
        }

        if (facts.Count > 0)
        {
            lines.Add(string.Join(" · ", facts));
        }

        lines.Add($"result: {Result}");
        if (!string.IsNullOrWhiteSpace(Detail))
        {
            lines.Add(Detail);
        }

        return string.Join('\n', lines);
    }
}

/// <summary>
/// Event → channel fan-out (B11): reads the notification settings once,
/// drops events the allow-list does not carry, then offers the payload to
/// the webhook and Telegram channels independently — each channel failure
/// is a warning and never propagates (the same never-block-the-main-flow
/// ruling as the Plex refresh enqueue).
/// <para>
/// <see cref="NotifyAsync"/> never throws except for real caller
/// cancellation, which no embed site passes (they all call it with
/// <c>CancellationToken.None</c> so a failing flow's unwind cannot cut the
/// failure notification).
/// </para>
/// <para>
/// Security contract: the Telegram bot token rides the encrypted settings
/// channel and must never reach a log line — the Telegram failure path
/// redacts the token from the exception text before logging (a connection
/// failure folds the request URL, which carries the token, into the
/// message — the PlexService header-vs-URL ruling, applied where the
/// upstream API forces the token into the URL).
/// </para>
/// </summary>
public class NotificationService
{
    public const string EventDownloaded = "downloaded";
    public const string EventDownloadFailed = "download_failed";
    public const string EventUpgraded = "upgraded";
    public const string EventTranslationFailed = "translation_failed";
    public const string EventJobFailed = "job_failed";

    /// <summary>Cap on <see cref="NotificationEvent.Detail"/> — the row is a notification, not a log.</summary>
    private const int DetailMaxLength = 500;

    private readonly ISettingService _settingService;
    private readonly LingarrDbContext _dbContext;
    private readonly WebhookChannel _webhook;
    private readonly TelegramChannel _telegram;
    private readonly ILogger<NotificationService> _logger;

    public NotificationService(
        ISettingService settingService,
        LingarrDbContext dbContext,
        WebhookChannel webhook,
        TelegramChannel telegram,
        ILogger<NotificationService> logger)
    {
        _settingService = settingService;
        _dbContext = dbContext;
        _webhook = webhook;
        _telegram = telegram;
        _logger = logger;
    }

    /// <summary>
    /// Fans one event out to every configured channel. Best-effort by
    /// contract: the master switch off, an unlisted event, or a channel
    /// without its credentials means no request; a failing channel is a
    /// warning; nothing here ever disturbs the flow that fired the event.
    /// </summary>
    public async Task NotifyAsync(NotificationEvent notification, CancellationToken cancellationToken = default)
    {
        try
        {
            var settings = await _settingService.GetSettings([
                SettingKeys.Notifications.Enabled,
                SettingKeys.Notifications.Events,
                SettingKeys.Notifications.WebhookUrl,
                SettingKeys.Notifications.TelegramChatId
            ]);

            if (settings.GetValueOrDefault(SettingKeys.Notifications.Enabled) != "true")
            {
                return; // the shipped default: outbound notifications never run implicitly
            }

            if (!IsEventAllowed(settings.GetValueOrDefault(SettingKeys.Notifications.Events), notification.Event))
            {
                return;
            }

            var title = notification.Title ?? await ResolveTitleAsync(notification.MediaType, notification.MediaId);
            var detail = notification.Detail is { Length: > DetailMaxLength }
                ? notification.Detail[..DetailMaxLength]
                : notification.Detail;
            var payload = new NotificationPayload(
                notification.Event,
                notification.MediaType?.ToString().ToLowerInvariant(),
                title,
                notification.Language,
                notification.Provider,
                notification.Result,
                DateTimeOffset.UtcNow.ToString("O"),
                detail);

            var webhookUrl = settings.GetValueOrDefault(SettingKeys.Notifications.WebhookUrl);
            if (!string.IsNullOrWhiteSpace(webhookUrl))
            {
                try
                {
                    await _webhook.SendAsync(webhookUrl, payload, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Webhook notification for {Event} to {WebhookUrl} failed.",
                        notification.Event, webhookUrl);
                }
            }

            // The token is read per dispatch through the encrypted channel
            // (never GetSetting) so a settings change takes effect on the
            // next event without a service restart.
            var token = await _settingService.GetEncryptedSetting(SettingKeys.Notifications.TelegramBotToken);
            var chatId = settings.GetValueOrDefault(SettingKeys.Notifications.TelegramChatId);
            if (!string.IsNullOrWhiteSpace(token) && !string.IsNullOrWhiteSpace(chatId))
            {
                try
                {
                    await _telegram.SendAsync(token!, chatId!, payload, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Redact before logging: transport exceptions fold the
                    // request URL (which carries the bot token) into their
                    // message — the raw exception object never reaches a log.
                    _logger.LogWarning(
                        "Telegram notification for {Event} failed: {Error}",
                        notification.Event, Redact(ex.Message, token));
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Notification dispatch for {Event} failed; the triggering flow is unaffected.",
                notification.Event);
        }
    }

    /// <summary>
    /// The allow-list verdict: a blank or missing row falls back to the
    /// seeded default (the two success events); an explicit row is matched
    /// case-insensitively after whitespace trimming. Unknown tokens are
    /// inert — they simply match no fired event. Public static (the
    /// "public test seams, no InternalsVisibleTo" ruling).
    /// </summary>
    public static bool IsEventAllowed(string? configuredEvents, string eventName) =>
        ParseEvents(configuredEvents).Contains(eventName, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Splits the comma-separated allow-list; blank/missing → the seeded
    /// default <c>downloaded,upgraded</c>.
    /// </summary>
    public static string[] ParseEvents(string? configuredEvents)
    {
        var raw = string.IsNullOrWhiteSpace(configuredEvents)
            ? SettingKeys.Notifications.DefaultEvents
            : configuredEvents;
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>
    /// Library title of the event's media — the pipeline's own display
    /// shapes ("Movie" for movies, "Show - S01E02 - Title" for episodes).
    /// Null for job-level events and vanished media rows; a title gap never
    /// blocks the notification.
    /// </summary>
    private async Task<string?> ResolveTitleAsync(MediaType? mediaType, int? mediaId)
    {
        if (mediaType is null || mediaId is null)
        {
            return null;
        }

        if (mediaType == MediaType.Movie)
        {
            return await _dbContext.Movies
                .Where(movie => movie.Id == mediaId)
                .Select(movie => movie.Title)
                .FirstOrDefaultAsync();
        }

        if (mediaType == MediaType.Episode)
        {
            var episode = await _dbContext.Episodes
                .Where(entity => entity.Id == mediaId)
                .Select(entity => new
                {
                    entity.Title,
                    entity.EpisodeNumber,
                    SeasonNumber = entity.Season.SeasonNumber,
                    ShowTitle = entity.Season.Show.Title
                })
                .FirstOrDefaultAsync();
            return episode is null
                ? null
                : $"{episode.ShowTitle} - S{episode.SeasonNumber:D2}E{episode.EpisodeNumber:D2} - {episode.Title}";
        }

        return null;
    }

    /// <summary>Replaces every occurrence of the secret so a sanitized message may be logged.</summary>
    public static string Redact(string text, string? secret) =>
        string.IsNullOrEmpty(secret) ? text : text.Replace(secret, "***");
}
