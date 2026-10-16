using Hangfire;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Filters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.OpenApi.Extensions;

namespace Lingarr.Server.Modules.Infrastructure;

public class CleanupJob
{
    /// <summary>
    /// Retention window in days for everything this job prunes: translation
    /// request rows and orphaned embedded-track extractions (audit S6 —
    /// extractions are regenerable, so a conservative week is safe).
    /// </summary>
    private const int RetentionDays = 7;

    /// <summary>
    /// Download-history retention in days. A judgment call, deliberately not
    /// a setting: the history feeds the per-media subtitle cards and the
    /// search KPIs, where anything past half a year is noise, while nightly
    /// search rounds would otherwise grow <c>subtitle_downloads</c> without
    /// bound. Failed attempts included — the blacklist (not the history) is
    /// what stops repeated retries.
    /// </summary>
    private const int SubtitleDownloadRetentionDays = 180;

    private readonly LingarrDbContext _dbContext;
    private readonly ILogger<CleanupJob> _logger;
    private readonly IScheduleService _scheduleService;
    private readonly IHostEnvironment _environment;

    public CleanupJob(
        LingarrDbContext dbContext,
        IScheduleService scheduleService,
        IHostEnvironment environment,
        ILogger<CleanupJob> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
        _scheduleService = scheduleService;
        _environment = environment;
    }

    [AutomaticRetry(Attempts = 0)]
    [Queue("system")]
    public async Task Execute()
    {
        var jobName = JobContextFilter.GetCurrentJobTypeName();
        await _scheduleService.UpdateJobState(jobName, JobStatus.Processing.GetDisplayName());

        var cutoff = DateTimeOffset.UtcNow.AddDays(-RetentionDays);
        var removedRequests = await RemoveExpiredTranslationRequests(cutoff);
        var removedExtractions = await RemoveOrphanedEmbeddedExtractions(cutoff);
        var removedDownloads = await RemoveExpiredSubtitleDownloads();

        await _scheduleService.UpdateJobState(jobName, JobStatus.Succeeded.GetDisplayName());
        _logger.LogInformation(
            "Cleanup removed {RequestCount} translation requests and {ExtractionCount} orphaned embedded track extractions older than {Days} days, plus {DownloadCount} download history rows older than {DownloadDays} days.",
            removedRequests, removedExtractions, RetentionDays, removedDownloads, SubtitleDownloadRetentionDays);
    }

    /// <summary>
    /// Translation request rows that outlived the retention window, removed
    /// set-based (audit S6/S2 precedent): children first, then the requests —
    /// three ExecuteDeletes, a constant statement count that does not scale
    /// with the expired-row volume (bounded by retention anyway, so no
    /// chunking is needed), zero rows materialized into the change tracker.
    /// </summary>
    private async Task<int> RemoveExpiredTranslationRequests(DateTimeOffset cutoff)
    {
        await _dbContext.TranslationRequestEvents
            .Where(requestEvent => _dbContext.TranslationRequests
                .Any(request => request.CreatedAt < cutoff
                                && request.Id == requestEvent.TranslationRequestId))
            .ExecuteDeleteAsync();

        await _dbContext.TranslationRequestLines
            .Where(line => _dbContext.TranslationRequests
                .Any(request => request.CreatedAt < cutoff
                                && request.Id == line.TranslationRequestId))
            .ExecuteDeleteAsync();

        return await _dbContext.TranslationRequests
            .Where(request => request.CreatedAt < cutoff)
            .ExecuteDeleteAsync();
    }

    /// <summary>
    /// Download-history rows (subtitle_downloads) past their retention
    /// window — every outcome lands a row per attempt, so nightly rounds
    /// would grow the table without bound. Set-based like the request
    /// cleanup above.
    /// </summary>
    private async Task<int> RemoveExpiredSubtitleDownloads()
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-SubtitleDownloadRetentionDays);
        return await _dbContext.SubtitleDownloads
            .Where(download => download.CreatedAt < cutoff)
            .ExecuteDeleteAsync();
    }

    /// <summary>
    /// Deletes stale extraction-cache files under config/imports/embedded
    /// (audit S6). The extraction cache is keyed by the container's scan key
    /// (path + mtime): a replaced video orphans its old extraction file and
    /// nothing else removed it. A file goes when it is older than the
    /// retention window AND its scan key matches no current scan-marker row
    /// (marker <c>MediaHash</c> folded with the same SHA-256[..12] the
    /// <c>EmbeddedTrackService</c> file naming uses). Referenced extractions
    /// are kept regardless of age so <c>ExtractTrack</c> can keep reusing
    /// them; a deleted-but-still-current extraction would simply re-run
    /// ffmpeg on demand. Files not matching the extraction naming scheme are
    /// not ours to delete and stay.
    /// </summary>
    private async Task<int> RemoveOrphanedEmbeddedExtractions(DateTimeOffset cutoff)
    {
        var directory = Path.Combine(_environment.ContentRootPath, "config", "imports", "embedded");
        if (!Directory.Exists(directory))
        {
            // Fresh install or nothing ever extracted — nothing to clean.
            return 0;
        }

        var currentSafeKeys = (await _dbContext.MediaSubtitleTracks
                .Where(track => track.StreamIndex == MediaSubtitleTrack.ScanMarkerStreamIndex)
                .Select(track => track.MediaHash)
                .ToListAsync())
            .OfType<string>()
            .Select(ExtractionSafeKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            if (!TryParseExtractionFileName(Path.GetFileName(file), out var safeKey)
                || currentSafeKeys.Contains(safeKey)
                || File.GetLastWriteTimeUtc(file) >= cutoff.UtcDateTime)
            {
                continue;
            }

            try
            {
                File.Delete(file);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort: a locked file only waits for the next run.
                _logger.LogWarning(ex, "Could not delete orphaned embedded extraction {File}", file);
            }
        }

        if (removed > 0)
        {
            _logger.LogInformation(
                "Removed {Count} orphaned embedded track extractions older than {Days} days from {Directory}.",
                removed, RetentionDays, directory);
        }

        return removed;
    }

    /// <summary>
    /// Folds a scan key exactly like the extraction file naming in
    /// <c>EmbeddedTrackService.ExtractionFilePath</c>: the first 12 hex
    /// characters of the SHA-256 digest. Keep both in sync — this is how a
    /// cache file is matched back to its marker row.
    /// </summary>
    private static string ExtractionSafeKey(string scanKey) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(scanKey)))[..12];

    /// <summary>
    /// Recognizes the extraction cache name
    /// <c>embedded.{movie|episode}.{id}.s{stream}.{safeKey}.srt</c>. Anything
    /// else in the directory is left untouched.
    /// </summary>
    private static bool TryParseExtractionFileName(string fileName, out string safeKey)
    {
        safeKey = string.Empty;
        var parts = fileName.Split('.');
        if (parts.Length != 6
            || parts[0] != "embedded"
            || (parts[1] != "movie" && parts[1] != "episode")
            || parts[5] != "srt"
            || !int.TryParse(parts[2], out _)
            || parts[3].Length <= 1
            || parts[3][0] != 's'
            || !int.TryParse(parts[3].AsSpan(1), out _)
            || parts[4].Length != 12
            || parts[4].Any(character => !Uri.IsHexDigit(character)))
        {
            return false;
        }

        safeKey = parts[4];
        return true;
    }
}
