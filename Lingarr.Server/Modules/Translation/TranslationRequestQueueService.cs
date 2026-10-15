using DeepL; // NotFoundException below binds to DeepL.NotFoundException, as in the original god-service
using Lingarr.Contracts.Models;
using Lingarr.Contracts.Exceptions;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Core.Configuration;
using Lingarr.Server.Models.Api;
using Lingarr.Server.Models.FileSystem;
using Microsoft.EntityFrameworkCore;
using Lingarr.Server.Modules.Media;
using Lingarr.Server.Modules.Settings;
using Lingarr.Server.Modules.Subtitle;

namespace Lingarr.Server.Modules.Translation;

/// <summary>
/// Lifecycle operations on translation requests: creation, enqueueing, cancellation,
/// removal, retry, resume and status updates. Hangfire interaction goes through
/// <see cref="IJobQueue"/>, cancellation of job-less content translations through
/// <see cref="IAsyncTranslationRegistry"/>.
/// </summary>
public class TranslationRequestQueueService : ITranslationRequestQueueService
{
    private readonly LingarrDbContext _dbContext;
    private readonly IJobQueue _jobQueue;
    private readonly IAsyncTranslationRegistry _asyncTranslationRegistry;
    private readonly ITranslationRequestQueryService _queryService;
    private readonly IContentTranslationService _contentTranslationService;
    private readonly ITranslationRequestEventService _eventService;
    private readonly IProgressService _progressService;
    private readonly ISettingService _settingService;
    private readonly ISubtitleService _subtitleService;
    private readonly IEmbeddedTrackService _embeddedTrackService;
    private readonly ILogger<TranslationRequestQueueService> _logger;

