using Lingarr.Server.Models.Api;
using Lingarr.Server.Modules.Subtitle.PostProcessing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Lingarr.Server.Controllers;

/// <summary>
/// Existing-library Chinese variant batch (B01): preview counts what the
/// configured post-processing mode would rewrite (nothing is written);
/// execute converts per file, idempotently, only files whose parsed text
/// actually changes. Both endpoints refuse a "none"/unset mode — converting
/// stored text is always an explicit choice.
/// </summary>
[ApiController]
[Authorize("LingarrAccess")]
[Route("api/subtitle/post-process")]
public class SubtitlePostProcessController : ControllerBase
{
    private readonly ISubtitleVariantBatchService _batchService;

    public SubtitlePostProcessController(ISubtitleVariantBatchService batchService)
    {
        _batchService = batchService;
    }

    /// <summary>
    /// Counts the library subtitle files the configured variant mode would
    /// rewrite. Pure preview — no file is created, moved or rewritten.
    /// </summary>
    [HttpPost("preview")]
    public async Task<ActionResult<SubtitlePostProcessPreviewResponse>> Preview(
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _batchService.PreviewAsync(cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Converts the existing library subtitles per the configured variant
    /// mode. Every file is re-evaluated at execution time (the result never
    /// depends on a preview having run); failures are reported per file and
    /// never block the rest.
    /// </summary>
    [HttpPost("execute")]
    public async Task<ActionResult<SubtitlePostProcessExecuteResponse>> Execute(
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _batchService.ExecuteAsync(cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }
}
