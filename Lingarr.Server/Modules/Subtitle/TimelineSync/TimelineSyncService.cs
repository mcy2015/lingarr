using System.Globalization;
using Lingarr.Core.Configuration;
using Lingarr.Server.Models.FileSystem;
using Lingarr.Server.Modules.Settings;
using Microsoft.Extensions.Logging;

namespace Lingarr.Server.Modules.Subtitle.TimelineSync;

/// <summary>Outcome statuses of <see cref="ITimelineSyncService.SyncFileAsync"/> (B05).</summary>
public static class TimelineSyncStatus
{
    /// <summary>The file was rewritten (backup created).</summary>
    public const string Corrected = "corrected";

    /// <summary>Within the no-op band (|offset| &lt; 200ms at R=1): nothing written.</summary>
    public const string NoCorrectionNeeded = "no-correction-needed";

    /// <summary>A backup already exists — the idempotence gate refused to run.</summary>
    public const string AlreadyCorrected = "already-corrected";

    /// <summary>A gate rejected the correction; the file is untouched.</summary>
    public const string Failed = "failed";
}

/// <summary>
/// One sync attempt's full verdict (B05). Gate failures are data, not
/// exceptions: the caller (history row / API response) shows the reason.
/// </summary>
public sealed record TimelineSyncOutcome(
    string Status,
    string? Reason = null,
    double Ratio = 1.0,
    double OffsetSeconds = 0,
    double Score = 0,
    int Cues = 0);

/// <summary>
/// Orchestrates one subtitle file's timeline synchronization (B05):
/// envelope extraction → ratio correlation → the gate ladder → the
/// backup-gated rewrite. Also owns the restore action.
/// </summary>
public interface ITimelineSyncService
{
    /// <summary>
    /// Synchronizes one subtitle file against one media video file.
    /// </summary>
    /// <exception cref="Lingarr.Server.Exceptions.FFprobeUnavailableException">ffmpeg cannot be resolved or started.</exception>
    /// <exception cref="InvalidOperationException">ffmpeg failed to decode the audio (no track / broken container).</exception>
    Task<TimelineSyncOutcome> SyncFileAsync(
        string subtitlePath,
        string videoPath,
        CancellationToken cancellationToken);

    /// <summary>
    /// Restores a synchronized file from its <c>.lingarr-tl-bk</c> backup
    /// (the backup moves back over the file).
    /// </summary>
    /// <exception cref="InvalidOperationException">No backup exists for the file.</exception>
    Task RestoreFileAsync(string subtitlePath, CancellationToken cancellationToken);
}

/// <inheritdoc />
/// <remarks>
/// The gate ladder, in order (algorithm spec §4 + implementation notes):
/// backup idempotence → parse → envelope → correlation → minimum score →
/// offset sanity (±700s) → no-op band (|offset| &lt; 200ms at R=1) →
/// rewrite. Every gate failure returns <see cref="TimelineSyncStatus.Failed"/>
/// with the file untouched — a failed correction never deletes or mutates
/// the subtitle.
/// </remarks>
public sealed class TimelineSyncService : ITimelineSyncService
{
    /// <summary>The no-op band: |offset| below this at R=1 needs no correction.</summary>
    public const double NoCorrectionThresholdSeconds = 0.2;

    /// <summary>The hard sanity bound on a candidate offset.</summary>
    public const double MaxOffsetSeconds = EnvelopeCorrelator.MaxOffsetSeconds;

    private readonly IAudioEnvelopeExtractor _envelopeExtractor;
    private readonly EnvelopeCorrelator _correlator;
    private readonly SubtitleTimelineRewriter _rewriter;
    private readonly ISettingService _settingService;
    private readonly ILogger<TimelineSyncService> _logger;

