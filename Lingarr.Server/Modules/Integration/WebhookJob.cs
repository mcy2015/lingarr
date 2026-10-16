using Hangfire;
using Lingarr.Core.Configuration;
using Lingarr.Core.Data;
using Lingarr.Core.Enum;
using Lingarr.Server.Models.Webhooks;
using Lingarr.Server.Modules.Settings;
using Microsoft.EntityFrameworkCore;
using Lingarr.Server.Modules.Media;

namespace Lingarr.Server.Modules.Integration;

public class WebhookJob
{
    private readonly LingarrDbContext _dbContext;
    private readonly IMediaService _mediaService;
    private readonly IMediaSubtitleProcessor _mediaSubtitleProcessor;
    private readonly Lingarr.Server.Modules.SubtitleSearch.Queue.SubtitleSearchQueueService _subtitleQueue;
    private readonly ISettingService _settingService;
    private readonly ILogger<WebhookJob> _logger;

    public WebhookJob(
        LingarrDbContext dbContext,
        IMediaService mediaService,
        IMediaSubtitleProcessor mediaSubtitleProcessor,
        Lingarr.Server.Modules.SubtitleSearch.Queue.SubtitleSearchQueueService subtitleQueue,
        ISettingService settingService,
        ILogger<WebhookJob> logger)
    {
        _dbContext = dbContext;
        _mediaService = mediaService;
        _mediaSubtitleProcessor = mediaSubtitleProcessor;
        _subtitleQueue = subtitleQueue;
        _settingService = settingService;
        _logger = logger;
    }

    [DisableConcurrentExecution(timeoutInSeconds: 2 * 60)]
    [AutomaticRetry(Attempts = 3)]
    [Queue("webhook")]
    public async Task ProcessRadarrWebhook(RadarrWebhookPayload payload)
    {
        if (payload.Movie == null)
        {
            _logger.LogWarning("Radarr webhook payload has no movie data. Skipping.");
            return;
        }

        try
        {
            var movieId = await _mediaService.GetMovieIdOrSyncFromRadarrMovieId(payload.Movie.Id);
            if (movieId == 0)
            {
                _logger.LogWarning("Failed to sync or find movie with Radarr ID {RadarrId}. Movie may not have a file yet.",
                    payload.Movie.Id);
                return;
            }

            var movie = await _dbContext.Movies.FirstOrDefaultAsync(m => m.Id == movieId);
            if (movie == null)
            {
                _logger.LogError("Movie with ID {MovieId} not found in database after sync", movieId);
                return;
            }

            await _mediaSubtitleProcessor.ProcessMedia(movie, MediaType.Movie);

            // M7: a freshly imported movie goes to the front of the subtitle
            // search queue with a 48h boost (PRD card A priority semantics).
            // Two gates close the QA3 P2 holes: only the Download event means
            // "a file just landed" (Grab/Rename/Test must not churn the
            // queue), and the master switch (subtitle_search_enabled) is the
            // rollback lever — with it off, a webhook never re-arms the
            // queue. Legacy senders without an eventType keep the old
            // behaviour (boost) so the field rollout breaks no one.
            if (IsDownloadEvent(payload.EventType) && movie.NeedsTranslation && !movie.NoSubtitleNeeded)
            {
                await BoostSubtitleSearchSafelyAsync(MediaType.Movie, movie.Id, movie.Title);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing Radarr webhook for movie {MovieTitle} (Radarr ID: {RadarrId})",
                payload.Movie.Title, payload.Movie.Id);
            throw;
        }
    }

    [DisableConcurrentExecution(timeoutInSeconds: 2 * 60)]
    [AutomaticRetry(Attempts = 3)]
    [Queue("webhook")]
    public async Task ProcessSonarrWebhook(SonarrWebhookPayload payload)
    {
        if (payload.Series == null || payload.Episodes == null || !payload.Episodes.Any())
        {
            _logger.LogWarning("Sonarr webhook payload has no series or episode data. Skipping.");
            return;
        }

        try
        {
            foreach (var episode in payload.Episodes)
            {
                var episodeId = await _mediaService.GetEpisodeIdOrSyncFromSonarrEpisodeId(episode.Id);
                if (episodeId == 0)
                {
                    _logger.LogWarning("Failed to sync or find episode with Sonarr ID {SonarrEpisodeId}. Episode may not have a file yet.",
                        episode.Id);
                    continue;
                }
                
                var episodeEntity = await _dbContext.Episodes.FirstOrDefaultAsync(e => e.Id == episodeId);
                if (episodeEntity == null)
                {
                    _logger.LogError("Episode with ID {EpisodeId} not found in database after sync", episodeId);
                    continue;
                }

                await _mediaSubtitleProcessor.ProcessMedia(episodeEntity, MediaType.Episode);
            }

            _logger.LogInformation("Completed processing Sonarr webhook for series: {SeriesTitle}", payload.Series.Title);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing Sonarr webhook for series {SeriesTitle} (Sonarr ID: {SonarrId})",
                payload.Series.Title, payload.Series.Id);
            throw;
        }
    }

    /// <summary>
    /// Radarr's "a file just landed" event. A missing/blank eventType keeps
    /// the boost (legacy senders and simple curl probes never sent one) —
    /// only an explicit non-Download value (Grab, Rename, Test, …) holds the
    /// queue back.
    /// </summary>
    private static bool IsDownloadEvent(string? eventType)
        => string.IsNullOrWhiteSpace(eventType)
           || eventType.Trim().Equals("Download", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The webhook boost, gated by the master switch and isolated from the
    /// import handling (QA3 P2-3/P3-11): a settings hiccup or a queue-write
    /// failure logs and lets the job succeed — media import already ran and
    /// must not be retried (nor fail) because of the subtitle side-effect.
    /// </summary>
    private async Task BoostSubtitleSearchSafelyAsync(MediaType mediaType, int mediaId, string title)
    {
        try
        {
            var settings = await _settingService.GetSettings([SettingKeys.SubtitleSearch.Enabled]);
            if (settings.GetValueOrDefault(SettingKeys.SubtitleSearch.Enabled) != "true")
            {
                _logger.LogInformation(
                    "Subtitle search is disabled; skipping the webhook boost for {MediaType} {MediaId} ({Title}).",
                    mediaType, mediaId, title);
                return;
            }

            await _subtitleQueue.EnqueueBoostedAsync(mediaType, mediaId);
            _logger.LogInformation(
                "{MediaType} {Title} enqueued for subtitle search with webhook boost.",
                mediaType, title);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Subtitle-search webhook boost failed for {MediaType} {MediaId} ({Title}); the import handling is unaffected.",
                mediaType, mediaId, title);
        }
    }
}
