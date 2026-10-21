using Hangfire;
using Lingarr.Contracts.Exceptions;
using Lingarr.Contracts.Translation;
using Lingarr.Core.Configuration;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Controllers;
using Lingarr.Server.Filters;
using Lingarr.Server.Hubs;
using Lingarr.Server.Modules.Translation;
using Lingarr.Server.Models.FileSystem;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Extensions;
using Lingarr.Server.Modules.Infrastructure;
using Lingarr.Server.Modules.Settings;
using Lingarr.Server.Modules.Statistics;
using Lingarr.Server.Modules.Subtitle;
using Lingarr.Server.Modules.SubtitleSearch.Pipeline;
using SubtitleValidationOptions = Lingarr.Server.Models.SubtitleValidationOptions;
namespace Lingarr.Server.Modules.Translation;

public class TranslationJob
{
    private readonly ILogger<TranslationJob> _logger;
    private readonly ISettingService _settings;
    private readonly LingarrDbContext _dbContext;
    private readonly IProgressService _progressService;
    private readonly ISubtitleService _subtitleService;
    private readonly IScheduleService _scheduleService;
    private readonly IStatisticsService _statisticsService;
    private readonly ITranslationPipeline _translationPipeline;
    private readonly ITranslationRequestService _translationRequestService;
    private readonly ITranslationRequestEventService _eventService;
    private readonly IGlossaryService _glossaryService;
    private readonly SubtitleCoverageRefresher _coverageRefresher;
    private readonly IHubContext<JobProgressHub> _hubContext;

    public TranslationJob(
        ILogger<TranslationJob> logger,
        ISettingService settings,
        LingarrDbContext dbContext,
        IProgressService progressService,
        ISubtitleService subtitleService,
        IScheduleService scheduleService,
        IStatisticsService statisticsService,
        ITranslationPipeline translationPipeline,
        ITranslationRequestService translationRequestService,
        ITranslationRequestEventService eventService,
        IGlossaryService glossaryService,
        SubtitleCoverageRefresher coverageRefresher,
        IHubContext<JobProgressHub> hubContext)
    {
        _logger = logger;
        _settings = settings;
        _dbContext = dbContext;
        _progressService = progressService;
        _subtitleService = subtitleService;
        _scheduleService = scheduleService;
        _statisticsService = statisticsService;
        _translationPipeline = translationPipeline;
        _translationRequestService = translationRequestService;
        _glossaryService = glossaryService;
        _eventService = eventService;
        _coverageRefresher = coverageRefresher;
        _hubContext = hubContext;
    }

