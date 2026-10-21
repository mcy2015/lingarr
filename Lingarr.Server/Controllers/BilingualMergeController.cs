using Lingarr.Core.Data;
using Lingarr.Server.Modules.Subtitle;
using Lingarr.Server.Modules.Subtitle.TimelineSync;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Lingarr.Server.Controllers;

/// <summary>
/// Two-file bilingual merge request (B06). Both subtitle files are
/// addressed by their server paths and must provably belong to the named
/// media — the same linkage rule TimelineSyncController applies: the
/// directory must be the media's own and the file names must derive from
/// the video's base name. The primary language is the caller's choice
/// (which of the two files is the main language); it decides the top line
/// and the output's primary language tag.
/// </summary>
/// <param name="MediaType">"Movie" or "Episode".</param>
/// <param name="MediaId">Library id of the movie/episode that owns both subtitles.</param>
/// <param name="PrimaryPath">Server path of the main-language (top line) subtitle file.</param>
/// <param name="PrimaryLanguage">Language code of the primary file (e.g. "zh").</param>
/// <param name="SecondaryPath">Server path of the secondary (appended below) subtitle file.</param>
/// <param name="SecondaryLanguage">Language code of the secondary file (e.g. "en").</param>
public sealed record BilingualMergeRequestBody(
    string MediaType,
    int MediaId,
    string PrimaryPath,
    string PrimaryLanguage,
    string SecondaryPath,
    string SecondaryLanguage);

/// <summary>
/// Merge response: the written output plus the pairing verdict — how many
/// cues paired into bilingual cues and how many were preserved monolingual
/// per side. Request-shape problems (unknown media, foreign path, same
/// language, both files empty) are 400s.
/// </summary>
public sealed record BilingualMergeResponse(
    string OutputPath,
    string OutputFileName,
    int PrimaryCues,
    int SecondaryCues,
    int PairedCues,
    int PrimaryOnlyCues,
    int SecondaryOnlyCues);

/// <summary>
/// Two-file bilingual merge surface (B06): merge two already-existing
/// external subtitle files of one media item into one bilingual file
/// (<c>{base}.{primary}.{secondary}.srt|ass</c>, naming template
/// respected). The manual tool the B10 subtitle-tools panel will
/// aggregate; translation-time bilingual output is a separate, existing
/// capability.
/// </summary>
[ApiController]
[Authorize("LingarrAccess")]
[Route("api/subtitle/bilingual-merge")]
public sealed class BilingualMergeController : ControllerBase
{
    private readonly LingarrDbContext _dbContext;
    private readonly IBilingualMergeService _mergeService;

    public BilingualMergeController(
        LingarrDbContext dbContext,
        IBilingualMergeService mergeService)
    {
        _dbContext = dbContext;
        _mergeService = mergeService;
    }

    /// <summary>
    /// Merges the two files: cues overlapping by ≥50% of the shorter cue
    /// pair into bilingual cues (primary language on top), every unpaired
    /// cue is preserved monolingual. Ownership of both paths is verified
    /// before the service runs.
    /// </summary>
    [HttpPost("merge")]
    public async Task<ActionResult<BilingualMergeResponse>> Merge(
        [FromBody] BilingualMergeRequestBody body,
        CancellationToken cancellationToken)
    {
        var media = await ResolveMediaAsync(body.MediaType, body.MediaId);
        if (media.Result is not null)
        {
            return media.Result;
        }

        var primary = ValidateOwnedPath(body.PrimaryPath, media.DirectoryPath!, media.VideoFileName);
        if (primary.Result is not null)
        {
            return primary.Result;
        }

        var secondary = ValidateOwnedPath(body.SecondaryPath, media.DirectoryPath!, media.VideoFileName);
        if (secondary.Result is not null)
        {
            return secondary.Result;
        }

        BilingualMergeResult result;
        try
        {
            result = await _mergeService.MergeAsync(
                primary.ResolvedPath!,
                body.PrimaryLanguage,
                secondary.ResolvedPath!,
                body.SecondaryLanguage,
                cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }

        return Ok(new BilingualMergeResponse(
            result.OutputPath,
            Path.GetFileName(result.OutputPath),
            result.PrimaryCues,
            result.SecondaryCues,
            result.PairedCues,
            result.PrimaryOnlyCues,
            result.SecondaryOnlyCues));
    }

    /// <summary>
    /// Resolves the owning media item (TimelineSync precedent): Movie and
    /// Episode are the addressable units; the media's directory and video
    /// file name feed the ownership check. The video file itself is not
    /// needed on disk — the merge never decodes media audio.
    /// </summary>
    private async Task<(ActionResult? Result, string? DirectoryPath, string? VideoFileName)> ResolveMediaAsync(
        string? mediaType,
        int mediaId)
    {
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

        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return (BadRequest(new { message = "the media item has no file location." }), null, null);
        }

        return (null, directoryPath, videoFileName);
    }

    /// <summary>
    /// Ownership validation of one subtitle path (TimelineSync precedent):
    /// resolved-containment inside the media's directory (no "..", mixed
    /// separators or duplicate separators smuggling a foreign path past
    /// the check) plus the file name deriving from the video's base name.
    /// </summary>
    private (ActionResult? Result, string? ResolvedPath) ValidateOwnedPath(
        string? subtitlePath,
        string directoryPath,
        string? videoFileName)
    {
        if (string.IsNullOrWhiteSpace(subtitlePath))
        {
            return (BadRequest(new { message = "both subtitle paths are required." }), null);
        }

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
