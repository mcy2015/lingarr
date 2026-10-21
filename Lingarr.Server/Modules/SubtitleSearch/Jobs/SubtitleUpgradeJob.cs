using Hangfire;
using Lingarr.Core.Configuration;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Filters;
using Lingarr.Server.Modules.Infrastructure;
using Lingarr.Server.Modules.Infrastructure.Notifications;
using Lingarr.Server.Modules.Settings;
using Lingarr.Server.Modules.SubtitleSearch.Orchestration;
using Lingarr.Server.Modules.SubtitleSearch.Pipeline;
using Lingarr.Server.Modules.SubtitleSearch.Requests;
using Lingarr.Server.Modules.SubtitleSearch.Scoring;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Extensions;

namespace Lingarr.Server.Modules.SubtitleSearch.Jobs;

/// <summary>
/// Subtitle upgrade re-search round (B02, the bazarr-upgrade port): a file
/// that landed at a low score keeps its chance at a better one. One round:
/// collect every (media, language) whose latest succeeded download still
/// exists on disk, landed inside <c>subtitle_upgrade_window_days</c> and
/// scored at or below <c>100 - subtitle_upgrade_score_margin</c> → re-search
/// that one language → the best candidate must cross
/// <c>max(subtitle_search_auto_score, old score + margin)</c> to land through
/// the download pipeline with <see cref="SubtitleDownloadSource.Upgrade"/> —
/// the superseded file is deleted once the replacement is on disk. A
/// candidate below the line leaves everything untouched and records a
/// LowConfidence history row (the honest "why did nothing change" answer).
/// <para>
/// Anti-loop by construction: the margin is clamped to ≥ 1, so every
/// replacement strictly raises the recorded score of the latest row and the
/// same target cannot be re-offered at the same score ("same score never
/// replaces").
/// </para>
/// <para>
/// Cancellation contract (SubtitleSearchJob precedent): the round ends
/// <see cref="JobStatus.Succeeded"/> only when it ran to the end; a cancelled
/// round unwinds the in-flight target — no history row is written for it,
/// nothing is marked failed — and rethrows
/// <see cref="OperationCanceledException"/>. The master switch
/// (<c>subtitle_upgrade_enabled</c>, seeded false by M0014) short-circuits
/// before any read or provider call, and ScheduleService keeps the recurring
/// registration off while it is unset — an off feature runs no no-op rounds.
/// </para>
/// <para>
/// Unlike the search job the round is serial: at most
/// <see cref="RoundTargetLimit"/> targets, each one aggregate search plus at
/// most one download, fits the 30-minute DisableConcurrentExecution ceiling
/// without the R14 media-level fan-out machinery; the collaborators are
/// resolved from the job's own Hangfire scope accordingly.
/// </para>
/// </summary>
public class SubtitleUpgradeJob
{
    /// <summary>Fallbacks when the seeded settings rows are missing/blank.</summary>
    public const int DefaultScoreMargin = 3;
    public const int DefaultWindowDays = 14;

    /// <summary>
    /// Upper bound of targets one round processes (B02 batch budget). The
    /// window filter already bounds the candidate set; this cap keeps a
    /// pathological window (a hand-edited 365-day row) inside the round
    /// ceiling. 10w-library first backfills digest day by day as the window
    /// scrolls.
    /// </summary>
    public const int RoundTargetLimit = 200;

    private readonly ISettingService _settingService;
    private readonly IScheduleService _scheduleService;
    private readonly LingarrDbContext _dbContext;
    private readonly SubtitleSearchRequestBuilder _requestBuilder;
    private readonly SubtitleSearchOrchestrator _orchestrator;
    private readonly SubtitleDownloadPipeline _pipeline;
    private readonly SubtitleMatchScorer _scorer;
    private readonly NotificationService _notifications;
    private readonly ILogger<SubtitleUpgradeJob> _logger;

