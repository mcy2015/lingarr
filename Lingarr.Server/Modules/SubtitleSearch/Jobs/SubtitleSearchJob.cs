using Hangfire;
using Lingarr.Core.Configuration;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Filters;
using Lingarr.Server.Modules.Infrastructure;
using Lingarr.Server.Modules.Settings;
using Lingarr.Server.Modules.SubtitleSearch.Orchestration;
using Lingarr.Server.Modules.SubtitleSearch.Pipeline;
using Lingarr.Server.Modules.SubtitleSearch.Queue;
using Lingarr.Server.Modules.SubtitleSearch.Requests;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Extensions;

namespace Lingarr.Server.Modules.SubtitleSearch.Jobs;

/// <summary>
/// Automated subtitle-search batch (tech design ⑤, the 7th recurring job,
/// queue=system, cron from subtitle_search_schedule seeded "0 5 * * *").
/// One round: fill the queue from the needs-translation gap scan → dequeue
/// one priority batch (≤ subtitle_search_batch_limit, default 200) → per
/// item one aggregate search across the sources not cooling down → best
/// candidate ≥ subtitle_search_auto_score (default 75) goes through the
/// download pipeline; below the line a LowConfidence history row is written
/// and the item backs off. The master switch
/// (subtitle_search_enabled, default false) short-circuits the round before
/// any queue or provider action.
/// </summary>
public class SubtitleSearchJob
{
    /// <summary>Fallbacks when the seeded settings rows are missing/blank.</summary>
    public const int DefaultBatchLimit = 200;
    public const int DefaultAutoScore = 75;

    private readonly LingarrDbContext _dbContext;
    private readonly ISettingService _settingService;
    private readonly IScheduleService _scheduleService;
    private readonly SubtitleSearchQueueService _queueService;
    private readonly SubtitleSearchRequestBuilder _requestBuilder;
    private readonly SubtitleSearchOrchestrator _orchestrator;
    private readonly SubtitleDownloadPipeline _pipeline;
    private readonly ILogger<SubtitleSearchJob> _logger;

    public SubtitleSearchJob(
        LingarrDbContext dbContext,
        ISettingService settingService,
        IScheduleService scheduleService,
        SubtitleSearchQueueService queueService,
        SubtitleSearchRequestBuilder requestBuilder,
        SubtitleSearchOrchestrator orchestrator,
        SubtitleDownloadPipeline pipeline,
        ILogger<SubtitleSearchJob> logger)
    {
        _dbContext = dbContext;
        _settingService = settingService;
        _scheduleService = scheduleService;
        _queueService = queueService;
        _requestBuilder = requestBuilder;
        _orchestrator = orchestrator;
        _pipeline = pipeline;
        _logger = logger;
    }