    [AutomaticRetry(Attempts = 0)]
    [Queue("translation")]
    public async Task Execute(
        TranslationRequest translationRequest,
        CancellationToken cancellationToken)
    {
        var jobName = JobContextFilter.GetCurrentJobTypeName();
        var jobId = JobContextFilter.GetCurrentJobId();

        // L2 correlation: every log line of this run — the start message, the
        // resume accounting, the writeback completion and every failure-path
        // entry below — carries the translation request id (the
        // translation_requests.Id the queue and detail screens key on). The
        // scope restores on completion, cancellation and thrown exceptions
        // alike. jobId arrives through the same ambient context from
        // JobContextFilter.OnPerforming.
        using var correlation = CorrelationContext.Push(
            requestId: translationRequest.Id.ToString());

        try
        {
            await _scheduleService.UpdateJobState(jobName, JobStatus.Processing.GetDisplayName());
            cancellationToken.ThrowIfCancellationRequested();

            var storedStatus = await _dbContext.TranslationRequests
                .Where(storedRequest => storedRequest.Id == translationRequest.Id)
                .Select(storedRequest => (TranslationStatus?)storedRequest.Status)
                .FirstOrDefaultAsync(cancellationToken);
            if (storedStatus is null or TranslationStatus.Completed or TranslationStatus.Cancelled)
            {
                _logger.LogInformation(
                    "Skipping translation job for request {RequestId}, it is no longer runnable ({Status}).",
                    translationRequest.Id, storedStatus);
                return;
            }

            var request = await _translationRequestService.UpdateTranslationRequest(translationRequest,
                TranslationStatus.InProgress,
                jobId);
            request.StartedAt = DateTimeOffset.UtcNow;
            await _dbContext.SaveChangesAsync();
            await _eventService.LogEvent(request.Id, TranslationStatus.InProgress,
                eventType: TranslationEventType.Started);
            await _translationRequestService.UpdateActiveCount();

            _logger.LogInformation("TranslateJob started for subtitle: {filePath}",
                translationRequest.SubtitleToTranslate);
            var settings = await _settings.GetSettings([
                SettingKeys.Translation.FixOverlappingSubtitles,
                SettingKeys.Translation.StripSubtitleFormatting,
                SettingKeys.Translation.PreserveLineBreaks,
                SettingKeys.Translation.BilingualSubtitleOutput,
                SettingKeys.Translation.AddTranslatorInfo,

                SettingKeys.SubtitleValidation.ValidateSubtitles,
                SettingKeys.SubtitleValidation.MaxFileSizeBytes,
                SettingKeys.SubtitleValidation.MaxSubtitleLength,
                SettingKeys.SubtitleValidation.MinSubtitleLength,
                SettingKeys.SubtitleValidation.MinDurationMs,
                SettingKeys.SubtitleValidation.MaxDurationSecs,

                SettingKeys.Translation.RemoveLanguageTag,
                SettingKeys.Translation.UseSubtitleTagging,
                SettingKeys.Translation.SubtitleTag
            ]);
            var stripSubtitleFormatting = settings[SettingKeys.Translation.StripSubtitleFormatting] == "true";
            var bilingualOutput = settings[SettingKeys.Translation.BilingualSubtitleOutput] == "true";
            var addTranslatorInfo = settings[SettingKeys.Translation.AddTranslatorInfo] == "true";
            var validateSubtitles = settings[SettingKeys.SubtitleValidation.ValidateSubtitles] != "false";
            var removeLanguageTag = settings[SettingKeys.Translation.RemoveLanguageTag] != "false";

            // validate subtitles
            if (validateSubtitles)
            {
                var validationOptions = new SubtitleValidationOptions
                {
                    // File size setting - default to 2MB if parsing fails
                    MaxFileSizeBytes = long.TryParse(settings[SettingKeys.SubtitleValidation.MaxFileSizeBytes],
                        out var maxFileSizeBytes)
                        ? maxFileSizeBytes
                        : 2 * 1024 * 1024,

                    // Maximum characters per subtitle - default to 500 if parsing fails
                    MaxSubtitleLength = int.TryParse(settings[SettingKeys.SubtitleValidation.MaxSubtitleLength],
                        out var maxSubtitleLength)
                        ? maxSubtitleLength
                        : 500,

                    // Minimum characters per subtitle - default to 1 if parsing fails
                    MinSubtitleLength = int.TryParse(settings[SettingKeys.SubtitleValidation.MinSubtitleLength],
                        out var minSubtitleLength)
                        ? minSubtitleLength
                        : 2,

                    // Minimum duration in milliseconds - default to 500ms if parsing fails
                    MinDurationMs = double.TryParse(settings[SettingKeys.SubtitleValidation.MinDurationMs],
                        out var minDurationMs)
                        ? minDurationMs
                        : 500,

                    // Maximum duration in seconds - default to 10s if parsing fails
                    MaxDurationSecs = double.TryParse(settings[SettingKeys.SubtitleValidation.MaxDurationSecs],
                        out var maxDurationSecs)
                        ? maxDurationSecs
                        : 10,

                    // Used to determine content length when
                    StripSubtitleFormatting = stripSubtitleFormatting
                };

                if (!_subtitleService.ValidateSubtitle(request.SubtitleToTranslate, validationOptions))
                {
                    _logger.LogWarning("Subtitle is not valid according to configured preferences.");
                    throw new TaskCanceledException("Subtitle is not valid according to configured preferences.");
                }
            }

            // translate subtitles via the shared pipeline
            var subtitles = await _subtitleService.ReadSubtitles(request.SubtitleToTranslate);

            // subtitle already carries a translation from an earlier prior run.
            // Group by Position and keep the most recent row in case the same position was used more than once.
            var persistedLines = (await _dbContext.TranslationRequestLines
                    .Where(line => line.TranslationRequestId == request.Id)
                    .Select(line => new { line.Id, line.Position, line.Target })
                    .ToListAsync(cancellationToken))
                .GroupBy(line => line.Position)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(l => l.Id).First().Target);

            if (persistedLines.Count > 0)
            {
                _logger.LogInformation(
                    "Resuming translation for request {RequestId}: {Resumed} of {Total} lines already translated.",
                    request.Id, persistedLines.Count, subtitles.Count);

                foreach (var subtitle in subtitles)
                {
                    if (persistedLines.TryGetValue(subtitle.Position, out var target))
                    {
                        subtitle.TranslatedLines = [target];
                    }
                }
            }

            var run = await _translationPipeline.RunAsync(
                new TranslationRun(request, subtitles),
                cancellationToken: cancellationToken);
            var translatedSubtitles = run.TranslatedSubtitles;
            var serviceType = run.ServiceType;
            var translationService = run.PrimaryService;

            // persist the service that actually produced this request so the
            // queue screen badge and the cost rollups attribute the output to
            // the provider that really translated it — on a degraded run that
            // is the fallback, not the configured primary. Note `request` is
            // the tracked entity - the job parameter is a deserialized copy
            request.ServiceType = serviceType;
            await _dbContext.SaveChangesAsync();

            // Locked glossary terms are enforced deterministically after the
            // provider run (A8): sources containing a locked term must carry
            // its target in the translation.
            var glossarySubstitutions = await _glossaryService.ApplyLockedTerms(translatedSubtitles);
            if (glossarySubstitutions > 0)
            {
                _logger.LogInformation(
                    "Applied locked glossary terms to {Count} lines for request {RequestId}",
                    glossarySubstitutions, request.Id);
            }

            if (settings[SettingKeys.Translation.FixOverlappingSubtitles] == "true")
            {
                translatedSubtitles = _subtitleService.FixOverlappingSubtitles(translatedSubtitles);
            }

            if (addTranslatorInfo)
            {
                _subtitleService.AddTranslatorInfo(serviceType, translatedSubtitles, translationService);
            }

            if (stripSubtitleFormatting)
            {
                var format = translatedSubtitles[0].SsaFormat;
                if (format != null)
                {
                    format.Styles = [];
                }
            }

            // statistics tracking, only count subtitles translated by this run
            var newlyTranslatedSubtitles = persistedLines.Count == 0
                ? translatedSubtitles
                : translatedSubtitles.Where(s => !persistedLines.ContainsKey(s.Position)).ToList();
            await _statisticsService.UpdateTranslationStatisticsFromSubtitles(
                request, serviceType, translationService.ModelName, newlyTranslatedSubtitles);

            var subtitleTag = "";
            if (settings[SettingKeys.Translation.UseSubtitleTagging] == "true")
            {
                subtitleTag = settings[SettingKeys.Translation.SubtitleTag];
            }

            await WriteSubtitles(request, translatedSubtitles, stripSubtitleFormatting, subtitleTag, removeLanguageTag,
                bilingualOutput, cancellationToken);
            await HandleCompletion(jobName, request, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await HandleCancellation(jobName, translationRequest);
        }
        catch (Exception ex)
        {
            await _translationRequestService.ClearMediaHash(translationRequest);
            translationRequest = await _translationRequestService.UpdateTranslationRequest(translationRequest, TranslationStatus.Failed,
                jobId);
            
            translationRequest.ErrorMessage = ex.Message;
            translationRequest.StackTrace = ex.ToString();

            // record how many Hangfire retries were already attempted for this job
            if (!string.IsNullOrEmpty(translationRequest.JobId))
            {
                try
                {
                    var details = JobStorage.Current.GetMonitoringApi()
                        .JobDetails(translationRequest.JobId);
                    var failedState = details?.History?.LastOrDefault(state =>
                        state.StateName == "Failed"
                        && state.Data != null
                        && state.Data.TryGetValue("RetryCount", out _));
                    if (failedState?.Data != null
                        && int.TryParse(failedState.Data["RetryCount"], out var retryCount))
                    {
                        translationRequest.RetryCount = retryCount;
                    }
                }
                catch (Exception monitorEx)
                {
                    _logger.LogWarning(monitorEx, "Unable to read Hangfire retry count for {JobId}",
                        translationRequest.JobId);
                }
            }

            await _dbContext.SaveChangesAsync(cancellationToken);

            await _eventService.LogEvent(translationRequest.Id, TranslationStatus.Failed, ex.Message,
                eventType: TranslationEventType.Failed);
            await _scheduleService.UpdateJobState(jobName, JobStatus.Failed.GetDisplayName());
            await _translationRequestService.UpdateActiveCount();
            await _progressService.Emit(translationRequest, 0);
            throw;
        }
    }

