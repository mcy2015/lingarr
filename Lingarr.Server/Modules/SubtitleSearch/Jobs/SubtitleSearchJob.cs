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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi.Extensions;
using System.Collections.Concurrent;
using System.Diagnostics;

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
/// <para>
/// R14: the per-item loop is a media-level bounded fan-out
/// (<see cref="MediaParallelism"/> items concurrently). The whole per-item
/// graph (request builder, orchestrator, download pipeline, queue marks,
/// telemetry — all scoped over the DbContext) is resolved from a DI scope
/// created per in-flight item, because a DbContext must never serve two
/// concurrent operations; the job keeps only the round-level collaborators
/// (settings read, gap enqueue, batch dequeue, cooldown snapshot, job
/// state) on its own Hangfire scope. Source-level behaviour is unchanged:
/// the orchestrator fans each item's sources out exactly as before and
/// SubtitleHttp keeps its global per-source throttling — the job adds
/// concurrency on the media dimension only.
/// </para>
/// </summary>
public class SubtitleSearchJob
{
    /// <summary>Fallbacks when the seeded settings rows are missing/blank.</summary>
    public const int DefaultBatchLimit = 200;
    public const int DefaultAutoScore = 75;

    /// <summary>
    /// Batch-limit guard rails (design ⑧ polish): a mistyped
    /// <c>subtitle_search_batch_limit</c> (or a hand-edited settings row)
    /// must never turn one round into an unbounded marathon — the 30min
    /// round ceiling of the NFR assumes ≤2000 items.
    /// </summary>
    public const int MinBatchLimit = 50;
    public const int MaxBatchLimit = 2000;

    /// <summary>
    /// Media-level concurrency ceiling of one round (R14): at most this many
    /// items run the search+download pipeline at the same time. 8 is what
    /// the 10w-library P95 ≤ 30min arithmetic assumes. The gate is taken
    /// around the whole per-item pipeline (build → search → download →
    /// queue mark), so "in flight" means exactly one media item inside its
    /// own DI scope.
    /// </summary>
    public const int MediaParallelism = 8;

    // Round-level collaborators only — every one of them is used from the
    // job's own execution flow (before the fan-out or after it joins), never
    // from inside the concurrent per-item bodies. The per-item graph lives
    // in the scopes created by ProcessBoundedAsync.
    private readonly ISettingService _settingService;
    private readonly IScheduleService _scheduleService;
    private readonly SubtitleSearchQueueService _queueService;
    private readonly SubtitleSearchOrchestrator _orchestrator;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SubtitleSearchJob> _logger;

