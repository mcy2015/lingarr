using Microsoft.AspNetCore.Authorization;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Models;
using Lingarr.Server.Models.Api;
using Lingarr.Server.Models.FileSystem;
using Lingarr.Server.Services.Subtitle;
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
    /// Imports uploaded subtitle files: stores them (converted to UTF-8) next to
    /// matched media when a binding is given, otherwise under the imports directory,
    /// and optionally enqueues a translation request per file. Unsupported
    /// extensions and per-file failures are reported individually.
    /// </summary>
    /// <param name="files">Subtitle files (.srt/.ass/.ssa).</param>
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
        [FromForm] string chineseConvert = "none")
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
            mediaType, mediaId, season, chineseConvert);
        return Ok(new ImportSubtitlesResponse { Results = results });
    }
}
