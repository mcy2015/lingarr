using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Models;
using Lingarr.Server.Models.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Lingarr.Server.Modules.Media;
using Lingarr.Server.Modules.Subtitle;
using Lingarr.Server.Modules.Translation;

namespace Lingarr.Server.Controllers;

[ApiController]
[Authorize("LingarrAccess")]
[Route("api/[controller]")]
public class MediaController : ControllerBase
{
    private readonly IMediaService _mediaService;
    private readonly IMediaLibraryService _mediaLibraryService;
    private readonly ISubtitleService _subtitleService;
    private readonly LingarrDbContext _dbContext;

    public MediaController(
        IMediaService mediaService,
        IMediaLibraryService mediaLibraryService,
        ISubtitleService subtitleService,
        LingarrDbContext dbContext)
    {
        _mediaService = mediaService;
        _mediaLibraryService = mediaLibraryService;
        _subtitleService = subtitleService;
        _dbContext = dbContext;
    }

    /// <summary>
    /// Every external subtitle file that lives next to one media item — the
    /// ground truth behind the coverage chips — plus the embedded subtitle
    /// tracks of the last ffprobe scan. Movies scan their directory, shows
    /// scan every season directory; embedded tracks aggregate over a show's
    /// episodes. Dual-language filenames (scjp, 简日…) report every language
    /// they carry.
    /// </summary>
    /// <response code="200">External files with detected languages, format and size; embedded tracks with normalized language, codec and text-based flag; whether the media has been probed at all.</response>
    /// <response code="404">Unknown media id.</response>
    [HttpGet("{mediaType}/{id}/subtitles")]
    public async Task<ActionResult<MediaSubtitlesResponse>> GetMediaSubtitles(
        MediaType mediaType,
        int id)
    {
        List<string> directories;
        // Media ids whose container files the embedded scan would probe —
        // drives the embeddedScanned flag below.
        List<int> probeableIds;
        if (mediaType == MediaType.Movie)
        {
            var movie = await _dbContext.Movies.FirstOrDefaultAsync(m => m.Id == id);
            if (movie == null) return NotFound();
            directories = string.IsNullOrEmpty(movie.Path) ? [] : [movie.Path];
            probeableIds = string.IsNullOrEmpty(movie.Path) || string.IsNullOrEmpty(movie.FileName)
                ? []
                : [movie.Id];
        }
        else
        {
            var show = await _dbContext.Shows
                .Include(s => s.Seasons)
                .FirstOrDefaultAsync(s => s.Id == id);
            if (show == null) return NotFound();
            directories = show.Seasons
                .Where(season => !string.IsNullOrEmpty(season.Path))
                .Select(season => season.Path!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            probeableIds = await _dbContext.Seasons
                .Where(season => season.ShowId == id)
                .SelectMany(season => season.Episodes)
                .Where(episode => !string.IsNullOrEmpty(episode.Path) && !string.IsNullOrEmpty(episode.FileName))
                .Select(episode => episode.Id)
                .ToListAsync();
        }

        var files = new List<MediaSubtitleFile>();
        foreach (var directory in directories)
        {
            var subtitles = await _subtitleService.GetAllSubtitles(directory);
            files.AddRange(subtitles.Select(subtitle => new MediaSubtitleFile
            {
                FileName = Path.GetFileName(subtitle.Path),
                Directory = Path.GetDirectoryName(subtitle.Path) ?? string.Empty,
                Languages = subtitle.Languages.Count > 0 ? subtitle.Languages : [subtitle.Language],
                Format = subtitle.Format.TrimStart('.'),
                Bytes = System.IO.File.Exists(subtitle.Path) ? new FileInfo(subtitle.Path).Length : 0
            }));
        }

        // Embedded tracks of the last scan: movies own their rows directly,
        // shows aggregate over their episodes' rows. The scan-marker rows
        // (StreamIndex -1) never surface — they only answer "was this probed".
        List<MediaSubtitleTrack> trackRows;
        List<int> scannedIds;
        if (mediaType == MediaType.Movie)
        {
            trackRows = await _dbContext.MediaSubtitleTracks
                .Where(track => track.MediaType == MediaType.Movie
                                && track.MediaId == id
                                && track.StreamIndex >= 0)
                .OrderBy(track => track.StreamIndex)
                .ToListAsync();
            scannedIds = await _dbContext.MediaSubtitleTracks
                .Where(track => track.MediaType == MediaType.Movie
                                && track.MediaId == id
                                && track.StreamIndex == MediaSubtitleTrack.ScanMarkerStreamIndex)
                .Select(track => track.MediaId)
                .ToListAsync();
        }
        else
        {
            trackRows = await _dbContext.MediaSubtitleTracks
                .Where(track => track.MediaType == MediaType.Episode
                                && probeableIds.Contains(track.MediaId)
                                && track.StreamIndex >= 0)
                .OrderBy(track => track.MediaId)
                .ThenBy(track => track.StreamIndex)
                .ToListAsync();
            scannedIds = await _dbContext.MediaSubtitleTracks
                .Where(track => track.MediaType == MediaType.Episode
                                && probeableIds.Contains(track.MediaId)
                                && track.StreamIndex == MediaSubtitleTrack.ScanMarkerStreamIndex)
                .Select(track => track.MediaId)
                .Distinct()
                .ToListAsync();
        }

        return Ok(new MediaSubtitlesResponse
        {
            Files = files.OrderBy(f => f.FileName, StringComparer.OrdinalIgnoreCase).ToList(),
            Embedded = trackRows.Select(track => new MediaEmbeddedTrack
            {
                MediaId = track.MediaId,
                StreamIndex = track.StreamIndex,
                Language = track.Language,
                RawLanguage = track.RawLanguage,
                Title = track.Title,
                Codec = track.Codec,
                IsTextBased = track.IsTextBased
            }).ToList(),
            // scanned only once every probeable file carries a scan marker
            EmbeddedScanned = probeableIds.Count > 0
                              && probeableIds.All(probeable => scannedIds.Contains(probeable))
        });
    }

    /// <summary>
    /// Matrix rows for the library screen (A4): subtitle track coverage,
    /// translation state and last activity per media item, paged. All params
    /// optional; a null mediaType mixes shows and movies in one list.
    /// </summary>
    [HttpGet("library")]
    public async Task<ActionResult<PagedResult<MediaLibraryItem>>?> GetLibrary(
        MediaType? mediaType,
        int? id,
        string? searchQuery,
        string? state,
        string? targetLanguage,
        string? orderBy,
        bool ascending = true,
        int pageSize = 20,
        int pageNumber = 1)
    {
        if (id is not null)
        {
            // media detail page: single-row lookup
            var single = await _mediaLibraryService.GetLibrary(
                mediaType,
                null,
                null,
                null,
                null,
                true,
                1,
                1000);
            var hit = single.Items.FirstOrDefault(item => item.Id == id);
            return hit is null ? NotFound() : Ok(new PagedResult<MediaLibraryItem>
            {
                Items = [hit],
                TotalCount = 1,
                PageNumber = 1,
                PageSize = 1
            });
        }

        var value = await _mediaLibraryService.GetLibrary(
            mediaType,
            searchQuery,
            state,
            targetLanguage,
            orderBy,
            ascending,
            pageNumber,
            pageSize);
        return Ok(value);
    }

    /// <summary>
    /// Rough cost estimate for translating the selected media into a target
    /// language (A5): uncovered episodes' source subtitle bytes priced at the
    /// default provider's rate.
    /// </summary>
    [HttpPost("estimate")]
    public async Task<ActionResult<TranslationEstimate>> Estimate([FromBody] LibraryEstimateRequest request)
    {
        var value = await _mediaLibraryService.Estimate(request.Items, request.TargetLanguage);
        return Ok(value);
    }
    
    /// <summary>
    /// Retrieves a paginated list of movies based on optional search criteria and sorting parameters.
    /// </summary>
    /// <param name="searchQuery">An optional search query to filter movies by title or other attributes.</param>
    /// <param name="orderBy">An optional parameter specifying the field to sort by (e.g., "Title", "DateAdded").</param>
    /// <param name="ascending">A boolean indicating whether to sort in ascending order (default is true).</param>
    /// <param name="pageSize">The number of movies to return per page (default is 20).</param>
    /// <param name="pageNumber">The page number to retrieve (default is 1).</param>
    /// <returns>Returns an HTTP 200 OK response with a paginated list of movies.</returns>
    [HttpGet("movies")]
    public async Task<ActionResult<PagedResult<MovieResponse>>> GetMovies(
        string? searchQuery,
        string? orderBy,
        bool ascending = true,
        int pageSize = 20,
        int pageNumber = 1)
    {
        var value = await _mediaService.GetMovies(
            searchQuery,
            orderBy,
            ascending,
            pageNumber,
            pageSize);

        return Ok(value);
    }
    
    /// <summary>
    /// Retrieves a paginated list of shows based on optional search criteria and sorting parameters.
    /// </summary>
    /// <param name="searchQuery">An optional search query to filter shows by title or other attributes.</param>
    /// <param name="orderBy">An optional parameter specifying the field to sort by (e.g., "Title", "DateAdded").</param>
    /// <param name="ascending">A boolean indicating whether to sort in ascending order (default is true).</param>
    /// <param name="pageSize">The number of shows to return per page (default is 20).</param>
    /// <param name="pageNumber">The page number to retrieve (default is 1).</param>
    /// <returns>Returns an HTTP 200 OK response with a paginated list of shows.</returns>
    [HttpGet("shows")]
    public async Task<ActionResult<PagedResult<Show>>> GetShows(
        string? searchQuery,
        string? orderBy,
        bool ascending = true,
        int pageSize = 20,
        int pageNumber = 1)
    {
        var value = await _mediaService.GetShows(
            searchQuery,
            orderBy,
            ascending,
            pageNumber,
            pageSize);
        return Ok(value);
    }
    
    /// <summary>
    /// Sets the include status of a specified media item for translation.
    /// When include is true, the item will be eligible for translation.
    /// When include is false, the item will be excluded from translation.
    /// </summary>
    /// <param name="request">The request object containing the media type, id, and include status.</param>
    [HttpPost("include")]
    public async Task<ActionResult> SetInclude([FromBody] IncludeRequest request)
    {
        var value = await _mediaService.SetInclude(request.MediaType, request.Id, request.Include);
        return Ok(value);
    }
    
    /// <summary>
    /// Sets the include status of all media items of a given type for translation.
    /// For shows, this cascades to all seasons and episodes.
    /// </summary>
    /// <param name="request">The request object containing the media type and include status.</param>
    [HttpPost("include/all")]
    public async Task<ActionResult> SetIncludeAll([FromBody] IncludeAllRequest request)
    {
        var value = await _mediaService.SetIncludeAll(request.MediaType, request.Include);
        return Ok(value);
    }
    
    /// <summary>
    /// Gets a summary of how many items of a given type are included for translation.
    /// </summary>
    /// <param name="mediaType">The type of media (Movie or Show).</param>
    [HttpGet("include/summary")]
    public async Task<ActionResult<IncludeSummary>> GetIncludeSummary([FromQuery] MediaType mediaType)
    {
        var value = await _mediaService.GetIncludeSummary(mediaType);
        return Ok(value);
    }
    
    /// <summary>
    /// Sets the amount of hours a media file needs to exist before translation is initiated.
    /// </summary>
    /// <param name="request">The request object containing the media type, id, and amount of hours to be set.</param>
    [HttpPost("threshold")]
    public async Task<ActionResult<PagedResult<Show>>> Threshold([FromBody] ThresholdRequest request)
    {
        var value = await _mediaService.Threshold(request.MediaType, request.Id, request.Hours);
        return Ok(value);
    }
}