    public SubtitleUpgradeJob(
        ISettingService settingService,
        IScheduleService scheduleService,
        LingarrDbContext dbContext,
        SubtitleSearchRequestBuilder requestBuilder,
        SubtitleSearchOrchestrator orchestrator,
        SubtitleDownloadPipeline pipeline,
        SubtitleMatchScorer scorer,
        NotificationService notifications,
        ILogger<SubtitleUpgradeJob> logger)
    {
        _settingService = settingService;
        _scheduleService = scheduleService;
        _dbContext = dbContext;
        _requestBuilder = requestBuilder;
        _orchestrator = orchestrator;
        _pipeline = pipeline;
        _scorer = scorer;
        _notifications = notifications;
        _logger = logger;
    }

    [DisableConcurrentExecution(timeoutInSeconds: 30 * 60)]
    [AutomaticRetry(Attempts = 0)]
    [Queue("system")]
    public Task Execute() => Execute(CancellationToken.None);

    /// <summary>
    /// Token-aware entry point (SubtitleSearchJob precedent): the Hangfire
    /// registration keeps the parameterless overload; tests (and later
    /// shutdown wiring) pass a token that penetrates the selection read and
    /// every per-target search/download.
    /// </summary>
    public async Task Execute(CancellationToken cancellationToken)
    {
        var jobName = JobContextFilter.GetCurrentJobTypeName();
        await _scheduleService.UpdateJobState(jobName, JobStatus.Processing.GetDisplayName());

        var settings = await _settingService.GetSettings([
            SettingKeys.SubtitleSearch.UpgradeEnabled,
            SettingKeys.SubtitleSearch.UpgradeScoreMargin,
            SettingKeys.SubtitleSearch.UpgradeWindowDays,
            SettingKeys.SubtitleSearch.AutoScore
        ]);

        // Master switch off (the M0014 seeded default): one log line, zero
        // database reads, zero provider calls.
        if (settings.GetValueOrDefault(SettingKeys.SubtitleSearch.UpgradeEnabled) != "true")
        {
            _logger.LogInformation("Subtitle upgrade is disabled; skipping the round.");
            await _scheduleService.UpdateJobState(jobName, JobStatus.Succeeded.GetDisplayName());
            return;
        }

        // Anti-loop clamp: a hand-edited margin below 1 (or unparsable)
        // becomes the default; 0 or negative would allow a same-score
        // replacement and re-offer the same target forever.
        var margin = Math.Max(1, ParsePositive(
            settings.GetValueOrDefault(SettingKeys.SubtitleSearch.UpgradeScoreMargin), DefaultScoreMargin));
        var windowDays = ParsePositive(
            settings.GetValueOrDefault(SettingKeys.SubtitleSearch.UpgradeWindowDays), DefaultWindowDays);
        var autoScore = ParsePositive(
            settings.GetValueOrDefault(SettingKeys.SubtitleSearch.AutoScore), SubtitleMatchScorer.DefaultAutoScore);

        var targets = await SelectTargetsAsync(margin, windowDays, cancellationToken);
        if (targets.Count == 0)
        {
            _logger.LogInformation(
                "Subtitle upgrade round found no candidates (window {WindowDays} days, margin {Margin}).",
                windowDays, margin);
            await _scheduleService.UpdateJobState(jobName, JobStatus.Succeeded.GetDisplayName());
            return;
        }

        var cooldowns = await _orchestrator.GetActiveCooldownsAsync(cancellationToken);
        var activeProviders = _orchestrator.RegisteredProviderKeys()
            .Except(cooldowns.Keys, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (activeProviders.Count == 0)
        {
            // Every source cooling down is a normal end of round (⑧) — warn
            // and succeed, never a retry storm.
            _logger.LogWarning("All subtitle sources are cooling down; ending the upgrade round.");
            await _scheduleService.UpdateJobState(jobName, JobStatus.Succeeded.GetDisplayName());
            return;
        }

        var upgraded = 0;
        var kept = 0;
        var crashed = 0;
        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var outcome = await ProcessTargetAsync(
                    target, margin, autoScore, activeProviders, cancellationToken);
                if (outcome == SubtitleDownloadResult.Succeeded)
                {
                    upgraded++;
                }
                else
                {
                    kept++; // below the line, cooldown-refused or failed — the old file stays
                }
            }
            catch (OperationCanceledException)
            {
                // Cancellation unwinds the round; the in-flight target wrote
                // no history row and is never counted as a failure.
                throw;
            }
            catch (Exception ex)
            {
                // One broken target never takes the round down — the old
                // file is untouched, the round continues.
                crashed++;
                _logger.LogWarning(ex,
                    "Subtitle upgrade for {MediaType} {MediaId} ({Language}) crashed.",
                    target.MediaType, target.MediaId, target.Language);
            }
        }

