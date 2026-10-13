using Microsoft.AspNetCore.Mvc;
using Lingarr.Core;
using Lingarr.Core.Models;
using Microsoft.AspNetCore.Authorization;
using Lingarr.Server.Modules.Integration;

namespace Lingarr.Server.Controllers;

[ApiController]
[Authorize("LingarrAccess")]
[Route("api/[controller]")]
public class VersionController : ControllerBase
{
    private readonly ILingarrApiService _lingarrApiService;

    public VersionController(ILingarrApiService lingarrApiService)
    {
        _lingarrApiService = lingarrApiService;
    }

    /// <summary>
    /// Retrieves the current version information and checks for available updates.
    /// </summary>
    /// <returns>A Task containing an ActionResult with VersionInfo data. Returns HTTP 200 OK on success.</returns>
    /// <response code="200">Returns the version information</response>
    /// <response code="500">If there was an error checking for updates</response>
    [AllowAnonymous]
    [HttpGet]
    public async Task<ActionResult<VersionInfo>> Get()
    {
        var versionInfo = await LingarrVersion.CheckForUpdates(_lingarrApiService);
        return Ok(versionInfo);
    }
}