    public TranslationRequestQueueService(
        LingarrDbContext dbContext,
        IJobQueue jobQueue,
        IAsyncTranslationRegistry asyncTranslationRegistry,
        ITranslationRequestQueryService queryService,
        IContentTranslationService contentTranslationService,
        ITranslationRequestEventService eventService,
        IProgressService progressService,
        ISettingService settingService,
        ISubtitleService subtitleService,
        IEmbeddedTrackService embeddedTrackService,
        ILogger<TranslationRequestQueueService> logger)
    {
        _dbContext = dbContext;
        _jobQueue = jobQueue;
        _asyncTranslationRegistry = asyncTranslationRegistry;
        _queryService = queryService;
        _contentTranslationService = contentTranslationService;
        _eventService = eventService;
        _progressService = progressService;
        _settingService = settingService;
        _subtitleService = subtitleService;
        _embeddedTrackService = embeddedTrackService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<int> CreateRequest(TranslateAbleSubtitle translateAbleSubtitle)
    {
        var mediaTitle = await _contentTranslationService.FormatMediaTitle(translateAbleSubtitle.MediaId, translateAbleSubtitle.MediaType);
        var translationRequest = new TranslationRequest
        {
            MediaId = translateAbleSubtitle.MediaId,
            Title = mediaTitle,
            SourceLanguage = translateAbleSubtitle.SourceLanguage,
            TargetLanguage = translateAbleSubtitle.TargetLanguage,
            SubtitleToTranslate = translateAbleSubtitle.SubtitlePath,
            MediaType = translateAbleSubtitle.MediaType,
            Status = TranslationStatus.Pending
        };

        return await EnqueueRequest(translationRequest);
    }

    /// <inheritdoc />
    public async Task<int> CreateImportRequest(
        string subtitlePath,
        string title,
        string sourceLanguage,
        string targetLanguage,
        int? mediaId,
        MediaType mediaType,
        string? contentHash = null,
        bool isBilingual = false)
    {
        // Bound imports carry the resolved Movie/Episode id so output naming and
        // the queue UI treat them like library media; Source = Import still marks
        // them as manual imports.
        var translationRequest = new TranslationRequest
        {
            MediaId = mediaId,
            Title = title,
            SourceLanguage = sourceLanguage,
            TargetLanguage = targetLanguage,
            SubtitleToTranslate = subtitlePath,
            MediaType = mediaType,
            Source = TranslationRequestSource.Import,
            Status = TranslationStatus.Pending,
            ContentHash = contentHash,
            IsBilingual = isBilingual
        };

        return await EnqueueRequest(translationRequest);
    }

    /// <inheritdoc />
    public async Task<int> CreateEmbeddedTrackRequest(
        MediaType mediaType,
        int mediaId,
        int streamIndex,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        if (mediaType is not (MediaType.Movie or MediaType.Episode))
        {
            throw new InvalidOperationException(
                $"Embedded tracks can only be translated for movies and episodes, not {mediaType}.");
        }

        var media = await ResolveMedia(mediaType, mediaId, cancellationToken)
                    ?? throw new NotFoundException($"No {mediaType} with id {mediaId} exists.");
        if (string.IsNullOrEmpty(media.Path) || string.IsNullOrEmpty(media.FileName))
        {
            throw new InvalidOperationException(
                $"{mediaType} {mediaId} has no media path on record, so its embedded tracks cannot be read.");
        }

        var tracks = await _dbContext.MediaSubtitleTracks
            .Where(track => track.MediaType == mediaType
                            && track.MediaId == mediaId
                            && track.StreamIndex >= 0)
            .ToListAsync(cancellationToken);
        var track = tracks.FirstOrDefault(candidate => candidate.StreamIndex == streamIndex)
                    ?? throw new NotFoundException(
                        $"No embedded track with stream index {streamIndex} is recorded for {mediaType} {mediaId}. Run the embedded track scan first.");
        if (!track.IsTextBased)
        {
            throw new InvalidOperationException(
                $"Embedded stream {streamIndex} ({track.Codec}) is image-based and cannot be translated.");
        }
        if (string.IsNullOrEmpty(track.Language))
        {
            throw new InvalidOperationException(
                $"The language of embedded stream {streamIndex} ('{track.RawLanguage ?? "unmarked"}') could not be recognized, so it cannot serve as a translation source.");
        }

        // Target-language gate: the union of external subtitle languages and
        // embedded track languages of this media item.
        var externalSubtitles = await _subtitleService.GetSubtitles(media.Path, media.FileName);
        var availableLanguages = EmbeddedTrackService.BuildAvailableLanguages(externalSubtitles, tracks);
        if (availableLanguages.Contains(targetLanguage.ToLowerInvariant()))
        {
            throw new TargetLanguageAlreadyPresentException(
                $"'{media.Title}' already has {targetLanguage.ToLowerInvariant()} subtitles (external file or embedded track) — translation was not queued.");
        }

        // Extract (cached) and place the source next to the video so the
        // output naming and write-back behave exactly like a library request.
        var sourcePath = await _embeddedTrackService.PrepareTranslationSource(
            mediaType, mediaId, streamIndex, cancellationToken);

        return await CreateRequest(new TranslateAbleSubtitle
        {
            MediaId = mediaId,
            MediaType = mediaType,
            SubtitlePath = sourcePath,
            SourceLanguage = track.Language,
            TargetLanguage = targetLanguage,
            SubtitleFormat = ".srt"
        });
    }

    private sealed record EmbeddedMedia(string Path, string? FileName, string Title);

    private async Task<EmbeddedMedia?> ResolveMedia(
        MediaType mediaType,
        int mediaId,
        CancellationToken cancellationToken)
    {
        switch (mediaType)
        {
            case MediaType.Movie:
                var movie = await _dbContext.Movies
                    .Where(item => item.Id == mediaId)
                    .Select(item => new { item.Path, item.FileName, item.Title })
                    .FirstOrDefaultAsync(cancellationToken);
                return movie == null ? null : new EmbeddedMedia(movie.Path!, movie.FileName, movie.Title);

            case MediaType.Episode:
                var episode = await _dbContext.Episodes
                    .Where(item => item.Id == mediaId)
                    .Select(item => new { item.Path, item.FileName, item.Title })
                    .FirstOrDefaultAsync(cancellationToken);
                return episode == null ? null : new EmbeddedMedia(episode.Path!, episode.FileName, episode.Title);

            default:
                return null;
        }
    }

    /// <inheritdoc />
    public async Task<string?> PauseTranslationRequest(TranslationRequest pauseRequest)
    {
        if (pauseRequest.Status != TranslationStatus.InProgress)
        {
            return "Only in-progress requests can be paused";
        }

        if (!_asyncTranslationRegistry.TryPause(pauseRequest.Id))
        {
            return "Request is already paused";
        }

        pauseRequest.Status = TranslationStatus.Paused;
        await _dbContext.SaveChangesAsync();
        await _progressService.Emit(pauseRequest, pauseRequest.Progress);
        await _eventService.LogEvent(pauseRequest.Id, TranslationStatus.Paused);
        await _queryService.UpdateActiveCount();
        return null;
    }

    /// <inheritdoc />
    public async Task<string?> ResumePausedTranslationRequest(TranslationRequest resumeRequest)
    {
        if (resumeRequest.Status != TranslationStatus.Paused)
        {
            return "Only paused requests can be resumed";
        }

        if (!_asyncTranslationRegistry.TryResume(resumeRequest.Id))
        {
            // the job already restarted (e.g. app restart cleared the in-memory gate)
            resumeRequest.Status = TranslationStatus.InProgress;
            await _dbContext.SaveChangesAsync();
            return null;
        }

        resumeRequest.Status = TranslationStatus.InProgress;
        await _dbContext.SaveChangesAsync();
        await _progressService.Emit(resumeRequest, resumeRequest.Progress);
        await _eventService.LogEvent(resumeRequest.Id, TranslationStatus.InProgress);
        return null;
    }

    private async Task<int> EnqueueRequest(TranslationRequest translationRequest)
    {
        if (translationRequest.SubtitleToTranslate != null)
        {
            var existing = await _dbContext.TranslationRequests
                .Where(activeRequest =>
                    activeRequest.SubtitleToTranslate == translationRequest.SubtitleToTranslate &&
                    activeRequest.TargetLanguage == translationRequest.TargetLanguage &&
                    new[]
                        {
                            TranslationStatus.Pending,
                            TranslationStatus.InProgress
                        }.Contains(activeRequest.Status))
                .Select(activeRequest => new { activeRequest.Id })
                .FirstOrDefaultAsync();

            if (existing != null)
            {
                _logger.LogInformation(
                    "Duplicate translation request skipped for '{Subtitle}' -> '{Language}' (existing id {Id}).",
                    translationRequest.SubtitleToTranslate, translationRequest.TargetLanguage, existing.Id);
                return existing.Id;
            }
        }

        // Create a new TranslationRequest to not keep ID and JobID
        var translationRequestCopy = new TranslationRequest
        {
            MediaId = translationRequest.MediaId,
            Title = translationRequest.Title,
            SourceLanguage = translationRequest.SourceLanguage,
            TargetLanguage = translationRequest.TargetLanguage,
            SubtitleToTranslate = translationRequest.SubtitleToTranslate,
            MediaType = translationRequest.MediaType,
            Source = translationRequest.Source,
            Status = TranslationStatus.Pending,
            JobType = TranslationJobType.Translation,
            ContentHash = translationRequest.ContentHash
        };

        _dbContext.TranslationRequests.Add(translationRequestCopy);
        await _dbContext.SaveChangesAsync();
        await _eventService.LogEvent(translationRequestCopy.Id, TranslationStatus.Pending,
            eventType: TranslationEventType.Created);

        var jobId = _jobQueue.EnqueueTranslationJob(translationRequestCopy);
        // QA P3-2: the Enqueued timeline row used to carry the Hangfire job id,
        // which reads as noise in the UI. No reliable queue-position data is
        // available (the SQLite storage's enqueued list is unordered and a
        // worker may pick the job up immediately), so the message stays empty
        // and the client copy names the queue instead.
        await _eventService.LogTimelineEvent(
            translationRequestCopy.Id,
            TranslationEventType.Enqueued,
            TranslationStatus.Pending);
        await UpdateTranslationRequest(translationRequestCopy, TranslationStatus.Pending, jobId);

        await _queryService.UpdateActiveCount();

        return translationRequestCopy.Id;
    }

    /// <inheritdoc />
    public async Task CreateBulkRequest(BulkTranslateRequest request)
    {
        var sourceLanguages = await _settingService.GetSettingAsJson<SourceLanguage>(
            SettingKeys.Translation.SourceLanguages
            );
        var sourceCodes = sourceLanguages.Select(sourceLanguage => sourceLanguage.Code).ToHashSet();
        var ignoreCaptions = await _settingService.GetSetting(SettingKeys.Translation.IgnoreCaptions) ?? "false";

        switch (request.MediaType)
        {
            case MediaType.Movie:
                var movies = await _dbContext.Movies
                    .Where(movie => request.MediaIds.Contains(movie.Id))
                    .ToListAsync();

                foreach (var movie in movies)
                {
                    if (movie.Path == null)
                    {
                        _logger.LogInformation("Bulk: skipping movie {Id} — path is null", movie.Id);
                        continue;
                    }
                    await ProcessMediaSubtitles(
                        movie.Path,
                        movie.FileName,
                        movie.Id,
                        MediaType.Movie,
                        request.TargetLanguage,
                        sourceCodes,
                        ignoreCaptions);
                }
                break;

            case MediaType.Show:
                var shows = await _dbContext.Shows
                    .Include(show => show.Seasons)
                    .ThenInclude(season => season.Episodes)
                    .Where(show => request.MediaIds.Contains(show.Id))
                    .ToListAsync();

                foreach (var show in shows)
                {
                    foreach (var season in show.Seasons)
                    {
                        if (string.IsNullOrEmpty(season.Path))
                        {
                            continue;
                        }

                        foreach (var episode in season.Episodes)
                        {
                            if (string.IsNullOrEmpty(episode.FileName) || string.IsNullOrEmpty(episode.Path))
                            {
                                continue;
                            }
                            await ProcessMediaSubtitles(
                                season.Path,
                                episode.FileName,
                                episode.Id,
                                MediaType.Episode,
                                request.TargetLanguage,
                                sourceCodes,
                                ignoreCaptions);
                        }
                    }
                }
                break;
        }
    }

    /// <summary>
    /// Discovers subtitles for a media item, resolves the source subtitle, and creates a translation request.
    /// </summary>
    private async Task ProcessMediaSubtitles(
        string path,
        string? fileName,
        int mediaId,
        MediaType mediaType,
        string targetLanguage,
        HashSet<string> sourceCodes,
        string ignoreCaptions)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            _logger.LogDebug("Bulk: skipping mediaId {MediaId} — fileName is empty", mediaId);
            return;
        }