        _logger.LogInformation(
            "Subtitle upgrade round finished: {Targets} target(s) — {Upgraded} upgraded, {Kept} kept, {Crashed} crashed.",
            targets.Count, upgraded, kept, crashed);
        await _scheduleService.UpdateJobState(jobName, JobStatus.Succeeded.GetDisplayName());
    }

    /// <summary>
    /// One upgrade target's re-search + verdict. Returns the pipeline
    /// outcome's result on a download, or LowConfidenceSkipped when no
    /// candidate crossed the per-target line — both shapes have their
    /// history rows by the time this returns.
    /// </summary>
    private async Task<SubtitleDownloadResult> ProcessTargetAsync(
        UpgradeTarget target,
        int margin,
        int autoScore,
        IReadOnlyCollection<string> activeProviders,
        CancellationToken cancellationToken)
    {
        var built = await _requestBuilder.BuildAsync(
            target.MediaType, target.MediaId, activeProviders, [target.Language], cancellationToken);
        if (built is null)
        {
            // Library entity vanished or the language is configured nowhere
            // anymore — an honest skip, no history row (nothing was refused).
            _logger.LogDebug(
                "Upgrade target {MediaType} {MediaId} ({Language}) is no longer searchable.",
                target.MediaType, target.MediaId, target.Language);
            return SubtitleDownloadResult.AlreadyExistsSkipped;
        }

        var outcome = await _orchestrator.SearchAsync(
            built.Request, target.MediaType, target.MediaId, singleProvider: null, cancellationToken);

        // The request asked for exactly this language, but a source may
        // still answer with another — landing the wrong language would be a
        // bug, so the best candidate is picked among the matching ones only.
        var best = outcome.Candidates.FirstOrDefault(candidate =>
            string.Equals(candidate.Language, target.Language, StringComparison.OrdinalIgnoreCase));

        // The effective line (B02 decision): the new file must beat BOTH the
        // configured auto score and the old score by the margin — same score
        // never replaces.
        var threshold = Math.Max(autoScore, target.MatchScore + margin);
        if (best is null || !_scorer.IsAutoEligible(best, (int?)threshold))
        {
            var note = best is null
                ? $"no candidate from any source (upgrade of score {target.MatchScore})"
                : $"best upgrade candidate scored {best.MatchScore} (< {threshold}, old score {target.MatchScore})";
            await RecordLowConfidenceAsync(target, best, note, cancellationToken);
            return SubtitleDownloadResult.LowConfidenceSkipped;
        }

        var sessionId = _orchestrator.SessionIdOf(outcome.CacheKey, best);
        var download = await _pipeline.DownloadAsync(
            best, target.MediaType, target.MediaId, SubtitleDownloadSource.Upgrade, sessionId,
            cancellationToken, skipTranslation: false, replaceFilePath: target.FilePath);

        // B11 upgrade deal point: the replacement landed and the superseded
        // file is gone — exactly one "upgraded" event (the pipeline itself
        // stays quiet on upgrade-source successes, so no double notify).
        // NotifyAsync never throws; the round continues either way.
        if (download.Result == SubtitleDownloadResult.Succeeded)
        {
            await _notifications.NotifyAsync(new NotificationEvent(
                NotificationService.EventUpgraded, target.MediaType, target.MediaId,
                Title: null, Language: target.Language, Provider: best.Provider,
                Result: "succeeded"), CancellationToken.None);
        }

        return download.Result;
    }

    /// <summary>
    /// Candidate targets of one round: per (media_type, media_id, language)
    /// the LATEST succeeded download — the window and score ceiling apply to
    /// exactly that row (an older sibling whose file still exists does not
    /// resurrect a key whose latest file was deleted; that media needs a
    /// fresh search, which is the search job's gap scan, not an upgrade).
    /// The file must still exist: coverage that is no longer on disk is not
    /// "upgrade it", it is "find it again".
    /// <para>
    /// The SQL stays a plain filtered read (window + score ceiling, newest
    /// first); the per-key latest pick runs client-side over the slim
    /// projection — a GroupBy-latest translation is not worth the SQLite
    /// risk, and the window bounds the row count either way.
    /// </para>
    /// </summary>
    private async Task<List<UpgradeTarget>> SelectTargetsAsync(
        int margin, int windowDays, CancellationToken cancellationToken)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-windowDays);
        var rows = await _dbContext.SubtitleDownloads.AsNoTracking()
            .Where(download => download.Result == SubtitleDownloadResult.Succeeded)
            .Where(download => download.CreatedAt > cutoff)
            .Where(download => download.MatchScore <= 100 - margin)
            .OrderByDescending(download => download.CreatedAt)
            .ThenByDescending(download => download.Id)
            .Select(download => new
            {
                download.MediaType,
                download.MediaId,
                download.Language,
                download.FilePath,
                download.MatchScore,
                download.CreatedAt
            })
            .ToListAsync(cancellationToken);

        var latest = new Dictionary<(MediaType, int, string), (string FilePath, int MatchScore, DateTimeOffset CreatedAt)>();
        foreach (var row in rows)
        {
            // Rows arrive newest-first: the first hit per key IS the latest.
            latest.TryAdd((row.MediaType, row.MediaId, row.Language),
                (row.FilePath, row.MatchScore, row.CreatedAt));
        }

        return latest
            .Where(pair => !string.IsNullOrEmpty(pair.Value.FilePath) && File.Exists(pair.Value.FilePath))
            // Newest landings get the first upgrade chance (deterministic
            // order — a partial round always covers the same head set).
            .OrderByDescending(pair => pair.Value.CreatedAt)
            .Take(RoundTargetLimit)
            .Select(pair => new UpgradeTarget(
                pair.Key.Item1, pair.Key.Item2, pair.Key.Item3,
                pair.Value.FilePath, pair.Value.MatchScore))
            .ToList();
    }

    private async Task RecordLowConfidenceAsync(
        UpgradeTarget target,
        Abstractions.SubtitleCandidate? best,
        string note,
        CancellationToken cancellationToken)
    {
        _dbContext.SubtitleDownloads.Add(new SubtitleDownload
        {
            MediaType = target.MediaType,
            MediaId = target.MediaId,
            Provider = best?.Provider ?? "aggregate",
            ExternalId = best?.ExternalId ?? string.Empty,
            Language = target.Language,
            FilePath = string.Empty,
            MatchScore = best?.MatchScore ?? 0,
            HashMatch = best?.HashMatch ?? false,
            Source = SubtitleDownloadSource.Upgrade,
            Result = SubtitleDownloadResult.LowConfidenceSkipped,
            Error = note,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private static int ParsePositive(string? value, int fallback)
        => int.TryParse(value, out var parsed) && parsed > 0 ? parsed : fallback;

    /// <summary>One upgrade candidate: the media/language pair plus the superseded file facts.</summary>
    private sealed record UpgradeTarget(
        MediaType MediaType,
        int MediaId,
        string Language,
        string FilePath,
        int MatchScore);
}
