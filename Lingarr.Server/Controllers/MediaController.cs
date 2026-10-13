using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Models;
using Lingarr.Server.Models.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Lingarr.Server.Modules.Media;
using Lingarr.Server.Modules.Translation;

namespace Lingarr.Server.Controllers;

[ApiController]
[Authorize("LingarrAccess")]
[Route("api/[controller]")]
public class MediaController : ControllerBase
{
    private readonly IMediaService _mediaService;
    private readonly IMediaLibraryService _mediaLibraryService;

    public MediaController(
        IMediaService mediaService,
        IMediaLibraryService mediaLibraryService)
    {
        _mediaService = mediaService;
        _mediaLibraryService = mediaLibraryService;
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