    [DisableConcurrentExecution(timeoutInSeconds: 30 * 60)]
    [AutomaticRetry(Attempts = 0)]
    [Queue("system")]
    public async Task Execute()
    {
        // Parameterless like every recurring-job entry point (Hangfire
        // expression trees cannot omit optional arguments). The concurrent-
        // execution guard covers the NFR's 30min round ceiling — items are
        // marked terminal only after processing, so two overlapping rounds
        // would dequeue the same batch twice.
        var cancellationToken = CancellationToken.None;
        var jobName = JobContextFilter.GetCurrentJobTypeName();
        await _scheduleService.UpdateJobState(jobName, JobStatus.Processing.GetDisplayName());

        var settings = await _settingService.GetSettings([
            SettingKeys.SubtitleSearch.Enabled,
            SettingKeys.SubtitleSearch.BatchLimit,
            SettingKeys.SubtitleSearch.AutoScore
        ]);

        // Master switch off (the seeded default): one log line, zero queue
        // writes, zero provider calls — the rollback lever of ⑫.
        if (settings.GetValueOrDefault(SettingKeys.SubtitleSearch.Enabled) != "true")
        {
            _logger.LogInformation("Subtitle search is disabled; skipping the round.");
            await _scheduleService.UpdateJobState(jobName, JobStatus.Succeeded.GetDisplayName());
            return;
        }

        var batchLimit = ParsePositive(
            settings.GetValueOrDefault(SettingKeys.SubtitleSearch.BatchLimit), DefaultBatchLimit);
        var autoScore = ParsePositive(
            settings.GetValueOrDefault(SettingKeys.SubtitleSearch.AutoScore), DefaultAutoScore);

        await _queueService.EnqueueGapMediaAsync(cancellationToken);

        var cooldowns = await _orchestrator.GetActiveCooldownsAsync(cancellationToken);
        var activeProviders = _orchestrator.RegisteredProviderKeys()
            .Except(cooldowns.Keys, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (activeProviders.Count == 0)
        {
            // ⑧: every source cooling down is a normal end of round — warn
            // and succeed, never a retry storm.
            _logger.LogWarning("All subtitle sources are cooling down; ending the round.");
            await _scheduleService.UpdateJobState(jobName, JobStatus.Succeeded.GetDisplayName());
            return;
        }

        var batch = await _queueService.DequeueBatchAsync(batchLimit, DateTimeOffset.UtcNow, cancellationToken);
        var processed = 0;
        foreach (var item in batch)
        {
            processed++;
            try
            {
                await ProcessItemAsync(item, autoScore, activeProviders, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One broken item never takes the round down — it backs off.
                _logger.LogWarning(ex,
                    "Subtitle search for {MediaType} {MediaId} crashed.", item.MediaType, item.MediaId);
                await _queueService.MarkFailedAsync(item.Id, ex.Message, cancellationToken);
            }
        }

        _logger.LogInformation(
            "Subtitle search round finished: {Processed} items of a {Limit} limit.", processed, batchLimit);
        await _scheduleService.UpdateJobState(jobName, JobStatus.Succeeded.GetDisplayName());
    }

    private async Task ProcessItemAsync(
        SubtitleSearchState item,
        int autoScore,
        IReadOnlyCollection<string> activeProviders,
        CancellationToken cancellationToken)
    {
        var built = await _requestBuilder.BuildAsync(
            item.MediaType, item.MediaId, activeProviders, null, cancellationToken);
        if (built is null)
        {
            // Library entity vanished or no languages configured — skip the
            // row instead of burning retries on a permanent condition.
            await _dbContext.SubtitleSearchStates
                .Where(state => state.Id == item.Id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(state => state.Status, SubtitleSearchStatus.Skipped)
                    .SetProperty(state => state.LastError, "media item not searchable (missing or no languages)")
                    .SetProperty(state => state.LastSearchedAt, DateTimeOffset.UtcNow)
                    .SetProperty(state => state.UpdatedAt, DateTimeOffset.UtcNow),
                    cancellationToken);
            return;
        }

        var outcome = await _orchestrator.SearchAsync(
            built.Request, item.MediaType, item.MediaId, singleProvider: null, cancellationToken);
        var best = outcome.Candidates.FirstOrDefault();

        if (best is null || best.MatchScore < autoScore)
        {
            // ③/AC12a: below the line is a recorded skip, not a silent one —
            // history row with the best score found (if any), item backs off
            // (new uploads appear over time; a hash-less round may succeed later).
            var note = best is null
                ? "no candidate from any source"
                : $"best candidate scored {best.MatchScore} (< {autoScore})";
            await RecordLowConfidenceAsync(item, built.Request, best, note, cancellationToken);
            await _queueService.MarkFailedAsync(item.Id, note, cancellationToken);
            return;
        }

        var sessionId = _orchestrator.SessionIdOf(outcome.CacheKey, best);
        var download = await _pipeline.DownloadAsync(
            best, item.MediaType, item.MediaId, SubtitleDownloadSource.Auto, sessionId, cancellationToken);
        switch (download.Result)
        {
            case SubtitleDownloadResult.Succeeded:
            case SubtitleDownloadResult.AlreadyExistsSkipped:
                await _queueService.MarkSucceededAsync(item.Id, cancellationToken);
                break;
            default:
                await _queueService.MarkFailedAsync(item.Id, download.Error ?? "download failed", cancellationToken);
                break;
        }
    }

    private async Task RecordLowConfidenceAsync(
        SubtitleSearchState item,
        Abstractions.SubtitleSearchRequest request,
        Abstractions.SubtitleCandidate? best,
        string note,
        CancellationToken cancellationToken)
    {
        _dbContext.SubtitleDownloads.Add(new SubtitleDownload
        {
            MediaType = item.MediaType,
            MediaId = item.MediaId,
            Provider = best?.Provider ?? "aggregate",
            ExternalId = best?.ExternalId ?? string.Empty,
            Language = best?.Language ?? string.Join(",", request.Languages),
            FilePath = string.Empty,
            MatchScore = best?.MatchScore ?? 0,
            HashMatch = best?.HashMatch ?? false,
            Source = SubtitleDownloadSource.Auto,
            Result = SubtitleDownloadResult.LowConfidenceSkipped,
            Error = note,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private static int ParsePositive(string? value, int fallback)
        => int.TryParse(value, out var parsed) && parsed > 0 ? parsed : fallback;
}