    private async Task WriteSubtitles(TranslationRequest translationRequest,
        List<SubtitleItem> translatedSubtitles,
        bool stripSubtitleFormatting,
        string subtitleTag,
        bool removeLanguageTag,
        bool bilingual,
        CancellationToken cancellationToken)
    {
        try
        {
            var targetLanguage = removeLanguageTag ? "" : translationRequest.TargetLanguage;

            var outputPath = await _subtitleService.CreateFilePath(
                translationRequest.SubtitleToTranslate,
                targetLanguage,
                subtitleTag);

            await _subtitleService.WriteSubtitles(outputPath, translatedSubtitles, stripSubtitleFormatting, bilingual);
            translationRequest.TranslatedSubtitle = outputPath;
            translationRequest.IsBilingual = bilingual;
            await _dbContext.SaveChangesAsync();
            await _eventService.LogTimelineEvent(
                translationRequest.Id,
                TranslationEventType.Writeback,
                TranslationStatus.InProgress,
                message: outputPath);

            // The file is on disk now — the coverage snapshot, the episode
            // need flags and the detail page's cached directory scan must
            // reflect it immediately, not on the next daily statistics run.
            await RefreshMediaAfterWritebackAsync(translationRequest, outputPath, cancellationToken);

            _logger.LogInformation("TranslateJob completed and created subtitle: {filePath}",
                outputPath);
        }
        catch (Exception e)
        {
            // The subtitle write-back failed — the full stack (on the
            // exception object below) plus the request id from the
            // correlation context identify the exact request.
            _logger.LogError(e, "Writing the translated subtitle to disk failed.");
            throw;
        }
    }

