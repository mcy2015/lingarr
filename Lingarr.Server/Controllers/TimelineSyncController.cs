using Lingarr.Core.Data;
using Lingarr.Core.Enum;
using Lingarr.Server.Exceptions;
using Lingarr.Server.Modules.Subtitle.TimelineSync;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Lingarr.Server.Controllers;

/// <summary>
/// Manual timeline-sync request (B05): the subtitle file is addressed by
/// its server path and must provably belong to the named media — the
/// directory must be the media's own and the file name must derive from
/// the video's base name (the same linkage rule SubtitleService applies
/// when attributing subtitle files to media).
/// </summary>
/// <param name="MediaType">"Movie" or "Episode".</param>
/// <param name="MediaId">Library id of the movie/episode that owns the subtitle.</param>
/// <param name="SubtitlePath">Server path of the subtitle file.</param>
public sealed record TimelineSyncRequestBody(
    string MediaType,
    int MediaId,
    string SubtitlePath);

/// <summary>
/// Timeline-sync response: the outcome status plus the correction's
/// parameters. Gate failures are <c>status = "failed"</c> with the reason —
/// the sync ran; its verdict is data, not an HTTP error. Request-shape
/// problems (unknown media, foreign path) are 400s.
/// </summary>
public sealed record TimelineSyncResponse(
    string Status,
    string? Reason,
    double Ratio,
    double OffsetSeconds,
    double Score,
    int Cues);

/// <summary>Restore response.</summary>
public sealed record TimelineSyncRestoreResponse(string SubtitlePath);

/// <summary>
/// Manual timeline-sync surface (B05): trigger one file's synchronization
/// and restore a synchronized file from its <c>.lingarr-tl-bk</c> backup.
/// The download-pipeline stage is the automated counterpart; these
/// endpoints work regardless of the on-download setting.
/// </summary>
[ApiController]
[Authorize("LingarrAccess")]
[Route("api/timeline-sync")]
public sealed class TimelineSyncController : ControllerBase
{
    private readonly LingarrDbContext _dbContext;
    private readonly ITimelineSyncService _syncService;

    public TimelineSyncController(
        LingarrDbContext dbContext,
        ITimelineSyncService syncService)
    {
        _dbContext = dbContext;
        _syncService = syncService;
    }