        var subtitles = await _subtitleService.GetSubtitles(path, fileName);
        var selected = _subtitleService.SelectSourceSubtitle(subtitles, sourceCodes, ignoreCaptions);
        if (selected == null)
        {
            _logger.LogDebug("Bulk: skipping mediaId {MediaId} — no source subtitle found (sourceCodes: {SourceCodes}, available: {Available})",
                mediaId, string.Join(", ", sourceCodes),
                string.Join(", ", subtitles.Select(s => s.Language)));
            return;
        }

        if (selected.AvailableLanguages.Contains(targetLanguage.ToLowerInvariant()))
        {
            _logger.LogDebug("Bulk: skipping mediaId {MediaId} — target language {Target} already exists",
                mediaId, targetLanguage);
            return;
        }

        await CreateRequest(new TranslateAbleSubtitle
        {
            MediaId = mediaId,
            MediaType = mediaType,
            SubtitlePath = selected.Subtitle.Path,
            TargetLanguage = targetLanguage,
            SourceLanguage = selected.SourceLanguage,
            SubtitleFormat = selected.Subtitle.Format
        });
    }

    /// <inheritdoc />
    public async Task<string?> CancelTranslationRequest(TranslationRequest cancelRequest)
    {
        var translationRequest = await _dbContext.TranslationRequests.FirstOrDefaultAsync(
            translationRequest => translationRequest.Id == cancelRequest.Id);
        if (translationRequest == null)
        {
            return null;
        }

        if (translationRequest.JobId != null)
        {
            _jobQueue.Delete(translationRequest.JobId);
        }
        else if (_asyncTranslationRegistry.TryGet(translationRequest.Id, out var cts))
        {
            // Maybe an async translation job
            await cts.CancelAsync();
        }

        if (translationRequest.JobType == TranslationJobType.Proofread &&
            !string.IsNullOrEmpty(translationRequest.TranslatedSubtitle))
        {
            translationRequest.Status = TranslationStatus.Completed;
            translationRequest.ErrorMessage = null;
            await _dbContext.SaveChangesAsync();
            await _eventService.LogEvent(translationRequest.Id, TranslationStatus.Cancelled, "Proofread was cancelled");
            await _queryService.UpdateActiveCount();
            await _progressService.Emit(translationRequest, 100);

            return $"Translation request with id {cancelRequest.Id} has been cancelled";
        }

        translationRequest.CompletedAt = DateTimeOffset.UtcNow;
        translationRequest.Status = TranslationStatus.Cancelled;
        translationRequest.ErrorMessage = "Translation was cancelled";
        await _dbContext.SaveChangesAsync();
        await _eventService.LogEvent(translationRequest.Id, TranslationStatus.Cancelled, "Translation was cancelled");
        await _queryService.UpdateActiveCount();
        await _progressService.Emit(translationRequest, 0);

        return $"Translation request with id {cancelRequest.Id} has been cancelled";
    }

    /// <inheritdoc />
    public async Task<string?> RemoveTranslationRequest(TranslationRequest cancelRequest)
    {
        var translationRequest = await _dbContext.TranslationRequests.FirstOrDefaultAsync(
            translationRequest => translationRequest.Id == cancelRequest.Id);
        if (translationRequest == null)
        {
            return null;
        }

        _dbContext.TranslationRequests.Remove(translationRequest);
        await _dbContext.SaveChangesAsync();

        return $"Translation request with id {cancelRequest.Id} has been removed";
    }

    /// <inheritdoc />
    public async Task<string?> RetryTranslationRequest(TranslationRequest retryRequest)
    {
        var translationRequest = await _dbContext.TranslationRequests.FirstOrDefaultAsync(
            translationRequest => translationRequest.Id == retryRequest.Id);
        if (translationRequest == null)
        {
            return null;
        }


        var newTranslationRequestId = await EnqueueRequest(translationRequest);
        return $"Translation request with id {retryRequest.Id} has been restarted, new job id {newTranslationRequestId}";
    }

    /// <inheritdoc />
    public async Task<string?> ResumeTranslationRequest(TranslationRequest resumeRequest)
    {
        var translationRequest = await _dbContext.TranslationRequests.FirstOrDefaultAsync(
            tr => tr.Id == resumeRequest.Id);
        if (translationRequest == null)
        {
            return null;
        }

        var resumable = new[]
        {
            TranslationStatus.Failed,
            TranslationStatus.Cancelled,
            TranslationStatus.Interrupted
        };
        if (!resumable.Contains(translationRequest.Status))
        {
            _logger.LogInformation(
                "Resume skipped for request {Id}: status {Status} is not resumable.",
                translationRequest.Id, translationRequest.Status);
            return null;
        }

        translationRequest.Status = TranslationStatus.Pending;
        translationRequest.ErrorMessage = null;
        translationRequest.StackTrace = null;
        translationRequest.CompletedAt = null;
        translationRequest.JobType = TranslationJobType.Translation;
        await _dbContext.SaveChangesAsync();
        await _eventService.LogEvent(translationRequest.Id, TranslationStatus.Pending, "Resumed");

        var jobId = _jobQueue.EnqueueTranslationJob(translationRequest);
        await UpdateTranslationRequest(translationRequest, TranslationStatus.Pending, jobId);
        await _queryService.UpdateActiveCount();

        return $"Translation request with id {resumeRequest.Id} has been resumed, new job id {jobId}";
    }

    /// <inheritdoc />
    public async Task ResumeTranslationRequests()
    {
        var requests = await _dbContext.TranslationRequests
            .Where(tr => tr.Status == TranslationStatus.Pending ||
                         tr.Status == TranslationStatus.InProgress)
            .ToListAsync();

        var queuedJobIds = _jobQueue.GetQueuedTranslationJobIds();

        foreach (var request in requests)
        {
            if (request.JobType == TranslationJobType.Proofread &&
                !string.IsNullOrEmpty(request.TranslatedSubtitle))
            {
                await _eventService.LogEvent(request.Id, TranslationStatus.Interrupted,
                    "Proofread did not finish, translation is unchanged");
                await UpdateTranslationRequest(request, TranslationStatus.Completed);
                continue;
            }

            if (request.JobId == null)
            {
                // Async translation job. Set as Interrupted and don't run
                // Those cannot be resumed
                await UpdateTranslationRequest(request, TranslationStatus.Interrupted);
                await _eventService.LogEvent(request.Id, TranslationStatus.Interrupted);
                continue;
            }

            foreach (var queuedJobId in queuedJobIds[request.Id])
            {
                _jobQueue.Delete(queuedJobId);
                _logger.LogWarning(
                    "Removed queued translation job {JobId} for request {RequestId} before resuming.",
                    queuedJobId, request.Id);
            }

            var jobId = _jobQueue.EnqueueTranslationJob(request);
            await UpdateTranslationRequest(request, TranslationStatus.Pending, jobId);
        }
    }

    /// <inheritdoc />
    public async Task ClearMediaHash(TranslationRequest translationRequest)
    {
        if (translationRequest.MediaId.HasValue)
        {
            switch (translationRequest.MediaType)
            {
                case MediaType.Movie:
                    var movie = await _dbContext.Movies.FirstOrDefaultAsync(m => m.Id == translationRequest.MediaId.Value);
                    if (movie != null)
                    {
                        movie.MediaHash = string.Empty;
                    }
                    break;

                case MediaType.Episode:
                    var episode = await _dbContext.Episodes.FirstOrDefaultAsync(e => e.Id == translationRequest.MediaId.Value);
                    if (episode != null)
                    {
                        episode.MediaHash = string.Empty;
                    }
                    break;
            }
            await _dbContext.SaveChangesAsync();
        }
    }

    /// <inheritdoc />
    public async Task<TranslationRequest> UpdateTranslationRequest(TranslationRequest translationRequest,
        TranslationStatus status, string? jobId = null)
    {
        var request = await _dbContext.TranslationRequests.FindAsync(translationRequest.Id);
        if (request == null)
        {
            throw new NotFoundException($"TranslationRequest with ID {translationRequest.Id} not found.");
        }

        if (jobId != null)
        {
            request.JobId = jobId;
        }
        request.Status = status;
        await _dbContext.SaveChangesAsync();

        return request;
    }
}
