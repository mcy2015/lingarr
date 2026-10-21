using Lingarr.Core.Data;
using Lingarr.Server.Modules.Subtitle.PostProcessing;
using Lingarr.Server.Modules.Subtitle.TimelineSync;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Lingarr.Server.Controllers;

/// <summary>
/// Manual shift request (B10): the subtitle file is addressed by its server
/// path and must provably belong to the named media — the same linkage rule
/// TimelineSyncController/BilingualMergeController apply (directory
/// containment plus video-base-name derivation).
/// </summary>
/// <param name="MediaType">"Movie" or "Episode".</param>
/// <param name="MediaId">Library id of the movie/episode that owns the subtitle.</param>
/// <param name="SubtitlePath">Server path of the subtitle file.</param>
/// <param name="OffsetMs">Shift applied to every cue, milliseconds (negative = earlier).</param>
public sealed record SubtitleShiftRequestBody(
    string MediaType,
    int MediaId,
    string SubtitlePath,
    double OffsetMs);

/// <summary>
/// Shift response: the applied offset plus the rewriter's real counts — how
/// many cues moved and how many had to be clamped (negative results lifted
/// to zero / collapsed durations repaired).
/// </summary>
public sealed record SubtitleShiftResponse(
    string SubtitlePath,
    double OffsetMs,
    int Cues,
    int ClampedCues,
    double LastEndSeconds);

/// <summary>
/// Single-file Chinese variant conversion request (B10): the manual
/// counterpart of the B01 library batch — same conversion core
/// (<see cref="ChineseVariantStage"/>), same idempotence (content that
/// would not change is reported, never written).
/// </summary>
/// <param name="MediaType">"Movie" or "Episode".</param>
/// <param name="MediaId">Library id of the movie/episode that owns the subtitle.</param>
/// <param name="SubtitlePath">Server path of the subtitle file.</param>
/// <param name="Mode">"simplified" or "traditional".</param>
public sealed record SubtitleVariantRequestBody(
    string MediaType,
    int MediaId,
    string SubtitlePath,
    string Mode);

/// <summary>
/// Variant conversion response. <c>Changed = false</c> is a no-change
/// verdict (already in the target variant, unsupported format, unparseable
/// file) delivered as 200 — the batch's skip semantics, never an error.
/// </summary>
public sealed record SubtitleVariantResponse(bool Changed, string? Reason);

/// <summary>
/// B10 subtitle-tools thin API layer: per-file manual actions that have no
/// dedicated endpoint of their own. Re-translation (api/translate/file),
/// bilingual re-render (api/translation-request/convert) and timeline sync
/// (api/timeline-sync/sync) are existing endpoints the client calls
/// directly; this controller adds the manual ±ms shift (the R=1 case of
/// the B05 rewriter's <c>t' = t×R + offset</c> transform, so the backup
/// gate, the atomic temp-file move and the format round-trip all apply
/// unchanged) and the single-file variant conversion. Ownership validation
/// follows the BilingualMergeController precedent verbatim.
/// </summary>
[ApiController]
[Authorize("LingarrAccess")]
[Route("api/subtitle/tools")]
public sealed class SubtitleToolsController : ControllerBase
{
    private readonly LingarrDbContext _dbContext;
    private readonly SubtitleTimelineRewriter _rewriter;
    private readonly ChineseVariantStage _variantStage;

    public SubtitleToolsController(
        LingarrDbContext dbContext,
        SubtitleTimelineRewriter rewriter,
        ChineseVariantStage variantStage)
    {
        _dbContext = dbContext;
        _rewriter = rewriter;
        _variantStage = variantStage;
    }