    /// <summary>
    /// Synchronizes one subtitle file against its media's audio. The media
    /// lookup resolves the video path; ownership of the subtitle path is
    /// verified before the service runs.
    /// </summary>
    [HttpPost("sync")]
    public async Task<ActionResult<TimelineSyncResponse>> Sync(
        [FromBody] TimelineSyncRequestBody body,
        CancellationToken cancellationToken)
    {
        var media = await ResolveOwnedMediaAsync(
            body.MediaType, body.MediaId, body.SubtitlePath, needVideo: true);
        if (media.Result is not null)
        {
            return media.Result;
        }

        TimelineSyncOutcome outcome;
        try
        {
            outcome = await _syncService.SyncFileAsync(
                media.SubtitlePath!, media.VideoPath!, cancellationToken);
        }
        catch (FFprobeUnavailableException ex)
        {
            // Environment problem with an install hint — actionable 400,
            // never a 500 (translate-test precedent).
            return BadRequest(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }

        return Ok(new TimelineSyncResponse(
            outcome.Status, outcome.Reason, outcome.Ratio, outcome.OffsetSeconds, outcome.Score, outcome.Cues));
    }

    /// <summary>
    /// Restores a synchronized subtitle file from its backup (the backup
    /// moves back over the corrected file). The media's video is not
    /// needed.
    /// </summary>
    [HttpPost("restore")]
    public async Task<ActionResult<TimelineSyncRestoreResponse>> Restore(
        [FromBody] TimelineSyncRequestBody body,
        CancellationToken cancellationToken)
    {
        var media = await ResolveOwnedMediaAsync(
            body.MediaType, body.MediaId, body.SubtitlePath, needVideo: false);
        if (media.Result is not null)
        {
            return media.Result;
        }

        try
        {
            await _syncService.RestoreFileAsync(media.SubtitlePath!, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }

        return Ok(new TimelineSyncRestoreResponse(media.SubtitlePath!));
    }

    /// <summary>
    /// Shared media + ownership validation. Returns either a terminal
    /// error result or the validated (subtitle, video) pair. The subtitle
    /// path must sit inside the media's directory and derive from the
    /// video's base name — the manual endpoints can never touch files of
    /// another media item (or arbitrary server paths).
    /// </summary>
    private async Task<(ActionResult? Result, string? SubtitlePath, string? VideoPath)> ResolveOwnedMediaAsync(
        string? mediaType,
        int mediaId,
        string? subtitlePath,
        bool needVideo)
    {
        if (string.IsNullOrWhiteSpace(subtitlePath))
        {
            return (BadRequest(new { message = "subtitlePath is required." }), null, null);
        }

        string? directoryPath;
        string? videoFileName;
        switch (mediaType?.Trim().ToLowerInvariant())
        {
            case "movie":
                var movie = await _dbContext.Movies
                    .Where(m => m.Id == mediaId)
                    .Select(m => new { m.Path, m.FileName })
                    .FirstOrDefaultAsync();
                if (movie is null)
                {
                    return (BadRequest(new { message = $"Movie {mediaId} does not exist in the library." }), null, null);
                }

                directoryPath = movie.Path;
                videoFileName = movie.FileName;
                break;
            case "episode":
                var episode = await _dbContext.Episodes
                    .Where(e => e.Id == mediaId)
                    .Select(e => new { e.Path, e.FileName })
                    .FirstOrDefaultAsync();
                if (episode is null)
                {
                    return (BadRequest(new { message = $"Episode {mediaId} does not exist in the library." }), null, null);
                }

                directoryPath = episode.Path;
                videoFileName = episode.FileName;
                break;
            default:
                return (BadRequest(new { message = "mediaType must be 'Movie' or 'Episode'." }), null, null);
        }

        if (string.IsNullOrWhiteSpace(directoryPath)
            || (needVideo && string.IsNullOrWhiteSpace(videoFileName)))
        {
            return (BadRequest(new { message = "the media item has no file location." }), null, null);
        }

        // Resolve both spellings before comparing so "..", duplicate
        // separators and mixed separators cannot smuggle a foreign path
        // past the containment check (path-traversal precedent).
        string resolvedSubtitle;
        string resolvedDirectory;
        try
        {
            resolvedSubtitle = Path.GetFullPath(subtitlePath);
            resolvedDirectory = Path.GetFullPath(directoryPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return (BadRequest(new { message = $"subtitlePath '{subtitlePath}' is not a valid path." }), null, null);
        }

        var normalizedSubtitle = NormalizePath(resolvedSubtitle);
        var normalizedDirectory = NormalizePath(resolvedDirectory);
        if (!normalizedSubtitle.StartsWith(normalizedDirectory + "/", StringComparison.OrdinalIgnoreCase))
        {
            return (BadRequest(new
            {
                message = $"the subtitle does not belong to this media (it is not inside '{directoryPath}')."
            }), null, null);
        }

        var subtitleName = Path.GetFileName(normalizedSubtitle);
        var baseName = videoFileName is null ? null : SubtitleTimelineRewriter.VideoBaseName(videoFileName);
        if (baseName is not null
            && !subtitleName.Equals(baseName, StringComparison.OrdinalIgnoreCase)
            && !subtitleName.StartsWith(baseName + ".", StringComparison.OrdinalIgnoreCase))
        {
            return (BadRequest(new
            {
                message = $"the subtitle does not derive from the media's video file name ('{videoFileName}')."
            }), null, null);
        }

        if (!System.IO.File.Exists(resolvedSubtitle))
        {
            return (BadRequest(new { message = $"subtitle file '{subtitlePath}' was not found on the server." }), null, null);
        }

        string? videoPath = null;
        if (needVideo)
        {
            videoPath = Path.Combine(resolvedDirectory, videoFileName!);
            if (!System.IO.File.Exists(videoPath))
            {
                return (BadRequest(new
                {
                    message = $"the media's video file '{videoFileName}' was not found on the server."
                }), null, null);
            }
        }

        return (null, resolvedSubtitle, videoPath);
    }

    /// <summary>Canonical "/"-separated, trailing-slash-free comparison form.</summary>
    private static string NormalizePath(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.Replace(Path.AltDirectorySeparatorChar, '/');
    }
}
