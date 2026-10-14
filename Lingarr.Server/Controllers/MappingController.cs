using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Lingarr.Server.Models.Api;
using Lingarr.Server.Modules.Media;
using Lingarr.Server.Modules.Sync;

namespace Lingarr.Server.Controllers;

[ApiController]
[Authorize("LingarrAccess")]
[Route("api/[controller]")]
public class MappingController : ControllerBase
{
    private readonly IMappingService _mappingService;
    private readonly PathConversionService _pathConversionService;

    public MappingController(
        IMappingService mappingService,
        PathConversionService pathConversionService)
    {
        _mappingService = mappingService;
        _pathConversionService = pathConversionService;
    }
    /// <summary>
    /// Retrieves all path mappings from the system.
    /// </summary>
    /// <returns>A list of <see cref="PathMapping"/> objects representing the current mappings.</returns>
    /// <response code="200">Returns the list of path mappings.</response>
    [HttpGet("get")]
    public async Task<ActionResult<List<PathMapping>>> GetMapping()
    {
        var mappings = await _mappingService.GetMapping();
        return Ok(mappings);
    }

    /// <summary>
    /// Updates or creates path mappings in the system.
    /// </summary>
    /// <param name="mappings">The list of path mappings to set.</param>
    /// <returns>An IActionResult indicating success or failure.</returns>
    /// <response code="200">The mappings were successfully updated.</response>
    /// <response code="400">If the mappings are invalid.</response>
    [HttpPost("set")]
    public async Task<IActionResult> SetMapping([FromBody] List<PathMapping> mappings)
    {
        await _mappingService.SetMapping(mappings);
        return Ok();
    }

    /// <summary>
    /// Reachability check over the stored media directories — the ground truth
    /// for "would subtitles land on disk". A directory that is not visible to
    /// Lingarr is the exact condition under which translations are skipped.
    /// </summary>
    /// <response code="200">Checked count plus the unreachable directories.</response>
    [HttpGet("status")]
    public async Task<ActionResult<MappingStatusDto>> GetStatus()
    {
        return Ok(await _mappingService.GetMappingStatus());
    }

    /// <summary>
    /// Tries an *arr-style path against the saved mappings. The conversion runs
    /// through the same PathConversionService the sync pipeline uses, so the
    /// verdict tests the real code path. Failures answer ok=false instead of 5xx.
    /// </summary>
    /// <param name="request">An *arr-style path and its media type (movie/show).</param>
    /// <response code="200">Verdict: matched/exists flags plus the mapped path.</response>
    /// <response code="400">Unknown media type or empty path.</response>
    [HttpPost("test")]
    public IActionResult TestMapping([FromBody] TestMappingRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Path))
        {
            return BadRequest("A path is required.");
        }
        if (!TryParseMediaType(request.MediaType, out var mediaType))
        {
            return BadRequest($"Unknown media type '{request.MediaType}'.");
        }

        var conversion = _pathConversionService.TryConvertAndMapPath(request.Path, mediaType);
        var exists = System.IO.File.Exists(conversion.MappedPath) || Directory.Exists(conversion.MappedPath);

        return Ok(new TestMappingResult
        {
            Matched = conversion.Matched,
            MappedPath = conversion.MappedPath,
            Exists = exists,
            Ok = conversion.Matched && exists
        });
    }

    private static bool TryParseMediaType(string value, out MediaType mediaType)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "movie":
                mediaType = MediaType.Movie;
                return true;
            case "show":
                mediaType = MediaType.Show;
                return true;
            default:
                mediaType = default;
                return false;
        }
    }
}