    /// <summary>
    /// Moves every cue of one owned subtitle file by ±offsetMs. The write
    /// goes through the B05 rewriter (ratio pinned to 1.0), so the original
    /// is backed up as <c>.lingarr-tl-bk</c> before any byte changes — a
    /// second shift refuses until the file is restored (timeline-sync
    /// restore), exactly like a second automated correction.
    /// </summary>
    [HttpPost("shift")]
    public async Task<ActionResult<SubtitleShiftResponse>> Shift(
        [FromBody] SubtitleShiftRequestBody body,
        CancellationToken cancellationToken)
    {
        if (double.IsNaN(body.OffsetMs) || Math.Abs(body.OffsetMs) < double.Epsilon)
        {
            return BadRequest(new { message = "offsetMs must be a non-zero millisecond count." });
        }

        var media = await ResolveOwnedMediaAsync(body.MediaType, body.MediaId, body.SubtitlePath);
        if (media.Result is not null)
        {
            return media.Result;
        }

        var subtitlePath = media.ResolvedPath!;
        if (!SubtitleTimelineRewriter.IsSupported(subtitlePath))
        {
            return BadRequest(new
            {
                message =
                    $"unsupported subtitle format '{Path.GetExtension(subtitlePath)}' (expected .srt/.ass/.ssa/.vtt)."
            });
        }

        // The rewriter's backup gate, surfaced as a friendly 400 instead of
        // the raw IOException the copy(overwrite: false) would throw.
        if (System.IO.File.Exists(SubtitleTimelineRewriter.BackupPathFor(subtitlePath)))
        {
            return BadRequest(new
            {
                message =
                    "a previous correction's backup exists — restore the file (timeline-sync restore) before shifting it again."
            });
        }

        TimelineRewriteResult result;
        try
        {
            // Offset-only: the R=1 case of the rewriter's transform.
            result = await _rewriter.RewriteAsync(
                subtitlePath, 1.0, body.OffsetMs / 1000.0, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The rewrite is all-or-nothing (parse first, temp file + atomic
            // move last), so any failure here leaves the file untouched —
            // an actionable 400 with the reason, never a 500.
            return BadRequest(new
            {
                message = $"could not shift '{Path.GetFileName(subtitlePath)}': {ex.Message}"
            });
        }

        return Ok(new SubtitleShiftResponse(
            subtitlePath, body.OffsetMs, result.Cues, result.ClampedCues, result.LastEndSeconds));
    }

    /// <summary>
    /// Converts one owned subtitle file's Chinese variant (简↔繁) through
    /// the same stage the B01 batch runs. Idempotent: a file already in the
    /// target variant (or with no Chinese content) is NOT rewritten and the
    /// response says so — 200 with <c>changed = false</c>.
    /// </summary>
    [HttpPost("variant")]
    public async Task<ActionResult<SubtitleVariantResponse>> ConvertVariant(
        [FromBody] SubtitleVariantRequestBody body,
        CancellationToken cancellationToken)
    {
        var mode = body.Mode?.Trim().ToLowerInvariant();
        if (mode is not ("simplified" or "traditional"))
        {
            return BadRequest(new { message = "mode must be 'simplified' or 'traditional'." });
        }

        var media = await ResolveOwnedMediaAsync(body.MediaType, body.MediaId, body.SubtitlePath);
        if (media.Result is not null)
        {
            return media.Result;
        }

        PostProcessResult result;
        try
        {
            result = await _variantStage.ProcessAsync(
                media.ResolvedPath!, new PostProcessContext(mode), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return BadRequest(new
            {
                message = $"could not convert '{Path.GetFileName(media.ResolvedPath!)}': {ex.Message}"
            });
        }

        if (result.Error is not null)
        {
            return BadRequest(new { message = result.Error });
        }

        return Ok(new SubtitleVariantResponse(result.Changed, result.Reason));
    }

    /// <summary>
    /// Shared media + ownership validation (BilingualMergeController
    /// precedent): the subtitle path must resolve inside the media's own
    /// directory and derive from the video's base name — the manual tools
    /// can never touch files of another media item (or arbitrary server
    /// paths). The video file itself is not needed on disk.
    /// </summary>
    private async Task<(ActionResult? Result, string? ResolvedPath)> ResolveOwnedMediaAsync(
        string? mediaType,
        int mediaId,
        string? subtitlePath)
    {
        if (string.IsNullOrWhiteSpace(subtitlePath))
        {
            return (BadRequest(new { message = "subtitlePath is required." }), null);
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
                    return (BadRequest(new { message = $"Movie {mediaId} does not exist in the library." }), null);
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
                    return (BadRequest(new { message = $"Episode {mediaId} does not exist in the library." }), null);
                }

                directoryPath = episode.Path;
                videoFileName = episode.FileName;
                break;
            default:
                return (BadRequest(new { message = "mediaType must be 'Movie' or 'Episode'." }), null);
        }

        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return (BadRequest(new { message = "the media item has no file location." }), null);
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
            return (BadRequest(new { message = $"subtitle path '{subtitlePath}' is not a valid path." }), null);
        }

        var normalizedSubtitle = NormalizePath(resolvedSubtitle);
        var normalizedDirectory = NormalizePath(resolvedDirectory);
        if (!normalizedSubtitle.StartsWith(normalizedDirectory + "/", StringComparison.OrdinalIgnoreCase))
        {
            return (BadRequest(new
            {
                message = $"the subtitle does not belong to this media (it is not inside '{directoryPath}')."
            }), null);
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
            }), null);
        }

        if (!System.IO.File.Exists(resolvedSubtitle))
        {
            return (BadRequest(new { message = $"subtitle file '{subtitlePath}' was not found on the server." }), null);
        }

        return (null, resolvedSubtitle);
    }

    /// <summary>Canonical "/"-separated, trailing-slash-free comparison form.</summary>
    private static string NormalizePath(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.Replace(Path.AltDirectorySeparatorChar, '/');
    }
}