    public SubtitleSearchJob(
        ISettingService settingService,
        IScheduleService scheduleService,
        SubtitleSearchQueueService queueService,
        SubtitleSearchOrchestrator orchestrator,
        IServiceScopeFactory scopeFactory,
        ILogger<SubtitleSearchJob> logger)
    {
        _settingService = settingService;
        _scheduleService = scheduleService;
        _queueService = queueService;
        _orchestrator = orchestrator;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    [DisableConcurrentExecution(timeoutInSeconds: 30 * 60)]
    [AutomaticRetry(Attempts = 0)]
    [Queue("system")]
    public Task Execute() => Execute(CancellationToken.None);

    /// <summary>
    /// Token-aware entry point (R14). The Hangfire registration keeps the
    /// parameterless overload (ScheduleService's expression binds it); this
    /// overload lets callers — tests today, shutdown wiring later — pass a
    /// token that penetrates every in-flight item. A cancelled round throws
    /// <see cref="OperationCanceledException"/> after all in-flight items
    /// have unwound; cancelled items are never marked failed.
    /// </summary>
    public async Task Execute(CancellationToken cancellationToken)
    {
        // Parameterless like every recurring-job entry point (Hangfire
        // expression trees cannot omit optional arguments). The concurrent-
        // execution guard covers the NFR's 30min round ceiling — items are
        // marked terminal only after processing, so two overlapping rounds
        // would dequeue the same batch twice.
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

        var batchLimit = Math.Clamp(
            ParsePositive(settings.GetValueOrDefault(SettingKeys.SubtitleSearch.BatchLimit), DefaultBatchLimit),
            MinBatchLimit,
            MaxBatchLimit);
        var autoScore = ParsePositive(
            settings.GetValueOrDefault(SettingKeys.SubtitleSearch.AutoScore), DefaultAutoScore);

        await _queueService.EnqueueGapMediaAsync(cancellationToken);

        var cooldowns = await _orchestrator.GetActiveCooldownsAsync(cancellationToken);
        var activeProviders = _orchestrator.RegisteredProviderKeys()
            .Except(cooldowns.Keys, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (cooldowns.Count > 0)
        {
            // L4b: the cooling set is a per-round fact — one Information
            // line here; the orchestrator's per-media repetition of the
            // same fact is Debug. The all-cooling warning below still marks
            // the "round could do nothing" case distinctly.
            _logger.LogInformation(
                "Cooling down this round: {Sources}.",
                string.Join(", ", cooldowns.Select(pair =>
                    $"{pair.Key} until {pair.Value.Until:u} ({pair.Value.Reason})")));
        }
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
        var roundStarted = Stopwatch.GetTimestamp();
        // L4b round aggregation: per-item outcomes tally into these and the
        // round-end Information line reports them — per-media detail stays
        // Debug in the pipeline/orchestrator, so a 10w library produces
        // O(1) Information lines per round.
        var downloadTally = new ConcurrentDictionary<SubtitleDownloadResult, int>();
        var sourceOutcomes = new ConcurrentQueue<SubtitleSourceOutcome>();

        // R14: media-level bounded fan-out. SemaphoreSlim(8) + Task.WhenAll
        // (chosen over Parallel.ForEachAsync) because the round's contract
        // is "every dequeued item is one task that reaches a terminal state
        // (or unwinds on cancellation) before the round is judged" — WhenAll
        // states that join directly, whereas ForEachAsync's early-exit-on-
        // cancellation scheduling leaves the not-yet-started items' fate to
        // implementation detail. The gate is released in a finally, so a
        // cancelled waiter never blocks the in-flight unwind; cancellation
        // propagates out of WhenAll exactly like the serial loop's rethrow.
        using var mediaGate = new SemaphoreSlim(MediaParallelism, MediaParallelism);

        async Task ProcessBoundedAsync(SubtitleSearchState item)
        {
            await mediaGate.WaitAsync(cancellationToken);
            try
            {
                Interlocked.Increment(ref processed);
                // The scope must outlive the catch below — MarkFailedAsync
                // runs on the scoped queue service, so it is declared here
                // and not inside the try.
                using var scope = _scopeFactory.CreateScope();
                var scopedQueue = scope.ServiceProvider.GetRequiredService<SubtitleSearchQueueService>();
                try
                {
                    await ProcessItemAsync(
                        scope.ServiceProvider, item, autoScore, activeProviders,
                        downloadTally, sourceOutcomes, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    // Cancellation unwinds the round; the item is not failed.
                    throw;
                }
                catch (Exception ex)
                {
                    // One broken item never takes the round down — it backs
                    // off (media id stays in the message for log search).
                    _logger.LogWarning(ex,
                        "Subtitle search for {MediaType} {MediaId} crashed.", item.MediaType, item.MediaId);
                    await scopedQueue.MarkFailedAsync(item.Id, ex.Message, cancellationToken);
                }
            }
            finally
            {
                mediaGate.Release();
            }
        }

        var workers = batch.Select(item => ProcessBoundedAsync(item)).ToArray();
        await Task.WhenAll(workers);

        var sourceFailures = sourceOutcomes
            .GroupBy(outcome => (outcome.Provider, outcome.Reason))
            .Select(group => $"{group.Key.Provider} {group.Key.Reason}×{group.Count()}")
            .ToList();
        _logger.LogInformation(
            "Subtitle search round finished: {Processed} items of a {Limit} limit in {ElapsedMs:0}ms. Downloads: {Tally}.{Failures}",
            processed, batchLimit, Stopwatch.GetElapsedTime(roundStarted).TotalMilliseconds,
            downloadTally.Count == 0
                ? "none"
                : string.Join(", ", downloadTally.Select(pair => $"{pair.Key}×{pair.Value}")),
            sourceFailures.Count == 0
                ? " No source failures."
                : " Source failures: " + string.Join("; ", sourceFailures) + ".");
        await _scheduleService.UpdateJobState(jobName, JobStatus.Succeeded.GetDisplayName());
    }

    /// <summary>
    /// One media item's search+download pipeline, entirely on services
    /// resolved from the item's own DI scope (R14): the request builder, the
    /// orchestrator, the download pipeline, the queue marks and every EF
    /// write behind them are scoped over the DbContext, and one DbContext
    /// must never serve two concurrent operations — so the concurrent bodies
    /// share nothing but stateless singletons (scorer, candidate cache,
    /// quota store) and the cooldown store, whose persisted map mutations
    /// are serialized by its process-wide static gate.
    /// </summary>
    private async Task ProcessItemAsync(
        IServiceProvider scopedServices,
        SubtitleSearchState item,
        int autoScore,
        IReadOnlyCollection<string> activeProviders,
        ConcurrentDictionary<SubtitleDownloadResult, int> downloadTally,
        ConcurrentQueue<SubtitleSourceOutcome> sourceOutcomes,
        CancellationToken cancellationToken)
    {
        var dbContext = scopedServices.GetRequiredService<LingarrDbContext>();
        var requestBuilder = scopedServices.GetRequiredService<SubtitleSearchRequestBuilder>();
        var orchestrator = scopedServices.GetRequiredService<SubtitleSearchOrchestrator>();
        var pipeline = scopedServices.GetRequiredService<SubtitleDownloadPipeline>();
        var queueService = scopedServices.GetRequiredService<SubtitleSearchQueueService>();

        var built = await requestBuilder.BuildAsync(
            item.MediaType, item.MediaId, activeProviders, null, cancellationToken);
        if (built is null)
        {
            // Library entity vanished or no languages configured — skip the
            // row instead of burning retries on a permanent condition.
            await dbContext.SubtitleSearchStates
                .Where(state => state.Id == item.Id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(state => state.Status, SubtitleSearchStatus.Skipped)
                    .SetProperty(state => state.LastError, "media item not searchable (missing or no languages)")
                    .SetProperty(state => state.LastSearchedAt, DateTimeOffset.UtcNow)
                    .SetProperty(state => state.UpdatedAt, DateTimeOffset.UtcNow),
                    cancellationToken);
            return;
        }

        var outcome = await orchestrator.SearchAsync(
            built.Request, item.MediaType, item.MediaId, singleProvider: null, cancellationToken);
        foreach (var sourceOutcome in outcome.SourceOutcomes)
        {
            sourceOutcomes.Enqueue(sourceOutcome);
        }
        var best = outcome.Candidates.FirstOrDefault();

        if (best is null || best.MatchScore < autoScore)
        {
            // ③/AC12a: below the line is a recorded skip, not a silent one —
            // history row with the best score found (if any), item backs off
            // (new uploads appear over time; a hash-less round may succeed later).
            var note = best is null
                ? "no candidate from any source"
                : $"best candidate scored {best.MatchScore} (< {autoScore})";
            await RecordLowConfidenceAsync(dbContext, item, built.Request, best, note, cancellationToken);
            await queueService.MarkFailedAsync(item.Id, note, cancellationToken);
            downloadTally.AddOrUpdate(
                SubtitleDownloadResult.LowConfidenceSkipped, 1, (_, count) => count + 1);
            return;
        }

        var sessionId = orchestrator.SessionIdOf(outcome.CacheKey, best);
        var download = await pipeline.DownloadAsync(
            best, item.MediaType, item.MediaId, SubtitleDownloadSource.Auto, sessionId, cancellationToken);
        downloadTally.AddOrUpdate(download.Result, 1, (_, count) => count + 1);
        switch (download.Result)
        {
            case SubtitleDownloadResult.Succeeded:
            case SubtitleDownloadResult.AlreadyExistsSkipped:
                await queueService.MarkSucceededAsync(item.Id, cancellationToken);
                break;
            default:
                await queueService.MarkFailedAsync(item.Id, download.Error ?? "download failed", cancellationToken);
                break;
        }
    }

    private async Task RecordLowConfidenceAsync(
        LingarrDbContext dbContext,
        SubtitleSearchState item,
        Abstractions.SubtitleSearchRequest request,
        Abstractions.SubtitleCandidate? best,
        string note,
        CancellationToken cancellationToken)
    {
        dbContext.SubtitleDownloads.Add(new SubtitleDownload
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
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static int ParsePositive(string? value, int fallback)
        => int.TryParse(value, out var parsed) && parsed > 0 ? parsed : fallback;
}
