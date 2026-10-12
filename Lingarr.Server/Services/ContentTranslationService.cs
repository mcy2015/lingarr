using Lingarr.Contracts.Exceptions;
using Lingarr.Contracts.Models;
using Lingarr.Contracts.Translation;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Core.Configuration;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Interfaces.Services.Translation;
using Lingarr.Server.Models.Batch.Response;
using Lingarr.Server.Models.FileSystem;
using Lingarr.Server.Services.Subtitle;
using Lingarr.Server.Services.Translation;
using Microsoft.EntityFrameworkCore;

namespace Lingarr.Server.Services;

/// <summary>
/// Job-less ("async content") translations used by external API integrations (ex. Bazarr).
/// Runs inline within the request and registers its cancellation source in the
/// <see cref="IAsyncTranslationRegistry"/> so it can be cancelled like queued jobs.
/// </summary>
public class ContentTranslationService : IContentTranslationService
{
    private readonly LingarrDbContext _dbContext;
    private readonly IAsyncTranslationRegistry _asyncTranslationRegistry;
    private readonly ITranslationRequestQueryService _queryService;
    private readonly IMediaService _mediaService;
    private readonly ITranslationPipeline _translationPipeline;
    private readonly ITranslationRequestEventService _eventService;
    private readonly IProgressService _progressService;
    private readonly IStatisticsService _statisticsService;
    private readonly ILogger<ContentTranslationService> _logger;