    public TimelineSyncService(
        IAudioEnvelopeExtractor envelopeExtractor,
        EnvelopeCorrelator correlator,
        SubtitleTimelineRewriter rewriter,
        ISettingService settingService,
        ILogger<TimelineSyncService> logger)
    {
        _envelopeExtractor = envelopeExtractor;
        _correlator = correlator;
        _rewriter = rewriter;
        _settingService = settingService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<TimelineSyncOutcome> SyncFileAsync(
        string subtitlePath,
        string videoPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(subtitlePath))
        {
            return new TimelineSyncOutcome(TimelineSyncStatus.Failed, $"subtitle file '{subtitlePath}' does not exist.");
        }

        if (!SubtitleTimelineRewriter.IsSupported(subtitlePath))
        {
            return new TimelineSyncOutcome(
                TimelineSyncStatus.Failed,
                $"unsupported subtitle format '{Path.GetExtension(subtitlePath)}' (expected .srt/.ass/.ssa/.vtt).");
        }

        // Idempotence gate first: a file that was already corrected once
        // never goes through the pipeline again until it is restored.
        if (File.Exists(SubtitleTimelineRewriter.BackupPathFor(subtitlePath)))
        {
            return new TimelineSyncOutcome(
                TimelineSyncStatus.AlreadyCorrected,
                "a previous correction's backup exists — restore the file before syncing it again.");
        }

        List<SubtitleItem> cues;
        try
        {
            cues = _rewriter.Read(subtitlePath);
        }
        catch (Exception ex)
        {
            return new TimelineSyncOutcome(
                TimelineSyncStatus.Failed, $"could not parse '{Path.GetFileName(subtitlePath)}': {ex.Message}");
        }

        // Extraction failures (missing ffmpeg, broken audio) propagate:
        // they are environment problems, not correction verdicts (AC3).
        var envelope = await _envelopeExtractor.ExtractAsync(videoPath, cancellationToken);

        var correlation = _correlator.Correlate(envelope, cues);
        if (correlation.FailureReason is not null || correlation.Chosen is null)
        {
            return new TimelineSyncOutcome(
                TimelineSyncStatus.Failed, correlation.FailureReason ?? "no candidate ratio could be evaluated.", Cues: cues.Count);
        }

        var chosen = correlation.Chosen;
        var minScore = await ResolveMinScoreAsync(cancellationToken);
        if (chosen.Score < minScore)
        {
            return new TimelineSyncOutcome(
                TimelineSyncStatus.Failed,
                $"correlation score {chosen.Score:0.000} is below the configured minimum {minScore:0.00} — the audio and subtitle do not line up.",
                chosen.Ratio, chosen.OffsetSeconds, chosen.Score, cues.Count);
        }

        if (Math.Abs(chosen.OffsetSeconds) > MaxOffsetSeconds)
        {
            return new TimelineSyncOutcome(
                TimelineSyncStatus.Failed,
                $"the best offset ({chosen.OffsetSeconds:0.0}s) exceeds the {MaxOffsetSeconds:0}s sanity limit.",
                chosen.Ratio, chosen.OffsetSeconds, chosen.Score, cues.Count);
        }

        if (Math.Abs(chosen.Ratio - 1.0) < 1e-12
            && Math.Abs(chosen.OffsetSeconds) < NoCorrectionThresholdSeconds)
        {
            return new TimelineSyncOutcome(
                TimelineSyncStatus.NoCorrectionNeeded,
                $"already aligned within {NoCorrectionThresholdSeconds * 1000:0}ms.",
                chosen.Ratio, chosen.OffsetSeconds, chosen.Score, cues.Count);
        }

        var rewrite = await _rewriter.RewriteAsync(subtitlePath, chosen.Ratio, chosen.OffsetSeconds, cancellationToken);
        _logger.LogInformation(
            "Timeline sync corrected {FilePath}: ratio {Ratio:0.000000} offset {OffsetMs:0}ms score {Score:0.000} ({Cues} cues, {Clamped} clamped).",
            subtitlePath, chosen.Ratio, chosen.OffsetSeconds * 1000, chosen.Score, rewrite.Cues, rewrite.ClampedCues);

        return new TimelineSyncOutcome(
            TimelineSyncStatus.Corrected,
            $"corrected {rewrite.Cues} cues (ratio {chosen.Ratio:0.000000}, offset {chosen.OffsetSeconds * 1000:0}ms, score {chosen.Score:0.000}" +
            $"{(rewrite.ClampedCues > 0 ? $", {rewrite.ClampedCues} clamped" : string.Empty)}).",
            chosen.Ratio, chosen.OffsetSeconds, chosen.Score, rewrite.Cues);
    }

    /// <inheritdoc />
    public Task RestoreFileAsync(string subtitlePath, CancellationToken cancellationToken)
    {
        var backup = SubtitleTimelineRewriter.BackupPathFor(subtitlePath);
        if (!File.Exists(backup))
        {
            throw new InvalidOperationException(
                $"no timeline-sync backup exists for '{Path.GetFileName(subtitlePath)}' — nothing to restore.");
        }

        // The backup moves back over the file (overwrite: the current file
        // is the corrected content; its loss is the point of restoring).
        // Works even when the corrected file was deleted in the meantime.
        File.Move(backup, subtitlePath, overwrite: true);
        _logger.LogInformation("Timeline sync restored {FilePath} from its backup.", subtitlePath);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Reads <c>timeline_sync_min_score</c>; missing/unparsable values fall
    /// back to the documented default 0.10, out-of-range values clamp into
    /// (0, 1] — a broken settings row must never wedge the sync.
    /// </summary>
    private async Task<double> ResolveMinScoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var raw = await _settingService.GetSetting(SettingKeys.SubtitleSearch.TimelineSyncMinScore);
        const double fallback = 0.10;
        if (string.IsNullOrWhiteSpace(raw)
            || !double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return fallback;
        }

        return Math.Clamp(parsed, 0.01, 1.0);
    }
}
