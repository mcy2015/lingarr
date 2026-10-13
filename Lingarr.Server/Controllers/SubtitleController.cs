using Microsoft.AspNetCore.Authorization;
using Lingarr.Server.Models;
using Lingarr.Server.Models.Api;
using Lingarr.Server.Models.FileSystem;
using Lingarr.Server.Modules.Subtitle;
using Microsoft.AspNetCore.Mvc;

namespace Lingarr.Server.Controllers;

[ApiController]
[Authorize("LingarrAccess")]
[Route("api/[controller]")]
public class SubtitleController : ControllerBase
{
    private readonly ISubtitleService _subtitleService;
    private readonly ISubtitleImportService _subtitleImportService;

    public SubtitleController(ISubtitleService subtitleService, ISubtitleImportService subtitleImportService)
    {
        _subtitleService = subtitleService;
        _subtitleImportService = subtitleImportService;
    }

    /// <summary>
    /// Retrieves a list of subtitle files located at the specified path.
    /// </summary>
    /// <param name="subtitlePath">The directory path to search for subtitle files.This path is relative to the media folder
    /// and should not start with a forward slash.</param>
    /// <returns>Returns an HTTP 200 OK response with a list of <see cref="Subtitles"/> objects found at the specified path.</returns>
    [HttpPost("all")]
    public async Task<ActionResult<List<Subtitles>>> GetAllSubtitles([FromBody] SubtitlePath subtitlePath)
    {
        var value = await _subtitleService.GetAllSubtitles(subtitlePath.Path);
        return Ok(value);
    }

    /// <summary>
    /// Parses season/episode/language hints out of subtitle file names for the
    /// import workbench's mapping preview. Pure filename analysis — no media lookup.
    /// </summary>
    [HttpPost("parse-filenames")]
    public ActionResult<List<SubtitleFilenameParseResult>> ParseFileNames(
        [FromBody] SubtitleFilenameParseRequest request)
    {
        var results = request.Filenames.Select(fileName =>
        {
            var info = SubtitleFilenameParser.Parse(fileName);
            return new SubtitleFilenameParseResult
            {
                FileName = fileName,
                Season = info.Season,
                Episode = info.Episode,
                MatchTier = info.MatchTier.ToString(),
                LanguageTags = [.. info.LanguageTags]
            };
        }).ToList();

        return Ok(results);
    }

    /// <summary>
    /// Lists supported subtitle files under a server directory, recursively
    /// (A7) — feeds the import workbench's path scan.
    /// </summary>
    [HttpPost("scan-path")]
    public async Task<ActionResult<List<ScannedSubtitleFile>>> ScanPath(
        [FromBody] SubtitlePath request)
    {
        if (string.IsNullOrWhiteSpace(request.Path))
        {
            return BadRequest(new { message = "Path is required." });
        }

        try
        {
            return Ok(await _subtitleImportService.ScanPath(request.Path));
        }
        catch (DirectoryNotFoundException)
        {
            return NotFound(new { message = $"Directory '{request.Path}' does not exist on the server." });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Imports subtitle files already on the server (paths from scan-path):
    /// same storage, binding, dedup and translation semantics as the upload
    /// import.
    /// </summary>
    [HttpPost("import-paths")]
    public async Task<ActionResult<ImportSubtitlesResponse>> ImportPaths(
        [FromBody] ImportPathsRequest request)
    {
        if (request.Paths.Count == 0)
        {
            return BadRequest(new { message = "No paths provided." });
        }

        var results = await _subtitleImportService.ImportPaths(
            request.Paths,
            request.SourceLanguage,
            request.TargetLanguage,
            request.Translate,
            request.MediaType,
            request.MediaId,
            request.Season,
            request.ChineseConvert,
            request.IsBilingual);
        return Ok(new ImportSubtitlesResponse { Results = results });
    }

    /// <summary>
    /// Imports uploaded subtitle files: stores them (converted to UTF-8) next to
    /// matched media when a binding is given, otherwise under the imports directory,
    /// and optionally enqueues a translation request per file. Unsupported
    /// extensions and per-file failures are reported individually.
    /// </summary>
    /// <param name="files">Subtitle files (.srt/.ass/.ssa/.vtt).</param>
    /// <param name="sourceLanguage">Source language code for queued translations.</param>
    /// <param name="targetLanguage">Target language code for queued translations.</param>
    /// <param name="translate">When true, each stored file is queued for translation.</param>
    /// <param name="mediaType">Optional binding: "Movie" or "Show".</param>
    /// <param name="mediaId">Movie/Show id for the binding.</param>
    /// <param name="season">Season number when binding to a show.</param>
    [HttpPost("import")]
    [RequestSizeLimit(100 * 1024 * 1024)]
    public async Task<ActionResult<ImportSubtitlesResponse>> Import(
        List<IFormFile> files,
        [FromForm] string sourceLanguage = "en",
        [FromForm] string targetLanguage = "nl",
        [FromForm] bool translate = false,
        [FromForm] string? mediaType = null,
        [FromForm] int? mediaId = null,
        [FromForm] int? season = null,
        [FromForm] string chineseConvert = "none",
        [FromForm] bool isBilingual = false)
    {
        if (files.Count == 0)
        {
            return BadRequest(new { message = "No subtitle files provided." });
        }

        if (translate && string.IsNullOrWhiteSpace(targetLanguage))
        {
            return BadRequest(new { message = "Target language is required when queuing translations." });
        }

        if ((mediaId is not null || !string.IsNullOrEmpty(mediaType))
            && (mediaId is null || string.IsNullOrEmpty(mediaType)
                || (!(mediaType.Equals("Movie", StringComparison.OrdinalIgnoreCase)
                    || mediaType.Equals("Show", StringComparison.OrdinalIgnoreCase)))))
        {
            return BadRequest(new { message = "mediaType must be 'Movie' or 'Show' together with mediaId." });
        }

        if (mediaType?.Equals("Show", StringComparison.OrdinalIgnoreCase) == true && mediaId is not null && season is null)
        {
            return BadRequest(new { message = "season is required when importing into a show." });
        }

        var results = await _subtitleImportService.ImportFiles(files, sourceLanguage, targetLanguage, translate,
            mediaType, mediaId, season, chineseConvert, isBilingual);
        return Ok(new ImportSubtitlesResponse { Results = results });
    }
}