    /// <summary>
    /// Post-writeback media refresh (walkthrough fix B1): recomputes the
    /// media's subtitle coverage the same moment the translated file lands —
    /// movies refresh their coverage snapshot + need flags, episodes refresh
    /// the owning show's aggregated snapshot + every episode flag — drops the
    /// detail page's cached scan of the directory the file was written to,
    /// and broadcasts the same "a subtitle landed for this media" hub event
    /// the download pipeline emits. Every step is best-effort: the file is
    /// already safely on disk, the daily scan re-syncs whatever a failed
    /// refresh would have missed, and a refresh problem must not fail the
    /// translation job.
    /// </summary>
    private async Task RefreshMediaAfterWritebackAsync(
        TranslationRequest translationRequest,
        string outputPath,
        CancellationToken cancellationToken)
    {
        if (translationRequest.MediaId is not int mediaId)
        {
            return;
        }

        // Movie requests carry the movie id; episode requests carry the
        // episode id under both conventions (Episode, and the legacy Show
        // rows) — the refresher maps the episode to its owning show.
        var isMovie = translationRequest.MediaType == MediaType.Movie;
        var isEpisode = translationRequest.MediaType is MediaType.Episode or MediaType.Show;
        if (!isMovie && !isEpisode)
        {
            return;
        }

        var mediaType = isMovie ? MediaType.Movie : MediaType.Episode;
        try
        {
            await _coverageRefresher.RefreshAsync(mediaType, mediaId, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Coverage refresh failed after translation writeback for request {RequestId}.",
                translationRequest.Id);
        }

        // The Files card caches directory scans for a short TTL — drop the
        // directory that just received the file so the next view re-scans.
        MediaController.InvalidateSubtitleFileCache(Path.GetDirectoryName(outputPath));

        try
        {
            // Same event, group and payload shape as the download pipeline's
            // SubtitleDownloaded broadcast: for consumers a subtitle that
            // landed is a subtitle that landed, downloaded or translated.
            await _hubContext.Clients.Group("JobProgress").SendAsync(
                "SubtitleDownloaded",
                new
                {
                    mediaType = mediaType.ToString(),
                    mediaId,
                    language = translationRequest.TargetLanguage,
                    provider = translationRequest.ServiceType,
                    path = outputPath
                },
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Subtitle writeback broadcast failed for request {RequestId}.",
                translationRequest.Id);
        }
    }

    private async Task HandleCompletion(
        string jobName,
        TranslationRequest translationRequest,
        CancellationToken cancellationToken)
    {
        translationRequest.CompletedAt = DateTimeOffset.UtcNow;
        translationRequest.Status = TranslationStatus.Completed;
        await _dbContext.SaveChangesAsync(cancellationToken);
        await _eventService.LogEvent(translationRequest.Id, TranslationStatus.Completed,
            eventType: TranslationEventType.Completed);
        await _translationRequestService.UpdateActiveCount();
        await _progressService.Emit(translationRequest, 100);
        await _scheduleService.UpdateJobState(jobName, JobStatus.Succeeded.GetDisplayName());
    }

    private async Task HandleCancellation(string jobName, TranslationRequest request)
    {
        _logger.LogInformation("Translation cancelled for subtitle: {subtitlePath}",
            request.SubtitleToTranslate);
        var translationRequest =
            await _dbContext.TranslationRequests.FirstOrDefaultAsync(translationRequest =>
                translationRequest.Id == request.Id);

        if (translationRequest != null)
        {
            translationRequest.CompletedAt = DateTimeOffset.UtcNow;
            translationRequest.Status = TranslationStatus.Cancelled;
            translationRequest.ErrorMessage = "Translation was cancelled";
            await _dbContext.SaveChangesAsync();
            await _eventService.LogEvent(translationRequest.Id, TranslationStatus.Cancelled, "Translation was cancelled");

            await _translationRequestService.ClearMediaHash(translationRequest);
            await _translationRequestService.UpdateActiveCount();
            await _progressService.Emit(translationRequest, 0);
            await _scheduleService.UpdateJobState(jobName, JobStatus.Cancelled.GetDisplayName());
        }
    }
}