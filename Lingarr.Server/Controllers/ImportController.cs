using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Models.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Lingarr.Server.Controllers;

/// <summary>
/// Import workbench endpoints (PM analysis P0). The file intake itself
/// lives on SubtitleController; this controller owns pre-flight estimates.
/// </summary>
[ApiController]
[Authorize("LingarrAccess")]
[Route("api/[controller]")]
public class ImportController : ControllerBase
{
    private readonly IImportEstimateService _importEstimateService;

    public ImportController(IImportEstimateService importEstimateService)
    {
        _importEstimateService = importEstimateService;
    }

    /// <summary>
    /// Estimates the translation cost of the files queued for import,
    /// priced at the default provider's rate via the shared cost service.
    /// Cost is null when the provider has no rate configured; a missing
    /// file list prices like an empty queue.
    /// </summary>
    [HttpPost("estimate")]
    public async Task<ActionResult<ImportEstimateResult>> Estimate(
        [FromBody] Lingarr.Server.Models.ImportEstimateRequest request)
    {
        var result = await _importEstimateService.Estimate(request.Files);
        return Ok(result);
    }
}