    public ContentTranslationService(
        LingarrDbContext dbContext,
        IAsyncTranslationRegistry asyncTranslationRegistry,
        ITranslationRequestQueryService queryService,
        IMediaService mediaService,
        ITranslationPipeline translationPipeline,
        ITranslationRequestEventService eventService,
        IProgressService progressService,
        IStatisticsService statisticsService,
        ILogger<ContentTranslationService> logger)
    {
        _dbContext = dbContext;
        _asyncTranslationRegistry = asyncTranslationRegistry;
        _queryService = queryService;
        _mediaService = mediaService;
        _translationPipeline = translationPipeline;
        _eventService = eventService;
        _progressService = progressService;
        _statisticsService = statisticsService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<BatchTranslatedLine[]> TranslateContentAsync(
        TranslateAbleSubtitleContent translateAbleContent,
        CancellationToken parentCancellationToken)
    {
        // Resolve internal media id from the client's ArrMediaId
        var mediaId = await GetMediaId(translateAbleContent.ArrMediaId, translateAbleContent.MediaType);

        // Derive canonical title from the media lookup (FormatMediaTitle queries the DB for
        // the resolved movie/episode title). The client's Title becomes an optional override.
        var canonicalTitle = await FormatMediaTitle(mediaId, translateAbleContent.MediaType);
        var contentTitle = !string.IsNullOrWhiteSpace(translateAbleContent.Title?.Trim())
            ? translateAbleContent.Title.Trim()
            : canonicalTitle;

        var translationRequest = new TranslationRequest
        {
            MediaId = mediaId,
            Title = contentTitle,
            SourceLanguage = translateAbleContent.SourceLanguage,
            TargetLanguage = translateAbleContent.TargetLanguage,
            SubtitleToTranslate = translateAbleContent.SourceSubtitlePath,
            TranslatedSubtitle = translateAbleContent.TranslatedSubtitlePath,
            MediaType = translateAbleContent.MediaType,
            Status = TranslationStatus.InProgress,
            JobType = TranslationJobType.Translation
        };

        // Link cancel token with new source to be able to cancel the async translation
        var asyncTranslationCancellationTokenSource = new CancellationTokenSource();
        var cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(parentCancellationToken, asyncTranslationCancellationTokenSource.Token);
        var cancellationToken = cancellationTokenSource.Token;

        try
        {
            // Resolve settings + service chain first so a missing configuration fails
            // before any request row is created.
            var dispatch = await _translationPipeline.PrepareAsync();

            // Skip if an active content-translation row for this media+target already exists.
            var existingId = await _dbContext.TranslationRequests
                .Where(r => r.MediaId == translationRequest.MediaId
                         && r.MediaType == translationRequest.MediaType
                         && r.Title == translationRequest.Title
                         && r.SourceLanguage == translationRequest.SourceLanguage
                         && r.TargetLanguage == translationRequest.TargetLanguage
                         && (r.Status == TranslationStatus.Pending || r.Status == TranslationStatus.InProgress))
                .Select(r => (int?)r.Id)
                .FirstOrDefaultAsync(cancellationToken);

            if (existingId != null)
            {
                _logger.LogInformation(
                    "Duplicate content-translation request skipped for mediaId {MediaId} ({MediaType}) '{Title}' {Src}->{Tgt} (existing id {Id}).",
                    translationRequest.MediaId, translationRequest.MediaType,
                    translationRequest.Title,
                    translationRequest.SourceLanguage, translationRequest.TargetLanguage, existingId.Value);
                return Array.Empty<BatchTranslatedLine>();
            }

            // Add TranslationRequest
            _dbContext.TranslationRequests.Add(translationRequest);
            await _dbContext.SaveChangesAsync(cancellationToken);
            await _eventService.LogEvent(translationRequest.Id, TranslationStatus.InProgress);
            await _queryService.UpdateActiveCount();

            // Add translation as a async translation request with cancellation source
            _asyncTranslationRegistry.Register(translationRequest.Id, cancellationTokenSource);


            var subtitleItems = translateAbleContent.Lines.Select(item => new SubtitleItem
            {
                Position = item.Position,
                Lines = new List<string> { item.Line },
                PlaintextLines = new List<string> { item.Line }
            }).ToList();

            // Execute via the shared pipeline; content lines carry no timing so the
            // stacked-line merge is disabled and batch requires more than one line.
            var run = await _translationPipeline.RunAsync(
                new TranslationRun(
                    translationRequest,
                    subtitleItems,
                    MergeStackedLines: false,
                    BatchRequiresMultipleLines: true),
                dispatch,
                cancellationToken);

            BatchTranslatedLine[] results;
            if (run.UsedBatchTranslation)
            {
                results = subtitleItems.Select(subtitle => new BatchTranslatedLine
                {
                    Position = subtitle.Position,
                    Line = string.Join(" ", subtitle.TranslatedLines)
                }).ToArray();

                _logger.LogInformation("Batch translation completed successfully. Processed {ResultCount} translated lines", results.Length);
            }
            else
            {
                results = subtitleItems.Select(subtitle =>
                {
                    var translatedText = string.Join(" ", subtitle.TranslatedLines);
                    return new BatchTranslatedLine
                    {
                        Position = subtitle.Position,
                        Line = run.StripSubtitleFormatting
                            ? SubtitleFormatterService.RemoveMarkup(translatedText)
                            : translatedText
                    };
                }).ToArray();

                _logger.LogInformation("Individual line translation completed. Processed {ResultCount} lines", results.Length);
            }

            await HandleAsyncTranslationCompletion(translationRequest, run.ServiceType, run.PrimaryService, results, cancellationToken);
            return results;
        }
        catch (OperationCanceledException ex)
        {
            // ExecuteUpdateAsync bypasses change-tracking so a concurrent write cannot abort this save.
            var now = DateTimeOffset.UtcNow;
            await _dbContext.TranslationRequests
                .Where(r => r.Id == translationRequest.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Status, TranslationStatus.Cancelled)
                    .SetProperty(r => r.ErrorMessage, ex.Message)
                    .SetProperty(r => r.CompletedAt, now)
                    .SetProperty(r => r.UpdatedAt, now));
            translationRequest.CompletedAt = now;
            translationRequest.Status = TranslationStatus.Cancelled;
            translationRequest.ErrorMessage = ex.Message;
            await _eventService.LogEvent(translationRequest.Id, TranslationStatus.Cancelled, ex.Message);
            await _queryService.UpdateActiveCount();
            await _progressService.Emit(translationRequest, 0);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error translating subtitle content");
            var now = DateTimeOffset.UtcNow;
            await _dbContext.TranslationRequests
                .Where(r => r.Id == translationRequest.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Status, TranslationStatus.Failed)
                    .SetProperty(r => r.ErrorMessage, ex.Message)
                    .SetProperty(r => r.StackTrace, ex.ToString())
                    .SetProperty(r => r.CompletedAt, now)
                    .SetProperty(r => r.UpdatedAt, now));
            translationRequest.CompletedAt = now;
            translationRequest.Status = TranslationStatus.Failed;
            translationRequest.ErrorMessage = ex.Message;
            translationRequest.StackTrace = ex.ToString();
            await _eventService.LogEvent(translationRequest.Id, TranslationStatus.Failed, ex.Message);
            await _queryService.UpdateActiveCount();
            await _progressService.Emit(translationRequest, 0);
            throw;
        }
        finally
        {
            // Remove async translation from async translation jobs
            _asyncTranslationRegistry.TryRemove(translationRequest.Id, out _);
        }
    }

    /// <summary>
    /// Get the Lingarr's media id for the Episode or the Show
    /// </summary>
    private async Task<int> GetMediaId(int arrMediaId, MediaType mediaType)
    {
        switch (mediaType)
        {
            case MediaType.Episode:
                return await _mediaService.GetEpisodeIdOrSyncFromSonarrEpisodeId(arrMediaId);
            case MediaType.Movie:
                return await _mediaService.GetMovieIdOrSyncFromRadarrMovieId(arrMediaId);
            default:
                _logger.LogWarning("Unsupported media type: {MediaType} for translate content async", mediaType);
                return 0;
        }
    }

    /// <summary>
    /// Handles a successful async translation job
    /// </summary>
    private async Task HandleAsyncTranslationCompletion(
        TranslationRequest translationRequest,
        string serviceType,
        ITranslationService translationService,
        BatchTranslatedLine[] results,
        CancellationToken cancellationToken)
    {
        await _statisticsService.UpdateTranslationStatisticsFromLines(translationRequest, serviceType, translationService.ModelName, results);

        var now = DateTimeOffset.UtcNow;
        await _dbContext.TranslationRequests
            .Where(r => r.Id == translationRequest.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, TranslationStatus.Completed)
                .SetProperty(r => r.CompletedAt, now)
                .SetProperty(r => r.UpdatedAt, now), cancellationToken);
        translationRequest.CompletedAt = now;
        translationRequest.Status = TranslationStatus.Completed;
        await _eventService.LogEvent(translationRequest.Id, TranslationStatus.Completed);
        await _queryService.UpdateActiveCount();
        await _progressService.Emit(translationRequest, 100); // Tells the frontend to update translation request to a finished state
    }

    /// <inheritdoc />
    public async Task<string> FormatMediaTitle(int mediaId, MediaType mediaType)
    {
        switch (mediaType)
        {
            case MediaType.Movie:
                var movie = await _dbContext.Movies
                    .FirstOrDefaultAsync(m => m.Id == mediaId);
                return movie?.Title ?? "Unknown Movie";

            case MediaType.Episode:
                var episode = await _dbContext.Episodes
                    .Include(e => e.Season)
                    .ThenInclude(s => s.Show)
                    .FirstOrDefaultAsync(e => e.Id == mediaId);

                if (episode == null)
                    return "Unknown Episode";

                // Format: "Show Title - S01E02 - Episode Title"
                return $"{episode.Season.Show.Title} - " +
                       $"S{episode.Season.SeasonNumber:D2}E{episode.EpisodeNumber:D2} - " +
                       $"{episode.Title}";

            default:
                throw new ArgumentException($"Unsupported media type: {mediaType}");
        }
    }
}
